using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MinecraftServerPilot.Models;

namespace MinecraftServerPilot.Services;

public sealed class InstallerCoordinator : IDisposable
{
    private readonly AppLog _log;
    private readonly ResilientDownloader _downloader;
    private readonly ServerCatalogService _catalog;
    private readonly JavaRuntimeService _java;
    private readonly ProcessRunner _processes;
    private readonly CompatibilityProbeService _probe;
    private readonly ServerPropertiesService _properties;

    public AppLog Log => _log;

    public InstallerCoordinator()
    {
        _log = new AppLog();
        _downloader = new ResilientDownloader(_log);
        _catalog = new ServerCatalogService(_downloader, _log);
        _java = new JavaRuntimeService(_downloader, _log);
        _processes = new ProcessRunner(_log);
        _probe = new CompatibilityProbeService(_downloader, _log);
        _properties = new ServerPropertiesService(_log);
    }

    public Task<IReadOnlyList<string>> GetVersionsAsync(CancellationToken token) =>
        _catalog.GetVersionsAsync(token);

    public ServerPropertiesSnapshot LoadServerProperties(ExistingServerInfo existing) =>
        _properties.Load(existing.ServerDirectory, existing.MinecraftVersion);

    public ExistingServerInfo SaveServerProperties(
        ExistingServerInfo existing,
        ServerPropertiesSnapshot snapshot,
        IReadOnlyDictionary<string, string> values)
    {
        var result = _properties.Save(snapshot, values);
        if (result.ServerPort is not int port || port == existing.ServerPort)
            return existing;

        var configPath = Path.Combine(existing.ServerDirectory, "server-pilot.json");
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(configPath, Encoding.UTF8))?.AsObject()
                       ?? throw new InvalidDataException(
                           "server-pilot.json 不是有效的 JSON 对象。");
            node["serverPort"] = port;
            node["updatedAt"] = DateTimeOffset.Now;
            var temporary = configPath + ".tmp";
            File.WriteAllText(temporary,
                node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            File.Move(temporary, configPath, overwrite: true);
            _log.Info("PROPERTIES", $"同步管理配置端口：{existing.ServerPort} -> {port}");
            return existing with { ServerPort = port };
        }
        catch (Exception ex)
        {
            var backup = snapshot.FilePath + ".pilot-backup";
            if (File.Exists(backup))
                File.Copy(backup, snapshot.FilePath, overwrite: true);
            throw new IOException(
                "server.properties 已写入，但同步 server-pilot.json 失败；程序已尝试从备份回滚属性文件。",
                ex);
        }
    }

    public ExistingServerInfo LoadExistingServer(string serverDirectory)
    {
        var directory = Path.GetFullPath(serverDirectory);
        var configPath = Path.Combine(directory, "server-pilot.json");
        if (!File.Exists(configPath))
            throw new FileNotFoundException(
                "所选目录不是由 Minecraft Server Pilot 交付的服务端：缺少 server-pilot.json。", configPath);
        var node = JsonNode.Parse(File.ReadAllText(configPath, Encoding.UTF8))?.AsObject()
                   ?? throw new InvalidDataException("server-pilot.json 不是有效的 JSON 对象。");
        var version = RequiredString(node, "minecraftVersion");
        var kindText = RequiredString(node, "serverKind");
        if (!Enum.TryParse<ServerKind>(kindText, ignoreCase: true, out var kind))
            throw new InvalidDataException($"不认识的服务端类型：{kindText}");
        var javaPath = RequiredString(node, "javaPath");
        var javaExe = Path.IsPathRooted(javaPath)
            ? javaPath
            : Path.GetFullPath(Path.Combine(directory, javaPath));
        if (!File.Exists(javaExe))
            throw new FileNotFoundException(
                $"配置记录的 Java 不存在：{javaExe}\n如果移动过单个文件，请整体恢复服务端目录；也可重新执行安装让程序修复 Java。", javaExe);
        return new ExistingServerInfo(
            directory,
            version,
            kind,
            node["distribution"]?.GetValue<string>() ?? kind.ToString(),
            javaExe,
            node["javaMajor"]?.GetValue<int>() ?? 0,
            node["minimumMemoryMb"]?.GetValue<int>() ?? 1024,
            node["maximumMemoryMb"]?.GetValue<int>() ?? 4096,
            node["serverPort"]?.GetValue<int>() ?? 25565);
    }

    public ExistingServerInfo UpdateExistingServer(
        ExistingServerInfo existing,
        int minimumMemoryMb,
        int maximumMemoryMb)
    {
        if (minimumMemoryMb < 512 || maximumMemoryMb < minimumMemoryMb)
            throw new ArgumentException("内存配置无效：最小值至少 512 MB，最大值不能小于最小值。");
        var directory = existing.ServerDirectory;
        var configPath = Path.Combine(directory, "server-pilot.json");
        var node = JsonNode.Parse(File.ReadAllText(configPath, Encoding.UTF8))?.AsObject()
                   ?? throw new InvalidDataException("server-pilot.json 不是有效的 JSON 对象。");
        node["minimumMemoryMb"] = minimumMemoryMb;
        node["maximumMemoryMb"] = maximumMemoryMb;
        node["updatedAt"] = DateTimeOffset.Now;
        var temporary = configPath + ".tmp";
        File.WriteAllText(temporary, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        File.Move(temporary, configPath, overwrite: true);
        var launch = BuildLaunchSpec(existing.ServerKind, existing.JavaExe, directory,
            minimumMemoryMb, maximumMemoryMb);
        WriteStartScript(directory, launch);
        _log.Info("MANAGER",
            $"更新已有服务端：{directory}; 内存={minimumMemoryMb}-{maximumMemoryMb} MB");
        return existing with
        {
            MinimumMemoryMb = minimumMemoryMb,
            MaximumMemoryMb = maximumMemoryMb
        };
    }

    public void StartExistingServer(ExistingServerInfo existing)
    {
        var command = Path.Combine(existing.ServerDirectory, "Start-Server.cmd");
        if (!File.Exists(command))
            throw new FileNotFoundException("缺少 Start-Server.cmd，请先保存配置以重新生成。", command);
        _ = Process.Start(new ProcessStartInfo(command)
        {
            WorkingDirectory = existing.ServerDirectory,
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("Windows 未能创建服务端控制台进程。");
        _log.Info("MANAGER", $"已在独立控制台启动：{existing.ServerDirectory}");
    }

    public async Task<InstallResult> InstallAsync(
        InstallRequest request,
        IProgress<OperationProgress>? progress,
        CancellationToken token,
        Func<InstallCheckpoint, CancellationToken, Task<bool>>? confirm = null)
    {
        Validate(request);
        progress?.Report(new("准备", request.Mode == InstallMode.Automatic
            ? "可以去喝一杯咖啡了，我们马上就好。"
            : "向导模式已启动；每一步和所有错误都会显示在下方。", 0));

        var folderName = $"Minecraft-{SanitizeVersion(request.MinecraftVersion)}-{request.ServerKind}-Server";
        var (serverDirectory, session, resumed) = PrepareSession(request, folderName);
        Directory.CreateDirectory(serverDirectory);
        var toolsDirectory = Path.Combine(serverDirectory, ".pilot-tools");
        Directory.CreateDirectory(toolsDirectory);
        var sessionLog = Path.Combine(serverDirectory, "pilot-install.log");
        if (resumed)
        {
            progress?.Report(new("恢复",
                $"发现同一版本的未完成安装，正在从原目录续作：{serverDirectory}", 1, true));
            _log.Info("SESSION", $"恢复未完成会话：{serverDirectory}; 前次阶段={session.Stage}");
        }
        File.AppendAllText(sessionLog,
            $"{(resumed ? "Resumed" : "Started")} at {DateTimeOffset.Now:O}{Environment.NewLine}",
            new UTF8Encoding(false));

        try
        {
            UpdateSession(serverDirectory, session, "Resolving", "Running");
            progress?.Report(new("解析", $"正在解析 {request.MinecraftVersion} / {request.ServerKind}…", 5));
            var package = await _catalog.ResolveAsync(
                request.MinecraftVersion, request.ServerKind, token);
            _log.Info("INSTALL", $"解析完成：{package.DisplayName}; Java={package.RequiredJavaMajor}");
            progress?.Report(new("解析",
                $"已选择 {package.DisplayName}；需要 Java {package.RequiredJavaMajor}", 10));
            await RequireConfirmationAsync(request, confirm, new InstallCheckpoint(
                "发行版与 Java",
                "确认发行版与运行环境",
                $"将安装 {package.DisplayName}。\n需要 Java {package.RequiredJavaMajor}；程序会优先复用本机完全匹配的 Java。" +
                (request.AllowJavaDownload
                    ? "缺失时自动下载免安装便携版。"
                    : "缺失时将暂停并提供手动安装说明，不会自动下载。"),
                IsSecurityRelevant: false), token);

            UpdateSession(serverDirectory, session, "PreparingJava", "Running");
            var runtime = await _java.EnsureAsync(
                package.RequiredJavaMajor, toolsDirectory, request.AllowJavaDownload, progress, token);
            progress?.Report(new("Java", $"Java {runtime.MajorVersion} 就绪（{runtime.Source}）", 24));

            UpdateSession(serverDirectory, session, "DownloadingServer", "Running");
            var packagePath = await _downloader.DownloadAsync(
                package.Artifact, serverDirectory, progress, token);
            progress?.Report(new("安装", $"{package.DisplayName} 下载完成", 46));

            if (package.NeedsInstaller)
            {
                UpdateSession(serverDirectory, session, "InstallingLoader", "Running");
                await InstallForgeAsync(runtime.JavaExe, packagePath, serverDirectory, progress, token);
            }

            var port = FindAvailablePort(25565);
            if (port != 25565)
                progress?.Report(new("纠错", $"默认端口 25565 已占用，自动改用 {port}。", null, true));
            await RequireConfirmationAsync(request, confirm, new InstallCheckpoint(
                "EULA 与安全配置",
                "确认 EULA 与服务端安全设置",
                $"下一步将写入 eula=true，表示你同意 Minecraft EULA；同时创建端口 {port} 的 server.properties，保持 online-mode=true，并关闭 RCON/Query。",
                IsSecurityRelevant: true), token);
            WriteEulaAndProperties(serverDirectory, port);
            var effectiveMinMemory = request.MinimumMemoryMb;
            var effectiveMaxMemory = request.MaximumMemoryMb;
            var launch = BuildLaunchSpec(
                request.ServerKind, runtime.JavaExe, serverDirectory,
                effectiveMinMemory, effectiveMaxMemory);
            WritePortableConfiguration(request, package, runtime, launch, serverDirectory, port,
                effectiveMinMemory, effectiveMaxMemory, session.Recoveries,
                session.CompatibilityProbeStatus);

            ProbeArtifact? probe = null;
            if (request.RunCompatibilityProbe)
            {
                await RequireConfirmationAsync(request, confirm, new InstallCheckpoint(
                    "一次性兼容性验证",
                    "确认下载测试组件",
                    request.ServerKind == ServerKind.Vanilla
                        ? "原版没有模组/插件加载器，将直接执行两次启动验证。"
                        : "程序将从 Modrinth 官方 API 下载已知开源的 spark 作为一次性测试组件。首次成功后会删除它和测试世界，再进行纯净复测。",
                    IsSecurityRelevant: false), token);
                probe = await _probe.TryInstallAsync(
                    request.ServerKind, request.MinecraftVersion, serverDirectory, progress, token);
                session.CompatibilityProbeStatus = probe is null ? "Unavailable" : "Prepared";
            }

            UpdateSession(serverDirectory, session, "FirstVerification", "Running");
            progress?.Report(new("启动验证", "第 1 次启动：验证服务端与测试组件，最长等待 3 分钟…", 62));
            var firstAttempt = await VerifyWithRecoveryAsync(
                request, launch, probe, serverDirectory, port, effectiveMinMemory, effectiveMaxMemory,
                progress, token);
            var first = firstAttempt.Result;
            launch = firstAttempt.Launch;
            port = firstAttempt.Port;
            effectiveMinMemory = firstAttempt.MinimumMemoryMb;
            effectiveMaxMemory = firstAttempt.MaximumMemoryMb;
            foreach (var recovery in firstAttempt.Recoveries)
            {
                session.Recoveries.Add(recovery);
                _log.Warn("RECOVERY", recovery);
            }
            if (probe is not null)
            {
                session.CompatibilityProbeStatus = firstAttempt.Recoveries.Any(
                    item => item.Contains("隔离", StringComparison.Ordinal))
                    ? "IsolatedAfterFailure"
                    : "Passed";
            }
            else if (request.RunCompatibilityProbe)
            {
                session.CompatibilityProbeStatus = "LoaderOnly";
            }
            if (!first.Success)
                throw BuildStartupException("第一次启动验证失败", first);

            _probe.Cleanup(probe, serverDirectory);
            WritePortableConfiguration(request, package, runtime, launch, serverDirectory, port,
                effectiveMinMemory, effectiveMaxMemory, session.Recoveries,
                session.CompatibilityProbeStatus);
            UpdateSession(serverDirectory, session, "CleanVerification", "Running");
            progress?.Report(new("启动验证", "测试数据已清理；第 2 次进行纯净启动验证…", 82));
            var secondAttempt = await VerifyWithRecoveryAsync(
                request, launch, probe: null, serverDirectory, port, effectiveMinMemory, effectiveMaxMemory,
                progress, token);
            var second = secondAttempt.Result;
            launch = secondAttempt.Launch;
            port = secondAttempt.Port;
            effectiveMinMemory = secondAttempt.MinimumMemoryMb;
            effectiveMaxMemory = secondAttempt.MaximumMemoryMb;
            foreach (var recovery in secondAttempt.Recoveries)
            {
                session.Recoveries.Add(recovery);
                _log.Warn("RECOVERY", recovery);
            }
            if (!second.Success)
                throw BuildStartupException("清理后的第二次启动验证失败", second);
            WritePortableConfiguration(request, package, runtime, launch, serverDirectory, port,
                effectiveMinMemory, effectiveMaxMemory, session.Recoveries,
                session.CompatibilityProbeStatus);

            var startCommand = Path.Combine(serverDirectory, "Start-Server.cmd");
            await RequireConfirmationAsync(request, confirm, new InstallCheckpoint(
                "交付",
                "验证完成，确认交付",
                $"服务端已通过两次启动验证。\n目录：{serverDirectory}\n端口：{port}\n内存：{effectiveMinMemory}–{effectiveMaxMemory} MB\n" +
                (request.KeepServerRunning ? "确认后会在独立控制台中启动正式服务端。" : "确认后保持安全停服，稍后可运行 Start-Server.cmd。"),
                IsSecurityRelevant: false), token);
            if (request.KeepServerRunning)
            {
                progress?.Report(new("交付", "双重验证通过，正在独立控制台窗口中启动正式服务端…", 96));
                _ = Process.Start(new ProcessStartInfo(startCommand)
                {
                    WorkingDirectory = serverDirectory,
                    UseShellExecute = true
                }) ?? throw new InvalidOperationException("Windows 未能创建正式服务端进程。");
            }

            File.AppendAllText(sessionLog,
                $"SUCCESS at {DateTimeOffset.Now:O}{Environment.NewLine}Log: {_log.FilePath}{Environment.NewLine}",
                new UTF8Encoding(false));
            UpdateSession(serverDirectory, session, "Completed", "Completed");
            progress?.Report(new("完成",
                request.KeepServerRunning
                    ? $"安装和双重验证完成，服务端正在端口 {port} 运行。"
                    : "安装和双重验证完成，服务端已安全停止，可用 Start-Server.cmd 启动。",
                100));
            return new InstallResult(serverDirectory, runtime.JavaExe, _log.FilePath,
                startCommand, request.KeepServerRunning);
        }
        catch (OperationCanceledException)
        {
            _log.Warn("INSTALL", "用户取消了安装。已下载的完整文件会保留以便下次复用，.part 文件可续传。");
            UpdateSession(serverDirectory, session, session.Stage, "Cancelled", "用户取消操作");
            throw;
        }
        catch (Exception ex)
        {
            _log.Error("INSTALL", $"安装失败；目标目录：{serverDirectory}", ex);
            UpdateSession(serverDirectory, session, session.Stage, "Failed", ex.ToString());
            File.AppendAllText(sessionLog,
                $"FAILED at {DateTimeOffset.Now:O}{Environment.NewLine}{ex}{Environment.NewLine}Log: {_log.FilePath}{Environment.NewLine}",
                new UTF8Encoding(false));
            throw new InvalidOperationException(
                $"{ex.Message}\n\n完整技术日志：{_log.FilePath}\n安装目录内摘要：{sessionLog}", ex);
        }
    }

    private async Task<VerificationAttempt> VerifyWithRecoveryAsync(
        InstallRequest request,
        LaunchSpec initialLaunch,
        ProbeArtifact? probe,
        string serverDirectory,
        int initialPort,
        int initialMinMemory,
        int initialMaxMemory,
        IProgress<OperationProgress>? progress,
        CancellationToken token)
    {
        var launch = initialLaunch;
        var port = initialPort;
        var minMemory = initialMinMemory;
        var maxMemory = initialMaxMemory;
        var recoveries = new List<string>();
        var eulaRecovered = false;
        var portRecovered = false;
        var memoryRecovered = false;
        var probeIsolated = false;
        ServerVerificationResult result = new(false, "", -1, false);

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            result = await _processes.VerifyServerAsync(
                launch, TimeSpan.FromMinutes(3), leaveRunning: false, progress, token);
            if (result.Success)
                return new VerificationAttempt(result, launch, port, minMemory, maxMemory, recoveries);

            var lower = result.Output.ToLowerInvariant();
            string? recovery = null;
            if (!eulaRecovered &&
                (lower.Contains("agree to the eula") || lower.Contains("eula=false")))
            {
                File.WriteAllText(Path.Combine(serverDirectory, "eula.txt"),
                    $"# Repaired by Minecraft Server Pilot at {DateTimeOffset.Now:O}{Environment.NewLine}eula=true{Environment.NewLine}",
                    new UTF8Encoding(false));
                eulaRecovered = true;
                recovery = "检测到 EULA 未生效，已在正确工作目录重写 eula=true 并重试。";
            }
            else if (!portRecovered &&
                     (lower.Contains("failed to bind to port") || lower.Contains("address already in use")))
            {
                var oldPort = port;
                port = FindAvailablePort(Math.Max(1024, port + 1));
                SetServerProperty(serverDirectory, "server-port", port.ToString());
                portRecovered = true;
                recovery = $"检测到端口 {oldPort} 在启动瞬间被占用，已改用 {port} 并重试。";
            }
            else if (!memoryRecovered &&
                     (lower.Contains("could not reserve enough space") || lower.Contains("outofmemoryerror")))
            {
                var oldMin = minMemory;
                var oldMax = maxMemory;
                minMemory = Math.Min(minMemory, 512);
                maxMemory = Math.Max(minMemory, Math.Min(2048, Math.Max(768, maxMemory / 2)));
                launch = BuildLaunchSpec(request.ServerKind, launch.FileName, serverDirectory,
                    minMemory, maxMemory);
                memoryRecovered = true;
                recovery = $"检测到 JVM 内存分配失败，已从 {oldMin}–{oldMax} MB 降为 {minMemory}–{maxMemory} MB 并重试。";
            }
            else if (probe is not null && !probeIsolated)
            {
                _probe.Cleanup(probe, serverDirectory);
                probeIsolated = true;
                recovery = "首次启动失败且基础环境错误不明确，已隔离一次性测试组件与测试世界，改为验证加载器本身。";
            }

            if (recovery is null)
                break;
            recoveries.Add(recovery);
            progress?.Report(new("自动纠错", recovery, null, true));
            AppendRecoveryReport(serverDirectory, attempt, recovery, result);
        }
        return new VerificationAttempt(result, launch, port, minMemory, maxMemory, recoveries);
    }

    private async Task InstallForgeAsync(
        string javaExe,
        string installer,
        string directory,
        IProgress<OperationProgress>? progress,
        CancellationToken token)
    {
        progress?.Report(new("Forge 安装", "正在运行 Forge 无界面服务端安装器（依赖下载可能需要几分钟）…", 52));
        var result = await _processes.RunAsync(javaExe,
            ["-Dfile.encoding=UTF-8", "-jar", installer, "--installServer", directory],
            directory, TimeSpan.FromMinutes(12), "FORGE", progress, token);
        if (!result.TimedOut && result.ExitCode != 0)
        {
            _log.Warn("FORGE",
                "Forge 首次安装未完成，改用当前工作目录形式重试；这既兼容旧参数，也可恢复一次性网络超时。");
            progress?.Report(new("Forge 安装",
                "首次安装未完成，正在切换工作目录兼容方式并恢复临时网络故障…", null, true));
            result = await _processes.RunAsync(javaExe,
                ["-Dfile.encoding=UTF-8", "-jar", installer, "--installServer"],
                directory, TimeSpan.FromMinutes(12), "FORGE", progress, token);
        }
        if (result.TimedOut || result.ExitCode != 0)
            throw new InvalidOperationException(
                $"Forge 安装器失败（退出码 {result.ExitCode}，超时={result.TimedOut}）。\n" +
                ErrorAdvisor.Analyze(result.Output, result.ExitCode) +
                "\nForge 安装输出已写入完整日志。");
        _log.Info("FORGE", "Forge 服务端安装器执行完成。");
    }

    private static LaunchSpec BuildLaunchSpec(
        ServerKind kind,
        string javaExe,
        string directory,
        int minMemory,
        int maxMemory)
    {
        var commonJvm = new List<string>
        {
            $"-Xms{minMemory}M", $"-Xmx{maxMemory}M", "-Dfile.encoding=UTF-8"
        };
        if (kind != ServerKind.Forge)
            return new LaunchSpec(javaExe, [.. commonJvm, "-jar", "server.jar", "nogui"], directory);

        var winArgs = Directory.Exists(Path.Combine(directory, "libraries"))
            ? Directory.EnumerateFiles(Path.Combine(directory, "libraries"), "win_args.txt", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
        if (winArgs is not null)
        {
            var relative = Path.GetRelativePath(directory, winArgs).Replace('\\', '/');
            return new LaunchSpec(javaExe, [.. commonJvm, $"@{relative}", "nogui"], directory);
        }
        var forgeJar = Directory.EnumerateFiles(directory, "forge-*.jar", SearchOption.TopDirectoryOnly)
            .Where(path => !path.Contains("installer", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(path => path.Contains("universal", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        if (forgeJar is null)
            throw new InvalidDataException(
                "Forge 安装器返回成功，但没有生成 win_args.txt 或可启动的 forge-*.jar。请查看 Forge 安装日志。");
        return new LaunchSpec(javaExe,
            [.. commonJvm, "-jar", Path.GetFileName(forgeJar), "nogui"], directory);
    }

    private static void WriteEulaAndProperties(string directory, int port)
    {
        File.WriteAllText(Path.Combine(directory, "eula.txt"),
            $"# Accepted through Minecraft Server Pilot at {DateTimeOffset.Now:O}{Environment.NewLine}eula=true{Environment.NewLine}",
            new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "server.properties"),
            $"# Minecraft Server Pilot safe defaults{Environment.NewLine}" +
            $"server-port={port}{Environment.NewLine}" +
            $"motd=A Minecraft Server created by Server Pilot{Environment.NewLine}" +
            $"online-mode=true{Environment.NewLine}" +
            $"enable-query=false{Environment.NewLine}" +
            $"enable-rcon=false{Environment.NewLine}" +
            $"white-list=false{Environment.NewLine}",
            new UTF8Encoding(false));
    }

    private static void WritePortableConfiguration(
        InstallRequest request,
        ServerPackage package,
        JavaRuntime runtime,
        LaunchSpec launch,
        string directory,
        int port,
        int effectiveMinimumMemoryMb,
        int effectiveMaximumMemoryMb,
        IReadOnlyList<string> recoveries,
        string compatibilityProbeStatus)
    {
        var config = new
        {
            schemaVersion = 1,
            minecraftVersion = request.MinecraftVersion,
            serverKind = request.ServerKind.ToString(),
            distribution = package.DisplayName,
            javaMajor = runtime.MajorVersion,
            javaPath = runtime.IsManaged ? Path.GetRelativePath(directory, runtime.JavaExe) : runtime.JavaExe,
            minimumMemoryMb = effectiveMinimumMemoryMb,
            maximumMemoryMb = effectiveMaximumMemoryMb,
            serverPort = port,
            updatedAt = DateTimeOffset.Now,
            automaticRecoveries = recoveries,
            compatibilityProbeStatus,
            note = "配置由 Minecraft Server Pilot 维护；修改内存后需要同步重新生成 Start-Server.cmd。"
        };
        File.WriteAllText(Path.Combine(directory, "server-pilot.json"),
            JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        WriteStartScript(directory, launch);

        File.WriteAllText(Path.Combine(directory, "NETWORK-GUIDE.txt"),
            "朋友联机与内网穿透指南\r\n\r\n" +
            $"当前服务端端口：{port}（TCP）\r\n\r\n" +
            "一、同一路由器 / 同一 Wi-Fi\r\n" +
            "1. 在服务端电脑运行 ipconfig，找到当前网卡的 IPv4 地址（通常是 192.168.x.x）。\r\n" +
            $"2. 朋友在多人游戏中填写 IPv4:{port}。不要填写 127.0.0.1。\r\n\r\n" +
            "二、通过互联网联机\r\n" +
            "方案 A：如果你有公网 IPv4，在路由器中将外部 TCP 端口映射到本机 IPv4 与上述端口。\r\n" +
            "方案 B：运营商 CGNAT、校园网或无法管理路由器时，使用可信的游戏隧道服务，或在自己的云服务器上部署 FRP。\r\n" +
            $"隧道的本地目标应填写 127.0.0.1:{port}，协议选 TCP；朋友使用隧道提供的公网地址。\r\n\r\n" +
            "三、安全检查（强烈建议）\r\n" +
            "1. 保持 online-mode=true，切勿为了“连不上”随意关闭正版验证。\r\n" +
            "2. 在控制台输入 whitelist on，再用 whitelist add 玩家名 添加朋友。\r\n" +
            "3. 不要在路由器或隧道中暴露 RCON；当前配置已默认关闭 RCON 和 Query。\r\n" +
            "4. 不要整体关闭 Windows 防火墙。若弹出提示，只允许所选 Java 通过“专用网络”。\r\n" +
            "5. 公网地址、隧道令牌和远程管理密码不要发到公开群聊。\r\n\r\n" +
            "四、排查顺序\r\n" +
            "先确认本机 localhost 能进入 → 同局域网 IPv4 能进入 → 检查防火墙 → 检查端口映射/隧道。\r\n" +
            "若服务端日志完全没有朋友的连接记录，问题在网络链路；若有记录并报认证/版本错误，按日志处理客户端版本或账号。\r\n",
            new UTF8Encoding(false));
    }

    private static void WriteStartScript(string directory, LaunchSpec launch)
    {
        var command = QuoteCmd(launch.FileName) + " " +
                      string.Join(" ", launch.Arguments.Select(QuoteCmd));
        var script = "@echo off\r\n" +
                     "chcp 65001 >nul\r\n" +
                     "cd /d \"%~dp0\"\r\n" +
                     "title Minecraft Server Pilot\r\n" +
                     command + "\r\n" +
                     "set \"PILOT_EXIT=%ERRORLEVEL%\"\r\n" +
                     "if not \"%PILOT_EXIT%\"==\"0\" (\r\n" +
                     "  echo.\r\n" +
                     "  echo Server stopped with exit code %PILOT_EXIT%.\r\n" +
                     "  echo Check logs\\latest.log and pilot-install.log for details.\r\n" +
                     "  pause\r\n" +
                     ")\r\n" +
                     "exit /b %PILOT_EXIT%\r\n";
        File.WriteAllText(Path.Combine(directory, "Start-Server.cmd"), script,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string RequiredString(JsonObject node, string property)
    {
        var value = node[property]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"server-pilot.json 缺少 {property}。")
            : value;
    }

    private static async Task RequireConfirmationAsync(
        InstallRequest request,
        Func<InstallCheckpoint, CancellationToken, Task<bool>>? confirm,
        InstallCheckpoint checkpoint,
        CancellationToken token)
    {
        if (request.Mode == InstallMode.Automatic)
            return;
        if (confirm is null)
            throw new InvalidOperationException("引导模式缺少交互确认处理器，已停止以保护用户知情权。");
        if (!await confirm(checkpoint, token))
            throw new OperationCanceledException($"用户在“{checkpoint.Stage}”阶段取消。", token);
    }

    private static (string Directory, InstallSessionState Session, bool Resumed) PrepareSession(
        InstallRequest request,
        string folderName)
    {
        var preferred = Path.Combine(request.ParentDirectory, folderName);
        var sessionPath = Path.Combine(preferred, ".pilot-session.json");
        if (File.Exists(sessionPath))
        {
            try
            {
                var existing = JsonSerializer.Deserialize<InstallSessionState>(
                    File.ReadAllText(sessionPath, Encoding.UTF8));
                if (existing is not null &&
                    existing.SchemaVersion == 1 &&
                    existing.MinecraftVersion.Equals(request.MinecraftVersion, StringComparison.OrdinalIgnoreCase) &&
                    existing.ServerKind == request.ServerKind &&
                    !existing.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase))
                {
                    existing.MinimumMemoryMb = request.MinimumMemoryMb;
                    existing.MaximumMemoryMb = request.MaximumMemoryMb;
                    existing.Status = "Running";
                    existing.LastError = null;
                    existing.UpdatedAt = DateTimeOffset.Now;
                    return (preferred, existing, true);
                }
            }
            catch
            {
                // An unreadable session file must never cause an existing directory to be overwritten.
            }
        }

        var directory = GetFreshDirectory(request.ParentDirectory, folderName);
        var session = new InstallSessionState
        {
            MinecraftVersion = request.MinecraftVersion,
            ServerKind = request.ServerKind,
            MinimumMemoryMb = request.MinimumMemoryMb,
            MaximumMemoryMb = request.MaximumMemoryMb
        };
        Directory.CreateDirectory(directory);
        UpdateSession(directory, session, "Created", "Running");
        return (directory, session, false);
    }

    private static void UpdateSession(
        string serverDirectory,
        InstallSessionState session,
        string stage,
        string status,
        string? error = null)
    {
        session.Stage = stage;
        session.Status = status;
        session.LastError = error;
        session.UpdatedAt = DateTimeOffset.Now;
        var path = Path.Combine(serverDirectory, ".pilot-session.json");
        var temporary = path + ".tmp";
        var json = JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(temporary, json, new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    private static void SetServerProperty(string serverDirectory, string key, string value)
    {
        var path = Path.Combine(serverDirectory, "server.properties");
        var lines = File.Exists(path)
            ? File.ReadAllLines(path, Encoding.UTF8).ToList()
            : [];
        var prefix = key + "=";
        var index = lines.FindIndex(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            lines[index] = prefix + value;
        else
            lines.Add(prefix + value);
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }

    private static void AppendRecoveryReport(
        string serverDirectory,
        int attempt,
        string recovery,
        ServerVerificationResult result)
    {
        var tail = string.Join(Environment.NewLine,
            result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).TakeLast(25));
        File.AppendAllText(Path.Combine(serverDirectory, "pilot-recovery-report.txt"),
            $"[{DateTimeOffset.Now:O}] Attempt {attempt}{Environment.NewLine}" +
            $"Action: {recovery}{Environment.NewLine}" +
            $"ExitCode: {result.ExitCode}; TimedOut: {result.TimedOut}{Environment.NewLine}" +
            $"Output tail:{Environment.NewLine}{tail}{Environment.NewLine}{Environment.NewLine}",
            new UTF8Encoding(false));
    }

    private sealed record VerificationAttempt(
        ServerVerificationResult Result,
        LaunchSpec Launch,
        int Port,
        int MinimumMemoryMb,
        int MaximumMemoryMb,
        IReadOnlyList<string> Recoveries);

    private static Exception BuildStartupException(string title, ServerVerificationResult result)
    {
        var tail = string.Join(Environment.NewLine,
            result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).TakeLast(35));
        return new InvalidOperationException(
            $"{title}（退出码 {result.ExitCode}，超时={result.TimedOut}）。\n" +
            $"{ErrorAdvisor.Analyze(result.Output, result.ExitCode)}\n\n最后输出：\n{tail}");
    }

    private static void Validate(InstallRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.MinecraftVersion))
            throw new ArgumentException("请选择或输入 Minecraft 版本。");
        if (!Directory.Exists(request.ParentDirectory))
            throw new DirectoryNotFoundException($"安装位置不存在：{request.ParentDirectory}");
        if (request.MinimumMemoryMb < 512)
            throw new ArgumentException("最小内存不能低于 512 MB。");
        if (request.MaximumMemoryMb < request.MinimumMemoryMb)
            throw new ArgumentException("最大内存不能小于最小内存。");
        var totalMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024;
        if (request.MaximumMemoryMb > totalMb * 0.85)
            throw new ArgumentException(
                $"最大内存 {request.MaximumMemoryMb} MB 过高；本机可用上限约 {totalMb} MB。请为 Windows 至少保留 15%。");
        var root = Path.GetPathRoot(Path.GetFullPath(request.ParentDirectory));
        if (root is not null)
        {
            var drive = new DriveInfo(root);
            if (drive.AvailableFreeSpace < 2L * 1024 * 1024 * 1024)
                throw new IOException($"目标磁盘剩余空间不足 2 GB：{drive.AvailableFreeSpace / 1024 / 1024} MB。");
        }
    }

    private static string GetFreshDirectory(string parent, string name)
    {
        var path = Path.Combine(parent, name);
        if (!Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any())
            return path;
        return path + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
    }

    private static string SanitizeVersion(string version)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(version.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray());
    }

    private static int FindAvailablePort(int preferred)
    {
        for (var port = preferred; port < preferred + 50; port++)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch (SocketException)
            {
                // Try the next port.
            }
        }
        throw new IOException($"端口 {preferred}-{preferred + 49} 均被占用，无法选择安全的服务端端口。");
    }

    private static string QuoteCmd(string value) =>
        "\"" + value.Replace("%", "%%").Replace("\"", "\"\"") + "\"";

    public void Dispose()
    {
        _downloader.Dispose();
        _log.Dispose();
    }
}

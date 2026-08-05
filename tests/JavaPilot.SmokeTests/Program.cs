using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using JavaPilot.Models;
using JavaPilot.Services;

var failures = new List<string>();

Check(
    JavaCatalog.Options.Select(option => option.Major)
        .SequenceEqual([25, 21, 17, 16, 11, 8, 7, 6]),
    "Java 版本目录顺序或覆盖范围错误。");
Check(
    JavaCatalog.Get(8).MinecraftVersions.Contains("1.7.10", StringComparison.Ordinal) &&
    JavaCatalog.Get(8).MinecraftVersions.Contains("1.16.5", StringComparison.Ordinal),
    "Java 8 的 Minecraft 推荐范围缺失。");
Check(
    JavaCatalog.Get(21).MinecraftVersions.Contains("1.20.5", StringComparison.Ordinal),
    "Java 21 的 Minecraft 推荐范围缺失。");
Check(
    JavaCatalog.Get(25).MinecraftVersions.Contains("26.x", StringComparison.Ordinal),
    "Java 25 的 Minecraft 推荐范围缺失。");
Check(
    JavaCatalog.Get(6).IsLegacy && JavaCatalog.Get(7).IsLegacy,
    "Java 6/7 必须标记为高风险旧版。");
var calibrated25 = JavaCatalog.WithLatestMinecraftRelease("26.2", 25);
Check(
    calibrated25.First(option => option.Major == 25).Description.Contains(
        "26.2",
        StringComparison.Ordinal),
    "Mojang 最新正式版校准没有更新对应 Java 描述。");
var calibratedFuture = JavaCatalog.WithLatestMinecraftRelease("29.1", 29);
Check(
    calibratedFuture.Any(option =>
        option.Major == 29 &&
        option.MinecraftVersions.Contains("29.1", StringComparison.Ordinal)),
    "遇到未来 Java 主版本时没有自动增加选项。");

CheckVersion("java version \"1.8.0_472\"", 8, "1.8.0_472");
CheckVersion("openjdk version \"17.0.16\" 2025-07-15", 17, "17.0.16");
CheckVersion("openjdk version \"21.0.8\" 2025-07-15 LTS", 21, "21.0.8");
CheckVersion("openjdk version \"25.0.1\" 2025-10-21 LTS", 25, "25.0.1");
Check(JavaVersionProbe.Parse("not a java version") is null, "无效输出不应被识别。");

var uninstallMatch = WindowsUninstallCatalog.FindBestMatch(
    @"C:\Program Files\Example Java\jdk-21",
    [
        new WindowsUninstallEntry(
            "{11111111-1111-1111-1111-111111111111}",
            "Example OpenJDK 21",
            "21.0.1",
            "Example",
            @"C:\Program Files\Example Java\jdk-21",
            null,
            "msiexec.exe /x {11111111-1111-1111-1111-111111111111}",
            null,
            IsWindowsInstaller: true),
        new WindowsUninstallEntry(
            "unrelated",
            "Example OpenJDK 17",
            "17.0.1",
            "Example",
            @"C:\Program Files\Example Java\jdk-17",
            null,
            "uninstall.exe",
            null,
            IsWindowsInstaller: false)
    ]);
Check(
    uninstallMatch?.DisplayName == "Example OpenJDK 21",
    "Windows 注册卸载项没有按 Java 主目录精确匹配。");
Check(
    WindowsUninstallCatalog.FindBestMatch(
        @"D:\Portable\jdk-21",
        uninstallMatch is null ? [] : [uninstallMatch]) is null,
    "不相关目录不应误匹配 Windows 卸载项。");

var settingsRoot = Path.Combine(
    Path.GetTempPath(),
    "JavaPilot-SettingsTests",
    Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(settingsRoot);
using (var settingsLog = new AppLog(Path.Combine(settingsRoot, "logs")))
{
    var settingsService = new AppSettingsService(
        settingsLog,
        Path.Combine(settingsRoot, "settings.json"));
    settingsService.Save(new JavaPilotSettings
    {
        InstallRoot = Path.Combine(settingsRoot, "custom-runtimes"),
        ReuseSystemJava = false,
        SetDefaultJava = false,
        ForceReinstall = true,
        SelectedJavaMajor = 17
    });
    var loaded = settingsService.Load();
    Check(
        loaded.SelectedJavaMajor == 17 &&
        loaded.ForceReinstall &&
        !loaded.ReuseSystemJava &&
        !loaded.SetDefaultJava,
        "用户设置没有被完整保存或恢复。");
}
Directory.Delete(settingsRoot, recursive: true);

var backupTestRoot = Path.Combine(
    Path.GetTempPath(),
    "JavaPilot-BackupTests",
    Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(backupTestRoot);
try
{
    var firstBackup = Path.Combine(backupTestRoot, "environment-first.json");
    var secondBackup = Path.Combine(backupTestRoot, "environment-second.JSON");
    var unrelatedFile = Path.Combine(backupTestRoot, "keep.json");
    var nestedDirectory = Path.Combine(backupTestRoot, "nested");
    var nestedBackup = Path.Combine(nestedDirectory, "environment-nested.json");
    File.WriteAllText(firstBackup, "first");
    File.WriteAllText(secondBackup, "second");
    File.WriteAllText(unrelatedFile, "keep");
    Directory.CreateDirectory(nestedDirectory);
    File.WriteAllText(nestedBackup, "nested");

    var backupService = new EnvironmentBackupService(backupTestRoot);
    var backupSummary = backupService.Inspect();
    Check(
        backupSummary.Count == 2 &&
        backupSummary.TotalBytes ==
        new FileInfo(firstBackup).Length + new FileInfo(secondBackup).Length,
        "环境备份统计包含了非 Java Pilot 文件或递归目录。");

    var cleanup = backupService.Clear();
    Check(
        cleanup.DeletedCount == 2 &&
        !File.Exists(firstBackup) &&
        !File.Exists(secondBackup),
        "环境备份清理没有删除所有顶层 Java Pilot 备份。");
    Check(
        File.Exists(unrelatedFile) && File.Exists(nestedBackup),
        "环境备份清理越界删除了其他文件或嵌套目录内容。");
    Check(
        backupService.Inspect().Count == 0,
        "环境备份清理后的统计仍报告残留备份。");
}
finally
{
    Directory.Delete(backupTestRoot, recursive: true);
}

await RunDefaultEnvironmentDetectionTestAsync();

if (args.Contains("--network", StringComparer.OrdinalIgnoreCase))
{
    using var log = new AppLog(Path.Combine(Path.GetTempPath(), "JavaPilot-SmokeTests"));
    using var downloader = new ResilientDownloader(log);
    var resolver = new RuntimeSourceResolver(downloader, log);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    await CheckProvidersAsync(6, resolver, timeout.Token);
    await CheckProvidersAsync(21, resolver, timeout.Token);
    using var recommendations = new MinecraftRecommendationService(log);
    var calibrated = await recommendations.GetOptionsAsync(timeout.Token);
    Check(
        calibrated.Any(option =>
            option.Description.Contains("Mojang 当前最新正式版", StringComparison.Ordinal)),
        "Mojang 最新正式版 Java 要求在线校准失败。");

    using var discovery = new JavaInstallerService(log);
    var installed = await discovery.DiscoverAsync(
        installRoot: null,
        cancellationToken: timeout.Token,
        forceRefresh: true);
    Check(
        installed.All(item => item.Is64Bit && File.Exists(item.JavaExe)),
        "本机 Java 扫描返回了无效或非 64 位结果。");
    if (installed.FirstOrDefault() is { } reusable)
    {
        var reuseRoot = Path.Combine(
            Path.GetTempPath(),
            "JavaPilot-ReuseTests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var reused = await discovery.InstallAsync(
                new InstallRequest(
                    reusable.Major,
                    reuseRoot,
                    SetAsUserDefault: false,
                    ReuseSystemJava: true),
                progress: null,
                timeout.Token);
            Check(
                reused.Reused && reused.IsSystemRuntime &&
                reused.JavaExe.Equals(reusable.JavaExe, StringComparison.OrdinalIgnoreCase),
                "已安装 Java 没有被正确复用。");
        }
        finally
        {
            if (Directory.Exists(reuseRoot))
                Directory.Delete(reuseRoot, recursive: true);
        }
    }
}

if (args.Contains("--install", StringComparer.OrdinalIgnoreCase))
{
    var integrationRoot = Path.Combine(
        Path.GetTempPath(),
        "JavaPilot-IntegrationTests",
        Guid.NewGuid().ToString("N"));
    var log = new AppLog(Path.Combine(integrationRoot, "logs"));
    var installer = new JavaInstallerService(log);
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
    try
    {
        var onlineRoot = Path.Combine(integrationRoot, "online-runtimes");
        var result = await installer.InstallAsync(
            new InstallRequest(
                8,
                onlineRoot,
                SetAsUserDefault: false,
                ReuseSystemJava: false),
            progress: null,
            timeout.Token);
        Check(result.JavaMajor == 8, "完整安装测试返回了错误的 Java 主版本。");
        Check(File.Exists(result.JavaExe), "完整安装测试没有生成 java.exe。");
        var verified = await JavaVersionProbe.ProbeAsync(result.JavaExe, timeout.Token);
        Check(verified.Major == 8, "完整安装测试的最终 java -version 验证失败。");

        var archive = Directory.EnumerateFiles(
                Path.Combine(onlineRoot, "downloads"),
                "*.zip",
                SearchOption.TopDirectoryOnly)
            .FirstOrDefault();
        Check(archive is not null, "完整安装测试没有保留可供离线导入的 ZIP 缓存。");
        if (archive is not null)
        {
            var offlineRoot = Path.Combine(integrationRoot, "offline-runtimes");
            var imported = await installer.InstallFromArchiveAsync(
                new InstallRequest(
                    8,
                    offlineRoot,
                    SetAsUserDefault: false,
                    ReuseSystemJava: false,
                    ForceReinstall: true),
                archive,
                progress: null,
                timeout.Token);
            Check(
                imported.Provider == "本地 JDK ZIP" &&
                File.Exists(imported.JavaExe),
                "本地 ZIP 导入没有生成可用 Java。");

            var manager = new ManagedRuntimeService(log);
            var managed = await manager.ListAsync(offlineRoot, timeout.Token);
            Check(
                managed.Count == 1 && managed[0].Healthy && managed[0].Major == 8,
                "Java 管理器没有识别离线导入的运行时。");
            if (managed.Count == 1)
            {
                await manager.RemoveAsync(offlineRoot, managed[0], timeout.Token);
                Check(
                    !Directory.Exists(imported.JavaHome),
                    "Java 管理器安全卸载后仍残留正式运行时目录。");
            }
        }
    }
    catch (Exception ex)
    {
        failures.Add($"Java 8 完整安装测试失败：{ex}");
    }
    finally
    {
        installer.Dispose();
        log.Dispose();
        if (Directory.Exists(integrationRoot))
            Directory.Delete(integrationRoot, recursive: true);
    }
}

if (args.Contains("--recovery", StringComparer.OrdinalIgnoreCase))
    await RunDownloadRecoveryTestAsync();

if (args.Contains("--inventory", StringComparer.OrdinalIgnoreCase))
{
    using var inventoryLog = new AppLog(Path.Combine(
        Path.GetTempPath(),
        "JavaPilot-InventoryTests"));
    var inventory = new JavaInventoryService(inventoryLog);
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    var items = await inventory.ListAsync(
        JavaPilotSettings.DefaultInstallRoot,
        timeout.Token);
    Check(
        items.Select(item => item.JavaHome)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() == items.Count,
        "本机 Java 库存仍包含相同主目录的重复入口。");
    Check(
        items.All(item => File.Exists(item.JavaExe)),
        "本机 Java 库存包含不存在的 java.exe。");
    Console.WriteLine("Detected Java inventory:");
    foreach (var item in items)
    {
        Console.WriteLine(
            $"- Java {item.FullVersion} {item.ArchitectureText} | " +
            $"{item.OwnershipText} | {item.JavaHome}");
    }
}

if (args.Contains("--adopt", StringComparer.OrdinalIgnoreCase))
    await RunAdoptExistingJavaTestAsync();

if (failures.Count > 0)
{
    Console.Error.WriteLine("Java Pilot smoke tests failed:");
    foreach (var failure in failures)
        Console.Error.WriteLine("- " + failure);
    return 1;
}

Console.WriteLine("Java Pilot smoke tests passed.");
return 0;

void CheckVersion(string text, int expectedMajor, string expectedFull)
{
    var parsed = JavaVersionProbe.Parse(text);
    Check(parsed?.Major == expectedMajor, $"{text} 主版本解析错误。");
    Check(parsed?.FullVersion == expectedFull, $"{text} 完整版本解析错误。");
}

void Check(bool condition, string message)
{
    if (!condition)
        failures.Add(message);
}

async Task CheckProvidersAsync(
    int major,
    RuntimeSourceResolver resolver,
    CancellationToken cancellationToken)
{
    foreach (var provider in resolver.GetResolvers(major))
    {
        try
        {
            var package = await provider.ResolveAsync(cancellationToken);
            Check(package.Sources.Count > 0, $"{provider.Name} 没有返回下载地址。");
            Check(!string.IsNullOrWhiteSpace(package.FileName), $"{provider.Name} 没有返回文件名。");
            Check(
                package.Sources.All(source => source.Uri.Scheme == Uri.UriSchemeHttps),
                $"{provider.Name} 返回了非 HTTPS 地址。");
        }
        catch (Exception ex)
        {
            failures.Add($"Java {major} / {provider.Name} 元数据解析失败：{ex.Message}");
        }
    }
}

async Task RunDownloadRecoveryTestAsync()
{
    var testRoot = Path.Combine(
        Path.GetTempPath(),
        "JavaPilot-DownloadRecovery",
        Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(testRoot);
    var log = new AppLog(Path.Combine(testRoot, "logs"));
    using var downloader = new ResilientDownloader(
        log,
        readIdleTimeout: TimeSpan.FromSeconds(3),
        lowSpeedWindow: TimeSpan.FromSeconds(1),
        minimumSustainableBytesPerSecond: 1024 * 1024);
    var payload = new byte[7 * 1024 * 1024];
    Random.Shared.NextBytes(payload);
    var checksum = Convert.ToHexString(SHA256.HashData(payload));
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    using var serverCancellation = new CancellationTokenSource();
    var serverTask = ServeTestDownloadsAsync(listener, payload, serverCancellation.Token);
    var progressMessages = new List<string>();
    var progress = new Progress<OperationProgress>(item =>
    {
        lock (progressMessages)
            progressMessages.Add($"{item.Stage}:{item.Message}");
    });

    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await downloader.DownloadAsync(
            new RuntimePackage(
                "recovery-test",
                [
                    new("故意低速源", new($"http://127.0.0.1:{port}/slow")),
                    new("快速备用源", new($"http://127.0.0.1:{port}/fast"))
                ],
                "payload.bin",
                HashKind.Sha256,
                checksum,
                payload.LongLength),
            Path.Combine(testRoot, "downloads"),
            progress,
            timeout.Token);
        Check(File.Exists(result), "低速自动换源测试没有生成最终文件。");
        await using var resultStream = File.OpenRead(result);
        var actualChecksum = Convert.ToHexString(
            await SHA256.HashDataAsync(resultStream, timeout.Token));
        Check(
            actualChecksum == checksum,
            "低速自动换源后的文件哈希不正确。");
        await Task.Delay(100);
        lock (progressMessages)
        {
            Check(
                progressMessages.Any(message =>
                    message.Contains("自动换源", StringComparison.Ordinal)),
                "低速源没有触发自动换源提示。");
        }
    }
    catch (Exception ex)
    {
        failures.Add($"低速自动换源测试失败：{ex}");
    }
    finally
    {
        serverCancellation.Cancel();
        listener.Stop();
        try
        {
            await serverTask;
        }
        catch (OperationCanceledException)
        {
            // 正常停止测试服务器。
        }
        catch (SocketException)
        {
            // listener.Stop 会中断 Accept。
        }

        log.Dispose();
        if (Directory.Exists(testRoot))
            Directory.Delete(testRoot, recursive: true);
    }
}

async Task ServeTestDownloadsAsync(
    TcpListener listener,
    byte[] payload,
    CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        TcpClient client;
        try
        {
            client = await listener.AcceptTcpClientAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch (ObjectDisposedException)
        {
            break;
        }

        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var requestBuffer = new byte[8192];
                var used = 0;
                while (used < requestBuffer.Length)
                {
                    var read = await stream.ReadAsync(
                        requestBuffer.AsMemory(used),
                        cancellationToken);
                    if (read == 0)
                        break;
                    used += read;
                    if (Encoding.ASCII.GetString(requestBuffer, 0, used)
                        .Contains("\r\n\r\n", StringComparison.Ordinal))
                        break;
                }

                var request = Encoding.ASCII.GetString(requestBuffer, 0, used);
                var slow = request.StartsWith("GET /slow ", StringComparison.Ordinal);
                var headers = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Length: {payload.Length}\r\n" +
                    "Content-Type: application/octet-stream\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headers, cancellationToken);
                if (slow)
                {
                    const int chunkSize = 8192;
                    for (var offset = 0; offset < payload.Length; offset += chunkSize)
                    {
                        var count = Math.Min(chunkSize, payload.Length - offset);
                        await stream.WriteAsync(
                            payload.AsMemory(offset, count),
                            cancellationToken);
                        await stream.FlushAsync(cancellationToken);
                        await Task.Delay(250, cancellationToken);
                    }
                }
                else
                {
                    await stream.WriteAsync(payload, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
            }
            catch (IOException)
            {
                // 下载器切换来源时会主动关闭低速连接。
            }
            catch (SocketException)
            {
                // 同上。
            }
        }
    }
}

async Task RunAdoptExistingJavaTestAsync()
{
    var testRoot = Path.Combine(
        Path.GetTempPath(),
        "JavaPilot-AdoptTests",
        Guid.NewGuid().ToString("N"));
    var sourceHome = Path.Combine(testRoot, "external-jdk");
    var sourceBin = Path.Combine(sourceHome, "bin");
    var managedRoot = Path.Combine(testRoot, "managed");
    Directory.CreateDirectory(sourceBin);
    var shimOutput = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        "FakeJavaShim",
        "bin",
        "Release",
        "net8.0"));
    try
    {
        foreach (var file in Directory.EnumerateFiles(shimOutput))
            File.Copy(file, Path.Combine(sourceBin, Path.GetFileName(file)));
        File.Copy(
            Path.Combine(shimOutput, "FakeJavaShim.exe"),
            Path.Combine(sourceBin, "java.exe"));
        File.WriteAllText(
            Path.Combine(sourceHome, "release"),
            "JAVA_VERSION=\"1.8.0_999\"\nOS_ARCH=\"amd64\"\n");

        using var log = new AppLog(Path.Combine(testRoot, "logs"));
        var inventoryService = new JavaInventoryService(log);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var adopted = await inventoryService.AdoptAsync(
            managedRoot,
            new JavaInventoryItem(
                8,
                "1.8.0_999",
                sourceHome,
                Path.Combine(sourceBin, "java.exe"),
                "test",
                Is64Bit: true,
                Healthy: true,
                JavaOwnership.ExternalPortable),
            timeout.Token);
        Check(
            adopted.Ownership == JavaOwnership.JavaPilotManaged &&
            File.Exists(Path.Combine(adopted.JavaHome, ".java-pilot-installation.json")),
            "外部 Java 纳入管理没有生成受管标记。");
        Check(
            Directory.Exists(sourceHome),
            "纳入管理不应修改或删除原始 Java。");

        var listed = await inventoryService.ListAsync(managedRoot, timeout.Token);
        var managed = listed.SingleOrDefault(item =>
            item.Ownership == JavaOwnership.JavaPilotManaged &&
            item.Major == 8);
        Check(managed is not null, "纳入管理后的 Java 没有出现在全机库存中。");
        if (managed is not null)
        {
            var managedService = new ManagedRuntimeService(log);
            await managedService.RemoveAsync(
                managedRoot,
                new ManagedRuntimeInfo(
                    managed.Major,
                    managed.FullVersion,
                    managed.ProviderText,
                    managed.JavaHome,
                    managed.JavaExe,
                    managed.InstalledAt,
                    managed.Healthy,
                    "测试"),
                timeout.Token);
            Check(
                !Directory.Exists(managed.JavaHome),
                "纳入管理的测试 Java 无法安全卸载。");
        }
    }
    catch (Exception ex)
    {
        failures.Add($"外部 Java 纳入管理测试失败：{ex}");
    }
    finally
    {
        if (Directory.Exists(testRoot))
            Directory.Delete(testRoot, recursive: true);
    }
}

async Task RunDefaultEnvironmentDetectionTestAsync()
{
    var testRoot = Path.Combine(
        Path.GetTempPath(),
        "JavaPilot-EnvironmentTests",
        Guid.NewGuid().ToString("N"));
    var javaHome = Path.Combine(testRoot, "jdk-8");
    var javaBin = Path.Combine(javaHome, "bin");
    var oldJavaHome = Path.Combine(testRoot, "jdk-old");
    var oldJavaBin = Path.Combine(oldJavaHome, "bin");
    Directory.CreateDirectory(javaBin);
    Directory.CreateDirectory(oldJavaBin);
    var shimOutput = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        "FakeJavaShim",
        "bin",
        "Release",
        "net8.0"));
    var originalJavaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
    var originalPath = Environment.GetEnvironmentVariable("PATH");
    try
    {
        foreach (var file in Directory.EnumerateFiles(shimOutput))
            File.Copy(file, Path.Combine(javaBin, Path.GetFileName(file)));
        File.Copy(
            Path.Combine(shimOutput, "FakeJavaShim.exe"),
            Path.Combine(javaBin, "java.exe"));
        File.Copy(
            Path.Combine(shimOutput, "FakeJavaShim.exe"),
            Path.Combine(oldJavaBin, "java.exe"));

        var previousContext = SynchronizationContext.Current;
        JavaVersionProbe.ProbeResult? nonBlockingProbe = null;
        var probeCompletedWithoutUiPump = false;
        try
        {
            SynchronizationContext.SetSynchronizationContext(
                new NonPumpingSynchronizationContext());
            using var probeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var probeTask = JavaVersionProbe.ProbeDetailsAsync(
                Path.Combine(javaBin, "java.exe"),
                probeTimeout.Token);
            probeCompletedWithoutUiPump = probeTask.Wait(TimeSpan.FromSeconds(6));
            if (probeCompletedWithoutUiPump)
                nonBlockingProbe = probeTask.GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
        Check(
            probeCompletedWithoutUiPump &&
            nonBlockingProbe is { Major: 8, Is64Bit: true },
            "Java 启动验证捕获了 UI 同步上下文，可能导致“设为默认 Java”界面死锁。");

        var unrelated = Path.Combine(testRoot, "unrelated-bin");
        var mixedPath = string.Join(
            Path.PathSeparator,
            oldJavaBin,
            unrelated,
            "%JAVA_HOME%\\bin",
            javaBin);
        var rebuiltUserPath = UserEnvironmentService.BuildPathWithJavaFirst(
            mixedPath,
            javaBin,
            oldJavaBin);
        var rebuiltEntries = rebuiltUserPath.Split(Path.PathSeparator);
        Check(
            Path.GetFullPath(rebuiltEntries[0]).Equals(
                Path.GetFullPath(javaBin),
                StringComparison.OrdinalIgnoreCase) &&
            !rebuiltEntries.Any(entry =>
                Path.GetFullPath(entry).Equals(
                    Path.GetFullPath(oldJavaBin),
                    StringComparison.OrdinalIgnoreCase)) &&
            rebuiltEntries.Contains(unrelated, StringComparer.OrdinalIgnoreCase) &&
            UserEnvironmentService.FindFirstJavaOnPath(rebuiltUserPath)?.Equals(
                Path.Combine(javaBin, "java.exe"),
                StringComparison.OrdinalIgnoreCase) == true,
            "用户 PATH 重建没有把目标 Java 放在首位并移除旧用户入口。");

        var rebuiltSystemPath = UserEnvironmentService.BuildPathWithJavaFirst(
            string.Join(Path.PathSeparator, oldJavaBin, unrelated),
            javaBin);
        Check(
            rebuiltSystemPath.Split(Path.PathSeparator)
                .Skip(1)
                .Contains(oldJavaBin, StringComparer.OrdinalIgnoreCase),
            "系统 PATH 优先级修复不应删除旧 Java PATH 条目。");

        Environment.SetEnvironmentVariable("JAVA_HOME", javaHome);
        Environment.SetEnvironmentVariable("PATH", javaBin);
        using var log = new AppLog(Path.Combine(testRoot, "logs"));
        var service = new DefaultJavaEnvironmentService(log);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var snapshot = await service.InspectAsync(timeout.Token);
        Check(
            snapshot.PathDefault?.Installation is { Major: 8, Is64Bit: true } &&
            snapshot.JavaHome?.Installation is { Major: 8, Is64Bit: true } &&
            snapshot.IsConsistent &&
            !snapshot.HasBrokenConfiguration,
            "默认 Java 检测没有正确识别一致的 PATH 与 JAVA_HOME。");

        Environment.SetEnvironmentVariable(
            "JAVA_HOME",
            Path.Combine(testRoot, "missing-jdk"));
        var broken = await service.InspectAsync(timeout.Token);
        Check(
            broken.PathDefault?.Installation?.Major == 8 &&
            broken.JavaHome is { IsHealthy: false } &&
            broken.HasBrokenConfiguration,
            "默认 Java 检测没有报告损坏的 JAVA_HOME。");
    }
    catch (Exception ex)
    {
        failures.Add($"默认 Java 环境检测测试失败：{ex}");
    }
    finally
    {
        Environment.SetEnvironmentVariable("JAVA_HOME", originalJavaHome);
        Environment.SetEnvironmentVariable("PATH", originalPath);
        if (Directory.Exists(testRoot))
            Directory.Delete(testRoot, recursive: true);
    }
}

sealed class NonPumpingSynchronizationContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback callback, object? state)
    {
        // 故意不调度：被测代码若捕获 UI 上下文，测试会超时而不是假通过。
    }
}

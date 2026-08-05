using System.IO.Compression;
using System.Text.Json;
using JavaPilot.Models;

namespace JavaPilot.Services;

public sealed class JavaInstallerService : IDisposable
{
    private const string MarkerFileName = ".java-pilot-installation.json";
    private readonly AppLog _log;
    private readonly ResilientDownloader _downloader;
    private readonly RuntimeSourceResolver _sourceResolver;
    private readonly UserEnvironmentService _environment;
    private readonly JavaDiscoveryService _discovery;

    public JavaInstallerService(AppLog log)
    {
        _log = log;
        _downloader = new ResilientDownloader(log);
        _sourceResolver = new RuntimeSourceResolver(_downloader, log);
        _environment = new UserEnvironmentService(log);
        _discovery = new JavaDiscoveryService(log);
    }

    public Task<IReadOnlyList<JavaInstallation>> DiscoverAsync(
        string? installRoot,
        CancellationToken cancellationToken,
        bool forceRefresh = false) =>
        _discovery.ScanAsync(installRoot, cancellationToken, forceRefresh);

    public async Task<InstallResult> InstallFromArchiveAsync(
        InstallRequest request,
        string archivePath,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        if (string.IsNullOrWhiteSpace(archivePath) ||
            !Path.IsPathFullyQualified(archivePath))
        {
            throw new ArgumentException("本地 JDK ZIP 必须使用完整路径。", nameof(archivePath));
        }

        var archive = Path.GetFullPath(archivePath);
        if (!File.Exists(archive))
            throw new FileNotFoundException("找不到所选的本地 JDK ZIP。", archive);
        if (!string.Equals(Path.GetExtension(archive), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("本地导入仅支持 ZIP 格式的 Windows x64 JDK。");

        var installRoot = Path.GetFullPath(request.InstallRoot);
        Directory.CreateDirectory(installRoot);
        EnsureWritable(installRoot);
        EnsureFreeSpace(installRoot);
        var target = Path.Combine(installRoot, $"jdk-{request.JavaMajor}");
        if (Directory.Exists(target) &&
            !File.Exists(Path.Combine(target, MarkerFileName)))
        {
            throw new InvalidOperationException(
                $"目标文件夹已存在，但不是 Java Pilot 管理的目录：\n{target}\n\n" +
                "为了避免覆盖你的文件，程序已经停止。请选择其他安装目录。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        _log.Info(
            "IMPORT",
            $"开始导入本地 JDK ZIP；Java={request.JavaMajor}; Archive={archive}; Target={target}");
        progress?.Report(new(
            "本地导入",
            $"正在验证并解压 {Path.GetFileName(archive)}…",
            0));
        var package = new RuntimePackage(
            "本地 JDK ZIP",
            [],
            Path.GetFileName(archive));
        return await ExtractDeployAndVerifyAsync(
            request with
            {
                ReuseSystemJava = false,
                ForceReinstall = true
            },
            target,
            package,
            archive,
            progress,
            cancellationToken);
    }

    public async Task<InstallResult> InstallAsync(
        InstallRequest request,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var installRoot = Path.GetFullPath(request.InstallRoot);
        Directory.CreateDirectory(installRoot);
        EnsureWritable(installRoot);
        EnsureFreeSpace(installRoot);

        var target = Path.Combine(installRoot, $"jdk-{request.JavaMajor}");
        if (!request.ForceReinstall)
        {
            var existing = await TryReuseAsync(
                target,
                request.JavaMajor,
                request.SetAsUserDefault,
                cancellationToken);
            if (existing is not null)
            {
                progress?.Report(new("完成", $"Java {request.JavaMajor} 已存在并通过验证。", 100));
                return existing;
            }

            if (request.ReuseSystemJava)
            {
                progress?.Report(new(
                    "扫描本机",
                    $"正在查找可复用的 Java {request.JavaMajor} x64…"));
                var installed = await _discovery.ScanAsync(installRoot, cancellationToken);
                var matching = installed.FirstOrDefault(item => item.Major == request.JavaMajor);
                if (matching is not null)
                {
                    if (request.SetAsUserDefault)
                        await SetDefaultOrThrowAsync(
                            matching.JavaHome,
                            cancellationToken);
                    _log.Info(
                        "INSTALL",
                        $"复用本机 Java {matching.FullVersion}（{matching.Source}）：{matching.JavaHome}");
                    progress?.Report(new(
                        "完成",
                        $"已复用本机 Java {matching.FullVersion}（{matching.Source}）。",
                        100));
                    return new InstallResult(
                        matching.Major,
                        matching.JavaHome,
                        matching.JavaExe,
                        $"本机已有 · {matching.Source}",
                        matching.FullVersion,
                        Reused: true,
                        IsSystemRuntime: true);
                }
            }
        }

        if (Directory.Exists(target) &&
            !File.Exists(Path.Combine(target, MarkerFileName)))
        {
            throw new InvalidOperationException(
                $"目标文件夹已存在，但不是 Java Pilot 管理的目录：\n{target}\n\n" +
                "为了避免覆盖你的文件，程序已经停止。请选择其他安装目录，或者手动确认并处理该文件夹。");
        }

        var downloads = Path.Combine(installRoot, "downloads");
        Directory.CreateDirectory(downloads);
        var failures = new List<string>();

        foreach (var provider in _sourceResolver.GetResolvers(request.JavaMajor))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RuntimePackage package;
            try
            {
                progress?.Report(new(
                    "解析来源",
                    $"正在查询 {provider.Name} 的 Java {request.JavaMajor}…"));
                package = await provider.ResolveAsync(cancellationToken);
                _log.Info("SOURCE", $"已解析 {provider.Name}：{package.FileName}");
            }
            catch (Exception ex) when (
                ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                var failure = $"{provider.Name} 元数据失败：{ex.Message}";
                failures.Add(failure);
                _log.Warn("SOURCE", failure);
                progress?.Report(new("自动换源", failure, IsWarning: true));
                continue;
            }

            string? archive = null;
            try
            {
                progress?.Report(new(
                    "下载",
                    $"开始从 {provider.Name} 下载 Java {request.JavaMajor}…",
                    0));
                archive = await _downloader.DownloadAsync(
                    package,
                    downloads,
                    progress,
                    cancellationToken);
                return await ExtractDeployAndVerifyAsync(
                    request,
                    target,
                    package,
                    archive,
                    progress,
                    cancellationToken);
            }
            catch (JavaEnvironmentException)
            {
                throw;
            }
            catch (Exception ex) when (
                ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                var failure = $"{provider.Name} 安装链失败：{ex.Message}";
                failures.Add(failure);
                _log.Warn("INSTALL", failure);
                progress?.Report(new(
                    "自动恢复",
                    $"{provider.Name} 未成功，正在清理临时文件并切换下一供应商。",
                    IsWarning: true));

                if (archive is not null &&
                    ex is InvalidDataException or InvalidOperationException)
                {
                    TryDeleteFile(archive);
                }
            }
        }

        throw new InvalidOperationException(
            $"自动安装 Java {request.JavaMajor} 失败，已依次尝试全部可用官方供应商。\n\n" +
            string.Join("\n\n", failures) +
            "\n\n建议：检查磁盘空间、系统时间、代理、防病毒软件拦截和网络连接后重试。" +
            $"完整日志：{_log.FilePath}");
    }

    private async Task<InstallResult?> TryReuseAsync(
        string target,
        int expectedMajor,
        bool setAsDefault,
        CancellationToken cancellationToken)
    {
        var javaExe = Path.Combine(target, "bin", "java.exe");
        if (!File.Exists(javaExe))
            return null;

        (int Major, string FullVersion) version;
        try
        {
            version = await JavaVersionProbe.ProbeAsync(javaExe, cancellationToken);
            if (version.Major != expectedMajor)
                return null;
            var isManaged = File.Exists(Path.Combine(target, MarkerFileName));
            if (isManaged)
                WriteLauncher(target);
            _log.Info("INSTALL", $"复用已安装 Java {version.FullVersion}：{target}");
            if (setAsDefault)
                await SetDefaultOrThrowAsync(target, cancellationToken);
            return new InstallResult(
                expectedMajor,
                target,
                javaExe,
                ReadProvider(target) ?? (isManaged ? "Java Pilot 便携版" : "目标目录已有 Java"),
                version.FullVersion,
                Reused: true,
                IsSystemRuntime: !isManaged);
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _log.Warn("INSTALL", $"现有 Java 无法复用：{ex.Message}");
            return null;
        }
    }

    private async Task<InstallResult> ExtractDeployAndVerifyAsync(
        InstallRequest request,
        string target,
        RuntimePackage package,
        string archive,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var installRoot = Path.GetFullPath(request.InstallRoot);
        var temporaryRoot = Path.Combine(
            installRoot,
            ".java-pilot-temp",
            Guid.NewGuid().ToString("N"));
        var extractRoot = Path.Combine(temporaryRoot, "extract");
        var preparedRoot = Path.Combine(temporaryRoot, "prepared");
        Directory.CreateDirectory(extractRoot);

        try
        {
            progress?.Report(new("解压", $"正在解压 {package.Provider} 归档…", 0));
            await Task.Run(
                () => ZipFile.ExtractToDirectory(archive, extractRoot),
                cancellationToken);

            var candidates = Directory.EnumerateFiles(
                    extractRoot,
                    "java.exe",
                    SearchOption.AllDirectories)
                .Where(path => path.EndsWith(
                    $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}java.exe",
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => Path.GetRelativePath(extractRoot, path)
                    .Count(character =>
                        character == Path.DirectorySeparatorChar ||
                        character == Path.AltDirectorySeparatorChar))
                .ToArray();
            if (candidates.Length == 0)
                throw new InvalidDataException("归档中没有 bin\\java.exe，供应商格式可能已变化。");

            string? found = null;
            JavaVersionProbe.ProbeResult? extractedDetails = null;
            var candidateReports = new List<string>();
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var details = await JavaVersionProbe.ProbeDetailsAsync(
                        candidate,
                        cancellationToken);
                    candidateReports.Add(
                        $"{Path.GetRelativePath(extractRoot, candidate)} -> " +
                        $"Java {details.FullVersion} " +
                        $"{(details.Is64Bit ? "64 位" : "32 位")}");
                    if (details.Major == request.JavaMajor && details.Is64Bit)
                    {
                        found = candidate;
                        extractedDetails = details;
                        break;
                    }
                }
                catch (Exception ex) when (
                    ex is not OperationCanceledException ||
                    !cancellationToken.IsCancellationRequested)
                {
                    candidateReports.Add(
                        $"{Path.GetRelativePath(extractRoot, candidate)} -> " +
                        $"无法启动：{ex.Message}");
                }
            }

            if (found is null || extractedDetails is null)
            {
                foreach (var report in candidateReports)
                    _log.Warn("ARCHIVE", report);
                throw new InvalidDataException(
                    $"归档中没有通过验证的 Java {request.JavaMajor} 64 位运行时。\n" +
                    string.Join(
                        "\n",
                        candidateReports.Take(12)) +
                    (candidateReports.Count > 12
                        ? $"\n……其余 {candidateReports.Count - 12} 个候选已写入日志。"
                        : ""));
            }

            var sourceBin = Path.GetDirectoryName(found)!;
            var sourceHome = Directory.GetParent(sourceBin)!.FullName;
            Directory.Move(sourceHome, preparedRoot);

            var preparedJava = Path.Combine(preparedRoot, "bin", "java.exe");
            var version = await JavaVersionProbe.ProbeDetailsAsync(
                preparedJava,
                cancellationToken);
            if (version.Major != request.JavaMajor || !version.Is64Bit)
                throw new InvalidDataException(
                    $"下载后验证得到 Java {version.Major} " +
                    $"{(version.Is64Bit ? "64 位" : "32 位")}，" +
                    $"预期是 Java {request.JavaMajor} 64 位。");

            progress?.Report(new(
                "部署",
                $"Java {version.FullVersion} 64 位验证通过，正在原子部署…",
                90));
            DeployPreparedDirectory(installRoot, target, preparedRoot);

            var finalJava = Path.Combine(target, "bin", "java.exe");
            var finalVersion = await JavaVersionProbe.ProbeDetailsAsync(
                finalJava,
                cancellationToken);
            if (finalVersion.Major != request.JavaMajor || !finalVersion.Is64Bit)
                throw new InvalidDataException("部署后的 Java 版本或 64 位架构复核失败。");

            WriteMarker(target, package.Provider, finalVersion.FullVersion);
            WriteLauncher(target);
            if (request.SetAsUserDefault)
                await SetDefaultOrThrowAsync(target, cancellationToken);

            _log.Info(
                "INSTALL",
                $"安装完成：Java {finalVersion.FullVersion}; Provider={package.Provider}; Home={target}");
            progress?.Report(new(
                "完成",
                $"Java {finalVersion.FullVersion} 已安装并通过双重验证。",
                100));
            return new InstallResult(
                request.JavaMajor,
                target,
                finalJava,
                package.Provider,
                finalVersion.FullVersion,
                Reused: false);
        }
        finally
        {
            TryDeleteDirectory(temporaryRoot, installRoot);
        }
    }

    private static void DeployPreparedDirectory(
        string installRoot,
        string target,
        string preparedRoot)
    {
        EnsureChildPath(installRoot, target);
        EnsureChildPath(installRoot, preparedRoot);

        string? backup = null;
        if (Directory.Exists(target))
        {
            if (!File.Exists(Path.Combine(target, MarkerFileName)))
                throw new InvalidOperationException("拒绝覆盖非 Java Pilot 管理的目标目录。");
            backup = target + $".backup-{DateTime.Now:yyyyMMddHHmmss}";
            EnsureChildPath(installRoot, backup);
            Directory.Move(target, backup);
        }

        try
        {
            Directory.Move(preparedRoot, target);
            if (backup is not null)
                Directory.Delete(backup, recursive: true);
        }
        catch
        {
            if (!Directory.Exists(target) && backup is not null && Directory.Exists(backup))
                Directory.Move(backup, target);
            throw;
        }
    }

    private static void ValidateRequest(InstallRequest request)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Java Pilot 仅支持 Windows 10/11。");
        if (!Environment.Is64BitOperatingSystem)
            throw new PlatformNotSupportedException("仅支持 64 位 Windows 10/11。");
        if (request.JavaMajor is < 6 or > 99)
            throw new ArgumentOutOfRangeException(
                nameof(request.JavaMajor),
                $"不支持 Java {request.JavaMajor}。");
        if (string.IsNullOrWhiteSpace(request.InstallRoot))
            throw new ArgumentException("请选择 Java 安装目录。", nameof(request.InstallRoot));
        if (!Path.IsPathFullyQualified(request.InstallRoot))
            throw new ArgumentException("安装目录必须是完整路径。", nameof(request.InstallRoot));
    }

    private static void EnsureWritable(string directory)
    {
        var probe = Path.Combine(directory, $".java-pilot-write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, "write-test", Encoding.UTF8);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException(
                $"没有权限写入安装目录：{directory}\n请选择当前用户可写的文件夹，不需要以管理员身份运行。",
                ex);
        }
        finally
        {
            TryDeleteFile(probe);
        }
    }

    private static void EnsureFreeSpace(string directory)
    {
        try
        {
            var root = Path.GetPathRoot(directory);
            if (string.IsNullOrWhiteSpace(root))
                return;
            var drive = new DriveInfo(root);
            const long required = 900L * 1024 * 1024;
            if (drive.AvailableFreeSpace < required)
                throw new IOException(
                    $"磁盘空间不足。建议至少保留 900 MB，当前可用 " +
                    $"{ResilientDownloader.FormatBytes(drive.AvailableFreeSpace)}。");
        }
        catch (ArgumentException)
        {
            // UNC 路径等环境可能无法通过 DriveInfo 查询，后续写入仍会给出完整错误。
        }
    }

    private static void WriteMarker(string target, string provider, string version)
    {
        var marker = JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                provider,
                version,
                installedAt = DateTimeOffset.Now
            },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(
            Path.Combine(target, MarkerFileName),
            marker,
            new UTF8Encoding(false));
    }

    private static string? ReadProvider(string target)
    {
        try
        {
            var text = File.ReadAllText(Path.Combine(target, MarkerFileName));
            using var document = JsonDocument.Parse(text);
            return document.RootElement.TryGetProperty("provider", out var provider)
                ? provider.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteLauncher(string javaHome)
    {
        const string script =
            "@echo off\r\n" +
            "setlocal\r\n" +
            "set \"JAVA_HOME=%~dp0\"\r\n" +
            "set \"PATH=%JAVA_HOME%bin;%PATH%\"\r\n" +
            "\"%JAVA_HOME%bin\\java.exe\" %*\r\n";
        File.WriteAllText(
            Path.Combine(javaHome, "Run-Java.cmd"),
            script,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private async Task SetDefaultOrThrowAsync(
        string javaHome,
        CancellationToken cancellationToken)
    {
        try
        {
            await _environment
                .SetDefaultJavaAsync(javaHome, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error(
                "ENV",
                $"Java 已成功安装，但无法写入当前用户环境变量：{javaHome}",
                ex);
            throw new JavaEnvironmentException(
                $"Java 已安装并通过验证，但当前用户环境变量设置失败。\n" +
                $"Java 目录：{javaHome}\n" +
                $"你仍可直接使用 bin\\java.exe 或 Run-Java.cmd。\n" +
                $"完整原因：{ex.Message}",
                ex);
        }
    }

    private static void EnsureChildPath(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var normalizedCandidate = Path.GetFullPath(candidate);
        if (!normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"安全检查拒绝操作安装目录以外的路径：{candidate}");
    }

    private static void TryDeleteDirectory(string directory, string installRoot)
    {
        try
        {
            if (!Directory.Exists(directory))
                return;
            EnsureChildPath(installRoot, directory);
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // 清理失败不覆盖原始安装错误；残留目录只位于 .java-pilot-temp 下。
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 下载缓存清理失败会在日志之外保留文件，下一次校验会拒绝错误内容。
        }
    }

    public void Dispose() => _downloader.Dispose();

    private sealed class JavaEnvironmentException(string message, Exception inner)
        : InvalidOperationException(message, inner);
}

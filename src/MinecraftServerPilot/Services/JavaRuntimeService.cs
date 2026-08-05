using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using MinecraftServerPilot.Models;

namespace MinecraftServerPilot.Services;

public sealed partial class JavaRuntimeService
{
    private readonly ResilientDownloader _downloader;
    private readonly AppLog _log;

    public JavaRuntimeService(ResilientDownloader downloader, AppLog log)
    {
        _downloader = downloader;
        _log = log;
    }

    public async Task<JavaRuntime> EnsureAsync(
        int requiredMajor,
        string toolsDirectory,
        bool allowDownload,
        IProgress<OperationProgress>? progress,
        CancellationToken token)
    {
        var managed = Path.Combine(toolsDirectory, "java", requiredMajor.ToString(), "bin", "java.exe");
        if (File.Exists(managed) && await ProbeMajorAsync(managed, token) == requiredMajor)
            return new JavaRuntime(managed, requiredMajor, "项目便携 Java", true);

        progress?.Report(new("Java", $"正在扫描 Java {requiredMajor}…"));
        foreach (var candidate in DiscoverJavaExecutables())
        {
            var major = await ProbeMajorAsync(candidate, token);
            if (major == requiredMajor)
            {
                _log.Info("JAVA", $"选用已安装 Java {major}: {candidate}");
                return new JavaRuntime(candidate, major.Value, "系统已安装 Java", false);
            }
        }

        if (!allowDownload)
        {
            throw new InvalidOperationException(
                $"没有找到可用的 Java {requiredMajor} x64，且你选择了“只使用已安装 Java”。\n" +
                $"请从 Eclipse Adoptium（https://adoptium.net/temurin/releases/?version={requiredMajor}）" +
                $"或 Azul Zulu（https://www.azul.com/downloads/）手动安装 Java {requiredMajor} x64，" +
                "然后重新开始。未完成会话和已下载文件会保留并自动续作。");
        }

        progress?.Report(new("Java", $"未找到 Java {requiredMajor}，开始下载免安装便携版…", null, true));
        Directory.CreateDirectory(Path.Combine(toolsDirectory, "downloads"));
        var archive = await DownloadRuntimeArchiveAsync(requiredMajor, toolsDirectory, progress, token);
        var extractRoot = Path.Combine(toolsDirectory, "java", requiredMajor.ToString());
        if (Directory.Exists(extractRoot))
            Directory.Delete(extractRoot, recursive: true);
        Directory.CreateDirectory(extractRoot);
        progress?.Report(new("Java", $"正在解压 Java {requiredMajor}…"));
        ZipFile.ExtractToDirectory(archive, extractRoot);

        var found = Directory.EnumerateFiles(extractRoot, "java.exe", SearchOption.AllDirectories)
            .FirstOrDefault(path => path.EndsWith(
                $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}java.exe",
                StringComparison.OrdinalIgnoreCase));
        if (found is null)
            throw new InvalidDataException("Java 压缩包已下载，但其中没有 bin\\java.exe。文件可能损坏或供应商格式已变化。");

        var sourceBin = Path.GetDirectoryName(found)!;
        var sourceHome = Directory.GetParent(sourceBin)!.FullName;
        if (!sourceHome.Equals(extractRoot, StringComparison.OrdinalIgnoreCase))
        {
            var staging = extractRoot + ".flatten";
            if (Directory.Exists(staging))
                Directory.Delete(staging, true);
            Directory.Move(sourceHome, staging);
            Directory.Delete(extractRoot, true);
            Directory.Move(staging, extractRoot);
        }
        var javaExe = Path.Combine(extractRoot, "bin", "java.exe");
        var actual = await ProbeMajorAsync(javaExe, token);
        if (actual != requiredMajor)
            throw new InvalidDataException($"下载的 Java 版本为 {actual}，预期为 {requiredMajor}，已停止以避免不兼容。");
        _log.Info("JAVA", $"便携 Java {actual} 就绪：{javaExe}");
        return new JavaRuntime(javaExe, actual.Value, "Eclipse Temurin/备用供应商便携版", true);
    }

    private async Task<string> DownloadRuntimeArchiveAsync(
        int major,
        string toolsDirectory,
        IProgress<OperationProgress>? progress,
        CancellationToken token)
    {
        Exception? firstFailure = null;
        try
        {
            var metadataUrl =
                $"https://api.adoptium.net/v3/assets/latest/{major}/hotspot?architecture=x64&image_type=jre&os=windows&vendor=eclipse";
            var json = await _downloader.GetTextAsync(
                [new("Eclipse Adoptium API", new(metadataUrl))], token);
            using var document = JsonDocument.Parse(json);
            var package = document.RootElement[0].GetProperty("binary").GetProperty("package");
            var link = package.GetProperty("link").GetString()!;
            var checksum = package.GetProperty("checksum").GetString();
            var name = $"temurin-jre-{major}-windows-x64.zip";
            return await _downloader.DownloadAsync(
                new DownloadArtifact(
                [
                    new("Eclipse Adoptium 官方", new(link)),
                    new("Adoptium Binary API", new(
                        $"https://api.adoptium.net/v3/binary/latest/{major}/ga/windows/x64/jre/hotspot/normal/eclipse"))
                ], name, HashKind.Sha256, checksum),
                Path.Combine(toolsDirectory, "downloads"), progress, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            firstFailure = ex;
            _log.Warn("JAVA", $"Adoptium 下载链失败，切换 Azul Zulu：{ex.Message}");
        }

        try
        {
            var azulUrl =
                $"https://api.azul.com/metadata/v1/zulu/packages/?java_version={major}&os=windows&arch=x86&hw_bitness=64&archive_type=zip&java_package_type=jre&release_status=ga&availability_types=CA&latest=true";
            var json = await _downloader.GetTextAsync([new("Azul 官方 API", new(azulUrl))], token);
            using var document = JsonDocument.Parse(json);
            var item = document.RootElement[0];
            var url = item.GetProperty("download_url").GetString()!;
            var name = $"zulu-jre-{major}-windows-x64.zip";
            return await _downloader.DownloadAsync(
                new DownloadArtifact([new("Azul Zulu 官方", new(url))], name),
                Path.Combine(toolsDirectory, "downloads"), progress, token);
        }
        catch (Exception second) when (second is not OperationCanceledException || !token.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"自动安装 Java {major} 失败。已尝试 Eclipse Adoptium 与 Azul Zulu 两个独立供应商。\n" +
                $"Adoptium：{firstFailure?.Message}\nAzul：{second.Message}\n" +
                $"可手动安装 Java {major} x64 后重新点击开始，程序会自动识别。", second);
        }
    }

    private static IEnumerable<string> DiscoverJavaExecutables()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in pathValue.Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var path = Path.Combine(directory, "java.exe");
            if (File.Exists(path) && found.Add(path))
                yield return path;
        }

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 })
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            foreach (var parent in new[] { Path.Combine(root, "Java"), Path.Combine(root, "Eclipse Adoptium") })
            {
                if (!Directory.Exists(parent))
                    continue;
                foreach (var path in Directory.EnumerateFiles(parent, "java.exe", SearchOption.AllDirectories))
                    if (found.Add(path))
                        yield return path;
            }
        }

        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            foreach (var keyName in new[]
                     {
                         @"SOFTWARE\JavaSoft\JDK", @"SOFTWARE\JavaSoft\Java Runtime Environment",
                         @"SOFTWARE\Eclipse Adoptium\JDK", @"SOFTWARE\Eclipse Adoptium\JRE"
                     })
            {
                using var key = baseKey.OpenSubKey(keyName);
                if (key is null)
                    continue;
                foreach (var version in key.GetSubKeyNames())
                {
                    using var versionKey = key.OpenSubKey(version);
                    var home = versionKey?.GetValue("JavaHome") as string ??
                               versionKey?.GetValue("Path") as string;
                    if (home is null)
                        continue;
                    var path = Path.Combine(home, "bin", "java.exe");
                    if (File.Exists(path) && found.Add(path))
                        yield return path;
                }
            }
        }
    }

    private async Task<int?> ProbeMajorAsync(string javaExe, CancellationToken token)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(javaExe, "-version")
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var outputTask = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(5), token);
            var text = await outputTask;
            var match = JavaVersionRegex().Match(text);
            if (!match.Success)
                return null;
            var first = int.Parse(match.Groups["first"].Value);
            var second = match.Groups["second"].Success ? int.Parse(match.Groups["second"].Value) : 0;
            return first == 1 ? second : first;
        }
        catch (Exception ex)
        {
            _log.Warn("JAVA", $"无法探测 {javaExe}: {ex.Message}");
            return null;
        }
    }

    [GeneratedRegex("version\\s+\"(?<first>\\d+)(?:\\.(?<second>\\d+))?", RegexOptions.IgnoreCase)]
    private static partial Regex JavaVersionRegex();
}

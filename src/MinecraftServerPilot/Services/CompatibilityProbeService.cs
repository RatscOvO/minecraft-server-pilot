using System.Text.Json;
using MinecraftServerPilot.Models;

namespace MinecraftServerPilot.Services;

public sealed record ProbeArtifact(
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> CleanupDirectories);

public sealed class CompatibilityProbeService
{
    private readonly ResilientDownloader _downloader;
    private readonly AppLog _log;

    public CompatibilityProbeService(ResilientDownloader downloader, AppLog log)
    {
        _downloader = downloader;
        _log = log;
    }

    public async Task<ProbeArtifact?> TryInstallAsync(
        ServerKind kind,
        string gameVersion,
        string serverDirectory,
        IProgress<OperationProgress>? progress,
        CancellationToken token)
    {
        if (kind == ServerKind.Vanilla)
            return null;
        var loader = kind switch
        {
            ServerKind.Paper => "paper",
            ServerKind.Fabric => "fabric",
            ServerKind.Forge => "forge",
            _ => ""
        };
        var installedPaths = new List<string>();
        try
        {
            var query = Uri.EscapeDataString($"[\"{loader}\"]");
            var versions = Uri.EscapeDataString($"[\"{gameVersion}\"]");
            var url = $"https://api.modrinth.com/v2/project/spark/version?loaders={query}&game_versions={versions}";
            var json = await _downloader.GetTextAsync(
                [new("Modrinth 官方 API（spark 测试组件）", new(url))], token);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.GetArrayLength() == 0)
            {
                _log.Warn("PROBE", $"spark 没有 {gameVersion}/{loader} 构建，将只验证加载器本身。");
                progress?.Report(new("兼容性测试",
                    "没有适配此版本的安全测试组件，将执行加载器级启动测试。", null, true));
                return null;
            }
            if (kind == ServerKind.Fabric)
            {
                progress?.Report(new("兼容性测试", "正在解析 spark 的 Fabric API 前置依赖…"));
                installedPaths.Add(await DownloadProjectVersionAsync(
                    projectId: "P7dR8mSH",
                    displayName: "Fabric API 前置",
                    loader,
                    gameVersion,
                    Path.Combine(serverDirectory, "mods"),
                    progress,
                    token));
            }
            var version = document.RootElement[0];
            var files = version.GetProperty("files").EnumerateArray().ToArray();
            var file = files.FirstOrDefault(x =>
                x.TryGetProperty("primary", out var primary) && primary.GetBoolean());
            if (file.ValueKind == JsonValueKind.Undefined)
                file = files.First();
            var fileName = file.GetProperty("filename").GetString()!;
            var fileUrl = file.GetProperty("url").GetString()!;
            var hash = file.GetProperty("hashes").TryGetProperty("sha512", out var sha)
                ? sha.GetString()
                : null;
            var size = file.TryGetProperty("size", out var sizeValue) ? sizeValue.GetInt64() : (long?)null;
            var componentDirectory = Path.Combine(serverDirectory,
                kind == ServerKind.Paper ? "plugins" : "mods");
            progress?.Report(new("兼容性测试", $"下载一次性测试组件 spark：{fileName}"));
            var path = await _downloader.DownloadAsync(
                new DownloadArtifact([new("Modrinth 官方 CDN", new(fileUrl))],
                    fileName, hash is null ? HashKind.None : HashKind.Sha512, hash, size),
                componentDirectory, progress, token);
            installedPaths.Add(path);
            return new ProbeArtifact(installedPaths,
            [
                Path.Combine(serverDirectory, "plugins", "spark"),
                Path.Combine(serverDirectory, "config", "spark"),
                Path.Combine(serverDirectory, "config", "fabric")
            ]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            foreach (var path in installedPaths)
            {
                try { SafeDeleteFile(path, serverDirectory); }
                catch (Exception cleanupError)
                {
                    _log.Warn("PROBE", $"清理未完成测试组件失败：{cleanupError.Message}");
                }
            }
            _log.Warn("PROBE", $"测试组件准备失败，降级为加载器启动验证：{ex}");
            progress?.Report(new("兼容性测试",
                $"测试组件不可用，已安全降级为加载器验证：{ex.Message}", null, true));
            return null;
        }
    }

    public void Cleanup(ProbeArtifact? probe, string serverDirectory)
    {
        if (probe is not null)
        {
            foreach (var path in probe.Paths)
                SafeDeleteFile(path, serverDirectory);
            foreach (var directory in probe.CleanupDirectories)
                SafeDeleteDirectory(directory, serverDirectory);
        }
        foreach (var name in new[] { "world", "world_nether", "world_the_end" })
            SafeDeleteDirectory(Path.Combine(serverDirectory, name), serverDirectory);
        _log.Info("PROBE", "已清除一次性测试组件与测试世界。");
    }

    private async Task<string> DownloadProjectVersionAsync(
        string projectId,
        string displayName,
        string loader,
        string gameVersion,
        string destinationDirectory,
        IProgress<OperationProgress>? progress,
        CancellationToken token)
    {
        var loaders = Uri.EscapeDataString($"[\"{loader}\"]");
        var games = Uri.EscapeDataString($"[\"{gameVersion}\"]");
        var url =
            $"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(projectId)}/version?loaders={loaders}&game_versions={games}";
        var json = await _downloader.GetTextAsync(
            [new($"Modrinth 官方 API（{displayName}）", new(url))], token);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.GetArrayLength() == 0)
            throw new InvalidOperationException($"{displayName} 没有 {gameVersion}/{loader} 的兼容构建。");
        var version = document.RootElement[0];
        var files = version.GetProperty("files").EnumerateArray().ToArray();
        var file = files.FirstOrDefault(x =>
            x.TryGetProperty("primary", out var primary) && primary.GetBoolean());
        if (file.ValueKind == JsonValueKind.Undefined)
            file = files.First();
        var fileName = file.GetProperty("filename").GetString()!;
        var fileUrl = file.GetProperty("url").GetString()!;
        var sha512 = file.GetProperty("hashes").TryGetProperty("sha512", out var hash)
            ? hash.GetString()
            : null;
        var size = file.TryGetProperty("size", out var sizeValue) ? sizeValue.GetInt64() : (long?)null;
        progress?.Report(new("兼容性测试", $"下载一次性{displayName}：{fileName}"));
        return await _downloader.DownloadAsync(
            new DownloadArtifact([new("Modrinth 官方 CDN", new(fileUrl))],
                fileName, sha512 is null ? HashKind.None : HashKind.Sha512, sha512, size),
            destinationDirectory, progress, token);
    }

    private static void SafeDeleteFile(string path, string root)
    {
        EnsureChild(path, root);
        if (File.Exists(path))
            File.Delete(path);
    }

    private static void SafeDeleteDirectory(string path, string root)
    {
        EnsureChild(path, root);
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private static void EnsureChild(string path, string root)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"拒绝清理服务端目录之外的路径：{fullPath}");
    }
}

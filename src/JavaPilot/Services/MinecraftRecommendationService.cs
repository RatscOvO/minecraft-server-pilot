using System.Net;
using System.Net.Http;
using System.Text.Json;
using JavaPilot.Models;

namespace JavaPilot.Services;

public sealed class MinecraftRecommendationService : IDisposable
{
    private static readonly Uri ManifestUri =
        new("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
    private readonly HttpClient _client;
    private readonly AppLog _log;
    private readonly string _cacheFile;

    public MinecraftRecommendationService(AppLog log)
    {
        _log = log;
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionLifetime = TimeSpan.FromMinutes(3)
        };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "JavaPilot/0.5.3 (Minecraft Java recommendation calibration)");
        _cacheFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JavaPilot",
            "cache",
            "minecraft-java-latest.json");
    }

    public async Task<IReadOnlyList<JavaOption>> GetOptionsAsync(
        CancellationToken cancellationToken)
    {
        var cache = ReadCache();
        if (cache is not null &&
            DateTimeOffset.Now - cache.CheckedAt < TimeSpan.FromHours(24))
        {
            _log.Info(
                "CATALOG",
                $"使用 Minecraft 推荐缓存：{cache.Release} -> Java {cache.JavaMajor}");
            return JavaCatalog.WithLatestMinecraftRelease(cache.Release, cache.JavaMajor);
        }

        try
        {
            var online = await QueryLatestAsync(cancellationToken);
            WriteCache(online);
            _log.Info(
                "CATALOG",
                $"Mojang 在线校准完成：Minecraft {online.Release} -> Java {online.JavaMajor}");
            return JavaCatalog.WithLatestMinecraftRelease(
                online.Release,
                online.JavaMajor);
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (cache is not null)
            {
                _log.Warn(
                    "CATALOG",
                    $"在线校准失败，使用过期缓存：{ex.Message}");
                return JavaCatalog.WithLatestMinecraftRelease(
                    cache.Release,
                    cache.JavaMajor);
            }

            _log.Warn(
                "CATALOG",
                $"在线校准失败，使用内置兼容表：{ex.Message}");
            return JavaCatalog.Options;
        }
    }

    private async Task<RecommendationCache> QueryLatestAsync(
        CancellationToken cancellationToken)
    {
        using var manifestResponse = await _client.GetAsync(
            ManifestUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        manifestResponse.EnsureSuccessStatusCode();
        var manifestText = await manifestResponse.Content.ReadAsStringAsync(cancellationToken);
        using var manifest = JsonDocument.Parse(manifestText);
        var latestRelease = manifest.RootElement
            .GetProperty("latest")
            .GetProperty("release")
            .GetString() ?? throw new InvalidDataException("Mojang 清单缺少 latest.release。");
        var entry = manifest.RootElement.GetProperty("versions")
            .EnumerateArray()
            .FirstOrDefault(item =>
                item.GetProperty("id").GetString() == latestRelease);
        if (entry.ValueKind == JsonValueKind.Undefined)
            throw new InvalidDataException("Mojang 清单找不到最新正式版详情地址。");
        var detailUrl = entry.GetProperty("url").GetString()
            ?? throw new InvalidDataException("Mojang 最新正式版缺少详情地址。");

        using var detailResponse = await _client.GetAsync(
            detailUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        detailResponse.EnsureSuccessStatusCode();
        var detailText = await detailResponse.Content.ReadAsStringAsync(cancellationToken);
        using var detail = JsonDocument.Parse(detailText);
        if (!detail.RootElement.TryGetProperty("javaVersion", out var java) ||
            !java.TryGetProperty("majorVersion", out var major))
            throw new InvalidDataException(
                $"Minecraft {latestRelease} 的官方元数据没有 Java 主版本。");

        return new RecommendationCache(
            latestRelease,
            major.GetInt32(),
            DateTimeOffset.Now);
    }

    private RecommendationCache? ReadCache()
    {
        try
        {
            if (!File.Exists(_cacheFile))
                return null;
            return JsonSerializer.Deserialize<RecommendationCache>(
                File.ReadAllText(_cacheFile));
        }
        catch (Exception ex)
        {
            _log.Warn("CATALOG", $"推荐缓存损坏，将重新查询：{ex.Message}");
            return null;
        }
    }

    private void WriteCache(RecommendationCache cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
            var temporary = _cacheFile + ".tmp";
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(
                    cache,
                    new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            File.Move(temporary, _cacheFile, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.Warn("CATALOG", $"无法写入推荐缓存：{ex.Message}");
        }
    }

    public void Dispose() => _client.Dispose();

    private sealed record RecommendationCache(
        string Release,
        int JavaMajor,
        DateTimeOffset CheckedAt);
}

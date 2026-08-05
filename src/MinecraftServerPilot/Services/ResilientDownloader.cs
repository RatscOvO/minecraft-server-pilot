using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace MinecraftServerPilot.Services;

public sealed class ResilientDownloader : IDisposable
{
    private static readonly TimeSpan DefaultReadIdleTimeout = TimeSpan.FromSeconds(20);
    private readonly HttpClient _client;
    private readonly AppLog _log;
    private readonly TimeSpan _readIdleTimeout;

    public ResilientDownloader(AppLog log, TimeSpan? readIdleTimeout = null)
    {
        _log = log;
        _readIdleTimeout = readIdleTimeout ?? DefaultReadIdleTimeout;
        if (_readIdleTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(readIdleTimeout), "下载失速超时必须大于零。");
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionLifetime = TimeSpan.FromMinutes(3)
        };
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "MinecraftServerPilot/0.3.0 (Windows desktop server installer; contact: local-user)");
        _client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.6");
    }

    public async Task<string> GetTextAsync(
        IEnumerable<DownloadCandidate> sources,
        CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        foreach (var source in sources)
        {
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    _log.Info("NETWORK", $"读取元数据：{source.Name} ({source.Uri})，第 {attempt} 次");
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(attempt == 1 ? 12 : 25));
                    using var response = await _client.GetAsync(source.Uri, timeout.Token);
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsStringAsync(timeout.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    var failure = $"{source.Name} 第 {attempt} 次失败：{Describe(ex)}";
                    failures.Add(failure);
                    _log.Warn("NETWORK", failure);
                }
            }
        }
        throw new InvalidOperationException(
            "所有元数据源均不可用。\n" + string.Join("\n", failures) +
            "\n建议：检查网络、DNS、系统时间或代理设置，稍后重试。");
    }

    public async Task<string> DownloadAsync(
        Models.DownloadArtifact artifact,
        string destinationDirectory,
        IProgress<Models.OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);
        var destination = Path.Combine(destinationDirectory, artifact.FileName);
        if (File.Exists(destination) && await VerifyAsync(destination, artifact, cancellationToken))
        {
            _log.Info("DOWNLOAD", $"复用已校验文件：{destination}");
            progress?.Report(new("下载", $"已存在且校验通过：{artifact.FileName}", 100));
            return destination;
        }

        var partial = destination + ".part";
        if (File.Exists(partial) && await VerifyAsync(partial, artifact, cancellationToken))
        {
            File.Move(partial, destination, overwrite: true);
            _log.Info("DOWNLOAD", $"断点文件已完整并通过校验：{destination}");
            progress?.Report(new("下载", $"断点文件已校验完成：{artifact.FileName}", 100));
            return destination;
        }
        if (File.Exists(partial) &&
            artifact.ExpectedSize is long expectedSize &&
            new FileInfo(partial).Length >= expectedSize)
        {
            _log.Warn("DOWNLOAD", "断点文件大小已完整但校验失败，将删除后重新下载。");
            File.Delete(partial);
        }
        var failures = new List<string>();
        foreach (var source in artifact.Sources)
        {
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    progress?.Report(new("下载", $"正在连接 {source.Name}（第 {attempt} 次）…"));
                    _log.Info("DOWNLOAD", $"开始：{source.Name} {source.Uri}");
                    await DownloadOneAsync(source, partial, artifact.ExpectedSize, progress, cancellationToken);
                    if (!await VerifyAsync(partial, artifact, cancellationToken))
                        throw new InvalidDataException("文件大小或哈希校验不一致，已拒绝使用该文件");
                    File.Move(partial, destination, overwrite: true);
                    _log.Info("DOWNLOAD", $"完成并校验：{destination}");
                    progress?.Report(new("下载", $"{artifact.FileName} 下载并校验完成", 100));
                    return destination;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    var failure = $"{source.Name} 第 {attempt} 次失败：{Describe(ex)}";
                    failures.Add(failure);
                    _log.Warn("DOWNLOAD", failure);
                    if (ex is DownloadStalledException)
                    {
                        var preserved = File.Exists(partial)
                            ? FormatBytes(new FileInfo(partial).Length)
                            : "0 B";
                        progress?.Report(new("下载",
                            $"连接超过 {_readIdleTimeout.TotalSeconds:0} 秒没有收到数据；" +
                            $"已保留 {preserved} 断点，正在自动重连或换源…",
                            IsWarning: true));
                    }
                    if (ex is InvalidDataException && File.Exists(partial))
                        File.Delete(partial);
                }
            }
        }

        throw new InvalidOperationException(
            $"无法下载 {artifact.FileName}，已尝试 {artifact.Sources.Count} 个来源及自动重试。\n" +
            string.Join("\n", failures) +
            "\n建议：确认磁盘空间充足、关闭拦截下载的安全软件，或更换网络后点击重试。");
    }

    private async Task DownloadOneAsync(
        Models.DownloadCandidate source,
        string partialPath,
        long? expectedSize,
        IProgress<Models.OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var existing = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (existing > 0)
        {
            _log.Info("DOWNLOAD",
                $"从断点续传：{source.Name}，已完成 {FormatBytes(existing)}");
            progress?.Report(new("下载",
                $"正在从 {FormatBytes(existing)} 断点续传 · {source.Name}",
                expectedSize is > 0 ? existing * 100d / expectedSize.Value : null));
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, source.Uri);
        if (existing > 0)
            request.Headers.Range = new RangeHeaderValue(existing, null);

        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headerTimeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var response = await _client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token);

        if (existing > 0 && response.StatusCode != HttpStatusCode.PartialContent)
        {
            _log.Warn("DOWNLOAD",
                $"{source.Name} 不支持当前断点请求，将安全地从头下载。");
            existing = 0;
            File.Delete(partialPath);
        }
        response.EnsureSuccessStatusCode();
        if (existing > 0 &&
            response.Content.Headers.ContentRange?.From is long rangeStart &&
            rangeStart != existing)
        {
            throw new InvalidDataException(
                $"服务器返回的断点位置 {rangeStart} 与本地文件大小 {existing} 不一致");
        }

        long? total = expectedSize ??
                      (response.Content.Headers.ContentLength is long length
                          ? existing + length
                          : null);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            partialPath, existing > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write,
            FileShare.Read, 1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1024 * 128];
        var downloaded = existing;
        var stopwatch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        while (true)
        {
            int read;
            using (var readTimeout =
                   CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                readTimeout.CancelAfter(_readIdleTimeout);
                try
                {
                    read = await input.ReadAsync(buffer.AsMemory(), readTimeout.Token);
                }
                catch (OperationCanceledException)
                    when (!cancellationToken.IsCancellationRequested)
                {
                    throw new DownloadStalledException(
                        $"连续 {_readIdleTimeout.TotalSeconds:0} 秒没有收到新数据");
                }
            }
            if (read == 0)
                break;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloaded += read;
            if (stopwatch.Elapsed - lastReport >= TimeSpan.FromMilliseconds(300))
            {
                lastReport = stopwatch.Elapsed;
                var speed = (downloaded - existing) / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.1);
                var percent = total is > 0 ? downloaded * 100d / total.Value : (double?)null;
                progress?.Report(new("下载",
                    $"{source.Name} · {FormatBytes(downloaded)}" +
                    (total is > 0 ? $" / {FormatBytes(total.Value)}" : "") +
                    (percent is not null ? $" ({percent.Value:0.0}%)" : "") +
                    $" · {FormatBytes((long)speed)}/s", percent));
            }
        }
        await output.FlushAsync(cancellationToken);
        if (expectedSize is long expected && downloaded < expected)
        {
            throw new IOException(
                $"连接提前结束，已接收 {FormatBytes(downloaded)} / {FormatBytes(expected)}；" +
                "断点已保留以便自动续传");
        }
        if (expectedSize is long maximum && downloaded > maximum)
            throw new InvalidDataException(
                $"下载大小异常：收到 {FormatBytes(downloaded)}，预期 {FormatBytes(maximum)}");
    }

    private static async Task<bool> VerifyAsync(
        string path,
        Models.DownloadArtifact artifact,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (artifact.ExpectedSize is long size && info.Length != size)
            return false;
        if (artifact.HashKind == Models.HashKind.None || string.IsNullOrWhiteSpace(artifact.ExpectedHash))
            return info.Length > 0;

        await using var stream = File.OpenRead(path);
        using HashAlgorithm algorithm = artifact.HashKind switch
        {
            Models.HashKind.Sha1 => SHA1.Create(),
            Models.HashKind.Sha256 => SHA256.Create(),
            Models.HashKind.Sha512 => SHA512.Create(),
            _ => throw new ArgumentOutOfRangeException()
        };
        var hash = Convert.ToHexString(await algorithm.ComputeHashAsync(stream, cancellationToken));
        return hash.Equals(artifact.ExpectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException => "连接或响应超时",
        HttpRequestException http when http.StatusCode is not null =>
            $"HTTP {(int)http.StatusCode.Value} {http.StatusCode}",
        HttpRequestException => $"网络错误：{ex.Message}",
        UnauthorizedAccessException => $"没有文件写入权限：{ex.Message}",
        IOException => $"磁盘/文件错误：{ex.Message}",
        _ => ex.Message
    };

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var number = (double)value;
        var unit = 0;
        while (number >= 1024 && unit < units.Length - 1)
        {
            number /= 1024;
            unit++;
        }
        return $"{number:0.##} {units[unit]}";
    }

    public void Dispose() => _client.Dispose();

    private sealed class DownloadStalledException(string message) : TimeoutException(message);
}

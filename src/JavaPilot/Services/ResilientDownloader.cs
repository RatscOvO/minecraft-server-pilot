using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using JavaPilot.Models;

namespace JavaPilot.Services;

public sealed class ResilientDownloader : IDisposable
{
    private readonly HttpClient _client;
    private readonly AppLog _log;
    private readonly TimeSpan _readIdleTimeout;
    private readonly TimeSpan _lowSpeedWindow;
    private readonly long _minimumSustainableBytesPerSecond;

    public ResilientDownloader(
        AppLog log,
        TimeSpan? readIdleTimeout = null,
        TimeSpan? lowSpeedWindow = null,
        long minimumSustainableBytesPerSecond = 24 * 1024)
    {
        _log = log;
        _readIdleTimeout = readIdleTimeout ?? TimeSpan.FromSeconds(18);
        _lowSpeedWindow = lowSpeedWindow ?? TimeSpan.FromSeconds(30);
        _minimumSustainableBytesPerSecond = minimumSustainableBytesPerSecond;
        if (_readIdleTimeout <= TimeSpan.Zero ||
            _lowSpeedWindow <= TimeSpan.Zero ||
            _minimumSustainableBytesPerSecond <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(readIdleTimeout),
                "下载超时、低速观察窗口和最低速度必须大于零。");
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionLifetime = TimeSpan.FromMinutes(3)
        };
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "JavaPilot/0.5.3 (Windows Java runtime installer)");
        _client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.6");
    }

    public async Task<string> GetTextAsync(
        IReadOnlyList<DownloadSource> sources,
        CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            foreach (var source in sources)
            {
                try
                {
                    _log.Info(
                        "NETWORK",
                        $"读取元数据：{source.Name} ({source.Uri})，第 {attempt} 次");
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(attempt == 1
                        ? TimeSpan.FromSeconds(12)
                        : TimeSpan.FromSeconds(25));
                    using var response = await _client.GetAsync(source.Uri, timeout.Token);
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsStringAsync(timeout.Token);
                }
                catch (Exception ex) when (
                    ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    var failure = $"{source.Name} 第 {attempt} 次失败：{Describe(ex)}";
                    failures.Add(failure);
                    _log.Warn("NETWORK", failure);
                }
            }
        }

        throw new InvalidOperationException(
            "所有元数据源均不可用。\n" + string.Join("\n", failures) +
            "\n建议检查网络、DNS、代理和系统时间。程序不会使用来源不明的文件。");
    }

    public async Task<string> DownloadAsync(
        RuntimePackage package,
        string destinationDirectory,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);
        var destination = Path.Combine(destinationDirectory, package.FileName);
        var partial = destination + ".part";

        if (File.Exists(destination))
        {
            if (await VerifyAsync(destination, package, cancellationToken))
            {
                _log.Info("DOWNLOAD", $"复用已校验归档：{destination}");
                progress?.Report(new("下载", $"复用已校验文件：{package.FileName}", 100));
                return destination;
            }

            _log.Warn("DOWNLOAD", $"已存在的归档校验失败，将重新下载：{destination}");
            File.Delete(destination);
        }

        if (File.Exists(partial) &&
            package.ExpectedSize is long expected &&
            new FileInfo(partial).Length > expected)
        {
            _log.Warn("DOWNLOAD", "断点文件大于预期大小，将丢弃错误断点。");
            File.Delete(partial);
        }

        var failures = new List<string>();
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            foreach (var source in package.Sources)
            {
                try
                {
                    progress?.Report(new(
                        "下载",
                        $"连接 {source.Name}（第 {attempt} 次）…"));
                    _log.Info("DOWNLOAD", $"开始：{source.Name} {source.Uri}");
                    await DownloadOneAsync(
                        source,
                        partial,
                        package.ExpectedSize,
                        progress,
                        cancellationToken);

                    if (!await VerifyAsync(partial, package, cancellationToken))
                        throw new InvalidDataException("大小或哈希校验不一致");

                    File.Move(partial, destination, overwrite: true);
                    _log.Info("DOWNLOAD", $"完成并校验：{destination}");
                    progress?.Report(new("下载", $"{package.FileName} 下载并校验完成", 100));
                    return destination;
                }
                catch (Exception ex) when (
                    ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    var failure = $"{source.Name} 第 {attempt} 次失败：{Describe(ex)}";
                    failures.Add(failure);
                    _log.Warn("DOWNLOAD", failure);

                    if (ex is DownloadStalledException)
                    {
                        var preserved = File.Exists(partial)
                            ? FormatBytes(new FileInfo(partial).Length)
                            : "0 B";
                        progress?.Report(new(
                            "自动恢复",
                            $"{_readIdleTimeout.TotalSeconds:0} 秒没有收到数据，" +
                            $"已保留 {preserved} 断点并自动重连/换源。",
                            IsWarning: true));
                    }
                    else if (ex is DownloadTooSlowException)
                    {
                        var preserved = File.Exists(partial)
                            ? FormatBytes(new FileInfo(partial).Length)
                            : "0 B";
                        progress?.Report(new(
                            "自动换源",
                            $"连续 {_lowSpeedWindow.TotalSeconds:0} 秒速度低于 " +
                            $"{FormatBytes(_minimumSustainableBytesPerSecond)}/s，" +
                            $"已保留 {preserved} 断点并切换线路。",
                            IsWarning: true));
                    }

                    if (ex is InvalidDataException && File.Exists(partial))
                        File.Delete(partial);
                }
            }
        }

        throw new InvalidOperationException(
            $"无法从 {package.Provider} 下载 {package.FileName}。\n" +
            string.Join("\n", failures));
    }

    private async Task DownloadOneAsync(
        DownloadSource source,
        string partialPath,
        long? expectedSize,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var existing = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, source.Uri);
        if (existing > 0)
            request.Headers.Range = new RangeHeaderValue(existing, null);

        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headerTimeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            headerTimeout.Token);

        if (existing > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            throw new InvalidDataException("服务器拒绝了现有断点范围，将清除断点后从头重试。");

        if (existing > 0 && response.StatusCode != HttpStatusCode.PartialContent)
        {
            _log.Warn(
                "DOWNLOAD",
                $"{source.Name} 不支持当前断点，将从头下载而不是拼接错误文件。");
            existing = 0;
        }

        response.EnsureSuccessStatusCode();
        var total = expectedSize ??
                    (response.Content.Headers.ContentLength is long length
                        ? length + existing
                        : null);
        var mode = existing > 0 ? FileMode.Append : FileMode.Create;

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            partialPath,
            mode,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 131072,
            useAsync: true);

        var buffer = new byte[131072];
        var completed = existing;
        var lastReport = DateTime.UtcNow;
        var speedStart = DateTime.UtcNow;
        var speedBytes = completed;
        var lowSpeedWindowStart = DateTime.UtcNow;
        var lowSpeedWindowBytes = completed;

        while (true)
        {
            int read;
            try
            {
                read = await input.ReadAsync(buffer, cancellationToken)
                    .AsTask()
                    .WaitAsync(_readIdleTimeout, cancellationToken);
            }
            catch (TimeoutException ex)
            {
                throw new DownloadStalledException(
                    $"超过 {_readIdleTimeout.TotalSeconds:0} 秒没有收到数据。", ex);
            }

            if (read == 0)
                break;

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            completed += read;

            var now = DateTime.UtcNow;
            if ((now - lastReport).TotalMilliseconds < 650)
                continue;

            var seconds = Math.Max((now - speedStart).TotalSeconds, 0.001);
            var bytesPerSecond = (completed - speedBytes) / seconds;
            double? percent = total is > 0 ? completed * 100d / total.Value : null;
            progress?.Report(new(
                "下载",
                $"{source.Name} · {FormatBytes(completed)}" +
                (total is > 0 ? $" / {FormatBytes(total.Value)}" : "") +
                $" · {FormatBytes((long)bytesPerSecond)}/s",
                percent));
            lastReport = now;
            if (seconds >= 4)
            {
                speedStart = now;
                speedBytes = completed;
            }

            var lowSpeedSeconds = (now - lowSpeedWindowStart).TotalSeconds;
            if (lowSpeedSeconds >= _lowSpeedWindow.TotalSeconds)
            {
                var windowSpeed = (completed - lowSpeedWindowBytes) / lowSpeedSeconds;
                var remaining = total is > 0 ? total.Value - completed : long.MaxValue;
                if (windowSpeed < _minimumSustainableBytesPerSecond &&
                    remaining > 5L * 1024 * 1024)
                {
                    throw new DownloadTooSlowException(
                        $"连续 {_lowSpeedWindow.TotalSeconds:0} 秒平均速度仅 " +
                        $"{FormatBytes((long)windowSpeed)}/s。");
                }

                lowSpeedWindowStart = now;
                lowSpeedWindowBytes = completed;
            }
        }

        await output.FlushAsync(cancellationToken);
    }

    private static async Task<bool> VerifyAsync(
        string path,
        RuntimePackage package,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return false;

        if (package.ExpectedSize is long expected &&
            new FileInfo(path).Length != expected)
            return false;

        if (package.HashKind == HashKind.None ||
            string.IsNullOrWhiteSpace(package.ExpectedHash))
            return new FileInfo(path).Length > 1_000_000;

        await using var stream = File.OpenRead(path);
        byte[] hash = package.HashKind switch
        {
            HashKind.Sha1 => await SHA1.HashDataAsync(stream, cancellationToken),
            HashKind.Sha256 => await SHA256.HashDataAsync(stream, cancellationToken),
            _ => []
        };
        return Convert.ToHexString(hash).Equals(
            NormalizeHash(package.ExpectedHash),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeHash(string value)
    {
        var token = value.Trim().Split(
            [' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries)[0];
        return token.Replace("-", "", StringComparison.Ordinal);
    }

    private static string Describe(Exception exception) => exception switch
    {
        HttpRequestException http when http.StatusCode is not null =>
            $"HTTP {(int)http.StatusCode} {http.StatusCode}",
        OperationCanceledException => "连接或读取超时",
        _ => exception.Message
    };

    public static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var amount = (double)value;
        var unit = 0;
        while (amount >= 1024 && unit < units.Length - 1)
        {
            amount /= 1024;
            unit++;
        }

        return $"{amount:0.##} {units[unit]}";
    }

    public void Dispose() => _client.Dispose();

    private sealed class DownloadStalledException(string message, Exception inner)
        : TimeoutException(message, inner);

    private sealed class DownloadTooSlowException(string message)
        : TimeoutException(message);
}

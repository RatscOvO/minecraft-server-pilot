using System.Text.Json;
using JavaPilot.Models;

namespace JavaPilot.Services;

public sealed class RuntimeSourceResolver
{
    private readonly ResilientDownloader _downloader;
    private readonly AppLog _log;

    public RuntimeSourceResolver(ResilientDownloader downloader, AppLog log)
    {
        _downloader = downloader;
        _log = log;
    }

    public IReadOnlyList<ProviderResolver> GetResolvers(int major)
    {
        var resolvers = new List<ProviderResolver>();
        if (major >= 8)
            resolvers.Add(new("Eclipse Temurin", token => ResolveAdoptiumAsync(major, token)));

        resolvers.Add(new("Azul Zulu", token => ResolveAzulAsync(major, token)));

        if (major >= 8)
            resolvers.Add(new("BellSoft Liberica", token => ResolveBellSoftAsync(major, token)));

        if (major is 8 or 11 or 17 or 21 or 25)
            resolvers.Add(new("Amazon Corretto", token => ResolveCorrettoAsync(major, token)));

        return resolvers;
    }

    private async Task<RuntimePackage> ResolveAdoptiumAsync(
        int major,
        CancellationToken cancellationToken)
    {
        var metadataUrl =
            $"https://api.adoptium.net/v3/assets/latest/{major}/hotspot" +
            "?architecture=x64&image_type=jdk&os=windows&vendor=eclipse";
        var json = await _downloader.GetTextAsync(
            [new("Eclipse Adoptium API", new(metadataUrl))],
            cancellationToken);
        using var document = JsonDocument.Parse(json);
        var items = document.RootElement.EnumerateArray().ToArray();
        if (items.Length == 0)
            throw new InvalidOperationException($"Adoptium 没有 Java {major} Windows x64 JDK。");

        var package = items[0].GetProperty("binary").GetProperty("package");
        var link = RequiredString(package, "link");
        var name = RequiredString(package, "name");
        var checksum = RequiredString(package, "checksum");
        var size = package.TryGetProperty("size", out var sizeElement)
            ? sizeElement.GetInt64()
            : (long?)null;

        return new RuntimePackage(
            "Eclipse Temurin",
            [
                new("Eclipse Temurin 官方发布", new(link)),
                new(
                    "Adoptium Binary API",
                    new($"https://api.adoptium.net/v3/binary/latest/{major}/ga/windows/x64/jdk/hotspot/normal/eclipse"))
            ],
            name,
            HashKind.Sha256,
            checksum,
            size);
    }

    private async Task<RuntimePackage> ResolveAzulAsync(
        int major,
        CancellationToken cancellationToken)
    {
        var metadataUrl =
            "https://api.azul.com/metadata/v1/zulu/packages/" +
            $"?java_version={major}&os=windows&arch=x86&hw_bitness=64" +
            "&archive_type=zip&java_package_type=jdk&javafx_bundled=false" +
            "&release_status=ga&availability_types=CA&latest=true";
        var json = await _downloader.GetTextAsync(
            [new("Azul Metadata API", new(metadataUrl))],
            cancellationToken);
        using var document = JsonDocument.Parse(json);
        var items = document.RootElement.EnumerateArray().ToArray();
        if (items.Length == 0)
            throw new InvalidOperationException($"Azul 没有 Java {major} Windows x64 JDK。");

        var item = items[0];
        var link = RequiredString(item, "download_url");
        var name = RequiredString(item, "name");
        return new RuntimePackage(
            "Azul Zulu",
            [new("Azul Zulu 官方 CDN", new(link))],
            name);
    }

    private async Task<RuntimePackage> ResolveBellSoftAsync(
        int major,
        CancellationToken cancellationToken)
    {
        var metadataUrl =
            "https://api.bell-sw.com/v1/liberica/releases" +
            $"?version-feature={major}&version-modifier=latest&bitness=64" +
            "&os=windows&arch=x86&package-type=zip&bundle-type=jdk";
        var json = await _downloader.GetTextAsync(
            [new("BellSoft Discovery API", new(metadataUrl))],
            cancellationToken);
        using var document = JsonDocument.Parse(json);
        var items = document.RootElement.EnumerateArray().ToArray();
        if (items.Length == 0)
            throw new InvalidOperationException($"BellSoft 没有 Java {major} Windows x64 JDK。");

        var item = items[0];
        var link = RequiredString(item, "downloadUrl");
        var name = RequiredString(item, "filename");
        var sha1 = RequiredString(item, "sha1");
        var size = item.TryGetProperty("size", out var sizeElement)
            ? sizeElement.GetInt64()
            : (long?)null;
        return new RuntimePackage(
            "BellSoft Liberica",
            [new("BellSoft Liberica 官方发布", new(link))],
            name,
            HashKind.Sha1,
            sha1,
            size);
    }

    private async Task<RuntimePackage> ResolveCorrettoAsync(
        int major,
        CancellationToken cancellationToken)
    {
        var fileName = $"amazon-corretto-{major}-x64-windows-jdk.zip";
        var checksumUrl = $"https://corretto.aws/downloads/latest_sha256/{fileName}";
        var checksum = await _downloader.GetTextAsync(
            [new("Amazon Corretto SHA-256", new(checksumUrl))],
            cancellationToken);
        var normalized = checksum.Trim().Split(
            [' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries)[0];
        if (normalized.Length != 64 || !normalized.All(Uri.IsHexDigit))
            throw new InvalidDataException("Amazon Corretto 返回了无效的 SHA-256。");

        var downloadUrl = $"https://corretto.aws/downloads/latest/{fileName}";
        return new RuntimePackage(
            "Amazon Corretto",
            [new("Amazon Corretto 官方 CDN", new(downloadUrl))],
            fileName,
            HashKind.Sha256,
            normalized);
    }

    private static string RequiredString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"供应商元数据缺少 {property}。");
        return value.GetString()!;
    }

    public sealed record ProviderResolver(
        string Name,
        Func<CancellationToken, Task<RuntimePackage>> ResolveAsync);
}

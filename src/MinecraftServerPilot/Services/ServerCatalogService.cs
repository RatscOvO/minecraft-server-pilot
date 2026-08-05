using System.Text.Json;
using System.Xml.Linq;
using MinecraftServerPilot.Models;

namespace MinecraftServerPilot.Services;

public sealed class ServerCatalogService
{
    private readonly ResilientDownloader _downloader;
    private readonly AppLog _log;

    private static readonly DownloadCandidate[] ManifestSources =
    [
        new("Mojang 官方", new("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json")),
        new("BMCLAPI 国内镜像", new("https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json"))
    ];

    public ServerCatalogService(ResilientDownloader downloader, AppLog log)
    {
        _downloader = downloader;
        _log = log;
    }

    public async Task<IReadOnlyList<string>> GetVersionsAsync(CancellationToken cancellationToken)
    {
        var json = await _downloader.GetTextAsync(ManifestSources, cancellationToken);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("versions").EnumerateArray()
            .Select(x => x.GetProperty("id").GetString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .ToArray();
    }

    public Task<ServerPackage> ResolveAsync(
        string minecraftVersion,
        ServerKind kind,
        CancellationToken cancellationToken) => kind switch
    {
        ServerKind.Vanilla => ResolveVanillaAsync(minecraftVersion, cancellationToken),
        ServerKind.Paper => ResolvePaperAsync(minecraftVersion, cancellationToken),
        ServerKind.Fabric => ResolveFabricAsync(minecraftVersion, cancellationToken),
        ServerKind.Forge => ResolveForgeAsync(minecraftVersion, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private async Task<ServerPackage> ResolveVanillaAsync(string version, CancellationToken token)
    {
        var manifestText = await _downloader.GetTextAsync(ManifestSources, token);
        using var manifest = JsonDocument.Parse(manifestText);
        var entry = manifest.RootElement.GetProperty("versions").EnumerateArray()
            .FirstOrDefault(x => x.GetProperty("id").GetString() == version);
        if (entry.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException($"Mojang 版本清单中不存在“{version}”。请从下拉列表选择有效版本。");
        var detailUrl = entry.GetProperty("url").GetString()
            ?? throw new InvalidDataException("Mojang 版本条目缺少详情地址");
        var detailText = await _downloader.GetTextAsync(
        [
            new("Mojang 官方", new(detailUrl)),
            new("BMCLAPI 国内镜像", new($"https://bmclapi2.bangbang93.com/version/{Uri.EscapeDataString(version)}/json"))
        ], token);
        using var detail = JsonDocument.Parse(detailText);
        if (!detail.RootElement.TryGetProperty("downloads", out var downloads) ||
            !downloads.TryGetProperty("server", out var server))
            throw new InvalidOperationException(
                $"Minecraft {version} 没有 Mojang 官方服务端文件。非常早期版本和部分快照可能只发布过客户端。");

        var official = server.GetProperty("url").GetString()!;
        var sha1 = server.GetProperty("sha1").GetString();
        var size = server.TryGetProperty("size", out var sizeValue) ? sizeValue.GetInt64() : (long?)null;
        var java = ReadJavaMajor(detail.RootElement, version);
        return new ServerPackage(
            new DownloadArtifact(
            [
                new("Mojang 官方", new(official)),
                new("BMCLAPI 国内镜像", new($"https://bmclapi2.bangbang93.com/version/{Uri.EscapeDataString(version)}/server"))
            ], "server.jar", HashKind.Sha1, sha1, size),
            $"Minecraft Vanilla {version}", java, NeedsInstaller: false);
    }

    private async Task<ServerPackage> ResolvePaperAsync(string version, CancellationToken token)
    {
        var endpoint = $"https://fill.papermc.io/v3/projects/paper/versions/{Uri.EscapeDataString(version)}/builds";
        var text = await _downloader.GetTextAsync([new("PaperMC 官方", new(endpoint))], token);
        using var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"PaperMC 对 {version} 返回了无法识别的数据。");
        var builds = document.RootElement.EnumerateArray().ToArray();
        var build = builds.FirstOrDefault(x =>
            x.TryGetProperty("channel", out var channel) && channel.GetString() == "STABLE");
        if (build.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException(
                $"PaperMC 没有 Minecraft {version} 的稳定构建。可改选原版、Fabric/Forge，或选择 Paper 支持的版本。");
        var download = build.GetProperty("downloads").GetProperty("server:default");
        var url = download.GetProperty("url").GetString()!;
        var name = download.GetProperty("name").GetString() ?? $"paper-{version}.jar";
        var hash = download.GetProperty("checksums").GetProperty("sha256").GetString();
        var size = download.GetProperty("size").GetInt64();
        var java = JavaMajorByVersion(version, ServerKind.Paper);
        return new ServerPackage(
            new DownloadArtifact([new("PaperMC 官方", new(url))], "server.jar", HashKind.Sha256, hash, size),
            $"{name}（稳定版）", java, NeedsInstaller: false);
    }

    private async Task<ServerPackage> ResolveFabricAsync(string version, CancellationToken token)
    {
        var escaped = Uri.EscapeDataString(version);
        var loaderText = await _downloader.GetTextAsync(
        [
            new("Fabric 官方", new($"https://meta.fabricmc.net/v2/versions/loader/{escaped}")),
            new("BMCLAPI 国内镜像", new($"https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader/{escaped}"))
        ], token);
        using var loaderDocument = JsonDocument.Parse(loaderText);
        var loaderItems = loaderDocument.RootElement.EnumerateArray().ToArray();
        var loader = loaderItems.FirstOrDefault(x =>
            x.GetProperty("loader").TryGetProperty("stable", out var stable) && stable.GetBoolean());
        if (loader.ValueKind == JsonValueKind.Undefined)
            loader = loaderItems.FirstOrDefault();
        if (loader.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException($"Fabric Loader 不支持 Minecraft {version}。");
        var loaderVersion = loader.GetProperty("loader").GetProperty("version").GetString()!;

        var installerText = await _downloader.GetTextAsync(
        [
            new("Fabric 官方", new("https://meta.fabricmc.net/v2/versions/installer")),
            new("BMCLAPI 国内镜像", new("https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/installer"))
        ], token);
        using var installerDocument = JsonDocument.Parse(installerText);
        var installerItems = installerDocument.RootElement.EnumerateArray().ToArray();
        var installer = installerItems.FirstOrDefault(x =>
            x.TryGetProperty("stable", out var stable) && stable.GetBoolean());
        if (installer.ValueKind == JsonValueKind.Undefined)
            installer = installerItems.First();
        var installerVersion = installer.GetProperty("version").GetString()!;
        var jarPath = $"v2/versions/loader/{escaped}/{Uri.EscapeDataString(loaderVersion)}/{Uri.EscapeDataString(installerVersion)}/server/jar";
        var sources = new[]
        {
            new DownloadCandidate("Fabric 官方", new($"https://meta.fabricmc.net/{jarPath}")),
            new DownloadCandidate("BMCLAPI 国内镜像", new($"https://bmclapi2.bangbang93.com/fabric-meta/{jarPath}"))
        };
        return new ServerPackage(new DownloadArtifact(sources, "server.jar"),
            $"Fabric Loader {loaderVersion}", JavaMajorByVersion(version, ServerKind.Fabric),
            NeedsInstaller: false, loaderVersion);
    }

    private async Task<ServerPackage> ResolveForgeAsync(string version, CancellationToken token)
    {
        var metadata = await _downloader.GetTextAsync(
        [
            new("Forge 官方 Maven", new("https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml")),
            new("BMCLAPI 国内镜像", new("https://bmclapi2.bangbang93.com/maven/net/minecraftforge/forge/maven-metadata.xml"))
        ], token);
        var document = XDocument.Parse(metadata);
        var forgeVersion = document.Descendants("version")
            .Select(x => x.Value)
            .Where(x => x.StartsWith(version + "-", StringComparison.Ordinal))
            .OrderBy(x => ParseForgeRelease(x, version))
            .LastOrDefault();
        if (forgeVersion is null)
            throw new InvalidOperationException(
                $"Forge Maven 中找不到 Minecraft {version}。Forge 并非支持每个游戏版本，可尝试 Fabric 或原版。");
        var relative = $"net/minecraftforge/forge/{forgeVersion}/forge-{forgeVersion}-installer.jar";
        return new ServerPackage(
            new DownloadArtifact(
            [
                new("Forge 官方 Maven", new($"https://maven.minecraftforge.net/{relative}")),
                new("BMCLAPI 国内镜像", new($"https://bmclapi2.bangbang93.com/maven/{relative}"))
            ], "forge-installer.jar"),
            $"Minecraft Forge {forgeVersion}", JavaMajorByVersion(version, ServerKind.Forge),
            NeedsInstaller: true, forgeVersion);
    }

    public static Version ParseForgeRelease(string forgeCoordinateVersion, string minecraftVersion)
    {
        var release = forgeCoordinateVersion[(minecraftVersion.Length + 1)..].Split('-')[0];
        return Version.TryParse(release, out var parsed) ? parsed : new Version(0, 0);
    }

    private static int ReadJavaMajor(JsonElement detail, string version)
    {
        if (detail.TryGetProperty("javaVersion", out var java) &&
            java.TryGetProperty("majorVersion", out var major))
            return major.GetInt32();
        return JavaMajorByVersion(version, ServerKind.Vanilla);
    }

    public static int JavaMajorByVersion(string version, ServerKind kind)
    {
        var parts = version.Split('.', '-', '_');
        if (int.TryParse(parts.ElementAtOrDefault(0), out var first) && first >= 26)
            return 25;
        var minor = int.TryParse(parts.ElementAtOrDefault(1), out var parsed) ? parsed : 0;
        var patch = int.TryParse(parts.ElementAtOrDefault(2), out var parsedPatch) ? parsedPatch : 0;
        if (minor <= 16)
        {
            if (kind == ServerKind.Paper && minor >= 12)
                return minor == 16 && patch >= 5 ? 16 : 11;
            return 8;
        }
        if (minor == 17)
            return kind == ServerKind.Vanilla ? 16 : 17;
        if (minor <= 20 && !(minor == 20 && patch >= 5))
            return 17;
        return 21;
    }

    public static string DescribeCompatibility(string version, ServerKind kind)
    {
        if (string.IsNullOrWhiteSpace(version))
            return "输入版本后，程序会在下载前解析上游元数据并给出精确结论。";
        var java = JavaMajorByVersion(version, kind);
        var parts = version.Split('.', '-', '_');
        var minor = int.TryParse(parts.ElementAtOrDefault(1), out var parsedMinor)
            ? parsedMinor
            : -1;
        return kind switch
        {
            ServerKind.Vanilla =>
                $"预计需要 Java {java}。原版覆盖最广；最终以 Mojang 版本详情是否提供 server 下载及其 SHA-1 为准。",
            ServerKind.Paper when minor is >= 0 and < 8 =>
                $"高风险不兼容：Paper 没有为 Minecraft {version} 发布官方稳定构建。建议改选原版或 Forge；程序会在下载前停止并说明。",
            ServerKind.Paper =>
                $"预计需要 Java {java}。支持 Bukkit/Paper 插件，不支持 Forge/Fabric 模组；只使用 PaperMC 标记为 STABLE 的构建。",
            ServerKind.Fabric when minor is >= 0 and < 14 =>
                $"高风险不兼容：官方 Fabric 主要覆盖 1.14 及以后版本，{version} 通常需要其他加载器。程序会查询 Fabric Meta 后再决定。",
            ServerKind.Fabric =>
                $"预计需要 Java {java}。将选择稳定 Fabric Loader；测试 spark 时会同时解析并安装 Fabric API 前置，验证后全部清理。",
            ServerKind.Forge =>
                $"预计需要 Java {java}。Forge 对游戏小版本和 Java 边界非常严格；程序按官方 Maven 的数字版本选择最新构建，并兼容新旧安装器。",
            _ => "程序会在下载前执行兼容性预检。"
        };
    }

    public static bool IsStableReleaseId(string value)
    {
        var parts = value.Split('.');
        return parts.Length is 2 or 3 &&
               parts.All(part => part.Length > 0 && part.All(char.IsDigit));
    }
}

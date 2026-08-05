using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MinecraftServerPilot.Models;
using MinecraftServerPilot.Services;

var passed = 0;
Check(ServerCatalogService.JavaMajorByVersion("1.7.10", ServerKind.Vanilla) == 8,
    "1.7.10 Vanilla -> Java 8");
Check(ServerCatalogService.JavaMajorByVersion("1.17.1", ServerKind.Vanilla) == 16,
    "1.17.1 Vanilla -> Java 16");
Check(ServerCatalogService.JavaMajorByVersion("1.20.4", ServerKind.Paper) == 17,
    "1.20.4 Paper -> Java 17");
Check(ServerCatalogService.JavaMajorByVersion("1.20.5", ServerKind.Paper) == 21,
    "1.20.5 Paper -> Java 21");
Check(ServerCatalogService.JavaMajorByVersion("26.1", ServerKind.Paper) == 25,
    "26.1 Paper -> Java 25");
Check(ErrorAdvisor.Analyze("java.lang.UnsupportedClassVersionError", 1).Contains("Java 主版本"),
    "Java mismatch diagnostic");
Check(ErrorAdvisor.Analyze("Failed to bind to port", 1).Contains("端口"),
    "Port diagnostic");
Check(ServerCatalogService.DescribeCompatibility("1.7.10", ServerKind.Paper)
        .Contains("高风险不兼容"),
    "Compatibility preflight warns before unsupported legacy Paper selection");
Check(ServerCatalogService.DescribeCompatibility("1.20", ServerKind.Fabric)
        .Contains("Fabric API"),
    "Compatibility preflight explains Fabric dependency probe");
Check(ServerCatalogService.IsStableReleaseId("26.1.2") &&
      !ServerCatalogService.IsStableReleaseId("26.3-snapshot-3"),
    "Default-version filter selects a numeric release instead of a snapshot");
var forgeVersions = new[]
{
    "1.7.10-10.13.4.1614-1.7.10", "1.7.10-10.13.0.1150", "1.7.10-10.13.4.1558-1.7.10"
};
Check(forgeVersions.OrderBy(x => ServerCatalogService.ParseForgeRelease(x, "1.7.10")).Last()
        == "1.7.10-10.13.4.1614-1.7.10",
    "Forge versions are numerically sorted instead of relying on Maven XML order");
TestLegacyServerPropertiesRoundTrip();
TestModernServerPropertiesCompatibility();
TestServerPropertiesConcurrentChangeGuard();
await TestDownloaderFallbackAsync();
await TestDownloaderStallRecoveryAsync();
await TestDownloaderCancellationAsync();
await TestManualJavaPolicyAsync();
if (args.Contains("--integration", StringComparer.OrdinalIgnoreCase))
    await TestVanilla1710IntegrationAsync();
if (args.Contains("--providers", StringComparer.OrdinalIgnoreCase))
    await TestProviderMetadataAsync();
if (args.Contains("--session-resume", StringComparer.OrdinalIgnoreCase))
    await TestFailedSessionResumeAsync();
if (args.Contains("--guided-cancel", StringComparer.OrdinalIgnoreCase))
    await TestGuidedCheckpointCancellationAsync();
if (args.Contains("--recovery-faults", StringComparer.OrdinalIgnoreCase))
    await TestEulaAndPortRecoveryAsync();
if (args.Contains("--recovery-memory", StringComparer.OrdinalIgnoreCase))
    await TestMemoryRecoveryWithJavaShimAsync();
if (args.Contains("--integration-paper", StringComparer.OrdinalIgnoreCase))
    await TestDistributionIntegrationAsync("1.21.11", ServerKind.Paper, 1024, 2048,
        requireProbePass: false);
if (args.Contains("--integration-fabric", StringComparer.OrdinalIgnoreCase))
    await TestDistributionIntegrationAsync("1.20", ServerKind.Fabric, 1024, 1536,
        requireProbePass: true);
if (args.Contains("--integration-forge", StringComparer.OrdinalIgnoreCase))
    await TestDistributionIntegrationAsync("1.7.10", ServerKind.Forge, 768, 1280,
        requireProbePass: false);
if (args.Contains("--integration-forge-modern", StringComparer.OrdinalIgnoreCase))
    await TestDistributionIntegrationAsync("1.20", ServerKind.Forge, 1024, 2048,
        requireProbePass: true);
if (args.Contains("--integration-java-bootstrap", StringComparer.OrdinalIgnoreCase))
    await TestDistributionIntegrationAsync("1.17.1", ServerKind.Vanilla, 768, 1280,
        requireProbePass: false, requireManagedJava: true);

Console.WriteLine($"PASS: {passed} checks");
return;

void Check(bool condition, string name)
{
    if (!condition)
        throw new Exception($"FAIL: {name}");
    passed++;
    Console.WriteLine($"PASS: {name}");
}

void TestLegacyServerPropertiesRoundTrip()
{
    var temp = Path.Combine(Path.GetTempPath(),
        "MinecraftServerPilotLegacyPropertiesTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temp);
    try
    {
        var path = Path.Combine(temp, "server.properties");
        File.WriteAllText(path,
            "# legacy server\r\n" +
            "online-mode=true\r\n" +
            "difficulty=1\r\n" +
            "gamemode=0\r\n" +
            "pvp=true\r\n" +
            "max-players=20\r\n" +
            "server-port=25565\r\n" +
            "motd=Old server\r\n" +
            "custom-plugin-value=keep-me\r\n",
            new UTF8Encoding(false));
        using var log = new AppLog(temp);
        var service = new ServerPropertiesService(log);
        var snapshot = service.Load(temp, "1.7.10");
        var values = snapshot.Values.ToDictionary(
            value => value.Definition.Key,
            value => value.Value,
            StringComparer.OrdinalIgnoreCase);
        values["online-mode"] = "false";
        values["difficulty"] = "3";
        values["gamemode"] = "1";
        values["max-players"] = "42";
        values["motd"] = "你好，Minecraft";
        service.Save(snapshot, values);

        var saved = File.ReadAllText(path, Encoding.UTF8);
        var reloaded = service.Load(temp, "1.7.10");
        Check(snapshot.Values.Any(value => value.Definition.Key == "pvp") &&
              snapshot.Values.First(value => value.Definition.Key == "difficulty")
                  .Definition.Choices?.Any(choice => choice.Value == "3") == true &&
              !snapshot.Values.Any(value =>
                  value.Definition.Key == "simulation-distance"),
            "Legacy properties expose legacy keys and numeric version-compatible choices");
        Check(saved.Contains("# legacy server") &&
              saved.Contains("custom-plugin-value=keep-me") &&
              saved.Contains("difficulty=3") &&
              saved.Contains("gamemode=1") &&
              !saved.Contains("simulation-distance=") &&
              File.Exists(path + ".pilot-backup"),
            "Properties save preserves comments and unknown fields while creating a backup");
        var savedMotd = saved.Split(['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries)
            .First(line => line.StartsWith("motd=", StringComparison.Ordinal));
        var reloadedMotd =
            reloaded.Values.First(value => value.Definition.Key == "motd").Value;
        Check(savedMotd == @"motd=\u4F60\u597D\uFF0CMinecraft" &&
              reloadedMotd == "你好，Minecraft",
            "Properties safely round-trip Chinese text through Java Unicode escapes");
    }
    finally
    {
        Directory.Delete(temp, recursive: true);
    }
}

void TestModernServerPropertiesCompatibility()
{
    var temp = Path.Combine(Path.GetTempPath(),
        "MinecraftServerPilotModernPropertiesTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temp);
    try
    {
        File.WriteAllText(Path.Combine(temp, "server.properties"),
            "allow-flight=false\r\n" +
            "difficulty=easy\r\n" +
            "gamemode=survival\r\n" +
            "max-players=20\r\n" +
            "motd=Modern server\r\n" +
            "online-mode=true\r\n" +
            "server-port=25565\r\n" +
            "simulation-distance=10\r\n" +
            "spawn-protection=16\r\n" +
            "view-distance=10\r\n" +
            "white-list=false\r\n" +
            "future-setting=preserve\r\n",
            new UTF8Encoding(false));
        using var log = new AppLog(temp);
        var service = new ServerPropertiesService(log);
        var snapshot = service.Load(temp, "26.2");
        var difficulty = snapshot.Values.First(value =>
            value.Definition.Key == "difficulty");
        Check(snapshot.Values.All(value => value.Definition.Key != "pvp") &&
              snapshot.UnavailableKnownSettings.Any(value => value.Contains("pvp")) &&
              difficulty.Definition.Choices?.Any(choice =>
                  choice.Value == "hard" && choice.Label == "困难") == true &&
              snapshot.Values.Any(value =>
                  value.Definition.Key == "simulation-distance"),
            "Modern properties hide absent legacy keys and use named version-compatible choices");
    }
    finally
    {
        Directory.Delete(temp, recursive: true);
    }
}

void TestServerPropertiesConcurrentChangeGuard()
{
    var temp = Path.Combine(Path.GetTempPath(),
        "MinecraftServerPilotPropertiesGuardTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temp);
    try
    {
        var path = Path.Combine(temp, "server.properties");
        File.WriteAllText(path, "online-mode=true\r\nserver-port=25565\r\n",
            new UTF8Encoding(false));
        using var log = new AppLog(temp);
        var service = new ServerPropertiesService(log);
        var snapshot = service.Load(temp, "1.20.1");
        var externallyChanged = File.ReadAllText(path, Encoding.UTF8)
            .Replace("server-port=25565", "server-port=25566",
                StringComparison.Ordinal);
        File.WriteAllText(path, externallyChanged, new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, snapshot.LastWriteTimeUtc);
        var values = snapshot.Values.ToDictionary(
            value => value.Definition.Key,
            value => value.Value,
            StringComparer.OrdinalIgnoreCase);
        var guarded = false;
        try
        {
            service.Save(snapshot, values);
        }
        catch (IOException)
        {
            guarded = true;
        }
        Check(guarded,
            "Properties editor detects same-size concurrent changes by content hash");
    }
    finally
    {
        Directory.Delete(temp, recursive: true);
    }
}

async Task TestDownloaderFallbackAsync()
{
    var good = Encoding.UTF8.GetBytes("verified minecraft server pilot payload");
    var bad = Encoding.UTF8.GetBytes("corrupt payload");
    var expected = Convert.ToHexString(SHA256.HashData(good));
    var listener = new HttpListener();
    var port = GetFreePort();
    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    listener.Start();
    using var serverCancellation = new CancellationTokenSource();
    var server = Task.Run(async () =>
    {
        while (!serverCancellation.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch { break; }
            var payload = context.Request.Url?.AbsolutePath == "/good" ? good : bad;
            context.Response.ContentLength64 = payload.Length;
            await context.Response.OutputStream.WriteAsync(payload);
            context.Response.Close();
        }
    });

    var temp = Path.Combine(Path.GetTempPath(), "MinecraftServerPilotTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temp);
    try
    {
        using (var log = new AppLog(temp))
        using (var downloader = new ResilientDownloader(log))
        {
            var result = await downloader.DownloadAsync(
                new DownloadArtifact(
                [
                    new("corrupt source", new($"http://127.0.0.1:{port}/bad")),
                    new("healthy fallback", new($"http://127.0.0.1:{port}/good"))
                ], "artifact.jar", HashKind.Sha256, expected, good.Length),
                temp, null, CancellationToken.None);
            Check(File.ReadAllBytes(result).SequenceEqual(good),
                "Downloader rejects corrupt source and switches to healthy source");
        }
    }
    finally
    {
        serverCancellation.Cancel();
        listener.Stop();
        await server;
        Directory.Delete(temp, recursive: true);
    }
}

async Task TestDownloaderStallRecoveryAsync()
{
    var payload = RandomNumberGenerator.GetBytes(512 * 1024);
    var expected = Convert.ToHexString(SHA256.HashData(payload));
    var listener = new HttpListener();
    var port = GetFreePort();
    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    listener.Start();
    using var serverCancellation = new CancellationTokenSource();
    var requestCount = 0;
    var resumedFrom = -1L;
    var server = Task.Run(async () =>
    {
        while (!serverCancellation.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch { break; }

            var currentRequest = Interlocked.Increment(ref requestCount);
            try
            {
                if (currentRequest == 1)
                {
                    const int prefixLength = 64 * 1024;
                    context.Response.ContentLength64 = payload.Length;
                    await context.Response.OutputStream.WriteAsync(
                        payload.AsMemory(0, prefixLength));
                    await context.Response.OutputStream.FlushAsync();
                    await Task.Delay(900);
                    context.Response.Close();
                    continue;
                }

                var range = context.Request.Headers["Range"];
                var rangeText = range?["bytes=".Length..].TrimEnd('-');
                resumedFrom = long.TryParse(rangeText, out var parsed) ? parsed : 0;
                context.Response.StatusCode = resumedFrom > 0
                    ? (int)HttpStatusCode.PartialContent
                    : (int)HttpStatusCode.OK;
                if (resumedFrom > 0)
                {
                    context.Response.AddHeader("Content-Range",
                        $"bytes {resumedFrom}-{payload.Length - 1}/{payload.Length}");
                }
                context.Response.ContentLength64 = payload.Length - resumedFrom;
                await context.Response.OutputStream.WriteAsync(
                    payload.AsMemory((int)resumedFrom));
                context.Response.Close();
            }
            catch
            {
                try { context.Response.Abort(); } catch { }
            }
        }
    });

    var temp = Path.Combine(Path.GetTempPath(),
        "MinecraftServerPilotStallTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temp);
    try
    {
        var updates = new List<OperationProgress>();
        using (var log = new AppLog(temp))
        using (var downloader = new ResilientDownloader(
                   log, TimeSpan.FromMilliseconds(250)))
        {
            var result = await downloader.DownloadAsync(
                new DownloadArtifact(
                    [new("stalling source", new($"http://127.0.0.1:{port}/artifact"))],
                    "artifact.jar", HashKind.Sha256, expected, payload.Length),
                temp, new InlineProgress<OperationProgress>(updates.Add),
                CancellationToken.None);
            Check(File.ReadAllBytes(result).SequenceEqual(payload) &&
                  requestCount >= 2 &&
                  resumedFrom == 64 * 1024 &&
                  updates.Any(update => update.IsWarning &&
                                        update.Message.Contains("自动重连")),
                "Downloader detects a stalled body read and resumes the partial file");
        }
    }
    finally
    {
        serverCancellation.Cancel();
        listener.Stop();
        await server;
        Directory.Delete(temp, recursive: true);
    }
}

async Task TestDownloaderCancellationAsync()
{
    var payload = RandomNumberGenerator.GetBytes(256 * 1024);
    var listener = new HttpListener();
    var port = GetFreePort();
    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    listener.Start();
    using var serverCancellation = new CancellationTokenSource();
    var server = Task.Run(async () =>
    {
        HttpListenerContext? context = null;
        try
        {
            context = await listener.GetContextAsync();
            context.Response.ContentLength64 = payload.Length;
            await context.Response.OutputStream.WriteAsync(payload.AsMemory(0, 32 * 1024));
            await context.Response.OutputStream.FlushAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, serverCancellation.Token);
        }
        catch
        {
            // The test deliberately cancels while the response body is stalled.
        }
        finally
        {
            try { context?.Response.Abort(); } catch { }
        }
    });

    var temp = Path.Combine(Path.GetTempPath(),
        "MinecraftServerPilotCancelTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temp);
    try
    {
        using var log = new AppLog(temp);
        using var downloader = new ResilientDownloader(log, TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var startedAt = DateTime.UtcNow;
        var wasCanceled = false;
        try
        {
            await downloader.DownloadAsync(
                new DownloadArtifact(
                    [new("cancel test source", new($"http://127.0.0.1:{port}/artifact"))],
                    "artifact.jar", ExpectedSize: payload.Length),
                temp, null, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            wasCanceled = true;
        }

        var partial = Path.Combine(temp, "artifact.jar.part");
        Check(wasCanceled &&
              DateTime.UtcNow - startedAt < TimeSpan.FromSeconds(2) &&
              File.Exists(partial) &&
              new FileInfo(partial).Length == 32 * 1024,
            "User cancellation interrupts a stalled read promptly and preserves the partial file");
    }
    finally
    {
        serverCancellation.Cancel();
        listener.Stop();
        await server;
        Directory.Delete(temp, recursive: true);
    }
}

async Task TestManualJavaPolicyAsync()
{
    var temp = Path.Combine(Path.GetTempPath(), "MinecraftServerPilotManualJavaTests",
        Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temp);
    try
    {
        using var log = new AppLog(temp);
        using var downloader = new ResilientDownloader(log);
        var java = new JavaRuntimeService(downloader, log);
        try
        {
            await java.EnsureAsync(999, Path.Combine(temp, "tools"), allowDownload: false,
                progress: null, CancellationToken.None);
            throw new Exception("Expected manual Java policy to pause.");
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("只使用已安装 Java") && ex.Message.Contains("手动安装"))
        {
            Check(!Directory.Exists(Path.Combine(temp, "tools", "downloads")),
                "Manual Java policy pauses with instructions before any runtime download");
        }
    }
    finally
    {
        if (Directory.Exists(temp))
            Directory.Delete(temp, recursive: true);
    }
}

int GetFreePort()
{
    var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
    socket.Start();
    var port = ((System.Net.IPEndPoint)socket.LocalEndpoint).Port;
    socket.Stop();
    return port;
}

async Task TestVanilla1710IntegrationAsync()
{
    var parent = Path.Combine(Path.GetTempPath(), "MinecraftServerPilotIntegration",
        Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(parent);
    try
    {
        using var coordinator = new InstallerCoordinator();
        var progress = new Progress<OperationProgress>(p =>
            Console.WriteLine($"INTEGRATION [{p.Stage}] {p.Message}"));
        var result = await coordinator.InstallAsync(
            new InstallRequest("1.7.10", ServerKind.Vanilla, parent, InstallMode.Automatic,
                512, 768, RunCompatibilityProbe: false, KeepServerRunning: false),
            progress, CancellationToken.None);
        Check(File.ReadAllText(Path.Combine(result.ServerDirectory, "eula.txt")).Contains("eula=true"),
            "1.7.10 integration writes accepted EULA");
        Check(File.Exists(Path.Combine(result.ServerDirectory, "server-pilot.json")) &&
              File.Exists(Path.Combine(result.ServerDirectory, "Start-Server.cmd")),
            "1.7.10 integration produces portable delivery files");
        Check(Directory.Exists(Path.Combine(result.ServerDirectory, "world")),
            "1.7.10 integration completed clean second boot and generated a world");
        var session = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(result.ServerDirectory, ".pilot-session.json")));
        Check(session.RootElement.GetProperty("Status").GetString() == "Completed" &&
              session.RootElement.GetProperty("Stage").GetString() == "Completed",
            "Integration session is atomically marked completed");
        var existing = coordinator.LoadExistingServer(result.ServerDirectory);
        var updated = coordinator.UpdateExistingServer(existing, 640, 1024);
        Check(updated.MinimumMemoryMb == 640 && updated.MaximumMemoryMb == 1024 &&
              File.ReadAllText(Path.Combine(result.ServerDirectory, "Start-Server.cmd"))
                  .Contains("-Xms640M") &&
              File.ReadAllText(Path.Combine(result.ServerDirectory, "Start-Server.cmd"))
                  .Contains("-Xmx1024M"),
            "Existing-server manager atomically updates portable memory and start command");
    }
    finally
    {
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MinecraftServerPilotIntegration"))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(parent);
        if (!target.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing unsafe integration cleanup: {target}");
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
    }
}

async Task TestProviderMetadataAsync()
{
    var temp = Path.Combine(Path.GetTempPath(), "MinecraftServerPilotProviderTests",
        Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temp);
    try
    {
        using var log = new AppLog(temp);
        using var downloader = new ResilientDownloader(log);
        var catalog = new ServerCatalogService(downloader, log);
        var vanilla = await catalog.ResolveAsync("1.7.10", ServerKind.Vanilla, CancellationToken.None);
        Check(vanilla.RequiredJavaMajor == 8 &&
              vanilla.Artifact.ExpectedHash == "952438ac4e01b4d115c5fc38f891710c4941df29",
            "Live Mojang metadata resolves 1.7.10 server and SHA-1");
        var paper = await catalog.ResolveAsync("1.21.11", ServerKind.Paper, CancellationToken.None);
        Check(paper.RequiredJavaMajor == 21 && paper.Artifact.HashKind == HashKind.Sha256,
            "Live PaperMC v3 metadata resolves a stable 1.21.11 build");
        var fabric = await catalog.ResolveAsync("1.20", ServerKind.Fabric, CancellationToken.None);
        Check(!string.IsNullOrWhiteSpace(fabric.LoaderVersion) && fabric.Artifact.Sources.Count >= 2,
            "Live Fabric metadata resolves stable loader with official and mirror sources");
        var forge = await catalog.ResolveAsync("1.7.10", ServerKind.Forge, CancellationToken.None);
        Check(forge.LoaderVersion?.Contains("10.13.4.1614") == true && forge.NeedsInstaller,
            "Live Forge Maven metadata resolves 1.7.10 recommended numeric-latest installer");
    }
    finally
    {
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MinecraftServerPilotProviderTests"))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(temp);
        if (!target.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing unsafe provider-test cleanup: {target}");
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
    }
}

async Task TestDistributionIntegrationAsync(
    string minecraftVersion,
    ServerKind kind,
    int minimumMemoryMb,
    int maximumMemoryMb,
    bool requireProbePass,
    bool requireManagedJava = false)
{
    var parent = Path.Combine(Path.GetTempPath(), "MinecraftServerPilotDistributionTests",
        $"{kind}-{minecraftVersion}-{Guid.NewGuid():N}");
    Directory.CreateDirectory(parent);
    try
    {
        using var coordinator = new InstallerCoordinator();
        var progress = new Progress<OperationProgress>(p =>
            Console.WriteLine($"{kind.ToString().ToUpperInvariant()} [{p.Stage}] {p.Message}"));
        var result = await coordinator.InstallAsync(
            new InstallRequest(minecraftVersion, kind, parent, InstallMode.Automatic,
                minimumMemoryMb, maximumMemoryMb, RunCompatibilityProbe: true,
                KeepServerRunning: false),
            progress, CancellationToken.None);
        Check(File.Exists(Path.Combine(result.ServerDirectory, "server.jar")) ||
              kind == ServerKind.Forge,
            $"{kind} integration installs a launchable distribution");
        Check(Directory.Exists(Path.Combine(result.ServerDirectory, "world")),
            $"{kind} integration reaches clean second boot");
        if (requireManagedJava)
        {
            Check(File.Exists(result.JavaExe) &&
                  Path.GetFullPath(result.JavaExe).StartsWith(
                      Path.GetFullPath(result.ServerDirectory) + Path.DirectorySeparatorChar,
                      StringComparison.OrdinalIgnoreCase),
                $"Missing Java is downloaded as a portable runtime inside the server directory");
        }
        var testDirectory = kind == ServerKind.Paper ? "plugins" : "mods";
        var leftovers = Directory.Exists(Path.Combine(result.ServerDirectory, testDirectory))
            ? Directory.EnumerateFiles(Path.Combine(result.ServerDirectory, testDirectory), "*.jar",
                SearchOption.TopDirectoryOnly).Any()
            : false;
        Check(!leftovers, $"{kind} integration removes one-time probe and dependency JARs");
        var session = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(result.ServerDirectory, ".pilot-session.json")));
        Check(session.RootElement.GetProperty("Status").GetString() == "Completed",
            $"{kind} integration marks session completed");
        var probeStatus = session.RootElement.GetProperty("CompatibilityProbeStatus").GetString();
        if (requireProbePass)
        {
            Check(probeStatus == "Passed",
                $"{kind} one-time test mod and required dependencies load successfully");
        }
        else
        {
            Check(probeStatus is "Passed" or "LoaderOnly",
                $"{kind} reports an honest probe outcome instead of a false-positive mod claim");
        }
    }
    finally
    {
        var allowed = Path.GetFullPath(
                Path.Combine(Path.GetTempPath(), "MinecraftServerPilotDistributionTests"))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(parent);
        if (!target.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing unsafe distribution cleanup: {target}");
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
    }
}

async Task TestFailedSessionResumeAsync()
{
    var parent = Path.Combine(Path.GetTempPath(), "MinecraftServerPilotSessionTests",
        Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(parent);
    try
    {
        var request = new InstallRequest("definitely-not-a-real-version", ServerKind.Vanilla,
            parent, InstallMode.Automatic, 512, 768, false, false);
        async Task RunExpectedFailureAsync(List<OperationProgress> updates)
        {
            using var coordinator = new InstallerCoordinator();
            try
            {
                await coordinator.InstallAsync(request,
                    new InlineProgress<OperationProgress>(updates.Add), CancellationToken.None);
                throw new Exception("Expected invalid version installation to fail.");
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("不存在") || ex.InnerException?.Message.Contains("不存在") == true)
            {
                // Expected metadata rejection.
            }
        }

        var firstUpdates = new List<OperationProgress>();
        await RunExpectedFailureAsync(firstUpdates);
        var directoriesAfterFirst = Directory.GetDirectories(parent);
        Check(directoriesAfterFirst.Length == 1,
            "Failed installation creates one recoverable working directory");
        var sessionPath = Path.Combine(directoriesAfterFirst[0], ".pilot-session.json");
        var firstSession = JsonDocument.Parse(File.ReadAllText(sessionPath));
        var createdAt = firstSession.RootElement.GetProperty("CreatedAt").GetDateTimeOffset();
        Check(firstSession.RootElement.GetProperty("Status").GetString() == "Failed",
            "Failed installation persists full session status");

        var secondUpdates = new List<OperationProgress>();
        await RunExpectedFailureAsync(secondUpdates);
        var directoriesAfterSecond = Directory.GetDirectories(parent);
        var secondSession = JsonDocument.Parse(File.ReadAllText(sessionPath));
        Check(directoriesAfterSecond.Length == 1 &&
              secondSession.RootElement.GetProperty("CreatedAt").GetDateTimeOffset() == createdAt,
            "Retry resumes the same failed session instead of creating another directory");
        Check(secondUpdates.Any(update => update.Stage == "恢复"),
            "Resumed session is explicitly disclosed to the user");
    }
    finally
    {
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MinecraftServerPilotSessionTests"))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(parent);
        if (!target.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing unsafe session-test cleanup: {target}");
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
    }
}

async Task TestGuidedCheckpointCancellationAsync()
{
    var parent = Path.Combine(Path.GetTempPath(), "MinecraftServerPilotGuidedTests",
        Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(parent);
    try
    {
        using var coordinator = new InstallerCoordinator();
        var checkpoints = new List<InstallCheckpoint>();
        try
        {
            await coordinator.InstallAsync(
                new InstallRequest("1.7.10", ServerKind.Vanilla, parent, InstallMode.Guided,
                    512, 768, false, false),
                new InlineProgress<OperationProgress>(_ => { }),
                CancellationToken.None,
                (checkpoint, _) =>
                {
                    checkpoints.Add(checkpoint);
                    return Task.FromResult(false);
                });
            throw new Exception("Expected guided checkpoint cancellation.");
        }
        catch (OperationCanceledException)
        {
            // Expected and safe.
        }
        Check(checkpoints.Count == 1 && checkpoints[0].Stage == "发行版与 Java",
            "Guided mode pauses at the first concrete post-analysis checkpoint");
        var directory = Directory.GetDirectories(parent).Single();
        var session = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(directory, ".pilot-session.json")));
        Check(session.RootElement.GetProperty("Status").GetString() == "Cancelled",
            "Guided cancellation persists a resumable cancelled session");
        Check(!File.Exists(Path.Combine(directory, "server.jar")),
            "Guided cancellation before download causes no server artifact transfer");
    }
    finally
    {
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MinecraftServerPilotGuidedTests"))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(parent);
        if (!target.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing unsafe guided-test cleanup: {target}");
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
    }
}

async Task TestEulaAndPortRecoveryAsync()
{
    var parent = Path.Combine(Path.GetTempPath(), "MinecraftServerPilotRecoveryTests",
        Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(parent);
    System.Net.Sockets.TcpListener? competingListener = null;
    try
    {
        var injected = false;
        using var coordinator = new InstallerCoordinator();
        var progress = new InlineProgress<OperationProgress>(update =>
        {
            if (injected || update.Stage != "启动验证" ||
                !update.Message.StartsWith("第 1 次启动", StringComparison.Ordinal))
                return;
            var directory = Path.Combine(parent, "Minecraft-1.7.10-Vanilla-Server");
            File.WriteAllText(Path.Combine(directory, "eula.txt"), "eula=false\r\n",
                new UTF8Encoding(false));
            competingListener = new System.Net.Sockets.TcpListener(IPAddress.Any, 25565);
            competingListener.Start();
            injected = true;
        });
        var result = await coordinator.InstallAsync(
            new InstallRequest("1.7.10", ServerKind.Vanilla, parent, InstallMode.Automatic,
                512, 768, false, false),
            progress, CancellationToken.None);
        var session = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(result.ServerDirectory, ".pilot-session.json")));
        var recoveries = session.RootElement.GetProperty("Recoveries").EnumerateArray()
            .Select(item => item.GetString() ?? "")
            .ToArray();
        Check(injected && recoveries.Any(item => item.Contains("EULA")),
            "Startup recovery repairs an EULA changed after preflight");
        Check(recoveries.Any(item => item.Contains("端口 25565")) &&
              File.ReadAllText(Path.Combine(result.ServerDirectory, "server.properties"))
                  .Contains("server-port=25566"),
            "Startup recovery escapes a port stolen after preflight and persists the new port");
        Check(File.Exists(Path.Combine(result.ServerDirectory, "pilot-recovery-report.txt")),
            "Every automatic startup repair writes a recovery report with failed output");
    }
    finally
    {
        competingListener?.Stop();
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MinecraftServerPilotRecoveryTests"))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(parent);
        if (!target.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing unsafe recovery-test cleanup: {target}");
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
    }
}

async Task TestMemoryRecoveryWithJavaShimAsync()
{
    var parent = Path.Combine(Path.GetTempPath(), "MinecraftServerPilotMemoryRecoveryTests",
        Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(parent);
    var previousRealJava = Environment.GetEnvironmentVariable("PILOT_TEST_REAL_JAVA");
    try
    {
        var serverDirectory = Path.Combine(parent, "Minecraft-1.7.10-Vanilla-Server");
        var fakeJavaDirectory = Path.Combine(serverDirectory, ".pilot-tools", "java", "8", "bin");
        Directory.CreateDirectory(fakeJavaDirectory);
        var shimOutput = Path.Combine(Environment.CurrentDirectory, "tests", "FakeJavaShim",
            "bin", "Release", "net8.0");
        if (!Directory.Exists(shimOutput))
            throw new DirectoryNotFoundException($"Build FakeJavaShim first: {shimOutput}");
        foreach (var source in Directory.EnumerateFiles(shimOutput, "*", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(source);
            if (fileName.Equals("FakeJavaShim.exe", StringComparison.OrdinalIgnoreCase))
                fileName = "java.exe";
            File.Copy(source, Path.Combine(fakeJavaDirectory, fileName), overwrite: true);
        }

        var realJava = Directory.EnumerateFiles(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"),
                "java.exe", SearchOption.AllDirectories)
            .FirstOrDefault(path => path.Contains("1.8", StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("Java 8 is required for the memory recovery integration test.");
        Environment.SetEnvironmentVariable("PILOT_TEST_REAL_JAVA", realJava);

        var session = new InstallSessionState
        {
            MinecraftVersion = "1.7.10",
            ServerKind = ServerKind.Vanilla,
            MinimumMemoryMb = 1024,
            MaximumMemoryMb = 4096,
            Status = "Failed",
            Stage = "Created"
        };
        File.WriteAllText(Path.Combine(serverDirectory, ".pilot-session.json"),
            JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        using var coordinator = new InstallerCoordinator();
        var result = await coordinator.InstallAsync(
            new InstallRequest("1.7.10", ServerKind.Vanilla, parent, InstallMode.Automatic,
                1024, 4096, false, false),
            new InlineProgress<OperationProgress>(_ => { }), CancellationToken.None);
        var finalSession = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(result.ServerDirectory, ".pilot-session.json")));
        var recoveries = finalSession.RootElement.GetProperty("Recoveries").EnumerateArray()
            .Select(item => item.GetString() ?? "")
            .ToArray();
        Check(recoveries.Any(item => item.Contains("JVM 内存分配失败")),
            "Startup recovery detects a real shimmed JVM heap reservation failure");
        var portable = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(result.ServerDirectory, "server-pilot.json")));
        Check(portable.RootElement.GetProperty("minimumMemoryMb").GetInt32() == 512 &&
              portable.RootElement.GetProperty("maximumMemoryMb").GetInt32() == 2048 &&
              File.ReadAllText(Path.Combine(result.ServerDirectory, "Start-Server.cmd"))
                  .Contains("-Xmx2048M"),
            "Memory recovery persists reduced limits in JSON and regenerated start command");
    }
    finally
    {
        Environment.SetEnvironmentVariable("PILOT_TEST_REAL_JAVA", previousRealJava);
        var allowed = Path.GetFullPath(
                Path.Combine(Path.GetTempPath(), "MinecraftServerPilotMemoryRecoveryTests"))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(parent);
        if (!target.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing unsafe memory-test cleanup: {target}");
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
    }
}

sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

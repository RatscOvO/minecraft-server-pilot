namespace MinecraftServerPilot.Models;

public enum ServerKind
{
    Vanilla,
    Paper,
    Fabric,
    Forge
}

public enum InstallMode
{
    Automatic,
    Guided
}

public enum HashKind
{
    None,
    Sha1,
    Sha256,
    Sha512
}

public sealed record InstallRequest(
    string MinecraftVersion,
    ServerKind ServerKind,
    string ParentDirectory,
    InstallMode Mode,
    int MinimumMemoryMb,
    int MaximumMemoryMb,
    bool RunCompatibilityProbe,
    bool KeepServerRunning,
    bool AllowJavaDownload = true);

public sealed record DownloadCandidate(string Name, Uri Uri);

public sealed record DownloadArtifact(
    IReadOnlyList<DownloadCandidate> Sources,
    string FileName,
    HashKind HashKind = HashKind.None,
    string? ExpectedHash = null,
    long? ExpectedSize = null);

public sealed record ServerPackage(
    DownloadArtifact Artifact,
    string DisplayName,
    int RequiredJavaMajor,
    bool NeedsInstaller,
    string? LoaderVersion = null);

public sealed record JavaRuntime(
    string JavaExe,
    int MajorVersion,
    string Source,
    bool IsManaged);

public sealed record LaunchSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory);

public sealed record InstallResult(
    string ServerDirectory,
    string JavaExe,
    string LogFile,
    string StartCommand,
    bool IsRunning);

public sealed record ExistingServerInfo(
    string ServerDirectory,
    string MinecraftVersion,
    ServerKind ServerKind,
    string Distribution,
    string JavaExe,
    int JavaMajor,
    int MinimumMemoryMb,
    int MaximumMemoryMb,
    int ServerPort);

public sealed record OperationProgress(
    string Stage,
    string Message,
    double? Percent = null,
    bool IsWarning = false);

public sealed record InstallCheckpoint(
    string Stage,
    string Title,
    string Message,
    bool IsSecurityRelevant = false);

public sealed class InstallSessionState
{
    public int SchemaVersion { get; set; } = 1;
    public string MinecraftVersion { get; set; } = "";
    public ServerKind ServerKind { get; set; }
    public int MinimumMemoryMb { get; set; }
    public int MaximumMemoryMb { get; set; }
    public string Status { get; set; } = "Running";
    public string Stage { get; set; } = "Created";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public string? LastError { get; set; }
    public List<string> Recoveries { get; set; } = [];
    public string CompatibilityProbeStatus { get; set; } = "NotRequested";
}

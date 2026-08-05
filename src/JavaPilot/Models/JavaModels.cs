namespace JavaPilot.Models;

public enum HashKind
{
    None,
    Sha1,
    Sha256
}

public sealed record JavaOption(
    int Major,
    string MinecraftVersions,
    string Description,
    bool IsLegacy = false)
{
    public string Title => $"Java {Major}";
    public string Recommendation => $"推荐 MC：{MinecraftVersions}";
}

public sealed record DownloadSource(string Name, Uri Uri);

public sealed record RuntimePackage(
    string Provider,
    IReadOnlyList<DownloadSource> Sources,
    string FileName,
    HashKind HashKind = HashKind.None,
    string? ExpectedHash = null,
    long? ExpectedSize = null);

public sealed record InstallRequest(
    int JavaMajor,
    string InstallRoot,
    bool SetAsUserDefault,
    bool ReuseSystemJava = true,
    bool ForceReinstall = false);

public sealed record InstallResult(
    int JavaMajor,
    string JavaHome,
    string JavaExe,
    string Provider,
    string FullVersion,
    bool Reused,
    bool IsSystemRuntime = false);

public sealed record JavaInstallation(
    int Major,
    string FullVersion,
    string JavaHome,
    string JavaExe,
    string Source,
    bool Is64Bit);

public enum JavaOwnership
{
    JavaPilotManaged,
    RegisteredInstaller,
    ExternalPortable,
    ExternalUnknown
}

public sealed record JavaInventoryItem(
    int Major,
    string FullVersion,
    string JavaHome,
    string JavaExe,
    string Source,
    bool Is64Bit,
    bool Healthy,
    JavaOwnership Ownership,
    string? ProductName = null,
    string? Publisher = null,
    string? UninstallCommand = null,
    bool IsCurrentUserDefault = false,
    DateTimeOffset? InstalledAt = null)
{
    public string DisplayVersion => Healthy
        ? $"Java {FullVersion}"
        : $"Java {Major}（验证异常）";
    public string ArchitectureText => Is64Bit ? "64 位" : "32 位";
    public string OwnershipText => Ownership switch
    {
        JavaOwnership.JavaPilotManaged => "Java Pilot 管理",
        JavaOwnership.RegisteredInstaller => "Windows 已注册安装",
        JavaOwnership.ExternalPortable => "外部便携 Java",
        _ => "来源待确认"
    };
    public string ProviderText =>
        ProductName ?? Publisher ?? Source;
    public string DefaultText =>
        IsCurrentUserDefault ? " · 当前用户默认" : "";
    public string InstalledAtText =>
        InstalledAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "";
}

public sealed record ManagedRuntimeInfo(
    int Major,
    string FullVersion,
    string Provider,
    string JavaHome,
    string JavaExe,
    DateTimeOffset? InstalledAt,
    bool Healthy,
    string Status)
{
    public string DisplayVersion => Healthy
        ? $"Java {FullVersion}"
        : $"Java {Major}（需要修复）";
    public string InstalledAtText => InstalledAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "未知";
}

public sealed record OperationProgress(
    string Stage,
    string Message,
    double? Percent = null,
    bool IsWarning = false);

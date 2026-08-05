using JavaPilot.Models;

namespace JavaPilot.Services;

/// <summary>
/// 读取当前进程实际看到的 JAVA_HOME 与 PATH，并验证它们各自指向的 Java。
/// 注意：Windows 执行 “java” 时使用 PATH；JAVA_HOME 只是供启动器或脚本引用的约定。
/// </summary>
public sealed class DefaultJavaEnvironmentService
{
    private readonly AppLog _log;

    public DefaultJavaEnvironmentService(AppLog log)
    {
        _log = log;
    }

    public async Task<DefaultJavaEnvironmentSnapshot> InspectAsync(
        CancellationToken cancellationToken)
    {
        var processJavaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        var userJavaHome = ReadEnvironmentVariable(
            "JAVA_HOME",
            EnvironmentVariableTarget.User);
        var machineJavaHome = ReadEnvironmentVariable(
            "JAVA_HOME",
            EnvironmentVariableTarget.Machine);
        var javaHomeScope = DescribeJavaHomeScope(
            processJavaHome,
            userJavaHome,
            machineJavaHome);

        var javaHomeReference = string.IsNullOrWhiteSpace(processJavaHome)
            ? null
            : await ProbeReferenceAsync(
                "JAVA_HOME",
                processJavaHome,
                Path.Combine(processJavaHome, "bin", "java.exe"),
                cancellationToken);

        var pathJava = FindFirstJavaOnPath();
        var pathReference = pathJava is null
            ? null
            : await ProbeReferenceAsync(
                "PATH",
                pathJava.Value.PathEntry,
                pathJava.Value.JavaExe,
                cancellationToken);

        var isConsistent =
            pathReference?.Installation is not null &&
            javaHomeReference?.Installation is not null &&
            PathsEqual(
                pathReference.Installation.JavaHome,
                javaHomeReference.Installation.JavaHome);

        var snapshot = new DefaultJavaEnvironmentSnapshot(
            pathReference,
            javaHomeReference,
            processJavaHome,
            userJavaHome,
            machineJavaHome,
            javaHomeScope,
            isConsistent);

        _log.Info("ENVIRONMENT", snapshot.ToLogText());
        return snapshot;
    }

    private async Task<EnvironmentJavaReference> ProbeReferenceAsync(
        string source,
        string configuredValue,
        string javaExe,
        CancellationToken cancellationToken)
    {
        string normalizedExe;
        try
        {
            normalizedExe = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(javaExe.Trim().Trim('"')));
        }
        catch (Exception ex)
        {
            return new EnvironmentJavaReference(
                source,
                configuredValue,
                null,
                null,
                $"路径格式无效：{ex.Message}");
        }

        if (!File.Exists(normalizedExe))
        {
            return new EnvironmentJavaReference(
                source,
                configuredValue,
                normalizedExe,
                null,
                "配置存在，但找不到 java.exe");
        }

        try
        {
            var details = await JavaVersionProbe.ProbeDetailsAsync(
                normalizedExe,
                cancellationToken);
            var logicalExe = ResolveFinalExecutable(normalizedExe);
            var javaHome = ResolveJavaHome(logicalExe);
            var installation = new JavaInstallation(
                details.Major,
                details.FullVersion,
                javaHome,
                logicalExe,
                source,
                details.Is64Bit);
            return new EnvironmentJavaReference(
                source,
                configuredValue,
                logicalExe,
                installation,
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn(
                "ENVIRONMENT",
                $"{source} 指向的 Java 无法启动验证：{normalizedExe}；{ex.Message}");
            return new EnvironmentJavaReference(
                source,
                configuredValue,
                normalizedExe,
                null,
                $"无法启动验证：{ex.Message}");
        }
    }

    private static (string PathEntry, string JavaExe)? FindFirstJavaOnPath()
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var rawEntry in pathValue.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var expanded = Environment
                    .ExpandEnvironmentVariables(rawEntry.Trim().Trim('"'));
                var candidate = Path.GetFullPath(Path.Combine(expanded, "java.exe"));
                if (File.Exists(candidate))
                    return (expanded, candidate);
            }
            catch
            {
                // 一个损坏的 PATH 项不应阻止检查后续入口。
            }
        }

        return null;
    }

    private static string ResolveFinalExecutable(string javaExe)
    {
        try
        {
            return new FileInfo(javaExe)
                       .ResolveLinkTarget(returnFinalTarget: true)?.FullName
                   ?? javaExe;
        }
        catch
        {
            return javaExe;
        }
    }

    private static string ResolveJavaHome(string javaExe)
    {
        var bin = Path.GetDirectoryName(javaExe)
                  ?? throw new InvalidDataException($"无法解析 Java bin 目录：{javaExe}");
        var home = Directory.GetParent(bin)?.FullName
                   ?? throw new InvalidDataException($"无法解析 Java 主目录：{javaExe}");
        if (Path.GetFileName(home).Equals("jre", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(home)?.FullName;
            if (parent is not null &&
                File.Exists(Path.Combine(parent, "bin", "java.exe")))
                return Path.GetFullPath(parent);
        }

        return Path.GetFullPath(home);
    }

    private static string? ReadEnvironmentVariable(
        string name,
        EnvironmentVariableTarget target)
    {
        try
        {
            return Environment.GetEnvironmentVariable(name, target);
        }
        catch
        {
            return null;
        }
    }

    private static string DescribeJavaHomeScope(
        string? processValue,
        string? userValue,
        string? machineValue)
    {
        if (string.IsNullOrWhiteSpace(processValue))
            return "未设置";
        if (PathsEqual(processValue, userValue))
            return "当前用户环境变量";
        if (PathsEqual(processValue, machineValue))
            return "系统环境变量";
        return "当前进程环境";
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        try
        {
            return Path.GetFullPath(left.Trim().Trim('"'))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(
                    Path.GetFullPath(right.Trim().Trim('"'))
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return left.Trim().Trim('"').Equals(
                right.Trim().Trim('"'),
                StringComparison.OrdinalIgnoreCase);
        }
    }
}

public sealed record EnvironmentJavaReference(
    string Source,
    string ConfiguredValue,
    string? JavaExe,
    JavaInstallation? Installation,
    string? Error)
{
    public bool IsHealthy => Installation is not null;
}

public sealed record DefaultJavaEnvironmentSnapshot(
    EnvironmentJavaReference? PathDefault,
    EnvironmentJavaReference? JavaHome,
    string? ProcessJavaHome,
    string? UserJavaHome,
    string? MachineJavaHome,
    string JavaHomeScope,
    bool IsConsistent)
{
    public bool HasAnyConfiguration =>
        PathDefault is not null || !string.IsNullOrWhiteSpace(ProcessJavaHome);

    public bool HasBrokenConfiguration =>
        PathDefault is { IsHealthy: false } ||
        JavaHome is { IsHealthy: false };

    public string ToLogText()
    {
        var path = PathDefault?.Installation is { } pathRuntime
            ? $"PATH=Java {pathRuntime.FullVersion} ({pathRuntime.JavaExe})"
            : PathDefault is null
                ? "PATH=未找到 java.exe"
                : $"PATH=无效 ({PathDefault.Error})";
        var home = JavaHome?.Installation is { } homeRuntime
            ? $"JAVA_HOME=Java {homeRuntime.FullVersion} ({homeRuntime.JavaHome})"
            : string.IsNullOrWhiteSpace(ProcessJavaHome)
                ? "JAVA_HOME=未设置"
                : $"JAVA_HOME=无效 ({JavaHome?.Error})";
        var relation = PathDefault?.Installation is not null &&
                       JavaHome?.Installation is not null
            ? IsConsistent ? "两者一致" : "两者不一致"
            : "未进行一致性比较";
        return $"默认 Java 检查：{path}；{home}；{relation}。";
    }
}

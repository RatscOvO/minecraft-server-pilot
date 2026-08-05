using System.Runtime.InteropServices;

namespace JavaPilot.Services;

public sealed class UserEnvironmentService
{
    private readonly AppLog _log;

    public UserEnvironmentService(AppLog log)
    {
        _log = log;
    }

    public async Task<JavaDefaultChangeResult> SetDefaultJavaAsync(
        string javaHome,
        CancellationToken cancellationToken)
    {
        javaHome = Path.GetFullPath(javaHome);
        var bin = Path.Combine(javaHome, "bin");
        var javaExe = Path.Combine(bin, "java.exe");
        if (!File.Exists(javaExe))
            throw new FileNotFoundException("设置环境变量前找不到 java.exe。", javaExe);
        var verified = await JavaVersionProbe
            .ProbeDetailsAsync(javaExe, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!verified.Is64Bit)
            throw new InvalidOperationException("拒绝把 32 位 Java 设为 Minecraft 默认运行时。");

        var previousUserHome = Environment.GetEnvironmentVariable(
            "JAVA_HOME",
            EnvironmentVariableTarget.User);

        Environment.SetEnvironmentVariable(
            "JAVA_HOME",
            javaHome,
            EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable("JAVA_HOME", javaHome);

        var userPath = Environment.GetEnvironmentVariable(
                           "PATH",
                           EnvironmentVariableTarget.User) ?? "";
        var updatedUserPath = BuildPathWithJavaFirst(
            userPath,
            bin,
            string.IsNullOrWhiteSpace(previousUserHome)
                ? null
                : Path.Combine(previousUserHome, "bin"));
        Environment.SetEnvironmentVariable(
            "PATH",
            updatedUserPath,
            EnvironmentVariableTarget.User);

        var savedHome = Environment.GetEnvironmentVariable(
            "JAVA_HOME",
            EnvironmentVariableTarget.User);
        var savedPath = Environment.GetEnvironmentVariable(
                            "PATH",
                            EnvironmentVariableTarget.User) ?? "";
        if (!Normalize(savedHome ?? "").Equals(
                Normalize(javaHome),
                StringComparison.OrdinalIgnoreCase) ||
            !FirstPathEntry(savedPath).Equals(
                Normalize(bin),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Windows 接受了环境变量写入请求，但回读校验不一致。可能被组策略或安全软件拦截。");
        }

        var processPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        var updatedProcessPath = BuildPathWithJavaFirst(
            processPath,
            bin,
            string.IsNullOrWhiteSpace(previousUserHome)
                ? null
                : Path.Combine(previousUserHome, "bin"));
        Environment.SetEnvironmentVariable("PATH", updatedProcessPath);

        var currentProcessJava = FindFirstJavaOnPath(updatedProcessPath);
        var currentProcessUsesTarget = PathsEqual(currentProcessJava, javaExe);
        if (!currentProcessUsesTarget)
        {
            throw new InvalidOperationException(
                "用户环境变量已经写入，但 Java Pilot 当前进程仍未调用目标 java.exe。" +
                $"目标：{javaExe}；实际：{currentProcessJava ?? "未找到"}。");
        }

        var machinePath = Environment.GetEnvironmentVariable(
                              "PATH",
                              EnvironmentVariableTarget.Machine) ?? "";
        var machineJava = FindFirstJavaOnPath(
            machinePath,
            Environment.GetEnvironmentVariable(
                "JAVA_HOME",
                EnvironmentVariableTarget.Machine));
        var machinePathConflict =
            !string.IsNullOrWhiteSpace(machineJava) &&
            !PathsEqual(machineJava, javaExe);

        BroadcastEnvironmentChange();
        var result = new JavaDefaultChangeResult(
            javaHome,
            javaExe,
            verified.Major,
            verified.FullVersion,
            currentProcessUsesTarget,
            machinePathConflict,
            machineJava);
        _log.Info(
            "ENV",
            $"已写入当前用户 JAVA_HOME={javaHome}，用户 PATH 首项={bin}；" +
            $"当前进程命令行={currentProcessJava}；" +
            (machinePathConflict
                ? $"检测到系统 PATH 优先项冲突：{machineJava}"
                : "系统 PATH 未抢占目标 Java。"));
        return result;
    }

    public void ClearIfPointsTo(string javaHome)
    {
        var bin = Path.Combine(javaHome, "bin");
        var savedHome = Environment.GetEnvironmentVariable(
            "JAVA_HOME",
            EnvironmentVariableTarget.User);
        var pointsToRemovedHome = Normalize(savedHome ?? "").Equals(
            Normalize(javaHome),
            StringComparison.OrdinalIgnoreCase);
        if (pointsToRemovedHome)
            Environment.SetEnvironmentVariable(
                "JAVA_HOME",
                null,
                EnvironmentVariableTarget.User);

        var userPath = Environment.GetEnvironmentVariable(
                           "PATH",
                           EnvironmentVariableTarget.User) ?? "";
        var entries = userPath.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(entry =>
            {
                if (Normalize(entry).Equals(
                        Normalize(bin),
                        StringComparison.OrdinalIgnoreCase))
                    return false;
                if (pointsToRemovedHome &&
                    entry.Trim().Equals(
                        "%JAVA_HOME%\\bin",
                        StringComparison.OrdinalIgnoreCase))
                    return false;
                return true;
            })
            .ToArray();
        Environment.SetEnvironmentVariable(
            "PATH",
            string.Join(Path.PathSeparator, entries),
            EnvironmentVariableTarget.User);

        if (pointsToRemovedHome)
            Environment.SetEnvironmentVariable("JAVA_HOME", null);
        BroadcastEnvironmentChange();
        _log.Info(
            "ENV",
            $"已清理指向待删除 Java 的当前用户环境变量：{javaHome}");
    }

    public static string BuildPathWithJavaFirst(
        string existingPath,
        string targetBin,
        string? previousJavaBin = null)
    {
        var normalizedTarget = Normalize(targetBin);
        var normalizedPrevious = Normalize(previousJavaBin ?? "");
        var entries = existingPath.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(entry =>
            {
                if (entry.Trim().Equals(
                        "%JAVA_HOME%\\bin",
                        StringComparison.OrdinalIgnoreCase))
                    return false;
                var normalized = Normalize(entry);
                if (normalized.Equals(
                        normalizedTarget,
                        StringComparison.OrdinalIgnoreCase))
                    return false;
                return string.IsNullOrWhiteSpace(normalizedPrevious) ||
                       !normalized.Equals(
                           normalizedPrevious,
                           StringComparison.OrdinalIgnoreCase);
            })
            .ToList();
        entries.Insert(0, Path.GetFullPath(targetBin));
        return string.Join(Path.PathSeparator, entries);
    }

    public static string? FindFirstJavaOnPath(
        string pathValue,
        string? javaHomeOverride = null)
    {
        foreach (var entry in pathValue.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var configured = entry.Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(javaHomeOverride))
                {
                    configured = configured.Replace(
                        "%JAVA_HOME%",
                        javaHomeOverride,
                        StringComparison.OrdinalIgnoreCase);
                }
                var candidate = Path.Combine(
                    Environment.ExpandEnvironmentVariables(configured),
                    "java.exe");
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            catch
            {
                // 损坏的单个 PATH 项不应阻止检查后续条目。
            }
        }

        return null;
    }

    private static string FirstPathEntry(string pathValue)
    {
        var entry = pathValue.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
        return Normalize(entry);
    }

    private static bool PathsEqual(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        Normalize(left).Equals(Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            return Path.GetFullPath(expanded)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return value.Trim().Trim('"').TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        }
    }

    internal static void BroadcastEnvironmentChange()
    {
        _ = SendMessageTimeout(
            new IntPtr(0xffff),
            0x001A,
            IntPtr.Zero,
            "Environment",
            0x0002,
            5000,
            out _);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        string lParam,
        uint flags,
        uint timeout,
        out IntPtr result);
}

public sealed record JavaDefaultChangeResult(
    string JavaHome,
    string JavaExe,
    int Major,
    string FullVersion,
    bool CurrentProcessUsesTarget,
    bool MachinePathConflict,
    string? ConflictingMachineJavaExe);

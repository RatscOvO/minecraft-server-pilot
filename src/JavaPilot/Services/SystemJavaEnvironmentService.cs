using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace JavaPilot.Services;

/// <summary>
/// 在用户明确同意 UAC 后调整系统级 Java 优先级。
/// 只把目标 bin 放到系统 PATH 首位，保留其余 Java 与 PATH 条目。
/// </summary>
public sealed class SystemJavaEnvironmentService
{
    private const string CommandName = "--apply-system-java-default";
    private const string CleanupCommandName = "--clear-system-java-backups";
    private const string ResultPrefix = "java-pilot-elevated-";
    private readonly AppLog _log;

    public SystemJavaEnvironmentService(AppLog log)
    {
        _log = log;
    }

    public async Task<SystemJavaDefaultResult> ApplyElevatedAsync(
        string javaHome,
        CancellationToken cancellationToken)
    {
        javaHome = Path.GetFullPath(javaHome);
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
            throw new InvalidOperationException("无法定位 Java Pilot 自身可执行文件，不能启动提权修复。");

        var resultPath = Path.Combine(
            Path.GetTempPath(),
            $"{ResultPrefix}{Guid.NewGuid():N}.json");
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = processPath,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            };
            startInfo.ArgumentList.Add(CommandName);
            startInfo.ArgumentList.Add(javaHome);
            startInfo.ArgumentList.Add(resultPath);
            using var process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException("Windows 没有启动提权修复进程。");
            await process.WaitForExitAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromMinutes(3), cancellationToken);

            if (!File.Exists(resultPath))
            {
                throw new InvalidOperationException(
                    $"提权修复进程已退出（代码 {process.ExitCode}），但没有生成结果报告。");
            }

            var envelope = JsonSerializer.Deserialize<ElevatedResultEnvelope>(
                               await File.ReadAllTextAsync(resultPath, cancellationToken))
                           ?? throw new InvalidDataException("提权修复结果文件为空或格式无效。");
            if (!envelope.Success || envelope.Result is null)
            {
                throw new InvalidOperationException(
                    "系统 Java 优先级修复失败。\n" +
                    (envelope.Error ?? "提权进程没有返回具体原因。"));
            }

            _log.Info(
                "ENV-SYSTEM",
                $"系统默认 Java 已更新为 {envelope.Result.JavaHome}；" +
                $"备份：{envelope.Result.BackupPath}");
            return envelope.Result;
        }
        finally
        {
            TryDeleteResultFile(resultPath);
        }
    }

    public JavaBackupSummary GetBackupSummary() =>
        new EnvironmentBackupService().Inspect();

    public async Task<JavaBackupCleanupResult> ClearBackupsElevatedAsync(
        CancellationToken cancellationToken)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
            throw new InvalidOperationException("无法定位 Java Pilot 自身可执行文件，不能启动提权清理。");

        var resultPath = Path.Combine(
            Path.GetTempPath(),
            $"{ResultPrefix}{Guid.NewGuid():N}.json");
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = processPath,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            };
            startInfo.ArgumentList.Add(CleanupCommandName);
            startInfo.ArgumentList.Add(resultPath);
            using var process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException("Windows 没有启动提权清理进程。");
            await process.WaitForExitAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromMinutes(3), cancellationToken);

            if (!File.Exists(resultPath))
            {
                throw new InvalidOperationException(
                    $"提权清理进程已退出（代码 {process.ExitCode}），但没有生成结果报告。");
            }

            var envelope = JsonSerializer.Deserialize<ElevatedBackupCleanupEnvelope>(
                               await File.ReadAllTextAsync(resultPath, cancellationToken))
                           ?? throw new InvalidDataException("提权清理结果文件为空或格式无效。");
            if (!envelope.Success || envelope.Result is null)
            {
                throw new InvalidOperationException(
                    "系统环境备份清理失败。\n" +
                    (envelope.Error ?? "提权进程没有返回具体原因。"));
            }

            _log.Info(
                "ENV-BACKUP",
                $"已清理 {envelope.Result.DeletedCount} 个环境备份，" +
                $"{envelope.Result.DeletedBytes} 字节；目录：{envelope.Result.DirectoryPath}");
            return envelope.Result;
        }
        finally
        {
            TryDeleteResultFile(resultPath);
        }
    }

    public static bool TryHandleElevatedCommand(
        IReadOnlyList<string> args,
        AppLog log,
        out int exitCode)
    {
        exitCode = 0;
        if (args.Count == 0)
            return false;

        if (args[0].Equals(CleanupCommandName, StringComparison.OrdinalIgnoreCase))
            return HandleElevatedCleanupCommand(args, log, out exitCode);
        if (!args[0].Equals(CommandName, StringComparison.OrdinalIgnoreCase))
            return false;

        if (args.Count != 3)
        {
            exitCode = 2;
            return true;
        }

        var resultPath = args[2];
        if (!IsSafeResultPath(resultPath))
        {
            log.Error("ENV-SYSTEM", $"拒绝不安全的提权结果路径：{resultPath}");
            exitCode = 3;
            return true;
        }

        ElevatedResultEnvelope envelope;
        try
        {
            var result = ApplySystemDefault(args[1], log);
            envelope = new ElevatedResultEnvelope(true, result, null);
        }
        catch (Exception ex)
        {
            log.Error("ENV-SYSTEM", "系统 Java 优先级修复失败。", ex);
            envelope = new ElevatedResultEnvelope(false, null, ex.ToString());
            exitCode = 1;
        }

        try
        {
            File.WriteAllText(
                resultPath,
                JsonSerializer.Serialize(
                    envelope,
                    new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            log.Error("ENV-SYSTEM", "无法写入提权结果报告。", ex);
            exitCode = 4;
        }

        return true;
    }

    private static bool HandleElevatedCleanupCommand(
        IReadOnlyList<string> args,
        AppLog log,
        out int exitCode)
    {
        exitCode = 0;
        if (args.Count != 2)
        {
            exitCode = 2;
            return true;
        }

        var resultPath = args[1];
        if (!IsSafeResultPath(resultPath))
        {
            log.Error("ENV-BACKUP", $"拒绝不安全的提权结果路径：{resultPath}");
            exitCode = 3;
            return true;
        }

        ElevatedBackupCleanupEnvelope envelope;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
                throw new UnauthorizedAccessException("清理系统环境备份需要管理员权限。");

            var result = new EnvironmentBackupService().Clear();
            log.Info(
                "ENV-BACKUP",
                $"提权清理完成：{result.DeletedCount} 个文件，" +
                $"{result.DeletedBytes} 字节；{result.DirectoryPath}");
            envelope = new ElevatedBackupCleanupEnvelope(true, result, null);
        }
        catch (Exception ex)
        {
            log.Error("ENV-BACKUP", "系统环境备份清理失败。", ex);
            envelope = new ElevatedBackupCleanupEnvelope(false, null, ex.ToString());
            exitCode = 1;
        }

        try
        {
            File.WriteAllText(
                resultPath,
                JsonSerializer.Serialize(
                    envelope,
                    new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            log.Error("ENV-BACKUP", "无法写入提权清理结果报告。", ex);
            exitCode = 4;
        }

        return true;
    }

    private static SystemJavaDefaultResult ApplySystemDefault(
        string javaHome,
        AppLog log)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("修复系统 PATH 需要管理员权限。");

        javaHome = Path.GetFullPath(javaHome);
        var bin = Path.Combine(javaHome, "bin");
        var javaExe = Path.Combine(bin, "java.exe");
        var details = JavaVersionProbe
            .ProbeDetailsAsync(javaExe, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        if (!details.Is64Bit)
            throw new InvalidOperationException("拒绝把 32 位 Java 放到系统 PATH 首位。");

        var previousHome = Environment.GetEnvironmentVariable(
            "JAVA_HOME",
            EnvironmentVariableTarget.Machine);
        var previousPath = Environment.GetEnvironmentVariable(
                               "PATH",
                               EnvironmentVariableTarget.Machine) ?? "";
        var backupPath = WriteBackup(previousHome, previousPath, javaHome);
        var updatedPath = UserEnvironmentService.BuildPathWithJavaFirst(
            previousPath,
            bin);

        try
        {
            Environment.SetEnvironmentVariable(
                "JAVA_HOME",
                javaHome,
                EnvironmentVariableTarget.Machine);
            Environment.SetEnvironmentVariable(
                "PATH",
                updatedPath,
                EnvironmentVariableTarget.Machine);

            var savedHome = Environment.GetEnvironmentVariable(
                "JAVA_HOME",
                EnvironmentVariableTarget.Machine);
            var savedPath = Environment.GetEnvironmentVariable(
                                "PATH",
                                EnvironmentVariableTarget.Machine) ?? "";
            var savedDefault = UserEnvironmentService.FindFirstJavaOnPath(
                savedPath,
                savedHome);
            if (!PathsEqual(savedHome, javaHome) ||
                !PathsEqual(savedDefault, javaExe))
            {
                throw new InvalidOperationException(
                    "系统环境变量写入后的回读校验不一致。" +
                    $"JAVA_HOME={savedHome ?? "未设置"}；PATH 默认={savedDefault ?? "未找到"}。");
            }

            Environment.SetEnvironmentVariable("JAVA_HOME", javaHome);
            Environment.SetEnvironmentVariable(
                "PATH",
                UserEnvironmentService.BuildPathWithJavaFirst(
                    Environment.GetEnvironmentVariable("PATH") ?? "",
                    bin));
            UserEnvironmentService.BroadcastEnvironmentChange();
            log.Info(
                "ENV-SYSTEM",
                $"系统 JAVA_HOME 与 PATH 首项已更新：Java {details.FullVersion}；{javaHome}");
            return new SystemJavaDefaultResult(
                javaHome,
                javaExe,
                details.Major,
                details.FullVersion,
                backupPath);
        }
        catch (Exception original)
        {
            Exception? rollbackError = null;
            try
            {
                Environment.SetEnvironmentVariable(
                    "JAVA_HOME",
                    previousHome,
                    EnvironmentVariableTarget.Machine);
                Environment.SetEnvironmentVariable(
                    "PATH",
                    previousPath,
                    EnvironmentVariableTarget.Machine);
                UserEnvironmentService.BroadcastEnvironmentChange();
            }
            catch (Exception ex)
            {
                rollbackError = ex;
            }

            throw new InvalidOperationException(
                rollbackError is null
                    ? $"系统环境变量更新失败，已恢复原值。备份：{backupPath}"
                    : $"系统环境变量更新失败，自动恢复也失败。请使用备份人工恢复：{backupPath}\n" +
                      $"恢复错误：{rollbackError.Message}",
                original);
        }
    }

    private static string WriteBackup(
        string? previousHome,
        string previousPath,
        string requestedHome)
    {
        var root = EnvironmentBackupService.DefaultDirectory;
        Directory.CreateDirectory(root);
        var path = Path.Combine(
            root,
            $"environment-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                new
                {
                    createdAt = DateTimeOffset.Now,
                    previousJavaHome = previousHome,
                    previousPath,
                    requestedJavaHome = requestedHome
                },
                new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static bool IsSafeResultPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var temp = Path.GetFullPath(Path.GetTempPath())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = Directory.GetParent(full)?.FullName?
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(full);
            return parent is not null &&
                   parent.Equals(temp, StringComparison.OrdinalIgnoreCase) &&
                   name.StartsWith(ResultPrefix, StringComparison.OrdinalIgnoreCase) &&
                   name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
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
            return false;
        }
    }

    private static void TryDeleteResultFile(string path)
    {
        try
        {
            if (File.Exists(path) && IsSafeResultPath(path))
                File.Delete(path);
        }
        catch
        {
            // 临时结果清理失败不覆盖实际操作结果。
        }
    }
}

public sealed record SystemJavaDefaultResult(
    string JavaHome,
    string JavaExe,
    int Major,
    string FullVersion,
    string BackupPath);

public sealed record ElevatedResultEnvelope(
    bool Success,
    SystemJavaDefaultResult? Result,
    string? Error);

public sealed record ElevatedBackupCleanupEnvelope(
    bool Success,
    JavaBackupCleanupResult? Result,
    string? Error);

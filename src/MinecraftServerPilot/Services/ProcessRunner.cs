using System.Diagnostics;
using System.Text;
using MinecraftServerPilot.Models;

namespace MinecraftServerPilot.Services;

public sealed record ProcessRunResult(int ExitCode, string Output, bool TimedOut);
public sealed record ServerVerificationResult(bool Success, string Output, int ExitCode, bool TimedOut);

public sealed class ProcessRunner
{
    private readonly AppLog _log;

    public ProcessRunner(AppLog log) => _log = log;

    public async Task<ProcessRunResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        string area,
        IProgress<OperationProgress>? progress,
        CancellationToken token)
    {
        using var process = CreateProcess(fileName, arguments, workingDirectory, redirectInput: false);
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        void Append(string? line)
        {
            if (line is null) return;
            lock (output) output.AppendLine(line);
            _log.Info(area, line);
            if (!IsVerboseArchiveEntry(line))
                progress?.Report(new(area, line));
        }

        _log.Info(area, $"执行：{fileName} {string.Join(" ", arguments.Select(QuoteForDisplay))}");
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(token).WaitAsync(timeout, token);
        }
        catch (TimeoutException)
        {
            timedOut = true;
            _log.Warn(area, $"进程超过 {timeout.TotalMinutes:0.#} 分钟，准备终止。");
            TryKillTree(process);
            await process.WaitForExitAsync(CancellationToken.None);
        }
        return new ProcessRunResult(process.ExitCode, output.ToString(), timedOut);
    }

    public async Task<ServerVerificationResult> VerifyServerAsync(
        LaunchSpec spec,
        TimeSpan startupTimeout,
        bool leaveRunning,
        IProgress<OperationProgress>? progress,
        CancellationToken token)
    {
        using var process = CreateProcess(spec.FileName, spec.Arguments, spec.WorkingDirectory, redirectInput: true);
        var output = new StringBuilder();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fatal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        void OnLine(string? line)
        {
            if (line is null) return;
            lock (output) output.AppendLine(line);
            _log.Info("SERVER", line);
            progress?.Report(new("启动验证", line));
            if (IsReadyLine(line)) ready.TrySetResult();
            if (IsFatalLine(line)) fatal.TrySetResult();
        }

        _log.Info("SERVER", $"启动：{spec.FileName} {string.Join(" ", spec.Arguments.Select(QuoteForDisplay))}");
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var exited = process.WaitForExitAsync(CancellationToken.None);
        var timeout = Task.Delay(startupTimeout, token);
        var completed = await Task.WhenAny(ready.Task, fatal.Task, exited, timeout);
        var success = completed == ready.Task;
        var timedOut = completed == timeout;

        if (success && leaveRunning)
        {
            _log.Info("SERVER", $"验证成功；按用户设置保持运行，PID={process.Id}");
            return new ServerVerificationResult(true, output.ToString(), 0, false);
        }

        if (!process.HasExited)
        {
            try
            {
                if (success)
                {
                    progress?.Report(new("启动验证", "服务端已就绪，正在发送 save-all 与 stop 进行安全停服…"));
                    await process.StandardInput.WriteLineAsync("save-all");
                    await process.StandardInput.WriteLineAsync("stop");
                    await process.StandardInput.FlushAsync();
                    await exited.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                }
                else
                {
                    TryKillTree(process);
                    await exited;
                }
            }
            catch
            {
                TryKillTree(process);
            }
        }
        return new ServerVerificationResult(success, output.ToString(),
            process.HasExited ? process.ExitCode : -1, timedOut);
    }

    private static Process CreateProcess(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        bool redirectInput)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectInput,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return new Process { StartInfo = startInfo, EnableRaisingEvents = true };
    }

    private static bool IsReadyLine(string line)
    {
        var text = line.ToLowerInvariant();
        return (text.Contains("done (") && text.Contains("for help")) ||
               text.Contains("server started") ||
               text.Contains("dedicated server took");
    }

    private static bool IsFatalLine(string line)
    {
        var text = line.ToLowerInvariant();
        return text.Contains("failed to start the minecraft server") ||
               text.Contains("failed to bind to port") ||
               text.Contains("unsupportedclassversionerror") ||
               text.Contains("you need to agree to the eula");
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The process may have exited between the check and Kill.
        }
    }

    private static string QuoteForDisplay(string value) =>
        value.Contains(' ') ? $"\"{value.Replace("\"", "\\\"")}\"" : value;

    private static bool IsVerboseArchiveEntry(string line)
    {
        var trimmed = line.Trim();
        return trimmed.EndsWith(".class", StringComparison.OrdinalIgnoreCase) &&
               (trimmed.StartsWith("net/", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("com/", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("org/", StringComparison.OrdinalIgnoreCase));
    }
}

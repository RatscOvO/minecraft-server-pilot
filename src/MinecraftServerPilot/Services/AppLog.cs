using System.Text;

namespace MinecraftServerPilot.Services;

public sealed class AppLog : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;

    public string FilePath { get; }
    public event Action<string>? LineWritten;

    public AppLog(string? directory = null)
    {
        directory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MinecraftServerPilot", "logs");
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, $"pilot-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _writer = new StreamWriter(FilePath, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
        Info("SESSION", $"启动会话；OS={Environment.OSVersion}; .NET={Environment.Version}; 进程={Environment.ProcessPath}");
    }

    public void Info(string area, string message) => Write("INFO", area, message);
    public void Warn(string area, string message) => Write("WARN", area, message);
    public void Error(string area, string message, Exception? exception = null) =>
        Write("ERROR", area, exception is null ? message : $"{message}{Environment.NewLine}{exception}");

    public void Write(string level, string area, string message)
    {
        var line = $"{DateTimeOffset.Now:O} [{level}] [{area}] {message}";
        lock (_gate)
        {
            _writer.WriteLine(line);
        }
        LineWritten?.Invoke(line);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Dispose();
        }
    }
}

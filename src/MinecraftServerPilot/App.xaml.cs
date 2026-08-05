using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace MinecraftServerPilot;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private static string WriteEmergencyLog(string source, Exception exception)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MinecraftServerPilot", "crash");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"crash-{DateTime.Now:yyyyMMdd-HHmmssfff}.log");
        File.WriteAllText(path,
            $"时间: {DateTimeOffset.Now:O}{Environment.NewLine}" +
            $"来源: {source}{Environment.NewLine}" +
            $"系统: {Environment.OSVersion}{Environment.NewLine}" +
            $".NET: {Environment.Version}{Environment.NewLine}{Environment.NewLine}{exception}");
        return path;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var path = WriteEmergencyLog("WPF Dispatcher", e.Exception);
        MessageBox.Show($"程序遇到未处理错误，但错误报告已经保存，窗口不会直接闪退。\n\n{e.Exception.Message}\n\n报告：{path}",
            "Minecraft Server Pilot - 错误报告", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
            WriteEmergencyLog("AppDomain", exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteEmergencyLog("TaskScheduler", e.Exception);
        e.SetObserved();
    }
}

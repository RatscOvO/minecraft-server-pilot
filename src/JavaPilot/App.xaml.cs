using System.Windows;
using System.Windows.Threading;
using JavaPilot.Services;

namespace JavaPilot;

public partial class App
{
    private AppLog? _emergencyLog;

    protected override void OnStartup(StartupEventArgs e)
    {
        _emergencyLog = new AppLog();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        base.OnStartup(e);
        if (SystemJavaEnvironmentService.TryHandleElevatedCommand(
                e.Args,
                _emergencyLog,
                out var exitCode))
        {
            Shutdown(exitCode);
            return;
        }

        ShutdownMode = ShutdownMode.OnMainWindowClose;
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _emergencyLog?.Error("UNHANDLED", "界面线程发生未处理异常。", e.Exception);
        MessageBox.Show(
            $"Java Pilot 遇到了未处理错误，完整信息已写入日志。\n\n{e.Exception}\n\n日志：{_emergencyLog?.FilePath}",
            "Java Pilot - 完整错误报告",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
        if (Current.MainWindow is null)
            Shutdown(-1);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        _emergencyLog?.Error("FATAL", "进程发生不可恢复错误。", e.ExceptionObject as Exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _emergencyLog?.Error("TASK", "后台任务发生未观察异常。", e.Exception);
        e.SetObserved();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _emergencyLog?.Dispose();
        base.OnExit(e);
    }
}

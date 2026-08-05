using System.Diagnostics;
using System.Windows;

namespace MinecraftServerPilot;

public partial class ErrorReportWindow : Window
{
    private readonly string _logPath;

    public ErrorReportWindow(Exception exception, string logPath)
    {
        InitializeComponent();
        _logPath = logPath;
        ReportTextBox.Text =
            $"时间：{DateTimeOffset.Now:O}{Environment.NewLine}" +
            $"系统：{Environment.OSVersion}{Environment.NewLine}" +
            $".NET：{Environment.Version}{Environment.NewLine}" +
            $"日志：{logPath}{Environment.NewLine}{Environment.NewLine}" +
            exception;
        LogPathText.Text = logPath;
    }

    private void CopyButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ReportTextBox.Text);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"复制失败：{ex.Message}", "提示");
        }
    }

    private void OpenLogButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_logPath}\"")
                { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开失败：{ex.Message}", "提示");
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();
}

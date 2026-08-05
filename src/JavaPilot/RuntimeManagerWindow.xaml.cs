using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Controls;
using JavaPilot.Models;
using JavaPilot.Services;

namespace JavaPilot;

public partial class RuntimeManagerWindow
{
    private readonly string _installRoot;
    private readonly JavaInventoryService _inventory;
    private readonly ManagedRuntimeService _managed;
    private readonly SystemJavaEnvironmentService _systemEnvironment;
    private CancellationTokenSource? _cancellation;
    private JavaBackupSummary _backupSummary = new(
        EnvironmentBackupService.DefaultDirectory,
        0,
        0);
    private bool _busy;

    public RuntimeManagerWindow(string installRoot, AppLog log)
    {
        InitializeComponent();
        _installRoot = Path.GetFullPath(installRoot);
        _inventory = new JavaInventoryService(log);
        _managed = new ManagedRuntimeService(log);
        _systemEnvironment = new SystemJavaEnvironmentService(log);
        InstallRootText.Text =
            $"将合并重复入口；Java Pilot 受管目录：{_installRoot}";
    }

    private JavaInventoryItem? SelectedRuntime =>
        RuntimeList.SelectedItem as JavaInventoryItem;

    private async void Window_Loaded(object sender, RoutedEventArgs e) =>
        await RefreshAsync();

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_busy)
            return;
        _busy = true;
        SetButtons();
        StatusText.Text =
            "正在扫描 JAVA_HOME、PATH、注册表、厂商目录和 Java Pilot 目录…";
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        try
        {
            var runtimes = await _inventory.ListAsync(
                _installRoot,
                _cancellation.Token);
            RuntimeList.ItemsSource = runtimes;
            EmptyText.Visibility = runtimes.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            StatusText.Text = runtimes.Count == 0
                ? "没有检测到可以启动的 Java。"
                : BuildInventorySummary(runtimes);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "验证已取消。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"读取失败：{ex.Message}";
        }
        finally
        {
            _busy = false;
            RefreshBackupSummary();
            SetButtons();
        }
    }

    private void RuntimeList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SetButtons();

    private void SetButtons()
    {
        var selected = SelectedRuntime;
        SetDefaultButton.IsEnabled =
            !_busy && selected is { Healthy: true, Is64Bit: true };
        OpenFolderButton.IsEnabled = !_busy && selected is not null;
        AdoptButton.IsEnabled =
            !_busy &&
            selected is { Healthy: true, Is64Bit: true } &&
            selected.Ownership != JavaOwnership.JavaPilotManaged;
        RemoveButton.IsEnabled =
            !_busy && selected?.Ownership is
                JavaOwnership.JavaPilotManaged or
                JavaOwnership.RegisteredInstaller or
                JavaOwnership.ExternalPortable;
        RemoveButton.Content = selected?.Ownership switch
        {
            JavaOwnership.RegisteredInstaller => "启动官方卸载",
            JavaOwnership.ExternalPortable => "移入回收站",
            _ => "安全卸载"
        };
        ClearBackupsButton.IsEnabled = !_busy && _backupSummary.Count > 0;
        RuntimeList.IsHitTestVisible = !_busy;
    }

    private void RefreshBackupSummary()
    {
        try
        {
            _backupSummary = _systemEnvironment.GetBackupSummary();
            ClearBackupsButton.Content = _backupSummary.Count > 0
                ? $"清除备份 ({_backupSummary.Count})"
                : "清除备份";
            ClearBackupsButton.ToolTip = _backupSummary.Count > 0
                ? $"清除 {_backupSummary.Count} 个 Java Pilot 环境备份，" +
                  $"共 {FormatBytes(_backupSummary.TotalBytes)}。\n" +
                  _backupSummary.DirectoryPath
                : $"没有可清理的环境备份。\n{_backupSummary.DirectoryPath}";
        }
        catch (Exception ex)
        {
            _backupSummary = new JavaBackupSummary(
                EnvironmentBackupService.DefaultDirectory,
                0,
                0);
            ClearBackupsButton.Content = "清除备份";
            ClearBackupsButton.ToolTip = $"无法读取备份：{ex.Message}";
        }
    }

    private async void ClearBackups_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        RefreshBackupSummary();
        if (_backupSummary.Count == 0)
        {
            StatusText.Text = "没有 Java Pilot 环境备份需要清理。";
            SetButtons();
            return;
        }

        var answer = MessageBox.Show(
            $"确定清除 {_backupSummary.Count} 个环境备份吗？\n\n" +
            $"占用空间：{FormatBytes(_backupSummary.TotalBytes)}\n" +
            $"目录：{_backupSummary.DirectoryPath}\n\n" +
            "只会删除 Java Pilot 生成的 environment-*.json 文件，" +
            "不会删除 Java，也不会修改 JAVA_HOME 或 PATH。\n\n" +
            "删除后无法使用这些文件人工恢复旧环境设置。",
            "确认清除 Java Pilot 备份",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;

        _busy = true;
        SetButtons();
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        StatusText.Text = "正在等待 Windows 管理员授权并清除环境备份…";
        try
        {
            var result = await _systemEnvironment.ClearBackupsElevatedAsync(
                _cancellation.Token);
            StatusText.Text =
                $"清理完成：已删除 {result.DeletedCount} 个备份，" +
                $"释放 {FormatBytes(result.DeletedBytes)}。Java 与环境变量均未修改。";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            StatusText.Text = "管理员授权已取消，没有删除任何备份。";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "备份清理已取消。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"备份清理失败：{ex.Message}";
            MessageBox.Show(
                ex.ToString(),
                "清除备份完整报告",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            RefreshBackupSummary();
            SetButtons();
        }
    }

    private async void SetDefault_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || SelectedRuntime is not { } runtime)
            return;
        _busy = true;
        SetButtons();
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        StatusText.Text =
            $"正在启动并验证 Java {runtime.FullVersion}，界面仍可正常响应…";
        try
        {
            var result = await _inventory.SetAsDefaultAsync(
                runtime,
                _cancellation.Token);
            if (!result.MachinePathConflict)
            {
                StatusText.Text =
                    $"已切换为 Java {runtime.FullVersion}：用户 JAVA_HOME、用户 PATH " +
                    "以及 Java Pilot 当前命令行验证均已通过。";
                return;
            }

            StatusText.Text =
                $"用户 JAVA_HOME 已切换到 Java {runtime.FullVersion}，但系统 PATH 中的 " +
                $"{result.ConflictingMachineJavaExe} 优先级更高。";
            var answer = MessageBox.Show(
                $"Java {runtime.FullVersion} 已写入当前用户环境变量，" +
                "Java Pilot 当前进程也已经可以正确调用它。\n\n" +
                "但是检测到系统级 PATH 中另一个 Java 排在用户 PATH 之前：\n" +
                $"{result.ConflictingMachineJavaExe}\n\n" +
                "这会导致从桌面或开始菜单新开的终端仍可能调用旧 Java。\n\n" +
                $"是否请求管理员权限，把 {runtime.JavaHome}\\bin 放到系统 PATH 最前面？\n" +
                "程序不会删除旧 Java，也不会删除其他 PATH 条目；修改前会自动保存完整备份。",
                "需要修复系统 PATH 优先级",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                StatusText.Text =
                    $"已完成用户级切换到 Java {runtime.FullVersion}；系统 PATH 未修改，" +
                    "因此外部命令行仍可能优先调用旧 Java。";
                return;
            }

            StatusText.Text = "正在等待 Windows 管理员授权并修复系统 PATH 优先级…";
            var systemResult = await _systemEnvironment.ApplyElevatedAsync(
                runtime.JavaHome,
                _cancellation.Token);
            StatusText.Text =
                $"已完整切换命令行默认 Java：Java {systemResult.FullVersion}。" +
                $"旧环境已备份到 {systemResult.BackupPath}";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            StatusText.Text =
                "管理员授权已取消。用户 JAVA_HOME 已切换，但系统 PATH 保持不变。";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "默认 Java 切换验证已取消。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"设置失败：{ex.Message}";
            MessageBox.Show(
                ex.ToString(),
                "无法设置默认 Java",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            var finalStatus = StatusText.Text;
            _busy = false;
            await RefreshAsync();
            StatusText.Text = finalStatus;
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRuntime is { } runtime && Directory.Exists(runtime.JavaHome))
            Process.Start(new ProcessStartInfo(runtime.JavaHome) { UseShellExecute = true });
    }

    private async void Adopt_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || SelectedRuntime is not { } runtime)
            return;
        var answer = MessageBox.Show(
            $"将这个 Java 完整复制到 Java Pilot 管理目录吗？\n\n" +
            $"Java：{runtime.FullVersion}（{runtime.ArchitectureText}）\n" +
            $"来源：{runtime.JavaHome}\n" +
            $"目标：{Path.Combine(_installRoot, $"jdk-{runtime.Major}")}\n\n" +
            "原安装不会被修改或删除。复制完成后会再次启动验证；" +
            "确认新副本可用后，你再决定是否卸载原安装。",
            "复制并纳入 Java Pilot 管理",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;

        _busy = true;
        SetButtons();
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        try
        {
            StatusText.Text =
                $"正在复制并验证 Java {runtime.FullVersion}，原安装保持不变…";
            var adopted = await _inventory.AdoptAsync(
                _installRoot,
                runtime,
                _cancellation.Token);
            StatusText.Text =
                $"已纳入管理：Java {adopted.FullVersion}；{adopted.JavaHome}";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "纳入管理已取消；临时副本正在清理。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"纳入管理失败：{ex.Message}";
            MessageBox.Show(
                ex.ToString(),
                "纳入管理完整报告",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            await RefreshAsync();
        }
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || SelectedRuntime is not { } runtime)
            return;
        var answer = ConfirmRemoval(runtime);
        if (answer != MessageBoxResult.Yes)
            return;

        if (runtime.Ownership == JavaOwnership.RegisteredInstaller)
        {
            try
            {
                _inventory.LaunchRegisteredUninstaller(runtime);
                StatusText.Text =
                    $"已启动 {runtime.ProductName ?? "厂商"} 的正式卸载程序。" +
                    "完成其中的步骤后点击“刷新并验证”。";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"无法启动正式卸载程序：{ex.Message}";
                MessageBox.Show(
                    ex.ToString(),
                    "官方卸载入口报告",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            return;
        }

        _busy = true;
        SetButtons();
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        try
        {
            if (runtime.Ownership == JavaOwnership.JavaPilotManaged)
            {
                StatusText.Text = $"正在安全卸载 Java {runtime.FullVersion}…";
                await _managed.RemoveAsync(
                    _installRoot,
                    new ManagedRuntimeInfo(
                        runtime.Major,
                        runtime.FullVersion,
                        runtime.ProviderText,
                        runtime.JavaHome,
                        runtime.JavaExe,
                        runtime.InstalledAt,
                        runtime.Healthy,
                        runtime.Healthy ? "启动验证通过" : "验证异常"),
                    _cancellation.Token);
                StatusText.Text = $"Java {runtime.FullVersion} 已安全卸载。";
            }
            else
            {
                StatusText.Text =
                    $"正在把外部便携 Java {runtime.FullVersion} 移入回收站…";
                _inventory.MovePortableToRecycleBin(_installRoot, runtime);
                StatusText.Text =
                    $"外部便携 Java {runtime.FullVersion} 已移入回收站，可恢复。";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"卸载未完全完成：{ex.Message}";
            MessageBox.Show(
                ex.ToString(),
                "安全卸载报告",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            await RefreshAsync();
        }
    }

    private static MessageBoxResult ConfirmRemoval(JavaInventoryItem runtime)
    {
        var description = runtime.Ownership switch
        {
            JavaOwnership.JavaPilotManaged =>
                "只会删除带 Java Pilot 标记、且位于受管根目录中的文件夹。",
            JavaOwnership.RegisteredInstaller =>
                "程序不会直接删除文件夹，而会启动 Windows 注册的厂商卸载入口。" +
                "可能出现 UAC 或厂商卸载界面；请确认没有服务端正在使用这个 Java。",
            JavaOwnership.ExternalPortable =>
                "程序仅在确认它是独立 Java 主目录后将整个目录移入回收站。" +
                "Program Files 和 Windows 目录中的未知安装会被拒绝处理。",
            _ => "来源无法确认，程序不会删除它。"
        };
        return MessageBox.Show(
            $"确定处理这个 Java 吗？\n\n" +
            $"Java：{runtime.FullVersion}（{runtime.ArchitectureText}）\n" +
            $"类型：{runtime.OwnershipText}\n" +
            $"目录：{runtime.JavaHome}\n\n{description}",
            runtime.Ownership == JavaOwnership.RegisteredInstaller
                ? "确认启动官方卸载"
                : "确认安全处理",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
    }

    private static string BuildInventorySummary(
        IReadOnlyList<JavaInventoryItem> runtimes)
    {
        var managed = runtimes.Count(item =>
            item.Ownership == JavaOwnership.JavaPilotManaged);
        var registered = runtimes.Count(item =>
            item.Ownership == JavaOwnership.RegisteredInstaller);
        var external = runtimes.Count - managed - registered;
        return $"已合并并验证 {runtimes.Count} 套独立 Java：" +
               $"Java Pilot {managed} 套，Windows 已注册 {registered} 套，" +
               $"其他 {external} 套。32 位 Java 仅供查看和卸载，不会设为 Minecraft 默认。";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024d:0.##} KB";
        return $"{bytes / 1024d / 1024d:0.##} MB";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_busy)
        {
            e.Cancel = true;
            StatusText.Text = "请等待当前验证或卸载操作结束。";
            return;
        }

        _cancellation?.Cancel();
        _cancellation?.Dispose();
    }
}

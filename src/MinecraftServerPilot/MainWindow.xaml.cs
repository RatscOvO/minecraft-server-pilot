using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using MinecraftServerPilot.Models;
using MinecraftServerPilot.Services;

namespace MinecraftServerPilot;

public partial class MainWindow : Window
{
    private readonly InstallerCoordinator _coordinator = new();
    private CancellationTokenSource? _operation;
    private InstallResult? _lastResult;
    private ExistingServerInfo? _existingServer;

    public MainWindow()
    {
        InitializeComponent();
        var preferred = Directory.Exists(@"D:\")
            ? @"D:\"
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        PathTextBox.Text = preferred;
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _operation?.Cancel();
            _coordinator.Dispose();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            var versions = await _coordinator.GetVersionsAsync(timeout.Token);
            VersionCombo.ItemsSource = versions;
            VersionCombo.Text = versions.FirstOrDefault(ServerCatalogService.IsStableReleaseId) ?? "1.21.1";
            StageText.Text = $"版本列表已就绪，共 {versions.Count} 个正式版与快照";
            AppendFriendlyLine("READY", $"版本列表加载完成，共 {versions.Count} 项。");
        }
        catch (Exception ex)
        {
            VersionCombo.Text = "1.21.1";
            StageText.Text = "在线版本列表暂不可用；仍可手动输入版本";
            AppendFriendlyLine("WARN", $"版本列表读取失败：{ex.Message}");
        }
    }

    private async void StartButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_existingServer is not null)
        {
            try
            {
                if (!int.TryParse(MinMemoryTextBox.Text.Trim(), out var min) ||
                    !int.TryParse(MaxMemoryTextBox.Text.Trim(), out var max))
                    throw new ArgumentException("内存必须填写整数 MB。");
                _existingServer = _coordinator.UpdateExistingServer(_existingServer, min, max);
                _coordinator.StartExistingServer(_existingServer);
                StageText.Text = "配置已保存，服务端已在独立控制台启动";
                StatusBadge.Text = "●  已启动";
                AppendFriendlyLine("MANAGER",
                    $"已更新内存为 {min}–{max} MB，并启动 {_existingServer.ServerDirectory}");
                MessageBox.Show(
                    "配置已保存，服务端正在独立控制台窗口中运行。\n\n需要停止时，请在服务端控制台输入 stop，等待保存完成后再关闭窗口。",
                    "服务端管理", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                var dialog = new ErrorReportWindow(ex, _coordinator.Log.FilePath) { Owner = this };
                dialog.ShowDialog();
            }
            return;
        }

        if (_lastResult is not null && StartButton.Content?.ToString() == "打开服务端文件夹")
        {
            OpenPath(_lastResult.ServerDirectory);
            return;
        }

        try
        {
            var request = BuildRequest();
            if (request.Mode == InstallMode.Guided)
            {
                var answer = MessageBox.Show(
                    $"即将创建：{request.MinecraftVersion} / {KindLabel(request.ServerKind)}\n" +
                    $"位置：{request.ParentDirectory}\n内存：{request.MinimumMemoryMb}–{request.MaximumMemoryMb} MB\n\n" +
                    "程序会下载服务端和缺失的便携 Java、写入 eula=true，并进行两次启动验证。继续吗？",
                    "确认安装摘要", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (answer != MessageBoxResult.Yes)
                    return;
            }

            _operation = new CancellationTokenSource();
            ToggleRunning(true);
            LogTextBox.Clear();
            MainProgress.Value = 0;
            StatusBadge.Text = "●  正在执行";
            StatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(255, 198, 88));
            var progress = new Progress<OperationProgress>(UpdateProgress);
            _lastResult = await _coordinator.InstallAsync(
                request, progress, _operation.Token,
                request.Mode == InstallMode.Guided ? ConfirmCheckpointAsync : null);

            StatusBadge.Text = "●  已完成";
            StatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(112, 227, 155));
            StageText.Text = "服务端已通过两次启动验证";
            FooterHint.Text = $"交付目录：{_lastResult.ServerDirectory}";
            StartButton.Content = "打开服务端文件夹";
            StartButton.IsEnabled = true;
            NetworkGuideButton.IsEnabled = true;
            MessageBox.Show(
                $"一切都做好了。\n\n服务端：{_lastResult.ServerDirectory}\n" +
                $"Java：{_lastResult.JavaExe}\n启动：{_lastResult.StartCommand}\n\n" +
                "目录中包含便携配置、启动文件、安装日志和联机指南。",
                "Minecraft Server Pilot", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            StageText.Text = "操作已取消；完整文件和断点数据已保留";
            StatusBadge.Text = "●  已取消";
            AppendFriendlyLine("CANCEL", "用户取消操作。下次创建时，已完成的校验文件可复用。");
        }
        catch (Exception ex)
        {
            StageText.Text = "遇到无法自动恢复的问题";
            StatusBadge.Text = "●  需要处理";
            StatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(255, 123, 114));
            AppendFriendlyLine("ERROR", ex.ToString());
            var dialog = new ErrorReportWindow(ex, _coordinator.Log.FilePath) { Owner = this };
            dialog.ShowDialog();
        }
        finally
        {
            ToggleRunning(false);
            _operation?.Dispose();
            _operation = null;
        }
    }

    private InstallRequest BuildRequest()
    {
        var version = VersionCombo.Text.Trim();
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("请填写 Minecraft 版本，例如 1.7.10。");
        if (KindCombo.SelectedItem is not ComboBoxItem item ||
            !Enum.TryParse<ServerKind>(item.Tag?.ToString(), out var kind))
            throw new ArgumentException("请选择服务端类型。");
        if (!int.TryParse(MinMemoryTextBox.Text.Trim(), out var min) ||
            !int.TryParse(MaxMemoryTextBox.Text.Trim(), out var max))
            throw new ArgumentException("内存必须填写整数 MB，例如 1024 和 4096。");
        return new InstallRequest(version, kind, PathTextBox.Text.Trim(),
            AutomaticModeRadio.IsChecked == true ? InstallMode.Automatic : InstallMode.Guided,
            min, max, ProbeCheckBox.IsChecked == true, KeepRunningCheckBox.IsChecked == true,
            AutoJavaCheckBox.IsChecked == true);
    }

    private void UpdateProgress(OperationProgress update)
    {
        StageText.Text = $"{update.Stage} · {update.Message}";
        if (update.Percent is double percent)
            MainProgress.Value = Math.Clamp(percent, 0, 100);
        AppendFriendlyLine(update.IsWarning ? "WARN" : update.Stage.ToUpperInvariant(), update.Message);
    }

    private async Task<bool> ConfirmCheckpointAsync(
        InstallCheckpoint checkpoint,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Dispatcher.CheckAccess())
        {
            return await Dispatcher.InvokeAsync(
                () => ShowCheckpoint(checkpoint), System.Windows.Threading.DispatcherPriority.Normal, token);
        }
        return ShowCheckpoint(checkpoint);
    }

    private bool ShowCheckpoint(InstallCheckpoint checkpoint)
    {
        var icon = checkpoint.IsSecurityRelevant
            ? MessageBoxImage.Warning
            : MessageBoxImage.Information;
        var answer = MessageBox.Show(
            $"{checkpoint.Message}\n\n选择“是”继续；选择“否”会安全暂停，已完成下载会保留供下次续作。",
            $"{checkpoint.Stage} · {checkpoint.Title}",
            MessageBoxButton.YesNo, icon);
        return answer == MessageBoxResult.Yes;
    }

    private void ToggleRunning(bool running)
    {
        StartButton.IsEnabled = !running;
        RefreshSecondaryAction(running);
        var creating = _existingServer is null;
        VersionCombo.IsEnabled = !running && creating;
        KindCombo.IsEnabled = !running && creating;
        PathTextBox.IsEnabled = !running && creating;
        AutomaticModeRadio.IsEnabled = !running && creating;
        GuidedModeRadio.IsEnabled = !running && creating;
        ProbeCheckBox.IsEnabled = !running && creating;
        KeepRunningCheckBox.IsEnabled = !running && creating;
        AutoJavaCheckBox.IsEnabled = !running && creating;
        ManageExistingButton.IsEnabled = !running;
        PropertiesButton.IsEnabled =
            !running && (_existingServer is not null || _lastResult is not null);
    }

    private void RefreshSecondaryAction(bool running)
    {
        if (running)
        {
            CancelButton.Content = "取消安装";
            CancelButton.Visibility = Visibility.Visible;
            CancelButton.IsEnabled = true;
            return;
        }

        if (_existingServer is not null)
        {
            CancelButton.Content = "返回新建";
            CancelButton.Visibility = Visibility.Visible;
            CancelButton.IsEnabled = true;
            return;
        }

        if (_lastResult is not null)
        {
            CancelButton.Content = "新建另一个";
            CancelButton.Visibility = Visibility.Visible;
            CancelButton.IsEnabled = true;
            return;
        }

        CancelButton.Visibility = Visibility.Collapsed;
        CancelButton.IsEnabled = false;
    }

    private void BrowseButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择服务端父目录",
            InitialDirectory = Directory.Exists(PathTextBox.Text) ? PathTextBox.Text : null,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
            PathTextBox.Text = dialog.FolderName;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_operation is not null)
        {
            CancelButton.Content = "正在取消…";
            CancelButton.IsEnabled = false;
            StageText.Text = "正在安全取消，请稍候…";
            _operation.Cancel();
            return;
        }

        if (_existingServer is not null)
        {
            ExitExistingServerMode();
            return;
        }

        if (_lastResult is not null)
            ResetForNewInstall();
    }

    private void OpenLogButton_OnClick(object sender, RoutedEventArgs e) =>
        OpenPath(_coordinator.Log.FilePath);

    private void ManageExistingButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_existingServer is not null)
        {
            ExitExistingServerMode();
            return;
        }

        try
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择包含 server-pilot.json 的服务端目录",
                InitialDirectory = Directory.Exists(PathTextBox.Text) ? PathTextBox.Text : null,
                Multiselect = false
            };
            if (dialog.ShowDialog(this) != true)
                return;
            var existing = _coordinator.LoadExistingServer(dialog.FolderName);
            _existingServer = existing;
            _lastResult = null;
            VersionCombo.Text = existing.MinecraftVersion;
            SelectKind(existing.ServerKind);
            PathTextBox.Text = existing.ServerDirectory;
            MinMemoryTextBox.Text = existing.MinimumMemoryMb.ToString();
            MaxMemoryTextBox.Text = existing.MaximumMemoryMb.ToString();
            StartButton.Content = "保存配置并启动";
            ManageExistingButton.Content = "返回创建模式";
            NetworkGuideButton.IsEnabled =
                File.Exists(Path.Combine(existing.ServerDirectory, "NETWORK-GUIDE.txt"));
            StageText.Text =
                $"正在管理 {existing.Distribution} · Java {existing.JavaMajor} · 端口 {existing.ServerPort}";
            CompatibilityText.Text =
                "管理模式只会更新内存和启动文件，不重新下载或覆盖世界。正式停服请在控制台输入 stop。";
            ToggleRunning(false);
            AppendFriendlyLine("MANAGER", $"已载入：{existing.ServerDirectory}");
        }
        catch (Exception ex)
        {
            var error = new ErrorReportWindow(ex, _coordinator.Log.FilePath) { Owner = this };
            error.ShowDialog();
        }
    }

    private void ExitExistingServerMode()
    {
        _existingServer = null;
        _lastResult = null;
        StartButton.Content = "开始创建服务端";
        ManageExistingButton.Content = "管理已有服务端";
        NetworkGuideButton.IsEnabled = false;
        StageText.Text = "已返回新建服务端模式";
        ToggleRunning(false);
        KindCombo_OnSelectionChanged(KindCombo, null!);
    }

    private void ResetForNewInstall()
    {
        _lastResult = null;
        StartButton.Content = "开始创建服务端";
        NetworkGuideButton.IsEnabled = false;
        MainProgress.Value = 0;
        StatusBadge.Text = "●  准备就绪";
        StatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(112, 227, 155));
        StageText.Text = "可继续创建新的服务端";
        FooterHint.Text = "下载失败会自动换源；所有技术细节都会写入日志。";
        ToggleRunning(false);
        AppendFriendlyLine("READY", "已返回新建服务端状态。");
    }

    private void NetworkGuideButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var existing = _existingServer;
            if (existing is null && _lastResult is not null)
                existing = _coordinator.LoadExistingServer(_lastResult.ServerDirectory);
            if (existing is null)
                return;
            new NetworkGuideWindow(existing, _coordinator.Log) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            new ErrorReportWindow(ex, _coordinator.Log.FilePath) { Owner = this }.ShowDialog();
        }
    }

    private void PropertiesButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var existing = _existingServer;
            if (existing is null && _lastResult is not null)
                existing = _coordinator.LoadExistingServer(_lastResult.ServerDirectory);
            if (existing is null)
                return;

            var window = new ServerPropertiesWindow(existing, _coordinator) { Owner = this };
            if (window.ShowDialog() == true)
            {
                if (_existingServer is not null)
                    _existingServer = window.UpdatedServer;
                StageText.Text = "server.properties 已保存；设置将在下次启动时生效";
                FooterHint.Text =
                    $"属性文件：{Path.Combine(existing.ServerDirectory, "server.properties")}";
                AppendFriendlyLine("PROPERTIES", "常用服务端设置已通过中文面板保存。");
            }
        }
        catch (Exception ex)
        {
            new ErrorReportWindow(ex, _coordinator.Log.FilePath) { Owner = this }.ShowDialog();
        }
    }

    private void SelectKind(ServerKind kind)
    {
        foreach (var candidate in KindCombo.Items.OfType<ComboBoxItem>())
        {
            if (candidate.Tag?.ToString() == kind.ToString())
            {
                KindCombo.SelectedItem = candidate;
                return;
            }
        }
    }

    private void KindCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CompatibilityText is null || KindCombo.SelectedItem is not ComboBoxItem item)
            return;
        if (Enum.TryParse<ServerKind>(item.Tag?.ToString(), out var kind))
            CompatibilityText.Text =
                ServerCatalogService.DescribeCompatibility(VersionCombo?.Text?.Trim() ?? "", kind);
    }

    private void VersionCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RefreshCompatibilitySummary();

    private void VersionCombo_OnLostKeyboardFocus(
        object sender,
        System.Windows.Input.KeyboardFocusChangedEventArgs e) =>
        RefreshCompatibilitySummary();

    private void RefreshCompatibilitySummary()
    {
        if (CompatibilityText is null || KindCombo?.SelectedItem is not ComboBoxItem item ||
            !Enum.TryParse<ServerKind>(item.Tag?.ToString(), out var kind))
            return;
        CompatibilityText.Text =
            ServerCatalogService.DescribeCompatibility(VersionCombo.Text.Trim(), kind);
    }

    private void OnLogLine(string line)
    {
        Dispatcher.BeginInvoke(() =>
        {
            LogTextBox.AppendText(line + Environment.NewLine);
            LogTextBox.ScrollToEnd();
        });
    }

    private void AppendFriendlyLine(string area, string message)
    {
        const int maximumCharacters = 500_000;
        const int trimToCharacters = 400_000;
        if (LogTextBox.Text.Length > maximumCharacters)
        {
            LogTextBox.Text =
                "—— 屏幕日志已截断；完整内容仍保存在持久化日志文件中 ——" +
                Environment.NewLine +
                LogTextBox.Text[^trimToCharacters..];
            LogTextBox.CaretIndex = LogTextBox.Text.Length;
        }
        LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] [{area}] {message}{Environment.NewLine}");
        LogTextBox.ScrollToEnd();
    }

    private static string KindLabel(ServerKind kind) => kind switch
    {
        ServerKind.Vanilla => "原版 Vanilla",
        ServerKind.Paper => "插件服 Paper",
        ServerKind.Fabric => "模组服 Fabric",
        ServerKind.Forge => "模组服 Forge",
        _ => kind.ToString()
    };

    private static void OpenPath(string path)
    {
        try
        {
            var target = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开：{path}\n\n{ex.Message}", "打开失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

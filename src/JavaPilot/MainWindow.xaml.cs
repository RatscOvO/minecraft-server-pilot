using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Media;
using JavaPilot.Models;
using JavaPilot.Services;
using Microsoft.Win32;

namespace JavaPilot;

public partial class MainWindow
{
    private readonly AppLog _log = new();
    private readonly JavaInstallerService _installer;
    private readonly MinecraftRecommendationService _recommendations;
    private readonly AppSettingsService _settingsService;
    private readonly DefaultJavaEnvironmentService _defaultJavaEnvironment;
    private JavaPilotSettings _settings = JavaPilotSettings.CreateDefault();
    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _discoveryCancellation;
    private CancellationTokenSource? _catalogCancellation;
    private InstallResult? _lastResult;
    private bool _isRunning;
    private bool _closeWhenStopped;

    public MainWindow()
    {
        InitializeComponent();
        _installer = new JavaInstallerService(_log);
        _recommendations = new MinecraftRecommendationService(_log);
        _settingsService = new AppSettingsService(_log);
        _defaultJavaEnvironment = new DefaultJavaEnvironmentService(_log);
        _log.LineWritten += AppendLogLine;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = _settingsService.Load();
        JavaVersionList.ItemsSource = JavaCatalog.Options;
        JavaVersionList.SelectedItem =
            JavaCatalog.Options.FirstOrDefault(option =>
                option.Major == _settings.SelectedJavaMajor) ??
            JavaCatalog.Get(21);
        InstallRootTextBox.Text = _settings.InstallRoot;
        ReuseSystemJavaCheckBox.IsChecked = _settings.ReuseSystemJava;
        SetDefaultJavaCheckBox.IsChecked = _settings.SetDefaultJava;
        ForceReinstallCheckBox.IsChecked = _settings.ForceReinstall;
        InstallRootTextBox.TextChanged += (_, _) => UpdateResolvedTarget();
        UpdateResolvedTarget();
        _log.Info("UI", "界面就绪。用户选定版本和目录后，安装过程无需再次确认。");
        FooterStatusText.Text = $"完整日志：{_log.FilePath}";
        await Task.WhenAll(
            RefreshDetectedJavaAsync(forceRefresh: false),
            RefreshMinecraftRecommendationsAsync());
    }

    private JavaOption? SelectedOption => JavaVersionList.SelectedItem as JavaOption;

    private void JavaVersionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedOption is not { } option)
            return;

        CompatibilityNoticeText.Text = option.IsLegacy
            ? $"安全提醒：Java {option.Major} 已停止公开安全维护，仅用于旧版兼容。" +
              "不要用它运行暴露到公网的服务端。选择它并点击安装即表示你知晓这一限制。"
            : $"{option.Recommendation}。程序将按 Eclipse Temurin、Azul Zulu、" +
              "BellSoft Liberica、Amazon Corretto 的可用情况自动切换，并在部署前验证真实版本。";
        CompatibilityNotice.Background = option.IsLegacy
            ? Brush(255, 246, 229)
            : Brush(234, 244, 251);
        CompatibilityNotice.BorderBrush = option.IsLegacy
            ? Brush(232, 201, 135)
            : Brush(188, 216, 234);
        CompatibilityNoticeText.Foreground = option.IsLegacy
            ? Brush(138, 93, 18)
            : Brush(53, 110, 146);
        UpdateResolvedTarget();
    }

    private void UpdateResolvedTarget()
    {
        if (ResolvedTargetText is null || SelectedOption is null)
            return;
        try
        {
            var root = InstallRootTextBox.Text.Trim();
            ResolvedTargetText.Text = string.IsNullOrWhiteSpace(root)
                ? "请选择目录。"
                : $"最终目录：{Path.Combine(root, $"jdk-{SelectedOption.Major}")}";
        }
        catch
        {
            ResolvedTargetText.Text = "当前路径格式无效。";
        }
    }

    private void BrowseInstallRoot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择便携 Java 安装根目录",
            Multiselect = false
        };
        if (Directory.Exists(InstallRootTextBox.Text))
            dialog.InitialDirectory = InstallRootTextBox.Text;
        if (dialog.ShowDialog(this) == true)
            InstallRootTextBox.Text = dialog.FolderName;
    }

    private async void RefreshJava_Click(object sender, RoutedEventArgs e) =>
        await RefreshDetectedJavaAsync(forceRefresh: true);

    private async void ManageRuntimes_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
            return;

        var root = InstallRootTextBox.Text.Trim();
        try
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
                throw new ArgumentException("请先选择完整的便携 Java 安装根目录。");
            SaveSettings();
            var window = new RuntimeManagerWindow(Path.GetFullPath(root), _log)
            {
                Owner = this
            };
            window.ShowDialog();
            await RefreshDetectedJavaAsync(forceRefresh: true);
        }
        catch (Exception ex)
        {
            _log.Error("MANAGER", "无法打开 Java 管理器。", ex);
            StageText.Text = $"无法打开管理器：{ex.Message}";
        }
    }

    private async Task RefreshDetectedJavaAsync(bool forceRefresh)
    {
        _discoveryCancellation?.Cancel();
        _discoveryCancellation?.Dispose();
        _discoveryCancellation = new CancellationTokenSource();
        RefreshJavaButton.IsEnabled = false;
        DefaultJavaSummaryText.Text = "正在读取环境变量与 PATH…";
        DefaultJavaDetailText.Text = "";
        DetectedJavaText.Text = "本机库存正在扫描…";
        try
        {
            var installedTask = _installer.DiscoverAsync(
                InstallRootTextBox.Text.Trim(),
                _discoveryCancellation.Token,
                forceRefresh);
            var environmentTask = _defaultJavaEnvironment.InspectAsync(
                _discoveryCancellation.Token);
            await Task.WhenAll(installedTask, environmentTask);
            var installed = await installedTask;
            ApplyDefaultJavaSnapshot(await environmentTask);
            DetectedJavaText.Text = installed.Count == 0
                ? "本机库存：没有发现其他可用的 64 位 Java；安装时会自动下载。"
                : "本机库存：" + string.Join(
                    " · ",
                    installed
                        .GroupBy(item => item.Major)
                        .OrderByDescending(group => group.Key)
                        .Select(group => $"Java {group.Key}（{group.Count()}个）"));
        }
        catch (OperationCanceledException)
        {
            DefaultJavaSummaryText.Text = "默认 Java 检查已取消";
            DetectedJavaText.Text = "扫描已取消。";
        }
        catch (Exception ex)
        {
            _log.Warn("DISCOVERY", $"本机 Java 扫描失败：{ex.Message}");
            DefaultJavaSummaryText.Text = "默认 Java 读取失败";
            DefaultJavaDetailText.Text = ex.Message;
            ApplyDefaultJavaCardState(DefaultJavaCardState.Warning);
            DetectedJavaText.Text = "扫描失败；不影响自动下载，详细原因已写入日志。";
        }
        finally
        {
            if (!_isRunning)
                RefreshJavaButton.IsEnabled = true;
        }
    }

    private void ApplyDefaultJavaSnapshot(DefaultJavaEnvironmentSnapshot snapshot)
    {
        var path = snapshot.PathDefault;
        var javaHome = snapshot.JavaHome;
        var details = new List<string>();
        var savedUserEnvironmentPending =
            !string.IsNullOrWhiteSpace(snapshot.UserJavaHome) &&
            !EnvironmentValueEquals(
                snapshot.UserJavaHome,
                snapshot.ProcessJavaHome);

        if (path?.Installation is { } pathRuntime)
        {
            DefaultJavaSummaryText.Text =
                $"命令行默认：Java {pathRuntime.FullVersion} · " +
                (pathRuntime.Is64Bit ? "64 位" : "32 位");
            details.Add($"PATH → {pathRuntime.JavaExe}");
        }
        else if (path is not null)
        {
            DefaultJavaSummaryText.Text = "PATH 中的默认 Java 无法使用";
            details.Add($"PATH → {path.JavaExe ?? path.ConfiguredValue}（{path.Error}）");
        }
        else
        {
            DefaultJavaSummaryText.Text = "PATH 中没有可直接调用的 Java";
            details.Add("在终端输入 java 时，Windows 当前找不到 java.exe");
        }

        if (javaHome?.Installation is { } homeRuntime)
        {
            if (path?.Installation is { } commandRuntime &&
                !snapshot.IsConsistent)
            {
                DefaultJavaSummaryText.Text =
                    $"默认 Java 冲突：命令行 {commandRuntime.FullVersion} / " +
                    $"JAVA_HOME {homeRuntime.FullVersion}";
            }
            details.Add(
                $"JAVA_HOME（{snapshot.JavaHomeScope}）→ " +
                $"Java {homeRuntime.FullVersion} · {homeRuntime.JavaHome}");
        }
        else if (!string.IsNullOrWhiteSpace(snapshot.ProcessJavaHome))
        {
            details.Add(
                $"JAVA_HOME（{snapshot.JavaHomeScope}）→ " +
                $"{snapshot.ProcessJavaHome}（{javaHome?.Error ?? "无效"}）");
        }
        else
        {
            details.Add("JAVA_HOME → 未设置");
        }

        if (savedUserEnvironmentPending)
        {
            details.Add(
                $"已保存的用户 JAVA_HOME → {snapshot.UserJavaHome} " +
                "（当前进程尚未继承）");
            if (path?.Installation is { } activeRuntime &&
                javaHome?.Installation is not null &&
                snapshot.IsConsistent)
            {
                DefaultJavaSummaryText.Text =
                    $"环境尚未同步：当前 {activeRuntime.FullVersion} / " +
                    "用户已保存其他 JAVA_HOME";
            }
        }

        if (path?.Installation is not null && javaHome?.Installation is not null)
        {
            details.Add(snapshot.IsConsistent
                ? "当前进程内的 PATH 与 JAVA_HOME 指向同一套 Java。"
                : "注意：PATH 与 JAVA_HOME 不一致；直接输入 java 时以 PATH 为准。" +
                  "可在“管理”中选择目标 Java 并点击“设为默认 Java”完成优先级修复。");
        }

        DefaultJavaDetailText.Text = string.Join(Environment.NewLine, details);
        ApplyDefaultJavaCardState(
            snapshot.HasBrokenConfiguration ||
            savedUserEnvironmentPending ||
            (path?.Installation is not null &&
             javaHome?.Installation is not null &&
             !snapshot.IsConsistent)
                ? DefaultJavaCardState.Warning
                : snapshot.HasAnyConfiguration
                    ? DefaultJavaCardState.Healthy
                    : DefaultJavaCardState.Neutral);
    }

    private void ApplyDefaultJavaCardState(DefaultJavaCardState state)
    {
        switch (state)
        {
            case DefaultJavaCardState.Healthy:
                DefaultJavaStateBorder.Background = Brush(237, 247, 242);
                DefaultJavaStateBorder.BorderBrush = Brush(184, 220, 200);
                DefaultJavaSummaryText.Foreground = Brush(36, 123, 82);
                break;
            case DefaultJavaCardState.Warning:
                DefaultJavaStateBorder.Background = Brush(255, 247, 231);
                DefaultJavaStateBorder.BorderBrush = Brush(231, 202, 143);
                DefaultJavaSummaryText.Foreground = Brush(151, 99, 12);
                break;
            default:
                DefaultJavaStateBorder.Background = Brush(241, 246, 248);
                DefaultJavaStateBorder.BorderBrush = Brush(202, 216, 224);
                DefaultJavaSummaryText.Foreground = Brush(83, 105, 120);
                break;
        }
    }

    private async Task RefreshMinecraftRecommendationsAsync()
    {
        _catalogCancellation?.Cancel();
        _catalogCancellation?.Dispose();
        _catalogCancellation = new CancellationTokenSource();
        var selectedMajor = SelectedOption?.Major ?? 21;
        try
        {
            var options = await _recommendations.GetOptionsAsync(
                _catalogCancellation.Token);
            JavaVersionList.ItemsSource = options;
            JavaVersionList.SelectedItem =
                options.FirstOrDefault(option => option.Major == selectedMajor) ??
                options.First();
        }
        catch (OperationCanceledException)
        {
            // 窗口关闭或重新加载时无需提示。
        }
        catch (Exception ex)
        {
            _log.Warn("CATALOG", $"Minecraft 推荐范围刷新失败：{ex.Message}");
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning || SelectedOption is not { } option)
            return;

        var request = new InstallRequest(
            option.Major,
            InstallRootTextBox.Text.Trim(),
            SetDefaultJavaCheckBox.IsChecked == true,
            ReuseSystemJavaCheckBox.IsChecked == true,
            ForceReinstallCheckBox.IsChecked == true);
        SaveSettings();
        await RunInstallOperationAsync(
            option,
            "自动下载安装",
            (progress, token) => _installer.InstallAsync(request, progress, token));
    }

    private async void ImportArchive_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning || SelectedOption is not { } option)
            return;

        var dialog = new OpenFileDialog
        {
            Title = $"选择 Java {option.Major} 的 Windows x64 JDK ZIP",
            Filter = "JDK ZIP 压缩包 (*.zip)|*.zip",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
            return;

        var request = new InstallRequest(
            option.Major,
            InstallRootTextBox.Text.Trim(),
            SetDefaultJavaCheckBox.IsChecked == true,
            ReuseSystemJava: false,
            ForceReinstall: true);
        SaveSettings();
        await RunInstallOperationAsync(
            option,
            "本地 JDK ZIP 导入",
            (progress, token) =>
                _installer.InstallFromArchiveAsync(request, dialog.FileName, progress, token));
    }

    private async Task RunInstallOperationAsync(
        JavaOption option,
        string operationName,
        Func<IProgress<OperationProgress>, CancellationToken, Task<InstallResult>> operation)
    {
        _discoveryCancellation?.Cancel();
        _operationCancellation = new CancellationTokenSource();
        SetRunning(true);
        _lastResult = null;
        OpenInstallFolderButton.IsEnabled = false;
        InstallProgress.IsIndeterminate = true;
        StageText.Text = $"准备安装 Java {option.Major}…";
        HeaderStatusText.Text = "● 正在执行";
        HeaderStatusText.Foreground = Brush(154, 105, 10);
        HeaderStatusBorder.Background = Brush(255, 241, 214);
        FooterStatusText.Text = "全自动流程已启动，可以去喝一杯咖啡了，我们马上就好。";
        _log.Info(
            "INSTALL",
            $"用户开始{operationName} Java {option.Major}; Root={InstallRootTextBox.Text.Trim()}; " +
            $"SetDefault={SetDefaultJavaCheckBox.IsChecked == true}");

        var progress = new Progress<OperationProgress>(UpdateProgress);
        try
        {
            _lastResult = await operation(progress, _operationCancellation.Token);
            InstallProgress.IsIndeterminate = false;
            InstallProgress.Value = 100;
            HeaderStatusText.Text = "● 安装完成";
            HeaderStatusText.Foreground = Brush(36, 123, 82);
            HeaderStatusBorder.Background = Brush(227, 244, 234);
            StageText.Text =
                $"Java {_lastResult.FullVersion} · {_lastResult.Provider} · " +
                (_lastResult.Reused ? "已复用" : "全新安装");
            FooterStatusText.Text =
                $"Java 已通过双重启动验证：{_lastResult.JavaExe}";
            OpenInstallFolderButton.IsEnabled = true;
            await RefreshDetectedJavaAsync(forceRefresh: true);
        }
        catch (OperationCanceledException)
        {
            _log.Warn("INSTALL", "用户取消了安装。断点下载文件已保留，重新开始可继续。");
            InstallProgress.IsIndeterminate = false;
            HeaderStatusText.Text = "● 已安全取消";
            HeaderStatusText.Foreground = Brush(88, 111, 127);
            HeaderStatusBorder.Background = Brush(235, 241, 244);
            StageText.Text = "安装已取消；断点文件保留，点击开始即可继续。";
            FooterStatusText.Text = $"取消操作已写入日志：{_log.FilePath}";
        }
        catch (Exception ex)
        {
            _log.Error("INSTALL", "Java 自动安装最终失败。", ex);
            InstallProgress.IsIndeterminate = false;
            HeaderStatusText.Text = "● 需要处理";
            HeaderStatusText.Foreground = Brush(166, 59, 70);
            HeaderStatusBorder.Background = Brush(251, 234, 236);
            StageText.Text = $"安装失败：{ex.Message}";
            FooterStatusText.Text = $"完整异常已显示并写入：{_log.FilePath}";
            AppendLogLine(
                $"{DateTimeOffset.Now:O} [ERROR REPORT]\n{ex}\n" +
                "建议直接点击“打开日志”提供完整文件，不要只截取最后一行。");
        }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
            SetRunning(false);
            if (_closeWhenStopped)
                Close();
        }
    }

    private void UpdateProgress(OperationProgress progress)
    {
        StageText.Text = $"{progress.Stage}：{progress.Message}";
        if (progress.Percent is double value)
        {
            InstallProgress.IsIndeterminate = false;
            InstallProgress.Value = Math.Clamp(value, 0, 100);
        }
        else if (progress.Stage is "解析来源" or "自动恢复")
        {
            InstallProgress.IsIndeterminate = true;
        }

        if (progress.IsWarning)
            _log.Warn(progress.Stage, progress.Message);
        else
            _log.Info(progress.Stage, progress.Message);
    }

    private void CancelInstall_Click(object sender, RoutedEventArgs e)
    {
        if (!_isRunning || _operationCancellation is null)
            return;
        CancelButton.IsEnabled = false;
        StageText.Text = "正在安全取消：等待当前文件操作结束…";
        FooterStatusText.Text = "正在停止网络读取并清理临时解压目录；已下载断点会保留。";
        _operationCancellation.Cancel();
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e) =>
        OpenWithShell(_log.FilePath);

    private void OpenInstallFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResult is not null)
            OpenWithShell(_lastResult.JavaHome);
    }

    private static void OpenWithShell(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void SetRunning(bool running)
    {
        _isRunning = running;
        JavaVersionList.IsEnabled = !running;
        InstallRootTextBox.IsEnabled = !running;
        SetDefaultJavaCheckBox.IsEnabled = !running;
        ReuseSystemJavaCheckBox.IsEnabled = !running;
        ForceReinstallCheckBox.IsEnabled = !running;
        RefreshJavaButton.IsEnabled = !running;
        ManageRuntimesButton.IsEnabled = !running;
        ImportArchiveButton.IsEnabled = !running;
        InstallButton.IsEnabled = !running;
        CancelButton.IsEnabled = running;
    }

    private void AppendLogLine(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AppendLogLine(line));
            return;
        }

        const int maximumCharacters = 450_000;
        if (LogTextBox.Text.Length > maximumCharacters)
            LogTextBox.Text = LogTextBox.Text[^300_000..];
        LogTextBox.AppendText(line + Environment.NewLine);
        LogTextBox.ScrollToEnd();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isRunning)
        {
            e.Cancel = true;
            _closeWhenStopped = true;
            FooterStatusText.Text = "正在安全取消安装，完成临时文件清理后窗口会自动关闭。";
            _operationCancellation?.Cancel();
            return;
        }

        SaveSettings();
        _installer.Dispose();
        _recommendations.Dispose();
        _discoveryCancellation?.Cancel();
        _discoveryCancellation?.Dispose();
        _catalogCancellation?.Cancel();
        _catalogCancellation?.Dispose();
        _log.Dispose();
    }

    private void SaveSettings()
    {
        _settings.InstallRoot = InstallRootTextBox.Text.Trim();
        _settings.ReuseSystemJava = ReuseSystemJavaCheckBox.IsChecked == true;
        _settings.SetDefaultJava = SetDefaultJavaCheckBox.IsChecked == true;
        _settings.ForceReinstall = ForceReinstallCheckBox.IsChecked == true;
        _settings.SelectedJavaMajor = SelectedOption?.Major ?? 21;
        _settingsService.Save(_settings);
    }

    private static SolidColorBrush Brush(byte red, byte green, byte blue) =>
        new(Color.FromRgb(red, green, blue));

    private static bool EnvironmentValueEquals(string? left, string? right)
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
            return left.Trim().Equals(right.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    private enum DefaultJavaCardState
    {
        Neutral,
        Healthy,
        Warning
    }
}

using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MinecraftServerPilot.Models;
using MinecraftServerPilot.Services;

namespace MinecraftServerPilot;

public partial class ServerPropertiesWindow : Window
{
    private readonly InstallerCoordinator _coordinator;
    private readonly Dictionary<string, FrameworkElement> _editors =
        new(StringComparer.OrdinalIgnoreCase);
    private ExistingServerInfo _server;
    private ServerPropertiesSnapshot _snapshot = null!;

    public ExistingServerInfo UpdatedServer => _server;

    public ServerPropertiesWindow(
        ExistingServerInfo server,
        InstallerCoordinator coordinator)
    {
        InitializeComponent();
        _server = server;
        _coordinator = coordinator;
        LoadSnapshot();
    }

    private void LoadSnapshot()
    {
        _snapshot = _coordinator.LoadServerProperties(_server);
        ServerSummaryText.Text =
            $"{_server.Distribution} · Minecraft {_server.MinecraftVersion} · {_snapshot.FilePath}";
        CompatibilitySummaryText.Text = _snapshot.UnavailableKnownSettings.Count == 0
            ? $"当前版本可安全管理 {_snapshot.Values.Count} 项常用设置；其他未知字段会原样保留。"
            : $"当前版本可安全管理 {_snapshot.Values.Count} 项。没有强行添加当前文件未提供的旧版字段：" +
              string.Join("、", _snapshot.UnavailableKnownSettings) +
              "。其他未知字段会原样保留。";
        FooterText.Text = "保存前会创建 server.properties.pilot-backup，并使用原子替换写入。";
        BuildEditors();
    }

    private void BuildEditors()
    {
        EditorsPanel.Children.Clear();
        _editors.Clear();
        foreach (var group in _snapshot.Values.GroupBy(value => value.Definition.Category))
        {
            EditorsPanel.Children.Add(new TextBlock
            {
                Text = group.Key,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, EditorsPanel.Children.Count == 0 ? 0 : 18, 0, 8)
            });
            foreach (var property in group)
                EditorsPanel.Children.Add(BuildPropertyRow(property));
        }
    }

    private FrameworkElement BuildPropertyRow(ServerPropertyValue property)
    {
        var definition = property.Definition;
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new StackPanel();
        label.Children.Add(new TextBlock
        {
            Text = definition.ChineseName,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13
        });
        label.Children.Add(new TextBlock
        {
            Text = definition.Key + (property.ExistedInFile ? "" : " · 使用版本默认值"),
            Foreground = (Brush)FindResource("MutedBrush"),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 10,
            Margin = new Thickness(0, 2, 0, 0)
        });
        label.Children.Add(new TextBlock
        {
            Text = definition.Description,
            Foreground = (Brush)FindResource("MutedBrush"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });
        row.Children.Add(label);

        var editor = CreateEditor(property);
        Grid.SetColumn(editor, 2);
        row.Children.Add(editor);
        _editors[definition.Key] = editor;

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(13, 17, 23)),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(13),
            Margin = new Thickness(0, 0, 0, 8),
            Child = row
        };
    }

    private static FrameworkElement CreateEditor(ServerPropertyValue property)
    {
        var definition = property.Definition;
        switch (definition.EditorKind)
        {
            case ServerPropertyEditorKind.Boolean:
                return new CheckBox
                {
                    Content = definition.SecuritySensitive
                        ? "启用（请仔细确认）"
                        : "启用",
                    IsChecked = bool.TryParse(property.Value, out var enabled) && enabled,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Left
                };

            case ServerPropertyEditorKind.Choice:
                var combo = new ComboBox
                {
                    ItemsSource = definition.Choices,
                    DisplayMemberPath = nameof(ServerPropertyChoice.Label),
                    SelectedValuePath = nameof(ServerPropertyChoice.Value),
                    SelectedValue = property.Value,
                    Height = 36,
                    MinWidth = 190
                };
                if (combo.SelectedIndex < 0)
                    combo.SelectedIndex = 0;
                return combo;

            default:
                return new TextBox
                {
                    Text = property.Value,
                    MinWidth = 190,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    ToolTip = definition.EditorKind == ServerPropertyEditorKind.Integer
                        ? $"允许范围：{definition.Minimum}–{definition.Maximum}"
                        : null
                };
        }
    }

    private Dictionary<string, string> ReadEditorValues()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in _snapshot.Values)
        {
            var key = property.Definition.Key;
            values[key] = _editors[key] switch
            {
                CheckBox checkBox => checkBox.IsChecked == true ? "true" : "false",
                ComboBox comboBox => comboBox.SelectedValue?.ToString() ?? "",
                TextBox textBox => textBox.Text,
                _ => throw new InvalidOperationException($"不认识的设置控件：{key}")
            };
        }
        return values;
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var values = ReadEditorValues();
            var oldOnlineMode = _snapshot.Values.FirstOrDefault(value =>
                value.Definition.Key.Equals("online-mode", StringComparison.OrdinalIgnoreCase))?.Value;
            if (oldOnlineMode?.Equals("true", StringComparison.OrdinalIgnoreCase) == true &&
                values.TryGetValue("online-mode", out var newOnlineMode) &&
                newOnlineMode == "false")
            {
                var answer = MessageBox.Show(
                    "你正在关闭正版验证（online-mode=false）。\n\n" +
                    "这会允许玩家伪造名称和身份；白名单也不能完全消除风险，玩家 UUID 与背包数据还可能变化。" +
                    "只应在隔离且完全可信的环境中使用。\n\n仍要保存吗？",
                    "高风险设置确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes)
                    return;
            }

            var listening = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Any(endpoint => endpoint.Port == _server.ServerPort);
            if (listening)
            {
                var answer = MessageBox.Show(
                    $"端口 {_server.ServerPort} 当前正在监听，服务端可能仍在运行。" +
                    "运行中的服务端不会立即应用这些设置，停止时还可能覆盖文件。\n\n" +
                    "推荐先在控制台输入 stop。仍要现在保存吗？",
                    "服务端可能正在运行", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes)
                    return;
            }

            _server = _coordinator.SaveServerProperties(_server, _snapshot, values);
            MessageBox.Show(
                "设置已经保存，并创建了最近备份。\n\n更改会在下次启动服务端时生效。",
                "服务端设置", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            new ErrorReportWindow(ex, _coordinator.Log.FilePath) { Owner = this }.ShowDialog();
        }
    }

    private void ReloadButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            LoadSnapshot();
        }
        catch (Exception ex)
        {
            new ErrorReportWindow(ex, _coordinator.Log.FilePath) { Owner = this }.ShowDialog();
        }
    }

    private void OpenRawButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_snapshot.FilePath}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            new ErrorReportWindow(ex, _coordinator.Log.FilePath) { Owner = this }.ShowDialog();
        }
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e) => Close();
}

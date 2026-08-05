using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using MinecraftServerPilot.Models;
using MinecraftServerPilot.Services;

namespace MinecraftServerPilot;

public partial class NetworkGuideWindow : Window
{
    private readonly ExistingServerInfo _server;
    private readonly AppLog _log;
    private string[] _addresses = [];

    public NetworkGuideWindow(ExistingServerInfo server, AppLog log)
    {
        InitializeComponent();
        _server = server;
        _log = log;
        GuideTextBox.Text = BuildGuide(server);
        RefreshNetworkState();
    }

    private void RefreshNetworkState()
    {
        try
        {
            _addresses = Dns.GetHostEntry(Dns.GetHostName()).AddressList
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork &&
                                  !IPAddress.IsLoopback(address))
                .Select(address => $"{address}:{_server.ServerPort}")
                .Distinct()
                .ToArray();
        }
        catch
        {
            _addresses = [];
        }
        AddressText.Text = _addresses.Length == 0
            ? $"未找到局域网 IPv4；端口 {_server.ServerPort}"
            : string.Join("  /  ", _addresses);
        var listening = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Any(endpoint => endpoint.Port == _server.ServerPort);
        PortStatusText.Text = listening ? $"● {_server.ServerPort} 正在监听" : $"○ {_server.ServerPort} 尚未监听";
        PortStatusText.Foreground = listening
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(112, 227, 155))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 198, 88));
        SafetyText.Text = listening
            ? "服务端端口已在本机监听。先让同一 Wi-Fi 的朋友使用上方地址测试；公网用户还需要端口映射或隧道。"
            : "端口当前没有监听。请先启动服务端并等待控制台出现 Done，再刷新检测；防火墙规则不能代替服务端启动。";
    }

    private static string BuildGuide(ExistingServerInfo server) =>
        $"服务端：{server.Distribution}\r\n" +
        $"版本：{server.MinecraftVersion}\r\n" +
        $"端口：{server.ServerPort} / TCP\r\n\r\n" +
        "推荐排查顺序\r\n" +
        "1. 本机用 localhost 进入，确认服务端与客户端版本一致。\r\n" +
        "2. 同一 Wi-Fi 的朋友使用上方局域网地址。\r\n" +
        "3. 若局域网失败，检查 Windows 防火墙是否允许当前 Java 的专用网络入站连接。\r\n" +
        "4. 有公网 IPv4：在路由器将 TCP 外部端口映射到本机 IPv4 和当前端口。\r\n" +
        "5. CGNAT、校园网或无路由器管理权：使用可信游戏隧道，或在自有云服务器部署 FRP；本地目标填写 127.0.0.1:" +
        server.ServerPort + "。\r\n\r\n" +
        "安全建议\r\n" +
        "• 保持 online-mode=true。\r\n" +
        "• 控制台执行 whitelist on，并用 whitelist add 玩家名 添加朋友。\r\n" +
        "• 不要暴露 RCON，不要整体关闭防火墙，不公开隧道令牌。\r\n" +
        "• 朋友连接时日志毫无记录通常是网络链路问题；日志出现认证或版本错误则处理客户端版本/账号。";

    private void CopyAddressButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_addresses.Length == 0)
        {
            MessageBox.Show("当前没有可复制的局域网 IPv4 地址。", "联机诊断");
            return;
        }
        try
        {
            Clipboard.SetText(_addresses[0]);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"复制失败：{ex.Message}", "联机诊断");
        }
    }

    private void RefreshButton_OnClick(object sender, RoutedEventArgs e) => RefreshNetworkState();

    private void FirewallButton_OnClick(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            $"将请求管理员权限，为以下精确范围创建入站允许规则：\n\n" +
            $"程序：{_server.JavaExe}\nTCP 端口：{_server.ServerPort}\n网络配置文件：仅专用网络\n\n" +
            "不会关闭防火墙，也不会开放 RCON。确定继续吗？",
            "确认创建防火墙规则", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            return;
        var safeRuleName =
            $"Minecraft Server Pilot {_server.MinecraftVersion} {_server.ServerPort}"
                .Replace("\"", "");
        var arguments =
            $"advfirewall firewall add rule name=\"{safeRuleName}\" dir=in action=allow " +
            $"protocol=TCP localport={_server.ServerPort} program=\"{_server.JavaExe}\" " +
            "profile=private enable=yes";
        try
        {
            _log.Info("FIREWALL",
                $"请求创建专用网络规则：Java={_server.JavaExe}; TCP={_server.ServerPort}; Name={safeRuleName}");
            using var process = Process.Start(new ProcessStartInfo("netsh.exe", arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            }) ?? throw new InvalidOperationException("Windows 未能启动防火墙配置程序。");
            process.WaitForExit();
            if (process.ExitCode == 0)
            {
                _log.Info("FIREWALL", "netsh 返回 0，规则创建成功。");
                MessageBox.Show("专用网络入站规则已创建。现在可以启动服务端并刷新端口检测。",
                    "防火墙规则", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                _log.Error("FIREWALL", $"netsh 返回退出码 {process.ExitCode}。");
                MessageBox.Show($"netsh 返回退出码 {process.ExitCode}。规则可能未创建，请在完整日志中记录此退出码。",
                    "防火墙规则失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            _log.Warn("FIREWALL", "用户取消了 UAC 请求，未修改防火墙。");
            MessageBox.Show("你取消了管理员权限请求，没有修改防火墙。", "防火墙规则");
        }
        catch (Exception ex)
        {
            _log.Error("FIREWALL", "创建防火墙规则失败。", ex);
            MessageBox.Show($"无法创建规则：{ex.Message}\n\n没有关闭或放宽其他防火墙设置。",
                "防火墙规则失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();
}

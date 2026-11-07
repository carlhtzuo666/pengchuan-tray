using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace PengchuanTray;

/// <summary>
/// 自定义组播通告监听器（方案 A，手机端 LanAnnouncer 的对端）。
///
/// 为什么需要它：系统 mDNS 把 _nfcshare._tcp 登记在 5353，而 Windows 上该端口
/// 常被系统 mDNS 服务或浏览器（本机实测为 msedge 的局域网发现）**排他占用**，
/// 我们抢不到端口就完全发现不了手机，症状是「碰了标签但电脑毫无反应」且时好时坏。
/// 手机端现在会额外向自定义组播地址周期通告，这里监听该地址即可完全绕开端口竞争。
///
/// 与 mDNS 的关系：**并行**。两条通道任一收到手机信息即可发现，不是替代关系。
/// </summary>
public sealed class LanListener : IDisposable
{
    /// <summary>组播地址：239.255.73.83，与手机端 LanAnnouncer 保持一致</summary>
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("239.255.73.83");

    /// <summary>端口：53318，非特权端口</summary>
    private const int MulticastPort = 53318;

    private readonly object _gate = new();
    private UdpClient? _client;
    private CancellationTokenSource? _cts;

    /// <summary>收到一帧有效通告</summary>
    public event Action<LanAnnouncement>? Announced;

    /// <summary>诊断信息（与 MdnsClient 保持同一约定，便于 --probe 统一输出）</summary>
    public List<string> Diagnostics { get; } = new();

    /// <summary>是否已成功加入组播组</summary>
    public bool IsRunning { get; private set; }

    public void Start()
    {
        if (_client is not null)
        {
            return;
        }

        try
        {
            var client = new UdpClient();
            // 允许端口复用：手机端也可能在同一台机器上跑（调试场景），
            // 且 Windows 有时会把该端口预留给其他 mDNS/SSDP 类组件。
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            client.Client.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));

            // 多网卡逐个加入组播组（Wi-Fi 与有线同时存在时，只加默认网卡会漏设备）
            var joined = 0;
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
                    nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                {
                    continue;
                }
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }
                    try
                    {
                        client.JoinMulticastGroup(MulticastAddress, addr.Address);
                        joined++;
                        lock (_gate)
                        {
                            Diagnostics.Add($"{DateTime.Now:HH:mm:ss.fff} 组播加入：{nic.Name} {addr.Address}");
                        }
                    }
                    catch
                    {
                        // 单网卡加入失败忽略（例如该网卡不支持组播）
                    }
                }
            }

            _client = client;
            _cts = new CancellationTokenSource();
            IsRunning = true;
            lock (_gate)
            {
                Diagnostics.Add($"{DateTime.Now:HH:mm:ss.fff} 组播监听已就绪 {MulticastAddress}:{MulticastPort}（加入 {joined} 个网卡）");
            }
            _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            // 失败不影响主链路：mDNS 与云端会合仍是可用通道
            IsRunning = false;
            lock (_gate)
            {
                Diagnostics.Add($"{DateTime.Now:HH:mm:ss.fff} 组播监听启动失败：{ex.Message}");
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var client = _client;
        if (client is null)
        {
            return;
        }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await client.ReceiveAsync(ct);
                var text = Encoding.UTF8.GetString(result.Buffer);
                var announcement = LanAnnouncement.TryParse(text, result.RemoteEndPoint.Address.ToString());
                if (announcement is not null)
                {
                    Announced?.Invoke(announcement);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    if (Diagnostics.Count < 200)
                    {
                        Diagnostics.Add($"{DateTime.Now:HH:mm:ss.fff} 组播接收异常：{ex.Message}");
                    }
                }
                // 避免异常空转成死循环
                try { await Task.Delay(500, ct); } catch { break; }
            }
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _client?.Dispose(); } catch { }
        _client = null;
        _cts = null;
        IsRunning = false;
    }
}

/// <summary>
/// 手机端组播通告的载荷。
///
/// 协议：{"v":"v1","port":8080,"token":"&lt;32位十六进制&gt;","name":"nfcshare"}
/// token 明文传递——与现有 mDNS TXT 同等安全等级（局域网本就是共享广播域，
/// 且令牌每次启动重新生成）。这是与用户确认过的选项 1。
/// </summary>
public sealed record LanAnnouncement(string Address, int Port, string Token, string Version)
{
    /// <summary>解析一帧通告；字段缺失或版本不认识时返回 null（忽略该帧而不是抛异常）</summary>
    public static LanAnnouncement? TryParse(string json, string sourceAddress)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("port", out var portEl) || !portEl.TryGetInt32(out var port))
            {
                return null;
            }
            if (port <= 0 || port > 65535)
            {
                return null;
            }
            if (!root.TryGetProperty("token", out var tokenEl))
            {
                return null;
            }
            var token = tokenEl.GetString();
            if (string.IsNullOrWhiteSpace(token) || token.Length < 16)
            {
                // 令牌明显不合法的帧直接丢弃，避免把垃圾包当成手机
                return null;
            }
            var version = root.TryGetProperty("v", out var vEl) ? (vEl.GetString() ?? "v1") : "v1";
            return new LanAnnouncement(sourceAddress, port, token, version);
        }
        catch
        {
            // 非本协议的组播包（同网段噪声）是常态，静默忽略
            return null;
        }
    }
}

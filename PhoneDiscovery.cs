using System.Net;

namespace PengchuanTray;

/// <summary>发现到的手机端点：URL 已带一次性令牌，可直接给浏览器或下载器使用。</summary>
public sealed record DiscoveredPhone(string Host, int Port, string Token, string Url)
{
    public string Display => $"{Host}:{Port}";
}

/// <summary>
/// mDNS 发现手机（服务类型 <c>_nfcshare._tcp.local</c>）。
///
/// 手机端广播约定（HarmonyOS <c>MdnsAdvertiser</c>）：
///   · 服务名：<c>nfcshare-&lt;token&gt;</c>
///   · TXT：<c>pcId=&lt;token&gt;</c>、<c>v=v1</c>、<c>path=/</c>
///   · 端口：手机本地 HTTP 服务端口（默认 8080）
///
/// 本类只做「状态聚合」：把 MdnsClient 收到的 PTR/SRV/TXT/A 拼成一台手机。
/// </summary>
public sealed class PhoneDiscovery : IDisposable
{
    private const string ServiceType = "_nfcshare._tcp.local";
    private const string InstancePrefix = "nfcshare-";

    private sealed class InstanceState
    {
        public string? Target;
        public int Port;
        public string Token = string.Empty;
        public string? Address;
        public DateTime LastQueryUtc;
        public DateTime LastSeenUtc;
    }

    private readonly object _gate = new();
    private readonly MdnsClient _mdns = new();
    private readonly Dictionary<string, InstanceState> _instances = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _ownDiagnostics = new();

    private CancellationTokenSource? _cts;
    private DiscoveredPhone? _current;
    private DateTime _lastSeenUtc = DateTime.MinValue;

    /// <summary>发现结果变化（null 表示手机已下线）</summary>
    public event Action<DiscoveredPhone?>? Changed;

    /// <summary>诊断信息（--probe 写进 probe.json）</summary>
    public IReadOnlyList<string> Diagnostics
    {
        get
        {
            lock (_gate)
            {
                var merged = new List<string>(_ownDiagnostics);
                merged.AddRange(_mdns.Diagnostics);
                return merged;
            }
        }
    }

    public DiscoveredPhone? Current
    {
        get
        {
            lock (_gate)
            {
                if (_current is not null && DateTime.UtcNow - _lastSeenUtc > TimeSpan.FromSeconds(30))
                {
                    return null;
                }
                return _current;
            }
        }
    }

    public void Start()
    {
        if (_cts is not null)
        {
            return;
        }
        _mdns.RecordsReceived += OnRecordsReceived;
        _mdns.Start();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => QueryLoopAsync(_cts.Token));
    }

    /// <summary>主动再扫一次（托盘「立即扫描」/ --probe 用）</summary>
    public void Rescan() => _mdns.Query(ServiceType, MdnsClient.TypePtr);

    private async Task QueryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Rescan();
            // 已发现实例但缺字段的，补发 SRV/TXT/A 查询
            List<string> pending;
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                pending = _instances
                    .Where(kv => kv.Value.Address is null || kv.Value.Port == 0 || kv.Value.Token.Length == 0)
                    .Where(kv => now - kv.Value.LastQueryUtc > TimeSpan.FromSeconds(3))
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var name in pending)
                {
                    _instances[name].LastQueryUtc = now;
                }
            }
            foreach (var name in pending)
            {
                _mdns.Query(name, MdnsClient.TypeSrv);
                _mdns.Query(name, MdnsClient.TypeTxt);
            }
            try
            {
                await Task.Delay(3000, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void OnRecordsReceived(IReadOnlyList<MdnsRecord> records)
    {
        lock (_gate)
        {
            foreach (var record in records)
            {
                if (record.Type == MdnsClient.TypePtr &&
                    record.Name.IndexOf("_nfcshare._tcp", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    !string.IsNullOrEmpty(record.Target))
                {
                    var instance = record.Target!;
                    if (!instance.StartsWith(InstancePrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (!_instances.ContainsKey(instance))
                    {
                        _instances[instance] = new InstanceState { LastQueryUtc = DateTime.MinValue };
                        _ownDiagnostics.Add($"{DateTime.Now:HH:mm:ss.fff} 发现实例 {instance}");
                    }
                    _instances[instance].LastSeenUtc = DateTime.UtcNow;
                }
                else if (record.Type == MdnsClient.TypeSrv && _instances.TryGetValue(record.Name, out var srvState))
                {
                    srvState.Port = record.Port;
                    srvState.Target = record.Target;
                    srvState.LastSeenUtc = DateTime.UtcNow;
                }
                else if (record.Type == MdnsClient.TypeTxt && _instances.TryGetValue(record.Name, out var txtState))
                {
                    var pcId = record.Texts.FirstOrDefault(t => t.StartsWith("pcId=", StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(pcId))
                    {
                        txtState.Token = pcId!["pcId=".Length..];
                    }
                    txtState.LastSeenUtc = DateTime.UtcNow;
                }
                else if ((record.Type == MdnsClient.TypeA || record.Type == MdnsClient.TypeAaaa) &&
                         !string.IsNullOrEmpty(record.Address))
                {
                    // A 记录的名字是 SRV 的 Target，需要反查是哪台手机
                    foreach (var kv in _instances)
                    {
                        if (!string.IsNullOrEmpty(kv.Value.Target) &&
                            string.Equals(kv.Value.Target, record.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            kv.Value.Address = record.Address;
                            kv.Value.LastSeenUtc = DateTime.UtcNow;
                        }
                    }
                }
            }

            foreach (var kv in _instances)
            {
                if (kv.Value.Address is null || kv.Value.Port == 0)
                {
                    continue;
                }
                var token = kv.Value.Token.Length > 0
                    ? kv.Value.Token
                    : kv.Key.Split('.')[0][InstancePrefix.Length..];
                var phone = new DiscoveredPhone(kv.Value.Address!, kv.Value.Port, token,
                    $"http://{kv.Value.Address}:{kv.Value.Port}/?t={Uri.EscapeDataString(token)}");
                PublishLocked(phone);
                return;
            }
        }
    }

    private void PublishLocked(DiscoveredPhone phone)
    {
        var previous = _current;
        _current = phone;
        _lastSeenUtc = DateTime.UtcNow;
        if (previous is null || previous.Host != phone.Host || previous.Port != phone.Port || previous.Token != phone.Token)
        {
            _ownDiagnostics.Add($"{DateTime.Now:HH:mm:ss.fff} 就绪 {phone.Url}");
            // 事件在锁外触发，避免回调里再次加锁死锁
            Task.Run(() => Changed?.Invoke(phone));
        }
    }

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // 忽略
        }
        _mdns.RecordsReceived -= OnRecordsReceived;
        _mdns.Dispose();
        _cts = null;
    }
}

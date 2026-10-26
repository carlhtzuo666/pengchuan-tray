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
            // 已发现实例但缺字段的，立刻补发 SRV/TXT 查询（不再等 3 秒）
            List<string> pending;
            lock (_gate)
            {
                pending = _instances
                    .Where(kv => kv.Value.Address is null || kv.Value.Port == 0 || kv.Value.Token.Length == 0)
                    .Select(kv => kv.Key)
                    .ToList();
            }
            foreach (var name in pending)
            {
                _mdns.Query(name, MdnsClient.TypeSrv);
                _mdns.Query(name, MdnsClient.TypeTxt);
            }
            try
            {
                // 1 秒一轮：发现要快，mDNS 报文很小，不会造成明显负担
                await Task.Delay(1000, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void OnRecordsReceived(IReadOnlyList<MdnsRecord> records)
    {
        var queries = new List<(string Name, ushort Type)>();
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
                        _instances[instance] = new InstanceState();
                        _ownDiagnostics.Add($"{DateTime.Now:HH:mm:ss.fff} 发现实例 {instance}");
                        // 新实例：立刻补 SRV/TXT，避免等下一轮 1 秒
                        queries.Add((instance, MdnsClient.TypeSrv));
                        queries.Add((instance, MdnsClient.TypeTxt));
                    }
                    var ptrState = _instances[instance];
                    ptrState.LastSeenUtc = DateTime.UtcNow;
                    // mDNS 报文来源就是手机 IP：不必再等 A 记录往返
                    if (ptrState.Address is null && record.SourceAddress is not null)
                    {
                        ptrState.Address = record.SourceAddress;
                    }
                }
                else if (record.Type == MdnsClient.TypeSrv && _instances.TryGetValue(record.Name, out var srvState))
                {
                    srvState.Port = record.Port;
                    srvState.Target = record.Target;
                    srvState.LastSeenUtc = DateTime.UtcNow;
                    if (srvState.Address is null && record.SourceAddress is not null)
                    {
                        srvState.Address = record.SourceAddress;
                    }
                    if (srvState.Address is null && !string.IsNullOrEmpty(record.Target))
                    {
                        queries.Add((record.Target!, MdnsClient.TypeA));
                    }
                }
                else if (record.Type == MdnsClient.TypeTxt && _instances.TryGetValue(record.Name, out var txtState))
                {
                    var pcId = record.Texts.FirstOrDefault(t => t.StartsWith("pcId=", StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(pcId))
                    {
                        txtState.Token = pcId!["pcId=".Length..];
                    }
                    txtState.LastSeenUtc = DateTime.UtcNow;
                    if (txtState.Address is null && record.SourceAddress is not null)
                    {
                        txtState.Address = record.SourceAddress;
                    }
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

            // 清理 15 秒没再出现的实例：手机重启后旧 token 不能抢先被打开
            var cutoff = DateTime.UtcNow.AddSeconds(-15);
            var stale = _instances
                .Where(kv => kv.Value.LastSeenUtc < cutoff)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var name in stale)
            {
                _instances.Remove(name);
                _ownDiagnostics.Add($"{DateTime.Now:HH:mm:ss.fff} 过期实例 {name}");
            }

            // 同一 IP:port 只保留最新的 mDNS 实例：
            // 手机重启/页面重开会换 token，旧实例若还缓存着，会让托盘在两个 token 间反复切换。
            var keepByEndpoint = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var duplicates = new List<string>();
            foreach (var kv in _instances)
            {
                if (kv.Value.Address is null || kv.Value.Port == 0)
                {
                    continue;
                }
                var endpoint = $"{kv.Value.Address}:{kv.Value.Port}";
                if (!keepByEndpoint.TryGetValue(endpoint, out var keep))
                {
                    keepByEndpoint[endpoint] = kv.Key;
                    continue;
                }
                if (kv.Value.LastSeenUtc > _instances[keep].LastSeenUtc)
                {
                    duplicates.Add(keep);
                    keepByEndpoint[endpoint] = kv.Key;
                }
                else
                {
                    duplicates.Add(kv.Key);
                }
            }
            foreach (var name in duplicates)
            {
                _instances.Remove(name);
                _ownDiagnostics.Add($"{DateTime.Now:HH:mm:ss.fff} 同端点旧实例 {name}");
            }

            // 选“最近活跃且字段完整”的一台，而不是字典里的第一台
            KeyValuePair<string, InstanceState>? best = null;
            foreach (var kv in _instances)
            {
                if (kv.Value.Address is null || kv.Value.Port == 0)
                {
                    continue;
                }
                if (best is null || kv.Value.LastSeenUtc > best.Value.Value.LastSeenUtc)
                {
                    best = kv;
                }
            }
            if (best is not null)
            {
                var state = best.Value.Value;
                var token = state.Token.Length > 0
                    ? state.Token
                    : best.Value.Key.Split('.')[0][InstancePrefix.Length..];
                var phone = new DiscoveredPhone(state.Address!, state.Port, token,
                    $"http://{state.Address}:{state.Port}/?t={Uri.EscapeDataString(token)}");
                PublishLocked(phone);
            }
        }
        // 查询放到锁外，避免持锁做 socket 发送
        foreach (var query in queries)
        {
            _mdns.Query(query.Name, query.Type);
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

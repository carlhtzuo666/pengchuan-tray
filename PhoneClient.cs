using System.Text.Json;

namespace PengchuanTray;

/// <summary>
/// 手机「发件箱」里的一个文件（手机端 <c>/api/outbox/list</c> 返回）。
/// Size / Mtime 用于「只下载新文件」的去重：同名同大小同时间即视为已收过，避免轮询反复覆盖。
/// </summary>
public sealed record RemoteFile(string Name, string Url, long Size = 0, long Mtime = 0)
{
    /// <summary>去重键：名字或大小/时间任一变化都算新文件</summary>
    public string DedupKey => $"{Name}|{Size}|{Mtime}";
}

/// <summary>
/// 手机端发来的一条文字（手机端 <c>/api/clip/latest</c> 返回）。
/// Id 单调递增，电脑端按 Id 去重，避免同一条反复写入剪贴板。
/// Enter=true 表示手机端点了「发送」，电脑端送达文字后还要按一次回车。
/// </summary>
public sealed record RemoteClip(long Id, string Text, bool Enter, string Mode, bool FileDeliver, string[] FileNames);

/// <summary>
/// 手机局域网服务客户端：只读发件箱 + 流式下载（带 Range 语义的服务端，这里先整文件流式拷贝）。
/// 令牌走查询参数 <c>?t=</c>（与手机端 <c>authorized()</c> 兼容），不写日志。
/// </summary>
public sealed class PhoneClient : IDisposable
{
    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    public async Task<IReadOnlyList<RemoteFile>> ListOutboxAsync(DiscoveredPhone phone, CancellationToken ct = default)
        => await ListAsync(phone, "/api/outbox/list", ct);

    /// <summary>手机「接收目录」列表（电脑→手机方向上传的文件，自检/排查用）</summary>
    public async Task<IReadOnlyList<RemoteFile>> ListDavAsync(DiscoveredPhone phone, CancellationToken ct = default)
        => await ListAsync(phone, "/api/dav/list", ct);

    /// <summary>
    /// 拉取最新一条「未送达」文字。
    /// 手机端在无新内容时返回 204 空响应，这里必须显式判空——
    /// 直接 JsonDocument.Parse 空串会抛异常，被轮询的空 catch 吞掉后完全看不到原因。
    /// </summary>
    public async Task<RemoteClip?> GetLatestClipAsync(DiscoveredPhone phone, long sinceId, CancellationToken ct = default)
    {
        var url = $"http://{phone.Host}:{phone.Port}/api/clip/latest"
                + $"?t={Uri.EscapeDataString(phone.Token)}&since={sinceId}";
        var json = await _http.GetStringAsync(url, ct);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id))
        {
            return null;
        }
        var text = root.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty;
        // enter 缺省按 false 处理：老版本手机端不下发该字段时保持「只复制」的原有行为
        var enter = root.TryGetProperty("enter", out var e) && e.ValueKind == JsonValueKind.True;
        // 投递方式由手机端逐条下发；旧版手机不带该字段时留空，由调用方回退到本地默认
        var mode = root.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString() ?? string.Empty
            : string.Empty;
        // fileDeliver=true：本批新文件要投递到当前焦点输入框（写文件剪贴板 + 粘贴），
        // 而不是只保存到下载目录
        var fileDeliver = root.TryGetProperty("fileDeliver", out var fd) && fd.ValueKind == JsonValueKind.True;
        // 本次要投递的文件名列表（精确投递用）：手机端下发，缺失时为空数组
        var names = new List<string>();
        if (root.TryGetProperty("fileNames", out var fn) && fn.ValueKind == JsonValueKind.Array)
        {
            foreach (var one in fn.EnumerateArray())
            {
                if (one.ValueKind == JsonValueKind.String)
                {
                    var s = one.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) { names.Add(s); }
                }
            }
        }
        return new RemoteClip(id, text, enter, mode, fileDeliver, names.ToArray());
    }

    /// <summary>
    /// 确认已写入剪贴板。
    /// 手机端 ack 只标记 delivered、不删除记录，因此重发/回看仍可用。
    /// 用 POST + JSON 载荷，与手机端 pushClip/ackClip 的解码方式一致。
    /// </summary>
    public async Task AckClipAsync(DiscoveredPhone phone, long id, CancellationToken ct = default)
    {
        var url = $"http://{phone.Host}:{phone.Port}/api/clip/ack?t={Uri.EscapeDataString(phone.Token)}";
        var payload = JsonSerializer.Serialize(new { text = id.ToString() });
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(url, content, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// 快速探测手机局域网服务是否真的可用（token 是否有效、端口是否在听）。
    /// 打开浏览器前先确认，避免把用户带到一个 401/连接失败的页面。
    /// </summary>
    public async Task<bool> PingAsync(DiscoveredPhone phone, int timeoutMs = 800, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            var url = $"http://{phone.Host}:{phone.Port}/api/ping?t={Uri.EscapeDataString(phone.Token)}";
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<IReadOnlyList<RemoteFile>> ListAsync(DiscoveredPhone phone, string path, CancellationToken ct)
    {
        var url = $"http://{phone.Host}:{phone.Port}{path}?t={Uri.EscapeDataString(phone.Token)}";
        var json = await _http.GetStringAsync(url, ct);
        var list = new List<RemoteFile>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("files", out var files))
        {
            return list;
        }
        foreach (var item in files.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
            if (name.Length == 0)
            {
                continue;
            }
            var rel = item.TryGetProperty("url", out var u) ? u.GetString() : null;
            // size / mtime 用于「只下载新文件」的去重；老版本手机端不返回时按 0 处理，
            // 此时调用方退化为「本地已存在同名文件则跳过」，不会反复覆盖。
            var size = item.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0;
            var mtime = item.TryGetProperty("mtime", out var m) && m.TryGetInt64(out var mv) ? mv : 0;
            list.Add(new RemoteFile(name, rel ?? $"/outbox/{Uri.EscapeDataString(name)}", size, mtime));
        }
        return list;
    }

    /// <summary>
    /// 下载单个文件到目录，返回落地路径。
    /// 本地已存在同名且大小一致时直接跳过（返回已有路径）：
    /// 托盘重启后去重集合会清空，有了这个判断就不会把用户已经收过的文件反复覆盖。
    /// </summary>
    public async Task<string> DownloadAsync(
        DiscoveredPhone phone,
        RemoteFile file,
        string directory,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        var dest = Path.Combine(directory, Sanitize(file.Name));
        if (file.Size > 0)
        {
            var existing = new FileInfo(dest);
            if (existing.Exists && existing.Length == file.Size)
            {
                return dest;
            }
        }
        var url = $"http://{phone.Host}:{phone.Port}{file.Url}?t={Uri.EscapeDataString(phone.Token)}";

        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? -1;

        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (total > 0)
            {
                progress?.Report((double)done / total);
            }
        }
        return dest;
    }

    /// <summary>文件名清洗：避免手机传来的名字带非法字符或路径穿越</summary>
    public static string Sanitize(string name)
    {
        var cleaned = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        cleaned = cleaned.Replace("..", "_");
        return cleaned.Length == 0 ? "unnamed" : cleaned;
    }

    public void Dispose() => _http.Dispose();
}

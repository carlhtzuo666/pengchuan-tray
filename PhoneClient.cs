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

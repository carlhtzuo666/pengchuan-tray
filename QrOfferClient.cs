using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PengchuanTray;

/// <summary>
/// 扫码贴纸会合客户端：按本机 pcQrId 从 share-api 取走手机登记的局域网地址。
/// 匿名即可取（二维码 ID 本身即一次性凭证），不涉及账号登录。
/// </summary>
public sealed class QrOfferClient : IDisposable
{
    private const string ApiUrl =
        "https://cozenote-d1gok705d22b400db-1324572532.ap-shanghai.app.tcloudbase.com/share";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    /// <summary>取一次会合记录；没有可用的返回 null（一次性，取走即失效）</summary>
    public async Task<string?> TakeAsync(string qrId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(qrId))
        {
            return null;
        }
        var payload = JsonSerializer.Serialize(new { action = "qrTake", qrId });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync(ApiUrl, content, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("found", out var found) || found.ValueKind != JsonValueKind.True)
        {
            return null;
        }
        if (!root.TryGetProperty("urls", out var urls) || urls.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (var item in urls.EnumerateArray())
        {
            var url = item.GetString();
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }
        }
        return null;
    }

    public void Dispose()
    {
        // HttpClient 为静态复用，无需释放
    }
}

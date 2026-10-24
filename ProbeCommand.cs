using System.Text.Json;

namespace PengchuanTray;

/// <summary>
/// 调试/自检入口：
///   <c>PengchuanTray.exe --probe 8</c>                       只做 mDNS 发现
///   <c>PengchuanTray.exe --probe 8 --receive D:\tmp\out</c>  发现后顺便下载一个文件（验证下载链路）
/// 结果写到 exe 同目录 <c>probe.json</c>。
/// </summary>
internal static class ProbeCommand
{
    public static int Run(string[] args)
    {
        var seconds = args.Length > 1 && int.TryParse(args[1], out var parsed) ? Math.Clamp(parsed, 1, 120) : 6;
        var receiveDir = ReadOption(args, "--receive");
        var found = new List<object>();
        using var discovery = new PhoneDiscovery();
        discovery.Changed += phone =>
        {
            if (phone is null)
            {
                return;
            }
            lock (found)
            {
                found.Add(new { phone.Host, phone.Port, phone.Token, phone.Url });
            }
        };

        discovery.Start();
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var nextRescan = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(200);
            if (DateTime.UtcNow >= nextRescan)
            {
                discovery.Rescan();
                nextRescan = DateTime.UtcNow.AddSeconds(2);
            }
        }

        var current = discovery.Current;
        object? receive = null;
        if (current is not null && !string.IsNullOrWhiteSpace(receiveDir))
        {
            receive = ReceiveOnce(current, receiveDir!);
        }

        var payload = new
        {
            seconds,
            current,
            found,
            receive,
            diagnostics = discovery.Diagnostics,
            generatedAt = DateTimeOffset.Now.ToString("O")
        };
        var path = Path.Combine(AppContext.BaseDirectory, "probe.json");
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(path);
        Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        return current is null ? 2 : 0;
    }

    private static object ReceiveOnce(DiscoveredPhone phone, string directory)
    {
        using var client = new PhoneClient();
        try
        {
            var files = client.ListOutboxAsync(phone).GetAwaiter().GetResult();
            var source = "outbox";
            if (files.Count == 0)
            {
                // 发件箱为空时用「接收目录」里的文件验证下载链路（同一套 HTTP 通道）
                files = client.ListDavAsync(phone).GetAwaiter().GetResult();
                source = "dav";
            }
            if (files.Count == 0)
            {
                return new { source, files = 0, downloaded = (string?)null, bytes = 0, note = "手机侧没有可下载文件" };
            }
            var dest = client.DownloadAsync(phone, files[0], directory).GetAwaiter().GetResult();
            return new
            {
                source,
                files = files.Count,
                name = files[0].Name,
                downloaded = dest,
                bytes = new FileInfo(dest).Length
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private static string? ReadOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index >= 0 && index + 1 < args.Length)
        {
            return args[index + 1];
        }
        return null;
    }
}

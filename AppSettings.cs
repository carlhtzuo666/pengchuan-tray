using System.Text.Json;
using System.Text.Json.Serialization;

namespace PengchuanTray;

/// <summary>
/// 用户设置：写在 exe 同目录（绿色工具习惯），目录不可写时退回 %LOCALAPPDATA%。
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>设置版本：用于老版本配置的一次性迁移</summary>
    public int SettingsVersion { get; set; }

    /// <summary>本机扫码贴纸 ID（pc_&lt;机器码&gt;_&lt;随机密钥&gt;，首次使用时生成）</summary>
    public string PcQrId { get; set; } = string.Empty;

    /// <summary>是否在托盘运行期间监听扫码贴纸（默认开启；关闭后不发起任何轮询）</summary>
    public bool QrListenEnabled { get; set; } = true;

    /// <summary>发现手机后自动用默认浏览器打开局域网页（默认开启：碰标签后零输入直达）</summary>
    public bool AutoOpenBrowser { get; set; } = true;

    /// <summary>发现手机后自动把「发件箱」文件下载到 DownloadFolder（默认开启）</summary>
    public bool AutoDownloadOutbox { get; set; } = true;

    /// <summary>
    /// 是否已经应用过「默认开机自启」。
    /// 首次运行/从旧版本升级时自动写入 HKCU Run；之后用户手动取消则不再强行恢复。
    /// </summary>
    public bool StartupDefaultApplied { get; set; }

    /// <summary>下载目录；空则用桌面\碰传share</summary>
    public string DownloadFolder { get; set; } = string.Empty;

    /// <summary>没有发现手机时，「接收文件」是否自动改为打开云端取件页</summary>
    public bool FallbackToCloudPage { get; set; } = true;

    [JsonIgnore]
    public string ResolvedDownloadFolder =>
        string.IsNullOrWhiteSpace(DownloadFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "碰传share")
            : DownloadFolder;

    private static string SettingsPath()
    {
        var besideExe = Path.Combine(AppContext.BaseDirectory, "settings.json");
        try
        {
            // 目录可写就用同目录，保持「绿色」
            var probe = Path.Combine(AppContext.BaseDirectory, ".write-test");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return besideExe;
        }
        catch
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PengchuanTray");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            var path = SettingsPath();
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions);
                if (loaded is not null)
                {
                    // v2 迁移：主路径改为「发现即自动收文件」，避免老配置仍只开浏览器
                    if (loaded.SettingsVersion < 2)
                    {
                        loaded.SettingsVersion = 2;
                        loaded.AutoDownloadOutbox = true;
                        loaded.AutoOpenBrowser = false;
                        loaded.Save();
                    }
                    // v3 迁移：默认开机自启 + 发现手机自动打开网页，用户无需任何手动设置
                    if (loaded.SettingsVersion < 3)
                    {
                        loaded.SettingsVersion = 3;
                        loaded.AutoOpenBrowser = true;
                        loaded.AutoDownloadOutbox = true;
                        loaded.StartupDefaultApplied = false;
                        loaded.Save();
                    }
                    return loaded;
                }
            }
        }
        catch
        {
            // 配置损坏时用默认值，不阻塞启动
        }
        // 首次运行：直接使用 v3 默认值（自动打开网页 + 自动收文件 + 首次启动时注册开机自启）
        return new AppSettings { SettingsVersion = 3 };
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(SettingsPath(), JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // 写配置失败不影响主流程
        }
    }
}

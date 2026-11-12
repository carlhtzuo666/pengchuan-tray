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

    /// <summary>
    /// 发现手机后自动用默认浏览器打开局域网页。
    /// **默认关闭**：托盘改为常驻后，这一项若开着，手机每次碰标签/每次重开待上传页
    /// 都会在电脑上弹出一个浏览器窗口，开会、全屏演示、写文档时会被抢焦点。
    /// 主路径改为「只自动收文件 + 托盘气泡提示」，需要看网页时手动点托盘菜单。
    /// </summary>
    public bool AutoOpenBrowser { get; set; } = false;

    /// <summary>发现手机后自动把「发件箱」文件下载到 DownloadFolder（默认开启）</summary>
    public bool AutoDownloadOutbox { get; set; } = true;

    /// <summary>
    /// 是否自动接收手机发来的文字并写入系统剪贴板（默认开启）。
    /// 老配置无此键时反序列化保留属性初始化器的 true，无需 bump SettingsVersion。
    /// </summary>
    public bool AutoReceiveClip { get; set; } = true;

    /// <summary>文字投递方式：注入到当前输入框（默认）</summary>
    public const string ClipModeInject = "inject";

    /// <summary>文字投递方式：只复制到系统剪贴板</summary>
    public const string ClipModeClipboard = "clipboard";

    /// <summary>
    /// 手机文字到达电脑后的处理方式：
    /// <c>inject</c>（默认）= 键入当前焦点输入框；<c>clipboard</c> = 只写剪贴板。
    /// 老配置无此键时保留初始化器默认值，无需 bump SettingsVersion。
    /// </summary>
    public string ClipDeliveryMode { get; set; } = ClipModeInject;

    /// <summary>当前是否为「注入到输入框」模式</summary>
    public bool IsClipInjectMode => ClipDeliveryMode != ClipModeClipboard;

    /// <summary>
    /// 实时同步输入（边打边传）：手机端打字停顿约 300ms 就推送，电脑端做差分注入，
    /// 包括按退格镜像删除。默认关闭——这会把输入中的草稿也实时送到电脑，
    /// 属于需要用户主动选择的高打扰模式。老配置无此键时保留默认 false。
    /// </summary>
    public bool RealtimeSync { get; set; }

    /// <summary>
    /// 是否已经应用过「默认开机自启」。
    /// 首次运行/从旧版本升级时自动写入 HKCU Run；之后用户手动取消则不再强行恢复。
    /// </summary>
    public bool StartupDefaultApplied { get; set; }

    /// <summary>下载目录；空则用桌面\贴传（旧目录 桌面\碰传share 存在且新目录未建时沿用旧目录）</summary>
    public string DownloadFolder { get; set; } = string.Empty;

    /// <summary>没有发现手机时，「接收文件」是否自动改为打开云端取件页</summary>
    public bool FallbackToCloudPage { get; set; } = true;

    [JsonIgnore]
    public string ResolvedDownloadFolder =>
        string.IsNullOrWhiteSpace(DownloadFolder)
            ? DefaultDownloadFolder()
            : DownloadFolder;

    /// <summary>
    /// 默认下载目录。
    ///
    /// MSIX（Microsoft Store）形态走**系统「下载」文件夹**：
    /// Store 应用装进 C:\Program Files\WindowsApps\，把文件写桌面既不符合商店应用习惯，
    /// 也容易让审核觉得行为怪异；系统下载夹是用户对「下载」的默认预期位置。
    ///
    /// 免安装版沿用「桌面\贴传」：这是老用户已经习惯的位置，贸然改成下载夹会让
    /// 升级后的用户找不到历史文件。改名兼容逻辑（新目录未建而旧目录存在时沿用旧目录）同样只对免安装版生效。
    /// </summary>
    private static string DefaultDownloadFolder()
    {
        if (AppRuntime.IsPackaged)
        {
            var downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(downloads);
            return downloads;
        }
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var current = Path.Combine(desktop, "贴传");
        var legacy = Path.Combine(desktop, "碰传share");
        return !Directory.Exists(current) && Directory.Exists(legacy) ? legacy : current;
    }

    /// <summary>
    /// 可写数据目录（配置与日志统一落这里）。
    ///
    /// 规则：程序同目录可写就用同目录（保持免安装版的「绿色」特性），
    /// 否则回退到 %LOCALAPPDATA%\PengchuanTray。
    ///
    /// MSIX（Microsoft Store）形态下这套逻辑依然成立：系统的 AppData 虚拟化会把
    /// %LOCALAPPDATA%\PengchuanTray 重定向到包私有目录
    /// （%LOCALAPPDATA%\Packages\&lt;包族名&gt;\LocalCache\Local\PengchuanTray），
    /// 所以**不需要为 Store 版单独写一套路径**。
    ///
    /// 为什么不能让日志继续写程序目录：MSIX 的安装目录
    /// C:\Program Files\WindowsApps\... 是只读的，写入会直接抛异常
    /// （本会话已实测确认该目录不可写）。
    /// </summary>
    private static string? _dataDirectory;

    public static string DataDirectory()
    {
        if (_dataDirectory is not null)
        {
            return _dataDirectory;
        }
        try
        {
            // 目录可写就用同目录，保持「绿色」
            var probe = Path.Combine(AppContext.BaseDirectory, ".write-test");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            _dataDirectory = AppContext.BaseDirectory;
        }
        catch
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PengchuanTray");
            Directory.CreateDirectory(dir);
            _dataDirectory = dir;
        }
        return _dataDirectory;
    }

    private static string SettingsPath() => Path.Combine(DataDirectory(), "settings.json");

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
                    // v4 迁移：托盘改常驻后关闭「自动弹浏览器」，只保留「自动收文件 + 气泡提示」。
                    // 常驻场景下 v3 的默认值会让手机每次碰标签都在电脑上弹窗抢焦点。
                    if (loaded.SettingsVersion < 4)
                    {
                        loaded.SettingsVersion = 4;
                        loaded.AutoOpenBrowser = false;
                        loaded.AutoDownloadOutbox = true;
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

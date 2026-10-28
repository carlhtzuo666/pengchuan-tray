using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;
using QRCoder;

namespace PengchuanTray;

/// <summary>
/// 托盘应用上下文：常驻托盘、盯住 mDNS、按设置自动打开手机局域网页或接收发件箱。
/// 设计约束（与需求文档 V1.7 一致）：
///   · T1 不监听端口、不装服务，只做「发现 + 打开/下载」；
///   · 局域网优先；发现不到手机时按设置退回云端账号取件页。
/// </summary>
public sealed class TrayContext : ApplicationContext
{
    private const string CloudPickupUrl = "https://share.ailinkfree.com/t/";

    private readonly NotifyIcon _icon;
    private readonly PhoneDiscovery _discovery = new();
    private readonly PhoneClient _phone = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly System.Windows.Forms.Timer _ticker = new() { Interval = 5000 };

    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _openLanItem;
    private readonly ToolStripMenuItem _receiveItem;
    private readonly ToolStripMenuItem _autoOpenItem;
    private readonly ToolStripMenuItem _autoDownloadItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _qrStickerItem;
    private readonly ToolStripMenuItem _qrListenItem;
    private readonly QrOfferClient _qrClient = new();

    private string? _openedToken;
    private bool _receiving;
    private bool _exiting;
    private bool _qrChecking;
    private readonly HashSet<string> _opening = new();
    private readonly object _openGate = new();

    public TrayContext()
    {
        _statusItem = new ToolStripMenuItem("手机：未发现（贴一下电脑上的 NFC 标签）") { Enabled = false };
        _openLanItem = new ToolStripMenuItem("打开手机局域网页", null, (_, _) => OpenLanPage()) { Enabled = false };
        _receiveItem = new ToolStripMenuItem("接收文件（下载手机发件箱）", null, async (_, _) => await ReceiveAsync());
        _autoOpenItem = new ToolStripMenuItem("发现手机后自动打开网页", null, (_, _) => ToggleAutoOpen())
        {
            Checked = _settings.AutoOpenBrowser
        };
        _autoDownloadItem = new ToolStripMenuItem("发现手机后自动接收文件", null, (_, _) => ToggleAutoDownload())
        {
            Checked = _settings.AutoDownloadOutbox
        };
        _startupItem = new ToolStripMenuItem("开机自动启动", null, (_, _) => ToggleStartup())
        {
            Checked = IsStartupEnabled()
        };
        _qrStickerItem = new ToolStripMenuItem("显示/打印扫码贴纸", null, (_, _) => ShowQrSticker());
        _qrListenItem = new ToolStripMenuItem("监听扫码贴纸", null, (_, _) => ToggleQrListen())
        {
            Checked = _settings.QrListenEnabled
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_openLanItem);
        menu.Items.Add(_receiveItem);
        menu.Items.Add(_qrStickerItem);
        menu.Items.Add(_qrListenItem);
        menu.Items.Add(new ToolStripMenuItem("打开云端取件页", null, (_, _) => OpenUrl(CloudPickupUrl)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_autoOpenItem);
        menu.Items.Add(_autoDownloadItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripMenuItem("打开下载文件夹", null, (_, _) => OpenFolder(_settings.ResolvedDownloadFolder)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitApp()));

        _icon = new NotifyIcon
        {
            Icon = BuildIcon(),
            Text = "贴传 · 托盘助手",
            Visible = true,
            ContextMenuStrip = menu
        };

        _discovery.Changed += OnPhoneChanged;
        _discovery.Start();
        // 首次运行/升级后自动注册开机自启，用户无需手动勾选
        EnsureDefaultStartup();

        // 启动提示：Windows 11 默认把新图标收进托盘溢出区，提示用户去哪里找
        var startupTip = IsStartupEnabled() ? "已默认开启开机自启。" : string.Empty;
        Notify("贴传托盘助手已启动",
            $"图标在右下角托盘（看不到请点 ^ 展开）。{startupTip}手机就绪后会自动打开局域网页并接收文件。");

        _ticker.Tick += (_, _) =>
        {
            RefreshStatus();
            _ = CheckQrOfferAsync();
        };
        _ticker.Start();
        RefreshStatus();
    }

    private void OnPhoneChanged(DiscoveredPhone? phone)
    {
        if (_exiting)
        {
            return;
        }
        RefreshStatus();
        if (phone is null)
        {
            return;
        }

        Log($"发现手机 {phone.Display} token={phone.Token.Substring(0, Math.Min(4, phone.Token.Length))}");
        // mDNS 可能拿到旧 token 或手机服务刚起还没就绪：先 ping 通再打开，避免失败页面
        _ = OpenWhenReadyAsync(phone);
    }

    /// <summary>
    /// 打开前确认局域网服务真的可用（HTTP 200）；失败时短间隔重试，
    /// 覆盖「手机刚起服务 / 旧 token 还没被新记录替换」的窗口。
    /// </summary>
    private async Task OpenWhenReadyAsync(DiscoveredPhone phone)
    {
        // 同一个 token 可能对应多个候选 IP（虚拟网卡/多网卡会收到不同来源地址），
        // 必须按 host+port+token 去重，否则正确的 192.168.x.x 会被错误候选挡住。
        var openKey = $"{phone.Host}:{phone.Port}:{phone.Token}";
        lock (_openGate)
        {
            if (!_opening.Add(openKey))
            {
                return;
            }
        }
        try
        {
            for (var attempt = 0; attempt < 6; attempt++)
            {
                if (await _phone.PingAsync(phone, timeoutMs: 600))
                {
                    Log($"ping 成功 {phone.Display}");
                    Notify("手机已就绪", $"局域网直连 {phone.Display}（不消耗云流量）");
                    if (_settings.AutoOpenBrowser && _openedToken != phone.Token)
                    {
                        _openedToken = phone.Token;
                        Log("打开局域网页");
                        OpenUrl(phone.Url);
                    }
                    if (_settings.AutoDownloadOutbox && !_receiving)
                    {
                        _ = ReceiveAsync();
                    }
                    return;
                }
                await Task.Delay(120);
            }
            // 同一 token 的正确 IP 可能已经打开成功；别让错误候选的失败提示干扰用户
            if (_openedToken != phone.Token)
            {
                Log($"ping 失败 {phone.Display}");
                Notify("局域网暂不可达", $"手机 {phone.Display} 未响应，请保持待上传页并确认同一 Wi-Fi");
            }
        }
        finally
        {
            lock (_openGate)
            {
                _opening.Remove(openKey);
            }
        }
    }

    private void RefreshStatus()
    {
        var phone = _discovery.Current;
        if (phone is null)
        {
            _statusItem.Text = "手机：未发现（贴一下电脑上的 NFC 标签）";
            _openLanItem.Enabled = false;
        }
        else
        {
            _statusItem.Text = $"手机：已就绪 {phone.Display}";
            _openLanItem.Enabled = true;
        }
        _icon.Text = Truncate(phone is null ? "贴传 · 未发现手机" : $"贴传 · {phone.Display}", 120);
    }

    private void OpenLanPage()
    {
        var phone = _discovery.Current;
        if (phone is null)
        {
            Notify("没有发现手机", "请让手机贴一下 NFC 标签并停留在待上传页");
            return;
        }
        OpenUrl(phone.Url);
    }

    /// <summary>
    /// 接收文件：有手机就下载发件箱；没有手机时按设置退回云端取件页。
    /// 这条路径对应需求里的「电脑侧弹窗 / 自动降级」。
    /// </summary>
    private async Task ReceiveAsync()
    {
        if (_receiving)
        {
            return;
        }
        var phone = _discovery.Current;
        if (phone is null)
        {
            if (_settings.FallbackToCloudPage)
            {
                OpenUrl(CloudPickupUrl);
                Notify("未发现局域网手机", "已打开云端取件页：手机走云端上传后，这里登录同账号即可下载");
            }
            else
            {
                Notify("未发现局域网手机", "请确认电脑与手机在同一 Wi-Fi，且手机停留在待上传页");
            }
            return;
        }

        _receiving = true;
        _receiveItem.Enabled = false;
        try
        {
            var files = await _phone.ListOutboxAsync(phone);
            if (files.Count == 0)
            {
                Notify("手机发件箱是空的", "请在手机上点「选择文件」，再回来接收");
                return;
            }

            var folder = _settings.ResolvedDownloadFolder;
            var progress = new Progress<double>(value =>
                _icon.Text = Truncate($"贴传 · 接收中 {value:P0}", 120));

            foreach (var file in files)
            {
                await _phone.DownloadAsync(phone, file, folder, progress);
            }
            Notify($"已接收 {files.Count} 个文件", folder);
        }
        catch (Exception ex)
        {
            Notify("接收失败", ex.Message);
        }
        finally
        {
            _receiving = false;
            _receiveItem.Enabled = true;
            RefreshStatus();
        }
    }

    private void ToggleAutoOpen()
    {
        _settings.AutoOpenBrowser = !_settings.AutoOpenBrowser;
        _autoOpenItem.Checked = _settings.AutoOpenBrowser;
        _settings.Save();
    }

    private void ToggleAutoDownload()
    {
        _settings.AutoDownloadOutbox = !_settings.AutoDownloadOutbox;
        _autoDownloadItem.Checked = _settings.AutoDownloadOutbox;
        _settings.Save();
    }

    /// <summary>生成并打开本机扫码贴纸二维码（可直接打印贴在电脑上）</summary>
    private void ShowQrSticker()
    {
        try
        {
            var id = EnsureQrId();
            var pageUrl = "https://share.ailinkfree.com/s/?id=" + Uri.EscapeDataString(id);
            using var generator = new QRCodeGenerator();
            using var qrData = generator.CreateQrCode(pageUrl, QRCodeGenerator.ECCLevel.Q);
            var png = new PngByteQRCode(qrData);
            var bytes = png.GetGraphic(20);
            var path = Path.Combine(Path.GetTempPath(), "pengchuan-qr-sticker.png");
            File.WriteAllBytes(path, bytes);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            Notify("扫码贴纸已生成", $"打印后贴在电脑上；手机系统相机扫码即可连接。\nID：{id}");
        }
        catch (Exception ex)
        {
            Notify("生成扫码贴纸失败", ex.Message);
        }
    }

    private void ToggleQrListen()
    {
        _settings.QrListenEnabled = !_settings.QrListenEnabled;
        _qrListenItem.Checked = _settings.QrListenEnabled;
        _settings.Save();
        Notify("贴传", _settings.QrListenEnabled ? "已开启扫码贴纸监听" : "已关闭扫码贴纸监听");
    }

    /// <summary>贴纸 ID 首次使用时生成并持久化：pc_&lt;机器码&gt;_&lt;随机密钥&gt;</summary>
    private string EnsureQrId()
    {
        if (!string.IsNullOrWhiteSpace(_settings.PcQrId))
        {
            return _settings.PcQrId;
        }
        var random = Guid.NewGuid().ToString("N");
        _settings.PcQrId = $"pc_{random[..8]}_{random[8..24]}";
        _settings.Save();
        return _settings.PcQrId;
    }

    /// <summary>托盘 ticker 里顺带检查一次扫码会合（不额外新增定时器）</summary>
    private async Task CheckQrOfferAsync()
    {
        if (!_settings.QrListenEnabled || _qrChecking || string.IsNullOrWhiteSpace(_settings.PcQrId))
        {
            return;
        }
        _qrChecking = true;
        try
        {
            var url = await _qrClient.TakeAsync(_settings.PcQrId);
            if (!string.IsNullOrWhiteSpace(url))
            {
                OpenUrl(url);
                Notify("扫码连接", "已打开手机的局域网地址");
            }
        }
        catch
        {
            // 网络抖动不影响托盘主流程，下个 tick 再试
        }
        finally
        {
            _qrChecking = false;
        }
    }

    /// <summary>
    /// 首次运行/从旧版本升级：自动写入开机自启，用户无需手动操作。
    /// 只执行一次；之后用户在菜单里取消，不会在下次启动时强行恢复。
    /// </summary>
    private void EnsureDefaultStartup()
    {
        if (!_settings.StartupDefaultApplied && SetStartup(true))
        {
            _settings.StartupDefaultApplied = true;
            _settings.Save();
        }
        _startupItem.Checked = IsStartupEnabled();
    }

    /// <summary>写入/删除 HKCU Run；成功返回 true（失败不抛异常）</summary>
    private static bool SetStartup(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key is null)
            {
                return false;
            }
            if (enabled)
            {
                key.SetValue("PengchuanTray", StartupCommand());
            }
            else
            {
                key.DeleteValue("PengchuanTray", throwOnMissingValue: false);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ToggleStartup()
    {
        if (!SetStartup(!IsStartupEnabled()))
        {
            Notify("设置失败", "无法修改开机启动注册表项");
            return;
        }
        _settings.StartupDefaultApplied = true;
        _settings.Save();
        _startupItem.Checked = IsStartupEnabled();
        Notify("贴传", _startupItem.Checked ? "已设置为开机自动启动" : "已取消开机自动启动");
    }

    private static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: false);
            return key?.GetValue("PengchuanTray") is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>开机启动命令：用 dotnet 主机加载 DLL，避免 Smart App Control 拦截未签名 exe</summary>
    private static string StartupCommand()
    {
        var process = Environment.ProcessPath ?? string.Empty;
        var entry = Environment.GetCommandLineArgs().FirstOrDefault() ?? string.Empty;
        if (process.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase) &&
            entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return $"\"{process}\" \"{entry}\"";
        }
        return $"\"{process}\"";
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notify("打开浏览器失败", ex.Message);
        }
    }

    private void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notify("打开文件夹失败", ex.Message);
        }
    }

    /// <summary>轻量诊断日志：只记时间与主机，不写令牌/URL（便于排查打开失败与延迟）</summary>
    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "tray.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }

    private void Notify(string title, string text)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = Truncate(text, 250);
            _icon.ShowBalloonTip(4000);
        }
        catch
        {
            // 通知失败不影响主流程
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";

    /// <summary>程序内生成托盘图标（蓝底 + 白色「碰」字提示），避免额外图标资源</summary>
    private static Icon BuildIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var brush = new SolidBrush(Color.FromArgb(31, 111, 235));
            graphics.FillEllipse(brush, 1, 1, 30, 30);
            using var font = new Font("Microsoft YaHei", 15, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.White);
            var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString("碰", font, textBrush, new RectangleF(0, 0, 32, 34), format);
        }
        var handle = bitmap.GetHicon();
        return Icon.FromHandle(handle);
    }

    private void ExitApp()
    {
        if (_exiting)
        {
            return;
        }
        _exiting = true;
        _ticker.Stop();
        _discovery.Changed -= OnPhoneChanged;
        _discovery.Dispose();
        _phone.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        ExitThread();
    }
}

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

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

    private string? _openedToken;
    private bool _receiving;
    private bool _exiting;

    public TrayContext()
    {
        _statusItem = new ToolStripMenuItem("手机：未发现（碰一下电脑上的 NFC 标签）") { Enabled = false };
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

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_openLanItem);
        menu.Items.Add(_receiveItem);
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
            Text = "碰传share · 托盘助手",
            Visible = true,
            ContextMenuStrip = menu
        };

        _discovery.Changed += OnPhoneChanged;
        _discovery.Start();

        // 启动提示：Windows 11 默认把新图标收进托盘溢出区，提示用户去哪里找
        Notify("碰传share 托盘助手已启动",
            "图标在右下角托盘（看不到请点 ^ 展开）。右键可设置开机自启；手机就绪后会自动接收文件。");

        _ticker.Tick += (_, _) => RefreshStatus();
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

        Notify("手机已就绪", $"局域网直连 {phone.Display}（不消耗云流量）");
        if (_settings.AutoOpenBrowser && _openedToken != phone.Token)
        {
            _openedToken = phone.Token;
            OpenUrl(phone.Url);
        }
        if (_settings.AutoDownloadOutbox && !_receiving)
        {
            _ = ReceiveAsync();
        }
    }

    private void RefreshStatus()
    {
        var phone = _discovery.Current;
        if (phone is null)
        {
            _statusItem.Text = "手机：未发现（碰一下电脑上的 NFC 标签）";
            _openLanItem.Enabled = false;
        }
        else
        {
            _statusItem.Text = $"手机：已就绪 {phone.Display}";
            _openLanItem.Enabled = true;
        }
        _icon.Text = Truncate(phone is null ? "碰传share · 未发现手机" : $"碰传share · {phone.Display}", 120);
    }

    private void OpenLanPage()
    {
        var phone = _discovery.Current;
        if (phone is null)
        {
            Notify("没有发现手机", "请让手机碰一下 NFC 标签并停留在待上传页");
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
                _icon.Text = Truncate($"碰传share · 接收中 {value:P0}", 120));

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

    private void ToggleStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key is null)
            {
                Notify("设置失败", "无法打开开机启动注册表项");
                return;
            }
            if (IsStartupEnabled())
            {
                key.DeleteValue("PengchuanTray", throwOnMissingValue: false);
            }
            else
            {
                key.SetValue("PengchuanTray", StartupCommand());
            }
            _startupItem.Checked = IsStartupEnabled();
            Notify("碰传share", _startupItem.Checked ? "已设置为开机自动启动" : "已取消开机自动启动");
        }
        catch (Exception ex)
        {
            Notify("设置失败", ex.Message);
        }
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

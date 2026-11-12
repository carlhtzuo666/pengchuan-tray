using System.Diagnostics;
using System.Linq;
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
    /// <summary>
    /// 文件投递时，若发件箱里没有「未接收过」的新文件（自动接收已全部拉过），
    /// 最多回溯投递最近这么多个，避免用户的「发送文件」操作静默无响应。
    /// </summary>
    private const int FileDeliverFallbackCount = 3;

    /// <summary>
    /// 云端取件页的实际打开地址。
    ///
    /// MSIX（Microsoft Store）形态下追加 <c>?from=store</c>：网页检测到该参数会隐藏
    /// 「从本站下载 Windows 客户端」区块，避免商店审核看到
    /// 「商店应用引导用户去第三方站点下载客户端」而产生疑问。
    /// 免安装版不带参数，网页表现完全不变。
    /// </summary>
    private static string CloudPickupPageUrl => AppRuntime.IsPackaged
        ? CloudPickupUrl + "?from=store"
        : CloudPickupUrl;

    private readonly NotifyIcon _icon;
    private readonly PhoneDiscovery _discovery = new();
    private readonly PhoneClient _phone = new();
    private readonly AppSettings _settings = AppSettings.Load();
    /// <summary>
    /// 主定时器。默认 2000ms（省电、够用）；实时同步输入开启后提到 500ms，
    /// 让「手机打字 → 电脑出现」的延迟压到感知不到的范围内。
    /// </summary>
    private readonly System.Windows.Forms.Timer _ticker = new() { Interval = NormalPollMs };
    /// <summary>常规轮询间隔（毫秒）</summary>
    private const int NormalPollMs = 2000;
    /// <summary>实时同步输入开启时的轮询间隔（毫秒）</summary>
    private const int RealtimePollMs = 500;

    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _openLanItem;
    private readonly ToolStripMenuItem _receiveItem;
    private readonly ToolStripMenuItem _autoOpenItem;
    private readonly ToolStripMenuItem _autoDownloadItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _qrStickerItem;
    private readonly ToolStripMenuItem _qrUniversalItem;
    private readonly ToolStripMenuItem _qrListenItem;
    private readonly ToolStripMenuItem _autoClipItem;
    private readonly QrOfferClient _qrClient = new();

    private string? _openedToken;
    private string? _openedCloudToken;
    private bool _receiving;
    private bool _exiting;
    private bool _qrChecking;
    private readonly HashSet<string> _opening = new();
    private readonly object _openGate = new();
    /** 已下载过的文件去重键（名字|大小|时间）；重启托盘会重新收一遍，但不会覆盖已有同名文件 */
    private readonly HashSet<string> _received = new();
    /** 轮询发件箱进行中标志：避免 2 秒定时器重入并发下载同一批文件 */
    private bool _polling;
    /** 已处理的最大文字 id：按 id 去重，避免同一条反复写剪贴板 */
    private long _lastClipId;
    /**
     * 上次轮询所用手机的令牌。
     * 手机端应用重启会换新令牌并重置文字 id 序列，此时必须把 _lastClipId 归零，
     * 否则旧的大 id 会把新文字永久判为「已处理」（真机实测复现过该故障）。
     */
    private string? _clipToken;
    /** 轮询文字传输进行中标志：与文件下载互不阻塞（下载大文件可能耗时数分钟） */
    private bool _clipPolling;
    /**
     * 实时同步模式下「电脑端当前已注入的文字」。
     * 每收到新内容时与它做差分：新增部分补键入、变短部分补退格，
     * 实现手机删字符时电脑同步删（真镜像），而不是每次整段重打。
     * 手动点「发送」的常规路径不使用它。
     */
    private string _lastTypedText = string.Empty;
    /** 实时同步菜单项（勾选状态反映 _settings.RealtimeSync） */
    private readonly ToolStripMenuItem _realtimeItem;

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
        _qrUniversalItem = new ToolStripMenuItem("显示/打印通用贴纸（手机电脑通用）", null, (_, _) => ShowUniversalSticker());
        _qrListenItem = new ToolStripMenuItem("监听扫码贴纸", null, (_, _) => ToggleQrListen())
        {
            Checked = _settings.QrListenEnabled
        };
        _autoClipItem = new ToolStripMenuItem("自动接收手机文字到剪贴板", null, (_, _) => ToggleAutoClip())
        {
            Checked = _settings.AutoReceiveClip
        };
        // 实时同步输入：手机打字停顿约 300ms 就推送，电脑差分注入（含退格镜像）
        _realtimeItem = new ToolStripMenuItem("实时同步输入（边打边传）", null, (_, _) => ToggleRealtimeSync())
        {
            Checked = _settings.RealtimeSync
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_openLanItem);
        menu.Items.Add(_receiveItem);
        menu.Items.Add(_qrStickerItem);
        menu.Items.Add(_qrUniversalItem);
        menu.Items.Add(_qrListenItem);
        menu.Items.Add(new ToolStripMenuItem("打开云端取件页", null, (_, _) => OpenUrl(CloudPickupPageUrl)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_autoOpenItem);
        menu.Items.Add(_autoDownloadItem);
        menu.Items.Add(_autoClipItem);
        menu.Items.Add(_realtimeItem);
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
        // 把 mDNS 是否降级写进日志：5353 被占用时发现能力会明显下降，
        // 这是「碰了标签电脑没反应」的常见原因之一，必须可见而不是静默。
        Log(_discovery.IsMdnsUnicastFallback
            ? "mDNS 降级：5353 被其它程序占用，已改用单播查询（发现可能变慢或漏设备）"
            : "mDNS 正常：已绑定 5353");
        // 首次运行/升级：自动注册**登录自启**（计划任务，不是 Run 键）。
        // 必须常驻才能实现「手机碰标签 → 电脑自动收文件」：NFC 由手机读，电脑侧
        // 没有 NFC 通路，不常驻就永远发现不了手机。用户之后可在菜单里取消。
        // 用计划任务而非 Run 键，是为了避开「未签名 exe + Run 键」这个 EDR 高频告警组合。
        EnsureDefaultStartup();

        // 启动提示：Windows 11 默认把新图标收进托盘溢出区，提示用户去哪里找
        var startupTip = IsStartupEnabled() ? "已开启登录自动启动。" : "需要登录自启可在托盘菜单勾选。";
        Notify("贴传托盘助手已启动",
            $"图标在右下角托盘（看不到请点 ^ 展开）。{startupTip}手机就绪后会自动接收文件。");

        _ticker.Tick += (_, _) =>
        {
            RefreshStatus();
            _ = CheckQrOfferAsync();
            // 持续轮询发件箱：只靠「发现手机」那一次下载会漏掉真正的主流程——
            // 用户碰完标签才去选文件，那时发件箱还是空的，之后就永远不下。
            // 这里每 2 秒补一次，只下载没见过的文件（按名字+大小+时间戳去重）。
            _ = PollOutboxAsync();
            // 文字传输与文件下载独立：大文件下载可能耗时数分钟，不能饿死低延迟的文字接收。
            _ = PollClipAsync();
        };
        // 启动时按持久化的「实时同步输入」开关决定轮询节奏：
        // 开启则 500ms（打字要跟手），关闭则 2000ms（省电，点发送才传）。
        _ticker.Interval = _settings.RealtimeSync ? RealtimePollMs : NormalPollMs;
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
                // 局域网不可达（多半是手机与电脑不在同一网络）：自动打开云端取件页，
                // 让用户多一条「走云端」的通路——手机端可在待上传页点「改走云端上传」，
                // 文件就会出现在该页面列表里（同一 token 只开一次，避免反复弹页）。
                if (_settings.AutoOpenBrowser && _openedCloudToken != phone.Token)
                {
                    _openedCloudToken = phone.Token;
                    Log("局域网不可达，打开云端取件页");
                    OpenUrl(CloudPickupPageUrl);
                    Notify("已打开云端取件页",
                        $"手机 {phone.Display} 与电脑不在同一网络，局域网直连不可用。" +
                        "请在手机上点「改走云端上传」，文件会出现在该页面里。");
                }
                else
                {
                    Notify("局域网暂不可达", $"手机 {phone.Display} 未响应，请保持待上传页并确认同一 Wi-Fi");
                }
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
                OpenUrl(CloudPickupPageUrl);
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
            Log($"接收开始：{phone.Display}，下载目录 {_settings.ResolvedDownloadFolder}");
            var files = await _phone.ListOutboxAsync(phone);
            Log($"发件箱列表：{files.Count} 个文件");
            if (files.Count == 0)
            {
                Notify("手机发件箱是空的", "请在手机上点「选择文件」，再回来接收");
                return;
            }

            var count = await DownloadFilesAsync(phone, files);
            Log($"接收完成：{count} 个文件");
            Notify($"已接收 {count} 个文件", _settings.ResolvedDownloadFolder);
        }
        catch (Exception ex)
        {
            // 必须落日志：MSIX（Store）形态下没有控制台，静默吞掉异常会让
            // 「碰了标签但文件没到」完全无法排查。
            Log($"接收失败：{ex.GetType().Name}: {ex.Message}");
            Notify("接收失败", ex.Message);
        }
        finally
        {
            _receiving = false;
            _receiveItem.Enabled = true;
            RefreshStatus();
        }
    }

    /// <summary>
    /// 下载给定文件并登记去重键，返回实际下载数量。
    /// 抽出来供「手动接收」与「自动轮询」共用，避免两份下载循环。
    /// </summary>
    private async Task<int> DownloadFilesAsync(DiscoveredPhone phone, IReadOnlyList<RemoteFile> files)
    {
        var folder = _settings.ResolvedDownloadFolder;
        var progress = new Progress<double>(value =>
            _icon.Text = Truncate($"贴传 · 接收中 {value:P0}", 120));

        var downloaded = 0;
        foreach (var file in files)
        {
            await _phone.DownloadAsync(phone, file, folder, progress);
            _received.Add(file.DedupKey);
            downloaded++;
        }
        return downloaded;
    }

    /// <summary>
    /// 轮询发件箱，只下载尚未接收过的新文件（静默，不打断用户）。
    ///
    /// 为什么必须有这一步：真实主流程是「手机碰标签 → 停在待上传页 → 再去选文件」，
    /// 碰标签那一刻发件箱还是空的；只在「发现手机」时下载一次的话，
    /// 用户随后选的文件永远不会被接收，表现为「碰了标签但文件没自动到电脑」。
    /// </summary>
    private async Task PollOutboxAsync()
    {
        if (_polling || _receiving || _exiting || !_settings.AutoDownloadOutbox)
        {
            return;
        }
        var phone = _discovery.Current;
        if (phone is null)
        {
            return;
        }
        _polling = true;
        try
        {
            var files = await _phone.ListOutboxAsync(phone);
            var fresh = files.Where(f => !_received.Contains(f.DedupKey)).ToList();
            if (fresh.Count == 0)
            {
                return;
            }
            var count = await DownloadFilesAsync(phone, fresh);
            if (count > 0)
            {
                Log($"轮询下载 {count} 个新文件");
                Notify($"已自动接收 {count} 个文件", _settings.ResolvedDownloadFolder);
            }
        }
        catch
        {
            // 轮询失败静默：手机可能刚切后台/服务刚停，下一个 tick 会重试
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>
    /// 轮询手机最新文字，按 id 去重后写入系统剪贴板。
    /// 与 PollOutboxAsync 独立：文件下载可能耗时数分钟，不能阻塞低延迟的文字传输。
    ///
    /// 线程说明：Tick 在 UI（STA）线程触发，await 默认回到同一线程，
    /// 因此可直接调用 WinForms 的 Clipboard。**切勿加 ConfigureAwait(false)**，
    /// 否则 continuation 落到非 STA 线程池，写剪贴板会抛 ThreadStateException。
    ///
    /// 隐私：只记录 id，绝不把文字内容写进日志（可能是密码/验证码）。
    /// </summary>
    private async Task PollClipAsync()
    {
        if (_clipPolling || _exiting || !_settings.AutoReceiveClip)
        {
            return;
        }
        var phone = _discovery.Current;
        if (phone is null)
        {
            return;
        }
        _clipPolling = true;
        try
        {
            // 令牌变化 = 手机端应用重启过，其文字 id 序列已重置，这里同步归零
            if (_clipToken != phone.Token)
            {
                _clipToken = phone.Token;
                _lastClipId = 0;
            }
            var clip = await _phone.GetLatestClipAsync(phone, _lastClipId);
            if (clip is null || clip.Id <= _lastClipId)
            {
                return;
            }
            // 先登记 id 再处理：即使 ack 失败也不会同一条反复注入
            _lastClipId = clip.Id;
            var target = TextInjector.IsSupported ? TextInjector.DescribeForegroundWindow() : "(非 Windows)";
            // 实时同步输入：手机端每次打字停顿都推一条「当前完整文本」，
            // 这里与「电脑端已注入的文本」做差分，只补差异部分（含退格删除），
            // 避免每次整段重打导致重复堆叠。纯回车/文件投递仍走下面的常规分支。
            if (_settings.RealtimeSync && clip.Enter == false && clip.FileDeliver == false
                && TextInjector.IsSupported && _settings.IsClipInjectMode)
            {
                ApplyRealtimeDiff(clip.Text, target);
                await _phone.AckClipAsync(phone, clip.Id);
                return;
            }
            if (clip.Text.Length == 0)
            {
                // 纯回车事件（手机「Enter」按钮）：不动剪贴板、不动输入框内容，只按一次回车
                if (clip.Enter && TextInjector.IsSupported)
                {
                    var sent = TextInjector.PressEnter();
                    Notify(sent ? "已发送回车" : "回车发送被系统拦截", sent ? "等同在电脑上按 Enter" : "请手动按 Enter");
                    Log($"已接收回车 id={clip.Id} ok={sent} target=[{target}]");
                }
            }
            else
            {
                Clipboard.SetDataObject(clip.Text, true);
                // 投递方式以「手机端下拉框」为准（每条消息自带 mode）；
                // 旧版手机不下发时回退到本地默认设置，保证兼容。
                var inject = clip.Mode.Length > 0
                    ? clip.Mode != AppSettings.ClipModeClipboard
                    : _settings.IsClipInjectMode;
                // 文字一律先写剪贴板（注入失败或用户想手动粘贴时都靠它兜底）。
                if (TextInjector.IsSupported && inject)
                {
                    // Unicode 逐字符键入，不用 Ctrl+V：
                    // 实测无扫描码的 Ctrl+V 会被目标应用静默忽略（SendInput 仍返回成功）；
                    // Unicode 通道不依赖剪贴板与组合键，最稳。
                    var typed = TextInjector.TypeText(clip.Text);
                    var entered = !clip.Enter || TextInjector.PressEnter();
                    if (typed && entered)
                    {
                        Notify(clip.Enter ? "已输入并回车" : "文字已输入到当前输入框",
                            clip.Enter ? "已键入并回车发出" : "已键入，可继续编辑");
                        Log($"已接收文字 id={clip.Id} enter={clip.Enter} 键入成功 target=[{target}]");
                    }
                    else
                    {
                        // 常见原因：目标窗口以管理员权限运行（UIPI 禁止低权限进程注入），
                        // 或当时没有输入框获得焦点。文字已在剪贴板，手动粘贴即可。
                        Notify("已复制，但自动输入被系统拦截",
                            "目标窗口可能以管理员权限运行，请手动 Ctrl+V 粘贴");
                        Log($"已接收文字 id={clip.Id} enter={clip.Enter} 键入失败 typed={typed} entered={entered} target=[{target}]");
                    }
                }
                else
                {
                    // 「只复制到剪贴板」模式：不碰焦点输入框，交给用户自己 Ctrl+V
                    Notify("文字已复制到剪贴板", "可直接 Ctrl+V 粘贴到任意位置");
                    Log($"已接收文字 id={clip.Id} 模式=剪贴板 target=[{target}]");
                }
            }
            // 文件投递：手机端选完附件点「发送」→ 把这一批新文件下载后直接粘贴进当前输入框。
            // 放在 ack 之前执行，投递结果（成功/降级）能一并告知用户。
            if (clip.FileDeliver)
            {
                await DeliverFilesAsync(phone, clip.FileNames);
            }
            await _phone.AckClipAsync(phone, clip.Id);
        }
        catch
        {
            // 轮询失败静默：手机可能刚切后台/服务刚停，下一个 tick 会重试
        }
        finally
        {
            _clipPolling = false;
        }
    }

    /// <summary>
    /// 把手机端刚发来的文件投递到电脑当前焦点输入框。
    ///
    /// 流程：列发件箱 → 只挑尚未接收过的新文件 → 下载到下载目录
    /// → 写文件剪贴板（CF_HDROP，等价资源管理器复制文件）→ Ctrl+V 粘贴。
    ///
    /// 失败降级：微信 PC、普通网页 &lt;input type=file&gt; 不接受粘贴文件，
    /// 此时在资源管理器里选中文件，让用户手动拖入——这是唯一稳妥的手动方式。
    /// </summary>
    private async Task DeliverFilesAsync(DiscoveredPhone phone, string[] clipFileNames)
    {
        if (!FileInjector.IsSupported)
        {
            return;
        }
        IReadOnlyList<RemoteFile> all;
        try
        {
            all = await _phone.ListOutboxAsync(phone);
        }
        catch
        {
            // 列发件箱失败：手机可能刚切后台，交给下一轮
            return;
        }
        if (all.Count == 0)
        {
            return;
        }
        // 精确投递：手机端会把本次选中的文件名列表一起下发，只投这些文件。
        // 旧版手机不下发列表时（clipFileNames 为空）才回退到「最近修改的几个」。
        List<RemoteFile> candidates;
        if (clipFileNames.Length > 0)
        {
            var wanted = new HashSet<string>(clipFileNames, StringComparer.OrdinalIgnoreCase);
            candidates = all.Where(f => wanted.Contains(f.Name)).ToList();
            if (candidates.Count == 0)
            {
                // 名字对不上（手机刚改名、发件箱尚未刷新）：退回兜底，避免用户操作静默无响应
                Log($"投递名单 {clipFileNames.Length} 个在发件箱未找到，改用最近文件兜底");
                candidates = all.OrderByDescending(f => f.Mtime).Take(FileDeliverFallbackCount).ToList();
            }
        }
        else
        {
            var fresh = new List<RemoteFile>();
            foreach (var f in all)
            {
                if (!_received.Contains(f.DedupKey))
                {
                    fresh.Add(f);
                }
            }
            candidates = fresh.Count > 0
                ? fresh
                : all.OrderByDescending(f => f.Mtime).Take(FileDeliverFallbackCount).ToList();
        }
        var folder = _settings.ResolvedDownloadFolder;
        var saved = new List<string>();
        foreach (var f in candidates)
        {
            try
            {
                var path = await _phone.DownloadAsync(phone, f, folder);
                _received.Add(f.DedupKey);
                saved.Add(path);
            }
            catch (Exception ex)
            {
                Log($"投递文件下载失败 name={f.Name} err={ex.Message}");
            }
        }
        if (saved.Count == 0)
        {
            return;
        }
        var target = TextInjector.DescribeForegroundWindow();
        var pasted = FileInjector.PasteFiles(saved.ToArray());
        if (pasted)
        {
            Notify($"已投递 {saved.Count} 个文件", "已粘贴到当前输入框；若没出现，请手动拖入");
            Log($"文件投递 id 完成 count={saved.Count} 已粘贴 target=[{target}]");
        }
        else
        {
            // 降级：目标不接受粘贴文件（微信/网页常见），定位文件让用户拖
            FileInjector.RevealInExplorer(saved.ToArray());
            Notify($"已下载 {saved.Count} 个文件", "目标输入框不支持粘贴文件，请在资源管理器中拖入");
            Log($"文件投递 count={saved.Count} 粘贴失败已降级 target=[{target}]");
        }
    }

    /// <summary>
    /// 实时同步的差分注入：把「手机当前完整文本」与「电脑已注入文本」比较，
    /// 只补发差异 —— 变短则连发退格删掉多出的字符，变长则键入新增部分。
    ///
    /// 为什么必须做差分：手机端每次推送的是输入框「完整内容」。
    /// 若每次都整段键入，电脑上会变成「你好你好你好」这样的重复堆叠；
    /// 做差分后才是真正的镜像效果（打字即出现、删字即消失）。
    /// </summary>
    private void ApplyRealtimeDiff(string text, string target)
    {
        if (text == _lastTypedText)
        {
            return;
        }
        // 找出公共前缀长度：这部分电脑上已经有了，不需要动
        var prefix = 0;
        var maxPrefix = Math.Min(text.Length, _lastTypedText.Length);
        while (prefix < maxPrefix && text[prefix] == _lastTypedText[prefix])
        {
            prefix++;
        }
        // 需要删掉电脑上多出来的字符数（手机端变短或中间回改时产生）
        var toDelete = _lastTypedText.Length - prefix;
        if (toDelete > 0)
        {
            TextInjector.PressBackspace(toDelete);
        }
        // 需要补键入的新增部分
        var toAppend = text.Substring(prefix);
        if (toAppend.Length > 0)
        {
            TextInjector.TypeText(toAppend);
        }
        _lastTypedText = text;
        Log($"实时同步 差分 prefix={prefix} del={toDelete} add={toAppend.Length} target=[{target}]");
    }

    private void ToggleAutoClip()
    {
        _settings.AutoReceiveClip = !_settings.AutoReceiveClip;
        _autoClipItem.Checked = _settings.AutoReceiveClip;
        _settings.Save();
    }

    /// <summary>
    /// 切换「实时同步输入（边打边传）」。
    /// 打开后：手机端打字停顿约 300ms 自动推送；电脑端轮询提速到 500ms，
    /// 并对内容做差分注入（新增补键入、删除补退格），实现真正的镜像。
    /// 关闭后恢复 2 秒轮询与「点发送才传」的常规模式。
    /// </summary>
    private void ToggleRealtimeSync()
    {
        _settings.RealtimeSync = !_settings.RealtimeSync;
        _realtimeItem.Checked = _settings.RealtimeSync;
        _settings.Save();
        // 立即生效，不必重启：定时器间隔按开关切换
        _ticker.Interval = _settings.RealtimeSync ? RealtimePollMs : NormalPollMs;
        // 切换模式时清空差分基线：避免上一次会话的残留内容与新文本算错差分
        _lastTypedText = string.Empty;
        Notify(_settings.RealtimeSync ? "已开启实时同步输入" : "已关闭实时同步输入",
            _settings.RealtimeSync ? "手机打字会实时出现在电脑当前输入框" : "恢复为点「发送」才传");
        Log($"实时同步输入 -> {_settings.RealtimeSync} 轮询={_ticker.Interval}ms");
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

    /// <summary>
    /// 生成并打开「通用贴纸码」：内容就是云端取件页地址，手机系统相机扫码即可直达贴传；
    /// 电脑侧不用扫码——托盘发现手机会自动接管（同一网络开局域网页，不同一网络开云端取件页）。
    /// </summary>
    private void ShowUniversalSticker()
    {
        try
        {
            var pageUrl = "https://share.ailinkfree.com/t/?from=sticker";
            using var generator = new QRCodeGenerator();
            using var qrData = generator.CreateQrCode(pageUrl, QRCodeGenerator.ECCLevel.Q);
            var png = new PngByteQRCode(qrData);
            var bytes = png.GetGraphic(20);
            var path = Path.Combine(Path.GetTempPath(), "pengchuan-qr-universal.png");
            File.WriteAllBytes(path, bytes);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            Notify("通用贴纸码已生成", "打印后贴在电脑上：手机系统相机扫码即可直达贴传 App。\n" +
                "电脑不用扫码——托盘发现手机会自动接管：同一网络开局域网页，不同一网络开云端取件页。");
        }
        catch (Exception ex)
        {
            Notify("生成通用贴纸码失败", ex.Message);
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

    /// <summary>计划任务名（登录触发，延时 30 秒启动，避免开机瞬间抢资源）</summary>
    private const string StartupTaskName = "贴传助手";

    /// <summary>
    /// 首次运行/从旧版本升级：自动写入登录自启，用户无需手动操作。
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

    /// <summary>
    /// 写入/删除**计划任务**（登录触发 + 延时 30 秒），成功返回 true。
    ///
    /// 为什么用计划任务而不是 HKCU\...\Run：
    ///   · Run 键值 + 未签名 exe 是杀软/企业 EDR 判定「可疑自启动」最典型的组合，
    ///     本机就发生过 EDR 把宿主程序整个隔离的事故；
    ///   · 计划任务 `onlogon` 语义明确（登录后一次性触发，不是周期轮询），
    ///     还能用 `/delay` 避开开机高峰，观感与行为都更接近正规软件。
    ///   · 兼容既有安装：写入新任务的同时清掉历史 Run 键，避免开机被拉起两份。
    /// </summary>
    private static bool SetStartup(bool enabled)
    {
        // MSIX（Microsoft Store）形态：登录自启由包清单声明的 startupTask 扩展负责。
        // 沙箱内注册 schtasks 计划任务会被 Store 审核拒绝，因此这里必须分流，
        // 不能沿用免安装版的计划任务写法。
        if (AppRuntime.IsPackaged)
        {
            return SetStartupPackaged(enabled);
        }
        try
        {
            if (enabled)
            {
                var psi = new ProcessStartInfo("schtasks.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                // /tr 里嵌了带空格的路径，schtasks 要求把内层引号转义成 \"
                var command = StartupCommand().Replace("\"", "\\\"");
                psi.ArgumentList.Add("/create");
                psi.ArgumentList.Add("/tn");
                psi.ArgumentList.Add(StartupTaskName);
                psi.ArgumentList.Add("/tr");
                psi.ArgumentList.Add(command);
                psi.ArgumentList.Add("/sc");
                psi.ArgumentList.Add("onlogon");
                psi.ArgumentList.Add("/delay");
                psi.ArgumentList.Add("0000:30");
                psi.ArgumentList.Add("/f");
                using var create = Process.Start(psi);
                if (create is null)
                {
                    return false;
                }
                create.WaitForExit(15000);
                if (create.ExitCode != 0)
                {
                    Log($"创建计划任务失败 code={create.ExitCode} {create.StandardError.ReadToEnd()}");
                    return false;
                }
            }
            else
            {
                var psi = new ProcessStartInfo("schtasks.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add("/delete");
                psi.ArgumentList.Add("/tn");
                psi.ArgumentList.Add(StartupTaskName);
                psi.ArgumentList.Add("/f");
                using var del = Process.Start(psi);
                del?.WaitForExit(15000);
            }
            // 无论开还是关，都顺手清掉历史 Run 键（旧版本会写，留着会重复拉起）
            ClearLegacyRunEntry();
            return true;
        }
        catch (Exception ex)
        {
            Log("设置自启动失败 " + ex.Message);
            return false;
        }
    }

    /// <summary>清理旧版本写进 HKCU Run 的条目（含指向 Temp 的失效副本）</summary>
    private static void ClearLegacyRunEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            key?.DeleteValue("PengchuanTray", throwOnMissingValue: false);
        }
        catch
        {
            // 清理失败不影响主流程
        }
    }

    private void ToggleStartup()
    {
        if (!SetStartup(!IsStartupEnabled()))
        {
            if (AppRuntime.IsPackaged)
            {
                // MSIX 形态下最常见的原因：用户已在任务管理器里禁用了启动项，
                // 此时程序无权强行恢复，只能如实告知并指路。
                Notify("无法修改自启", StartupStateText(GetStartupStatePackaged()));
            }
            else
            {
                Notify("设置失败", "无法修改开机启动计划任务");
            }
            return;
        }
        _settings.StartupDefaultApplied = true;
        _settings.Save();
        _startupItem.Checked = IsStartupEnabled();
        if (AppRuntime.IsPackaged)
        {
            Notify("贴传", $"登录自启：{StartupStateText(GetStartupStatePackaged())}");
        }
        else
        {
            Notify("贴传", _startupItem.Checked ? "已设置为登录后自动启动" : "已取消登录自动启动");
        }
    }

    /// <summary>是否已注册登录自启（MSIX 形态看 startupTask，免安装版看计划任务）</summary>
    private static bool IsStartupEnabled()
    {
        if (AppRuntime.IsPackaged)
        {
            return GetStartupStatePackaged() == Windows.ApplicationModel.StartupTaskState.Enabled;
        }
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("/query");
            psi.ArgumentList.Add("/tn");
            psi.ArgumentList.Add(StartupTaskName);
            using var query = Process.Start(psi);
            if (query is null)
            {
                return false;
            }
            query.WaitForExit(8000);
            return query.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// MSIX 形态：用包清单里声明的 startupTask 控制登录自启。
    ///
    /// 必须尊重的三个平台限制（官方文档明确写了，会直接影响 UX）：
    ///   1. 用户必须手动启动过本应用至少一次，该 startupTask 才会注册；
    ///   2. 用户可在任务管理器「启动」标签里自行禁用；
    ///   3. 一旦被用户禁用（DisabledByUser），程序**无法**用代码强行恢复，
    ///      只能引导用户自己去任务管理器打开——不要试图绕过，那既做不到也不合规。
    ///
    /// WinRT 的 StartupTask API 是异步的，而菜单回调是同步的，
    /// 这里用 Task.Run 包一层再同步等待，避免在 UI 线程上直接等 IAsyncOperation 造成死锁。
    /// </summary>
    private static bool SetStartupPackaged(bool enabled)
    {
        try
        {
            var work = Task.Run(async () =>
            {
                var task = await Windows.ApplicationModel.StartupTask.GetAsync(AppRuntime.StartupTaskId);
                if (enabled)
                {
                    var state = await task.RequestEnableAsync();
                    return state == Windows.ApplicationModel.StartupTaskState.Enabled;
                }
                task.Disable();
                return task.State != Windows.ApplicationModel.StartupTaskState.Enabled;
            });
            return work.Wait(TimeSpan.FromSeconds(8)) && work.Result;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>MSIX 形态下查询 startupTask 当前状态（可能被用户或策略禁用）</summary>
    private static Windows.ApplicationModel.StartupTaskState? GetStartupStatePackaged()
    {
        try
        {
            var work = Task.Run(async () =>
            {
                var task = await Windows.ApplicationModel.StartupTask.GetAsync(AppRuntime.StartupTaskId);
                return task.State;
            });
            return work.Wait(TimeSpan.FromSeconds(8)) ? work.Result : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>MSIX 形态下把 startupTask 状态翻译成给用户看的中文说明</summary>
    private static string StartupStateText(Windows.ApplicationModel.StartupTaskState? state)
    {
        if (state is null)
        {
            return "状态未知";
        }
        return state switch
        {
            Windows.ApplicationModel.StartupTaskState.Enabled => "已开启",
            Windows.ApplicationModel.StartupTaskState.Disabled => "未开启",
            Windows.ApplicationModel.StartupTaskState.DisabledByUser =>
                "已被你在任务管理器中关闭，需到「任务管理器 → 启动」里重新打开",
            Windows.ApplicationModel.StartupTaskState.DisabledByPolicy => "已被系统策略禁用",
            Windows.ApplicationModel.StartupTaskState.EnabledByPolicy => "已由系统策略开启",
            _ => state.ToString() ?? "状态未知",
        };
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
            // 与 Program.Log 一致：走 DataDirectory()，兼容 MSIX 只读安装目录
            File.AppendAllText(
                Path.Combine(AppSettings.DataDirectory(), "tray.log"),
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

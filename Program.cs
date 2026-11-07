using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PengchuanTray;

/// <summary>
/// 应用入口：单实例 + 托盘常驻。
/// 另提供 <c>--probe &lt;秒数&gt;</c> 调试模式：只做 mDNS 发现并把结果写到 probe.json 后退出，
/// 便于在没有界面的环境下验证局域网发现逻辑。
/// 启动异常写入 startup.log（同目录），便于无控制台时排查。
/// </summary>
internal static class Program
{
    private static Mutex? _singleInstance;

    private static void Log(string message)
    {
        try
        {
            // 日志走 AppSettings.DataDirectory()：MSIX 形态下安装目录只读，
            // 直接写 AppContext.BaseDirectory 会抛异常（已被 catch 吞掉，表现为"日志消失"）。
            File.AppendAllText(
                Path.Combine(AppSettings.DataDirectory(), "startup.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Log($"start args=[{string.Join(' ', args)}] base={AppContext.BaseDirectory}");

            // 排查开关：强制走 mDNS 降级单播模式，用于验证「5353 抢不到」时的兜底路径
            if (args.Any(a => a.Equals("--force-unicast", StringComparison.OrdinalIgnoreCase)))
            {
                MdnsClient.ForceUnicastForTesting = true;
                Log("--force-unicast：强制 mDNS 单播降级模式");
            }

            if (args.Length > 0 && args[0] == "--probe")
            {
                return ProbeCommand.Run(args);
            }

            _singleInstance = new Mutex(true, AppRuntime.MutexName, out var isFirst);
            if (!isFirst)
            {
                // 开机自启可能由「启动文件夹 + 计划任务 + Run 键」同时触发；
                // 多实例时静默退出，避免弹窗打扰用户，只保留第一个托盘实例。
                Log("another instance is running, exit silently");
                return 0;
            }

            InitializeWinForms();
            Log("winforms initialized");
            RegisterRestartOnUpdate();
            using var context = new TrayContext();
            Log("tray context created");
            Application.Run(context);
            Log("application exited");
            return 0;
        }
        catch (Exception ex)
        {
            Log("FATAL " + ex);
            MessageBox.Show($"贴传托盘助手启动失败：{ex.Message}", "贴传",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    /// <summary>
    /// 注册「应用因更新/系统更新被关闭后自动重启」。
    ///
    /// 为什么 MSIX 版必须加：Microsoft Store 自动更新时会关闭正在运行的应用。
    /// 不注册这个，用户更新完会发现托盘不见了，必须手动再打开一次——
    /// 官方文档明确要求：想让应用在更新后续命，每个需要重启的进程都要调用
    /// RegisterApplicationRestart（并在收到 WM_QUERYENDSESSION 时可再次调用以更新命令行）。
    ///
    /// 时机很关键：实测在 Application.Run 之前直接调用会返回 false，
    /// 因为此时进程还没有消息泵/顶层窗口。改为挂在 Application.Idle 上，
    /// 等消息循环跑起来、NotifyIcon 也已创建后再注册，并且只执行一次。
    /// </summary>
    private static void RegisterRestartOnUpdate()
    {
        Application.Idle += OnFirstIdleRegisterRestart;
    }

    private static void OnFirstIdleRegisterRestart(object? sender, EventArgs e)
    {
        Application.Idle -= OnFirstIdleRegisterRestart;
        try
        {
            var ok = RegisterApplicationRestart(null, 0);
            if (ok)
            {
                Log("RegisterApplicationRestart ok");
            }
            else
            {
                var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                Log($"RegisterApplicationRestart failed, lastError={err}");
            }
        }
        catch (Exception ex)
        {
            Log("RegisterApplicationRestart threw: " + ex.Message);
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool RegisterApplicationRestart(string? pwzCommandline, int dwFlags);

    /// <summary>
    /// WinForms 初始化。
    /// 单文件发布时 WinForms 的 EnableVisualStyles 偶尔取不到程序清单
    /// （Assembly.Location 为空 → 打开清单文件流失败），会让托盘助手整个起不来。
    /// 这里对初始化做降级：清单取不到就退回手工设置 DPI 感知，保证托盘仍能起来。
    /// </summary>
    private static void InitializeWinForms()
    {
        try
        {
            ApplicationConfiguration.Initialize();
            return;
        }
        catch (Exception ex)
        {
            Log("ApplicationConfiguration.Initialize failed: " + ex.Message);
        }

        try { Application.SetHighDpiMode(HighDpiMode.SystemAware); } catch { }
        try { Application.EnableVisualStyles(); } catch { }
        try { Application.SetCompatibleTextRenderingDefault(false); } catch { }
    }
}

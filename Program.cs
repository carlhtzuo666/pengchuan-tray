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
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "startup.log"),
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
            if (args.Length > 0 && args[0] == "--probe")
            {
                return ProbeCommand.Run(args);
            }

            _singleInstance = new Mutex(true, @"Local\PengchuanTray", out var isFirst);
            if (!isFirst)
            {
                // 开机自启可能由「启动文件夹 + 计划任务 + Run 键」同时触发；
                // 多实例时静默退出，避免弹窗打扰用户，只保留第一个托盘实例。
                Log("another instance is running, exit silently");
                return 0;
            }

            InitializeWinForms();
            Log("winforms initialized");
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

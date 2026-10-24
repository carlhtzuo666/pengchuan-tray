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
                Log("another instance is running");
                MessageBox.Show("碰传share 托盘助手已经在运行（看屏幕右下角托盘图标）。", "碰传share",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }

            ApplicationConfiguration.Initialize();
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
            MessageBox.Show($"碰传share 托盘助手启动失败：{ex.Message}", "碰传share",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}

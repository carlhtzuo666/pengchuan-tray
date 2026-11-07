using System;

namespace PengchuanTray;

/// <summary>
/// 运行形态探测与形态相关常量。
///
/// 本项目同一份代码要同时支持两种分发形态：
///   1. MSIX 包（Microsoft Store）—— 安装目录只读、自启走 startupTask 扩展、
///      由 Store 负责签名与更新；
///   2. 免安装版（GitHub 单文件 exe）—— 程序目录可写、自启走 schtasks 计划任务。
///
/// 判定方式：在非打包环境访问 <c>Package.Current</c> 会抛异常，
/// 因此「能否取到包标识」就是最可靠的形态判据，不需要额外的配置文件或命令行开关。
///
/// 为什么需要区分 Mutex 名：两种形态可以同时安装在同一台机器上。
/// 若共用同一个互斥名，后启动的那个会被判定为「已有实例」而静默退出，
/// 用户会看到「两个都装了但只有一个能开」这种莫名其妙的现象。
/// </summary>
internal static class AppRuntime
{
    private static readonly Lazy<bool> PackagedLazy = new(DetectPackaged);

    /// <summary>当前进程是否运行在 MSIX 包内（即 Microsoft Store 形态）。</summary>
    public static bool IsPackaged => PackagedLazy.Value;

    /// <summary>单实例互斥名：按形态区分，避免两种形态互相顶掉。</summary>
    public static string MutexName => IsPackaged
        ? @"Local\PengchuanTray.Store"
        : @"Local\PengchuanTray.Portable";

    /// <summary>MSIX 清单里声明的 startupTask 的 TaskId，必须与 AppxManifest.xml 保持一致。</summary>
    public const string StartupTaskId = "PengchuanTrayStartup";

    private static bool DetectPackaged()
    {
        try
        {
            // 非打包环境这里会抛 COMException / InvalidOperationException，
            // 且不需要区分具体异常类型——取不到就是没打包。
            _ = Windows.ApplicationModel.Package.Current.Id.Name;
            return true;
        }
        catch
        {
            return false;
        }
    }
}

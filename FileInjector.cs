using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace PengchuanTray;

/// <summary>
/// 本地文件投递：把已下载到电脑的文件放进「当前焦点输入框」。
///
/// 机制说明（2026-11-12 真机验证过）：
/// 用 <see cref="Clipboard.SetFileDropList"/> 把文件写进剪贴板，等价于用户在资源管理器里
/// Ctrl+C 复制文件（Win32 层面就是标准 CF_HDROP / 系统格式 15），再发 Ctrl+V 让目标应用粘贴。
/// 实测：AI 对话网页（ChatGPT 类）与邮件撰写窗能直接接收并显示为附件；
/// 微信 PC 与普通网页 &lt;input type=file&gt; 因产品/浏览器安全限制不接受粘贴文件，
/// 此时降级为「在资源管理器里选中文件、让用户手动拖入」。
///
/// 为什么不手写 DROPFILES 结构：.NET 内置 SetFileDropList 已正确构造该结构，
/// 手写易错且无收益；也不引第三方库（底层都是同一个 Win32 API）。
/// </summary>
internal static class FileInjector
{
    /// <summary>本机是否具备文件投递能力（目前仅 Windows 有实现）</summary>
    internal static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// 把文件写进剪贴板并尝试粘贴到当前焦点输入框。
    ///
    /// 返回值语义：
    /// true  = 已写入剪贴板且 Ctrl+V 事件成功投递（目标是否真的接收由目标应用决定）；
    /// false = 平台不支持、文件不存在或剪贴板写入失败（调用方应降级提示）。
    /// </summary>
    internal static bool PasteFiles(string[] paths)
    {
        if (!IsSupported || paths.Length == 0)
        {
            return false;
        }
        // 只投递确实存在的文件，避免把失效路径写进剪贴板
        var exist = new StringCollection();
        foreach (var p in paths)
        {
            if (File.Exists(p))
            {
                exist.Add(p);
            }
        }
        if (exist.Count == 0)
        {
            return false;
        }
        try
        {
            Clipboard.SetFileDropList(exist);
        }
        catch
        {
            // 剪贴板被其它进程占用时会抛异常（剪贴板是全局单例资源）
            return false;
        }
        // 给剪贴板一点时间完成 OLE 数据登记，紧接着的 Ctrl+V 才不会拿到空内容
        System.Threading.Thread.Sleep(120);
        return TextInjector.PressCtrlV();
    }

    /// <summary>
    /// 降级路径：在资源管理器里选中这些文件，让用户直接拖进目标窗口。
    /// 微信 PC、普通网页 &lt;input type=file&gt; 不吃粘贴文件，拖拽是唯一稳妥的手动方式。
    /// </summary>
    internal static void RevealInExplorer(string[] paths)
    {
        if (!IsSupported || paths.Length == 0)
        {
            return;
        }
        var first = paths[0];
        if (!File.Exists(first))
        {
            return;
        }
        try
        {
            if (paths.Length == 1)
            {
                // /select 会打开所在目录并高亮该文件，用户可直接拖拽
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{first}\"")
                {
                    UseShellExecute = true
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{first}\"")
                {
                    UseShellExecute = true
                });
            }
        }
        catch
        {
            // 打开资源管理器失败不影响主流程（文件已在下载目录）
        }
    }
}

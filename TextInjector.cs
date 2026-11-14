using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace PengchuanTray;

/// <summary>
/// 本地键盘注入：用 Windows 官方 <c>SendInput</c> 模拟真实按键。
///
/// 为什么自己写 P/Invoke 而不引第三方库（InputSimulatorPlus 等）：
/// 本项目只需三个能力（Unicode 键入、Ctrl+V、回车），裸 P/Invoke 约百行且零依赖，
/// 对单文件发布的 MSIX 包体与依赖管理最友好；换任何库底层仍是 SendInput，
/// 安全软件看到的特征完全相同，引库没有收益。
///
/// 注入目标是「当前键盘焦点的输入框」，不是鼠标坐标——SendInput 走键盘通道。
/// 因此调用方必须先让目标输入框获得焦点，否则文字会打进别的窗口。
/// </summary>
internal static class TextInjector
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint INPUT_MOUSE = 0;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    // 鼠标事件标志（功能2：手机端触控板驱动电脑光标）
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;

    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_LCONTROL = 0xA2;
    private const ushort VK_LSHIFT = 0xA0;
    private const ushort VK_V = 0x56;
    private const ushort VK_BACK = 0x08;

    // 物理扫描码：部分应用（Chromium/Electron、游戏、远程桌面）只看扫描码或
    // 用 GetAsyncKeyState 校验修饰键状态，只给 VK 不给扫描码会被当成无效组合键。
    private const ushort SCAN_RETURN = 0x1C;
    private const ushort SCAN_LCONTROL = 0x1D;
    private const ushort SCAN_LSHIFT = 0x2A;
    private const ushort SCAN_V = 0x2F;
    private const ushort SCAN_BACK = 0x0E;

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>
    /// 联合体按 x64 最大成员（MOUSEINPUT）占 32 字节；
    /// 这里显式声明大小，保证 INPUT 结构与系统 ABI 一致（4 + 4 对齐 + 32 = 40 字节），
    /// 否则 cbSize 不符会让 SendInput 直接返回 0 失败。
    /// </summary>
    /// <summary>
    /// 鼠标事件结构：dx/dy 为相对位移；dwFlags 决定是移动还是按键。
    /// 三个 int 字段对应 Win32 MOUSEINPUT 的 dx/dy/mouseData，顺序与 ABI 一致。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder text, int count);

    /// <summary>本机是否具备键盘注入能力（当前仅 Windows 完整实现）</summary>
    internal static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// 当前前台窗口的「类名 | 标题」，仅用于排障日志。
    /// 注入打偏时靠它判断焦点当时到底在哪个窗口（不记录输入内容，无隐私风险）。
    /// </summary>
    internal static string DescribeForegroundWindow()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return "(无前台窗口)";
        }
        var cls = new System.Text.StringBuilder(256);
        GetClassNameW(hwnd, cls, cls.Capacity);
        var title = new System.Text.StringBuilder(256);
        GetWindowTextW(hwnd, title, title.Capacity);
        var t = title.ToString();
        if (t.Length > 40)
        {
            t = t.Substring(0, 40) + "…";
        }
        return $"{cls} | {t}";
    }

    /// <summary>发送一次回车：等价用户按下 Enter（用于「手机点发送 → 电脑把内容发出去」）</summary>
    internal static bool PressEnter()
    {
        return Send(
            KeyboardKey(VK_RETURN, SCAN_RETURN, false),
            KeyboardKey(VK_RETURN, SCAN_RETURN, true));
    }

    /// <summary>
    /// 发送一次 Shift+Enter：文本内部换行专用。
    ///
    /// 为什么文本内换行不能用普通 Enter：聊天类输入框（AI 对话框、微信、Teams 等）
    /// 把 Enter 当作「发送」，用普通 Enter 插换行会把没写完的草稿直接发出去
    /// （真机复现：手机打个换行，电脑端内容被发掉了）。
    /// Shift+Enter 是这些输入框通用的「换行不发送」组合键；
    /// 在记事本/多行文本框里也等同于换行；在单行输入框里则什么都不做，
    /// 比普通 Enter 触发默认提交（发送/搜索）更安全。
    /// </summary>
    internal static bool PressShiftEnter()
    {
        return Send(
            KeyboardKey(VK_LSHIFT, SCAN_LSHIFT, false),
            KeyboardKey(VK_RETURN, SCAN_RETURN, false),
            KeyboardKey(VK_RETURN, SCAN_RETURN, true),
            KeyboardKey(VK_LSHIFT, SCAN_LSHIFT, true));
    }

    /// <summary>
    /// 连发 N 次退格：实时边打边传模式下，手机端删掉字符时电脑端要同步删。
    /// </summary>
    internal static bool PressBackspace(int count)
    {
        if (count <= 0)
        {
            return true;
        }
        var events = new INPUT[count * 2];
        for (var i = 0; i < count; i++)
        {
            events[i * 2] = KeyboardKey(VK_BACK, SCAN_BACK, false);
            events[i * 2 + 1] = KeyboardKey(VK_BACK, SCAN_BACK, true);
        }
        return Send(events);
    }

    /// <summary>发送 Ctrl+V：把剪贴板内容粘贴到焦点输入框</summary>
    internal static bool PressCtrlV()
    {
        return Send(
            KeyboardKey(VK_LCONTROL, SCAN_LCONTROL, false),
            KeyboardKey(VK_V, SCAN_V, false),
            KeyboardKey(VK_V, SCAN_V, true),
            KeyboardKey(VK_LCONTROL, SCAN_LCONTROL, true));
    }

    /// <summary>
    /// 逐字符键入任意 Unicode 文本（含中文，绕过输入法直接送字符事件）。
    /// 每字符间隔很小但不能为零，否则部分应用（浏览器、Electron）会丢字符。
    ///
    /// 换行特殊处理（两轮真机踩坑后定稿）：
    /// 1) 手机端 TextArea 的换行是 U+000A（LF），直接当 Unicode 字符键入会被
    ///    Windows 编辑控件丢弃（第一版：换行挤在一行不上屏）；
    /// 2) 改发普通 Enter 又会在聊天类输入框里被当成「发送」（第二版：换行把草稿发出去）。
    /// 最终一律发 Shift+Enter——聊天框的通用「换行不发送」键，
    /// 记事本/多行框里等同换行，单行框里无副作用。
    /// CRLF 与 LFCR 两种组合只算一次换行，避免多出空行。
    /// </summary>
    internal static bool TypeText(string text, int delayMs = 4)
    {
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            bool sent;
            if (ch == '\r' || ch == '\n')
            {
                // CRLF / LFCR 都只算一次换行
                if (i + 1 < text.Length &&
                    ((ch == '\r' && text[i + 1] == '\n') || (ch == '\n' && text[i + 1] == '\r')))
                {
                    i++;
                }
                // 必须 Shift+Enter：普通 Enter 在聊天框里是「发送」，会把草稿直接发出去
                sent = PressShiftEnter();
            }
            else
            {
                sent = Send(
                    KeyboardUnicode(ch, false),
                    KeyboardUnicode(ch, true));
            }
            if (!sent)
            {
                return false;
            }
            if (delayMs > 0)
            {
                Thread.Sleep(delayMs);
            }
        }
        return true;
    }

    private static INPUT KeyboardKey(ushort vk, ushort scan, bool keyUp)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = scan,
                    dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    private static INPUT KeyboardUnicode(char ch, bool keyUp)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = ch,
                    dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0),
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    // ---------------- 鼠标注入（功能2：手机触控板驱动电脑光标） ----------------

    /// <summary>
    /// 相对移动光标：dx/dy 为像素增量，与笔记本触控板语义一致。
    ///
    /// 为什么用相对位移而不是绝对定位：手机触控板面积与电脑屏幕分辨率没有对应关系，
    /// 绝对定位需要先知道屏幕尺寸（多显示器更复杂），且手机屏比例与电脑屏比例不一致会变形。
    /// 相对位移对任何分辨率/多屏/缩放天然兼容。
    /// </summary>
    internal static bool MoveMouse(int dx, int dy)
    {
        if (dx == 0 && dy == 0)
        {
            return true;
        }
        return Send(MouseEvent(dx, dy, MOUSEEVENTF_MOVE, 0));
    }

    /// <summary>在光标当前位置单击左键（手机端双击触控板按钮触发）</summary>
    internal static bool LeftClick()
    {
        return Send(
            MouseEvent(0, 0, MOUSEEVENTF_LEFTDOWN, 0),
            MouseEvent(0, 0, MOUSEEVENTF_LEFTUP, 0));
    }

    /// <summary>滚轮滚动（正数向上）。预留扩展，暂未接 UI</summary>
    internal static bool ScrollWheel(int delta)
    {
        if (delta == 0)
        {
            return true;
        }
        return Send(MouseEvent(0, 0, MOUSEEVENTF_WHEEL, unchecked((uint)delta)));
    }

    private static INPUT MouseEvent(int dx, int dy, uint flags, uint mouseData)
    {
        return new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION
            {
                mi = new MOUSEINPUT
                {
                    dx = dx,
                    dy = dy,
                    mouseData = mouseData,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    private static bool Send(params INPUT[] inputs)
    {
        if (inputs.Length == 0)
        {
            return true;
        }
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        return sent == inputs.Length;
    }
}

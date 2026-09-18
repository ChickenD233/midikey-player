using System.Runtime.InteropServices;
using MidiKeyPlayer.Engine;

namespace MidiKeyPlayer.Input;

/// <summary>用 Windows SendInput 模拟键鼠（全局真实输入，目标窗口聚焦即可）。</summary>
public static class InputSender
{
    public enum MouseButton { Left, Right, Middle }

    private const int INPUT_MOUSE = 0;
    private const int INPUT_KEYBOARD = 1;

    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MAPVK_VK_TO_VSC = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern ushort MapVirtualKeyW(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    /// <summary>窗口所属进程的 PID；hwnd 为空返回 0。</summary>
    public static uint PidOfWindow(IntPtr hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid;
    }

    public static IntPtr ForegroundWindow =>
        OperatingSystem.IsWindows() ? GetForegroundWindow() : IntPtr.Zero;

    /// <summary>前台窗口标题，用于诊断按键发给了谁。</summary>
    public static string ForegroundWindowTitle
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return "";
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return "";
            var sb = new System.Text.StringBuilder(512);
            GetWindowTextW(h, sb, sb.Capacity);
            return sb.ToString();
        }
    }

    /// <summary>置前后台窗口；停止时先把焦点还给目标程序，再补发一次“松开”。</summary>
    public static void BringToForeground(IntPtr hWnd)
    {
        if (OperatingSystem.IsWindows() && hWnd != IntPtr.Zero) SetForegroundWindow(hWnd);
    }

    public static bool IsSupported => OperatingSystem.IsWindows();

    private static bool _mouseWarnedInDriverMode;   // 罗技驱动模式下「鼠标键发不了」只提醒一次

    // ---------------------------------------------------------------- 输入后端

    /// <summary>输入后端：SendInput（默认，用户态注入）/ 罗技 G HUB 驱动（驱动级 HID 报告）。</summary>
    public enum BackendKind { SendInput = 0, LogitechGHub = 1 }

    /// <summary>当前输入后端。默认 SendInput；只有 SetBackend 成功后才会变成罗技驱动。</summary>
    public static BackendKind Backend { get; private set; } = BackendKind.SendInput;

    /// <summary>
    /// 切换输入后端。切罗技驱动时立刻初始化：失败则保持 SendInput 不变，error 是中文原因。
    /// 成功但 G HUB 没在运行时 warn 非空（设备句柄能开但按键发不出去）。切回 SendInput 时把驱动设备句柄关掉。
    /// </summary>
    public static bool SetBackend(BackendKind kind, out string error, out string? warn)
    {
        error = "";
        warn = null;
        if (kind == BackendKind.LogitechGHub)
        {
            if (!IbDriver.TryInit(out error, out warn)) return false;
            Backend = BackendKind.LogitechGHub;
            return true;
        }

        IbDriver.Shutdown();
        Backend = BackendKind.SendInput;
        return true;
    }

    /// <summary>
    /// 播放前确认当前后端可用：罗技驱动已选但尚未初始化（比如启动时 G HUB 还没装好）时在这里补一次初始化。
    /// 失败返回 false，error 可直接弹给用户；后端自动退回 SendInput，避免无声播放。
    /// warn：这次真的做了初始化且发现 G HUB 没在运行时非空；没动初始化时为 null（不重复提醒）。
    /// </summary>
    public static bool EnsureBackend(out string error, out string? warn)
    {
        error = "";
        warn = null;
        if (Backend != BackendKind.LogitechGHub) return true;
        // TryInit 在已初始化时直接返回、warn 保持 null：只有这次真做了初始化才可能带出提醒
        if (IbDriver.TryInit(out error, out warn)) return true;
        Backend = BackendKind.SendInput;
        error += "\n本次先退回 SendInput。";
        return false;
    }

    // ---------------------------------------------------------------- 按下记账（A04）

    // 本程序真正按下过的键/鼠标键。ReleaseEverything 只抬这一份记录，绝不碰用户物理按住的键。
    private static readonly object HeldGate = new();
    private static readonly HashSet<string> HeldKeys = new(StringComparer.Ordinal);
    private static readonly HashSet<MouseButton> HeldButtons = new();

    /// <summary>记账用的规范键名：鼠标键与命名键大小写敏感，单字符键统一成大写（"z" 与 "Z" 是同一个键）。</summary>
    private static string NormKey(string? keyName)
    {
        string name = keyName ?? "";
        if (name.Length == 1) return char.ToUpperInvariant(name[0]).ToString();
        return name;
    }

    // ---------------------------------------------------------------- 键名 → 虚拟键码

    /// <summary>标点键的虚拟键码（US 布局，与扫描码方式配套）。</summary>
    private static readonly Dictionary<char, ushort> OemVk = new()
    {
        [','] = 0xBC, ['.'] = 0xBE, [';'] = 0xBA, ['/'] = 0xBF, ['\''] = 0xDE,
        ['['] = 0xDB, [']'] = 0xDD, ['\\'] = 0xDC, ['-'] = 0xBD, ['='] = 0xBB, ['`'] = 0xC0,
    };

    /// <summary>命名键的虚拟键码。</summary>
    private static readonly Dictionary<string, ushort> NamedVk = new(StringComparer.Ordinal)
    {
        ["Space"] = 0x20, ["Enter"] = 0x0D, ["Tab"] = 0x09, ["Back"] = 0x08, ["Escape"] = 0x1B,
        ["Shift"] = 0xA0, ["Ctrl"] = 0xA2, ["Alt"] = 0xA4,
        ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["Home"] = 0x24, ["End"] = 0x23,
        ["Insert"] = 0x2D, ["Delete"] = 0x2E,
        ["Up"] = 0x26, ["Down"] = 0x28, ["Left"] = 0x25, ["Right"] = 0x27,
        ["F1"] = 0x70, ["F2"] = 0x71, ["F3"] = 0x72, ["F4"] = 0x73, ["F5"] = 0x74, ["F6"] = 0x75,
        ["F7"] = 0x76, ["F8"] = 0x77, ["F9"] = 0x78, ["F10"] = 0x79, ["F11"] = 0x7A, ["F12"] = 0x7B,
        ["NumPad0"] = 0x60, ["NumPad1"] = 0x61, ["NumPad2"] = 0x62, ["NumPad3"] = 0x63, ["NumPad4"] = 0x64,
        ["NumPad5"] = 0x65, ["NumPad6"] = 0x66, ["NumPad7"] = 0x67, ["NumPad8"] = 0x68, ["NumPad9"] = 0x69,
    };

    /// <summary>要用「扩展键」标志发送的虚拟键码（方向键、翻页、Home/End、Insert/Delete、小键盘除号）。</summary>
    private static readonly HashSet<ushort> ExtendedVk = new()
    {
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x6F,
    };

    /// <summary>
    /// 键名 → 虚拟键码。支持字母、数字、标点、命名键（F1..F12、PageUp、NumPad0..9、Shift 等）。
    /// 鼠标键与认不出的键名返回 false。
    /// </summary>
    public static bool TryVkCode(string? keyName, out ushort vk)
    {
        vk = 0;
        string name = KeymapProfile.CanonicalKeyName(keyName);
        if (name.Length == 0) return false;

        if (name.Length == 1)
        {
            char c = name[0];
            if (c is >= 'A' and <= 'Z') { vk = c; return true; }
            if (c is >= '0' and <= '9') { vk = c; return true; }
            return OemVk.TryGetValue(c, out vk);
        }
        return NamedVk.TryGetValue(name, out vk);
    }

    /// <summary>键名 → 鼠标键。不是鼠标键则返回 false。</summary>
    public static bool TryMouseButton(string? keyName, out MouseButton button)
    {
        button = MouseButton.Left;
        string name = KeymapProfile.CanonicalKeyName(keyName);
        switch (name)
        {
            case "MouseLeft": button = MouseButton.Left; return true;
            case "MouseRight": button = MouseButton.Right; return true;
            case "MouseMiddle": button = MouseButton.Middle; return true;
            default: return false;
        }
    }

    /// <summary>字符形式 → 键名（命名键的哨兵字符在这里还原）。</summary>
    public static string KeyNameOf(char c) => KeymapProfile.NameOfKeyChar(c);

    // ---------------------------------------------------------------- 发送

    /// <returns>true = 事件注入成功；false = 被系统拦截（SendInput 返回 0）或驱动报告失败。</returns>
    private static bool SendKeyVk(bool down, ushort vk, bool extended)
    {
        // 罗技驱动后端：直接发标准 HID 键盘报告，不走 SendInput（注入标记那条链路）。
        if (Backend == BackendKind.LogitechGHub)
            return IbDriver.Keybd(down, vk);

        // 一律发扫描码（wVk=0 + KEYEVENTF_SCANCODE）：很多目标程序/DirectInput 只认扫描码
        uint flags = (down ? 0u : KEYEVENTF_KEYUP) | KEYEVENTF_SCANCODE;
        if (extended) flags |= KEYEVENTF_EXTENDEDKEY;

        var ki = new KEYBDINPUT
        {
            wVk = 0,
            wScan = MapVirtualKeyW(vk, MAPVK_VK_TO_VSC),
            dwFlags = flags,
            time = 0,
            dwExtraInfo = IntPtr.Zero
        };

        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion { ki = ki }
        };
        return SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>()) == 1;
    }

    /// <returns>true = 事件注入成功；false = 被系统拦截（SendInput 返回 0）。</returns>
    private static bool SendMouse(MouseButton button, bool down)
    {
        if (!OperatingSystem.IsWindows()) return false;
        // 新版 G HUB 砍掉了鼠标驱动，罗技后端只发键盘。鼠标键（八度/半音修饰）仍走 SendInput，
        // 目标程序屏蔽注入时这些键会发不出去 —— 记一次日志提醒，不刷屏。
        if (Backend == BackendKind.LogitechGHub && !_mouseWarnedInDriverMode)
        {
            _mouseWarnedInDriverMode = true;
            Persist.LogFile.Append("[输入] 罗技 G HUB 驱动模式不支持鼠标键：方案里的鼠标键仍走 SendInput。");
        }
        uint flag = button switch
        {
            MouseButton.Left => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
            MouseButton.Right => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            _ => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP
        };

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = 0,
                    dwFlags = flag,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        return SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>()) == 1;
    }

    /// <summary>按下一个键。键名可以是方案里的任何键名，也可以是 "MouseLeft" / "MouseRight" / "MouseMiddle"。</summary>
    public static void KeyDown(string? keyName) => SendKeyByName(keyName, true);

    /// <summary>松开一个键。名字规则同 <see cref="KeyDown(string?)"/>。</summary>
    public static void KeyUp(string? keyName) => SendKeyByName(keyName, false);

    private static void SendKeyByName(string? keyName, bool down)
    {
        if (!OperatingSystem.IsWindows()) return;

        // 鼠标键：记账走 HeldButtons
        if (TryMouseButton(keyName, out var button))
        {
            bool changed;
            lock (HeldGate) changed = down ? HeldButtons.Add(button) : HeldButtons.Remove(button);
            if (!changed) return;
            if (!SendMouse(button, down))
            {
                // 注入失败：回滚记账（抬起失败要留账等 ReleaseEverything 再抬；按下失败别记，
                // 否则账上以为按着，实际没按下）
                lock (HeldGate) { if (down) HeldButtons.Remove(button); else HeldButtons.Add(button); }
            }
            return;
        }

        // 记账（A04）：只记不在同一状态的键，重复调用不发也不重复记
        string norm = NormKey(keyName);
        bool changedKey;
        lock (HeldGate) changedKey = down ? HeldKeys.Add(norm) : HeldKeys.Remove(norm);
        if (!changedKey) return;

        if (!TryVkCode(keyName, out ushort vk))
        {
            // 认不出的键名：回滚记账，避免留一条永远不会被释放的记录
            lock (HeldGate) { if (down) HeldKeys.Remove(norm); else HeldKeys.Add(norm); }
            return;
        }
        if (!SendKeyVk(down, vk, ExtendedVk.Contains(vk)))
        {
            // 注入失败（SendInput 返回 0，被系统拦截）：回滚记账。
            // KeyUp 失败必须把键留回账里 —— 否则账上以为已抬、实际还按着，
            // 这个键就物理卡死，连 ReleaseEverything 都救不回来；KeyDown 失败则别记账。
            lock (HeldGate) { if (down) HeldKeys.Remove(norm); else HeldKeys.Add(norm); }
        }
    }

    /// <summary>按下一个键（字符形式）。命名键的哨兵字符会还原成键名。</summary>
    public static void KeyDown(char c) => SendKeyByChar(c, true);

    /// <summary>松开一个键（字符形式）。</summary>
    public static void KeyUp(char c) => SendKeyByChar(c, false);

    private static void SendKeyByChar(char c, bool down)
    {
        string name = KeyNameOf(c);
        if (name.Length == 0) return;
        SendKeyByName(name, down);
    }

    public static void MouseDown(MouseButton b) => SendMouse(b, true);
    public static void MouseUp(MouseButton b) => SendMouse(b, false);

    /// <summary>
    /// 只把本程序真正按下过的键/鼠标键抬起，用于停止/暂停时清理状态（A04）。
    /// 不再遍历整张方案的键位表，也不补发硬编码的兜底键：用户自己物理按住的键不归本程序管，
    /// 抬掉它们会让用户正在弹的音或正在用的修饰键中断。
    /// 记账在 <see cref="KeyDown(string?)"/> / <see cref="KeyUp(string?)"/> 里完成，只记状态真正变化的键。
    /// </summary>
    public static void ReleaseEverything()
    {
        if (!OperatingSystem.IsWindows()) return;

        // 一次在锁内取一笔并结账、锁外抬键，循环到账空为止。
        // 不能"锁内快照、锁外逐个抬"：快照后并发的 KeyDown 会落进快照之外的账里，漏抬。
        while (true)
        {
            string? key = null;
            MouseButton? button = null;
            lock (HeldGate)
            {
                foreach (var k in HeldKeys) { key = k; break; }
                if (key == null)
                    foreach (var b in HeldButtons) { button = b; break; }
                if (key == null && button == null) return;   // 账已空
                if (key != null) HeldKeys.Remove(key);
                else HeldButtons.Remove(button!.Value);
            }
            // 账已结，直接发抬键（不走 SendKeyByName 的记账：抬起失败也不再记回，避免死循环）
            if (key != null)
            {
                if (TryVkCode(key, out ushort vk)) SendKeyVk(false, vk, ExtendedVk.Contains(vk));
            }
            else SendMouse(button!.Value, false);
        }
    }
}

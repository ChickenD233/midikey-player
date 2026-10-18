using System.Runtime.InteropServices;
using MidiKeyPlayer.Engine;

namespace MidiKeyPlayer.Input;

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

    public static uint PidOfWindow(IntPtr hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid;
    }

    public static IntPtr ForegroundWindow =>
        OperatingSystem.IsWindows() ? GetForegroundWindow() : IntPtr.Zero;

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

    public static void BringToForeground(IntPtr hWnd)
    {
        if (OperatingSystem.IsWindows() && hWnd != IntPtr.Zero) SetForegroundWindow(hWnd);
    }

    public static bool IsSupported => OperatingSystem.IsWindows();

    private static bool _mouseWarnedInDriverMode;   

    public enum BackendKind { SendInput = 0, LogitechGHub = 1 }

    public static BackendKind Backend { get; private set; } = BackendKind.SendInput;

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

    public static bool EnsureBackend(out string error, out string? warn)
    {
        error = "";
        warn = null;
        if (Backend != BackendKind.LogitechGHub) return true;
        if (IbDriver.TryInit(out error, out warn)) return true;
        Backend = BackendKind.SendInput;
        error += "\n本次先退回 SendInput。";
        return false;
    }

    private static readonly object HeldGate = new();
    private static readonly HashSet<string> HeldKeys = new(StringComparer.Ordinal);
    private static readonly HashSet<MouseButton> HeldButtons = new();

    private static string NormKey(string? keyName)
    {
        string name = keyName ?? "";
        if (name.Length == 1) return char.ToUpperInvariant(name[0]).ToString();
        return name;
    }

    private static readonly Dictionary<char, ushort> OemVk = new()
    {
        [','] = 0xBC, ['.'] = 0xBE, [';'] = 0xBA, ['/'] = 0xBF, ['\''] = 0xDE,
        ['['] = 0xDB, [']'] = 0xDD, ['\\'] = 0xDC, ['-'] = 0xBD, ['='] = 0xBB, ['`'] = 0xC0,
    };

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

    private static readonly HashSet<ushort> ExtendedVk = new()
    {
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x6F,
    };

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

    public static string KeyNameOf(char c) => KeymapProfile.NameOfKeyChar(c);

    private static bool SendKeyVk(bool down, ushort vk, bool extended)
    {
        if (Backend == BackendKind.LogitechGHub)
            return IbDriver.Keybd(down, vk);

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

    private static bool SendMouse(MouseButton button, bool down)
    {
        if (!OperatingSystem.IsWindows()) return false;
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

    public static void KeyDown(string? keyName) => SendKeyByName(keyName, true);

    public static void KeyUp(string? keyName) => SendKeyByName(keyName, false);

    private static void SendKeyByName(string? keyName, bool down)
    {
        if (!OperatingSystem.IsWindows()) return;

        if (TryMouseButton(keyName, out var button))
        {
            bool changed;
            lock (HeldGate) changed = down ? HeldButtons.Add(button) : HeldButtons.Remove(button);
            if (!changed) return;
            if (!SendMouse(button, down))
            {
                lock (HeldGate) { if (down) HeldButtons.Remove(button); else HeldButtons.Add(button); }
            }
            return;
        }

        string norm = NormKey(keyName);
        bool changedKey;
        lock (HeldGate) changedKey = down ? HeldKeys.Add(norm) : HeldKeys.Remove(norm);
        if (!changedKey) return;

        if (!TryVkCode(keyName, out ushort vk))
        {
            lock (HeldGate) { if (down) HeldKeys.Remove(norm); else HeldKeys.Add(norm); }
            return;
        }
        if (!SendKeyVk(down, vk, ExtendedVk.Contains(vk)))
        {
            lock (HeldGate) { if (down) HeldKeys.Remove(norm); else HeldKeys.Add(norm); }
        }
    }

    public static void KeyDown(char c) => SendKeyByChar(c, true);

    public static void KeyUp(char c) => SendKeyByChar(c, false);

    private static void SendKeyByChar(char c, bool down)
    {
        string name = KeyNameOf(c);
        if (name.Length == 0) return;
        SendKeyByName(name, down);
    }

    public static void MouseDown(MouseButton b) => SendMouse(b, true);
    public static void MouseUp(MouseButton b) => SendMouse(b, false);

    public static void ReleaseEverything()
    {
        if (!OperatingSystem.IsWindows()) return;

        while (true)
        {
            string? key = null;
            MouseButton? button = null;
            lock (HeldGate)
            {
                foreach (var k in HeldKeys) { key = k; break; }
                if (key == null)
                    foreach (var b in HeldButtons) { button = b; break; }
                if (key == null && button == null) return;   
                if (key != null) HeldKeys.Remove(key);
                else HeldButtons.Remove(button!.Value);
            }
            if (key != null)
            {
                if (TryVkCode(key, out ushort vk)) SendKeyVk(false, vk, ExtendedVk.Contains(vk));
            }
            else SendMouse(button!.Value, false);
        }
    }
}

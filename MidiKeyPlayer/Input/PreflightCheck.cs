using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MidiKeyPlayer.Input;

public static class PreflightCheck
{
    public enum Status { Pass, Warn, Fail }

    public sealed record Check(string Name, Status Status, string Detail)
    {
        public Check(string name, bool passed, string detail)
            : this(name, passed ? Status.Pass : Status.Fail, detail) { }

        public bool Passed => Status == Status.Pass;
        public bool Blocked => Status == Status.Fail;
    }

    public sealed class Report
    {
        public Check Admin { get; init; } = new("管理员", false, "");
        public Check Ime { get; init; } = new("输入法", false, "");
        public bool AllPassed => Admin.Passed && Ime.Passed;
        public bool HasBlocked => Admin.Blocked || Ime.Blocked;
    }

    public enum ImeMode
    {
        Direct,
        Native,
        Unknown
    }

    public sealed record ImeProbe(
        ushort WindowLayout, uint FocusedThread,
        bool ImeWndRead, int ImeWndOpen, uint ImeWndConversion,
        int OpenStatus, bool? Alphanumeric, uint RawConversion,
        bool ThreadLayoutRead, ushort ThreadLayout, bool CallerLayoutRead, ushort CallerLayout);

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO info);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeoutMs, out IntPtr result);

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetContext(IntPtr hWnd);

    [DllImport("imm32.dll")]
    private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);

    [DllImport("imm32.dll")]
    private static extern bool ImmGetOpenStatus(IntPtr hIMC);

    [DllImport("imm32.dll")]
    private static extern bool ImmGetConversionStatus(IntPtr hIMC, out uint conversion, out uint sentence);

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hWnd);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
        out uint tokenInformation, uint tokenInformationLength, out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;

    private const uint WM_IME_CONTROL = 0x0283;
    private const int IMC_GETOPENSTATUS = 0x0005;
    private const int IMC_GETCONVERSIONMODE = 0x0001;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint SMTO_BLOCK = 0x0001;
    private const uint IME_CMODE_NATIVE = 0x0001;
    private const uint IME_CMODE_KATAKANA = 0x0002;
    private const uint IME_CMODE_FULLSHAPE = 0x0008;
    private const uint IME_CMODE_ROMAN = 0x0010;

    public static bool IsSelfElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_QUERY, out token))
                return false;
            if (!GetTokenInformation(token, TokenElevation, out uint elevated, sizeof(uint), out _))
                return false;
            return elevated != 0;
        }
        catch { return false; }
        finally { if (token != IntPtr.Zero) CloseHandle(token); }
    }

    public static ushort ForegroundKeyboardLayoutId()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        IntPtr h = GetForegroundWindow();
        if (h == IntPtr.Zero) return 0;
        uint tid = GetWindowThreadProcessId(h, out _);
        IntPtr hkl = GetKeyboardLayout(tid == 0 ? 0 : tid);
        return (ushort)(hkl.ToInt64() & 0xFFFF);
    }

    public static bool IsCjkLayout(ushort lang) =>
        lang is 0x0804 or 0x0404 or 0x0411 or 0x0412;   

    public static string LayoutName(ushort lang) => lang switch
    {
        0x0804 => "简体中文",
        0x0404 => "繁体中文",
        0x0411 => "日文",
        0x0412 => "韩文",
        0x0409 => "英文",
        _ => $"0x{lang:X4}"
    };

    public static ImeMode Decide(ushort windowLayout, int open, bool? alphanumeric,
                                 ushort focusedThreadLayout = 0)
    {
        if (windowLayout != 0 && !IsCjkLayout(windowLayout)) return ImeMode.Direct;
        if (focusedThreadLayout != 0 && !IsCjkLayout(focusedThreadLayout)) return ImeMode.Direct;

        if (open == 0) return ImeMode.Direct;
        if (alphanumeric == true) return ImeMode.Direct;

        if (windowLayout == 0) return ImeMode.Unknown;   
        if (open < 0) return ImeMode.Unknown;            
        if (alphanumeric == false) return ImeMode.Native;
        return ImeMode.Unknown;
    }

    public static (ImeMode Mode, ImeProbe Probe) DetectImeModeDetailed()
    {
        if (!OperatingSystem.IsWindows())
            return (ImeMode.Unknown, new ImeProbe(0, 0, false, -1, 0, -1, null, 0, false, 0, false, 0));

        IntPtr h = GetForegroundWindow();
        ushort windowLayout = 0;
        uint tid = 0;
        if (h != IntPtr.Zero)
        {
            tid = GetWindowThreadProcessId(h, out _);
            IntPtr hkl = GetKeyboardLayout(tid);
            windowLayout = (ushort)(hkl.ToInt64() & 0xFFFF);
        }

        uint focused = FocusedGuiThread(tid);
        bool threadRead = false;
        ushort threadLayout = 0;
        if (focused != 0)
        {
            IntPtr hkl2 = GetKeyboardLayout(focused);
            if (hkl2 != IntPtr.Zero)
            {
                threadRead = true;
                threadLayout = (ushort)(hkl2.ToInt64() & 0xFFFF);
            }
        }

        uint caller = GetCurrentThreadId();
        IntPtr hkl3 = GetKeyboardLayout(caller);
        bool callerRead = hkl3 != IntPtr.Zero;
        ushort callerLayout = callerRead ? (ushort)(hkl3.ToInt64() & 0xFFFF) : (ushort)0;

        var empty = new ImeProbe(windowLayout, focused, false, -1, 0, -1, null, 0, threadRead, threadLayout, callerRead, callerLayout);
        if ((windowLayout != 0 && !IsCjkLayout(windowLayout)) ||
            (threadRead && !IsCjkLayout(threadLayout)))
            return (ImeMode.Direct, empty);

        var (wndOk, wndOpen, wndConv) = ReadImeWindowState(h);

        int open = ReadOpenStatus(h);
        bool? alnum = open == 1 ? ReadAlphanumeric(h) : null;
        uint rawConv = ReadRawConversion(h);

        ImeMode mode = Conclude(wndOk, wndOpen, wndConv, open, alnum);
        var probe = empty with
        {
            ImeWndRead = wndOk,
            ImeWndOpen = wndOk ? wndOpen : -1,
            ImeWndConversion = wndOk ? wndConv : 0,
            OpenStatus = open,
            Alphanumeric = alnum,
            RawConversion = rawConv
        };
        return (mode, probe);
    }

    private static uint ReadRawConversion(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return 0;
        try
        {
            IntPtr r = SendMessageTimeoutW(hWnd, WM_IME_CONTROL, (IntPtr)IMC_GETCONVERSIONMODE, IntPtr.Zero,
                SMTO_ABORTIFHUNG | SMTO_BLOCK, 150, out IntPtr result);
            return r == IntPtr.Zero ? 0 : (uint)result.ToInt64();
        }
        catch { return 0; }
    }

    public static ImeMode DetectImeMode() => DetectImeModeDetailed().Mode;

    private static uint FocusedGuiThread(uint fallbackThread)
    {
        try
        {
            if (fallbackThread != 0)
            {
                var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
                if (GetGUIThreadInfo(fallbackThread, ref info) && info.hwndFocus != IntPtr.Zero)
                {
                    uint t = GetWindowThreadProcessId(info.hwndFocus, out _);
                    if (t != 0) return t;
                }
            }
        }
        catch { }
        return fallbackThread;
    }

    private static int ReadOpenStatus(IntPtr hWnd)
    {
        if (hWnd != IntPtr.Zero)
        {
            try
            {
                IntPtr r = SendMessageTimeoutW(hWnd, WM_IME_CONTROL, (IntPtr)IMC_GETOPENSTATUS, IntPtr.Zero,
                    SMTO_ABORTIFHUNG | SMTO_BLOCK, 150, out IntPtr result);
                if (r != IntPtr.Zero) return result != IntPtr.Zero ? 1 : 0;
            }
            catch { }
        }

        IntPtr hImc = IntPtr.Zero;
        try
        {
            hImc = ImmGetContext(hWnd);
            if (hImc == IntPtr.Zero) return -1;
            return ImmGetOpenStatus(hImc) ? 1 : 0;
        }
        catch { return -1; }
        finally { if (hImc != IntPtr.Zero) ImmReleaseContext(hWnd, hImc); }
    }

    private static bool? ReadAlphanumeric(IntPtr hWnd)
    {
        IntPtr hImc = IntPtr.Zero;
        try
        {
            hImc = ImmGetContext(hWnd);
            if (hImc == IntPtr.Zero) return null;
            if (!ImmGetConversionStatus(hImc, out uint conversion, out _)) return null;
            return IsAlphanumeric(conversion);
        }
        catch { return null; }
        finally { if (hImc != IntPtr.Zero) ImmReleaseContext(hWnd, hImc); }
    }

    private static (bool Ok, int Open, uint Conversion) ReadImeWindowState(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return (false, -1, 0);
        try
        {
            IntPtr imeWnd = ImmGetDefaultIMEWnd(hWnd);
            if (imeWnd == IntPtr.Zero || imeWnd == hWnd) return (false, -1, 0);

            int open = -1;
            IntPtr r1 = SendMessageTimeoutW(imeWnd, WM_IME_CONTROL, (IntPtr)IMC_GETOPENSTATUS, IntPtr.Zero,
                SMTO_ABORTIFHUNG | SMTO_BLOCK, 150, out IntPtr o1);
            if (r1 != IntPtr.Zero) open = o1 != IntPtr.Zero ? 1 : 0;

            uint conv = 0;
            IntPtr r2 = SendMessageTimeoutW(imeWnd, WM_IME_CONTROL, (IntPtr)IMC_GETCONVERSIONMODE, IntPtr.Zero,
                SMTO_ABORTIFHUNG | SMTO_BLOCK, 150, out IntPtr o2);
            if (r2 != IntPtr.Zero) conv = (uint)o2.ToInt64();

            return (true, open, conv);
        }
        catch { return (false, -1, 0); }
    }

    private static bool IsAlphanumeric(uint conversion) => (conversion & IME_CMODE_NATIVE) == 0;

    private static bool IsWideOrKana(uint conversion) =>
        (conversion & (IME_CMODE_FULLSHAPE | IME_CMODE_KATAKANA)) != 0;

    public static ImeMode Conclude(bool wndOk, int wndOpen, uint wndConv, int open, bool? alnum)
    {
        if (!wndOk)
        {
            if (open == 0 || alnum == true) return ImeMode.Direct;
            if (open == 1 && alnum == false) return ImeMode.Native;
            return ImeMode.Unknown;
        }

        if (wndOpen == 0) return ImeMode.Direct;      
        if (wndConv == 0) return ImeMode.Direct;      
        return IsAlphanumeric(wndConv) ? ImeMode.Direct : ImeMode.Native;
    }

    public static string Describe(ImeProbe p) =>
        $"窗口布局=0x{p.WindowLayout:X4} 焦点线程布局={(p.ThreadLayoutRead ? $"0x{p.ThreadLayout:X4}" : "读不到")} " +
        $"IME窗口={(p.ImeWndRead ? $"开={p.ImeWndOpen} 转换模式=0x{p.ImeWndConversion:X4}" : "读不到")} " +
        $"前台窗口开={p.OpenStatus} 转换模式=0x{p.RawConversion:X4}";

    public static Report Run()
    {
        if (!OperatingSystem.IsWindows())
            return new Report
            {
                Admin = new Check("管理员", false, "非 Windows，无法模拟输入"),
                Ime = new Check("输入法", false, "非 Windows")
            };

        bool admin = IsSelfElevated();
        var adminCheck = admin
            ? new Check("管理员", Status.Pass, "已提权")
            : new Check("管理员", Status.Warn,
                "未提权：普通目标程序不受影响；目标以管理员运行时 SendInput 会被拦截"
                + "（可换「罗技 G HUB 驱动」输入方式，不受此限；或点「以管理员重启」）");

        var (mode, probe) = DetectImeModeDetailed();
        string layout = probe.WindowLayout == 0 ? "读不到布局" : $"{LayoutName(probe.WindowLayout)}布局";

        Check imeCheck;
        if (mode == ImeMode.Direct)
        {
            string how = probe.ImeWndRead
                ? (probe.ImeWndOpen == 0 ? "输入法已关闭" : "英文/半角输入状态")
                : "英文输入状态";
            imeCheck = new Check("输入法", Status.Pass, $"{layout}，{how}，按键直达");
        }
        else if (mode == ImeMode.Native)
        {
            imeCheck = new Check("输入法", Status.Fail,
                $"{layout}，输入法在中文输入状态，请按 Shift 切英文");
        }
        else
        {
            imeCheck = new Check("输入法", Status.Warn,
                $"{layout}，读不准输入法状态（{Describe(probe)}）。若目标程序里收不到按键，按 Shift 切英文");
        }

        return new Report { Admin = adminCheck, Ime = imeCheck };
    }
}

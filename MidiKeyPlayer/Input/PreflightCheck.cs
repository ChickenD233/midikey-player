using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MidiKeyPlayer.Input;

/// <summary>
/// 播放前自检：管理员权限、前台窗口输入法状态。
///
/// 输入法这块只有一个真正要判的问题：**现在按下的字母键会不会被输入法截走**。
/// 中文键盘布局本身不影响，中文输入法切到英文模式也只是输入法内部状态变化，
/// 键盘布局和语言列表都不变 —— 所以不能只看布局，必须直接问"现在是不是英文输入"。
/// </summary>
public static class PreflightCheck
{
    /// <summary>自检结论。三态：通过 / 提醒 / 不通过。</summary>
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

    /// <summary>输入法状态检测结果。</summary>
    public enum ImeMode
    {
        /// <summary>按键直达目标程序（非中文输入状态）。</summary>
        Direct,
        /// <summary>中文/假名输入状态，会截走按键。</summary>
        Native,
        /// <summary>读不到状态。</summary>
        Unknown
    }

    /// <summary>检测过程中收到的证据，用来给用户看得懂的说明，也方便排障。</summary>
    public sealed record ImeProbe(
        ushort WindowLayout, uint FocusedThread,
        bool ImeWndRead, int ImeWndOpen, uint ImeWndConversion,
        int OpenStatus, bool? Alphanumeric, uint RawConversion,
        bool ThreadLayoutRead, ushort ThreadLayout, bool CallerLayoutRead, ushort CallerLayout);

    // ---------------- P/Invoke ----------------
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

    // 输入法状态：WM_IME_CONTROL / IMC_GETOPENSTATUS 可以跨进程查询
    private const uint WM_IME_CONTROL = 0x0283;
    private const int IMC_GETOPENSTATUS = 0x0005;
    private const int IMC_GETCONVERSIONMODE = 0x0001;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint SMTO_BLOCK = 0x0001;
    /// <summary>IME_CMODE_NATIVE：中文/假名（"原生"）输入模式。这个位为 0 才是英文/半角。</summary>
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

    /// <summary>
    /// 前台窗口所属线程的键盘布局；低 16 位是 LANGID（0x0804 简中、0x0404 繁中、0x0411 日文）。
    /// 注意：中文输入法用 Shift 切英文时键盘布局不变，所以它**读不出**中英文状态。
    /// </summary>
    public static ushort ForegroundKeyboardLayoutId()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        IntPtr h = GetForegroundWindow();
        if (h == IntPtr.Zero) return 0;
        uint tid = GetWindowThreadProcessId(h, out _);
        IntPtr hkl = GetKeyboardLayout(tid == 0 ? 0 : tid);
        return (ushort)(hkl.ToInt64() & 0xFFFF);
    }

    /// <summary>这个 LANGID 是否属于中日韩（需要看输入法开关状态）的语言。</summary>
    public static bool IsCjkLayout(ushort lang) =>
        lang is 0x0804 or 0x0404 or 0x0411 or 0x0412;   // 简中 / 繁中 / 日文 / 韩文

    /// <summary>LANGID 的中文名（写日志用）。</summary>
    public static string LayoutName(ushort lang) => lang switch
    {
        0x0804 => "简体中文",
        0x0404 => "繁体中文",
        0x0411 => "日文",
        0x0412 => "韩文",
        0x0409 => "英文",
        _ => $"0x{lang:X4}"
    };

    /// <summary>
    /// 纯判定逻辑（便于测试）。
    /// 只要有一条证据说明"不是中文输入状态"，就放行 —— 判错的代价是误解，
    /// 而误报"不通过"会让用户每次都得手动确认，还会拦住本来能用的配置。
    /// </summary>
    /// <param name="windowLayout">前台窗口的键盘布局 LANGID，0 = 读不到。</param>
    /// <param name="open">输入法开关：1 开、0 关、-1 读不到。</param>
    /// <param name="alphanumeric">是否英文/半角模式；仅在 open = 1 时有意义。</param>
    /// <param name="focusedThreadLayout">前台窗口里**真正获得焦点**的那个 GUI 线程的键盘布局，0 = 读不到。</param>
    public static ImeMode Decide(ushort windowLayout, int open, bool? alphanumeric,
                                 ushort focusedThreadLayout = 0)
    {
        // 只要有一条证据说明布局不是中日韩 → 一定不截键
        if (windowLayout != 0 && !IsCjkLayout(windowLayout)) return ImeMode.Direct;
        if (focusedThreadLayout != 0 && !IsCjkLayout(focusedThreadLayout)) return ImeMode.Direct;

        // 布局读不到时，仍然可以用"输入法关着 / 英文模式"这两条证据放行
        if (open == 0) return ImeMode.Direct;
        if (alphanumeric == true) return ImeMode.Direct;

        if (windowLayout == 0) return ImeMode.Unknown;   // 布局也没读到，无法判断
        if (open < 0) return ImeMode.Unknown;            // 布局是中日韩，但开关读不到
        if (alphanumeric == false) return ImeMode.Native;
        return ImeMode.Unknown;
    }

    /// <summary>
    /// 读前台窗口的输入法状态。带上检测过程里的原始证据，便于排障与说明。
    /// </summary>
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

        // 真正吃键的是"有键盘焦点的那个线程"。顶层窗口的线程不一定等于它。
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

        // 证据一：布局不是中日韩 → 一定不截键
        var empty = new ImeProbe(windowLayout, focused, false, -1, 0, -1, null, 0, threadRead, threadLayout, callerRead, callerLayout);
        if ((windowLayout != 0 && !IsCjkLayout(windowLayout)) ||
            (threadRead && !IsCjkLayout(threadLayout)))
            return (ImeMode.Direct, empty);

        // 证据二（最可靠）：默认输入法窗口的开关与转换模式
        var (wndOk, wndOpen, wndConv) = ReadImeWindowState(h);

        // 证据三：直接问前台窗口（有些窗口自己处理 WM_IME_CONTROL）
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

    /// <summary>原始转换模式位（跨进程查）。0 = 读不到。写进日志用于排障。</summary>
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

    /// <summary>输入法状态（不带检测证据）。</summary>
    public static ImeMode DetectImeMode() => DetectImeModeDetailed().Mode;

    /// <summary>前台窗口线程里获得键盘焦点的 GUI 线程；读不到返回 0。</summary>
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
        catch { /* 读不到就用回退值 */ }
        return fallbackThread;
    }

    /// <summary>读输入法开关：1 开、0 关、-1 读不到。跨进程用 WM_IME_CONTROL，失败再退回 ImmGetContext。</summary>
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
            catch { /* 目标进程无响应时忽略，退回 ImmGetContext */ }
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

    /// <summary>输入法是否处于英文/半角模式：true 是、false 否、null 读不到。</summary>
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

    /// <summary>
    /// 读输入法状态的最可靠来源：前台窗口的**默认输入法窗口**（ImmGetDefaultIMEWnd）。
    ///
    /// 直接问前台窗口（尤其是目标窗口）往往读不到，因为它不一定处理 WM_IME_CONTROL；
    /// 而默认输入法窗口是系统为这个窗口建的 IME 窗口，它的 IMC_GETCONVERSIONMODE
    /// 能反映"现在是中文还是英文"—— 微软拼音按 Shift 切换时这个值会从 0x0401
    /// （IME_CMODE_NATIVE，中文）变成 0x0000（半角英文）。
    /// </summary>
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

    /// <summary>
    /// 转换模式是否属于"英文/半角"，也就是按键不会被输入法截走。
    /// 判据是 IME_CMODE_NATIVE 位为 0（微软拼音中文态是 0x0401，英文态是 0x0000）。
    /// 注意 IME_CMODE_ALPHANUMERIC 本身是 0x0000，不能拿它做位与判断 —— 那样永远为真。
    /// </summary>
    private static bool IsAlphanumeric(uint conversion) => (conversion & IME_CMODE_NATIVE) == 0;

    /// <summary>转换模式是否属于"全角/假名"，这种状态一定会截键。</summary>
    private static bool IsWideOrKana(uint conversion) =>
        (conversion & (IME_CMODE_FULLSHAPE | IME_CMODE_KATAKANA)) != 0;

    /// <summary>
    /// 从各条证据得出结论。
    ///
    /// 判"不通过"要有硬证据：输入法开着，且转换模式明确不是英文
    /// （中文的 0x0401、全角假名的 0x0411/0x0419 都带 IME_CMODE_NATIVE 或全角位）。
    /// 其余情况一律放行或只提醒 —— 误报"不通过"会拦住本来能用的配置（微软拼音按 Shift
    /// 切英文就是布局不变、只变转换模式），代价比漏报大得多。
    /// </summary>
    public static ImeMode Conclude(bool wndOk, int wndOpen, uint wndConv, int open, bool? alnum)
    {
        if (!wndOk)
        {
            // 没有输入法窗口这条证据，退回前台窗口那一路
            if (open == 0 || alnum == true) return ImeMode.Direct;
            if (open == 1 && alnum == false) return ImeMode.Native;
            return ImeMode.Unknown;
        }

        if (wndOpen == 0) return ImeMode.Direct;      // 输入法关着
        if (wndConv == 0) return ImeMode.Direct;      // 转换模式 0 = 英文/半角
        return IsAlphanumeric(wndConv) ? ImeMode.Direct : ImeMode.Native;
    }

    /// <summary>给日志用的一行证据。</summary>
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

        // ① 模拟按键受 UIPI 限制：仅当目标程序以管理员运行时，注入方才需要同权。
        //    默认不提权（asInvoker），所以这里只提醒、不判不通过 —— 普通目标程序完全不受影响。
        bool admin = IsSelfElevated();
        var adminCheck = admin
            ? new Check("管理员", Status.Pass, "已提权")
            : new Check("管理员", Status.Warn,
                "未提权：普通目标程序不受影响；目标以管理员运行时 SendInput 会被拦截"
                + "（可换「罗技 G HUB 驱动」输入方式，不受此限；或点「以管理员重启」）");

        // ② 输入法：只有"输入法开着且在中文/假名状态"才会截走按键。
        //    误报"不通过"会拦住本来能用的配置，所以读不准只提醒、不报错。
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

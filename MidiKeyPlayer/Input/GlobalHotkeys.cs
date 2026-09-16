using System.Runtime.InteropServices;
using System.Diagnostics;

namespace MidiKeyPlayer.Input;

/// <summary>
/// 全局热键（目标程序中也能触发）：WH_KEYBOARD_LL 低层钩子须配独立消息线程；
/// 回调在工作线程触发，UI 需自行 marshal。
/// </summary>
public static class GlobalHotkeys
{
    public static event Action<int, bool>? KeyState;   // (虚拟键码, 是否按下) 工作线程
    public static event Action<string>? Status;        // 注册状态/错误（工作线程）

    private static readonly object _sync = new();
    private static readonly HashSet<int> _active = new();
    private static Thread? _thread;
    private static volatile bool _running;
    private static readonly Dictionary<int, long> _lastDownTick = new();
    private static uint _winThreadId;
    private static readonly ManualResetEventSlim _ready = new(false);   // 钩子线程报告"线程 id 已就绪"

    public static bool IsAvailable => OperatingSystem.IsWindows();
    public static bool Running => _running;

    /// <summary>F1..F12 的虚拟键码。</summary>
    public static int FunctionKeyCode(int n)
    {
        if (n is < 1 or > 12) return 0;
        return 0x70 + n - 1;   // VK_F1..VK_F12
    }

    public static void SetActive(IEnumerable<int> codes)
    {
        lock (_sync)
        {
            _active.Clear();
            foreach (var c in codes)
                if (c != 0) _active.Add(c);
        }
    }

    public static bool Start()
    {
        if (_running || !IsAvailable) return _running;
        lock (_sync)
        {
            if (_running) return true;
            _ready.Reset();
            _running = true;
            _thread = new Thread(Worker) { IsBackground = true, Name = "GlobalHotkeys" };
            _thread.Start();
            // A08：阻塞等线程把 _winThreadId 写好（或注册失败）。否则 Stop() 可能在 id 写入前执行：
            // 那样发不出 WM_QUIT，_thread 又被置 null，钩子线程与低级键盘钩子终身泄漏。
            _ready.Wait(500);
            if (_running && _winThreadId == 0)
            {
                // 超时：线程 id 还没写好。不能把 _running 留真 —— Stop() 发不出 WM_QUIT，
                // 钩子线程泄漏，下次 Start() 还会再装一个钩子（同一按键触发两次）。
                // 按启动失败处理：再给一小段时间等 id 补写，等到就补发 WM_QUIT，等不到就放弃
                //（Worker 的消息循环以 _running 为条件，之后任何消息到达都会让它自行退出拆钩）。
                _running = false;
                if (_ready.Wait(200))
                {
                    uint tid = _winThreadId;
                    if (tid != 0) PostThreadMessageW(tid, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                }
            }
            return _running;
        }
    }

    public static void Stop()
    {
        _running = false;
        // 唤醒阻塞在 GetMessage 的钩子线程
        uint tid = _winThreadId;
        if (tid != 0)
            PostThreadMessageW(tid, WM_QUIT, IntPtr.Zero, IntPtr.Zero);

        // A16：等工作线程真正退出再放掉引用，否则紧接着的 Start() 会再建一个线程与第二个钩子
        Thread? t;
        lock (_sync)
        {
            t = _thread;
            _thread = null;
        }
        if (t != null && t.IsAlive && !ReferenceEquals(t, Thread.CurrentThread))
            t.Join(300);

        Interlocked.Exchange(ref _winThreadId, 0u);   // A08：复位，避免下次 Stop 对着已经没了的线程 id 发消息
    }

    // ================= 事件分发（工作线程） =================
    private static void RaiseKey(int code, bool down)
    {
        if (!down) return;   // 只用按下触发（避免重复/抬起抖动）

        lock (_sync)
        {
            if (!_active.Contains(code)) return;
            long now = Environment.TickCount64;
            if (_lastDownTick.TryGetValue(code, out long last) && now - last < 100) return; // 防连发
            _lastDownTick[code] = now;
        }
        // 订阅者代码不能同步跑在钩子回调里：处理一慢（如开演奏前的自检有多个 150ms 超时等待），
        // Windows 会按 LowLevelHooksTimeout 静默摘掉 LL 钩子，热键无声失效、还不报任何错。
        // 防连发判断留在钩子线程，真正的派发扔给线程池，回调立刻返回。
        int vk = code;
        Task.Run(() => KeyState?.Invoke(vk, true));
    }

    // ================= 低层键盘钩子 =================
    private const int WH_KEYBOARD_LL = 13;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_QUIT = 0x0012;
    private const uint LLKHF_INJECTED = 0x10;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    // PostThreadMessageW 认的是 Win32 原生线程 id，不是托管线程 id
    // （Environment.CurrentManagedThreadId 是另一套编号，发过去等于发丢）。
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private static readonly LowLevelKeyboardProc WinProc = WinHookCallback;
    private static IntPtr _winHook;

    private static IntPtr WinHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (uint)wParam == WM_KEYDOWN)
        {
            var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if ((kbd.flags & LLKHF_INJECTED) == 0)
                RaiseKey((int)kbd.vkCode, true);
        }
        return CallNextHookEx(_winHook, nCode, wParam, lParam);
    }

    private static void Worker()
    {
        try
        {
            // A08：先写线程 id 再注册钩子。这样 Start() 的 _ready.Wait 返回后 id 一定可用，
            // Stop() 一定能发出 WM_QUIT（注册失败时也能让消息循环立刻退出）。
            // 必须是原生线程 id：PostThreadMessageW 不认托管线程 id（Environment.CurrentManagedThreadId）。
            _winThreadId = GetCurrentThreadId();
            _ready.Set();

            using var cur = Process.GetCurrentProcess();
            IntPtr mod = cur.MainModule is { } m ? GetModuleHandleW(m.ModuleName) : IntPtr.Zero;
            _winHook = SetWindowsHookExW(WH_KEYBOARD_LL, WinProc, mod, 0);
            if (_winHook == IntPtr.Zero)
            {
                _running = false;
                Status?.Invoke("全局热键注册失败（系统钩子不可用）");
                return;
            }

            Status?.Invoke("全局热键已启用（目标程序中直接生效）");

            while (_running && GetMessageW(out _, IntPtr.Zero, 0, 0) > 0)
            {
                if (!_running) break;
            }

            UnhookWindowsHookEx(_winHook);
            _winHook = IntPtr.Zero;
        }
        catch (Exception ex)
        {
            _running = false;
            Status?.Invoke($"全局热键异常：{ex.Message}");
        }
        finally
        {
            _running = false;
            if (_winHook != IntPtr.Zero)   // 异常路径也要拆钩子，否则钩子留在系统里
            {
                UnhookWindowsHookEx(_winHook);
                _winHook = IntPtr.Zero;
            }
            _winThreadId = 0;
            _ready.Set();   // 注册前就抛异常时也要放行 Start()
        }
    }
}

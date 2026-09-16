using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MidiKeyPlayer.Engine;

/// <summary>
/// 内置试听：借用 Windows 自带的 MIDI 合成器（winmm 的 MIDI Out，默认设备通常是
/// "Microsoft GS Wavetable Synth"）发声，方便改谱时边改边听。
///
/// 为什么不用第三方音频库：本项目是自包含单文件发布，引入 NAudio 之类会明显
/// 增加体积，而 winmm 是系统自带的，P/Invoke 即可 —— 与项目已有的 SendInput /
/// user32 调用风格一致，零新增依赖。
///
/// 打不开设备时 <see cref="IsAvailable"/> 为 false，界面据此把开关灰掉并说明原因。
///
/// 本类只管"往设备发消息"（单音试听 + 清音）。整曲试听的调度在
/// <see cref="MidiPreviewSequencer"/>：独立线程 + 高精度等待 + 短前瞻派发。
/// </summary>
public sealed class MidiPreview : IDisposable
{
    private const uint MidiMapper = 0xFFFFFFFF;   // 选默认设备
    private const uint NoteOnBase = 0x90;         // 通道 0
    private const uint NoteOffBase = 0x80;
    private const uint ProgramChangeBase = 0xC0;
    private const uint ControlChangeBase = 0xB0;
    private const uint CcAllSoundOff = 120;       // 立刻静音（跳转/停止时最硬的保证）
    private const uint CcAllNotesOff = 123;       // 放开所有音
    private const int Channels = 16;
    private const int ProgramPiano = 0;           // 0 = Acoustic Grand Piano

    private readonly object _gate = new();
    private IntPtr _handle = IntPtr.Zero;
    private readonly HashSet<int> _sounding = new();
    private readonly List<System.Threading.Timer> _timers = new();
    private bool _disposed;

    private long _sendFailureCount;
    private long _handleEmptyCount;
    private long _loggedCount;
    private long _noteOnCount;
    private long _noteOffCount;

    /// <summary>设备是否可用。false 时所有方法都安全地什么都不做。</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>不可用时的原因（写进日志用）。</summary>
    public string LastError { get; private set; } = "";

    /// <summary>当前正在响的音高个数（我们发出去、还没收回的 note-on 数）。诊断用。</summary>
    public int SoundingCount { get { lock (_gate) { return _sounding.Count; } } }

    /// <summary>累计发出的 note-on / note-off 条数。清音验证用（winmm 没有"查询合成器在发声数"的接口）。</summary>
    public long NoteOnCount => Interlocked.Read(ref _noteOnCount);
    public long NoteOffCount => Interlocked.Read(ref _noteOffCount);

    /// <summary>发送失败次数（句柄为空、抛异常或 MMRESULT!=0）。</summary>
    public long SendFailureCount =>
        Interlocked.Read(ref _sendFailureCount) + Interlocked.Read(ref _handleEmptyCount);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint midiOutOpen(out IntPtr lphmo, uint uDeviceID,
        IntPtr dwCallback, IntPtr dwInstance, uint dwFlags);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint midiOutClose(IntPtr hmo);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint midiOutShortMsg(IntPtr hmo, uint dwMsg);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint midiOutReset(IntPtr hmo);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeEndPeriod(uint uPeriod);

    /// <summary>把系统定时器精度提到 1ms（进入播放前调用）。返回 true = 已生效，退出时要复原。</summary>
    internal static bool TryRaiseTimerResolution(uint milliseconds)
    {
        try { return timeBeginPeriod(milliseconds) == 0; }
        catch { return false; }
    }

    /// <summary>复原系统定时器精度（退出播放时调用）。</summary>
    internal static void RestoreTimerResolution(uint milliseconds)
    {
        try { timeEndPeriod(milliseconds); } catch { }
    }

    public MidiPreview()
    {
        try
        {
            uint rc = midiOutOpen(out IntPtr h, MidiMapper, IntPtr.Zero, IntPtr.Zero, 0);
            if (rc != 0)
            {
                LastError = $"midiOutOpen 失败（MMRESULT={rc}）";
                return;
            }
            _handle = h;
            IsAvailable = true;
            // 选钢琴音色：试听旋律比默认音色清楚
            Send(ProgramChangeBase | (uint)ProgramPiano);
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
        }
    }

    /// <summary>
    /// 试听一个音。<paramref name="autoRelease"/> 为 true 时到点自动放开（适合点一下听一声）；
    /// 为 false 时一直响，直到显式调用 <see cref="StopNote"/>（适合整曲试听）。
    /// </summary>
    public void PlayNote(int pitch, int milliseconds = 420, int velocity = 96, bool autoRelease = true)
    {
        if (!IsAvailable || _disposed) return;
        NoteOn(pitch, velocity);
        if (!autoRelease) return;

        // 自动放开。用一次性定时器而不是 Sleep，避免阻塞 UI 线程。
        System.Threading.Timer? t = null;
        t = new System.Threading.Timer(_ =>
        {
            lock (_gate) { NoteOffLocked(pitch); }
            lock (_gate) { _timers.Remove(t!); }
            t!.Dispose();
        }, null, Math.Max(60, milliseconds), Timeout.Infinite);
        lock (_gate) { _timers.Add(t); }
    }

    /// <summary>发一个 note-on（同音高已经在响时先放开，保证重新触发）。</summary>
    public void NoteOn(int pitch, int velocity = 96)
    {
        if (!IsAvailable || _disposed) return;
        int p = Math.Clamp(pitch, 0, 127);
        int v = Math.Clamp(velocity, 1, 127);
        lock (_gate)
        {
            NoteOffLocked(p);
            Send(NoteOnBase | ((uint)p << 8) | ((uint)v << 16));
            _sounding.Add(p);
            Interlocked.Increment(ref _noteOnCount);
        }
    }

    /// <summary>放开某个音高（整曲试听时由调度线程按谱面时间调用）。</summary>
    public void StopNote(int pitch)
    {
        if (!IsAvailable || _disposed) return;
        lock (_gate) { NoteOffLocked(Math.Clamp(pitch, 0, 127)); }
    }

    /// <summary>
    /// 立刻放开所有正在响的音：逐个补 note-off，再对 16 个通道发 CC123（All Notes Off）
    /// 与 CC120（All Sound Off）。停止、跳转、换曲、关窗口都走这里。
    /// </summary>
    public void StopAll() => AllNotesOff();

    /// <summary>同 <see cref="StopAll"/>：清掉全部发声，保证不留残音。</summary>
    public void AllNotesOff()
    {
        if (!IsAvailable || _disposed) return;
        lock (_gate)
        {
            foreach (int p in _sounding) SendRaw(NoteOffBase | ((uint)p << 8));
            _sounding.Clear();
            for (uint ch = 0; ch < Channels; ch++)
            {
                SendRaw((ControlChangeBase | ch) | (CcAllNotesOff << 8));
                SendRaw((ControlChangeBase | ch) | (CcAllSoundOff << 8));
            }
        }
    }

    private void NoteOffLocked(int pitch)
    {
        Send(NoteOffBase | ((uint)pitch << 8));
        _sounding.Remove(pitch);
    }

    /// <summary>发一条消息并计数。</summary>
    private void Send(uint message)
    {
        SendRaw(message);
    }

    /// <summary>真正调 winmm：检查句柄与返回码，失败写日志但不中断播放。</summary>
    private void SendRaw(uint message)
    {
        IntPtr h = _handle;
        if (h == IntPtr.Zero) { Interlocked.Increment(ref _handleEmptyCount); LogThrottled("设备句柄为空，消息丢弃"); return; }
        uint rc;
        try
        {
            rc = midiOutShortMsg(h, message);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _sendFailureCount);
            LogThrottled("midiOutShortMsg 抛异常：" + ex.GetType().Name);
            return;
        }
        byte status = (byte)(message & 0xF0);
        if (status == NoteOffBase) Interlocked.Increment(ref _noteOffCount);
        if (rc != 0)
        {
            Interlocked.Increment(ref _sendFailureCount);
            LogThrottled($"midiOutShortMsg 返回 MMRESULT={rc}，已忽略并继续播放");
        }
    }

    /// <summary>失败不中断播放，但写日志。限流：前 5 次全写，之后每 500 次写一条。</summary>
    private void LogThrottled(string what)
    {
        long n = Interlocked.Increment(ref _loggedCount);
        if (n > 5 && n % 500 != 0) return;
        try { Persist.LogFile.Append($"[试听] {what}（第 {n} 次）"); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            foreach (var t in _timers) { try { t.Dispose(); } catch { } }
            _timers.Clear();
            foreach (int p in _sounding) SendRaw(NoteOffBase | ((uint)p << 8));
            _sounding.Clear();
            if (_handle != IntPtr.Zero)
            {
                try { midiOutReset(_handle); } catch { }
                try { midiOutClose(_handle); } catch { }
                _handle = IntPtr.Zero;
            }
        }
        IsAvailable = false;
    }
}

/// <summary>
/// 整曲试听的调度器：**独立线程 + 高精度等待 + 短前瞻派发**。
///
/// 关键点（对应卡顿的六个可能来源）：
/// 1. 时间用单调时钟 <see cref="Stopwatch"/>，不用 DateTime.Now。
/// 2. 播放前 timeBeginPeriod(1)，播放后 timeEndPeriod(1) 复原。
/// 3. 事件表只在 <see cref="Load"/> 里建一次，播放中不重建。
/// 4. 每轮只派发"当前时间 + 前瞻窗口（默认 20ms）"以内的事件，然后精确睡到下一个事件时刻；
///    不会每个 tick 把未来一大段事件一次灌进 winmm 队列。
/// 5. 不碰 UI 线程：位置由 UI 自己低频读取 <see cref="PositionSeconds"/>。
/// 6. 停止 / 跳转先清音（AllNotesOff + AllSoundOff），线程结束后再清一次兜底。
/// </summary>
public sealed class MidiPreviewSequencer : IDisposable
{
    private const uint TimerPeriodMs = 1;

    private readonly MidiPreview _out;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _wake = new(false);

    private double[] _time = Array.Empty<double>();
    private sbyte[] _down = Array.Empty<sbyte>();      // 1 = note-on，0 = note-off
    private int[] _pitch = Array.Empty<int>();
    private (double S, double E, int Pitch)[] _spans = Array.Empty<(double, double, int)>();

    private int _index;
    private long _baseTicks;            // 单调时钟基准：位置 = (现在 - 基准) / 频率
    private volatile bool _stopRequested;
    private volatile bool _finished;
    private volatile bool _running;
    private bool _seekPending;
    private double _seekTarget;
    private bool _periodRaised;
    private Thread? _thread;

    /// <summary>前瞻窗口（毫秒）：一轮最多派发这么远以内的事件。15～30 之间比较合适。</summary>
    public int LookaheadMs { get; set; } = 20;

    /// <summary>最后一段自旋等待的长度（毫秒）：Sleep 精度不够，留 2ms 自旋。</summary>
    public int SpinMs { get; set; } = 2;

    /// <summary>note-on 力度。</summary>
    public int Velocity { get; set; } = 96;

    public double TotalSeconds { get; private set; }
    public int NoteCount { get; private set; }
    public int EventCount => _time.Length;
    public bool IsRunning => _running;
    public bool IsFinished => _finished;

    /// <summary>当前播放位置（秒）。UI 线程直接读，无需加锁。</summary>
    public double PositionSeconds
    {
        get
        {
            long b = Volatile.Read(ref _baseTicks);
            if (b == 0) return 0;
            return (Stopwatch.GetTimestamp() - b) / (double)Stopwatch.Frequency;
        }
    }

    public MidiPreviewSequencer(MidiPreview output) => _out = output;

    /// <summary>
    /// 建事件表（只调用一次）。<paramref name="spans"/> 是谱面秒（未除速度），
    /// <paramref name="speed"/> 与演奏共用同一套换算：真实秒 = 谱面秒 / 速度。
    /// </summary>
    public void Load(IReadOnlyList<(double Start, double End, int Pitch)> spans, double speed)
    {
        if (_running) throw new InvalidOperationException("播放中不能重建事件表");
        double sp = Math.Max(0.1, speed);
        const double gap = 0.02;   // 同音高重复时留出断开，否则不会重新触发

        int n = spans.Count;
        var buf = new (double T, sbyte Down, int Pitch)[n * 2];
        var built = new (double S, double E, int Pitch)[n];
        for (int i = 0; i < n; i++)
        {
            var s = spans[i];
            double on = s.Start / sp;
            double end = s.End / sp;
            // 音符比断开间隙还短时，off 绝不能越过本音自己的结束点：
            // 否则会落到下一个同音高音符的 on 之后，把那个音提前关掉。
            double off = end - on < gap ? Math.Min(on + 0.03, end)
                                        : Math.Max(on + 0.03, end - gap);
            buf[i * 2] = (on, 1, s.Pitch);
            buf[i * 2 + 1] = (off, 0, s.Pitch);
            built[i] = (on, off, s.Pitch);
        }
        // 先按时间，再让同一时刻的 note-off 排在 note-on 前面（否则同音高会自己把自己关掉）
        Array.Sort(buf, (a, b) =>
        {
            int c = a.T.CompareTo(b.T);
            return c != 0 ? c : a.Down.CompareTo(b.Down);
        });

        var t = new double[buf.Length];
        var d = new sbyte[buf.Length];
        var p = new int[buf.Length];
        for (int i = 0; i < buf.Length; i++) { t[i] = buf[i].T; d[i] = buf[i].Down; p[i] = buf[i].Pitch; }

        _time = t;
        _down = d;
        _pitch = p;
        _spans = built;
        TotalSeconds = buf.Length == 0 ? 0 : t[^1];
        NoteCount = n;
    }

    /// <summary>从某个位置开始播放（起新线程）。</summary>
    public void Start(double atSeconds)
    {
        Stop();
        double t = Math.Clamp(atSeconds, 0, TotalSeconds);
        Volatile.Write(ref _baseTicks, Stopwatch.GetTimestamp() - (long)(t * Stopwatch.Frequency));
        _index = FirstIndexAtOrAfter(t);
        _stopRequested = false;
        _finished = false;
        // 起播位置也走"跳转"那一套：先清音，再把正在响的音补上（从谱面中间开播不会是哑的）
        _seekPending = true;
        _seekTarget = t;
        _wake.Reset();
        _periodRaised = MidiPreview.TryRaiseTimerResolution(TimerPeriodMs);
        PreviewJitterProbe.Note($"timeBeginPeriod(1) = {(_periodRaised ? "成功" : "失败（系统拒绝，精度可能回落到 15.6ms）")}");
        _running = true;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "midi-preview-dispatch",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    /// <summary>跳转到某个秒数：先清音，再挪时间基准、移游标，并把"跳进去正在响的音"补上。</summary>
    public void Seek(double seconds)
    {
        double t = Math.Clamp(seconds, 0, TotalSeconds);
        if (!_running)
        {
            Volatile.Write(ref _baseTicks, Stopwatch.GetTimestamp() - (long)(t * Stopwatch.Frequency));
            _index = FirstIndexAtOrAfter(t);
            return;
        }
        lock (_gate)
        {
            _seekTarget = t;
            _seekPending = true;
        }
        _wake.Set();
    }

    /// <summary>停止：唤醒线程、等它退出、复原定时器精度，最后再清一次音（保证 note-off 一定发出）。</summary>
    public void Stop()
    {
        _stopRequested = true;
        _wake.Set();
        Thread? th = _thread;
        _thread = null;
        if (th != null && th.IsAlive)
        {
            try { th.Join(1500); } catch { }
        }
        _running = false;
        if (_periodRaised)
        {
            MidiPreview.RestoreTimerResolution(TimerPeriodMs);
            _periodRaised = false;
            PreviewJitterProbe.Note("timeEndPeriod(1) 已复原系统定时器精度");
        }
        _out.AllNotesOff();
    }

    public void Dispose()
    {
        Stop();
        _wake.Dispose();
    }

    private void Run()
    {
        try
        {
            while (!_stopRequested)
            {
                if (_seekPending) ApplyPendingSeek();

                if (_stopRequested) break;
                if (_index >= _time.Length) { _finished = true; break; }

                double horizon = Seconds() + LookaheadMs / 1000.0;
                bool sentAny = false;
                int sentThisRound = 0;
                while (!_stopRequested && !_seekPending && _index < _time.Length && _time[_index] <= horizon)
                {
                    double planned = _time[_index];
                    if (!WaitUntil(planned)) break;
                    if (_stopRequested || _seekPending) break;

                    double actual = Seconds();
                    long t0 = Stopwatch.GetTimestamp();
                    if (_down[_index] != 0) _out.NoteOn(_pitch[_index], Velocity);
                    else _out.StopNote(_pitch[_index]);
                    double sendMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                    PreviewJitterProbe.Record(planned, actual, sendMs);
                    _index++;
                    sentAny = true;
                    sentThisRound++;
                }
                PreviewJitterProbe.Batch(sentThisRound);

                if (_stopRequested || _seekPending) continue;
                if (_index >= _time.Length) { _finished = true; break; }

                // 下一个事件还在前瞻窗口之外：粗睡到"该事件时刻 - 前瞻窗口"，睡眠可被跳转/停止打断
                if (!sentAny)
                {
                    double waitUntil = _time[_index] - LookaheadMs / 1000.0;
                    WaitCoarse(waitUntil);
                }
            }
        }
        catch (Exception ex)
        {
            PreviewJitterProbe.Note("调度线程异常：" + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            _running = false;
            _out.AllNotesOff();   // 线程退出兜底：不留残音
        }
    }

    private void ApplyPendingSeek()
    {
        double target;
        lock (_gate)
        {
            if (!_seekPending) return;
            target = _seekTarget;
            _seekPending = false;
        }
        _out.AllNotesOff();                     // 跳转前先清音
        Volatile.Write(ref _baseTicks, Stopwatch.GetTimestamp() - (long)(target * Stopwatch.Frequency));
        _index = FirstIndexAtOrAfter(target);

        // 跳进音符中间时补上正在进行的声音（同一音高只补一次）
        var seen = new HashSet<int>();
        foreach (var s in _spans)
        {
            if (s.S <= target && target < s.E && seen.Add(s.Pitch)) _out.NoteOn(s.Pitch, Velocity);
        }
        PreviewJitterProbe.Note($"跳转到 {target:F3}s：已清音并补上 {seen.Count} 个在响的音");
    }

    /// <summary>精确等到某个时刻：先可打断地睡，最后 2ms 自旋。返回 false = 被停止/跳转打断。</summary>
    private bool WaitUntil(double targetSeconds)
    {
        double spin = Math.Max(0, SpinMs) / 1000.0;
        while (true)
        {
            if (_stopRequested || _seekPending) return false;
            double remain = targetSeconds - Seconds();
            if (remain <= 0) return true;
            if (remain > spin)
            {
                int ms = (int)Math.Clamp((remain - spin) * 1000.0, 1, 5);
                if (_wake.Wait(ms))
                {
                    _wake.Reset();
                    if (_stopRequested || _seekPending) return false;
                }
            }
            else
            {
                Thread.SpinWait(40);
            }
        }
    }

    /// <summary>粗睡到某个时刻之前（最多 50ms 醒一次，便于响应停止/跳转）。</summary>
    private void WaitCoarse(double targetSeconds)
    {
        while (!_stopRequested && !_seekPending)
        {
            double remainMs = (targetSeconds - Seconds()) * 1000.0;
            if (remainMs <= 1) return;
            if (_wake.Wait((int)Math.Clamp(remainMs, 1, 50))) { _wake.Reset(); return; }
        }
    }

    private int FirstIndexAtOrAfter(double t)
    {
        int lo = 0, hi = _time.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_time[mid] < t) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private double Seconds() => (Stopwatch.GetTimestamp() - Volatile.Read(ref _baseTicks)) / (double)Stopwatch.Frequency;
}

/// <summary>
/// 【诊断用，可删】试听派发抖动统计：记录每个事件"计划时刻"与"实际发出时刻"的差值。
/// 只有环境变量 MIDIKEY_PREVIEW_PROBE 非空时才收集，正常使用零行为、零输出。
/// 报告用 <see cref="WriteReport"/> 落到 MIDIKEY_PREVIEW_PROBE_OUT 指向的文件。
/// </summary>
internal static class PreviewJitterProbe
{
    internal static readonly bool Enabled =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MIDIKEY_PREVIEW_PROBE"));

    private static readonly object Gate = new();
    private static readonly List<double> Dev = new();     // 偏差（毫秒，正数 = 晚发）
    private static readonly List<double> Send = new();    // midiOutShortMsg 自身耗时（毫秒）
    private static readonly List<string> Notes = new();
    private static readonly List<double> Ui = new();      // 界面定时器相对理想节拍的滞后（毫秒）
    private static int _batchRounds, _batchMax, _batchSum, _batchBig;
    private static int _maxIndex = -1;
    private static double _maxValue;
    private static string _title = "(未命名)";

    internal static void Reset(string title)
    {
        lock (Gate)
        {
            Dev.Clear();
            Send.Clear();
            Notes.Clear();
            Ui.Clear();
            _batchRounds = _batchMax = _batchSum = _batchBig = 0;
            _maxIndex = -1;
            _maxValue = 0;
            _title = title;
        }
    }

    /// <summary>记一轮派发里发了几条消息（判断"一次性突发"）。</summary>
    internal static void Batch(int count)
    {
        if (!Enabled || count <= 0) return;
        lock (Gate)
        {
            _batchRounds++;
            _batchSum += count;
            if (count > _batchMax) _batchMax = count;
            if (count > 8) _batchBig++;
        }
    }

    /// <summary>记一次界面定时器节拍的滞后（毫秒，判断 UI 线程是否被派发阻塞）。</summary>
    internal static void UiTick(double lateMs)
    {
        if (!Enabled) return;
        lock (Gate) { Ui.Add(lateMs); }
    }

    /// <summary>记一条派发：计划时刻、实际发出时刻（同一单调时钟，秒）、发这条消息自身的耗时（毫秒）。</summary>
    internal static void Record(double plannedSeconds, double actualSeconds, double sendMs)
    {
        if (!Enabled) return;
        double ms = (actualSeconds - plannedSeconds) * 1000.0;
        lock (Gate)
        {
            if (Dev.Count == 0 || ms > _maxValue) { _maxValue = ms; _maxIndex = Dev.Count; }
            Dev.Add(ms);
            Send.Add(sendMs);
        }
    }

    internal static void Note(string text)
    {
        if (!Enabled) return;
        lock (Gate) { Notes.Add(text); }
    }

    private static string ReportPath =>
        Environment.GetEnvironmentVariable("MIDIKEY_PREVIEW_PROBE_OUT") ?? "";

    /// <summary>把统计追加写进报告文件（没设 MIDIKEY_PREVIEW_PROBE_OUT 就什么都不写）。</summary>
    internal static void WriteReport()
    {
        if (!Enabled) return;
        string path = ReportPath;
        if (string.IsNullOrWhiteSpace(path)) return;

        double[] dev, send, ui;
        string title;
        List<string> notes;
        int batchRounds, batchMax, batchSum, batchBig, maxIndex;
        lock (Gate)
        {
            dev = Dev.ToArray();
            send = Send.ToArray();
            ui = Ui.ToArray();
            title = _title;
            notes = new List<string>(Notes);
            batchRounds = _batchRounds; batchMax = _batchMax; batchSum = _batchSum; batchBig = _batchBig;
            maxIndex = _maxIndex;
            Dev.Clear();
            Send.Clear();
            Ui.Clear();
            Notes.Clear();
            _batchRounds = _batchMax = _batchSum = _batchBig = 0;
            _maxIndex = -1;
            _maxValue = 0;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"==== 试听派发抖动统计：{title} ====");
        sb.AppendLine($"事件总数        : {dev.Length}");
        if (dev.Length > 0)
        {
            Array.Sort(dev);
            double sum = 0, absSum = 0;
            int over10 = 0, over30 = 0, over50 = 0, under0 = 0;
            foreach (double d in dev)
            {
                sum += d;
                absSum += Math.Abs(d);
                if (d > 10) over10++;
                if (d > 30) over30++;
                if (d > 50) over50++;
                if (d < 0) under0++;
            }
            double pct(double p)
            {
                int idx = (int)Math.Ceiling(p * dev.Length) - 1;
                return dev[Math.Clamp(idx, 0, dev.Length - 1)];
            }
            sb.AppendLine($"平均偏差        : {sum / dev.Length:F3} ms（正数 = 晚发）");
            sb.AppendLine($"平均绝对偏差    : {absSum / dev.Length:F3} ms");
            sb.AppendLine($"95 分位偏差     : {pct(0.95):F3} ms");
            sb.AppendLine($"最大偏差        : {dev[^1]:F3} ms（第 {maxIndex + 1} 条事件）");
            sb.AppendLine($"最小偏差        : {dev[0]:F3} ms（提前的条数 {under0}）");
            sb.AppendLine($"超 10ms         : {over10} 条（{over10 * 100.0 / dev.Length:F2}%）");
            sb.AppendLine($"超 30ms         : {over30} 条（{over30 * 100.0 / dev.Length:F2}%）");
            sb.AppendLine($"超 50ms         : {over50} 条（{over50 * 100.0 / dev.Length:F2}%）");
        }
        if (send.Length > 0)
        {
            var sorted = (double[])send.Clone();
            Array.Sort(sorted);
            double ssum = 0;
            foreach (double s in sorted) ssum += s;
            sb.AppendLine($"midiOutShortMsg 耗时：平均 {ssum / sorted.Length:F4} ms / 95分位 "
                          + $"{sorted[Math.Clamp((int)Math.Ceiling(0.95 * sorted.Length) - 1, 0, sorted.Length - 1)]:F4} ms / "
                          + $"最大 {sorted[^1]:F4} ms");
        }
        sb.AppendLine($"派发轮数        : {batchRounds}（平均每轮 {batchSum / Math.Max(1, batchRounds):F2} 条）");
        sb.AppendLine($"单轮最大派发    : {batchMax} 条；单轮超过 8 条的次数 {batchBig}");
        if (ui.Length > 0)
        {
            var su = (double[])ui.Clone();
            Array.Sort(su);
            double usum = 0;
            foreach (double u in su) usum += u;
            sb.AppendLine($"界面节拍滞后    : 平均 {usum / su.Length:F2} ms / 95分位 "
                          + $"{su[Math.Clamp((int)Math.Ceiling(0.95 * su.Length) - 1, 0, su.Length - 1)]:F2} ms / 最大 {su[^1]:F2} ms"
                          + $"（n={su.Length}）");
        }
        foreach (string n in notes) sb.AppendLine("说明: " + n);
        sb.AppendLine();

        try { File.AppendAllText(path, sb.ToString()); } catch { }
    }
}

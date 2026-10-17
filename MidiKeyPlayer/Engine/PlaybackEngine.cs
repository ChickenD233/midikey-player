using System.Diagnostics;
using MidiKeyPlayer.Input;
using MidiKeyPlayer.Midi;

namespace MidiKeyPlayer.Engine;

public sealed class PlaybackEngine : IDisposable
{
    // —— 对外事件（工作线程触发，UI 需自行 marshal）——
    public event Action<string>? Log;
    public event Action<string>? CurrentNoteChanged;   // 正在演奏的音符描述（"" 表示无）
    public event Action? Finished;                     // 自然播完或手动停止

    private sealed class PhysicalEvent
    {
        public double T;
        public readonly int Kind;
        public readonly char Code;
        public readonly string Name;
        public readonly int Slot;          // 修饰键专用：0 = 八度键，1 = 升半音键，2 = 降半音键
        public readonly bool Down;
        public readonly string Label;

        private PhysicalEvent(double t, int kind, char code, string name, int slot, bool down, string label)
        {
            T = t; Kind = kind; Code = code; Name = name; Slot = slot; Down = down; Label = label;
        }

        /// <summary>音键事件。</summary>
        public static PhysicalEvent Key(double t, char code, bool down, string label)
            => new(t, K_Key, code, "", 0, down, label);

        /// <summary>修饰键事件（八度 / 升半音 / 降半音，slot 见 <see cref="Slot"/>）。</summary>
        public static PhysicalEvent Modifier(double t, string name, int slot, bool down)
            => new(t, K_Modifier, ' ', name, slot, down, "");
    }

    private readonly record struct HeldKey(char Key, double DownT, double UpT);

    private const int K_Key = 0;
    private const int K_Modifier = 1;   // 八度 / 升半音修饰键：具体是键盘键还是鼠标键由键名决定

    // 播放速度区间：与界面滑块（10%–400%）和设置层（AppConfig）同口径。
    public const double MinSpeed = 0.1;
    public const double MaxSpeed = 4.0;

    private readonly object _gate = new();
    private List<PhysicalEvent> _events = new();
    private List<MappedNote> _allNotes = new();   // 本轮全部可演奏音符（跳转/重建用）
    private double _totalMusic;          // 音乐时间总长
    private double _speed = 1.0;
    private double _leadSec;             // 提前量（音乐秒：物理预算 × 速度，与 _musicNow 同口径比较）
    private bool _loop;

    private Thread? _thread;
    private int _generation;             // 每次 Play 自增，用于把上一代线程隔离开（R2-05）
    private volatile int _wgen;          // 当前这一代工作线程的序号（跨线程读，必须 volatile）
    private volatile bool _running;
    private volatile bool _paused;
    private ModState _pausedMods = ModState.None;   // 暂停时按着的修饰键，Resume 用它补按（IN-02）
    private readonly ManualResetEventSlim _resumeGate = new(true);
    private readonly ManualResetEventSlim _cancelEvent = new(false);   // 停止信号，唤醒一切等待
    private readonly Stopwatch _clock = new();

    // 工作线程运行状态
    private double _musicNow;            // 已到达的音乐时间
    private double _lastPhys;            // 上一次积分采样的物理时刻
    private int _nextIdx;                // 下一个待派发事件下标

    private int _loopCount;
    private bool _manualStop;
    private string _snapNote = "";

    // UI 轮询快照（plain double 在 x64 上读写原子，轻微时序可接受）
    private double _snapElapsed;
    private double _snapTotal;
    private volatile int _snapLoop;

    public double ElapsedSeconds => _snapElapsed;
    public double TotalSeconds => _snapTotal;
    public int LoopCount => _snapLoop;
    public string CurrentNote => _snapNote;
    public bool IsRunning => _running;
    public bool IsPaused => _paused;

    /// <summary>实时播放速度（0.1–4.0，即 10%–400%，播放中可改，立即生效）。</summary>
    public double Speed
    {
        get => _speed;
        set => _speed = Math.Clamp(value, MinSpeed, MaxSpeed);
    }

    /// <summary>输入时序预算（物理毫秒）。播放中可改，下一轮播放生效。</summary>
    public InputTiming Timing { get; set; } = InputTiming.Standard;

#if MIDIKEY_TEST
    public (double T, int Kind, char Code, bool Down)[] BuildScheduleForTest(
        IReadOnlyList<MappedNote> notes, InputTiming timing)
    {
        var saved = Timing;
        try
        {
            Timing = timing;
            var (evs, _) = BuildSchedule(notes, ModState.None);
            return evs.Select(e => (e.T, e.Kind, e.Code, e.Down)).ToArray();
        }
        finally { Timing = saved; }
    }

    /// <summary>仅供时序回归测试使用：调度决策追踪输出口（null = 不追踪）。</summary>
    public static Action<string>? TraceSink;
#endif

    /// <summary>本轮播放的输入时序诊断（由 Execute 在派发时统计）。</summary>
    public InputTimingProbe Probe { get; } = new();

    public int SqueezedNotes => Probe.CompressedNotes;

    /// <summary>对外暴露的调度事件（音乐时间，秒）。用于导出按键表/宏，保证与实际演奏一致。</summary>
    public sealed record ScheduledEvent(double MusicTime, string Kind, char Key, bool Down)
    {
        public string KeyLabel => Key == ' ' || Key >= '\uE000' ? "" : (Key == ',' ? "，" : Key.ToString());
    }

    /// <summary>按指定时序预算构建"整首曲子"的事件表（不实际发送），与 <see cref="Play"/> 同一套构建逻辑。</summary>
    public static List<ScheduledEvent> BuildSchedulePreview(
        IReadOnlyList<MappedNote> notes, InputTiming? timing = null, double speed = 1.0)
    {
        var engine = new PlaybackEngine { Timing = timing ?? InputTiming.Standard };
        double safeSpeed = speed <= 0 ? 1.0 : Math.Clamp(speed, MinSpeed, MaxSpeed);
        engine.Speed = safeSpeed;
        var decided = notes.Where(n => n.Key != ' ' && n.Key != '\0').ToList();
        var (evs, _) = engine.BuildSchedule(decided, ModState.None);

        var list = new List<ScheduledEvent>(evs.Count);
        foreach (var e in evs)
        {
            char key = e.Code;
            string kind;
            if (e.Kind == K_Key)
            {
                kind = "key";
            }
            else if (InputSender.TryMouseButton(e.Name, out var button))
            {
                kind = button switch
                {
                    InputSender.MouseButton.Left => "mouse-left",
                    InputSender.MouseButton.Right => "mouse-right",
                    _ => "mouse-middle"
                };
                key = ' ';
            }
            else
            {
                // 方案把八度/升半音绑在键盘键上（如 PageUp / Shift / O）时按普通按键导出
                kind = "key";
                key = KeymapProfile.KeyCharOf(e.Name);
            }
            // 事件表用音乐时间；除以速度得到实际物理播放时刻（毫秒）
            list.Add(new ScheduledEvent(e.T / safeSpeed, kind, key, e.Down));
        }
        return list;
    }

    /// <summary>开始播放。notes 为映射后 InRange 的音符（音乐时间，未乘速度）。</summary>
    public void Play(IReadOnlyList<MappedNote> notes, double speed, double leadMs, bool loop)
    {
        lock (_gate)
        {
            if (_running) StopInternal();
            _wgen = ++_generation;
            if (_thread is { IsAlive: true } previous) previous.Join(200);

            _speed = speed <= 0 ? 1.0 : Math.Clamp(speed, MinSpeed, MaxSpeed);
            double needLeadMs = Timing.ModLeadMs + Timing.FrameMs;
            _leadSec = Math.Max(Timing.LeadMs, needLeadMs) / 1000.0 * _speed;
            // 诊断口径跟着本轮速度走：表里的时间差是音乐时间，报出的毫秒要乘速度换回物理时间（A13）
            Probe.Speed = _speed;
            Probe.Timing = Timing;
            _loop = loop;
            _manualStop = false;
            _loopCount = 0;
            _snapNote = "";
            _snapLoop = 0;

            // 只保留可演奏音（跳转会重建整表，必须同样过滤）
            _allNotes = notes.Where(n => n.InRange).ToList();
            (_events, _totalMusic) = BuildSchedule(_allNotes, CurrentModifiers());
            _snapTotal = _totalMusic;

            _musicNow = 0;
            _nextIdx = 0;

            Log?.Invoke($"开始播放：共 {_allNotes.Count} 个音符，总时长 ≈ {_totalMusic:F1}s" +
                        (_loop ? "（循环）" : "") +
                        $"；输入档位 {Timing.Name}（帧长 {Timing.FrameMs:F0}ms、修饰键提前 {Timing.ModLeadMs:F0}ms）");

            _running = true;
            _paused = false;
            _resumeGate.Set();
            _cancelEvent.Reset();
            _clock.Restart();
            _lastPhys = 0;
            _thread = new Thread(Worker) { IsBackground = true, Name = "PlaybackWorker" };
            _thread.Start();
        }
    }

    /// <summary>播放中更换剩余音符（用于实时移调）。位置保持在当前音乐时间附近。</summary>
    public void UpdateNotes(IReadOnlyList<MappedNote> notes)
    {
        lock (_gate)
        {
            if (!_running) return;

            // 必须先强制释放并同步状态机，否则换谱后的第一个音会以为修饰键还按着 → 音高错
            ForceReleaseModifiers();

            var inRange = notes.Where(n => n.InRange).ToList();
            _allNotes = inRange;
            var (evs, total) = BuildSchedule(inRange, ModState.None);
            _events = evs;
            _totalMusic = total;
            _nextIdx = FindNextIdx(_musicNow);
            ResyncModifiersAt(_nextIdx);
            SetCurrentNote("");
            Log?.Invoke($"已应用移调：剩余可演奏 {inRange.Count} 音");
        }
    }

    /// <summary>跳转到总进度 0..1 的位置（播放中可用）。</summary>
    public void SeekFraction(double fraction)
    {
        lock (_gate)
        {
            if (!_running) return;

            ForceReleaseModifiers();

            double target = Math.Clamp(fraction, 0, 1) * _totalMusic;
            var (evs, total) = BuildSchedule(_allNotes, ModState.None);
            _events = evs;
            _totalMusic = total;
            _musicNow = target;
            _nextIdx = FindNextIdx(_musicNow);
            ResyncModifiersAt(_nextIdx);
            SetCurrentNote("");
            _snapElapsed = _musicNow;
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (!_running || _paused) return;
            _pausedMods = ReadModifierStateAt(_nextIdx);
            _paused = true;
            _resumeGate.Reset();
            ReleaseAllInput();
        }
        SetCurrentNote("");
        Log?.Invoke("已暂停（当前音中断）。");
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!_running || !_paused) return;
            RestoreHeldModifiers(_pausedMods);
            _pausedMods = ModState.None;
            _paused = false;
            _resumeGate.Set();
        }
        Log?.Invoke("继续播放。");
    }

    /// <summary>停止并清理所有按下的键。物理状态改写收进 <see cref="_gate"/>（A10）。</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _manualStop = true;
            _running = false;
            _resumeGate.Set();
            _cancelEvent.Set();
            ReleaseAllInput();
        }
        SetCurrentNote("");
    }

    public void Dispose()
    {
        Stop();
        _resumeGate.Dispose();
        _cancelEvent.Dispose();
    }

    /// <summary>播放中重新开始一轮用的内部停止。调用方必须已持 <see cref="_gate"/>（A10）。</summary>
    private void StopInternal()
    {
        _manualStop = true;
        _running = false;
        _resumeGate.Set();
        _cancelEvent.Set();
        ReleaseAllInput();
        SetCurrentNote("");
    }

    // ================= 后台线程 =================

    private void Worker()
    {
        // 本线程属于哪一代：Play 先写 _wgen 再 Start，所以这里读到的一定是本代（R2-05）。
        int myGen = _wgen;
        try
        {
            while (_running && myGen == _wgen)
            {
                // 被新一代 Play 顶掉的旧线程：直接退出，不再积分、不再派发（R2-05）
                if (myGen != _wgen) break;

                if (_paused)
                {
                    _resumeGate.Wait();
                    _lastPhys = _clock.Elapsed.TotalSeconds;
                    continue;
                }

                double now = _clock.Elapsed.TotalSeconds;
                double dt = now - _lastPhys;
                _lastPhys = now;

                int next = 0;              // 0 = 继续，1 = 等尾音，2 = 循环，3 = 自然播完
                double slackT = 0;
                double wakeMusic = 0;
                string? pendingNote = null;
                lock (_gate)
                {
                    // 等锁期间可能已被新一代 Play 顶掉：进门后再确认一次身份（R2-05）
                    if (myGen != _wgen) break;
                    // 等锁期间用户可能已经点了暂停：这一趟不派发，回到外层的暂停分支等放行（R3-01）
                    if (_paused) continue;
                    // 积分累加进锁：与 SeekFraction / UpdateNotes 对 _musicNow 的写入同一口径
                    if (dt > 0) _musicNow += dt * _speed;
                    double lead = _leadSec;
                    while (_running && _nextIdx < _events.Count && _events[_nextIdx].T <= _musicNow + lead)
                    {
                        var ev = _events[_nextIdx];
                        Execute(ev);
                        if (ev.Kind == K_Key)
                        {
                            if (ev.Down) pendingNote = ev.Label;
                            else if (_nextIdx + 1 >= _events.Count || _events[_nextIdx + 1].T > ev.T)
                                pendingNote = "";
                        }
                        _nextIdx++;
                    }

                    // 3) 循环 / 结束判定
                    if (_nextIdx >= _events.Count)
                    {
                        slackT = _totalMusic + 0.25;
                        next = _musicNow < slackT ? 1 : (_loop ? 2 : 3);
                    }
                    else
                    {
                        // 4) 进度快照（限频由外层节流，简单直接赋值即可）
                        _snapElapsed = _musicNow;
                        _snapTotal = _totalMusic;

                        // 5) 睡到下一个事件（分段睡，保证速度调节平滑响应）
                        wakeMusic = _events[_nextIdx].T - lead;
                    }
                }

                if (pendingNote != null) SetCurrentNote(pendingNote);

                if (next == 1)
                {
                    SleepAWhile(slackT);
                    continue;
                }
                if (next == 2)
                {
                    RestartLoop();
                    continue;
                }
                if (next == 3) break;   // 自然播完

                if (_musicNow < wakeMusic)
                {
                    double remainMs = (wakeMusic - _musicNow) / _speed * 1000.0;
                    if (remainMs > 8)
                    {
                        _cancelEvent.Wait((int)Math.Min(remainMs - 4, 45));
                    }
                    else if (remainMs > 1.5)
                    {
                        _cancelEvent.Wait(1);
                    }
                    else
                    {
                        // 最后一点自旋等待，尽量精确
                        while (_running && !_paused && _musicNow < wakeMusic - 0.0004)
                        {
                            double t2 = _clock.Elapsed.TotalSeconds;
                            double d2 = t2 - _lastPhys;
                            _lastPhys = t2;
                            if (d2 > 0) { lock (_gate) { _musicNow += d2 * _speed; } }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"播放线程异常，已停止播放：{ex.GetType().Name}: {ex.Message}");
            global::MidiKeyPlayer.Persist.LogFile.Append($"[播放] 线程异常：{ex}");
            ReleaseAllInput();
            SetCurrentNote("");
        }
        finally
        {
            // 只有本代线程才允许收尾：被新一代 Play 顶掉的旧线程不许再清新一轮的字段（R2-05）。
            if (myGen == _wgen)
            {
                _running = false;
                // 物理松开并清零：否则下一轮 Play 会以上一轮留下的陈旧修饰键为起点建表
                ReleaseAllInput();
                SetCurrentNote("");
                _snapElapsed = Math.Min(_snapElapsed, _totalMusic);

                if (!_manualStop)
                    Finished?.Invoke();
                else
                    _snapElapsed = 0;
            }
        }
    }

    private void SleepAWhile(double untilMusic)
    {
        // 在直到 untilMusic 前保持积分前进（分段睡，可被停止信号打断）
        var sw = Stopwatch.StartNew();
        while (_running && !_paused && !_cancelEvent.IsSet &&
               _musicNow < untilMusic && sw.ElapsedMilliseconds < 60000)
        {
            double now = _clock.Elapsed.TotalSeconds;
            double dt = now - _lastPhys;
            _lastPhys = now;
            // 累加进锁，理由同派发段：长空拍正睡在这里，拖进度条不能被冲掉
            if (dt > 0) { lock (_gate) { _musicNow += dt * _speed; } }
            _cancelEvent.Wait(2);
        }
    }

    private void RestartLoop()
    {
        lock (_gate)
        {
            ReleaseAllInput();
            _loopCount++;
            _snapLoop = _loopCount;
            _musicNow = 0;
            _nextIdx = 0;
        }
        SetCurrentNote("");
        Log?.Invoke($"—— 第 {_loopCount + 1} 遍 ——");
        _cancelEvent.Wait(160);   // 中途点停止也能立刻醒来
        _lastPhys = _clock.Elapsed.TotalSeconds;
    }

    private int FindNextIdx(double musicNow)
    {
        double physSec = (Math.Max(Timing.ModLeadMs, Timing.ReleaseGapMs) + Timing.FrameMs) / 1000.0;
        double floor = musicNow - physSec * _speed;
        int idx = 0;
        while (idx < _events.Count && _events[idx].T <= floor) idx++;
        return idx;
    }

    public bool Silent { get; set; }

    private void Execute(PhysicalEvent ev)
    {
        switch (ev.Kind)
        {
            case K_Key:
                if (ev.Down)
                {
                    if (!Silent) InputSender.KeyDown(ev.Code);
                    _physHeldKeys.Add(ev.Code);
                    Probe.OnNoteOn(ev.Code, ev.T);
                }
                else
                {
                    if (!Silent) InputSender.KeyUp(ev.Code);
                    _physHeldKeys.Remove(ev.Code);
                    Probe.OnNoteOff(ev.Code, ev.T);
                }
                break;
            default:   // K_Modifier：八度键或半音键，键盘键与鼠标键都走同一个键名入口
                if (!Silent)
                {
                    if (ev.Down) InputSender.KeyDown(ev.Name);
                    else InputSender.KeyUp(ev.Name);
                }
                switch (ev.Slot)
                {
                    case 1: _physSharpKey = ev.Down ? ev.Name : null; break;
                    case 2: _physFlatKey = ev.Down ? ev.Name : null; break;
                    default: _physOctKey = ev.Down ? ev.Name : null; break;
                }
                Probe.OnModifier(ev.T, ev.Down);
                break;
        }
    }

    private void SetCurrentNote(string label)
    {
        _snapNote = label;
        CurrentNoteChanged?.Invoke(label);
    }

    // ================= 事件表构建（音乐时间） =================

    private readonly record struct ModState(string? OctaveKey, string? SharpKey, string? FlatKey)
    {
        public static ModState None => new(null, null, null);
    }

    /// <summary>音键当前真正处于按下状态的那几根（供停止/跳转后与目标程序对表）。和弦会同时有多根。</summary>
    private readonly HashSet<char> _physHeldKeys = new();
    /// <summary>当前真实按着的八度修饰键名（null = 没按）。</summary>
    private string? _physOctKey;
    /// <summary>当前真实按着的升半音修饰键名（null = 没按）。</summary>
    private string? _physSharpKey;
    /// <summary>当前真实按着的降半音修饰键名（null = 没按）。</summary>
    private string? _physFlatKey;

    private ModState CurrentModifiers()
    {
        lock (_gate) return new ModState(_physOctKey, _physSharpKey, _physFlatKey);
    }

    private ModState ReadModifierStateAt(int idx)
    {
        string? oct = null, sharp = null, flat = null;
        for (int i = 0; i < idx && i < _events.Count; i++)
        {
            var e = _events[i];
            if (e.Kind != K_Modifier) continue;
            switch (e.Slot)
            {
                case 1: sharp = e.Down ? e.Name : null; break;
                case 2: flat = e.Down ? e.Name : null; break;
                default: oct = e.Down ? e.Name : null; break;
            }
        }
        return new ModState(oct, sharp, flat);
    }

    private void RestoreHeldModifiers(ModState mods)
    {
        if (mods.OctaveKey is { Length: > 0 } oct)
        {
            if (!Silent) InputSender.KeyDown(oct);
            _physOctKey = oct;
        }
        if (mods.SharpKey is { Length: > 0 } sharp)
        {
            if (!Silent) InputSender.KeyDown(sharp);
            _physSharpKey = sharp;
        }
        if (mods.FlatKey is { Length: > 0 } flat)
        {
            if (!Silent) InputSender.KeyDown(flat);
            _physFlatKey = flat;
        }
    }

    private void ResyncModifiersAt(int idx)
    {
        var want = ReadModifierStateAt(idx);
        if (_paused)
        {
            _pausedMods = want;
            return;
        }
        RestoreHeldModifiers(want);
    }

    private void ReleaseAllInput()
    {
        InputSender.ReleaseEverything();
        _physOctKey = null;
        _physSharpKey = null;
        _physFlatKey = null;
    }

    private void ForceReleaseModifiers()
    {
        ReleaseAllInput();
        Probe.Reset(0);
    }

    /// <summary>方案里的键名归一化；空/认不出 → null。</summary>
    private static string? KeyOrNull(string? keyName)
    {
        string name = KeymapProfile.CanonicalKeyName(keyName);
        return name.Length == 0 ? null : name;
    }

    private (List<PhysicalEvent>, double) BuildSchedule(
        IReadOnlyList<MappedNote> notes, ModState startMods)
    {
        var evs = new List<PhysicalEvent>();
        if (notes.Count == 0) return (evs, 0);

        var ordered = notes
            .OrderBy(n => n.Start)
            .ThenBy(n => n.End)
            .ToList();

        // 音乐秒 = 物理秒 × 速度：下面所有物理预算都乘这个系数（A01）
        double scale = _speed;
        double frame = Timing.FrameMs / 1000.0 * scale;
        double modLead = Math.Max(Timing.ModLeadMs / 1000.0 * scale, frame);   // 修饰键至少提前一帧
        // 重触发间隔：至少要跨过"抬起被采样到"的那一帧，同时不小于配置值
        double retrig = Math.Max(Timing.RetriggerMs / 1000.0 * scale, frame);
        double minUpT = (Timing.FrameMs / 1000.0 + 0.001) * scale;

        var profile = KeymapProfile.Current;
        string? octUpKey = KeyOrNull(profile.OctaveUp);
        string? octDownKey = KeyOrNull(profile.OctaveDown);
        string? sharpKey = KeyOrNull(profile.SharpKeyToHold);   // 自带 Shift 的键位（Roblox 钢琴）没绑功能键时也按 Shift
        string? flatKey = KeyOrNull(profile.Flat);
        string? heldOct = startMods.OctaveKey;
        string? heldSharp = startMods.SharpKey;
        string? heldFlat = startMods.FlatKey;

        // 起点若还按着音键（上一轮中断残留），先全部松开
        if (_physHeldKeys.Count > 0)
        {
            foreach (char leftover in _physHeldKeys.ToList())
                evs.Add(PhysicalEvent.Key(0, leftover, false, ""));
            _physHeldKeys.Clear();
        }

        void EmitModifiers(string? wantOct, bool wantS, bool wantF, double modT)
        {
            // 顺序固定：先松开所有不该按的，再按下所有该按的。
            // 这样即便同刻也不依赖排序稳定性，且半音切换时"松"先于"按"。
            if (heldOct != null && heldOct != wantOct)
            {
                evs.Add(PhysicalEvent.Modifier(modT, heldOct, 0, down: false));
                heldOct = null;
            }
            if (heldSharp != null && !wantS)
            {
                evs.Add(PhysicalEvent.Modifier(modT, heldSharp, 1, down: false));
                heldSharp = null;
            }
            if (heldFlat != null && !wantF)
            {
                evs.Add(PhysicalEvent.Modifier(modT, heldFlat, 2, down: false));
                heldFlat = null;
            }

            if (wantOct != null && heldOct != wantOct)
            {
                evs.Add(PhysicalEvent.Modifier(modT, wantOct, 0, down: true));
                heldOct = wantOct;
            }
            if (wantS && heldSharp == null && sharpKey != null)
            {
                evs.Add(PhysicalEvent.Modifier(modT, sharpKey, 1, down: true));
                heldSharp = sharpKey;
            }
            if (wantF && heldFlat == null && flatKey != null)
            {
                evs.Add(PhysicalEvent.Modifier(modT, flatKey, 2, down: true));
                heldFlat = flatKey;
            }
        }

        var group = new List<HeldKey>();
        bool hasGroup = false;          // 上面这一组是不是已经按出来了（空组不算）
        string? groupOct = null;
        bool groupSharp = false;
        bool groupFlat = false;

        var lastDown = new Dictionary<char, double>();     // 每根键上一次按下时刻（重触发用）
        double lastRelease = double.NegativeInfinity;      // 本音之前最后一次"松开音键"的时刻

        void ReleaseHeld(HeldKey h, double limit, double scoreEnd)
        {
            double natural = Math.Max(h.UpT, h.DownT + minUpT);          // 自然抬起也要跨过一个帧点
            double upT = natural <= limit ? natural : Math.Max(limit, h.DownT + minUpT);
            if (upT < natural - 1e-9)
            {
                if (scoreEnd < h.DownT + minUpT) Probe.OnMinUpForced();
                else Probe.OnMinUpLimited();
            }
            evs.Add(PhysicalEvent.Key(upT, h.Key, false, ""));
            if (upT > lastRelease) lastRelease = upT;
        }

        foreach (var n in ordered)
        {
            double baseStart = Math.Max(0, n.Start);
            double scoreEnd = Math.Max(baseStart, n.End);      // 谱面结束时刻（保留原时值）
            double duration = scoreEnd - baseStart;

            // 本音需要的修饰键状态：八度档位 -1 = 按「降八度」键，+1 = 按「升八度」键，0 = 都不按
            string? wantOct = n.OctaveOffset < 0 ? octDownKey : (n.OctaveOffset > 0 ? octUpKey : null);
            bool wantS = n.Sharp && sharpKey != null;   // 方案里没绑升半音键时不可能真的要按
            bool wantF = n.Flat && flatKey != null;     // 降半音同理（Sharp 与 Flat 互斥，不会同时要）
            bool sharpHeld = heldSharp != null && heldSharp == sharpKey;
            bool flatHeld = heldFlat != null && heldFlat == flatKey;

            double limit = baseStart;                  // 本音最早能按下 = 谱面起点
            lastRelease = double.NegativeInfinity;

            // ① 已经弹完的音键按计划松开，与本音无关（自然结束，不记诊断）
            for (int i = group.Count - 1; i >= 0; i--)
            {
                HeldKey h = group[i];
                if (Math.Max(h.UpT, h.DownT + minUpT) <= limit)
                {
                    ReleaseHeld(h, limit, scoreEnd);
                    group.RemoveAt(i);
                }
            }

            if (group.Count > 0 &&
                !(hasGroup && groupOct == wantOct && groupSharp == wantS && groupFlat == wantF))
            {
                foreach (HeldKey h in group) ReleaseHeld(h, limit, scoreEnd);
                group.Clear();
                hasGroup = false;
            }

            // ③ 同一根键还被按着（同音重复，或方案里两个音共用一根键）→ 先松开它，再按重触发间隔按回去
            for (int i = group.Count - 1; i >= 0; i--)
            {
                if (group[i].Key == n.Key)
                {
                    ReleaseHeld(group[i], limit, scoreEnd);
                    group.RemoveAt(i);
                }
            }

            // ④ 本音最早能按下的时刻：谱面起点与"刚松开的音键"取晚者
            double t = Math.Max(baseStart, lastRelease);
            double endT = t + duration;

            // ⑤ 同一根音键的重触发间隔（旧版只给 12ms，短于一帧 → 两音粘连）
            double downT = t;
            if (lastDown.TryGetValue(n.Key, out double prevDown) && downT < prevDown + retrig)
                downT = prevDown + retrig;

            if (lastDown.TryGetValue(n.Key, out prevDown) && downT > endT)
            {
                double avail = Math.Max(0, endT - prevDown);
                double effRetrig = Math.Max(frame, Math.Min(retrig, avail));
                downT = Math.Max(t, Math.Min(endT, prevDown + effRetrig));
            }

            if (wantOct != heldOct || wantS != sharpHeld || wantF != flatHeld)
            {
                double modT = Math.Max(0, downT - modLead);
                if (lastRelease > modT) modT = lastRelease;
                EmitModifiers(wantOct, wantS, wantF, modT);
                if (downT < modT + frame) downT = modT + frame;
            }

            // ⑦ 修饰键提前量若把本音按下推后了，整段跟着后移，时值不变。
            //    不能只推按下不推抬起：那会把时值压没，又变成零时长按键。
            if (downT > t)
            {
                double shift = downT - t;
                t += shift;
                endT += shift;
            }

            evs.Add(PhysicalEvent.Key(downT, n.Key, true,
                NoteMapper.Describe(n, withTime: false)));
#if MIDIKEY_TEST
            TraceSink?.Invoke($"  音 {n.Key} 谱面 {baseStart:F4}→{scoreEnd:F4} 实发 down={downT:F4} up={endT:F4} "
                              + $"同组 {group.Count} 根"
                              + $"{(lastDown.ContainsKey(n.Key) ? $" prevDown={lastDown[n.Key]:F4}" : "")}");
#endif
            group.Add(new HeldKey(n.Key, downT, endT));
            hasGroup = true;
            groupOct = wantOct;
            groupSharp = wantS;
            groupFlat = wantF;
            lastDown[n.Key] = downT;
        }

        // 收尾：还按着的音键全部松开（自然抬起，至少跨过一个帧点）
        foreach (HeldKey h in group)
            evs.Add(PhysicalEvent.Key(Math.Max(h.UpT, h.DownT + minUpT), h.Key, false, ""));

        evs = evs.OrderBy(e => e.T).ToList();
        double total = evs.Count == 0 ? 0 : evs[^1].T;
        return (evs, total);
    }
}

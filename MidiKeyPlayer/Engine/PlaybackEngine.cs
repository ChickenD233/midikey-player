using System.Diagnostics;
using MidiKeyPlayer.Input;
using MidiKeyPlayer.Midi;

namespace MidiKeyPlayer.Engine;

/// <summary>
/// 播放调度引擎：把映射后的音符变成键盘/鼠标按下抬起事件，后台线程按真实时间发送（SendInput）。
/// 时间模型：事件表用"音乐时间"（秒，与速度无关）；工作线程按速度把物理流逝时间积分成音乐时间，
/// 因此 Speed 随时可变、SeekFraction 可跳转、UpdateNotes 可播放中更换剩余音符（实时移调）。
/// </summary>
public sealed class PlaybackEngine : IDisposable
{
    // —— 对外事件（工作线程触发，UI 需自行 marshal）——
    public event Action<string>? Log;
    public event Action<string>? CurrentNoteChanged;   // 正在演奏的音符描述（"" 表示无）
    public event Action? Finished;                     // 自然播完或手动停止

    /// <summary>
    /// 一个待派发的物理输入事件。T 为**音乐时间**（秒，与速度无关）。
    /// 用可变类而非 record，是为了在派发时回填"真正发出的时刻"，供时序诊断使用。
    /// 音键用字符形式（命名键是私用区哨兵字符），修饰键用键名（鼠标键或键盘键都行）。
    /// </summary>
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

    /// <summary>
    /// 一根正在按着的音键：DownT = 实际按下时刻，UpT = 计划抬起时刻（都是音乐时间）。
    /// 同一组（修饰键状态相同）里可以同时有多根，和弦靠它发声。
    /// </summary>
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

    // 原来的第 4 节「和弦开关与单音提取」已整体删除：ChordMode / PlayedNotes /
    // ResolveNotes（两个重载）/ ApplyChordMode 全都没有调用者。
    // 谱面现在由上游 MainWindow.ComputeAutoNotes 定好（演奏全部音），本类只负责派发按键。

#if MIDIKEY_TEST
    /// <summary>
    /// 仅供时序回归测试使用：按指定预算构建事件表。
    /// 注意用局部副本设置 Timing，避免副作用污染引擎状态。
    /// </summary>
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

    /// <summary>
    /// 本轮"没能成音"的音符数：按键时值被压到一个帧点都盖不住，目标程序读不到。
    /// 修复后正常曲子应为 0。不是 0 就说明这首谱面挤得比输入档位允许的最快速度还快，
    /// 用户可以换「稳健」档或把速度降一点。
    /// B04：只算「被迫压缩」（谱面时值本来就放不下），不算重叠音的「正常让位」。
    /// </summary>
    public int SqueezedNotes => Probe.CompressedNotes;

    /// <summary>对外暴露的调度事件（音乐时间，秒）。用于导出按键表/宏，保证与实际演奏一致。</summary>
    public sealed record ScheduledEvent(double MusicTime, string Kind, char Key, bool Down)
    {
        /// <summary>
        /// 便于人读的按键名。命名键（鼠标键、PageUp 等）在 <see cref="MappedNote.Key"/> 里是私用区哨兵字符，
        /// 一律不显示；调用方要用键名请读 MappedNote.KeyName。
        /// </summary>
        public string KeyLabel => Key == ' ' || Key >= '\uE000' ? "" : (Key == ',' ? "，" : Key.ToString());
    }

    /// <summary>按指定时序预算构建"整首曲子"的事件表（不实际发送），与 <see cref="Play"/> 同一套构建逻辑。</summary>
    public static List<ScheduledEvent> BuildSchedulePreview(
        IReadOnlyList<MappedNote> notes, InputTiming? timing = null, double speed = 1.0)
    {
        var engine = new PlaybackEngine { Timing = timing ?? InputTiming.Standard };
        // 先把速度写进引擎再建表：BuildSchedule 用 _speed 把物理毫秒预算（修饰键提前量、
        // 最短按住、重触发间隔）换算成音乐时间。留成默认的 1.0 会在导出时有速度误差——
        // 400% 下修饰键提前量按 10ms 排出，小于一帧，导出的脚本就会漏掉八度 / 升半音键。
        double safeSpeed = speed <= 0 ? 1.0 : Math.Clamp(speed, MinSpeed, MaxSpeed);
        engine.Speed = safeSpeed;
        // 谱面已由上游定好（MainWindow.ComputeAutoNotes），这里不再自己按音域取舍。
        // 只跳过没有可用按键的音（Key 不是按键字符），免得排出一次空格键。
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
            // 上一代线程可能还没走到 finally：它会在收尾时再清一次物理状态与 _running。
            // 先换世代号把它隔离，再等一小会，避免它把新一轮的状态清掉（R2-05）。
            _wgen = ++_generation;
            if (_thread is { IsAlive: true } previous) previous.Join(200);

            _speed = speed <= 0 ? 1.0 : Math.Clamp(speed, MinSpeed, MaxSpeed);
            // 提前量在派发时与**音乐时间**比较（T 与 _musicNow 都是音乐时间），
            // 所以物理毫秒预算必须乘速度换成音乐秒，否则物理提前量 = lead / speed：
            // 400% 时只剩 14ms（小于一帧，预置修饰键被推到音键之后 → 音高全错）；
            // 10% 时膨胀到 570ms。换算方向与建表侧 A01 一致：音乐秒 = 物理秒 × 速度。
            // 下限仍须 ≥ ModLeadMs + 一帧，保证修饰键先于音键发出。
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
            // 起点 = 引擎记录的物理修饰键状态，与 _nextIdx = 0 处的表状态一致。
            // 本文件每条释放路径都走 ReleaseAllInput()，它会把 _physOctKey / _physSharpKey 清零，
            // 所以这里就是「全松」；跳转/换谱建表也用 ModState.None，三处口径相同。
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
            // 新表从「没有修饰键按着」这个已知状态建起，同一根键在表里不会出现两次相邻 KeyDown。
            // 换谱点真正按着的修饰键由 ResyncModifiersAt 直接物理补按，不往表里插表头事件。
            var (evs, total) = BuildSchedule(inRange, ModState.None);
            _events = evs;
            // 直接采用新表总长：不能用 Max 保留旧值，否则换成更短的谱面后，
            // 工作线程会按陈旧的更大总长空等几分钟才循环 / 结束。
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
            // 重新构建整张表：从「没有修饰键按着」这个已知状态建起，同一根键不会两次相邻 KeyDown；
            // 跳转点该按着的修饰键不靠表来补，而由 ResyncModifiersAt 当场物理补按（RV-01）。
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
            // 先记下当时真正按着的修饰键（按事件表已派发部分推算），
            // 下面松开物理键后，Resume 才能按同一份状态补按（IN-02）。
            _pausedMods = ReadModifierStateAt(_nextIdx);
            _paused = true;
            _resumeGate.Reset();
            // 物理键全松并清零状态：Resume 靠 _pausedMods 补按回来（IN-02）。
            // A10：物理状态改写全部收进 _gate，避免与派发线程的 Execute 交错
            //（旧写法在锁外松开，工作线程可能刚派发完一个 KeyDown 又被这里抬掉）。
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
            // 暂停时松开了全部物理键，这里把当时按着的修饰键补按回去，
            // 否则继续之后到下一次状态变化之前整段八度 / 升半音都错（IN-02）。
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

                // 1) 积分：物理流逝 × 速度 → 音乐时间
                // dt 在锁外采样（读物理时钟不需锁），但累加进 _musicNow 必须进锁：
                // SeekFraction / UpdateNotes 在锁内改写 _musicNow，锁外累加会把那次写入冲掉（丢更新）。
                double now = _clock.Elapsed.TotalSeconds;
                double dt = now - _lastPhys;
                _lastPhys = now;

                // 2) 派发到期的事件
                // _leadSec 已是音乐时间口径（Play 里已乘速度），与 _musicNow 直接可比；
                // 实际发出的物理提前量恒等于 InputTiming 的毫秒值，换算由积分步长承担（A01）。
                //
                // 派发段与跳转 / 换谱 / 暂停共用 _gate：否则 SeekFraction 重建表之后，
                // 工作线程可能先派发新表的第一个音，而补按的修饰键晚一步（R2-01、R2-02）。
                // 锁内只做派发与判定，所有等待都放到锁外。
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

                // 派发路径的音符文字放到锁外调用：订阅者目前是 0 个，但这条路径一次要跑很多事件，
                // 不把外部回调留在锁内。其它路径的 SetCurrentNote 调用点在锁内，订阅者同样为 0（R3-04）
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
                            // 累加同样要进锁：这里也在跑时用户拖进度条（SeekFraction 锁内写 _musicNow）
                            // 会被锁外累加冲掉，与派发段同型竞态
                            if (d2 > 0) { lock (_gate) { _musicNow += d2 * _speed; } }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // A05：后台线程异常绝不能杀进程（SendInput 失败、表被并发改坏等）。
            // 写一条日志、松开物理键、把结束状态置好，再走正常收尾。
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
        // 状态改写与跳转 / 暂停共用 _gate：否则循环边界撞上用户跳转时，两边会互相冲掉
        // _musicNow 与 _nextIdx，或出现「物理按着修饰键、表却回到下标 0」（R3-02）。
        lock (_gate)
        {
            // 重开一遍前先物理松开并清零修饰键状态：上一遍末尾按着的键不会带进这一遍，
            // 表头也没有任何待派发的陈旧事件，建表起点与 _nextIdx = 0 一致（RV-04）。
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

    /// <summary>
    /// 定位「下一个待派发」事件：返回第一个晚于预滚窗口起点的事件下标。
    /// 保留预滚窗口，是为了让跳转/换谱后紧接的事件从头重放一遍修饰键变化；
    /// 窗口按**物理**时间预算折算成音乐时间（速度越快，同样物理时长覆盖的音乐时间越多）。
    /// </summary>
    private int FindNextIdx(double musicNow)
    {
        double physSec = (Math.Max(Timing.ModLeadMs, Timing.ReleaseGapMs) + Timing.FrameMs) / 1000.0;
        double floor = musicNow - physSec * _speed;
        int idx = 0;
        while (idx < _events.Count && _events[idx].T <= floor) idx++;
        return idx;
    }

    /// <summary>把物理事件真正发出去，并顺带记录时序诊断（发出时的音乐时间）。</summary>
    /// <summary>
    /// 试听模式：整个时间轴、倒计时、速度、循环、定位都照常走，但**不真的发按键/鼠标**。
    /// 界面改用内置 MIDI 合成器发声，于是"试听"就是放音频而不是在目标程序里按键。
    /// 状态机仍然照常更新，切换到非试听时不会有残留状态。
    /// </summary>
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

    /// <summary>
    /// 修饰键的真实按下状态（键名；null = 没按）。避免"我以为按着"与目标程序实际状态不一致。
    /// 修饰键可以绑键盘键（PageUp / Shift / O）也可以绑鼠标键（MouseLeft …），所以记键名。
    /// </summary>
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

    /// <summary>
    /// 读「事件表里派发到 idx 之前（不含 idx）时，表认为按着的修饰键」。
    /// 物理状态始终与 idx 处的表状态一致（每次重建表后都由 <see cref="ResyncModifiersAt"/> 对齐），
    /// 所以这个推算值就是物理真值：暂停（IN-02）、跳转/换谱后的补按（RV-01）都用它。
    /// 只由持 <see cref="_gate"/> 的调用方使用。
    /// </summary>
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

    /// <summary>
    /// 把物理修饰键状态对齐到 mods：按下其中记着的键，并同步 _physOctKey / _physSharpKey。
    /// Resume（IN-02）与跳转/换谱后的补按（RV-01）共用这一条路径。
    /// 调用前必须已经全松（见 <see cref="ForceReleaseModifiers"/>），否则旧键会留在按下状态。
    /// </summary>
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

    /// <summary>
    /// 重建表（跳转 / 实时移调）之后，把物理修饰键状态对齐到新表 idx 处的状态。
    /// 新表从「全松」建起，表头到 idx 之间的修饰键事件不派发、物理上也从没按过，
    /// 所以这里直接物理补按，而不是往表里插补按事件（插进表里的事件会被预滚窗口跳过，RV-01）。
    /// 补按与重建表在同一个 <see cref="_gate"/> 内完成（与 Resume 的写法一致），
    /// 所以跳转返回之后工作线程才派发的新表事件一定晚于补按：修饰键 KeyDown 先于第一个音键。
    /// 暂停中（工作线程停在 _resumeGate）不发按键，只记到 _pausedMods，由 Resume 先补按再放行（IN-02）。
    /// 只由持 <see cref="_gate"/> 的调用方使用。
    /// </summary>
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

    /// <summary>
    /// 松开全部物理键，并把两个修饰键状态字段清零：本文件所有释放路径的唯一入口。
    /// 只抬本程序真正按下过的键：InputSender 内部记账，用户物理按住的键不动（A04）。
    /// 清零是让 <see cref="CurrentModifiers"/> 说真话的前提 —— 否则上一轮结束时若还按着修饰键，
    /// 下一轮 <see cref="Play"/> 会以那个已经松开的陈旧键为起点建表，整轮都不再补发它的 KeyDown。
    /// _physHeldKeys 有意不动：下一轮建表会按它补发一次音键 KeyUp（见 <see cref="BuildSchedule"/> 起点处理）。
    /// </summary>
    private void ReleaseAllInput()
    {
        InputSender.ReleaseEverything();
        _physOctKey = null;
        _physSharpKey = null;
        _physFlatKey = null;
    }

    /// <summary>
    /// 强制把修饰键与音键全部松开，并把状态机复位，供换谱/跳转以「全松」为起点重建表。
    /// 顺带复位时序诊断：跳转前那一轮的事件记录已经作废。
    /// </summary>
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

    /// <summary>
    /// 把一个主旋律音符序列压成物理键盘/鼠标事件时间表（音乐时间，秒，与速度无关）。
    /// 旧实现把所有最小间隔写成 12ms / 8ms 这类远小于一帧的硬编码值，
    /// 把"松开前音 + 八度键 + 中键 + 本音按下"全挤在 20ms 内，目标程序按帧采样时整簇被折叠、
    /// 排在末尾的音键被吃掉 → 漏音。本实现所有最小间隔改用 InputTiming 的**物理毫秒**：
    /// 修饰键比音键早 ModLeadMs 且音键至少晚一帧；同键两次按下 ≥ RetriggerMs；
    /// 每次按下至少按住一帧。
    ///
    /// **和弦**：修饰键状态相同的一组音可以同时按住多根音键，同刻起音就真的同时发声。
    /// 组内自由重叠，不再互相顺延。组与组之间必须顺延：一个八度键 / 半音键只有一种状态，
    /// 切换修饰键时按着的音在目标程序里的音高会跟着变，所以换组前先把这一组全部松开，
    /// 而且修饰键的按下一定晚于最后一根音键的松开。同一根键要重按时，
    /// 也先松开再按重触发间隔按回去 —— 不会出现同一根键被"按两次"的错觉。
    /// 顺延的音保留原时值，绝不靠缩短前音腾位置：那会排出零时长的按键，
    /// 目标程序按帧采样时一帧都读不到，整段音被吃掉。
    ///
    /// startMods = 建表这一刻目标程序侧真实按着的修饰键。当前唯一的调用点（Play）传
    /// CurrentModifiers()，但每条释放路径都会先清掉物理状态，所以实际总是 ModState.None。
    /// 这个参数只为将来复用保留，不是当前行为的一部分。修饰键的成对性由
    /// 「物理全松 + 物理补按」承担，跳转/换谱的物理状态另由 <see cref="ResyncModifiersAt"/> 对齐。
    ///
    /// 关键口径（A01）：本表的 T 是**音乐时间**，InputTiming 的预算是**物理毫秒**，
    /// 两者靠播放速度换算 —— 音乐秒 = 物理秒 × 速度。所以下面每个物理预算都乘了 scale = _speed：
    /// 派发时工作线程按「音乐时间 += 物理流逝 × 速度」积分，两边抵消，实际发出的物理间隔
    /// 恒等于 InputTiming 的毫秒值，10% 与 400% 两端都不变。漏掉这一步就是旧 bug：
    /// 400% 时 45ms 的按住被压成 11ms（小于一帧）、修饰键提前量被压成 10ms → 漏音。
    /// 注意乘的是**建表时刻**的速度。播放中改速度不会重建表，已排定的间隔会按新速度等比变化。
    /// </summary>
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
        // "至少提前一帧"与"至少跨一帧"都要在**物理**时间上成立，所以 Math.Max 的两个操作数
        // 必须同一口径：两边都是乘过 scale 的物理值。只乘一边会在 10% 速度下失守
        // （物理间隔被压到 1.7ms，比一帧还小 → 照样漏音）。
        double modLead = Math.Max(Timing.ModLeadMs / 1000.0 * scale, frame);   // 修饰键至少提前一帧
        // 重触发间隔：至少要跨过"抬起被采样到"的那一帧，同时不小于配置值
        double retrig = Math.Max(Timing.RetriggerMs / 1000.0 * scale, frame);
        // 最短按住时刻：比一帧再多一点余量。
        // 只有刚好一帧时，若按下刚好落在帧边界上，整个按住区间可能一个帧点都不含 → 目标程序读不到。
        // 加 1ms 余量后，区间内一定落得进至少一个帧点。（1ms 也是物理值，同样乘速度）
        double minUpT = (Timing.FrameMs / 1000.0 + 0.001) * scale;

        // —— 修饰键状态机（起点 = 目标程序侧当前真实状态）——
        // 方案里的八度/半音键可以是键盘键（PageUp / Shift / O），也可以是鼠标键（MouseLeft …）。
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

        // —— 和弦：同一组（修饰键状态相同）的音可以同时按着多根音键 ——
        // group = 当前按着的那一组音键。组内自由重叠，这就是和弦发声的地方；
        // 组与组之间必须顺延，因为一个八度键 / 半音键只有一种状态。
        var group = new List<HeldKey>();
        bool hasGroup = false;          // 上面这一组是不是已经按出来了（空组不算）
        string? groupOct = null;
        bool groupSharp = false;
        bool groupFlat = false;

        var lastDown = new Dictionary<char, double>();     // 每根键上一次按下时刻（重触发用）
        double lastRelease = double.NegativeInfinity;      // 本音之前最后一次"松开音键"的时刻

        // 松开一根按着的音键。limit = 必须抬起的最晚时刻；scoreEnd = 触发这次让位的音符的谱面结束时刻。
        // 只有"提前松开"记诊断，自然结束不记（口径同 B04：被迫压缩 / 正常让位）。
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

            // ② 本音要的修饰键状态与这一组不同 → 整组让位：先全松，再切修饰键。
            //    一个修饰键只能有一种状态；切换时按着的音在目标程序里会跟着变音高。
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

            // 若顺延已超过本音结束时刻，就按"本音可用时长"临时收敛重触发间隔：
            // 极快段落宁可留一点粘连风险，也不能把整段音推没（时值会归零）。
            if (lastDown.TryGetValue(n.Key, out prevDown) && downT > endT)
            {
                double avail = Math.Max(0, endT - prevDown);
                double effRetrig = Math.Max(frame, Math.Min(retrig, avail));
                downT = Math.Max(t, Math.Min(endT, prevDown + effRetrig));
            }

            // ⑥ 修饰键切换：提前 modLead 发出，但一定晚于最后一根音键的松开（否则按着的音会跟着变调），
            //    并保证音键至少晚于一帧。
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

        // 表尾不补修饰键 KeyUp：表若在「某根修饰键还按着」处结束，接手的必定是一条释放路径 ——
        // 自然播完（Worker 收尾）、循环重开（RestartLoop）、下一轮跳转/换谱（ForceReleaseModifiers），
        // 三条都调 ReleaseAllInput() 物理松开并清零状态，不会把按下的键留到下一版或下一遍（RV-04）。
        // 稳定排序：同刻事件保持"先修饰键、后音键"的插入顺序
        evs = evs.OrderBy(e => e.T).ToList();
        double total = evs.Count == 0 ? 0 : evs[^1].T;
        return (evs, total);
    }
}

using System.Diagnostics;
using MidiKeyPlayer.Input;
using MidiKeyPlayer.Midi;

namespace MidiKeyPlayer.Engine;

public sealed class PlaybackEngine : IDisposable
{
    public event Action<string>? Log;
    public event Action<string>? CurrentNoteChanged;   
    public event Action? Finished;                     

    private sealed class PhysicalEvent
    {
        public double T;
        public readonly int Kind;
        public readonly char Code;
        public readonly string Name;
        public readonly int Slot;          
        public readonly bool Down;
        public readonly string Label;

        private PhysicalEvent(double t, int kind, char code, string name, int slot, bool down, string label)
        {
            T = t; Kind = kind; Code = code; Name = name; Slot = slot; Down = down; Label = label;
        }

        public static PhysicalEvent Key(double t, char code, bool down, string label)
            => new(t, K_Key, code, "", 0, down, label);

        public static PhysicalEvent Modifier(double t, string name, int slot, bool down)
            => new(t, K_Modifier, ' ', name, slot, down, "");
    }

    private readonly record struct HeldKey(char Key, double DownT, double UpT);

    private const int K_Key = 0;
    private const int K_Modifier = 1;   

    public const double MinSpeed = 0.1;
    public const double MaxSpeed = 4.0;

    private readonly object _gate = new();
    private List<PhysicalEvent> _events = new();
    private List<MappedNote> _allNotes = new();   
    private double _totalMusic;          
    private double _speed = 1.0;
    private double _leadSec;             
    private bool _loop;

    private Thread? _thread;
    private int _generation;             
    private volatile int _wgen;          
    private volatile bool _running;
    private volatile bool _paused;
    private ModState _pausedMods = ModState.None;   
    private readonly ManualResetEventSlim _resumeGate = new(true);
    private readonly ManualResetEventSlim _cancelEvent = new(false);   
    private readonly Stopwatch _clock = new();

    private double _musicNow;            
    private double _lastPhys;            
    private int _nextIdx;                

    private int _loopCount;
    private bool _manualStop;
    private string _snapNote = "";

    private double _snapElapsed;
    private double _snapTotal;
    private volatile int _snapLoop;

    public double ElapsedSeconds => _snapElapsed;
    public double TotalSeconds => _snapTotal;
    public int LoopCount => _snapLoop;
    public string CurrentNote => _snapNote;
    public bool IsRunning => _running;
    public bool IsPaused => _paused;

    public double Speed
    {
        get => _speed;
        set => _speed = Math.Clamp(value, MinSpeed, MaxSpeed);
    }

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

    public static Action<string>? TraceSink;
#endif

    public InputTimingProbe Probe { get; } = new();

    public int SqueezedNotes => Probe.CompressedNotes;

    public sealed record ScheduledEvent(double MusicTime, string Kind, char Key, bool Down)
    {
        public string KeyLabel => Key == ' ' || Key >= '\uE000' ? "" : (Key == ',' ? "，" : Key.ToString());
    }

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
                kind = "key";
                key = KeymapProfile.KeyCharOf(e.Name);
            }
            list.Add(new ScheduledEvent(e.T / safeSpeed, kind, key, e.Down));
        }
        return list;
    }

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
            Probe.Speed = _speed;
            Probe.Timing = Timing;
            _loop = loop;
            _manualStop = false;
            _loopCount = 0;
            _snapNote = "";
            _snapLoop = 0;

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

    public void UpdateNotes(IReadOnlyList<MappedNote> notes)
    {
        lock (_gate)
        {
            if (!_running) return;

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

    private void StopInternal()
    {
        _manualStop = true;
        _running = false;
        _resumeGate.Set();
        _cancelEvent.Set();
        ReleaseAllInput();
        SetCurrentNote("");
    }

    private void Worker()
    {
        int myGen = _wgen;
        try
        {
            while (_running && myGen == _wgen)
            {
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

                int next = 0;              
                double slackT = 0;
                double wakeMusic = 0;
                string? pendingNote = null;
                lock (_gate)
                {
                    if (myGen != _wgen) break;
                    if (_paused) continue;
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

                    if (_nextIdx >= _events.Count)
                    {
                        slackT = _totalMusic + 0.25;
                        next = _musicNow < slackT ? 1 : (_loop ? 2 : 3);
                    }
                    else
                    {
                        _snapElapsed = _musicNow;
                        _snapTotal = _totalMusic;

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
                if (next == 3) break;   

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
            if (myGen == _wgen)
            {
                _running = false;
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
        var sw = Stopwatch.StartNew();
        while (_running && !_paused && !_cancelEvent.IsSet &&
               _musicNow < untilMusic && sw.ElapsedMilliseconds < 60000)
        {
            double now = _clock.Elapsed.TotalSeconds;
            double dt = now - _lastPhys;
            _lastPhys = now;
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
        Log?.Invoke($"第 {_loopCount + 1} 遍");
        _cancelEvent.Wait(160);   
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
            default:   
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

    private readonly record struct ModState(string? OctaveKey, string? SharpKey, string? FlatKey)
    {
        public static ModState None => new(null, null, null);
    }

    private readonly HashSet<char> _physHeldKeys = new();
    private string? _physOctKey;
    private string? _physSharpKey;
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

        double scale = _speed;
        double frame = Timing.FrameMs / 1000.0 * scale;
        double modLead = Math.Max(Timing.ModLeadMs / 1000.0 * scale, frame);   
        double retrig = Math.Max(Timing.RetriggerMs / 1000.0 * scale, frame);
        double minUpT = (Timing.FrameMs / 1000.0 + 0.001) * scale;

        var profile = KeymapProfile.Current;
        string? octUpKey = KeyOrNull(profile.OctaveUp);
        string? octDownKey = KeyOrNull(profile.OctaveDown);
        string? sharpKey = KeyOrNull(profile.SharpKeyToHold);   
        string? flatKey = KeyOrNull(profile.Flat);
        string? heldOct = startMods.OctaveKey;
        string? heldSharp = startMods.SharpKey;
        string? heldFlat = startMods.FlatKey;

        if (_physHeldKeys.Count > 0)
        {
            foreach (char leftover in _physHeldKeys.ToList())
                evs.Add(PhysicalEvent.Key(0, leftover, false, ""));
            _physHeldKeys.Clear();
        }

        void EmitModifiers(string? wantOct, bool wantS, bool wantF, double modT)
        {
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
        bool hasGroup = false;          
        string? groupOct = null;
        bool groupSharp = false;
        bool groupFlat = false;

        var lastDown = new Dictionary<char, double>();     
        double lastRelease = double.NegativeInfinity;      

        void ReleaseHeld(HeldKey h, double limit, double scoreEnd)
        {
            double natural = Math.Max(h.UpT, h.DownT + minUpT);          
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
            double scoreEnd = Math.Max(baseStart, n.End);      
            double duration = scoreEnd - baseStart;

            string? wantOct = n.OctaveOffset < 0 ? octDownKey : (n.OctaveOffset > 0 ? octUpKey : null);
            bool wantS = n.Sharp && sharpKey != null;   
            bool wantF = n.Flat && flatKey != null;     
            bool sharpHeld = heldSharp != null && heldSharp == sharpKey;
            bool flatHeld = heldFlat != null && heldFlat == flatKey;

            double limit = baseStart;                  
            lastRelease = double.NegativeInfinity;

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

            for (int i = group.Count - 1; i >= 0; i--)
            {
                if (group[i].Key == n.Key)
                {
                    ReleaseHeld(group[i], limit, scoreEnd);
                    group.RemoveAt(i);
                }
            }

            double t = Math.Max(baseStart, lastRelease);
            double endT = t + duration;

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

        foreach (HeldKey h in group)
            evs.Add(PhysicalEvent.Key(Math.Max(h.UpT, h.DownT + minUpT), h.Key, false, ""));

        evs = evs.OrderBy(e => e.T).ToList();
        double total = evs.Count == 0 ? 0 : evs[^1].T;
        return (evs, total);
    }
}

using MidiKeyPlayer.Input;
using MidiKeyPlayer.Midi;

namespace MidiKeyPlayer.Engine;

public readonly record struct LiveMapping(
    bool Playable, string Key, bool Low, bool High, bool Sharp, bool Flat, string Reason)
{
    public string KeyLabel => string.IsNullOrEmpty(Key) ? "" : Key switch
    {
        "," => "，",
        "MouseLeft" => "左键",
        "MouseRight" => "右键",
        "MouseMiddle" => "中键",
        _ => Key,
    };
}

public sealed class LivePlayback : IDisposable
{
    private const int MaxQueue = 1024;          
    private const int MaxSounding = 32;         
    private const int MaxRecent = 24;           
    private const double DefaultDuplicateWindowMs = 30.0;   

    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly List<Pending> _queue = new();          
    private readonly List<Sounding> _sounding = new();      
    private readonly List<int> _recent = new();
    private readonly Dictionary<int, double> _lastAttackAt = new();   
    private Thread? _thread;
    private volatile bool _running;
    private bool _disposed;

    private sealed class Pending
    {
        public double Due;                 
        public string Key = "";
        public bool Down;
    }

    private sealed class Sounding
    {
        public int Pitch;
        public string Key = "";
        public double DownAt;              
        public double MinUpAt;             
        public bool Released;              
    }

    public Action<string, bool> Sink { get; set; } = DefaultSink;

    private static void DefaultSink(string key, bool down)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (down) InputSender.KeyDown(key); else InputSender.KeyUp(key);
    }

    private string? _modKey;               
    private bool _modSharp;                
    private bool _modFlat;                 

    private double _lastDownAt = double.NegativeInfinity;   
    private double _lastKeyUpAt = double.NegativeInfinity;  
    private double _lastUpAt = double.NegativeInfinity;     

    public event Action<string>? Log;
    public event Action<LiveMapping, int, int>? NoteObserved;   

    public int NoteOnCount { get; private set; }
    public int OutOfRangeCount { get; private set; }
    public int TooShortCount { get; private set; }
    public int StolenCount { get; private set; }
    public int DuplicateCount { get; private set; }

    private KeymapProfile? _keymap;

    public KeymapProfile Keymap
    {
        get => _keymap ?? KeymapProfile.Current;
        set
        {
            if (value == null) return;
            _keymap = value;
            KeymapProfile.Current = value;
        }
    }

    public int Transpose
    {
        get => _transpose;
        set => _transpose = Math.Clamp(value, -24, 24);
    }
    private int _transpose;

    public int BaseOctave
    {
        get => _baseOctave;
        set => _baseOctave = Math.Clamp(value, 0, 8);
    }
    private int _baseOctave = 4;

    public int MinVelocity
    {
        get => _minVelocity;
        set => _minVelocity = Math.Clamp(value, 1, 127);
    }
    private int _minVelocity = 1;

    public double DuplicateWindowMs
    {
        get => _duplicateWindowMs;
        set => _duplicateWindowMs = Math.Clamp(value, 0, 200);
    }
    private double _duplicateWindowMs = DefaultDuplicateWindowMs;

    public InputTiming Timing { get; set; } = InputTiming.Standard;

    public bool AutoFit { get; set; } = true;

    public static LiveMapping Map(int pitch, int baseOctave)
        => Map(pitch, baseOctave, KeymapProfile.Current);

    public static LiveMapping Map(int pitch, int baseOctave, KeymapProfile profile)
    {
        int shifted = pitch - 12 * (baseOctave - profile.BaseOctave);

        if (profile.TryKeyOfPitch(shifted, out string key, out int offset, out bool sharp, out bool flat, out _))
            return new LiveMapping(true, key, offset < 0, offset > 0, sharp, flat, "");

        var (lo, hi) = PlayableRange(baseOctave, profile);
        if (!profile.InRange(shifted))
        {
            return new LiveMapping(false, "", false, false, false, false,
                $"超出音域（本档可演奏 {Music.SolfegeRange(lo, hi)}，可用基准八度或移调调整）");
        }
        return new LiveMapping(false, "", false, false, false, false,
            "键表里没有这个音（没有对应键的音直接跳过），可在方案里加一个键");
    }

    public static (int Lo, int Hi) PlayableRange(int baseOctave)
        => PlayableRange(baseOctave, KeymapProfile.Current);

    private static (int Lo, int Hi) PlayableRange(int baseOctave, KeymapProfile profile)
    {
        int lo = profile.ResolveMinNote();
        int hi = profile.ResolveMaxNote();
        if (lo > hi) (lo, hi) = (hi, lo);
        int shift = 12 * (baseOctave - profile.BaseOctave);
        return (lo + shift, hi + shift);
    }

    private static string? ModKeyOrNull(string? keyName, KeymapProfile profile)
        => (profile.ModifiersEnabled || profile.HasSelfShiftKeys) && !string.IsNullOrWhiteSpace(keyName)
            ? keyName : null;

    public static (int BaseOctave, int OutCount) FitBaseOctave(IReadOnlyList<int> pitches)
    {
        if (pitches.Count == 0) return (4, 0);
        int best = 4, bestOut = int.MaxValue;
        double bestScore = double.MaxValue;
        for (int b = 0; b <= 8; b++)
        {
            var (rlo, rhi) = PlayableRange(b);
            int outCount = 0;
            double score = 0;
            foreach (int p in pitches)
            {
                if (p < rlo) { outCount++; score += (rlo - p) * (rlo - p) * 0.5; }
                else if (p > rhi) { outCount++; score += (p - rhi) * (p - rhi) * 0.5; }
                else score += CenterDistance(p, rlo, rhi);
            }
            if (outCount < bestOut || (outCount == bestOut && score < bestScore))
            {
                bestOut = outCount;
                bestScore = score;
                best = b;
            }
        }
        return (Math.Clamp(best, 0, 8), bestOut);
    }

    private static double CenterDistance(int pitch, int lo, int hi)
    {
        double center = (lo + hi) / 2.0;
        double half = (hi - lo) / 2.0;
        double d = Math.Abs(pitch - center) / Math.Max(1.0, half);
        return d * d;
    }

    public LivePlayback()
    {
        MidiInputService.DeviceLost += OnDeviceLost;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _queue.Clear();
            _sounding.Clear();
            _lastAttackAt.Clear();
            _modKey = null;
            _modSharp = false;
            _modFlat = false;
            _lastDownAt = _lastKeyUpAt = _lastUpAt = double.NegativeInfinity;
            _thread = new Thread(Worker) { IsBackground = true, Name = "LivePlayback" };
            _thread.Start();
        }
    }

    public void Stop()
    {
        bool wasRunning;
        lock (_gate)
        {
            wasRunning = _running;
            _running = false;
            _queue.Clear();
            _sounding.Clear();
            _lastAttackAt.Clear();
            _modKey = null;
            _modSharp = false;
            _modFlat = false;
        }
        _wake.Release();
        if (_thread != null && _thread.IsAlive && !ReferenceEquals(_thread, Thread.CurrentThread))
            _thread.Join(500);
        _thread = null;
        if (wasRunning) InputSender.ReleaseEverything();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        MidiInputService.DeviceLost -= OnDeviceLost;
        Stop();
        _wake.Dispose();
    }

    public void NoteOn(int pitch, int velocity)
    {
        if (velocity < MinVelocity) return;
        if (pitch is < 0 or > 127) return;

        double now = NowMs();
        LiveMapping map = default;
        bool octaveMoved = false;
        bool duplicate = false;
        lock (_gate)
        {
            duplicate = IsDuplicateAttack(pitch, now);
            if (duplicate)
            {
                DuplicateCount++;
            }
            else
            {
                NoteOnCount++;
                octaveMoved = ObserveForAutoFit(pitch);
                map = Map(pitch + _transpose, _baseOctave, Keymap);
                if (!map.Playable) OutOfRangeCount++;
            }
        }

        if (duplicate)
        {
            if (DuplicateCount == 1 || DuplicateCount % 50 == 0)
                Log?.Invoke($"[MIDI] 去重：{Music.SolfegeName(pitch)} 在 {DuplicateWindowMs:F0}ms 内被重复发送，"
                            + $"已丢掉这一次（累计 {DuplicateCount} 次）。键盘抖动或同一个音多通道都会这样。");
            return;
        }

        NoteObserved?.Invoke(map, pitch, velocity);

        if (octaveMoved)
            Log?.Invoke($"[MIDI] 自动贴合音域：基准八度改为 {Music.SolfegeName(_baseOctave * 12)}");
        if (!map.Playable)
        {
            Log?.Invoke($"[MIDI] {Music.SolfegeName(pitch)} 未发声：{map.Reason}");
            return;
        }

        lock (_gate)
        {
            var profile = Keymap;

            foreach (var s in _sounding.Where(x => !x.Released && x.Key == map.Key).ToList())
            {
                ReleaseNote(s, now, retrigger: true);
                StolenCount++;
            }

            double minDown = now;
            if (_lastDownAt > double.NegativeInfinity)
            {
                double need = _lastDownAt + minUpMs;
                if (map.Key == _lastKey && _lastKeyUpAt > double.NegativeInfinity)
                    need = Math.Max(need, _lastKeyUpAt + retrigMs);
                if (need > minDown) minDown = need;
            }

            double modLead = Math.Max(Timing.ModLeadMs, Timing.FrameMs);
            string? wantMod = map.Low ? ModKeyOrNull(profile.OctaveDown, profile)
                            : map.High ? ModKeyOrNull(profile.OctaveUp, profile)
                            : null;
            if (_modKey != wantMod)
            {
                if (_modKey is { Length: > 0 } old) { PurgePending(old); Enqueue(now, old, false); }
                if (wantMod is { Length: > 0 } neu) { PurgePending(neu); Enqueue(Math.Max(now, minDown - modLead), neu, true); }
                _modKey = wantMod;
            }
            string? sharpKey = ModKeyOrNull(profile.SharpKeyToHold, profile);
            if (_modSharp != map.Sharp && sharpKey != null)
            {
                PurgePending(sharpKey);
                if (_modSharp) Enqueue(now, sharpKey, false);
                if (map.Sharp) Enqueue(Math.Max(now, minDown - modLead), sharpKey, true);
                _modSharp = map.Sharp;
            }
            string? flatKey = ModKeyOrNull(profile.Flat, profile);
            if (_modFlat != map.Flat && flatKey != null)
            {
                PurgePending(flatKey);
                if (_modFlat) Enqueue(now, flatKey, false);
                if (map.Flat) Enqueue(Math.Max(now, minDown - modLead), flatKey, true);
                _modFlat = map.Flat;
            }

            double downAt = minDown;
            var note = new Sounding
            {
                Pitch = pitch,
                Key = map.Key,
                DownAt = downAt,
                MinUpAt = downAt + minUpMs
            };
            _sounding.Add(note);
            if (_sounding.Count > MaxSounding)
            {
                var oldest = _sounding[0];
                _sounding.RemoveAt(0);
                if (!oldest.Released) ReleaseNote(oldest, now, retrigger: false);
            }
            Enqueue(downAt, map.Key, true);
            _lastDownAt = downAt;
            _lastKey = map.Key;
            _wake.Release();
        }
    }

    private string? _lastKey;

    public void NoteOff(int pitch)
    {
        double now = NowMs();
        lock (_gate)
        {
            foreach (var s in _sounding.Where(x => x.Pitch == pitch && !x.Released).ToList())
                ReleaseNote(s, now, retrigger: false);
            _wake.Release();
        }
    }

    public void ReleaseAll()
    {
        double now = NowMs();
        lock (_gate)
        {
            foreach (var s in _sounding.Where(x => !x.Released).ToList()) ReleaseNote(s, now, retrigger: false);
            string? sharpKey = ModKeyOrNull(Keymap.SharpKeyToHold, Keymap);
            if (_modSharp && sharpKey != null) { Enqueue(now, sharpKey, false); _modSharp = false; }
            string? flatKey = ModKeyOrNull(Keymap.Flat, Keymap);
            if (_modFlat && flatKey != null) { Enqueue(now, flatKey, false); _modFlat = false; }
            if (_modKey is { Length: > 0 } m) { Enqueue(now, m, false); _modKey = null; }
            _wake.Release();
        }
        if (!_running) InputSender.ReleaseEverything();
    }

    private void OnDeviceLost()
    {
        if (!_running) return;
        Log?.Invoke("[MIDI] 设备断开或异常：已停止实时演奏并松开所有键。");
        Stop();
    }

    private void ReleaseNote(Sounding s, double now, bool retrigger)
    {
        double due = Math.Max(now, s.MinUpAt);
        if (retrigger) due = Math.Max(due, s.DownAt + retrigMs);
        if (due > now + 0.5) TooShortCount++;
        s.Released = true;
        Enqueue(due, s.Key, false);
        if (due > _lastUpAt) _lastUpAt = due;
        if (s.Key == _lastKey && due > _lastKeyUpAt) _lastKeyUpAt = due;
    }

    private double minUpMs => Math.Max(Timing.MinHoldMs, Timing.FrameMs) + 1.0;
    private double retrigMs => Math.Max(Timing.RetriggerMs, Timing.FrameMs);

    private static double NowMs()
    {
#if MIDIKEY_TEST
        if (ClockForTest != null) return ClockForTest();
#endif
        return Environment.TickCount64;
    }

    private void PurgePending(string key) => _queue.RemoveAll(e => e.Key == key);

    private bool IsDuplicateAttack(int pitch, double now)
    {
        if (_duplicateWindowMs <= 0) return false;
        if (_lastAttackAt.TryGetValue(pitch, out double last) && now - last < _duplicateWindowMs) return true;
        _lastAttackAt[pitch] = now;   
        return false;
    }

    private void Enqueue(double due, string key, bool down)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (!_running) return;
        if (_queue.Count >= MaxQueue)
        {
            int drop = _queue.FindIndex(e => e.Down);
            if (drop >= 0)
            {
                _queue.RemoveAt(drop);
            }
            else
            {
                if (down)
                {
                    DroppedDown++;
                    if (DroppedDown == 1 || DroppedDown % 100 == 0)
                        Log?.Invoke($"[MIDI] 输出队列积压，已丢弃 {DroppedDown} 个按下事件（抬起事件不丢，不会卡键）");
                }
                return;
            }
        }
        var ev = new Pending { Due = due, Key = key, Down = down };
        int i = _queue.Count;
        while (i > 0 && _queue[i - 1].Due > due) i--;
        _queue.Insert(i, ev);
    }

    public int DroppedDown { get; private set; }

    private bool ObserveForAutoFit(int rawPitch)
    {
        _recent.Add(rawPitch);
        if (_recent.Count > MaxRecent) _recent.RemoveAt(0);
        if (!AutoFit || _recent.Count * 2 < MaxRecent) return false;

        var (oct, _) = FitBaseOctave(_recent.Select(p => p + _transpose).ToList());
        if (oct == _baseOctave) return false;
        _baseOctave = oct;
        return true;
    }

    private void Worker()
    {
        try
        {
            while (_running)
            {
                List<Pending>? due = null;
                lock (_gate)
                {
                    double now = NowMs();
                    if (_queue.Count > 0 && _queue[0].Due <= now)
                    {
                        due = new List<Pending>();
                        while (_queue.Count > 0 && _queue[0].Due <= now)
                        {
                            due.Add(_queue[0]);
                            _queue.RemoveAt(0);
                        }
                        _sounding.RemoveAll(s => s.Released && s.MinUpAt <= now);
                    }
                }

                if (due != null)
                {
                    var sink = Sink;
                    foreach (var e in due) sink(e.Key, e.Down);
                    continue;
                }

                int waitMs;
                lock (_gate)
                {
                    waitMs = _queue.Count == 0 ? 40 : (int)Math.Clamp(_queue[0].Due - NowMs(), 1, 20);
                }
                _wake.Wait(waitMs);
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[MIDI] 实时演奏线程异常：{ex.Message}");
        }
        finally
        {
            _running = false;
            InputSender.ReleaseEverything();
        }
    }

#if MIDIKEY_TEST
    public (double Ms, string Key, bool Down)[] DrainForTest()
        => _queue.OrderBy(e => e.Due)
                 .Select(e => (e.Due, e.Key, e.Down))
                 .ToArray();

    public static Func<double>? ClockForTest;
#endif
}

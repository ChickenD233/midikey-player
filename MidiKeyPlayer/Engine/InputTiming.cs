namespace MidiKeyPlayer.Engine;

public sealed record InputTiming
{
    public double FrameMs { get; init; } = 16.7;

    public double ModLeadMs { get; init; } = 40.0;      

    public double RetriggerMs { get; init; } = 45.0;    

    public double MinHoldMs { get; init; } = 45.0;      

    public double ReleaseGapMs { get; init; } = 40.0;   

    public double LeadMs { get; init; } = 57.0;         

    public string Name { get; init; } = "标准";

    public static InputTiming Safe => new()
    {
        Name = "稳健",
        FrameMs = 33.3,
        ModLeadMs = 70,
        RetriggerMs = 80,
        MinHoldMs = 80,
        ReleaseGapMs = 70,
        LeadMs = 104         
    };

    public static InputTiming Standard => new();

    public static InputTiming Aggressive => new()
    {
        Name = "极限",
        FrameMs = 8.0,
        ModLeadMs = 20,
        RetriggerMs = 22,
        MinHoldMs = 22,
        ReleaseGapMs = 18,
        LeadMs = 28          
    };

    public static InputTiming FromIndex(int index) => index switch
    {
        0 => Safe,
        2 => Aggressive,
        _ => Standard
    };

    public static string[] Names => new[] { "稳健（30fps / 卡顿）", "标准（60fps 推荐）", "极限（高帧率）" };
}

public sealed class InputTimingProbe
{
    private readonly object _gate = new();

    private double _lastModMusicT = double.NegativeInfinity;
    private readonly Dictionary<char, double> _lastDownOfKey = new();
    private readonly Dictionary<char, double> _downAtOfKey = new();
    private int _heldMods;
    private bool _sawLead;

    public double Speed = 1.0;

    public InputTiming Timing { get; set; } = InputTiming.Standard;

    public int TotalNoteOn { get; private set; }
    public int ModLeadTooShort { get; private set; }    
    public int ModLeadZero { get; private set; }        
    public int RetriggerTooShort { get; private set; }  
    public int MinHoldTooShort { get; private set; }    
    public double MinModLeadMs { get; private set; } = double.MaxValue;

    public int MinUpLimited { get; private set; }

    public int CompressedNotes { get; private set; }

    public int MinUpRelaxed { get; private set; }

    public void OnMinUpLimited() { MinUpLimited++; MinUpRelaxed++; }

    public void OnMinUpForced() { MinUpLimited++; CompressedNotes++; }

    public void OnModifier(double musicT, bool down)
    {
        lock (_gate)
        {
            if (down) _lastModMusicT = musicT;
            _heldMods += down ? 1 : -1;
            if (_heldMods < 0) _heldMods = 0;
        }
    }

    public void OnNoteOn(char key, double musicT)
    {
        lock (_gate)
        {
            TotalNoteOn++;
            double phys = Speed <= 0 ? 1.0 : Speed;
            if (_lastDownOfKey.TryGetValue(key, out double prevDown))
            {
                double gapMs = (musicT - prevDown) * phys * 1000.0;
                if (gapMs < Timing.RetriggerMs) RetriggerTooShort++;
            }
            if (_heldMods > 0 && !double.IsNegativeInfinity(_lastModMusicT))
            {
                double leadMs = (musicT - _lastModMusicT) * phys * 1000.0;
                if (leadMs < MinModLeadMs) MinModLeadMs = leadMs;
                _sawLead = true;
                if (leadMs < Timing.FrameMs) ModLeadTooShort++;
                if (leadMs < 0.5) ModLeadZero++;
            }
            _lastDownOfKey[key] = musicT;
            _downAtOfKey[key] = musicT;
        }
    }

    public void OnNoteOff(char key, double musicT)
    {
        lock (_gate)
        {
            double phys = Speed <= 0 ? 1.0 : Speed;
            if (_downAtOfKey.TryGetValue(key, out double downAt))
            {
                double holdMs = (musicT - downAt) * phys * 1000.0;
                if (holdMs < Timing.FrameMs) MinHoldTooShort++;
                _downAtOfKey.Remove(key);
            }
        }
    }

    public void Reset(double musicT)
    {
        lock (_gate)
        {
            _lastModMusicT = double.NegativeInfinity;
            _heldMods = 0;
            _lastDownOfKey.Clear();
            _downAtOfKey.Clear();
        }
    }

    public string Summary()
    {
        lock (_gate)
        {
            if (TotalNoteOn == 0) return "时序诊断：无音符";
            double frame = Timing.FrameMs;
            string lead = _sawLead ? $"{MinModLeadMs:F1}ms" : "无";
            return $"时序诊断（物理毫秒，速度 {Speed * 100:F0}%）：音符 {TotalNoteOn} 个；" +
                   $"修饰键提前量<{frame:F1}ms 的 {ModLeadTooShort} 个（同刻 {ModLeadZero} 个）；" +
                   $"同键重触发<{Timing.RetriggerMs:F0}ms 的 {RetriggerTooShort} 个；" +
                   $"按住<{frame:F1}ms 的 {MinHoldTooShort} 个；" +
                   $"时值被压到下限的 {MinUpLimited} 个（被迫压缩 {CompressedNotes} 个，正常让位 {MinUpRelaxed} 个）；" +
                   $"最小修饰键提前量 {lead}";
        }
    }
}

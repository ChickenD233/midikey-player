using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using MidiKeyPlayer.Midi;

namespace MidiKeyPlayer;

public sealed class PianoRoll : Control
{
    // ---- 沟槽：标尺在上、琴键栏在左。换算里必须同时含这两个偏移，两个方向要对称 ----
    private const double KeyW = 50;          // 琴键栏宽
    private const double RulerH = 20;        // 标尺高
    private const double PadR = 8, PadB = 6; // 右/下留白
    private const double EdgeGrabPx = 6;     // 改长度的抓取带宽度（上限）
    private const double MinNoteSeconds = 0.03;
    private const double DragThreshold = 4;  // 超过它才算"拖"，否则算"点"
    private const int FallbackLo = 48, FallbackHi = 48 + MinSpan;   // C3 起，4 个八度
    /// <summary>可见音域的最小跨度（半音数）。4 个八度 = 48 个半音，即 hi-lo = 47。</summary>
    private const int MinSpan = 47;
    private int _lastLo = FallbackLo, _lastHi = FallbackHi;   // 上一次实际用过的音域

    private List<RawNote> _notes = new();
    private HashSet<int> _inRange = new();
    private Dictionary<int, int> _voiceOfPitch = new();
    private double _total;
    private double _position;
    /// <summary>谱面时间 → 卷帘时间轴的比例（默认 1，试听时按实际总长设置）。</summary>
    private double _playheadScale = 1.0;
    /// <summary>「去除开头空拍」剪掉的前奏秒数（0 = 未剪或该选项关闭）。</summary>
    private double _trimmedLead;

    // 画小节线用的速度/拍号（取不到时回落到 120bpm 4/4）
    private double _secPerBeat = 0.5;
    private int _beatsPerBar = 4;

    private double _viewFrom, _viewTo;
    private double _positionAtLastFollow = double.NaN;

    private readonly HashSet<int> _sel = new();
    private int _hover = -1;
    private int _hoverKey = -1;

    private GeometryGroup? _barsSkip;
    /// <summary>按声轨颜色号分组的可演奏音符几何缓存。声轨各用一色，一眼对得上左侧列表。</summary>
    private readonly Dictionary<int, GeometryGroup> _barsByVoice = new();
    private double _builtW = -1, _builtH = -1, _builtFrom = double.NaN, _builtTo = double.NaN;
    private int _builtLo = int.MinValue, _builtHi = int.MinValue, _builtCount = -1;

    private enum DragMode { None, ScrubRuler, Pan, Marquee, Move, ResizeL, ResizeR, Erase, KeyLane }
    private DragMode _mode = DragMode.None;
    private bool _dragMoved;
    private Point _pressPt;
    /// <summary>最后一次已知指针位置：捕获被抢走时没有落点可用，收尾用它。</summary>
    private Point _lastPt;
    private Rect _marquee;

    private readonly List<(RawNote Note, int Pitch, double Start, double End)> _dragBase = new();
    // 本次移动算出来的临时位置，只用于绘制与提交
    private readonly List<(RawNote Note, int Pitch, double Start, double End)> _dragNow = new();
    private double _anchorSec;
    private int _anchorPitch;
    /// <summary>拖动中已经试听过的音高，防止同一音高反复发声。</summary>
    private int _lastAuditioned = int.MinValue;
    private readonly HashSet<RawNote> _eraseDone = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<int> _pitchSelDone = new();

    // ================= 自绘配色（浅色 / 深色两套，跟随皮肤） =================

    private sealed class Skin
    {
        public IBrush Bg = null!, Skip = null!, Head = null!, ReadoutBg = null!, Text = null!, Muted = null!;
        public IBrush KeyWhite = null!, KeyBlack = null!, KeySel = null!, RulerBg = null!, MarqueeFill = null!;
        public IPen BorderPen = null!, GridPen = null!, BeatPen = null!, BarPen = null!, OctavePen = null!;
        public IPen HoverPen = null!, SelPen = null!, HeadPen = null!, ReadoutBorder = null!;
        public IPen LaneBorder = null!, MarqueePen = null!;
    }

    private static readonly Dictionary<ThemeVariant, Skin> Skins = new();
    private static Skin? _skin;
    private static ThemeVariant _skinVariant = ThemeVariant.Default;

    private static Skin CurrentSkin()
    {
        var variant = Application.Current?.ActualThemeVariant ?? ThemeVariant.Light;
        var cached = _skin;
        if (cached != null && _skinVariant == variant) return cached;

        if (!Skins.TryGetValue(variant, out var skin))
        {
            skin = BuildSkin(variant);
            Skins[variant] = skin;
        }
        _skin = skin;
        _skinVariant = variant;
        return skin;
    }

    private static Skin BuildSkin(ThemeVariant variant)
    {
        bool dark = variant == ThemeVariant.Dark;
        return new Skin
        {
            Bg = RollBrush("BrushRollBg", variant, dark ? "#161B21" : "#F7F9FC"),
            BorderPen = RollPen("BrushBorder", variant, dark ? "#2C343E" : "#DDE3EA", 1),
            GridPen = RollPen("BrushRollGrid", variant, dark ? "#232A33" : "#E9EEF5", 1),
            BeatPen = RollPen("BrushRollBeat", variant, dark ? "#2A323C" : "#DCE4EE", 1),
            BarPen = RollPen("BrushRollBar", variant, dark ? "#3A444F" : "#C9D5E3", 1),
            OctavePen = RollPen("BrushRollOctave", variant, dark ? "#242B34" : "#E3E9F1", 1),
            Skip = RollBrush("BrushSkipNote", variant, dark ? "#4C5560" : "#C8D0D9"),
            HoverPen = RollPen("BrushRollHover", variant, dark ? "#6FA8F0" : "#8FB4E8", 1),
            SelPen = RollPen("BrushRollAccent", variant, dark ? "#4C9AFF" : "#1F6FEB", 2),
            HeadPen = RollPen("BrushRollAccent", variant, dark ? "#4C9AFF" : "#1F6FEB", 2),
            Head = RollBrush("BrushRollAccent", variant, dark ? "#4C9AFF" : "#1F6FEB"),
            ReadoutBg = RollBrush("BrushRollReadout", variant, dark ? "#1E2A3A" : "#E8F0FC"),
            ReadoutBorder = RollPen("BrushRollReadoutBorder", variant, dark ? "#33507A" : "#C3D6F2", 1),
            Text = RollBrush("BrushText", variant, dark ? "#E7ECF2" : "#2A3646"),
            Muted = RollBrush("BrushTextMuted", variant, dark ? "#93A0AE" : "#7C8798"),
            KeyWhite = RollBrush("BrushKeyWhite", variant, dark ? "#D8DEE6" : "#FFFFFF"),
            KeyBlack = RollBrush("BrushKeyBlack", variant, dark ? "#2A323C" : "#E4EAF2"),
            KeySel = RollBrush("BrushRollKeySel", variant, dark ? "#2F4A6B" : "#C7DBFA"),
            RulerBg = RollBrush("BrushRollRuler", variant, dark ? "#1C2229" : "#EEF3F9"),
            LaneBorder = RollPen("BrushBorder", variant, dark ? "#2C343E" : "#DDE3EA", 1),
            MarqueeFill = RollBrush("BrushRollMarquee", variant, dark ? "#404C9AFF" : "#331F6FEB"),
            MarqueePen = RollPen("BrushRollAccent", variant, dark ? "#4C9AFF" : "#1F6FEB", 1),
        };
    }

    /// <summary>按资源名取主题画刷（先按皮肤查，查不到再不指定变体查一次）。</summary>
    private static IBrush RollBrush(string key, ThemeVariant variant, string fallback)
    {
        var app = Application.Current;
        if (app != null)
        {
            if (app.TryFindResource(key, variant, out var themed) && themed is IBrush tb) return tb;
            if (app.TryFindResource(key, out var any) && any is IBrush ab) return ab;
        }
        return new SolidColorBrush(Color.Parse(fallback));
    }

    private static IPen RollPen(string key, ThemeVariant variant, string fallback, double thickness)
        => new Pen(RollBrush(key, variant, fallback), thickness);

    private static IBrush Bg => CurrentSkin().Bg;
    private static IPen BorderPen => CurrentSkin().BorderPen;
    private static IPen GridPen => CurrentSkin().GridPen;
    private static IPen BeatPen => CurrentSkin().BeatPen;
    private static IPen BarPen => CurrentSkin().BarPen;
    private static IPen OctavePen => CurrentSkin().OctavePen;
    /// <summary>声轨调色板色数（与 Theme.axaml 的 BrushVoice0..11 对齐）。</summary>
    public const int VoiceCount = 12;

    private static IBrush[]? _palette;
    private static ThemeVariant? _paletteVariant;

    private static IBrush[] Palette()
    {
        var variant = Application.Current?.ActualThemeVariant;
        var cached = _palette;
        if (cached != null && _paletteVariant == variant) return cached;

        var list = new IBrush[VoiceCount];
        for (int i = 0; i < VoiceCount; i++)
        {
            list[i] = ThemeSwitch.BrushOf("BrushVoice" + i)
                      ?? new SolidColorBrush(Color.Parse(FallbackVoice[i]));
        }
        _palette = list;
        _paletteVariant = variant;
        return list;
    }

    /// <summary>资源字典取不到时的兜底色值（白底那一套，与 Theme.axaml 的 Light 一致）。</summary>
    private static readonly string[] FallbackVoice =
    {
        "#1660C8", "#0F7A46", "#8A6500", "#C22B2B", "#4C7A0A", "#0D6E8C",
        "#AE4A10", "#7A45C8", "#C0185E", "#0E7A72", "#4A4ED0", "#96601A"
    };

    /// <summary>该声轨颜色号对应的画刷。</summary>
    private static IBrush VoiceBrushOf(int voice)
    {
        var p = Palette();
        int i = voice <= 0 ? 0 : voice % VoiceCount;
        if (i < 0) i = 0;
        return p[i];
    }

    /// <summary>音高 → 声轨颜色号（兜底用）。音符自带 Voice 时不查这里。</summary>
    private int VoiceIndexOf(int pitch) =>
        _voiceOfPitch.TryGetValue(pitch, out int v) ? v : 0;

    public int VoiceOfNote(RawNote note) =>
        note.Voice >= 0 ? note.Voice : VoiceIndexOf(note.Pitch);

    /// <summary>卷帘当前持有的音符（诊断报告用）。</summary>
    public IReadOnlyList<RawNote> Notes => _notes;

    /// <summary>该音高当前是否算「可演奏」（决定画声轨色还是画灰）。</summary>
    public bool IsInRangePitch(int pitch) => _inRange.Contains(pitch);

    private static IBrush SkipBrush => CurrentSkin().Skip;
    private static IPen HoverPen => CurrentSkin().HoverPen;
    private static IPen SelPen => CurrentSkin().SelPen;
    private static IPen HeadPen => CurrentSkin().HeadPen;
    private static IBrush HeadBrush => CurrentSkin().Head;
    private static IBrush ReadoutBg => CurrentSkin().ReadoutBg;
    private static IPen ReadoutBorder => CurrentSkin().ReadoutBorder;
    private static IBrush TextBrush => CurrentSkin().Text;
    private static IBrush MutedBrush => CurrentSkin().Muted;
    private static IBrush KeyWhite => CurrentSkin().KeyWhite;
    private static IBrush KeyBlack => CurrentSkin().KeyBlack;
    private static IBrush KeySel => CurrentSkin().KeySel;
    private static IBrush RulerBg => CurrentSkin().RulerBg;
    private static IPen LaneBorder => CurrentSkin().LaneBorder;
    private static IBrush MarqueeFill => CurrentSkin().MarqueeFill;
    private static IPen MarqueePen => CurrentSkin().MarqueePen;

    private static readonly Typeface Face = Typeface.Default;
    private static readonly int[] BlackPc = { 1, 3, 6, 8, 10 };

    // 光标在每次指针移动时都要赋值，按类型缓存成静态实例，避免高频新建（UI-19）
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private static readonly Cursor SizeWECursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor SizeAllCursor = new(StandardCursorType.SizeAll);

    private static readonly Dictionary<(string Text, double Size, IBrush Brush), FormattedText> TextCache = new();

    private static FormattedText TextOf(string text, double size, IBrush brush)
    {
        var key = (text, size, brush);
        if (TextCache.TryGetValue(key, out var ft)) return ft;
        if (TextCache.Count > 512) TextCache.Clear();
        ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                               Face, size, brush);
        TextCache[key] = ft;
        return ft;
    }

    /// <summary>拖动中持续触发：只挪指针，不打断播放。</summary>
    public event Action<double>? SeekPreview;
    /// <summary>松手：真正跳转。</summary>
    public event Action<double>? SeekCommitted;
    /// <summary>选择集合变化。</summary>
    public event Action? SelectionChanged;
    /// <summary>一次编辑手势结束：提交新的完整谱面 + 可读的动作描述。</summary>
    public event Action<IReadOnlyList<RawNote>, string>? EditCommitted;
    /// <summary>视口 / 缩放 / 跟随状态变化，供工具栏刷新读数。</summary>
    public event Action? ViewChanged;
    /// <summary>试听请求：点选 / 新增 / 拖动改音高 / 点琴键栏时，把该音高交给上层发声。</summary>
    public event Action<int>? NoteAuditioned;

    public bool SnapEnabled { get; set; } = true;
    public double SnapSeconds { get; set; } = 0.1;
    /// <summary>加音用的默认长度，由 MainWindow 按当前谱面中位音长设置。</summary>
    public double DefaultNoteSeconds { get; set; } = 0.25;
    /// <summary>播放中自动把视口跟到播放头；用户手动滚动会关掉它。</summary>
    public bool FollowPlayhead { get; set; } = true;
    /// <summary>由 MainWindow 告知是否正在播放（只有播放中才自动跟随）。</summary>
    public bool IsPlaying { get; set; }

    public int SelectedCount => _sel.Count;
    /// <summary>卷帘当前持有的音符数。用于诊断"能出声但不显示"这类不同步问题。</summary>
    public int NoteCount => _notes.Count;
    public bool HasSelection => _sel.Count > 0;
    public double ViewFrom => _viewFrom;
    public double ViewTo => _viewTo;
    /// <summary>缩放读数：整首歌刚好铺满 = 100%。</summary>
    public double ZoomPercent => _total <= 0 ? 100 : _total / ViewSpan * 100.0;

    public PianoRoll()
    {
        Cursor = HandCursor;
        ClipToBounds = true;
        Focusable = true;
    }

    // ================= 数据 =================

    /// <summary>换谱：整首歌铺满视口并清空选择。载入文件 / 换轨 / 改取音选项后调用。</summary>
    public void SetNotes(IReadOnlyList<RawNote> notes, IEnumerable<int> inRangePitches, double totalSeconds,
                         bool preserveView = false)
    {
        _notes = new List<RawNote>(notes ?? Array.Empty<RawNote>());
        _inRange = new HashSet<int>(inRangePitches ?? Array.Empty<int>());
        _total = Math.Max(totalSeconds, 0.5);
        _sel.Clear();
        _hover = -1;
        if (preserveView) ClampView();      // 保留缩放与位置（撤销/重做时用），只把视口夹回新总长
        else { _viewFrom = 0; _viewTo = _total; }
        _positionAtLastFollow = double.NaN;
        EnsureBarsInvalid();   // 音符换了必须废掉条形缓存（不能靠调用方先调 SetVoiceColors 兜底）
        InvalidateVisual();
        RaiseSelectionChanged();
        RaiseViewChanged();
    }

    public bool PreviewOnly { get; private set; }

    /// <summary>进「只看」模式之前的那份谱面与视口，退出时原样放回来。</summary>
    private (List<RawNote> Notes, HashSet<int> InRange, double Total,
             Dictionary<int, int> VoiceOfPitch, double From, double To)? _previewSaved;

    public void BeginPreview()
    {
        if (PreviewOnly) return;
        _previewSaved = (new List<RawNote>(_notes), new HashSet<int>(_inRange), _total,
                         new Dictionary<int, int>(_voiceOfPitch), _viewFrom, _viewTo);
        PreviewOnly = true;
        ClearSelection();      // 选中的是「谱面里的下标」，换了谱面就不作数了
        _hover = -1;
        InvalidateVisual();
    }

    /// <summary>退出「只看」模式：把之前那份谱面、视口与配色原样放回来。</summary>
    public void RestoreAfterPreview()
    {
        if (!PreviewOnly) return;
        PreviewOnly = false;
        if (_previewSaved is { } s)
        {
            _previewSaved = null;
            _notes = s.Notes;
            _inRange = s.InRange;
            _total = s.Total;
            _voiceOfPitch = s.VoiceOfPitch;
            _viewFrom = s.From;
            _viewTo = s.To;
            _sel.Clear();
            _hover = -1;
            EnsureBarsInvalid();
            InvalidateVisual();
            RaiseSelectionChanged();
            RaiseViewChanged();
        }
        else InvalidateVisual();
    }

    public void SetVoiceColors(IReadOnlyDictionary<int, int>? pitchToVoice)
    {
        _voiceOfPitch = new Dictionary<int, int>();
        if (pitchToVoice != null)
        {
            foreach (var kv in pitchToVoice)
            {
                if (kv.Key < 0 || kv.Key > 127) continue;
                int v = kv.Value < 0 ? 0 : kv.Value;
                if (_voiceOfPitch.TryGetValue(kv.Key, out int old) && old <= v) continue;
                _voiceOfPitch[kv.Key] = v;
            }
        }
        EnsureBarsInvalid();
        InvalidateVisual();
    }

    public void SetInRangePitches(IEnumerable<int> pitches)
    {
        _inRange = new HashSet<int>(pitches ?? Array.Empty<int>());
        EnsureBarsInvalid();
        InvalidateVisual();
    }

    /// <summary>标尺画小节线用的速度与拍号。</summary>
    public void SetTempo(double secondsPerBeat, int beatsPerBar)
    {
        if (secondsPerBeat > 0.01 && secondsPerBeat < 10) _secPerBeat = secondsPerBeat;
        if (beatsPerBar >= 1 && beatsPerBar <= 16) _beatsPerBar = beatsPerBar;
        InvalidateVisual();
    }

    /// <summary>「去除开头空拍」剪掉的秒数；>0 时在卷帘左下角标出来。</summary>
    public void SetTrimInfo(double removedSeconds)
    {
        double v = Math.Max(0, removedSeconds);
        if (Math.Abs(v - _trimmedLead) < 1e-6) return;
        _trimmedLead = v;
        InvalidateVisual();
    }

    public void SetPosition(double seconds)
    {
        double v = Math.Clamp(seconds * _playheadScale, 0, _total);
        if (Math.Abs(v - _position) > 0.0005)
        {
            _position = v;
            InvalidateVisual();
        }
        ApplyFollow();
    }

    public void SetPlayheadScale(double scale)
    {
        double v = scale > 0.05 && scale < 20 ? scale : 1.0;
        if (Math.Abs(v - _playheadScale) < 1e-9) return;
        _playheadScale = v;
        InvalidateVisual();
    }

    // ================= 视口 =================

    public void FitAll()
    {
        _viewFrom = 0;
        _viewTo = _total;
        _positionAtLastFollow = double.NaN;
        InvalidateVisual();
        RaiseViewChanged();
    }

    /// <summary>以某时刻为锚点缩放。factor &lt; 1 放大。</summary>
    public void Zoom(double factor, double anchorSeconds)
    {
        if (_total <= 0) return;
        double span = ViewSpan;
        double newSpan = Math.Clamp(span * factor, 0.35, Math.Max(_total, 0.5));
        double frac = (anchorSeconds - _viewFrom) / span;
        _viewFrom = anchorSeconds - frac * newSpan;
        _viewTo = _viewFrom + newSpan;
        ClampView();
        InvalidateVisual();
        RaiseViewChanged();
    }

    public void ZoomCenter(double factor) => Zoom(factor, _viewFrom + ViewSpan / 2);

    /// <summary>整体平移视口。用户主动操作会关掉播放头跟随。</summary>
    public void PanBy(double seconds, bool userInitiated = true)
    {
        double span = ViewSpan;
        _viewFrom = Math.Clamp(_viewFrom + seconds, 0, Math.Max(0, _total - span));
        _viewTo = _viewFrom + span;
        if (userInitiated) SetFollow(false);
        InvalidateVisual();
        RaiseViewChanged();
    }

    public void SetFollow(bool on)
    {
        if (FollowPlayhead == on) return;
        FollowPlayhead = on;
        RaiseViewChanged();
    }

    private void ClampView()
    {
        double span = Math.Min(ViewSpan, Math.Max(_total, 0.5));
        if (_viewFrom < 0) _viewFrom = 0;
        if (_viewFrom + span > _total) _viewFrom = Math.Max(0, _total - span);
        _viewTo = _viewFrom + span;
    }

    /// <summary>播放头跑出视口时页跳跟随（播放头落到视口 30% 处）。</summary>
    private void ApplyFollow()
    {
        if (!FollowPlayhead || !IsPlaying || _total <= 0) return;
        double span = ViewSpan;
        if (span >= _total - 1e-9) return;                 // 全曲可见，不必跟随
        double rel = (_position - _viewFrom) / span;
        if (rel >= 0.02 && rel <= 0.7) return;             // 还在舒适区内
        if (Math.Abs(_positionAtLastFollow - _position) < 1e-6) return;
        _viewFrom = Math.Clamp(_position - span * 0.3, 0, Math.Max(0, _total - span));
        _viewTo = _viewFrom + span;
        _positionAtLastFollow = _position;
        InvalidateVisual();
        RaiseViewChanged();
    }

    // ================= 选择 =================

    public void ClearSelection()
    {
        if (_sel.Count == 0) return;
        _sel.Clear();
        InvalidateVisual();
        RaiseSelectionChanged();
    }

    public void SelectAll()
    {
        _sel.Clear();
        for (int i = 0; i < _notes.Count; i++) _sel.Add(i);
        InvalidateVisual();
        RaiseSelectionChanged();
    }

    /// <summary>删除所选（工具栏按钮 / Delete 键调用）。</summary>
    public void DeleteSelected()
    {
        if (_sel.Count == 0) return;
        int n = _sel.Count;
        var keep = new List<RawNote>(_notes.Count - n);
        for (int i = 0; i < _notes.Count; i++) if (!_sel.Contains(i)) keep.Add(_notes[i]);
        _notes = keep;
        _sel.Clear();
        Commit($"删除 {n} 个音");
    }

    // ================= 坐标换算（渲染与命中共用同一套） =================

    private double PlotW => Math.Max(1, Bounds.Width - KeyW - PadR);
    private double PlotH => Math.Max(1, Bounds.Height - RulerH - PadB);
    private double ViewSpan => Math.Max(1e-6, _viewTo - _viewFrom);

    private double XOf(double sec) => KeyW + (sec - _viewFrom) / ViewSpan * PlotW;

    private double TimeAt(double x) =>
        _viewFrom + Math.Clamp((x - KeyW) / PlotW, 0, 1) * ViewSpan;

    public static (int Lo, int Hi) ComputePitchRange(IReadOnlyList<RawNote> notes)
    {
        if (notes.Count == 0) return (FallbackLo, FallbackHi);
        int lo = int.MaxValue, hi = int.MinValue;
        foreach (var n in notes) { if (n.Pitch < lo) lo = n.Pitch; if (n.Pitch > hi) hi = n.Pitch; }

        int span = MinSpan;
        int center = (lo + hi) / 2;
        lo = center - span / 2;
        hi = lo + span;
        // 夹到合法 MIDI 音高
        if (lo < 0) { hi -= lo; lo = 0; }
        if (hi > 127) { lo -= hi - 127; hi = 127; }
        if (lo < 0) lo = 0;
        return (lo, hi);
    }

    private (int Lo, int Hi) PitchRange()
    {
        if (_notes.Count == 0) return (_lastLo, _lastHi);
        var r = ComputePitchRange(_notes);
        _lastLo = r.Lo;
        _lastHi = r.Hi;
        return r;
    }

    private double RowH((int Lo, int Hi) r) => PlotH / Math.Max(1, r.Hi - r.Lo + 1);

    private double YCenterOf(int pitch, (int Lo, int Hi) r) =>
        YCenterOf(pitch, r, RowH(r));

    private double YCenterOf(int pitch, (int Lo, int Hi) r, double rowH) =>
        RulerH + (r.Hi - pitch + 0.5) * rowH;

    private int PitchAt(double y, (int Lo, int Hi) r)
    {
        double row = (y - RulerH) / RowH(r);
        int p = r.Hi - (int)Math.Floor(Math.Clamp(row, 0, r.Hi - r.Lo + 0.999));
        return Math.Clamp(p, 0, 127);
    }

    private Rect RectOf(int pitch, double start, double end)
    {
        var r = PitchRange();
        return RectOf(pitch, start, end, r, RowH(r));
    }

    private Rect RectOf(int pitch, double start, double end, (int Lo, int Hi) r, double rowH)
    {
        double barH = Math.Max(3, rowH * 0.72);
        double x0 = XOf(start), x1 = XOf(end);
        double yc = YCenterOf(pitch, r, rowH);
        return new Rect(x0, yc - barH / 2, Math.Max(2.5, x1 - x0), barH);
    }

    private static bool IsBlackKey(int pitch) => Array.IndexOf(BlackPc, Music.Mod(pitch, 12)) >= 0;

    private double Snap(double sec) =>
        !SnapEnabled || SnapSeconds <= 0 ? sec : Math.Round(sec / SnapSeconds) * SnapSeconds;

    private static bool ShiftHeld(KeyModifiers m) => m.HasFlag(KeyModifiers.Shift);

    /// <summary>一个吸附步长，同时作为改长度的下限。</summary>
    private double MinLen => SnapEnabled && SnapSeconds > 0 ? SnapSeconds : MinNoteSeconds;

    /// <summary>命中音符：先看矩形（外扩 3px）；行太薄时退化为"取纵向最近的一行"，但收紧到半行高。</summary>
    private int HitTest(Point p)
    {
        int best = -1;
        double bestDy = double.MaxValue;
        // 音域与行高在循环外算一次，循环里只传值（每音重算就是 O(N²)，见 UI-03）
        var r = PitchRange();
        double rowH = RowH(r);
        for (int i = _notes.Count - 1; i >= 0; i--)
        {
            var n = _notes[i];
            if (n.End < _viewFrom || n.Start > _viewTo) continue;
            var rect = RectOf(n.Pitch, n.Start, n.End, r, rowH);
            if (rect.Inflate(3).Contains(p)) return i;
            if (p.X >= rect.X - 4 && p.X <= rect.Right + 4)
            {
                double dy = Math.Abs(rect.Center.Y - p.Y);
                if (dy < bestDy) { bestDy = dy; best = i; }
            }
        }
        double limit = Math.Max(4, rowH * 0.55);
        return bestDy <= limit ? best : -1;
    }

    private static double GrabBand(double noteWidth) => Math.Min(EdgeGrabPx, Math.Max(2.0, noteWidth * 0.3));

    private static DragMode EdgeAt(Rect rect, double x)
    {
        double band = GrabBand(rect.Width);
        bool nearLeft = x <= rect.Left + band;
        bool nearRight = x >= rect.Right - band;
        if (nearLeft && nearRight)
        {
            if (x - rect.Left <= rect.Right - x) nearRight = false;
            else nearLeft = false;
        }
        if (nearLeft) return DragMode.ResizeL;
        if (nearRight) return DragMode.ResizeR;
        return DragMode.Move;
    }

    // ================= 绘制 =================

    private void EnsureBars()
    {
        var (lo, hi) = PitchRange();
        if (_barsSkip is not null && Math.Abs(_builtW - Bounds.Width) < 0.5
            && Math.Abs(_builtH - Bounds.Height) < 0.5
            && Math.Abs(_builtFrom - _viewFrom) < 1e-9 && Math.Abs(_builtTo - _viewTo) < 1e-9
            && _builtLo == lo && _builtHi == hi && _builtCount == _notes.Count)
            return;

        double rowH = RowH((lo, hi));
        double barH = Math.Max(3, rowH * 0.72);
        // 可演奏音按声轨颜色号各建一组几何，超出音域的统一进灰组
        var skip = new GeometryGroup();
        _barsByVoice.Clear();
        foreach (var n in _notes)
        {
            if (n.End <= n.Start) continue;
            if (n.End < _viewFrom || n.Start > _viewTo) continue;   // 视口外不参与绘制
            double x0 = XOf(n.Start), x1 = XOf(n.End);
            double yc = YCenterOf(n.Pitch, (lo, hi), rowH);
            var rect = new Rect(x0, yc - barH / 2, Math.Max(2.5, x1 - x0), barH);

            if (!_inRange.Contains(n.Pitch))
            {
                skip.Children.Add(new RectangleGeometry(rect));
                continue;
            }
            int voice = VoiceOfNote(n);
            if (!_barsByVoice.TryGetValue(voice, out var g))
            {
                g = new GeometryGroup();
                _barsByVoice[voice] = g;
            }
            g.Children.Add(new RectangleGeometry(rect));
        }
        _barsSkip = skip;
        _builtW = Bounds.Width;
        _builtH = Bounds.Height;
        _builtFrom = _viewFrom;
        _builtTo = _viewTo;
        _builtLo = lo;
        _builtHi = hi;
        _builtCount = _notes.Count;
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        ctx.DrawRectangle(Bg, BorderPen, new Rect(0, 0, w, h), 6, 6);
        if (w <= KeyW + PadR + 8 || h <= RulerH + PadB + 8 || _total <= 0) return;

        EnsureBars();
        var range = PitchRange();

        DrawPlotGrid(ctx, range);
        DrawKeyLane(ctx, range);
        DrawNotes(ctx, range);
        DrawRuler(ctx);
        DrawMarquee(ctx);
        DrawPlayhead(ctx);
        DrawTrimNote(ctx);
        DrawReadout(ctx);
    }

    private void DrawTrimNote(DrawingContext ctx)
    {
        if (_trimmedLead <= 0.05) return;
        var ft = TextOf($"已剪掉开头 {_trimmedLead:F1}s 空拍", 10.5, MutedBrush);
        ctx.DrawText(ft, new Point(KeyW + 6, RulerH + PlotH - ft.Height - 2));
    }

    private void DrawPlotGrid(DrawingContext ctx, (int Lo, int Hi) range)
    {
        double top = RulerH, bottom = RulerH + PlotH;

        // 小节 / 拍线；太密就退回"秒"级网格，避免糊成一片
        double beatPx = _secPerBeat / ViewSpan * PlotW;
        if (beatPx >= 4)
        {
            double first = Math.Floor(_viewFrom / _secPerBeat) * _secPerBeat;
            for (double t = first; t <= _viewTo; t += _secPerBeat)
            {
                if (t < 0) continue;
                int beatIdx = (int)Math.Round(t / _secPerBeat);
                bool isBar = _beatsPerBar > 0 && beatIdx % _beatsPerBar == 0;
                double x = XOf(t);
                ctx.DrawLine(isBar ? BarPen : BeatPen, new Point(x, top), new Point(x, bottom));
            }
        }
        else
        {
            double step = NiceStep(ViewSpan);
            for (double t = Math.Ceiling(_viewFrom / step) * step; t <= _viewTo; t += step)
            {
                double x = XOf(t);
                ctx.DrawLine(GridPen, new Point(x, top), new Point(x, bottom));
            }
        }

        // 每个 C 一条八度线，作为纵向参照
        double rowH = RowH(range);
        for (int p = range.Lo; p <= range.Hi; p++)
        {
            if (Music.Mod(p, 12) != 0) continue;
            double y = RulerH + (range.Hi - p) * rowH;
            ctx.DrawLine(OctavePen, new Point(KeyW, y), new Point(KeyW + PlotW, y));
        }
    }

    /// <summary>左侧琴键栏：黑白键底色 + 音名标注，点击可选中该音高的全部音符。</summary>
    private void DrawKeyLane(DrawingContext ctx, (int Lo, int Hi) range)
    {
        double rowH = RowH(range);
        ctx.FillRectangle(RulerBg, new Rect(0, RulerH, KeyW - 1, PlotH));
        ctx.DrawLine(LaneBorder, new Point(KeyW, RulerH), new Point(KeyW, RulerH + PlotH));

        for (int p = range.Lo; p <= range.Hi; p++)
        {
            double y = RulerH + (range.Hi - p) * rowH;
            var rect = new Rect(0.5, y + 0.5, KeyW - 1.5, Math.Max(1, rowH - 1));
            var brush = _hoverKey == p ? KeySel : (IsBlackKey(p) ? KeyBlack : KeyWhite);
            ctx.FillRectangle(brush, rect);

            // 只标 do（各家的通行做法）：行高很薄时也只标 do 才不会糊成一片
            if (rowH < 7 || Music.Mod(p, 12) != 0) continue;
            var ft = TextOf(Music.SolfegeName(p), 9, TextBrush);
            ctx.DrawText(ft, new Point(KeyW - 4 - ft.Width, y + (rowH - ft.Height) / 2));
        }
    }

    private void DrawNotes(DrawingContext ctx, (int Lo, int Hi) range)
    {
        // 灰条 = 超出音域（不可演奏）；其它每个声轨一个颜色，与左侧列表一一对应
        if (_barsSkip is not null) ctx.DrawGeometry(SkipBrush, null, _barsSkip);
        if (_barsByVoice.Count > 0)
        {
            int maxVoice = 0;
            foreach (int v in _barsByVoice.Keys) if (v > maxVoice) maxVoice = v;
            for (int v = 0; v <= maxVoice; v++)
                if (_barsByVoice.TryGetValue(v, out var g) && g.Children.Count > 0)
                    ctx.DrawGeometry(VoiceBrushOf(v), null, g);
        }

        // 下面的循环逐音符求矩形，音域与行高先算一次（UI-03）
        double rowH = RowH(range);

        // 拖动中：用底色擦掉原位，再画临时块（不重建几何缓存）
        if (_mode is DragMode.Move or DragMode.ResizeL or DragMode.ResizeR && _dragNow.Count > 0)
        {
            foreach (var o in _dragNow)
            {
                ctx.FillRectangle(Bg, RectOf(o.Note.Pitch, o.Note.Start, o.Note.End, range, rowH).Inflate(2));
                var r = RectOf(o.Pitch, o.Start, o.End, range, rowH);
                ctx.FillRectangle(_inRange.Contains(o.Pitch) ? VoiceBrushOf(VoiceOfNote(o.Note)) : SkipBrush, r);
                ctx.DrawRectangle(null, SelPen, r);
            }
            return;
        }

        if (_hover >= 0 && _hover < _notes.Count && !_sel.Contains(_hover))
        {
            var n = _notes[_hover];
            ctx.DrawRectangle(null, HoverPen, RectOf(n.Pitch, n.Start, n.End, range, rowH).Inflate(1.5));
        }
        foreach (int i in _sel)
        {
            if (i < 0 || i >= _notes.Count) continue;
            var n = _notes[i];
            ctx.DrawRectangle(null, SelPen, RectOf(n.Pitch, n.Start, n.End, range, rowH).Inflate(1.5));
        }
    }

    /// <summary>顶部标尺：小节号或秒数，便于判断"现在在第几小节"。</summary>
    private void DrawRuler(DrawingContext ctx)
    {
        ctx.FillRectangle(RulerBg, new Rect(0, 0, Bounds.Width, RulerH));
        ctx.DrawLine(LaneBorder, new Point(0, RulerH), new Point(Bounds.Width, RulerH));

        double barSec = _secPerBeat * _beatsPerBar;
        if (barSec > 0 && barSec / ViewSpan * PlotW >= 22)
        {
            int firstBar = (int)Math.Max(0, Math.Floor(_viewFrom / barSec));
            for (int k = firstBar; ; k++)
            {
                double t = k * barSec;
                if (t > _viewTo) break;
                double x = XOf(t);
                ctx.DrawLine(BarPen, new Point(x, RulerH - 7), new Point(x, RulerH));
                var ft = TextOf((k + 1).ToString(), 9.5, MutedBrush);
                ctx.DrawText(ft, new Point(x + 3, 1));
            }
        }
        else
        {
            double step = NiceStep(ViewSpan);
            for (double t = Math.Ceiling(_viewFrom / step) * step; t <= _viewTo; t += step)
            {
                double x = XOf(t);
                ctx.DrawLine(BarPen, new Point(x, RulerH - 6), new Point(x, RulerH));
                var ft = TextOf(FormatSeconds(t), 9.5, MutedBrush);
                ctx.DrawText(ft, new Point(x + 3, 1));
            }
        }
    }

    private void DrawMarquee(DrawingContext ctx)
    {
        if (_mode != DragMode.Marquee) return;
        if (_marquee.Width < 1 && _marquee.Height < 1) return;
        ctx.DrawRectangle(MarqueeFill, MarqueePen, _marquee);
    }

    private void DrawPlayhead(DrawingContext ctx)
    {
        double px = XOf(_position);
        if (px < KeyW - 1 || px > KeyW + PlotW + 1) return;
        ctx.DrawLine(HeadPen, new Point(px, RulerH), new Point(px, RulerH + PlotH));
        var head = new StreamGeometry();
        using (var c = head.Open())
        {
            c.BeginFigure(new Point(px - 4, RulerH), true);
            c.LineTo(new Point(px + 4, RulerH));
            c.LineTo(new Point(px, RulerH + 6));
            c.EndFigure(true);
        }
        ctx.DrawGeometry(HeadBrush, null, head);
    }

    /// <summary>悬停 / 拖动时显示简谱音高、时间与选中数量。</summary>
    private void DrawReadout(DrawingContext ctx)
    {
        string? text = null;
        if (_mode is DragMode.Move or DragMode.ResizeL or DragMode.ResizeR && _dragNow.Count > 0)
        {
            var o = _dragNow[0];
            string extra = _dragNow.Count > 1 ? $"   （{_dragNow.Count} 个）" : "";
            text = $"{Music.SolfegeName(o.Pitch)}   {o.Start:F2} - {o.End:F2}s{extra}";
        }
        else if (_hover >= 0 && _hover < _notes.Count)
        {
            var n = _notes[_hover];
            text = $"{Music.SolfegeName(n.Pitch)}   {n.Start:F2} - {n.End:F2}s";
        }
        if (text is null) return;

        var ft = TextOf(text, 12, TextBrush);
        var box = new Rect(KeyW + 4, RulerH + 4, ft.Width + 12, ft.Height + 6);
        ctx.DrawRectangle(ReadoutBg, ReadoutBorder, box, 4, 4);
        ctx.DrawText(ft, new Point(box.X + 6, box.Y + 3));
    }

    private static string FormatSeconds(double t) =>
        t >= 60 ? $"{(int)(t / 60)}:{t % 60:00}" : $"{t:0.##}s";

    /// <summary>从 1/2/5/10… 里挑步长，让网格线落在 6-12 条。</summary>
    private static double NiceStep(double total)
    {
        double[] cand = { 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300 };
        foreach (var s in cand)
            if (total / s <= 12) return s;
        return total / 12.0;
    }

    // ================= 交互 =================

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (_total <= 0) return;
        // 「只看」模式（歌单勾音轨时的预览）：能定位、能滚，不许选音符、不许加删改
        if (PreviewOnly) return;

        var p = e.GetPosition(this);
        _lastPt = p;
        var props = e.GetCurrentPoint(this).Properties;

        if (props.IsMiddleButtonPressed)
        {
            _mode = DragMode.Pan;
            _pressPt = p;
            e.Pointer.Capture(this);
            Cursor = SizeWECursor;
            e.Handled = true;
            return;
        }

        if (props.IsRightButtonPressed)
        {
            _mode = DragMode.Erase;
            _eraseDone.Clear();
            e.Pointer.Capture(this);
            EraseAt(p);
            e.Handled = true;
            return;
        }

        if (!props.IsLeftButtonPressed) return;

        // 标尺：定位
        if (p.Y < RulerH)
        {
            _mode = DragMode.ScrubRuler;
            _dragMoved = false;
            e.Pointer.Capture(this);
            SeekPreview?.Invoke(TimeAt(p.X));
            e.Handled = true;
            return;
        }

        // 琴键栏：选中该音高的全部音符（按住拖动连选）
        if (p.X < KeyW)
        {
            _mode = DragMode.KeyLane;
            _pitchSelDone.Clear();
            if (!ShiftHeld(e.KeyModifiers)) _sel.Clear();
            SelectPitchAt(p);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        int hit = HitTest(p);

        // 双击空白：加音，并立刻进入改尾手势，一次手势把位置与长度都定好
        if (e.ClickCount >= 2 && hit < 0)
        {
            double len = Math.Clamp(DefaultNoteSeconds, 0.05, 5.0);
            double st = Math.Max(0, Snap(TimeAt(p.X)));
            var n = new RawNote { Pitch = PitchAt(p.Y, PitchRange()), Start = st, End = st + len };
            _notes.Add(n);
            _notes.Sort((a, b) => a.Start.CompareTo(b.Start));
            int idx = _notes.IndexOf(n);
            _sel.Clear();
            _sel.Add(idx);
            BeginDrag(n, p);
            _mode = DragMode.ResizeR;
            e.Pointer.Capture(this);
            InvalidateVisual();
            RaiseSelectionChanged();
            NoteAuditioned?.Invoke(n.Pitch);
            Commit("加音");
            e.Handled = true;
            return;
        }

        e.Pointer.Capture(this);
        _pressPt = p;
        _dragMoved = false;

        if (hit >= 0)
        {
            if (ShiftHeld(e.KeyModifiers))
            {
                if (!_sel.Remove(hit)) _sel.Add(hit);
                _mode = DragMode.None;
                InvalidateVisual();
                RaiseSelectionChanged();
                e.Handled = true;
                return;
            }

            // 已选中的音再按下时保留整组选择（便于整组拖动），否则只选它
            if (!_sel.Contains(hit)) { _sel.Clear(); _sel.Add(hit); }
            InvalidateVisual();
            RaiseSelectionChanged();

            var rect = RectOf(_notes[hit].Pitch, _notes[hit].Start, _notes[hit].End);
            var edge = EdgeAt(rect, p.X);

            BeginDrag(_notes[hit], p);
            NoteAuditioned?.Invoke(_notes[hit].Pitch);
            if (edge == DragMode.ResizeL) { _mode = DragMode.ResizeL; Cursor = SizeWECursor; }
            else if (edge == DragMode.ResizeR) { _mode = DragMode.ResizeR; Cursor = SizeWECursor; }
            else { _mode = DragMode.Move; Cursor = SizeAllCursor; }
        }
        else
        {
            // 空白：先"待定"，拖起来才是框选，松手没动就是定位
            _mode = DragMode.Marquee;
            _marquee = new Rect(p, p);
        }
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        _lastPt = p;

        if (_mode == DragMode.None)
        {
            UpdateHover(p);
            return;
        }

        switch (_mode)
        {
            case DragMode.ScrubRuler:
                SeekPreview?.Invoke(TimeAt(p.X));
                break;

            case DragMode.Pan:
            {
                double span = ViewSpan;
                double dx = (p.X - _pressPt.X) / PlotW * span;
                _viewFrom = Math.Clamp(_viewFrom - dx, 0, Math.Max(0, _total - span));
                _viewTo = _viewFrom + span;
                _pressPt = p;
                SetFollow(false);
                InvalidateVisual();
                break;
            }

            case DragMode.Marquee:
            {
                double dx = p.X - _pressPt.X, dy = p.Y - _pressPt.Y;
                if (!_dragMoved && Math.Abs(dx) + Math.Abs(dy) < DragThreshold) break;
                _dragMoved = true;
                _marquee = new Rect(Math.Min(_pressPt.X, p.X), Math.Min(_pressPt.Y, p.Y),
                                    Math.Abs(dx), Math.Abs(dy));
                ApplyMarquee(_marquee);
                InvalidateVisual();
                break;
            }

            case DragMode.KeyLane:
                SelectPitchAt(p);
                break;

            case DragMode.Erase:
                EraseAt(p);
                break;

            case DragMode.Move:
                DoMoveDrag(p, e.KeyModifiers);
                break;

            case DragMode.ResizeL:
                DoResizeDrag(p, e.KeyModifiers, leftEdge: true);
                break;

            case DragMode.ResizeR:
                DoResizeDrag(p, e.KeyModifiers, leftEdge: false);
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        EndDrag(e.GetPosition(this));
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag(_lastPt);
    }

    /// <summary>松手 / 捕获丢失的统一收尾：按手势模式提交或定位，再清掉拖动状态。</summary>
    private void EndDrag(Point p)
    {
        Cursor = HandCursor;
        var mode = _mode;
        _mode = DragMode.None;

        switch (mode)
        {
            case DragMode.ScrubRuler:
                SeekCommitted?.Invoke(TimeAt(p.X));
                break;

            case DragMode.Marquee when !_dragMoved:
                ClearSelection();
                SeekCommitted?.Invoke(TimeAt(p.X));
                break;

            case DragMode.Move:
                if (_dragMoved) CommitDrag("移动");
                break;

            case DragMode.ResizeL:
            case DragMode.ResizeR:
                // 与手势起点比较而不是看 _dragMoved：原地双击加音后松手不再压一次空快照（UI-04）
                if (ResizeChanged()) CommitDrag("改长度");
                break;

            case DragMode.Erase:
                if (_eraseDone.Count > 0) Commit($"删除 {_eraseDone.Count} 个音");
                break;
        }

        _dragBase.Clear();
        _dragNow.Clear();
        _dragMoved = false;
        _marquee = default;
        if (mode == DragMode.KeyLane) RaiseSelectionChanged();
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_total <= 0) return;
        if (ShiftHeld(e.KeyModifiers))
            PanBy(-Math.Sign(e.Delta.Y) * ViewSpan * 0.2);
        else
            Zoom(e.Delta.Y > 0 ? 0.8 : 1.25, TimeAt(e.GetPosition(this).X));
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_mode != DragMode.None) return;
        if (_hover != -1 || _hoverKey != -1)
        {
            _hover = -1;
            _hoverKey = -1;
            InvalidateVisual();
        }
    }

    /// <summary>Ctrl+A 全选、Esc 清空、方向键微调（左右按吸附步长、上下按半音）。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (PreviewOnly) return;   // 「只看」模式不许编辑
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (ctrl && e.Key == Key.A) { SelectAll(); e.Handled = true; return; }
        if (e.Key == Key.Escape) { ClearSelection(); e.Handled = true; return; }
        if (_sel.Count == 0) return;

        double dt = 0;
        int dp = 0;
        switch (e.Key)
        {
            case Key.Left: dt = -MinLen * (shift ? 4 : 1); break;
            case Key.Right: dt = MinLen * (shift ? 4 : 1); break;
            case Key.Up: dp = shift ? 12 : 1; break;
            case Key.Down: dp = shift ? -12 : -1; break;
            default: return;
        }
        NudgeSelection(dt, dp);
        e.Handled = true;
    }

    // ================= 手势实现 =================

    /// <summary>记录手势基准：被抓音符 + 当前选择各自的原位。这份数据在手势期间不得修改。</summary>
    private void BeginDrag(RawNote grabbed, Point p)
    {
        _dragBase.Clear();
        _dragNow.Clear();
        _anchorSec = TimeAt(p.X);
        _anchorPitch = PitchAt(p.Y, PitchRange());
        _dragMoved = false;
        _lastAuditioned = int.MinValue;

        // 多选时整组一起动。以抓到的那个音为基准决定吸附与音高位移，
        // 其余音保持相对位置 —— 逐个吸附会把和弦/乐句拉散。
        var selRefs = new List<RawNote>();
        foreach (int i in _sel) if (i >= 0 && i < _notes.Count) selRefs.Add(_notes[i]);
        if (!selRefs.Contains(grabbed)) selRefs.Insert(0, grabbed);
        foreach (var n in selRefs) _dragBase.Add((n, n.Pitch, n.Start, n.End));
        int gi = _dragBase.FindIndex(o => ReferenceEquals(o.Note, grabbed));
        if (gi > 0)   // 把抓到的那个排到首位，作为吸附基准
        {
            var g = _dragBase[gi];
            _dragBase.RemoveAt(gi);
            _dragBase.Insert(0, g);
        }
        _dragNow.AddRange(_dragBase);
    }

    private void DoMoveDrag(Point p, KeyModifiers mods)
    {
        if (_dragBase.Count == 0) return;

        // 位移一律相对**基准**计算（TimeAt 内部会夹到视口内，所以拖出画布也不会失控）
        double d = TimeAt(p.X) - _anchorSec;
        int dp = PitchAt(p.Y, PitchRange()) - _anchorPitch;

        var prim = _dragBase[0];
        if (!ShiftHeld(mods) && SnapEnabled && SnapSeconds > 0)
        {
            double s = prim.Start + d, e = prim.End + d;
            double adjS = Snap(s) - s, adjE = Snap(e) - e;
            d += Math.Abs(adjS) <= Math.Abs(adjE) ? adjS : adjE;
        }

        // 整组移调时不允许任何一个音出界
        int minP = int.MaxValue, maxP = int.MinValue;
        foreach (var o in _dragBase) { if (o.Pitch < minP) minP = o.Pitch; if (o.Pitch > maxP) maxP = o.Pitch; }
        dp = Math.Clamp(dp, -minP, 127 - maxP);

        // 音高变了就试听一下，方便对准；同一个音高只响一次，避免拖动时狂响
        int leadPitch = prim.Pitch + dp;
        if (leadPitch != _lastAuditioned)
        {
            _lastAuditioned = leadPitch;
            NoteAuditioned?.Invoke(leadPitch);
        }

        _dragNow.Clear();
        foreach (var o in _dragBase)
        {
            double st = Math.Max(0, o.Start + d);
            _dragNow.Add((o.Note, o.Pitch + dp, st, Math.Max(st + MinNoteSeconds, o.End + d)));
        }
        _dragMoved = true;
        InvalidateVisual();
    }

    private void DoResizeDrag(Point p, KeyModifiers mods, bool leftEdge)
    {
        if (_dragBase.Count == 0) return;
        double t = TimeAt(p.X);
        // 按住 Shift 临时关闭吸附（UI-15）
        if (!ShiftHeld(mods) && SnapEnabled && SnapSeconds > 0) t = Snap(t);

        // 同样以基准为准：改左端时右端取基准的尾，改右端时左端取基准的头
        var b = _dragBase[0];
        double st = b.Start, en = b.End;
        if (leftEdge) st = Math.Min(en - MinLen, Math.Max(0, t));
        else en = Math.Max(st + MinLen, t);
        _dragNow.Clear();
        _dragNow.Add((b.Note, b.Pitch, st, en));
        _dragMoved = true;
        InvalidateVisual();
    }

    /// <summary>手势结束时，改长度的结果与手势起点有没有差异（逐字段比较）。</summary>
    private bool ResizeChanged()
    {
        if (_dragNow.Count == 0 || _dragBase.Count == 0) return false;
        var a = _dragBase[0];
        var b = _dragNow[0];
        return b.Pitch != a.Pitch
            || Math.Abs(b.Start - a.Start) > 1e-9
            || Math.Abs(b.End - a.End) > 1e-9;
    }

    /// <summary>把拖动结果写回谱面：重建列表 + 排序 + 重映射选择，最后一次性提交。</summary>
    private void CommitDrag(string what)
    {
        if (_dragNow.Count == 0) return;

        var touched = new Dictionary<RawNote, (int Pitch, double Start, double End)>();
        foreach (var o in _dragNow) touched[o.Note] = (o.Pitch, o.Start, o.End);

        var selRefs = new HashSet<RawNote>();
        foreach (int i in _sel) if (i >= 0 && i < _notes.Count) selRefs.Add(_notes[i]);

        var rebuilt = new List<(RawNote N, bool Sel)>(_notes.Count);
        bool anyChange = false;
        foreach (var n in _notes)
        {
            if (touched.TryGetValue(n, out var v))
            {
                if (v.Pitch != n.Pitch || Math.Abs(v.Start - n.Start) > 1e-9 || Math.Abs(v.End - n.End) > 1e-9)
                    anyChange = true;
                // Voice 一起带走：拖动的音仍然属于原来那条声轨，颜色不该因为改位置就变
                rebuilt.Add((new RawNote
                {
                    Pitch = v.Pitch,
                    Start = v.Start,
                    End = v.End,
                    Velocity = n.Velocity,
                    Voice = n.Voice
                }, true));
            }
            else
            {
                rebuilt.Add((n, selRefs.Contains(n)));
            }
        }
        if (!anyChange) return;

        rebuilt.Sort((a, b) => a.N.Start.CompareTo(b.N.Start));
        var next = new List<RawNote>(rebuilt.Count);
        var newSel = new HashSet<int>();
        for (int i = 0; i < rebuilt.Count; i++)
        {
            next.Add(rebuilt[i].N);
            if (rebuilt[i].Sel) newSel.Add(i);
        }
        _notes = next;
        _sel.Clear();
        foreach (int i in newSel) _sel.Add(i);

        Commit(_dragNow.Count > 1 ? $"{what} {_dragNow.Count} 个音" : what);
    }

    private void Commit(string what)
    {
        _hover = -1;
        EnsureBarsInvalid();
        InvalidateVisual();
        RaiseSelectionChanged();
        EditCommitted?.Invoke(new List<RawNote>(_notes), what);
    }

    /// <summary>音符集合变了，几何缓存必须失效（否则画面还是旧谱面）。</summary>
    private void EnsureBarsInvalid()
    {
        _barsSkip = null;
        _barsByVoice.Clear();
        _builtCount = -1;
    }

    private void NudgeSelection(double dt, int dp)
    {
        if (_sel.Count == 0) return;
        var selRefs = new HashSet<RawNote>();
        foreach (int i in _sel) if (i >= 0 && i < _notes.Count) selRefs.Add(_notes[i]);

        var rebuilt = new List<(RawNote N, bool Sel)>(_notes.Count);
        bool anyChange = false;
        foreach (var n in _notes)
        {
            if (!selRefs.Contains(n)) { rebuilt.Add((n, false)); continue; }
            int p = Math.Clamp(n.Pitch + dp, 0, 127);
            double d = dt < 0 && n.Start <= 0 ? 0 : dt;
            double st = Math.Max(0, n.Start + d);
            double en = Math.Max(st + MinNoteSeconds, n.End + d);
            if (p == n.Pitch && Math.Abs(st - n.Start) < 1e-9 && Math.Abs(en - n.End) < 1e-9)
            {
                rebuilt.Add((n, true));   // 这个音没变，保留原件
                continue;
            }
            anyChange = true;
            rebuilt.Add((new RawNote
            {
                Pitch = p,
                Start = st,
                End = en,
                Velocity = n.Velocity,
                Voice = n.Voice      // 微调不改声轨归属
            }, true));
        }
        // 逐字段比较后全都没变化就不提交：否则会在撤销栈里压一步空操作并清空重做栈（UI-07）
        if (!anyChange) return;

        rebuilt.Sort((a, b) => a.N.Start.CompareTo(b.N.Start));
        var next = new List<RawNote>(rebuilt.Count);
        var newSel = new HashSet<int>();
        for (int i = 0; i < rebuilt.Count; i++)
        {
            next.Add(rebuilt[i].N);
            if (rebuilt[i].Sel) newSel.Add(i);
        }
        _notes = next;
        _sel.Clear();
        foreach (int i in newSel) _sel.Add(i);
        Commit("微调");
    }

    private void UpdateHover(Point p)
    {
        int h = p.X < KeyW ? -1 : HitTest(p);
        int hk = p.X < KeyW ? PitchAt(p.Y, PitchRange()) : -1;

        var want = HandCursor;
        if (h >= 0)
        {
            var rect = RectOf(_notes[h].Pitch, _notes[h].Start, _notes[h].End);
            var edge = EdgeAt(rect, p.X);
            want = edge is DragMode.ResizeL or DragMode.ResizeR ? SizeWECursor : SizeAllCursor;
        }
        if (h != _hover || hk != _hoverKey)
        {
            _hover = h;
            _hoverKey = hk;
            InvalidateVisual();
        }
        if (!ReferenceEquals(Cursor, want)) Cursor = want;
    }

    private void ApplyMarquee(Rect r)
    {
        _sel.Clear();
        // 音域与行高在循环外算一次（UI-03）
        var rng = PitchRange();
        double rowH = RowH(rng);
        for (int i = 0; i < _notes.Count; i++)
        {
            var n = _notes[i];
            if (n.End < _viewFrom || n.Start > _viewTo) continue;
            if (RectOf(n.Pitch, n.Start, n.End, rng, rowH).Intersects(r)) _sel.Add(i);
        }
        RaiseSelectionChanged();
    }

    private void SelectPitchAt(Point p)
    {
        int pitch = PitchAt(p.Y, PitchRange());
        if (!_pitchSelDone.Add(pitch)) return;
        for (int i = 0; i < _notes.Count; i++)
            if (_notes[i].Pitch == pitch) _sel.Add(i);
        InvalidateVisual();
        NoteAuditioned?.Invoke(pitch);
    }

    /// <summary>右键擦除：擦掉光标下的音。删除会移动下标，所以选择集要同步平移，悬停下标直接作废。</summary>
    private void EraseAt(Point p)
    {
        int hit = HitTest(p);
        if (hit < 0) return;
        var note = _notes[hit];
        if (!_eraseDone.Add(note)) return;
        _notes.RemoveAt(hit);

        var remapped = new HashSet<int>();
        foreach (int i in _sel)
        {
            if (i == hit) continue;
            remapped.Add(i > hit ? i - 1 : i);
        }
        _sel.Clear();
        foreach (int i in remapped) _sel.Add(i);

        // 悬停框记的是下标，删除后它已经指到别的音上；置 -1，等下一次指针移动时重算（UI-18）
        _hover = -1;
        EnsureBarsInvalid();
        InvalidateVisual();
    }

    private void RaiseSelectionChanged() => SelectionChanged?.Invoke();
    private void RaiseViewChanged() => ViewChanged?.Invoke();
}

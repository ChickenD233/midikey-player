namespace MidiKeyPlayer.Engine;

/// <summary>
/// 输入时序预算（全部为**物理毫秒**，与播放速度无关）。
/// 目标程序按键按帧采样（30fps=33ms/帧），修饰键与音键间隔小于一帧会被折进同一帧而漏音，
/// 因此这些间隔必须按物理帧给足，不能被速度除小。
///
/// 引擎侧的口径（A01）：事件表内部存的是**音乐时间**（秒），所以
/// PlaybackEngine.BuildSchedule 建表时把这里的物理毫秒乘上当前速度换成音乐时间
/// （音乐秒 = 物理秒 × 速度）。派发时再按速度积分，实际发出的物理间隔就等于本文件的毫秒值，
/// 10% 与 400% 两端都不变。**表里的 T 是音乐时间，本文件的数值是物理时间，两者不要混。**
/// </summary>
public sealed record InputTiming
{
    /// <summary>目标程序采样帧长估计（16.7 = 60fps；33.3 = 30fps）。</summary>
    public double FrameMs { get; init; } = 16.7;

    /// <summary>修饰键（鼠标左/右/中键）必须比音键早这么多，才能被正确采样到。</summary>
    public double ModLeadMs { get; init; } = 40.0;      // ≈ 2.5 帧 @60fps

    /// <summary>同一根音键两次按下的最小间隔（要跨过"抬起"帧，否则两音粘连）。</summary>
    public double RetriggerMs { get; init; } = 45.0;    // ≈ 3 帧 @60fps

    /// <summary>音键最短按住时长（避免按下与抬起被折进同一帧）。</summary>
    public double MinHoldMs { get; init; } = 45.0;      // ≈ 3 帧 @60fps

    /// <summary>前音抬起 → 后音按下之间的最小间隔（跨帧安全边距）。</summary>
    public double ReleaseGapMs { get; init; } = 40.0;   // ≈ 2.5 帧 @60fps

    /// <summary>提前派发预算（物理毫秒）。引擎乘速度换成音乐秒后与音乐时间比较。须 ≥ ModLeadMs + FrameMs。</summary>
    public double LeadMs { get; init; } = 57.0;         // ≥ 40 + 16.7

    /// <summary>档位中文名（界面用）。</summary>
    public string Name { get; init; } = "标准";

    /// <summary>稳健档：给 30fps、掉帧或机器负载高时留余量。</summary>
    public static InputTiming Safe => new()
    {
        Name = "稳健",
        FrameMs = 33.3,
        ModLeadMs = 70,
        RetriggerMs = 80,
        MinHoldMs = 80,
        ReleaseGapMs = 70,
        LeadMs = 104         // ≥ 70 + 33.3
    };

    /// <summary>标准档：60fps 默认，绝大多数机器适用。</summary>
    public static InputTiming Standard => new();

    /// <summary>极限档：帧率很高、要跟极快的歌时用；时间余量最小，漏音风险自负。</summary>
    public static InputTiming Aggressive => new()
    {
        Name = "极限",
        FrameMs = 8.0,
        ModLeadMs = 20,
        RetriggerMs = 22,
        MinHoldMs = 22,
        ReleaseGapMs = 18,
        LeadMs = 28          // ≥ 20 + 8
    };

    /// <summary>按界面下拉框序号取档位（0 稳健 / 1 标准 / 2 极限）。</summary>
    public static InputTiming FromIndex(int index) => index switch
    {
        0 => Safe,
        2 => Aggressive,
        _ => Standard
    };

    public static string[] Names => new[] { "稳健（30fps / 卡顿）", "标准（60fps 推荐）", "极限（高帧率）" };
}

/// <summary>
/// 输入时序诊断：统计"音键按下时修饰键已稳定多久"，用于在真机验证是否还有漏音；
/// 修饰键提前量不足一帧、同刻、同键重触发过密即为漏音的现场证据。
///
/// 口径（A13）：事件表里的时刻是**音乐时间**，而这里报出的毫秒是**物理毫秒**。
/// 所以时间差一律乘上播放速度换算回物理秒，再与物理阈值比较（速度 400% 时音乐差 ×4 = 物理差）。
/// </summary>
public sealed class InputTimingProbe
{
    private readonly object _gate = new();

    private double _lastModMusicT = double.NegativeInfinity;
    // 按音键分别记账：和弦会同时按着多根键，"最近按下的那根"不足以判断重触发与按住时长。
    private readonly Dictionary<char, double> _lastDownOfKey = new();
    private readonly Dictionary<char, double> _downAtOfKey = new();
    private int _heldMods;
    private bool _sawLead;

    /// <summary>
    /// 本轮播放速度（1.0 = 100%）。引擎在开始播放时写入，用于音乐时间 ↔ 物理时间换算。
    /// 只做诊断换算用，不加 volatile：C# 不允许 volatile double，读到旧值也不影响判定。
    /// </summary>
    public double Speed = 1.0;

    /// <summary>本轮使用的输入档位，用于按档位显示阈值（A13）。</summary>
    public InputTiming Timing { get; set; } = InputTiming.Standard;

    public int TotalNoteOn { get; private set; }
    public int ModLeadTooShort { get; private set; }    // 修饰键提前量不足一帧
    public int ModLeadZero { get; private set; }        // 修饰键与音键同刻
    public int RetriggerTooShort { get; private set; }  // 同键重触发间隔不足
    public int MinHoldTooShort { get; private set; }    // 音键按住时长不足一帧
    public double MinModLeadMs { get; private set; } = double.MaxValue;

    /// <summary>
    /// 音键时值被压到"最短按住"下限的次数（两种原因合计）。
    /// 原因见 <see cref="MinUpLimited"/> 与 <see cref="MinUpRelaxed"/>（B04）。
    /// </summary>
    public int MinUpLimited { get; private set; }

    /// <summary>
    /// 被迫压缩：本音与前音重叠，时值本来就放不下最短按住，只能压到下限。
    /// 这才是「谱面挤得比输入档位允许的还快」的信号，界面上的 <c>SqueezedNotes</c> 用它。
    /// </summary>
    public int CompressedNotes { get; private set; }

    /// <summary>正常让位：前音重叠把时值缩短，但没有压到最短按住下限。属正常现象，不是异常。</summary>
    public int MinUpRelaxed { get; private set; }

    /// <summary>调度阶段报告：时值被压到下限（正常让位），见 <see cref="MinUpRelaxed"/>。</summary>
    public void OnMinUpLimited() { MinUpLimited++; MinUpRelaxed++; }

    /// <summary>调度阶段报告：时值被压到下限（被迫压缩），见 <see cref="CompressedNotes"/>。</summary>
    public void OnMinUpForced() { MinUpLimited++; CompressedNotes++; }

    /// <summary>修饰键状态变化（按音乐时间记录）。</summary>
    public void OnModifier(double musicT, bool down)
    {
        lock (_gate)
        {
            // 提前量基准只记「按下」：松开也更新的话，紧跟在修饰键松开后的音会算出 ≈0 的
            // 提前量 → 误报 ModLeadTooShort。是否仍有修饰键按着由 _heldMods 把关。
            if (down) _lastModMusicT = musicT;
            _heldMods += down ? 1 : -1;
            if (_heldMods < 0) _heldMods = 0;
        }
    }

    /// <summary>音键按下（按音乐时间记录）。和弦里每根键各记一份。</summary>
    public void OnNoteOn(char key, double musicT)
    {
        lock (_gate)
        {
            TotalNoteOn++;
            // 事件表存音乐时间 → 物理时间 = 音乐时间 × 速度（A13）
            double phys = Speed <= 0 ? 1.0 : Speed;
            if (_lastDownOfKey.TryGetValue(key, out double prevDown))
            {
                double gapMs = (musicT - prevDown) * phys * 1000.0;
                if (gapMs < Timing.RetriggerMs) RetriggerTooShort++;
            }
            // 只有此刻真有修饰键按着才评估提前量：修饰键已松开后来的音不做这项检查
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

    /// <summary>音键抬起，用于统计按住时长（按这根键自己的按下时刻算）。</summary>
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

    /// <summary>跳转/换谱后重置（此时会强制释放全部修饰键）。</summary>
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

using System.ComponentModel;
using System.Runtime.CompilerServices;
using MidiKeyPlayer.Midi;

namespace MidiKeyPlayer;

public sealed class TrackRowVM : INotifyPropertyChanged
{
    private bool _isMain;
    private bool _isMix;
    private int _mixRank;
    private int _voiceIndex = -1;
    private Avalonia.Media.IBrush? _voiceBrush;
    private bool _voiceActive;
    private Avalonia.Media.IBrush? _roleBrush;
    private bool _roleActive;

    public TrackRowVM(MidiCandidate candidate)
    {
        Candidate = candidate;
    }

    public MidiCandidate Candidate { get; }

    public bool IsMain
    {
        get => _isMain;
        set
        {
            if (_isMain == value) return;
            _isMain = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowStar));
        }
    }

    /// <summary>勾选进“多声部合奏”。</summary>
    public bool IsMix
    {
        get => _isMix;
        set
        {
            if (_isMix == value) return;
            _isMix = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowStar));
        }
    }

    /// <summary>参与合奏时显示优先级编号，不再显示主旋律圆点。</summary>
    public bool ShowStar => IsMain && !IsMix;

    /// <summary>合奏优先级：1 最优先，0=未参与合奏。</summary>
    public int MixRank
    {
        get => _mixRank;
        set
        {
            if (_mixRank == value) return;
            _mixRank = value;
            OnPropertyChanged();
        }
    }

    public string TrackLabel => Candidate.TrackLabel;
    public string ChannelLabel => Candidate.ChannelLabel;
    public string Name => Candidate.Name;
    public int NoteCount => Candidate.NoteCount;
    public string RangeLabel => Candidate.RangeLabel;

    /// <summary>名字列的悬浮提示：文件里的原始轨名 + 时长 + 音符数，方便核对识别结果。</summary>
    public string NameTip =>
        Candidate.TrackName.Length > 0 && Candidate.TrackName != Candidate.Name
            ? $"{Candidate.Name}（文件里的轨名：{Candidate.TrackName}）"
            : Candidate.Name;

    /// <summary>时长显示：不足 1 分钟显示秒，否则 分:秒。</summary>
    public string DurationText
    {
        get
        {
            double d = Candidate.DurationSec;
            if (d < 1) return "—";
            if (d < 60) return $"{d:F0}秒";
            int m = (int)(d / 60);
            int s = (int)d % 60;
            return $"{m}分{s:00}秒";
        }
    }

    /// <summary>打击乐等不适合作为乐器主旋律的轨道。</summary>
    public bool IsPercussion =>
        Candidate.Role == TrackRole.Drums ||
        Candidate.Channel == 9 ||
        Candidate.Name.Contains("打击", StringComparison.OrdinalIgnoreCase) ||
        Candidate.Name.Contains("鼓", StringComparison.OrdinalIgnoreCase) ||
        Candidate.Name.Contains("drum", StringComparison.OrdinalIgnoreCase) ||
        Candidate.Name.Contains("percussion", StringComparison.OrdinalIgnoreCase);

    // ================= 声部识别 =================

    /// <summary>声部标牌的字：鼓 / 贝斯 / 电吉他 / 和声 / 人声…（识别结果，见 GmInstrument）。</summary>
    public string RoleTag => Candidate.RoleTag;

    /// <summary>
    /// 标牌取的色（声部角色配色，由 MainWindow 按 RoleTone 取主题资源写入）。
    /// 未识别时为 null，文字退回默认色。
    /// </summary>
    public Avalonia.Media.IBrush? RoleBrush
    {
        get => _roleBrush;
        set
        {
            if (ReferenceEquals(_roleBrush, value)) return;
            _roleBrush = value;
            OnPropertyChanged();
        }
    }

    /// <summary>这个声部有没有参与本次演奏。参与时标牌用实色，没参与时整块弱化。</summary>
    public bool RoleActive
    {
        get => _roleActive;
        set
        {
            if (_roleActive == value) return;
            _roleActive = value;
            OnPropertyChanged();
        }
    }

    /// <summary>声部标牌的悬浮说明：识别出什么、证据是什么、是文件里写的还是推断的。</summary>
    public string RoleTip
    {
        get
        {
            string tag = Candidate.RoleTag;
            if (Candidate.Role == TrackRole.Unknown)
                return "这条轨没有 GM 音色信息，也没有说得清的轨名，看不出是什么声部。";
            var sb = new System.Text.StringBuilder();
            sb.Append("识别为「").Append(tag).Append('」');
            sb.Append(Candidate.RoleGuessed ? "（按音域与节奏推断，不是文件里写的）" : "（来自 MIDI 文件的信息）");
            if (Candidate.Program >= 0)
                sb.Append('\n').Append("GM 音色 ").Append(Candidate.Program + 1)
                  .Append("：").Append(GmInstrument.ProgramName(Candidate.Program));
            else if (Candidate.Channel == 9)
                sb.Append('\n').Append("通道 10：MIDI 规范里的打击乐通道");
            if (Candidate.TrackName.Length > 0)
                sb.Append('\n').Append("文件里的轨名：").Append(Candidate.TrackName);
            return sb.ToString();
        }
    }

    /// <summary>
    /// 能否选作主旋律。打击乐轨也可以选：有的目标乐器自带鼓组，
    /// 这种时候鼓点就该被当成正常声部发出去。
    /// </summary>
    public bool IsPlayable => true;

    // ================= 声轨配色 =================

    /// <summary>
    /// 声轨颜色号：参与合奏的轨 = 勾选顺序（0 起），未参与合奏时主旋律轨 = 0，其余 = -1。
    /// 卷帘按同一个号上色，所以左侧文字与卷帘音符一一对应。
    /// </summary>
    public int VoiceIndex
    {
        get => _voiceIndex;
        set
        {
            if (_voiceIndex == value) return;
            _voiceIndex = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 名称列的文字颜色。色块来自 Styles\Theme.axaml 的 BrushVoice0..11，
    /// 由 MainWindow 按 VoiceIndex 取资源后写入；未参与合奏与打击乐轨用弱化色。
    /// </summary>
    public Avalonia.Media.IBrush? VoiceBrush
    {
        get => _voiceBrush;
        set
        {
            if (ReferenceEquals(_voiceBrush, value)) return;
            _voiceBrush = value;
            OnPropertyChanged();
        }
    }

    /// <summary>本轨当前是否参与演奏（决定卷帘与列表用亮色还是弱化色）。</summary>
    public bool IsVoiceActive
    {
        get => _voiceActive;
        set
        {
            if (_voiceActive == value) return;
            _voiceActive = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 「这条轨参与演奏吗」→ 声部标牌的不透明度：参与 1.0，没参与 0.45（整块变淡但还看得清）。
/// 只用在 MainWindow.axaml 的声部标牌上。
/// </summary>
public sealed class ActiveOpacityConverter : Avalonia.Data.Converters.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is true ? 1.0 : 0.45;

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;
}

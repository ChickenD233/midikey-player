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
        Candidate.Channel == 9 ||
        Candidate.Name.Contains("打击", StringComparison.OrdinalIgnoreCase) ||
        Candidate.Name.Contains("鼓", StringComparison.OrdinalIgnoreCase) ||
        Candidate.Name.Contains("drum", StringComparison.OrdinalIgnoreCase) ||
        Candidate.Name.Contains("percussion", StringComparison.OrdinalIgnoreCase);

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

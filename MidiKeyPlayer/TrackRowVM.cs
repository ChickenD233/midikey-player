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
    private Avalonia.Media.IBrush? _roleTextBrush;
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

    public bool ShowStar => IsMain && !IsMix;

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

    public string NameTip =>
        Candidate.TrackName.Length > 0 && Candidate.TrackName != Candidate.Name
            ? $"{Candidate.Name}（文件里的轨名：{Candidate.TrackName}）"
            : Candidate.Name;

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

    public bool IsPercussion =>
        Candidate.Role == TrackRole.Drums ||
        Candidate.Channel == 9 ||
        Candidate.Name.Contains("打击", StringComparison.OrdinalIgnoreCase) ||
        Candidate.Name.Contains("鼓", StringComparison.OrdinalIgnoreCase) ||
        Candidate.Name.Contains("drum", StringComparison.OrdinalIgnoreCase) ||
        Candidate.Name.Contains("percussion", StringComparison.OrdinalIgnoreCase);

    public string RoleTag => Candidate.RoleTag;

    public Avalonia.Media.IBrush? RoleBrush
    {
        get => _roleBrush;
        set
        {
            if (ReferenceEquals(_roleBrush, value)) return;
            _roleBrush = value;
            _roleTextBrush = TextOn(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(RoleTextBrush));
        }
    }

    public Avalonia.Media.IBrush? RoleTextBrush => _roleTextBrush;

    private static readonly Avalonia.Media.IBrush RoleInk =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0x12, 0x16, 0x1B));

    private static Avalonia.Media.IBrush? TextOn(Avalonia.Media.IBrush? background)
    {
        if (background is not Avalonia.Media.ISolidColorBrush s) return null;
        double bg = Luminance(s.Color);
        double lWhite = 1.05 / (bg + 0.05);
        double ink = Luminance(Avalonia.Media.Color.FromRgb(0x12, 0x16, 0x1B));
        double lInk = (bg + 0.05) / (ink + 0.05);
        return lWhite >= lInk ? Avalonia.Media.Brushes.White : RoleInk;
    }

    private static double Luminance(Avalonia.Media.Color c)
    {
        static double Channel(byte v)
        {
            double x = v / 255.0;
            return x <= 0.03928 ? x / 12.92 : System.Math.Pow((x + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

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

    public bool IsPlayable => true;

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

public sealed class ActiveOpacityConverter : Avalonia.Data.Converters.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is true ? 1.0 : 0.45;

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;
}

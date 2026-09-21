namespace MidiKeyPlayer.Midi;

/// <summary>MIDI 文件解析后的一个音符（已换算成秒）。</summary>
public sealed class RawNote
{
    public int Pitch { get; init; }        // MIDI 音高 0..127, 60 = C4
    public double Start { get; init; }     // 起始秒
    public double End { get; init; }       // 结束秒
    public int Velocity { get; init; }
    /// <summary>MIDI 通道 0..15（9 = 通道 10 = 打击乐）；-1 = 未知。单音提取用它排除打击乐。</summary>
    public int Channel { get; init; } = -1;

    /// <summary>
    /// 这个音属于哪条声轨（合奏时的声部序号，0 起）；-1 = 未标注。
    /// 让音符自己携带归属，卷帘才能按声轨上色，而不是按音高猜。
    /// </summary>
    public int Voice { get; init; } = -1;

    public override string ToString() => $"{Music.NoteName(Pitch)} {Start:F2}s~{End:F2}s";
}

/// <summary>一个可被选作主旋律的候选 = (轨道, 声道)。</summary>
public sealed class MidiCandidate
{
    public int TrackIndex { get; init; }   // 0 起
    public int Channel { get; init; }      // 0 起（MIDI 内部声道编号）
    public string Name { get; init; } = "";
    public List<RawNote> Notes { get; init; } = new();
    public double DurationSec { get; set; }

    public int NoteCount => Notes.Count;
    public int MinPitch => Notes.Count == 0 ? 0 : Notes.Min(n => n.Pitch);
    public int MaxPitch => Notes.Count == 0 ? 0 : Notes.Max(n => n.Pitch);

    /// <summary>MIDI 文件里的原始轨名（MTrk 的 SequenceTrackName），没写就是空串。</summary>
    public string TrackName { get; init; } = "";

    /// <summary>这条轨用的 GM 音色号 0..127；-1 = 文件里没有 Program Change。</summary>
    public int Program { get; init; } = -1;

    /// <summary>识别出的声部角色。Program 或轨名给的一手证据优先，都没有才是推断。</summary>
    public TrackRole Role { get; init; } = TrackRole.Unknown;

    /// <summary>Role 是推断出来的（文件里既没音色号也没说得清的轨名）。界面会写「疑似」。</summary>
    public bool RoleGuessed { get; init; }

    /// <summary>声部标牌的字，例：鼓 / 贝斯 / 电吉他 / 和声 / 人声。</summary>
    public string RoleTag => GmInstrument.TagOf(Role);

    public string ChannelLabel => Channel == 9 ? $"{Channel + 1}(鼓)" : (Channel + 1).ToString();
    /// <summary>列表里的音域：简谱范围，例如 5.~2。不留空格，列表里省地方。</summary>
    public string RangeLabel => Notes.Count == 0 ? "-"
        : Music.SolfegeRange(MinPitch, MaxPitch);
    public string TrackLabel => (TrackIndex + 1).ToString();
}

/// <summary>解析后的整份 MIDI 文件。</summary>
public sealed class ParsedMidi
{
    public string FilePath { get; init; } = "";
    public string DivisionLabel { get; init; } = "";
    public double DurationSec { get; init; }
    /// <summary>拍长（秒），取文件第一处速度；卷帘标尺用它画小节线。</summary>
    public double SecondsPerBeat { get; init; } = 0.5;
    /// <summary>每小节拍数，取文件第一处拍号。</summary>
    public int BeatsPerBar { get; init; } = 4;
    public List<MidiCandidate> Candidates { get; init; } = new();
}

/// <summary>MIDI 音高相关的命名工具。</summary>
public static class Music
{
    private static readonly string[] Names =
        { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    private static readonly string[] JianPu =
        { "1", "#1", "2", "#2", "3", "4", "#4", "5", "#5", "6", "#6", "7" };

    /// <summary>标准音名，如 C4 / F#5。</summary>
    public static string NoteName(int pitch) => $"{Names[Mod(pitch, 12)]}{pitch / 12 - 1}";

    /// <summary>简谱记号（含升降号），音高模 12。不带八度点，内部用途保留。</summary>
    public static string DegreeName(int pitch) => JianPu[Mod(pitch, 12)];

    // 八度点用「间距」字符，不用组合字符：界面的字体（Microsoft YaHei UI 等）没有
    // U+0307 / U+0323 的字形，组合点会渲染成豆腐块。U+02D9 是上点，点号是下点，两者都必定有字形。
    private const string DotUp = "\u02D9";     // ˙ 上点
    private const string DotDown = ".";        // . 下点

    /// <summary>
    /// 面向用户的简谱音高：数字 1..7 加升降号，八度用数字后面的点表示，上点是高八度，下点是低八度。
    /// 基准是 60（中音 do = 1，不带点）；往上每十二个半音加一个上点，往下加下点，最多两个。
    /// 例如 1 / 1˙ / 1˙˙ / 1. / #4。
    /// </summary>
    public static string SolfegeName(int pitch)
    {
        int octave = (int)Math.Floor((pitch - 60) / 12.0);
        if (octave == 0) return DegreeName(pitch);
        string mark = octave > 0 ? DotUp : DotDown;
        int count = Math.Min(Math.Abs(octave), 2);   // 最多两个点：低音 / 高音 / 高高音
        var sb = new System.Text.StringBuilder(DegreeName(pitch), DegreeName(pitch).Length + count);
        for (int i = 0; i < count; i++) sb.Append(mark);
        return sb.ToString();
    }

    /// <summary>简谱音域：低到高，例如 5.~2。传反了自动换过来。不留空格，列表里省地方。</summary>
    public static string SolfegeRange(int lo, int hi)
    {
        if (lo > hi) (lo, hi) = (hi, lo);
        return $"{SolfegeName(lo)}~{SolfegeName(hi)}";
    }

    /// <summary>0..127 全部简谱名，配表用。下标就是音高，省得每次现算。</summary>
    public static readonly string[] SolfegeNames = BuildSolfegeNames();

    private static string[] BuildSolfegeNames()
    {
        var list = new string[128];
        for (int p = 0; p < 128; p++) list[p] = SolfegeName(p);
        return list;
    }

    public static int Mod(int a, int b) => ((a % b) + b) % b;
}

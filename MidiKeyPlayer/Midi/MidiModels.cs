namespace MidiKeyPlayer.Midi;

public sealed class RawNote
{
    public int Pitch { get; init; }        
    public double Start { get; init; }     
    public double End { get; init; }       
    public int Velocity { get; init; }
    public int Channel { get; init; } = -1;

    public int Voice { get; init; } = -1;

    public override string ToString() => $"{Music.NoteName(Pitch)} {Start:F2}s~{End:F2}s";
}

public sealed class MidiCandidate
{
    public int TrackIndex { get; init; }   
    public int Channel { get; init; }      
    public string Name { get; init; } = "";
    public List<RawNote> Notes { get; init; } = new();
    public double DurationSec { get; set; }

    public int NoteCount => Notes.Count;
    public int MinPitch => Notes.Count == 0 ? 0 : Notes.Min(n => n.Pitch);
    public int MaxPitch => Notes.Count == 0 ? 0 : Notes.Max(n => n.Pitch);

    public string TrackName { get; init; } = "";

    public int Program { get; init; } = -1;

    public TrackRole Role { get; init; } = TrackRole.Unknown;

    public bool RoleGuessed { get; init; }

    public string RoleTag => GmInstrument.TagOf(Role);

    public string ChannelLabel => Channel == 9 ? $"{Channel + 1}(鼓)" : (Channel + 1).ToString();
    public string RangeLabel => Notes.Count == 0 ? "-"
        : Music.SolfegeRange(MinPitch, MaxPitch);
    public string TrackLabel => (TrackIndex + 1).ToString();
}

public sealed class ParsedMidi
{
    public string FilePath { get; init; } = "";
    public string DivisionLabel { get; init; } = "";
    public double DurationSec { get; init; }
    public double SecondsPerBeat { get; init; } = 0.5;
    public int BeatsPerBar { get; init; } = 4;
    public List<MidiCandidate> Candidates { get; init; } = new();
}

public static class Music
{
    private static readonly string[] Names =
        { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    private static readonly string[] JianPu =
        { "1", "#1", "2", "#2", "3", "4", "#4", "5", "#5", "6", "#6", "7" };

    public static string NoteName(int pitch) => $"{Names[Mod(pitch, 12)]}{pitch / 12 - 1}";

    public static string DegreeName(int pitch) => JianPu[Mod(pitch, 12)];

    private const string DotUp = "\u02D9";     
    private const string DotDown = ".";        

    public static string SolfegeName(int pitch)
    {
        int octave = (int)Math.Floor((pitch - 60) / 12.0);
        if (octave == 0) return DegreeName(pitch);
        string mark = octave > 0 ? DotUp : DotDown;
        int count = Math.Min(Math.Abs(octave), 2);   
        var sb = new System.Text.StringBuilder(DegreeName(pitch), DegreeName(pitch).Length + count);
        for (int i = 0; i < count; i++) sb.Append(mark);
        return sb.ToString();
    }

    public static string SolfegeRange(int lo, int hi)
    {
        if (lo > hi) (lo, hi) = (hi, lo);
        return $"{SolfegeName(lo)}~{SolfegeName(hi)}";
    }

    public static readonly string[] SolfegeNames = BuildSolfegeNames();

    private static string[] BuildSolfegeNames()
    {
        var list = new string[128];
        for (int p = 0; p < 128; p++) list[p] = SolfegeName(p);
        return list;
    }

    public static int Mod(int a, int b) => ((a % b) + b) % b;
}

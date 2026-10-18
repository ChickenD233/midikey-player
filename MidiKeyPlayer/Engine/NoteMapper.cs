using MidiKeyPlayer.Midi;

namespace MidiKeyPlayer.Engine;

public sealed class MappedNote
{
    public int Pitch { get; init; }

    public double Start { get; init; }   
    public double End { get; init; }

    public char Key { get; init; }

    public string KeyName { get; init; } = "";

    public bool Sharp { get; init; }

    public bool Flat { get; init; }

    public int OctaveOffset { get; init; }

    public int SoundingPitch { get; init; }

    public int FoldedSemitones { get; init; }

    public int Voice { get; init; } = -1;

    public bool InRange { get; init; }

    public bool SameKeyBlocked { get; set; }

    public string SkipReason { get; init; } = "";

    public bool Shifted => InRange && SoundingPitch != Pitch;
}

public sealed class MappingResult
{
    public int BaseOctave { get; set; }

    public bool FoldedOctave { get; set; }

    public List<MappedNote> Notes { get; init; } = new();

    public int InRangeCount => Notes.Count(n => n.InRange);

    public int SkipCount => Notes.Count(n => !n.InRange);

    public int ShiftedCount => Notes.Count(n => n.Shifted);
}

public static class NoteMapper
{
    public static char KeyOfPitch(int pitch)
        => KeymapProfile.Current.TryKeyOfPitch(pitch, out string key, out _, out _)
            ? KeymapProfile.KeyCharOf(key)
            : ' ';

    public static bool IsSharpPitch(int pitch)
        => KeymapProfile.Current.TryKeyOfPitch(pitch, out _, out _, out bool sharp) && sharp;

    public static bool IsReachable(int pitch, int baseOctave)
    {
        var profile = KeymapProfile.Current;
        int shifted = pitch - 12 * (baseOctave - profile.BaseOctave);
        if (!profile.InRange(shifted)) return false;
        return profile.TryKeyOfPitch(shifted, out _, out _, out _, out _);
    }

    public static int AutoBaseOctave(IReadOnlyList<int> pitches)
    {
        if (pitches.Count == 0) return KeymapProfile.Current.BaseOctave;

        int minO = int.MaxValue, maxO = int.MinValue;
        double sumO = 0;
        foreach (var p in pitches)
        {
            int o = p / 12 - 1;
            if (o < minO) minO = o;
            if (o > maxO) maxO = o;
            sumO += o;
        }
        double meanO = sumO / pitches.Count;

        int bestB = minO;
        int bestPlay = -1;
        for (int b = minO; b <= maxO; b++)
        {
            int play = pitches.Count(p => IsReachable(p, b));
            if (play > bestPlay ||
                (play == bestPlay && Math.Abs(b - meanO) < Math.Abs(bestB - meanO)))
            {
                bestPlay = play;
                bestB = b;
            }
        }
        return bestB;
    }

    public static MappingResult Map(IReadOnlyList<RawNote> notes, int transpose, int? manualBaseOctave,
                                    bool foldOctave = false)
    {
        var profile = KeymapProfile.Current;
        var result = new MappingResult();
        result.FoldedOctave = foldOctave;

        var valid = new List<int>();
        foreach (var n in notes)
        {
            int p = n.Pitch + transpose;
            if (p >= 0 && p <= 127) valid.Add(p);
        }

        int baseOctave = manualBaseOctave ?? AutoBaseOctave(valid);
        result.BaseOctave = baseOctave;

        int shift = 12 * (baseOctave - profile.BaseOctave);
        int lo = profile.ResolveMinNote();
        int hi = profile.ResolveMaxNote();
        bool empty = profile.Keys.Count == 0;

        foreach (var n in notes)
        {
            int p = n.Pitch + transpose;
            if (p < 0 || p > 127)
            {
                result.Notes.Add(Skipped(p, n, "移调后超出 MIDI 音域"));
                continue;
            }

            if (empty)
            {
                result.Notes.Add(Skipped(p, n, "键位方案的键表是空的"));
                continue;
            }

            int want = p - shift;
            int folded = 0;
            if (!profile.InRange(want))
            {
                if (foldOctave)
                {
                    if (TryFoldIntoRange(profile, want, out int foldedWant))
                    {
                        folded = foldedWant - want;
                        want = foldedWant;
                    }
                    else
                    {
                        result.Notes.Add(Skipped(p, n,
                            $"超出能弹范围（{Music.SolfegeRange(lo, hi)}），折八度后仍然没有对应的键"));
                        continue;
                    }
                }
                else
                {
                    result.Notes.Add(Skipped(p, n,
                        $"超出能弹范围（{Music.SolfegeRange(lo, hi)}），没有对应的键"));
                    continue;
                }
            }

            if (!profile.TryKeyOfPitch(want, out string keyName, out int octaveOffset,
                                       out bool sharp, out bool flat, out _))
            {
                result.Notes.Add(Skipped(p, n, "键表里没有这个音（没有对应键的音直接跳过）"));
                continue;
            }

            result.Notes.Add(new MappedNote
            {
                Pitch = p,
                Start = n.Start,
                End = n.End,
                Key = KeymapProfile.KeyCharOf(keyName),
                KeyName = keyName,
                Sharp = sharp,
                Flat = flat,
                OctaveOffset = octaveOffset,
                SoundingPitch = p + folded,
                FoldedSemitones = folded,
                Voice = n.Voice,
                InRange = true,
            });
        }

        MarkKeyCollisions(result.Notes);
        return result;
    }

    internal static bool TryFoldIntoRange(KeymapProfile profile, int pitch, out int folded)
    {
        folded = pitch;
        for (int octaves = 1; octaves <= 4; octaves++)
        {
            foreach (int dir in new[] { 1, -1 })
            {
                int candidate = pitch + 12 * octaves * dir;
                if (candidate < 0 || candidate > 127) continue;
                if (!profile.InRange(candidate)) continue;
                if (!profile.TryKeyOfPitch(candidate, out _, out _, out _, out _, out _)) continue;
                folded = candidate;
                return true;
            }
        }
        return false;
    }

    private static MappedNote Skipped(int pitch, RawNote n, string reason) => new()
    {
        Pitch = pitch,
        Start = n.Start,
        End = n.End,
        Key = ' ',
        KeyName = "",
        Sharp = false,
        OctaveOffset = 0,
        SoundingPitch = pitch,
        InRange = false,
        SkipReason = reason,
    };

    public static List<RawNote> MergeVoicesByPriority(IEnumerable<(int Rank, RawNote Note)> voices)
    {
        var ordered = voices
            .OrderBy(v => v.Note.Start)
            .ThenBy(v => v.Rank)
            .ThenBy(v => v.Note.Pitch)
            .ToList();
        if (ordered.Count == 0) return new List<RawNote>();

        const double eps = 0.025;

        var result = new List<RawNote>(ordered.Count);
        var seen = new HashSet<(int Pitch, long StartTick)>();
        var heldByVoice = new Dictionary<(int Voice, int Pitch), double>();

        foreach (var (rank, note) in ordered)
        {
            long tick = (long)Math.Round(note.Start / eps);

            if (!seen.Add((note.Pitch, tick))) continue;

            if (heldByVoice.TryGetValue((rank, note.Pitch), out double until) && note.Start < until - 1e-9)
                continue;

            var copy = WithVoice(note, rank);
            result.Add(copy);
            heldByVoice[(rank, note.Pitch)] = copy.End;
        }

        return result.OrderBy(n => n.Start).ThenBy(n => n.Voice).ToList();
    }

    internal static void MarkKeyCollisions(List<MappedNote> notes)
    {
        var usedUntil = new Dictionary<char, (double Until, int Voice)>();

        foreach (var n in notes.OrderBy(x => x.Start).ThenBy(x => x.Voice))
        {
            bool blocked = false;
            if (usedUntil.TryGetValue(n.Key, out var held) && n.Start < held.Until - 1e-9)
                blocked = n.Voice >= held.Voice;   

            if (blocked)
            {
                n.SameKeyBlocked = true;
                continue;
            }

            double until = Math.Max(n.End, n.Start);
            int voice = usedUntil.TryGetValue(n.Key, out var old) ? Math.Min(old.Voice, n.Voice) : n.Voice;
            usedUntil[n.Key] = (until, voice);
        }
    }

    public static List<MappedNote> Playable(IEnumerable<MappedNote> notes)
        => notes.Where(n => n.InRange && !n.SameKeyBlocked).ToList();

    private static RawNote WithVoice(RawNote n, int voice) => new()
    {
        Pitch = n.Pitch,
        Start = n.Start,
        End = n.End,
        Velocity = n.Velocity,
        Channel = n.Channel,
        Voice = voice,
    };

    public static List<RawNote> TrimLeadingSilence(IReadOnlyList<RawNote> notes)
    {
        if (notes.Count == 0) return new List<RawNote>();
        double first = notes.Min(n => n.Start);
        if (first <= 0.001) return notes.ToList();

        return notes.Select(n => new RawNote
        {
            Pitch = n.Pitch,
            Start = Math.Max(0, n.Start - first),
            End = Math.Max(0, n.End - first),
            Velocity = n.Velocity,
            Channel = n.Channel,
            Voice = n.Voice        
        }).ToList();
    }

    public static string Describe(MappedNote n, bool withTime)
    {
        if (!n.InRange) return (withTime ? $"{n.Start:F2}s " : "") + "空拍（不发声）";

        var profile = KeymapProfile.Current;
        string slot = n.OctaveOffset switch
        {
            < 0 => OctaveLabel(profile.OctaveDown, "低八度"),
            > 0 => OctaveLabel(profile.OctaveUp, "高八度"),
            _ => "基准八度"
        };
        string keyShow = string.IsNullOrEmpty(n.KeyName) ? "（无按键）"
            : (n.KeyName == "," ? "，" : n.KeyName);
        string sharpName = string.IsNullOrWhiteSpace(profile.Sharp) || !profile.ModifiersEnabled
            ? "升半音键" : profile.Sharp!;
        string sharp = n.Sharp ? $"{sharpName}+升半音 " : "";
        string flatName = string.IsNullOrWhiteSpace(profile.Flat) || !profile.ModifiersEnabled
            ? "降半音键" : profile.Flat!;
        string flat = n.Flat ? $"{flatName}+降半音 " : "";
        string fold = n.Shifted ? $"（改弹 {Music.SolfegeName(n.SoundingPitch)}）" : "";
        string time = withTime ? $"{n.Start:F2}s " : "";
        return $"{time}{Music.SolfegeName(n.Pitch)}{fold} → 按[{keyShow}] {sharp}{flat}{slot}";
    }

    private static string OctaveLabel(string? key, string fallback)
        => string.IsNullOrWhiteSpace(key) ? fallback : $"{fallback}(按{key})";
}

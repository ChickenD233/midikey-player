using MidiKeyPlayer.Midi;

namespace MidiKeyPlayer.Engine;

/// <summary>映射后的单个可演奏音符。</summary>
public sealed class MappedNote
{
    /// <summary>移调后的原音高（界面与卷帘按它显示）。</summary>
    public int Pitch { get; init; }

    public double Start { get; init; }   // 秒（未乘速度）
    public double End { get; init; }

    public char Key { get; init; }

    /// <summary>方案里写的键名（"Z" / "," / "PageUp" / "MouseLeft"）。</summary>
    public string KeyName { get; init; } = "";

    /// <summary>true → 需要按住升半音键。功能键关掉或键表里没有升半音键时恒为 false。</summary>
    public bool Sharp { get; init; }

    /// <summary>true → 需要按住降半音键（与 <see cref="Sharp"/> 互斥；只有方案绑了降半音键时才可能出现）。</summary>
    public bool Flat { get; init; }

    /// <summary>八度档位：相对方案基准音所在八度的偏移（-1 / 0 / +1）。</summary>
    public int OctaveOffset { get; init; }

    public int SoundingPitch { get; init; }

    public int FoldedSemitones { get; init; }

    public int Voice { get; init; } = -1;

    public bool InRange { get; init; }

    public bool SameKeyBlocked { get; set; }

    public string SkipReason { get; init; } = "";

    /// <summary>发声了，但音高被挪过。只有折八度这一条路径会造成它。</summary>
    public bool Shifted => InRange && SoundingPitch != Pitch;
}

/// <summary>一次映射的结果。</summary>
public sealed class MappingResult
{
    /// <summary>基准八度（MIDI 编号，C4 = 第 4 八度）。</summary>
    public int BaseOctave { get; set; }

    /// <summary>本次映射有没有开「超范围的音折八度」。</summary>
    public bool FoldedOctave { get; set; }

    public List<MappedNote> Notes { get; init; } = new();

    /// <summary>有对应的键、演奏时会发声的音数。</summary>
    public int InRangeCount => Notes.Count(n => n.InRange);

    /// <summary>没有对应的键（或超出能弹范围）而跳过的音数。卷帘画成灰色的就是它们。</summary>
    public int SkipCount => Notes.Count(n => !n.InRange);

    /// <summary>发声但音高被折八度挪过的音数。</summary>
    public int ShiftedCount => Notes.Count(n => n.Shifted);
}

public static class NoteMapper
{
    public static char KeyOfPitch(int pitch)
        => KeymapProfile.Current.TryKeyOfPitch(pitch, out string key, out _, out _)
            ? KeymapProfile.KeyCharOf(key)
            : ' ';

    /// <summary>该音高是否需要按住升半音键（旧接口保留：内部走当前方案）。</summary>
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

        // baseOctave 相对方案基准八度整体移动多少个八度；音域与键表都跟着移动。
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
                    // 折八度：把音上下挪整八度，落在能弹范围里最近的那个位置。
                    // 找不到（例如手碟上没有 #4 与 7）就照旧不弹。
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
                // 折过八度的音：发声音高要跟着挪，否则试听与卷帘显示都会与手上按的对不上
                SoundingPitch = p + folded,
                FoldedSemitones = folded,
                Voice = n.Voice,
                InRange = true,
            });
        }

        // 收尾：标出"同一个键同一时刻撞车"的音。只标记、不删除，理由见 MarkKeyCollisions。
        MarkKeyCollisions(result.Notes);
        return result;
    }

    internal static bool TryFoldIntoRange(KeymapProfile profile, int pitch, out int folded)
    {
        folded = pitch;
        // ±4 个八度足够覆盖任何现实音域；再远就没有音乐意义了。
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

        // 起音在同一帧内就算"同一刻"。25 毫秒约等于一帧半，够容下文件里的微小抖动。
        const double eps = 0.025;

        var result = new List<RawNote>(ordered.Count);
        // 已经保留过的"真正同一个音"：键是（音高，起音刻度）。用来去重。
        var seen = new HashSet<(int Pitch, long StartTick)>();
        // 声部序号 → 已经在响的同一个音高到几点。同声部同音高不重复起音（那是同一根键）。
        var heldByVoice = new Dictionary<(int Voice, int Pitch), double>();

        foreach (var (rank, note) in ordered)
        {
            long tick = (long)Math.Round(note.Start / eps);

            // 1) 跨声部的真正同一个音：只留一个，别的扔掉。
            //    第一次保留时用它自己的 Rank 与时长。
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
        // 键 → 这个键已经被占到几点，以及占用它的声部序号
        var usedUntil = new Dictionary<char, (double Until, int Voice)>();

        foreach (var n in notes.OrderBy(x => x.Start).ThenBy(x => x.Voice))
        {
            bool blocked = false;
            if (usedUntil.TryGetValue(n.Key, out var held) && n.Start < held.Until - 1e-9)
                blocked = n.Voice >= held.Voice;   // 序号更靠后的让位；同序号也先到先得

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

    /// <summary>复制一个音并写上声部序号。RawNote.Voice 是 init，只能复制，不能就地改。</summary>
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
            Voice = n.Voice        // 整体平移不改归属，声轨号必须一起带过去
        }).ToList();
    }

    /// <summary>给界面用的单音描述。</summary>
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
        // 只显示方案里的键名：内部的哨兵字符绝不给人看
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

    /// <summary>八度档位的中文名，给跳过原因与音高描述用。</summary>
    private static string OctaveLabel(string? key, string fallback)
        => string.IsNullOrWhiteSpace(key) ? fallback : $"{fallback}(按{key})";
}

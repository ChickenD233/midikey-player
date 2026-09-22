using MidiKeyPlayer.Midi;

namespace MidiKeyPlayer.Engine;

/// <summary>映射后的单个可演奏音符。</summary>
public sealed class MappedNote
{
    /// <summary>移调后的原音高（界面与卷帘按它显示）。</summary>
    public int Pitch { get; init; }

    public double Start { get; init; }   // 秒（未乘速度）
    public double End { get; init; }

    /// <summary>音键的字符形式。命名键（PageUp 等）是私用区哨兵字符，见 <see cref="KeymapProfile.NameOfKeyChar"/>。</summary>
    public char Key { get; init; }

    /// <summary>方案里写的键名（"Z" / "," / "PageUp" / "MouseLeft"）。</summary>
    public string KeyName { get; init; } = "";

    /// <summary>true → 需要按住升半音键。功能键关掉或键表里没有升半音键时恒为 false。</summary>
    public bool Sharp { get; init; }

    /// <summary>true → 需要按住降半音键（与 <see cref="Sharp"/> 互斥；只有方案绑了降半音键时才可能出现）。</summary>
    public bool Flat { get; init; }

    /// <summary>八度档位：相对方案基准音所在八度的偏移（-1 / 0 / +1）。</summary>
    public int OctaveOffset { get; init; }

    /// <summary>
    /// 实际发声音高。默认等于 <see cref="Pitch"/>；
    /// 开了「超范围的音折八度」之后，超范围的音会被上下挪整八度，这里就是挪过之后的音高。
    /// </summary>
    public int SoundingPitch { get; init; }

    /// <summary>
    /// 折八度挪动了多少半音（0 = 没折，+12 = 升一个八度，-12 = 降一个八度）。
    /// 一定是 12 的整数倍。
    /// </summary>
    public int FoldedSemitones { get; init; }

    /// <summary>
    /// 这个音属于哪条声轨（合奏时的声部序号，0 起）；-1 = 未标注。
    /// 从 <see cref="RawNote.Voice"/> 原样带过来，用于「同一个键撞车时谁让位」的裁决。
    /// </summary>
    public int Voice { get; init; } = -1;

    /// <summary>
    /// false → 演奏时跳过这个音。四种原因：移调后超出 MIDI 音域 0–127、键表为空、
    /// 超出能弹范围、键表里没有这个音。后两种合起来就是「当前方案里没有对应的键」，
    /// 卷帘按它画灰色。
    /// </summary>
    public bool InRange { get; init; }

    /// <summary>
    /// true → 这个音有键，但同一时刻这个键已经被优先级更高的声部占着，按不下去。
    /// 由 <see cref="NoteMapper.MarkKeyCollisions"/> 标记；演奏与导出都该跳过它。
    /// </summary>
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

/// <summary>
/// 把主旋律 MIDI 音高映射成乐器按键。键位与音域全部读 <see cref="KeymapProfile.Current"/>：
/// 能弹范围由键位推导（见 <see cref="KeymapProfile.ReachableExtent"/>），范围外的音固定不弹；
/// 范围内的音只认精确命中 —— 有键就发、没键就不发，不按任何策略改音高。
///
/// 「没有对应的键」= 超出能弹范围，或能弹范围内键表里查不到这个音高。两者都是
/// <see cref="MappedNote.InRange"/> = false：演奏与试听都跳过，卷帘画成灰色。
/// 它不是「超出乐器音域」——音高完全可以在音域内，只是当前方案没给它配键。
/// </summary>
public static class NoteMapper
{
    /// <summary>
    /// 画面上要按的音键字符。查不到返回 ' '（空键）。
    /// 旧接口保留：内部走当前方案。
    /// </summary>
    public static char KeyOfPitch(int pitch)
        => KeymapProfile.Current.TryKeyOfPitch(pitch, out string key, out _, out _)
            ? KeymapProfile.KeyCharOf(key)
            : ' ';

    /// <summary>该音高是否需要按住升半音键（旧接口保留：内部走当前方案）。</summary>
    public static bool IsSharpPitch(int pitch)
        => KeymapProfile.Current.TryKeyOfPitch(pitch, out _, out _, out bool sharp) && sharp;

    /// <summary>
    /// 某个音高在基准八度 baseOctave 下是否可演奏：先按方案音域判断，再查键表。
    /// baseOctave 与方案基准八度（BaseNote 所在八度）的差就是整体移八度的量。
    /// </summary>
    public static bool IsReachable(int pitch, int baseOctave)
    {
        var profile = KeymapProfile.Current;
        int shifted = pitch - 12 * (baseOctave - profile.BaseOctave);
        if (!profile.InRange(shifted)) return false;
        return profile.TryKeyOfPitch(shifted, out _, out _, out _, out _);
    }

    /// <summary>
    /// 自动选基准八度：让可演奏（落在方案音域内且键表里有键）的音最多；
    /// 打平时取更靠近所有音平均八度的那个。
    /// </summary>
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

    /// <summary>
    /// 执行映射。notes 为原始音符；transpose 为整体移调半音数（-24..+24）；
    /// manualBaseOctave 为手动基准八度，null 表示自动；
    /// foldOctave 为真时，超出能弹范围的音上下折八度落回范围，而不是直接丢掉。
    /// </summary>
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

    /// <summary>
    /// 把一个超出能弹范围的音高折进范围：上下各试最多几个八度，取落到范围里、
    /// 而且**真的有键**的那一个，优先取挪动最小的。
    /// 找不到返回 false（例如手碟上没有 #4 与 7，这两个音在任何八度都没有键）。
    /// </summary>
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

    /// <summary>
    /// 多声部合奏把各条声轨的音合成一条谱面。
    ///
    /// **只在"真的是同一个音"时才合并。** 同一个音高、同一刻起音、来自不同声部 ——
    /// 这才是真正的一个音，合起来弹一次，不能弹两次。
    ///
    /// 其余一律**全部保留**：同一个声部里的和弦不丢，不同声部同时响也各弹各的。
    ///
    /// 这里曾经按"声部编号小的优先"把相撞的音压掉，那是历史遗留：
    /// 当时的目标乐器一次只能发出一个音（三角洲口琴），所以多声部必须裁成单音线。
    /// 现在面向的是全功能 MIDI 乐器，能同时按多个键，再压就是白丢音。
    /// 真正"按不下去"的情况只有一个：**两个音落到同一个键**，
    /// 那个由 <see cref="CollapseKeyCollisions"/> 在映射之后处理，因为只有映射之后才知道键。
    ///
    /// 输出的每个音都写上 <see cref="RawNote.Voice"/> = 它的声部序号（Rank），
    /// 这样卷帘才知道「这个音属于哪条声轨」，不必再按音高猜。
    /// </summary>
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

            // 2) 同一个声部里同一个音高还在响：这是同一根键的重复起音，丢掉。
            //    不同声部不受这条限制 —— 两个人弹同一个音高由第 1 条处理。
            if (heldByVoice.TryGetValue((rank, note.Pitch), out double until) && note.Start < until - 1e-9)
                continue;

            var copy = WithVoice(note, rank);
            result.Add(copy);
            heldByVoice[(rank, note.Pitch)] = copy.End;
        }

        return result.OrderBy(n => n.Start).ThenBy(n => n.Voice).ToList();
    }

    /// <summary>
    /// 标记「按不下去」的音：**同一个键、同一时刻，被两个音同时占用**。
    /// 一根键不能同时按两次，所以其中一个必须让位。
    ///
    /// 让位规则：先到先得；起音相同时声部序号小的优先。
    /// 被让位的音**不删除**，只把 <see cref="MappedNote.SameKeyBlocked"/> 置真。
    /// 为什么不删：调用方有六处（试听、演奏、导出、卷帘、统计），
    /// 在这里删就得在六处都记得删，漏一处两边计数就对不上。
    /// 标记一次，各处按同一个判据过滤，就不会分歧。
    /// </summary>
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

    /// <summary>
    /// 真正要发出去的那一批音：有键、而且没有因为撞键被判让位。
    /// 演奏、试听、导出、统计都该用这个，别各写一份 Where。
    /// </summary>
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

    /// <summary>
    /// 整体平移旋律，使首音从 0 秒开始（音间相对时值不变）；很多 MIDI 开头有几小节休止，剪掉后点播放立刻出音。
    /// 首音 ≈0s 时原样返回。
    /// </summary>
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

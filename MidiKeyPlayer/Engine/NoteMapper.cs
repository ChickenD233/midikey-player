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

    /// <summary>实际发声音高。规则固定为「有键就发、没键就不发」，命中时与 <see cref="Pitch"/> 相同。</summary>
    public int SoundingPitch { get; init; }

    /// <summary>
    /// false → 演奏时跳过这个音。四种原因：移调后超出 MIDI 音域 0–127、键表为空、
    /// 超出能弹范围、键表里没有这个音。后两种合起来就是「当前方案里没有对应的键」，
    /// 卷帘按它画灰色。
    /// </summary>
    public bool InRange { get; init; }

    public string SkipReason { get; init; } = "";

    /// <summary>发声了，但音高被挪过。现在只有「原样发声」一条路径，恒为 false。</summary>
    public bool Shifted => InRange && SoundingPitch != Pitch;
}

/// <summary>一次映射的结果。</summary>
public sealed class MappingResult
{
    /// <summary>基准八度（MIDI 编号，C4 = 第 4 八度）。</summary>
    public int BaseOctave { get; set; }

    public List<MappedNote> Notes { get; init; } = new();

    /// <summary>有对应的键、演奏时会发声的音数。</summary>
    public int InRangeCount => Notes.Count(n => n.InRange);

    /// <summary>没有对应的键（或超出能弹范围）而跳过的音数。卷帘画成灰色的就是它们。</summary>
    public int SkipCount => Notes.Count(n => !n.InRange);

    /// <summary>发声但音高被挪过的音数。现在恒为 0（不再有改音高的路径）。</summary>
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
    /// 执行映射。notes 为主旋律原始音符；transpose 为整体移调半音数（-24..+24）；
    /// manualBaseOctave 为手动基准八度，null 表示自动。
    /// </summary>
    public static MappingResult Map(IReadOnlyList<RawNote> notes, int transpose, int? manualBaseOctave)
    {
        var profile = KeymapProfile.Current;
        var result = new MappingResult();

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
            if (!profile.InRange(want))
            {
                // 超出能弹范围：固定不弹，没有折八度开关。对用户来说同样是「没有对应的键」
                result.Notes.Add(Skipped(p, n,
                    $"超出能弹范围（{Music.SolfegeRange(lo, hi)}），没有对应的键"));
                continue;
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
                SoundingPitch = p,
                InRange = true,
            });
        }

        return result;
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
    /// 多声部合奏按优先级合并成一条谱面。
    ///
    /// **同一条声轨（同一个 Rank）同一时刻的音是和弦，一个不丢，全部保留。**
    /// 只有**不同声部**同刻相撞时才按优先级裁决：只保留编号最小（Rank 最小）的声部，
    /// 低优先级音压在高优先级音尾音上的那一段让位。
    /// 它不改音高，也不丢同声部的和弦音。
    ///
    /// 输出的每个音都写上 <see cref="RawNote.Voice"/> = 它的 Rank（声部序号），
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

        const double eps = 0.025;
        // 1) 同刻组内选 Rank 最小的声部：**该声部这一组的音全部留下**（它就是这一轨的和弦），
        //    其余声部让位；被压掉的低优先级音若更长，"超出主声部结束"的尾巴稍后补回。
        var items = new List<(int Rank, RawNote Note)>();
        int i = 0;
        while (i < ordered.Count)
        {
            int j = i;
            double s0 = ordered[i].Note.Start;
            while (j + 1 < ordered.Count && ordered[j + 1].Note.Start - s0 <= eps) j++;

            // 这一组归 Rank 最小的声部
            int keepRank = ordered[i].Rank;
            for (int k = i + 1; k <= j; k++)
                if (ordered[k].Rank < keepRank) keepRank = ordered[k].Rank;

            // 该声部这一组最晚响到几点：和弦里各音长短可以不同，要看最长的那个
            double keepEnd = double.MinValue;
            for (int k = i; k <= j; k++)
                if (ordered[k].Rank == keepRank && ordered[k].Note.End > keepEnd)
                    keepEnd = ordered[k].Note.End;

            for (int k = i; k <= j; k++)
            {
                var o = ordered[k];
                if (o.Rank == keepRank)
                {
                    items.Add(o);       // 和弦音：同一声部同刻的音全部保留
                    continue;
                }
                if (o.Note.End > keepEnd)
                {
                    items.Add((o.Rank, new RawNote
                    {
                        Pitch = o.Note.Pitch,
                        Start = keepEnd,                // 主声部结束后才轮到它
                        End = o.Note.End,
                        Velocity = o.Note.Velocity,
                        Channel = o.Note.Channel,       // 打击乐判定要用声道，不能丢
                        Voice = o.Rank                  // 补的尾巴段仍然属于它自己那条声轨
                    }));
                }
            }
            i = j + 1;
        }

        // 2) 排序后统一压制：低优先级音落在**优先级更高的音**持续期间 → 让位（超出部分补尾巴）。
        //    这里按声部记「还响到几点」，不能只看上一个音：保留下来的和弦音长短不一，
        //    只看最后一个会把长音后面的空档算错，低优先级音就会挤进来。
        items = items.OrderBy(v => v.Note.Start).ThenBy(v => v.Rank).ThenBy(v => v.Note.Pitch).ToList();
        var result = new List<RawNote>();
        var endByRank = new Dictionary<int, double>();
        foreach (var c in items)
        {
            // 比它优先（Rank 更小）而且还在响的声部，最晚响到几点
            double blocker = double.MinValue;
            foreach (var kv in endByRank)
                if (kv.Key < c.Rank && kv.Value > blocker) blocker = kv.Value;

            if (blocker > c.Note.Start)
            {
                if (c.Note.End > blocker)
                {
                    // 部分被吞：补回超出主声部的尾巴段，并登记它的结束时刻——
                    // 不登记的话，落在尾巴区间里的后一个音会被整条加入，输出就重叠了。
                    var tail = new RawNote
                    {
                        Pitch = c.Note.Pitch,
                        Start = blocker,
                        End = c.Note.End,
                        Velocity = c.Note.Velocity,
                        Channel = c.Note.Channel,
                        Voice = c.Rank
                    };
                    result.Add(tail);
                    endByRank[c.Rank] = tail.End;
                }
                continue;
            }
            result.Add(WithVoice(c.Note, c.Rank));
            endByRank[c.Rank] = c.Note.End;
        }

        return result.OrderBy(n => n.Start).ToList();
    }

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

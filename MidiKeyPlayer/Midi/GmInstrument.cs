namespace MidiKeyPlayer.Midi;

/// <summary>
/// 声部角色：这条轨在合奏里干什么活（鼓 / 贝斯 / 吉他 / 和声 / 人声…）。
/// 界面左侧列表的「声部」列就用这套字，卷帘与列表的配色跟它无关（配色看声部序号）。
/// </summary>
public enum TrackRole
{
    Unknown = 0,   // 文件里没写 GM 音色，也推不出来
    Drums,         // 鼓组（通道 10，或音色名说是鼓）
    Bass,          // 贝斯 / 合成贝斯
    ElectricGuitar,
    AcousticGuitar,
    Melody,        // 推出来的旋律（没有 GM 音色时）
    Accompaniment, // 推出来的伴奏铺底（没有 GM 音色时）
    Piano,
    Organ,
    Strings,
    Ensemble,      // 合奏 / 人声合唱 → 界面标「和声」
    Vocals,        // GM 人声类音色 → 界面标「人声」
    Brass,
    Wind,
    SynthLead,
    SynthPad,
    Chromatic,
    Effects,
}

/// <summary>
/// 声部标牌的取色分组：列表里能一眼分出「节奏 / 低音 / 和声 / 旋律」四类。
/// </summary>
public enum RoleTone
{
    Neutral,   // 未知：不上色
    Rhythm,    // 鼓
    Low,       // 贝斯
    Harmony,   // 吉他 / 键盘 / 弦乐 / 和声 / 铺底
    Lead,      // 旋律 / 主音 / 人声
    Other,     // 铜管 / 管乐 / 效果
}

/// <summary>
/// General MIDI 音色表与声部识别。
///
/// 识别的来源按可信度排：MIDI 文件里的轨名与 GM 音色号是一手证据，直接用；
/// 两者都没有（网上不少 MIDI 只写「Track 1」）才按音域与节奏推一个「疑似」的结果。
/// 提示文字会把「文件里写的」与「推的」分开写，不假装推断结果是文件里的信息。
/// </summary>
public static class GmInstrument
{
    /// <summary>
    /// 128 个 GM 音色（0 起，0 = 大钢琴，127 = 鼓组）的中文名，按 GM 1 规范的分组顺序排列。
    /// 名字取自 General MIDI 1 音色表（MIDI 厂商协会 1991 年规范）。
    /// </summary>
    private static readonly string[] Names =
    {
        "大钢琴", "明亮钢琴", "电钢琴", "酒吧钢琴", "电钢琴1", "电钢琴2", "羽管键琴", "击弦古钢琴",
        "钢片琴", "钟琴", "音乐盒", "颤音琴", "马林巴", "木琴", "管钟", "扬琴",
        "击风琴", "打击风琴", "摇滚风琴", "教堂风琴", "簧风琴", "手风琴", "口琴", "探戈手风琴",
        "尼龙弦吉他", "钢弦吉他", "爵士电吉他", "清音电吉他", "闷音电吉他", "过载电吉他", "失真电吉他", "吉他泛音",
        "原声贝斯", "指弹电贝斯", "拨片电贝斯", "无品贝斯", "击弦贝斯1", "击弦贝斯2", "合成贝斯1", "合成贝斯2",
        "小提琴", "中提琴", "大提琴", "低音提琴", "弦乐震音", "弦乐拨奏", "竖琴", "定音鼓",
        "弦乐合奏1", "弦乐合奏2", "合成弦乐1", "合成弦乐2", "人声合唱「啊」", "人声「噢」", "合成人声", "管弦乐齐奏",
        "小号", "长号", "大号", "弱音小号", "圆号", "铜管乐组", "合成铜管1", "合成铜管2",
        "高音萨克斯", "中音萨克斯", "次中音萨克斯", "上低音萨克斯", "双簧管", "英国管", "巴松管", "单簧管",
        "短笛", "长笛", "竖笛", "排箫", "吹瓶声", "尺八", "口哨", "陶笛",
        "方波主音", "锯齿波主音", "汽笛风琴主音", "高音主音", "旋律主音", "合成主音1", "合成主音2", "合成主音3",
        "合成铺底1", "合成铺底2", "合成铺底3", "合成铺底4", "合成铺底5", "合成铺底6", "合成铺底7", "合成铺底8",
        "合成效果1", "合成效果2", "合成效果3", "合成效果4", "合成效果5", "合成效果6", "合成效果7", "合成效果8",
        "西塔琴", "班卓琴", "三味线", "筝", "卡林巴", "风笛", "沙锤", "木鱼",
        "铃铛", "阿哥哥铃", "钢鼓", "木鱼块", "太鼓", "旋律鼓", "合成鼓", "反镲",
        "吉他品丝声", "呼吸声", "海浪", "鸟鸣", "电话铃", "直升机", "鼓掌声",
        "枪声", "合成打击乐", "钢琴泛音", "合成贝斯滑音", "合成弦乐滑音", "合成人声滑音", "合成铜管滑音", "合成铺底滑音", "合成效果滑音",
        "鼓组",
    };

    /// <summary>音色名：越界（-1 或 &gt;=128）给空串。</summary>
    public static string ProgramName(int program) =>
        program >= 0 && program < Names.Length ? Names[program] : "";

    /// <summary>按 GM 音色号给声部角色。0..127 之外当作未知。</summary>
    public static TrackRole RoleOfProgram(int program)
    {
        if (program < 0 || program >= 128) return TrackRole.Unknown;
        if (program <= 7) return TrackRole.Piano;
        if (program <= 15) return TrackRole.Chromatic;
        if (program <= 23) return TrackRole.Organ;
        if (program <= 27) return TrackRole.AcousticGuitar;   // 尼龙弦 / 钢弦 / 爵士电吉他
        if (program <= 31) return TrackRole.ElectricGuitar;   // 清音 / 闷音 / 过载 / 失真 / 泛音
        if (program <= 39) return TrackRole.Bass;
        if (program == 47) return TrackRole.Drums;            // 定音鼓算节奏
        if (program <= 46) return TrackRole.Strings;          // 小提琴…拨奏弦乐
        if (program <= 51) return TrackRole.Strings;          // 弦乐合奏 / 合成弦乐
        if (program <= 55) return TrackRole.Vocals;           // 人声「啊」「噢」/ 合成人声
        if (program <= 63) return TrackRole.Brass;
        if (program <= 79) return TrackRole.Wind;
        if (program <= 87) return TrackRole.SynthLead;
        if (program <= 95) return TrackRole.SynthPad;
        // 117..120 是太鼓 / 旋律鼓 / 合成鼓 / 反镲，也算节奏
        if (program >= 116 && program <= 119) return TrackRole.Drums;
        return TrackRole.Effects;                             // 96..127 其余
    }

    /// <summary>列表里那个短标牌的字。宽度最多三个字，列表列宽按它定。</summary>
    public static string TagOf(TrackRole role) => role switch
    {
        TrackRole.Drums => "鼓",
        TrackRole.Bass => "贝斯",
        TrackRole.ElectricGuitar => "电吉他",
        TrackRole.AcousticGuitar => "吉他",
        TrackRole.Melody => "旋律",
        TrackRole.Accompaniment => "伴奏",
        TrackRole.Piano => "键盘",
        TrackRole.Organ => "风琴",
        TrackRole.Strings => "弦乐",
        TrackRole.Ensemble => "和声",
        TrackRole.Vocals => "人声",
        TrackRole.Brass => "铜管",
        TrackRole.Wind => "管乐",
        TrackRole.SynthLead => "主音",
        TrackRole.SynthPad => "铺底",
        TrackRole.Chromatic => "音块",
        TrackRole.Effects => "效果",
        _ => "未标注",
    };

    /// <summary>标牌取色分组。</summary>
    public static RoleTone ToneOf(TrackRole role) => role switch
    {
        TrackRole.Drums => RoleTone.Rhythm,
        TrackRole.Bass => RoleTone.Low,
        TrackRole.ElectricGuitar or TrackRole.AcousticGuitar or TrackRole.Piano or TrackRole.Organ
            or TrackRole.Strings or TrackRole.Ensemble or TrackRole.SynthPad => RoleTone.Harmony,
        TrackRole.Melody or TrackRole.Vocals or TrackRole.SynthLead => RoleTone.Lead,
        TrackRole.Brass or TrackRole.Wind or TrackRole.Chromatic or TrackRole.Effects => RoleTone.Other,
        _ => RoleTone.Neutral,
    };

    /// <summary>
    /// 没有 GM 音色号时的兜底推断，按音域与节奏猜一个。界面会把它标成「疑似」。
    /// 这是猜测，不是文件里的信息；宁可给「伴奏」也不硬说成某件乐器。
    /// </summary>
    public static TrackRole Infer(int channel, string? name, IReadOnlyList<RawNote> notes)
    {
        if (channel == 9) return TrackRole.Drums;
        if (LooksLikeDrums(name)) return TrackRole.Drums;
        if (notes == null || notes.Count == 0) return TrackRole.Unknown;

        // 音域重心用中位数：比平均值抗离群（一个八度外的错音不会带偏整条轨）
        var pitches = new List<int>(notes.Count);
        foreach (var n in notes) pitches.Add(n.Pitch);
        pitches.Sort();
        int median = pitches[pitches.Count / 2];

        // 又短又快、音高几乎不动的轨：更像打击乐或效果，不猜旋律
        double span = 0;
        foreach (var n in notes) span += Math.Max(0, n.End - n.Start);
        double perNote = span / notes.Count;
        if (perNote < 0.12 && pitches[^1] - pitches[0] <= 12) return TrackRole.Drums;

        if (median <= 47) return TrackRole.Bass;                          // 重心在 B2 以下 → 低音
        if (median >= 67 && notes.Count <= 400) return TrackRole.Melody;   // 高音区、不太密 → 旋律
        return TrackRole.Accompaniment;                                    // 其余 → 伴奏铺底
    }

    /// <summary>
    /// 轨名是不是「没写什么」的通用名（Track 1 / 声道 3 / 空）。
    /// 命中时列表改显示识别出的乐器名，比原样显示「Track 1」有用。
    /// </summary>
    public static bool IsGenericTrackName(string? name)
    {
        string t = (name ?? "").Trim();
        if (t.Length == 0) return true;
        string low = t.ToLowerInvariant();
        if (low.StartsWith("track") || low.StartsWith("sequence") || low.StartsWith("声道")
            || low.StartsWith("通道") || low.StartsWith("声部") || low.StartsWith("未命名")
            || low.StartsWith("part") || low.StartsWith("channel") || low.StartsWith("ch ")) return true;

        // 通篇只有 ASCII 字母、空格与数字，而且带数字：基本是「Track 1」「Piano 2」这类占位名
        bool hasDigit = false;
        foreach (char c in t)
        {
            if (char.IsDigit(c)) { hasDigit = true; continue; }
            if (c > 0x7F) return false;
            if (!char.IsLetter(c) && c != ' ' && c != '_' && c != '-' && c != '.') return false;
        }
        return hasDigit;
    }

    private static bool LooksLikeDrums(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string low = name.ToLowerInvariant();
        return low.Contains("drum") || low.Contains("perc") || low.Contains("kick")
            || low.Contains("snare") || low.Contains("hat") || low.Contains("cymbal")
            || low.Contains("鼓") || low.Contains("打击");
    }
}

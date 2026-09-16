using System.Text.Json;
using System.Text.Json.Serialization;
using MidiKeyPlayer.Persist;

namespace MidiKeyPlayer.Engine;

/// <summary>
/// 【兼容保留，界面不再提供】超界处理：丢音 / 就近折八度。JSON 里写 <c>drop</c> / <c>fold</c>。
/// 新版本固定按 <see cref="Drop"/> 处理：超出可弹范围的音一律不弹。读到老文件的 <c>fold</c> 只写一条日志。
/// </summary>
[JsonConverter(typeof(OutOfRangeModeConverter))]
public enum OutOfRangeMode { Drop, Fold }

/// <summary>
/// 【兼容保留，界面不再提供】老的缺音处理档位：跳过 / 用高半音代替 / 用低半音代替。
/// 行为已经固定成「跳过」：键表里没有这个音就不发声，不再按策略改音高。
/// 这一项也不再写回 JSON（见 <see cref="KeymapProfile.MissingNote"/>）。
/// </summary>
[JsonConverter(typeof(MissingNoteModeConverter))]
public enum MissingNoteMode { Skip, Up, Down }

/// <summary>
/// 一个主键：物理键名 + 相对 <see cref="KeymapProfile.BaseNote"/> 的半音偏移 + 属于第几行。
/// JSON 里写成三元素数组 <c>["Z",0,0]</c>，由 <see cref="KeyBindingConverter"/> 负责。
/// </summary>
[JsonConverter(typeof(KeyBindingConverter))]
public sealed class KeyBinding
{
    /// <summary>键名。单字符键写 "Z"、","、"1"；命名键写 "PageUp"、"MouseLeft"。</summary>
    public string Key { get; set; } = "";

    /// <summary>相对 <see cref="KeymapProfile.BaseNote"/> 的半音偏移。</summary>
    public int Offset { get; set; }

    /// <summary>
    /// 这个键属于方案的第几行（从 0 数起，一行里的键必须写同一个值）。
    /// 界面按它一行一行整齐显示：21 键自然音与 21 键半音的下排 Z..M 是 0、中排 A..J 是 1、上排 Q..U 是 2；
    /// 第五人格键位（三排各 12 个半音）的低音排是 0、中音排是 1、高音排是 2。
    /// 老文件没有这个字段，读进来是 0；一套方案里所有键都是 0 时，界面退回「按物理键盘的排分组」。
    /// 不能用「物理键盘的排」推方案的行：第五人格键位的低音排用了逗号、句点、分号、斜杠、减号、左方括号，
    /// 这些键散在四个物理排上。
    /// </summary>
    public int Row { get; set; }

    public override string ToString() => $"{Key}{(Offset >= 0 ? "+" : "")}{Offset}@第{Row}行";
}

/// <summary>方案 JSON 读不动时的异常。消息是给人看的中文。</summary>
public sealed class KeymapFormatException : Exception
{
    public KeymapFormatException(string message) : base(message) { }
    public KeymapFormatException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// 键位方案：把「键名 → 半音偏移」与修饰键全部交给配置。
///
/// 活动方案是 <see cref="Current"/>（全局一份）。文件在 %LOCALAPPDATA%\MidiKeyPlayer\keymap.json。
/// 序列化走源生成上下文 <see cref="KeymapJson"/>：发布开裁剪（PublishTrimmed），
/// 反射式序列化依赖的元数据可能被裁掉；源生成在编译期产出读写代码，不受裁剪影响。
/// </summary>
public sealed class KeymapProfile
{
    /// <summary>
    /// 默认方案名。内置四套都用直白名字（自然音 / 半音 / 第五人格键位 / 8 键半音），
    /// 不再由几何量拼出来 ——「36 键 4 排 3 个八度」这类名字会把用户绕晕，实际只有三排。
    /// 默认方案：Z X C V B N M / A S D F G H J / Q W E R T Y U 三排自然音，一排一个八度，60..95。
    /// </summary>
    public const string DefaultName = "21 键自然音";

    /// <summary>当前格式版本。读到更大的版本号就是不认识的格式。</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string Name { get; set; } = DefaultName;
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public int BaseNote { get; set; } = 60;
    public List<KeyBinding> Keys { get; set; } = new();
    public string? OctaveUp { get; set; } = "MouseRight";
    public string? OctaveDown { get; set; } = "MouseLeft";
    public string? Sharp { get; set; } = "MouseMiddle";

    /// <summary>
    /// 降半音键（JSON 里写 <c>flat</c>）。按住它再按音键，发出的音低半音 ——
    /// 与升半音键对称：升半音用「下方邻键 + 升半音」，降半音用「上方邻键 + 降半音」。
    /// 同一个黑键两条路都能到时，固定优先升半音键（规则见 <see cref="TryKeyOfPitch(int, out string, out int, out bool, out bool, out int)"/>）；
    /// 降半音键因此主要用在两处：方案没绑升半音键时补半音、把音域向下多扩一个半音。
    /// 老方案文件没有这个字段，读进来是 null（按没绑处理）。
    /// </summary>
    public string? Flat { get; set; }

    /// <summary>
    /// 是否启用功能键（八度键与升半音键）。界面上的勾选框「启用功能键」。
    /// 不勾时：功能键区块的输入禁用，引擎按「没有修饰键」处理 ——
    /// 超出键位范围的音直接不发声，不做八度折叠。默认启用。
    /// </summary>
    public bool ModifiersEnabled { get; set; } = true;

    /// <summary>
    /// 【兼容保留，界面不再提供】老文件里写过音域下限。新版本的音域一律由键位推导
    /// （见 <see cref="ResolveMinNote"/>），这里读到什么值都不参与判断，只在写盘时原样保留。
    /// </summary>
    public int? MinNote { get; set; }

    /// <summary>【兼容字段】老文件里写过音域上限。含义同 <see cref="MinNote"/>。</summary>
    public int? MaxNote { get; set; }

    /// <summary>【兼容字段】超出可弹范围的音固定不弹，这个字段不再影响行为。</summary>
    public OutOfRangeMode OutOfRange { get; set; } = OutOfRangeMode.Drop;

    /// <summary>
    /// 【兼容字段，界面不再提供】老的「半音怎么处理？」三档。行为已固定为「跳过」：
    /// 键表里没有这个音就不发声。这里永远读到 <see cref="MissingNoteMode.Skip"/>，
    /// 读盘时把老文件里的选择压成 Skip，并且不再写回 JSON（<see cref="JsonIgnoreAttribute"/>）。
    /// </summary>
    [JsonIgnore]
    public MissingNoteMode MissingNote
    {
        get => MissingNoteMode.Skip;
        set { /* 老调用点写进来的值一律忽略：行为固定为跳过 */ }
    }

    // ================= 位置与活动方案 =================

    private static string DirPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MidiKeyPlayer");

    /// <summary>方案文件路径：%LOCALAPPDATA%\MidiKeyPlayer\keymap.json。</summary>
    public static string FilePath => Path.Combine(DirPath, "keymap.json");

    private static KeymapProfile? _current;

    /// <summary>
    /// 全局活动方案。赋 null 等于恢复默认，永远不会把 null 放出去。
    /// 首次访问时读盘（keymap.json → 设置里的方案名 → 默认方案）。
    /// </summary>
    public static KeymapProfile Current
    {
        get => _current ??= Load();
        set => _current = value ?? BuildDefault();
    }

    // ================= 内置预设 =================

    private static IReadOnlyList<KeymapProfile>? _presets;

    /// <summary>
    /// 4 套内置预设。第一项就是默认方案。名字现在是写死的中文（见 <see cref="BuildPresets"/>），
    /// <see cref="SchemeNameOf"/> 只留着算几何量，不再用来命名。只读用；要改先 <see cref="Clone"/>。
    /// </summary>
    public static IReadOnlyList<KeymapProfile> Presets => _presets ??= BuildPresets();

    /// <summary>默认方案（第 1 套）。每次取都是新副本，改它不影响内置预设。</summary>
    public static KeymapProfile Default => BuildDefault();

    /// <summary>
    /// 第 1 套（默认）：21 键自然音，三行七列，一行一个八度。
    /// 下排 Z X C V B N M = 中音 do..si；中排 A S D F G H J = 高八度；上排 Q W E R T Y U = 再高一个八度。
    /// 60..95。预设不带修饰键：功能键总开关关掉。
    /// </summary>
    private static KeymapProfile BuildDefault() => BuildNatural21();

    /// <summary>
    /// 21 键自然音：三行七列，每行一个八度，从左到右 do re mi fa sol la si。
    /// 偏移 0/2/4/5/7/9/11 一行，行距 +12。能弹 60..95。
    /// </summary>
    private static KeymapProfile BuildNatural21()
    {
        var keys = new List<KeyBinding>();
        // 三行的物理排：下排 ZXCV 排、中排 ASDF 排、上排 QWERTY 排。相对基准 60 的行距 = 0 / +12 / +24。
        // Row 也按这个顺序：0 = 下排（中音）、1 = 中排、2 = 上排。界面把 Row 大的显示在上面。
        string[][] rows = { new[] { "Z", "X", "C", "V", "B", "N", "M" },
                            new[] { "A", "S", "D", "F", "G", "H", "J" },
                            new[] { "Q", "W", "E", "R", "T", "Y", "U" } };
        int[] degrees = { 0, 2, 4, 5, 7, 9, 11 };
        for (int r = 0; r < rows.Length; r++)
            for (int c = 0; c < degrees.Length; c++)
                keys.Add(new KeyBinding { Key = rows[r][c], Offset = 12 * r + degrees[c], Row = r });

        return new KeymapProfile
        {
            Version = CurrentVersion,
            Description = "三行七列自然音：Z X C V B N M 是 do..si，A S D F G H J 高一个八度，Q W E R T Y U 再高一个八度",
            BaseNote = 60,
            Keys = keys,
            ModifiersEnabled = false,
            OctaveUp = null,
            OctaveDown = null,
            Sharp = null,
        };
    }

    /// <summary>
    /// 21 键半音：三行七列**自然音**，三行分别是低音 / 中音 / 高音三个八度，配半音键补齐半音。
    /// 下排 Z X C V B N M = 低音 do..si（48 起，偏移 −12 −10 −8 −7 −5 −3 −1）；
    /// 中排 A S D F G H J = 中音 do..si（60 起，偏移 0 2 4 5 7 9 11）；
    /// 上排 Q W E R T Y U = 高音 do..si（72 起，偏移 12 14 16 17 19 21 23）。
    /// 功能键打开：按住 Shift 升半音、按住 Ctrl 降低半音（v1.0.14 的「异环键位」已并入本套，
    /// 键位一字未动，见 <see cref="LegacyPresetAlias"/>）。八度键留空。
    /// 能弹 47..84（低音 do 再低半音 到 高音 si 升半音）。
    /// </summary>
    private static KeymapProfile BuildChromatic21()
    {
        var keys = new List<KeyBinding>();
        string[][] rows = { new[] { "Z", "X", "C", "V", "B", "N", "M" },
                            new[] { "A", "S", "D", "F", "G", "H", "J" },
                            new[] { "Q", "W", "E", "R", "T", "Y", "U" } };
        int[] degrees = { 0, 2, 4, 5, 7, 9, 11 };
        // 行距 12：下排（Row 0）低一个八度，中排（Row 1）是本八度，上排（Row 2）高一个八度。
        for (int r = 0; r < rows.Length; r++)
            for (int c = 0; c < degrees.Length; c++)
                keys.Add(new KeyBinding { Key = rows[r][c], Offset = 12 * (r - 1) + degrees[c], Row = r });

        return new KeymapProfile
        {
            Version = CurrentVersion,
            Description = "三行七列自然音，三行是低音 / 中音 / 高音三个八度："
                        + "Z X C V B N M 是低音 do..si，A S D F G H J 是中音 do..si，Q W E R T Y U 是高音 do..si；"
                        + "按住 Shift 升半音，按住 Ctrl 降低半音",
            BaseNote = 60,
            Keys = keys,
            ModifiersEnabled = true,
            OctaveUp = null,
            OctaveDown = null,
            Sharp = "Shift",
            Flat = "Ctrl",
        };
    }

    /// <summary>
    /// 第 3 套（第五人格键位）：三排各 12 个半音，一排一个八度。键位与旧名「36 键半音三排」完全相同。
    ///
    /// 低音排（基准 60 − 12 = 48 起）：
    ///   DO=, #DO=L RE=. #RE=; MI=/ FA=I #FA=9 SO=O #SO=0 RA=P #RA=- XI=[
    /// 中音排（60 起）：
    ///   DO=Z #DO=S RE=X #RE=D MI=C FA=V #FA=G SO=B #SO=H RA=N #RA=J XI=M
    /// 高音排（72 起）：
    ///   DO=Q #DO=2 RE=W #RE=3 MI=E FA=R #FA=5 SO=T #SO=6 RA=Y #RA=7 XI=U
    ///
    /// 能弹 48..83。预设不带修饰键。
    /// </summary>
    private static KeymapProfile BuildChromatic36()
    {
        // 每排 12 个键，从 DO 到 XI 依次加一个半音。行距 = 12。
        string[] low = { ",", "L", ".", ";", "/", "I", "9", "O", "0", "P", "-", "[" };
        string[] mid = { "Z", "S", "X", "D", "C", "V", "G", "B", "H", "N", "J", "M" };
        string[] high = { "Q", "2", "W", "3", "E", "R", "5", "T", "6", "Y", "7", "U" };

        var keys = new List<KeyBinding>();
        string[][] rows = { low, mid, high };
        for (int r = 0; r < rows.Length; r++)
            for (int c = 0; c < rows[r].Length; c++)
                keys.Add(new KeyBinding { Key = rows[r][c], Offset = -12 + 12 * r + c, Row = r });

        return new KeymapProfile
        {
            Version = CurrentVersion,
            Description = "三排各 12 个半音：,L.;/I9O0P-[ 是低八度，ZSXDCVGBHNJM 是中音，Q2W3ER5T6Y7U 是高八度",
            BaseNote = 60,
            Keys = keys,
            ModifiersEnabled = false,
            OctaveUp = null,
            OctaveDown = null,
            Sharp = null,
        };
    }

    /// <summary>
    /// 第 4 套（8 键半音）：一排 8 个键 <c>Z X C V B N M ,</c> 是 do..高音 do（自然音），
    /// 鼠标三键补齐：左键降八度、右键升八度、中键升半音。能弹 48..85。
    /// 键位与旧版删掉的「8 键单排（含高八度 do，默认）」完全相同（见 <see cref="LegacyPresetAlias"/>）。
    /// </summary>
    private static KeymapProfile BuildChromatic8()
    {
        var keys = new List<KeyBinding>();
        // 一排八个键：自然音 do re mi fa sol la si，再加高音 do。行号都是 0。
        string[] row = { "Z", "X", "C", "V", "B", "N", "M", "," };
        int[] offsets = { 0, 2, 4, 5, 7, 9, 11, 12 };
        for (int c = 0; c < row.Length; c++)
            keys.Add(new KeyBinding { Key = row[c], Offset = offsets[c], Row = 0 });

        return new KeymapProfile
        {
            Version = CurrentVersion,
            Description = "一排 8 键自然音：Z X C V B N M , 是 do..高音 do；"
                        + "鼠标左键降八度、右键升八度、中键升半音",
            BaseNote = 60,
            Keys = keys,
            ModifiersEnabled = true,
            OctaveUp = "MouseRight",
            OctaveDown = "MouseLeft",
            Sharp = "MouseMiddle",
        };
    }

    /// <summary>
    /// 方案名：「N 键 M 排 K 个八度」。
    /// N = 键位数；M = 这些键在物理键盘上占几排；K = 能弹音域的八度数（向上取整）。
    /// K 由「键位 × 八度键」能到达的音高张角算出，升半音键只在音域内补半音，不扩展边界。
    /// </summary>
    public static string SchemeNameOf(KeymapProfile profile)
    {
        int keys = profile.Keys.Count(k => k != null && !string.IsNullOrWhiteSpace(k.Key));
        return $"{keys} 键 {RowCountOf(profile)} 排 {OctaveCountOf(profile)} 个八度";
    }

    /// <summary>这些键在物理键盘上占几排。认不出排的键（鼠标、命名键）不计数。</summary>
    private static int RowCountOf(KeymapProfile profile)
    {
        var rows = new HashSet<int>();
        foreach (var k in profile.Keys)
        {
            if (k == null) continue;
            int row = PhysicalRowOf(k.Key);
            if (row >= 0) rows.Add(row);
        }
        return Math.Max(1, rows.Count);
    }

    /// <summary>键名 → 物理排号：0 数字排、1 QWERTY 排、2 ASDF 排、3 ZXCV 排；-1 表示认不出。</summary>
    private static int PhysicalRowOf(string? key)
    {
        string name = CanonicalKeyName(key);
        if (name.Length != 1) return -1;
        char c = name[0];
        if ("1234567890-=".Contains(c)) return 0;
        if ("QWERTYUIOP[]\\".Contains(c)) return 1;
        if ("ASDFGHJKL;'".Contains(c)) return 2;
        if ("ZXCVBNM,./".Contains(c)) return 3;
        return -1;
    }

    /// <summary>
    /// 能弹音域的八度数：最短键位到最长键位之间，加上八度键能挪动的量，再向上取整。
    /// 一个音都认不出时算 1 个八度。功能键关掉时八度键不参与。
    /// </summary>
    private static int OctaveCountOf(KeymapProfile profile)
    {
        int min = int.MaxValue, max = int.MinValue;
        foreach (var k in profile.Keys)
        {
            if (k == null || !IsKnownKeyName(k.Key)) continue;
            if (k.Offset < min) min = k.Offset;
            if (k.Offset > max) max = k.Offset;
        }
        if (min > max) return 1;

        bool canUp = profile.ModifiersEnabled && !string.IsNullOrWhiteSpace(profile.OctaveUp);
        bool canDown = profile.ModifiersEnabled && !string.IsNullOrWhiteSpace(profile.OctaveDown);
        int lo = min - (canDown ? 12 : 0);
        int hi = max + (canUp ? 12 : 0);
        return Math.Max(1, (int)Math.Ceiling((hi - lo) / 12.0));
    }

    /// <summary>
    /// 4 套内置预设，名字直接写死成直白的中文（不再由 <see cref="SchemeNameOf"/> 拼几何量）。
    /// 第 1 套的名字必须等于 <see cref="DefaultName"/>，不等就写日志。
    /// 第 3 套的用户可见名字由用户指定，键位与旧的「36 键半音三排」相同（见 <see cref="LegacyPresetAlias"/>）。
    /// </summary>
    private static IReadOnlyList<KeymapProfile> BuildPresets()
    {
        var list = new List<KeymapProfile>
        {
            Preset(BuildDefault(), DefaultName),              // 第 1 套 = 默认方案：中 / 高 / 高高，三行七列自然音
            Preset(BuildChromatic21(), "21 键半音"),           // 第 2 套：低 / 中 / 高三个八度 + Shift 升 / Ctrl 降半音
            Preset(BuildChromatic36(), "第五人格键位"),         // 第 3 套：三排各 12 个半音
            Preset(BuildChromatic8(), "8 键半音"),             // 第 4 套：一排 8 键 do..高音 do + 鼠标三键
        };
        if (!string.Equals(list[0].Name, DefaultName, StringComparison.Ordinal))
            LogFile.Append($"[键位] 默认方案名是「{list[0].Name}」，与常量「{DefaultName}」不同，请同步。");
        return list;
    }

    private static KeymapProfile Preset(KeymapProfile profile, string name)
    {
        profile.Name = name;
        return profile;
    }

    /// <summary>按方案名找内置预设。找不到返回 null。老名字（改名前 / 已删掉）先过一次别名表。</summary>
    public static KeymapProfile? PresetByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        foreach (var p in Presets)
            if (string.Equals(p.Name, name, StringComparison.Ordinal)) return p;

        string? now = AliasOf(name);
        if (now != null)
            foreach (var p in Presets)
                if (string.Equals(p.Name, now, StringComparison.Ordinal)) return p;
        return null;
    }

    // ================= 老方案名（升级用） =================

    /// <summary>
    /// 改过名的预设：老名字 → 现在的名字。老用户设置里存的还是老名字。
    /// 只列「老名字对应的键位今天还在」的那些；键位已经删掉的老名字走 <see cref="RemovedPresetNames"/>。
    /// </summary>
    private static readonly Dictionary<string, string> LegacyPresetAlias = new(StringComparer.Ordinal)
    {
        // 上一版由 SchemeNameOf 算出来的名字 → 现在的直白名字（键位没变，只是换了称呼）
        ["21 键 3 排 3 个八度"] = DefaultName,
        ["21 键 3 排 2 个八度"] = "21 键半音",
        ["36 键 4 排 3 个八度"] = "第五人格键位",
        ["36 键 3 排 3 个八度"] = "第五人格键位",
        // 第 3 套改名：老名字 → 现在的名字。键位一字未动
        ["36 键半音三排"] = "第五人格键位",
        // 8 键单排这一套删掉过一版，v1.0.7 又加回来了（名字改成「8 键半音」）。键位一字未动，
        // 所以老名字指回新预设，老用户的设置能直接恢复，不用手工再选一次。
        ["8 键单排（含高八度 do，默认）"] = "8 键半音",
        ["8 键 1 排 3 个八度"] = "8 键半音",
        // v1.0.14 的「异环键位」在 v1.0.15 并入「21 键半音」（键位相同，只是多了 Ctrl 降半音）。
        // 已选过它的老用户直接落到合并后的方案，不用手工再选一次。
        ["异环键位"] = "21 键半音",
    };

    /// <summary>
    /// 已经删掉的预设：读到这些名字就回退到默认方案。
    /// 现在保留 21 键自然音 / 21 键半音 / 第五人格键位 / 8 键半音四套，其余历史名字全部列在这里。
    /// 改过名但键位还在的（例如「36 键半音三排」「8 键单排（含高八度 do，默认）」）走
    /// <see cref="LegacyPresetAlias"/>，不要写在这里。
    /// </summary>
    private static readonly HashSet<string> RemovedPresetNames = new(StringComparer.Ordinal)
    {
        // 旧的自然音方案（7 键 / 15 键 / 23 键）
        "7 键 1 排 1 个八度",
        "7 键单排自然音阶",
        "15 键 3 排 2 个八度",
        "15 键三排",
        "23 键 3 排 3 个八度",
        "23 键三排自然音阶",
        // 旧的半音阶方案（12 键 / 24 键）
        "12 键 2 排 1 个八度",
        "24 键 2 排 2 个八度",
        "12 键半音阶双排",
        // 更早删掉的
        "5 键极简",
        "宽音域 8 八度折叠",
    };

    /// <summary>老名字对应的新名字（改过名的那几套）。不是老名字就返回 null。</summary>
    public static string? AliasOf(string? name)
        => !string.IsNullOrWhiteSpace(name) && LegacyPresetAlias.TryGetValue(name!, out string? now) ? now : null;

    /// <summary>这个名字是不是「已经删掉的预设」。</summary>
    public static bool IsRemovedPresetName(string? name)
        => !string.IsNullOrWhiteSpace(name) && RemovedPresetNames.Contains(name!);

    /// <summary>
    /// 读到一个方案名之后做一次升级：改过名的预设就地改名（键位不动）；删掉的预设回退到默认方案。
    /// 返回要用的方案，log 里是中文说明（空串 = 不用记）。
    /// </summary>
    private static KeymapProfile UpgradeLegacyName(KeymapProfile profile, out string log)
    {
        log = "";
        string name = profile.Name ?? "";

        string? now = AliasOf(name);
        if (now != null)
        {
            profile.Name = now;
            log = $"[键位] 方案名已升级：{name} → {now}";
            return profile;
        }

        if (IsRemovedPresetName(name))
        {
            log = $"[键位] 方案「{name}」已经删掉，回退到默认方案「{DefaultName}」。";
            return BuildDefault();
        }

        return profile;
    }

    // ================= 方案库（内置 + 用户自己存的文件） =================

    /// <summary>
    /// 用户方案文件所在目录：%LOCALAPPDATA%\MidiKeyPlayer\schemes\。
    /// 一个方案一个文件，文件名（去掉扩展名）= 方案名。
    /// </summary>
    public static string SchemesDir =>
        Path.Combine(DirPath, "schemes");

    /// <summary>某个方案名对应的文件路径。名字里的非法字符换成下划线。</summary>
    public static string SchemeFilePath(string? name)
    {
        string safe = (name ?? "").Trim();
        foreach (char bad in Path.GetInvalidFileNameChars()) safe = safe.Replace(bad, '_');
        if (safe.Length == 0) safe = "未命名方案";
        return Path.Combine(SchemesDir, safe + ".json");
    }

    /// <summary>schemes 目录下已有的方案文件名（不含扩展名）。目录不在或读不动就返回空。</summary>
    private static List<string> SchemeFiles()
    {
        var list = new List<string>();
        try
        {
            if (!Directory.Exists(SchemesDir)) return list;
            foreach (var path in Directory.GetFiles(SchemesDir, "*.json"))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (!string.IsNullOrWhiteSpace(name)) list.Add(name);
            }
        }
        catch (Exception ex)
        {
            LogFile.Append("[键位] 列 schemes 方案文件失败：" + ex.Message);
        }
        return list;
    }

    /// <summary>
    /// 全部可用方案名：内置 3 套在前（按 Presets 的顺序），用户方案文件在后（按名字排序）。
    /// 名字相同的只留一次。下拉框直接用这个列表。
    /// </summary>
    public static IReadOnlyList<string> ListSchemeNames()
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in Presets)
            if (!string.IsNullOrWhiteSpace(p.Name) && seen.Add(p.Name)) names.Add(p.Name);

        var files = SchemeFiles();
        files.Sort(StringComparer.Ordinal);
        foreach (string f in files)
            if (seen.Add(f)) names.Add(f);

        return names;
    }

    /// <summary>
    /// 这个名字是不是内置预设名。内置方案不能被改名、不能被删除，名字也不能被新方案占用。
    /// 但它的**键位改动**可以存到 schemes 目录里，见 <see cref="LoadByName"/>。
    /// </summary>
    public static bool IsBuiltInSchemeName(string? name) => PresetByName(name) != null;

    /// <summary>这个名字已经被某个方案占用（内置或用户文件）。</summary>
    public static bool SchemeNameExists(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (IsBuiltInSchemeName(name)) return true;
        try
        {
            return File.Exists(SchemeFilePath(name));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 按名字载入一个方案。**用户目录优先**：先读 schemes\&lt;名字&gt;.json，
    /// 读到了就用它（内置名字也一样，用户改过的键位存在这里）；文件不在才退回内置预设。
    /// 两边都没有返回 null，并写出可读原因。绝不抛异常。
    /// </summary>
    public static KeymapProfile? LoadByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        if (TryLoadSchemeFile(name, out var user))
            return user;

        var preset = PresetByName(name);
        if (preset != null) return preset.Clone();

        LogFile.Append($"[键位] 没有方案文件：{SchemeFilePath(name)}");
        return null;
    }

    /// <summary>
    /// 只读用户目录里的同名方案文件。读到就返回 true（profile 是它）。
    /// 文件不在返回 false（这不是错误，调用方接着找内置预设）；文件坏了写一条日志再返回 false。
    /// </summary>
    public static bool TryLoadSchemeFile(string? name, out KeymapProfile? profile)
    {
        profile = null;
        if (string.IsNullOrWhiteSpace(name)) return false;

        string path = SchemeFilePath(name);
        try
        {
            if (!File.Exists(path)) return false;
            profile = FromJson(File.ReadAllText(path));
            return true;
        }
        catch (KeymapFormatException ex)
        {
            LogFile.Append($"[键位] 方案文件格式不对（{path}）：{ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            LogFile.Append($"[键位] 读方案文件失败（{path}）：{ex.Message}");
            return false;
        }
    }

    // ================= 读盘 / 写盘 =================

    /// <summary>
    /// 读活动方案。顺序：keymap.json → 设置里记的方案名（`LoadByName`：先读用户目录的同名方案，
    /// 读不到才用内置预设）→ 默认方案。
    /// 读到改名前的预设名就地改名；读到已经删掉的预设名回退到默认方案，两种情况都写日志。
    /// 任何失败都只写日志，返回默认方案的副本，绝不抛异常。
    /// </summary>
    public static KeymapProfile Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = FromJson(File.ReadAllText(FilePath));
                if (!string.IsNullOrWhiteSpace(loaded.Name))
                {
                    var fixedUp = UpgradeLegacyName(loaded, out string note);
                    if (note.Length > 0)
                    {
                        LogFile.Append(note);
                        try { fixedUp.Save(); } catch { /* 存不动只影响下次启动 */ }
                    }
                    return fixedUp;
                }
                LogFile.Append("[键位] keymap.json 没有方案名，用默认方案。");
            }
        }
        catch (Exception ex)
        {
            LogFile.Append("[键位] 读取 keymap.json 失败，用默认方案：" + ex.Message);
        }

        try
        {
            string want = AppConfig.Load().KeymapName;
            if (!string.IsNullOrWhiteSpace(want))
            {
                var byName = LoadByName(want);
                if (byName != null)
                {
                    LogFile.Append($"[键位] 按设置载入方案：{byName.Name}");
                    return byName;
                }
            }
        }
        catch (Exception ex)
        {
            LogFile.Append("[键位] 按设置选方案失败，用默认方案：" + ex.Message);
        }

        return BuildDefault();
    }

    /// <summary>把当前方案写回 keymap.json。失败只写日志，不抛异常。</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DirPath);
            File.WriteAllText(FilePath, ToJson());
        }
        catch (Exception ex)
        {
            LogFile.Append("[键位] 保存 keymap.json 失败：" + ex.Message);
        }
    }

    /// <summary>导出到用户选的路径。返回 false 时 error 里是中文原因。</summary>
    public bool TryExportFile(string path, out string error)
    {
        error = "";
        try
        {
            string dir = Path.GetDirectoryName(path) ?? "";
            if (dir.Length > 0) Directory.CreateDirectory(dir);
            File.WriteAllText(path, ToJson());
            return true;
        }
        catch (Exception ex)
        {
            error = $"写文件失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 从文件导入。成功时给出新方案；失败时 error 里是中文原因，调用方的原有设置不动。
    /// </summary>
    public static bool TryImportFile(string path, out KeymapProfile profile, out string error)
    {
        profile = BuildDefault();
        error = "";
        try
        {
            return TryFromJson(File.ReadAllText(path), out profile, out error);
        }
        catch (Exception ex)
        {
            error = $"读文件失败：{ex.Message}";
            return false;
        }
    }

    // ================= JSON =================

    /// <summary>序列化成一个 JSON 文本。</summary>
    public string ToJson() => JsonSerializer.Serialize(this, KeymapJson.Default.KeymapProfile);

    /// <summary>
    /// 解析一份方案 JSON。格式不认识就抛 <see cref="KeymapFormatException"/>（消息是中文）。
    /// 界面导入要接住它，并保留原有设置。老文件的 <c>missingNote</c> 读到就丢，行为固定跳过。
    /// </summary>
    public static KeymapProfile FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new KeymapFormatException("文件是空的。");

        KeymapProfile? profile;
        try
        {
            profile = JsonSerializer.Deserialize(json, KeymapJson.Default.KeymapProfile);
        }
        catch (JsonException ex)
        {
            throw new KeymapFormatException("不是有效的 JSON：" + ex.Message, ex);
        }

        if (profile == null)
            throw new KeymapFormatException("文件里没有方案内容。");
        if (profile.Version > CurrentVersion)
            throw new KeymapFormatException($"方案版本 {profile.Version} 高于本程序支持的 {CurrentVersion}。");
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new KeymapFormatException("方案缺少名称。");
        if (profile.Keys == null || profile.Keys.Count == 0)
            throw new KeymapFormatException("方案里一个主键都没有。");

        profile.Keys.RemoveAll(k => k == null);
        if (profile.Keys.Count == 0)
            throw new KeymapFormatException("方案里的主键都不可用。");

        foreach (var k in profile.Keys)
        {
            k.Key = (k.Key ?? "").Trim();
            if (k.Key.Length == 0)
                throw new KeymapFormatException("方案里有主键没写键名。");
            if (!IsKnownKeyName(k.Key))
                throw new KeymapFormatException($"不认识的键名「{k.Key}」。");
        }

        foreach (var (name, label) in new[]
                 {
                     (profile.OctaveUp, "octaveUp"),
                     (profile.OctaveDown, "octaveDown"),
                     (profile.Sharp, "sharp"),
                     (profile.Flat, "flat"),
                 })
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!IsKnownKeyName(name))
                throw new KeymapFormatException($"{label} 的键名「{name}」不认识。");
        }

        profile.Version = profile.Version <= 0 ? CurrentVersion : profile.Version;
        profile.BaseNote = Math.Clamp(profile.BaseNote, 0, 127);
        // minNote / maxNote 只做兼容保留：夹一下范围就放着，音域一律由键位推导，不再据此报错。
        if (profile.MinNote.HasValue) profile.MinNote = Math.Clamp(profile.MinNote.Value, 0, 127);
        if (profile.MaxNote.HasValue) profile.MaxNote = Math.Clamp(profile.MaxNote.Value, 0, 127);
        return profile;
    }

    /// <summary>解析一份方案 JSON，失败时用中文说明原因，不抛异常。</summary>
    public static bool TryFromJson(string json, out KeymapProfile profile, out string error)
    {
        try
        {
            profile = FromJson(json);
            error = "";
            return true;
        }
        catch (KeymapFormatException ex)
        {
            profile = BuildDefault();
            error = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            profile = BuildDefault();
            error = "解析失败：" + ex.Message;
            return false;
        }
    }

    // ================= 音域（由键位推导） =================
    //
    // 能弹的音高集合 = 所有键位在基准音与修饰键作用下实际能到达的音高：
    //   音高 = BaseNote + 键偏移 + 12 × 八度档（有升/降八度键才有这一档）
    //   升半音键再 +1。
    // 集合的上下限就是音域，界面不再填 minNote / maxNote。
    // 功能键总开关（ModifiersEnabled）关掉时，八度档只有 0，也没有升半音键。

    /// <summary>基准音所在的八度编号（C4 = 4）。</summary>
    public int BaseOctave => BaseNote / 12 - 1;

    /// <summary>本方案能不能用修饰键（八度键与半音键）。总开关关掉就一律按没有处理。</summary>
    public bool CanUseOctaveUp => ModifiersEnabled && !string.IsNullOrWhiteSpace(OctaveUp);
    public bool CanUseOctaveDown => ModifiersEnabled && !string.IsNullOrWhiteSpace(OctaveDown);
    public bool CanUseSharp => ModifiersEnabled && !string.IsNullOrWhiteSpace(Sharp);
    public bool CanUseFlat => ModifiersEnabled && !string.IsNullOrWhiteSpace(Flat);

    /// <summary>可演奏最低音：所有键位配上八度键能到的最低音。</summary>
    public int ResolveMinNote() => ReachableExtent().Lo;

    /// <summary>可演奏最高音：所有键位配上八度键与升半音键能到的最高音。</summary>
    public int ResolveMaxNote() => ReachableExtent().Hi;

    /// <summary>能弹范围的上下限（含端点）。一个可用的键都没有时返回基准音。</summary>
    public (int Lo, int Hi) ReachableExtent()
    {
        if (!TryReachableBounds(out int lo, out int hi))
            return (Math.Clamp(BaseNote, 0, 127), Math.Clamp(BaseNote, 0, 127));
        if (lo > hi) (lo, hi) = (hi, lo);
        return (Math.Clamp(lo, 0, 127), Math.Clamp(hi, 0, 127));
    }

    /// <summary>把键位、八度键、升半音键铺开，算出能到达的最低与最高音高。</summary>
    private bool TryReachableBounds(out int lo, out int hi)
    {
        lo = 0;
        hi = 0;
        bool any = false;

        bool canUp = CanUseOctaveUp;
        bool canDown = CanUseOctaveDown;
        bool canSharp = CanUseSharp;
        bool canFlat = CanUseFlat;

        foreach (var k in Keys)
        {
            if (k == null || string.IsNullOrWhiteSpace(k.Key)) continue;
            if (!IsKnownKeyName(k.Key)) continue;

            for (int mod = -1; mod <= 1; mod++)
            {
                if (mod < 0 && !canDown) continue;
                if (mod > 0 && !canUp) continue;

                // 降半音键向下多到一个半音，升半音键向上多到一个半音
                int baseP = BaseNote + k.Offset + 12 * mod;
                int low = canFlat ? baseP - 1 : baseP;
                int high = canSharp ? baseP + 1 : baseP;
                if (!any) { lo = low; hi = high; any = true; continue; }
                if (low < lo) lo = low;
                if (high > hi) hi = high;
            }
        }
        return any;
    }

    /// <summary>
    /// 实际能弹出的音高（升序、去重）：每个键位配上可用的八度档与升半音键。
    /// 界面用这份列表铺音高下拉与「还有几个音没绑键」的统计。
    /// </summary>
    public IReadOnlyList<int> ReachablePitches()
    {
        var set = new SortedSet<int>();
        bool canUp = CanUseOctaveUp;
        bool canDown = CanUseOctaveDown;
        bool canSharp = CanUseSharp;
        bool canFlat = CanUseFlat;

        foreach (var k in Keys)
        {
            if (k == null || string.IsNullOrWhiteSpace(k.Key)) continue;
            if (!IsKnownKeyName(k.Key)) continue;

            for (int mod = -1; mod <= 1; mod++)
            {
                if (mod < 0 && !canDown) continue;
                if (mod > 0 && !canUp) continue;

                int low = BaseNote + k.Offset + 12 * mod;
                // s = 0 原键、+1 升半音、-1 降半音
                for (int s = -1; s <= 1; s++)
                {
                    if (s > 0 && !canSharp) continue;
                    if (s < 0 && !canFlat) continue;
                    int p = low + s;
                    if (p >= 0 && p <= 127) set.Add(p);
                }
            }
        }
        return set.ToList();
    }

    /// <summary>该音高是否在能弹范围内（含端点）。范围是上下限之间的区间；区间之外固定不弹。</summary>
    public bool InRange(int pitch)
    {
        var (lo, hi) = ReachableExtent();
        return pitch >= lo && pitch <= hi;
    }

    // ================= 查表 =================

    /// <summary>某个键名对应的半音偏移（取第一项）。找不到返回 false。</summary>
    public bool TryKeyByName(string keyName, out int semitoneOffset)
    {
        semitoneOffset = 0;
        if (string.IsNullOrWhiteSpace(keyName)) return false;
        foreach (var k in Keys)
        {
            if (k == null) continue;
            if (string.Equals(k.Key, keyName, StringComparison.OrdinalIgnoreCase))
            {
                semitoneOffset = k.Offset;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 把一个音高（方案基准八度下的绝对 MIDI 音高）映射到「按键 + 八度档 + 升半音」。
    ///
    /// 规则只有一条：**有键就发、没键就不发**。
    /// 范围外的音固定不弹（返回 false）；范围里的音精确命中键表才返回 true，
    /// 没有对应的键就直接不弹，不按任何策略改音高。
    ///
    /// 目标音相同的候选之间优先顺序固定：不用升半音键 → 键偏移小 → 八度档绝对值小 → 键表里靠前。
    /// 功能键总开关关掉时，八度档只有 0，也不会有升半音键。
    /// </summary>
    public bool TryKeyOfPitch(int pitch, out string key, out int octaveOffset, out bool sharp)
        => TryKeyOfPitch(pitch, out key, out octaveOffset, out sharp, out _, out _);

    /// <summary>同上，另给出实际发声音高（命中时与入参相同）。</summary>
    public bool TryKeyOfPitch(int pitch, out string key, out int octaveOffset, out bool sharp,
                              out int soundingPitch)
        => TryKeyOfPitch(pitch, out key, out octaveOffset, out sharp, out _, out soundingPitch);

    /// <summary>
    /// 完整版：另给出 <paramref name="flat"/>（要不要按住降半音键，与 <paramref name="sharp"/> 互斥）。
    /// 候选优先顺序固定：不用半音键 → 用升半音键 → 用降半音键 → 键偏移小 → 八度档绝对值小 → 键表里靠前。
    /// 也就是说同一个黑键「下方白键 + 升半音」与「上方白键 + 降半音」都能到时，固定用升半音；
    /// 降半音键负责的是：方案没绑升半音键时补半音，以及最低键下面那一个半音（升半音够不到）。
    /// </summary>
    public bool TryKeyOfPitch(int pitch, out string key, out int octaveOffset,
                              out bool sharp, out bool flat, out int soundingPitch)
    {
        key = "";
        octaveOffset = 0;
        sharp = false;
        flat = false;
        soundingPitch = pitch;
        if (Keys.Count == 0) return false;
        if (!InRange(pitch)) return false;   // 超出能弹范围：固定不弹

        int want = pitch;
        bool canSharp = CanUseSharp;
        bool canFlat = CanUseFlat;
        bool canUp = CanUseOctaveUp;
        bool canDown = CanUseOctaveDown;

        // 半音键优先级：0 不用、1 升半音、2 降半音（数值小的优先）
        static int RankOf(int s) => s == 0 ? 0 : (s > 0 ? 1 : 2);

        bool found = false;
        int bestRank = int.MaxValue;
        int bestOffset = int.MaxValue;
        int bestModAbs = int.MaxValue;
        int bestIndex = int.MaxValue;
        string bestKey = "";
        int bestMod = 0;
        int bestS = 0;

        for (int mod = -1; mod <= 1; mod++)
        {
            if (mod < 0 && !canDown) continue;
            if (mod > 0 && !canUp) continue;

            for (int i = 0; i < Keys.Count; i++)
            {
                var kb = Keys[i];
                if (kb == null || string.IsNullOrEmpty(kb.Key)) continue;
                if (KeyCharOf(kb.Key) == '\0') continue;   // 认不出的键名不参与映射

                int basePitch = BaseNote + kb.Offset + 12 * mod;
                for (int s = -1; s <= 1; s++)
                {
                    if (s > 0 && !canSharp) continue;
                    if (s < 0 && !canFlat) continue;
                    int p = basePitch + s;
                    if (p != want) continue;               // 只认精确命中：没键就不发声

                    int rank = RankOf(s);
                    bool better =
                        !found ||
                        rank < bestRank ||
                        (rank == bestRank && kb.Offset < bestOffset) ||
                        (rank == bestRank && kb.Offset == bestOffset && Math.Abs(mod) < bestModAbs) ||
                        (rank == bestRank && kb.Offset == bestOffset && Math.Abs(mod) == bestModAbs && i < bestIndex);
                    if (!better) continue;

                    found = true;
                    bestRank = rank;
                    bestOffset = kb.Offset;
                    bestModAbs = Math.Abs(mod);
                    bestIndex = i;
                    bestKey = kb.Key;
                    bestMod = mod;
                    bestS = s;
                }
            }
        }

        if (!found) return false;

        key = bestKey;
        octaveOffset = bestMod;
        sharp = bestS > 0;
        flat = bestS < 0;
        soundingPitch = want;
        return true;
    }

    /// <summary>复制一份（键表也复制），用于编辑内置预设前的拷贝。</summary>
    public KeymapProfile Clone()
    {
        var copy = new KeymapProfile
        {
            Version = Version,
            Name = Name,
            Author = Author,
            Description = Description,
            BaseNote = BaseNote,
            OctaveUp = OctaveUp,
            OctaveDown = OctaveDown,
            Sharp = Sharp,
            Flat = Flat,
            ModifiersEnabled = ModifiersEnabled,
            MinNote = MinNote,
            MaxNote = MaxNote,
            OutOfRange = OutOfRange,
        };
        foreach (var k in Keys)
        {
            if (k == null) continue;
            copy.Keys.Add(new KeyBinding { Key = k.Key, Offset = k.Offset, Row = k.Row });
        }
        return copy;
    }

    // ================= 键名表 =================
    //
    // 键名到字符是「多对一」：单字符键（字母、数字、标点）用自身；
    // 命名键（PageUp、MouseLeft、F1、NumPad3…）用私用区哨兵字符，一个键名一个码位。
    // 这样播放引擎仍可以用 char 传递按键（PlaybackEngine 的 PhysicalEvent.Code），
    // 而 InputSender / MacroExporter 能把哨兵还原成真正的键名。

    private const char NamedKeyBase = '\uE000';

    /// <summary>命名键（非单字符）。下标 + <see cref="NamedKeyBase"/> 就是它的哨兵字符。</summary>
    private static readonly string[] NamedKeys =
    {
        "Space", "Enter", "Tab", "Back", "Escape", "Shift", "Ctrl", "Alt",
        "PageUp", "PageDown", "Home", "End", "Insert", "Delete",
        "Up", "Down", "Left", "Right",
        "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
        "NumPad0", "NumPad1", "NumPad2", "NumPad3", "NumPad4",
        "NumPad5", "NumPad6", "NumPad7", "NumPad8", "NumPad9",
        "MouseLeft", "MouseRight", "MouseMiddle",
    };

    /// <summary>允许出现在方案里的别名（统一按小写比较）。</summary>
    private static readonly Dictionary<string, string> KeyAlias = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Backspace"] = "Back",
        ["Esc"] = "Escape",
        ["Return"] = "Enter",
        ["PgUp"] = "PageUp",
        ["PgDn"] = "PageDown",
        ["UpArrow"] = "Up",
        ["DownArrow"] = "Down",
        ["LeftArrow"] = "Left",
        ["RightArrow"] = "Right",
        ["LShift"] = "Shift",
        ["RShift"] = "Shift",
        ["LCtrl"] = "Ctrl",
        ["RCtrl"] = "Ctrl",
        ["LAlt"] = "Alt",
        ["RAlt"] = "Alt",
        ["LeftMouse"] = "MouseLeft",
        ["RightMouse"] = "MouseRight",
        ["MiddleMouse"] = "MouseMiddle",
    };

    /// <summary>可单独作为键名的标点（与界面能录入的键一致）。</summary>
    private const string SingleCharKeys = ",.;/'\\[]-=`";

    /// <summary>
    /// 可单独作为键名的全部字符，共 47 个：26 个字母 + 10 个数字 + 11 个标点（<see cref="SingleCharKeys"/>）。
    /// 界面的「+ 加一行」按这个池子挑空闲键；池子用尽时界面不再写空键名，而是提示并中止。
    /// </summary>
    public const string SingleCharPool = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789" + SingleCharKeys;

    /// <summary>归一化键名：大小写、别名。认不出返回空串。</summary>
    public static string CanonicalKeyName(string? keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName)) return "";
        string s = keyName.Trim();

        if (s.Length == 1)
        {
            char c = s[0];
            if (c is >= 'a' and <= 'z') return char.ToUpperInvariant(c).ToString();
            if (c is >= 'A' and <= 'Z') return s;
            if (c is >= '0' and <= '9') return s;
            return SingleCharKeys.Contains(c) ? s : "";
        }

        if (KeyAlias.TryGetValue(s, out string? alias)) s = alias;
        foreach (string n in NamedKeys)
            if (string.Equals(n, s, StringComparison.OrdinalIgnoreCase)) return n;
        return "";
    }

    /// <summary>键名是否可用（单字符键或已知命名键）。</summary>
    public static bool IsKnownKeyName(string? keyName) => CanonicalKeyName(keyName).Length > 0;

    /// <summary>
    /// 键名 → 单字符。单字符键返回自身；命名键返回哨兵字符；认不出返回 '\0'。
    /// </summary>
    public static char KeyCharOf(string? keyName)
    {
        string name = CanonicalKeyName(keyName);
        if (name.Length == 0) return '\0';
        if (name.Length == 1) return name[0];
        int i = Array.IndexOf(NamedKeys, name);
        return i < 0 ? '\0' : (char)(NamedKeyBase + i);
    }

    /// <summary>单字符 → 键名。哨兵字符还原成命名键；其余按原样返回。</summary>
    public static string NameOfKeyChar(char c)
    {
        if (c == '\0') return "";
        if (c >= NamedKeyBase && c < NamedKeyBase + NamedKeys.Length) return NamedKeys[c - NamedKeyBase];
        return c.ToString();
    }
}

/// <summary>
/// 方案 JSON 的源生成上下文。读写走它，不用 JsonSerializer 的反射重载：
/// 当前发布没开裁剪，但源生成更快，也不会因为将来重新开裁剪而失效。
/// 字段名按设计文档的字段表写成 camelCase（version / name / baseNote / keys / octaveUp …）；
/// 读的时候大小写不敏感，手写 "BaseNote" 也能读进来。
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(KeymapProfile))]
internal sealed partial class KeymapJson : JsonSerializerContext
{
}

/// <summary>
/// <c>outOfRange</c> 写成小写字符串 <c>drop</c> / <c>fold</c>（设计文档的字段表），
/// 读的时候大小写不敏感，也接受 0/1 两个旧数字写法。
/// </summary>
internal sealed class OutOfRangeModeConverter : JsonConverter<OutOfRangeMode>
{
    public override OutOfRangeMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
            return reader.GetInt32() == 1 ? LegacyFold() : OutOfRangeMode.Drop;
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("outOfRange 只能写 drop 或 fold。");
        string s = reader.GetString() ?? "";
        if (s.Equals("drop", StringComparison.OrdinalIgnoreCase)) return OutOfRangeMode.Drop;
        if (s.Equals("fold", StringComparison.OrdinalIgnoreCase)) return LegacyFold();
        throw new JsonException($"outOfRange 只能写 drop 或 fold，收到「{s}」。");
    }

    /// <summary>老文件的 fold 保留原值，但新版本固定不弹；写一条日志说明。</summary>
    private static OutOfRangeMode LegacyFold()
    {
        LogFile.Append("[键位] outOfRange 旧值 fold 已停用：超出能弹范围的音固定不弹，不再折八度。");
        return OutOfRangeMode.Fold;
    }

    public override void Write(Utf8JsonWriter writer, OutOfRangeMode value, JsonSerializerOptions options)
        => writer.WriteStringValue(value == OutOfRangeMode.Fold ? "fold" : "drop");
}

/// <summary>
/// 【兼容保留，界面不再提供】老文件里的 <c>missingNote</c>：<c>skip</c> / <c>up</c> / <c>down</c>，
/// 还认得更老的 <c>snap</c>（读成 up）与 <c>drop</c>（读成 skip），以及 0/1 数字写法。
///
/// 读进来的值一律写成 <see cref="MissingNoteMode.Skip"/>：行为已固定为「有键就发、没键就不发」。
/// 因为 <see cref="KeymapProfile.MissingNote"/> 打了 <see cref="JsonIgnoreAttribute"/>，
/// 这个转换器只可能在读老文件时被调用，新文件里不再出现这个字段。
/// </summary>
internal sealed class MissingNoteModeConverter : JsonConverter<MissingNoteMode>
{
    public override MissingNoteMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            reader.GetInt32();
            LogFile.Append("[键位] missingNote 旧数字写法已停用：缺的音固定跳过，不再改音高。");
            return MissingNoteMode.Skip;
        }
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("missingNote 只能写 skip、up 或 down。");

        string s = reader.GetString() ?? "";
        if (s.Equals("skip", StringComparison.OrdinalIgnoreCase)
            || s.Equals("up", StringComparison.OrdinalIgnoreCase)
            || s.Equals("down", StringComparison.OrdinalIgnoreCase)
            || s.Equals("snap", StringComparison.OrdinalIgnoreCase)
            || s.Equals("drop", StringComparison.OrdinalIgnoreCase))
        {
            if (!s.Equals("skip", StringComparison.OrdinalIgnoreCase))
                LogFile.Append($"[键位] missingNote 旧值 {s} 已停用：缺的音固定跳过，不再改音高。");
            return MissingNoteMode.Skip;
        }
        throw new JsonException($"missingNote 只能写 skip、up 或 down，收到「{s}」。");
    }

    public override void Write(Utf8JsonWriter writer, MissingNoteMode value, JsonSerializerOptions options)
        => writer.WriteStringValue("skip");
}

/// <summary>
/// <c>keys</c> 里每项写成数组 <c>["Z",0,0]</c>（键名、半音偏移、第几行），不是对象。
/// 老文件的两元素写法 <c>["Z",0]</c> 照样读，行号默认 0。
/// 放在 <see cref="KeyBinding"/> 类型上，源生成器直接按它读写列表元素。
/// </summary>
internal sealed class KeyBindingConverter : JsonConverter<KeyBinding>
{
    public override KeyBinding Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("keys 里每一项要写成 [\"键名\", 半音偏移, 第几行] 数组。");

        if (!reader.Read() || reader.TokenType != JsonTokenType.String)
            throw new JsonException("keys 里每一项的第一个值要是键名字符串。");
        string name = reader.GetString() ?? "";

        if (!reader.Read())
            throw new JsonException("keys 里的项不完整。");
        int offset;
        if (reader.TokenType == JsonTokenType.Number)
        {
            if (!reader.TryGetInt32(out offset))
                throw new JsonException($"键「{name}」的半音偏移要写整数。");
        }
        else
        {
            throw new JsonException($"键「{name}」的半音偏移要写整数。");
        }

        // 第三项是行号。老文件没有这一项，读成 0（界面会退回按物理键盘的排分组）。
        if (!reader.Read())
            throw new JsonException("keys 里的项不完整。");
        int row = 0;
        if (reader.TokenType == JsonTokenType.Number)
        {
            if (!reader.TryGetInt32(out row))
                throw new JsonException($"键「{name}」的行号要写整数。");
        }

        if (reader.TokenType != JsonTokenType.EndArray)
        {
            if (!reader.Read())
                throw new JsonException("keys 里的项不完整。");
            while (reader.TokenType != JsonTokenType.EndArray)
            {
                if (!reader.Read()) throw new JsonException("keys 里的项不完整。");   // 多余项忽略
            }
        }

        return new KeyBinding { Key = name, Offset = offset, Row = row };
    }

    public override void Write(Utf8JsonWriter writer, KeyBinding value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteStringValue(value.Key);
        writer.WriteNumberValue(value.Offset);
        writer.WriteNumberValue(value.Row);
        writer.WriteEndArray();
    }
}

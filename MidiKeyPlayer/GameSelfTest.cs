using System.Text;
using MidiKeyPlayer.Engine;
using MidiKeyPlayer.Midi;

namespace MidiKeyPlayer;

/// <summary>
/// 【开发用】纯逻辑自检：键位方案（内置预设、键名、绑定的键）与简谱换算。
///
/// 用法：设环境变量 <c>MIDIKEY_GAME_SELFTEST=1</c> 启动程序，或在值里给一个输出文件路径（以 .txt 结尾）：
/// <c>MIDIKEY_GAME_SELFTEST=E:\tmp\keymap-selftest.txt</c>。
/// 不写路径就写到 <c>%TEMP%\midikey-keymap-selftest.txt</c>。
/// 取值 <c>0</c> / <c>false</c> / <c>no</c> / <c>off</c> 等于没设，程序照常开窗。
///
/// 环境变量名带 GAME 是历史原因，不改：改名会让 run-selftest.ps1 与既有用法一起失效。
///
/// 走这条路时程序**不创建窗口、不注册热键、不碰按键与 MIDI 设备**，跑完直接退出，
/// 退出码 0 = 全过，1 = 有用例失败。和 DevUISnapshot / DevPreviewProbe 一样属于可删的开发件。
/// </summary>
internal static class GameSelfTest
{
    public const string EnvVar = "MIDIKEY_GAME_SELFTEST";

    private static readonly List<string> Lines = new();
    private static int _failed;

    public static bool Requested
    {
        get
        {
            string v = Environment.GetEnvironmentVariable(EnvVar) ?? "";
            return v.Length > 0 && !IsOffValue(v);
        }
    }

    /// <summary>
    /// 「明确关掉」的取值：0 / false / no / off（不分大小写）。
    /// <c>MIDIKEY_GAME_SELFTEST=0</c> 以前也会让程序不建窗口直接退出，与仓库其它开关的 <c>=="1"</c> 约定不一致。
    /// </summary>
    private static bool IsOffValue(string value)
    {
        string v = value.Trim();
        return v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase)
            || v.Equals("no", StringComparison.OrdinalIgnoreCase)
            || v.Equals("off", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>环境变量给出的自定义报告路径（没有就是空串）。</summary>
    private static string CustomPath(string value)
    {
        string v = value.Trim();
        // "1" 是约定的「开」，没有路径含义
        if (v == "1" || IsOffValue(v)) return "";
        return v.Contains('\\') || v.Contains('/') ? v : "";
    }

    /// <summary>跑完全部用例，返回进程退出码。</summary>
    public static int Run()
    {
        string value = Environment.GetEnvironmentVariable(EnvVar) ?? "";
        string custom = CustomPath(value);
        string path = custom.Length > 0
            ? custom
            : Path.Combine(Path.GetTempPath(), "midikey-keymap-selftest.txt");
        // 报告一律写成 .txt：环境变量驱动的路径只在开发机上用，避免写出奇怪的后缀
        if (!path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            path = Path.Combine(Path.GetTempPath(), "midikey-keymap-selftest.txt");

        Lines.Add($"键位自检 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Lines.Add($"键位方案：{KeymapProfile.Default.Name}；内置预设 {KeymapProfile.Presets.Count} 套");
        Lines.Add("");

        try
        {
            TestPresetBinding();
            TestKeyMapNames();
            TestSolfegeNames();
            TestFlatModifier();
            TestRobloxPiano();
        }
        catch (Exception ex)
        {
            _failed++;
            Lines.Add("FAIL 自检抛异常：" + ex);
        }

        Lines.Add("");
        Lines.Add(_failed == 0 ? "结果：全部通过" : $"结果：{_failed} 项失败");

        string text = string.Join(Environment.NewLine, Lines);
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
        }
        catch { /* 写不出去也要把退出码给对 */ }
        try { Console.WriteLine(text); } catch { }

        return _failed == 0 ? 0 : 1;
    }

    // ================= 内置方案 =================

    /// <summary>
    /// 内置预设的自检：套数下限、名字唯一且取得回、每套的键名与偏移不重复。
    ///
    /// 套数只断言下限、不写死：预设正在被改（6 套 → 3 套），写死数字一改就红。
    /// 「一个八度的半音阶方案」这类具体配置同样随预设一起改过，不再是硬要求，
    /// 只在方案存在时检查它内部自洽。
    /// </summary>
    private static void TestPresetBinding()
    {
        var presets = KeymapProfile.Presets;
        Check("预设：至少 3 套内置方案", presets.Count >= 3, $"实际 {presets.Count}");

        var names = new List<string>();
        int emptyName = 0;
        int dupName = 0;
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in presets)
        {
            if (string.IsNullOrWhiteSpace(p.Name)) { emptyName++; continue; }
            names.Add(p.Name);
            if (!seenNames.Add(p.Name)) dupName++;
        }
        Check("预设：每套都有名字", emptyName == 0, $"没名字 {emptyName} 套");
        Check("预设：名字两两不同", dupName == 0, $"重名 {dupName} 套");

        int dupKey = 0;
        int emptyKeys = 0;
        foreach (var p in presets)
        {
            var keys = p.Keys.Where(k => k != null && !string.IsNullOrWhiteSpace(k.Key))
                             .Select(k => KeymapProfile.CanonicalKeyName(k.Key))
                             .ToList();
            if (keys.Count == 0) emptyKeys++;
            if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Count) dupKey++;
        }
        Check("预设：每套都有按键", emptyKeys == 0, $"空方案 {emptyKeys} 套");
        Check("预设：每套的键名都不重复", dupKey == 0, $"重键 {dupKey} 套");

        // 界面换方案走的就是这条查表路径：名字取得回来，才能真的切过去
        var notFound = names.Where(n => KeymapProfile.PresetByName(n) == null).ToList();
        Check("预设：每套都能按名字取回", notFound.Count == 0, string.Join(" | ", notFound));

        // 默认方案是内置方案时也要能查回来，否则「回到默认」这条路径会落空。
        // 默认方案允许不是预设（用户自建方案可以当默认），所以用「取不回才失败」的写法。
        string defaultName = KeymapProfile.Default.Name;
        bool defaultPresent = names.Any(n => string.Equals(n, defaultName, StringComparison.Ordinal));
        Check("预设：默认方案的名字取得回",
              !string.IsNullOrWhiteSpace(defaultName)
              && (!defaultPresent || KeymapProfile.PresetByName(defaultName) != null),
              $"默认「{defaultName}」；内置：{string.Join(" | ", names)}");

        // 半音阶方案（偏移从 0 起连续覆盖 12 个半音）存在时，一个偏移只配一个键
        var chromatic = presets.Where(p => Covers(p, 12)).ToList();
        bool oneOffsetPerKey = chromatic.All(p =>
            p.Keys.Where(k => k != null).Select(k => k.Offset).Distinct().Count()
            == p.Keys.Count(k => k != null));
        Check("预设：半音阶方案里一个偏移只占一个键", oneOffsetPerKey,
              $"半音阶方案 {chromatic.Count} 套");
    }

    /// <summary>这套方案的偏移是否从 0 起连续覆盖 n 个半音。</summary>
    private static bool Covers(KeymapProfile p, int n)
    {
        var offsets = new HashSet<int>(p.Keys.Where(k => k != null).Select(k => k.Offset));
        for (int i = 0; i < n; i++) if (!offsets.Contains(i)) return false;
        return true;
    }

    // ================= 键名 =================

    /// <summary>
    /// 键名这条链路：方案认不认这个名字（<see cref="KeymapProfile.IsKnownKeyName"/>），
    /// 以及键名能不能换成字符（<see cref="KeymapProfile.KeyCharOf"/>）。每个键名都必须是方案认得的。
    ///
    /// 这里不再自己写一份「Avalonia 按键 → 键名」的翻译。原来那份是 Input\GameKeyMap.cs
    /// （音游认键用）搬过来的副本，只被自检调用，断言的是它自己，覆盖不到产品代码：
    /// 生产里录制按键走 KeymapWindow 的 KeyLabelOf / ModifierNameOf，键名归一化走
    /// <see cref="KeymapProfile.CanonicalKeyName"/>。副本和依赖它的断言一起删掉，
    /// 原因与取舍见 midikey-audit\fixes\42-leftovers.md（LF-09）。
    /// </summary>
    private static void TestKeyMapNames()
    {
        // 鼠标键要能被方案认出来，否则鼠标修饰键永远匹配不上
        Check("键名：鼠标三键在方案里可用",
              IsUsableName("MouseLeft") && IsUsableName("MouseRight") && IsUsableName("MouseMiddle"),
              "MouseLeft / MouseRight / MouseMiddle");

        // 内置三套预设都不带功能键：这是设计（不是所有方案都需要，界面另有启用开关）。
        // 所以这里断言「空着，或者写的是认得的键名」，而不是强制三个都非空。
        var def = KeymapProfile.Default;
        Check("键名：方案里的功能键要么留空、要么认得",
              IsBlankOrKnown(def.OctaveDown) && IsBlankOrKnown(def.OctaveUp) && IsBlankOrKnown(def.Sharp),
              $"「{def.OctaveDown}」「{def.OctaveUp}」「{def.Sharp}」");

        bool modsEmpty = string.IsNullOrWhiteSpace(def.OctaveUp)
                         && string.IsNullOrWhiteSpace(def.OctaveDown)
                         && string.IsNullOrWhiteSpace(def.Sharp);
        Check("键名：功能键开关打开时必须有功能键", !def.ModifiersEnabled || !modsEmpty,
              $"开关={def.ModifiersEnabled} 三个都空={modsEmpty}");
        Check("键名：没有功能键的方案开关是关的", !modsEmpty || !def.ModifiersEnabled,
              $"开关={def.ModifiersEnabled}");

        // 键名 → 键名字符：命名键走私用区哨兵，单字符键原样返回
        Check("键名：命名字符是私用区哨兵",
              KeymapProfile.KeyCharOf("PageUp") != '\0' && KeymapProfile.KeyCharOf("PageUp") != 'P',
              $"实际 U+{(int)KeymapProfile.KeyCharOf("PageUp"):X4}");
        Check("键名：单字符键原样返回字符",
              KeymapProfile.KeyCharOf("Z") == 'Z' && KeymapProfile.KeyCharOf(",") == ',',
              "Z=" + KeymapProfile.KeyCharOf("Z") + " ,=" + KeymapProfile.KeyCharOf(","));
    }

    /// <summary>键名可用：非空、方案认得、能换出键名字符。</summary>
    private static bool IsUsableName(string name)
        => name.Length > 0 && KeymapProfile.IsKnownKeyName(name) && KeymapProfile.KeyCharOf(name) != '\0';

    /// <summary>功能键位置允许留空（方案不一定需要功能键）；填了就必须是认得的键名。</summary>
    private static bool IsBlankOrKnown(string? name)
        => string.IsNullOrWhiteSpace(name) || IsUsableName(KeymapProfile.CanonicalKeyName(name));

    // ================= 简谱换算 =================

    /// <summary>
    /// 简谱音高换算：中音区只有数字，高八度加上点，低八度加下点，最多两个点。
    /// 音域文字（SolfegeRange）与音名（NoteName）一起验，界面上到处在用。
    /// </summary>
    private static void TestSolfegeNames()
    {
        // 中音区（60 起）：do re mi fa sol la si = 1 2 3 4 5 6 7
        string[] middle = { "1", "2", "3", "4", "5", "6", "7" };
        bool middleOk = true;
        string middleBad = "";
        int[] degrees = { 60, 62, 64, 65, 67, 69, 71 };
        for (int i = 0; i < degrees.Length; i++)
        {
            string got = Music.SolfegeName(degrees[i]);
            if (got != middle[i]) { middleOk = false; middleBad += $"{degrees[i]}→「{got}」应为「{middle[i]}」 "; }
        }
        Check("简谱：中音区 1..7 对得上", middleOk, middleBad);

        // 中音 do 不带八度点
        Check("简谱：中音 do（60）= 1", Music.SolfegeName(60) == "1", $"实际「{Music.SolfegeName(60)}」");

        // 上点（U+02D9）是间距字符，界面的字体有字形；组合字符会渲染成豆腐块
        Check("简谱：高八度 do（72）= 1 加上点",
              Music.SolfegeName(72) == "1\u02D9", $"实际「{Music.SolfegeName(72)}」");
        Check("简谱：低八度 do（48）= 1 加下点",
              Music.SolfegeName(48) == "1.", $"实际「{Music.SolfegeName(48)}」");
        Check("简谱：八度点最多两个",
              Music.SolfegeName(96) == "1\u02D9\u02D9" && Music.SolfegeName(24) == "1..",
              $"96→「{Music.SolfegeName(96)}」 24→「{Music.SolfegeName(24)}」");

        // 升降号：61 = #1
        Check("简谱：升号音（61）= #1", Music.SolfegeName(61) == "#1", $"实际「{Music.SolfegeName(61)}」");

        // 音名与音域文字
        Check("简谱：音名 60 = C4", Music.NoteName(60) == "C4", $"实际「{Music.NoteName(60)}」");
        Check("简谱：音域低到高",
              Music.SolfegeRange(72, 60) == "1~1\u02D9",
              $"实际「{Music.SolfegeRange(72, 60)}」");

        // 预生成的查表结果必须与现算的一致（配表用，下标就是音高）
        Check("简谱：查表 SolfegeNames 与现算一致",
              Music.SolfegeNames.Length == 128 && Music.SolfegeNames[60] == "1"
              && Music.SolfegeNames[72] == "1\u02D9",
              $"长度 {Music.SolfegeNames.Length}");
    }

    // ================= 降半音键 =================

    /// <summary>
    /// 降半音键（v1.0.14 新增）：「21 键半音」预设有 Shift 升半音 + Ctrl 降半音；
    /// 黑键两条路都能到时固定优先升半音；最低键下面那一个半音只能用降半音；
    /// 只绑降半音键的方案用「上方邻键 + 降半音」补黑键。
    /// v1.0.15 把短命的「异环键位」并入「21 键半音」，老名字走别名表。
    /// </summary>
    private static void TestFlatModifier()
    {
        var p = KeymapProfile.PresetByName("21 键半音");
        Check("降半音：21 键半音预设存在且半音键绑对",
              p != null && p.Sharp == "Shift" && p.Flat == "Ctrl" && p.ModifiersEnabled,
              p == null ? "取不到" : $"Sharp=「{p.Sharp}」 Flat=「{p.Flat}」 开关={p.ModifiersEnabled}");
        if (p == null) return;

        // 改名合并：v1.0.14 的「异环键位」要落到合并后的「21 键半音」上
        var alias = KeymapProfile.PresetByName("异环键位");
        Check("降半音：老名字「异环键位」落到 21 键半音",
              alias != null && alias.Name == "21 键半音",
              alias == null ? "取不到" : $"落到「{alias.Name}」");

        // 音域：低音 do（48）被 Ctrl 向下多扩一个半音到 47；最高仍是高音 si+升半音 = 84
        Check("降半音：音域向下多扩一个半音",
              p.ResolveMinNote() == 47 && p.ResolveMaxNote() == 84,
              $"实际 {p.ResolveMinNote()}..{p.ResolveMaxNote()}");

        // 黑键（61 = 中音 #1）：升半音优先 → A(60) + Shift，不用 Ctrl
        bool okSharp = p.TryKeyOfPitch(61, out string k61, out _, out bool s61, out bool f61, out _)
                       && k61 == "A" && s61 && !f61;
        Check("降半音：黑键固定优先升半音", okSharp, $"61 → {k61} 升={s61} 降={f61}");

        // 最低键下面那一个半音（47）：升半音够不到，用 低音 do(Z, 48) + Ctrl
        bool okLow = p.TryKeyOfPitch(47, out string k47, out _, out bool s47, out bool f47, out _)
                     && k47 == "Z" && !s47 && f47;
        Check("降半音：最低键下面半音用降半音", okLow, $"47 → {k47} 升={s47} 降={f47}");

        // 只绑降半音键的方案：黑键用「上方邻键 + 降半音」（61 = 中音 re(S, 62) + Ctrl）
        var onlyFlat = p.Clone();
        onlyFlat.Sharp = null;
        bool okOnly = onlyFlat.TryKeyOfPitch(61, out string k61b, out _, out bool s61b, out bool f61b, out _)
                      && k61b == "S" && !s61b && f61b;
        Check("降半音：没绑升半音时用上方邻键", okOnly, $"61 → {k61b} 升={s61b} 降={f61b}");
    }

    // ================= Roblox 钢琴键位 =================

    /// <summary>
    /// Roblox 钢琴键位（v1.0.27 新增）：四排 36 个白键（C2..C7）+ 25 个黑键，
    /// 黑键固定是「按住 Shift + 下面那个白键」，与目标程序的虚拟钢琴一致。
    /// 白键表与黑键表逐条写在这里：偏移改错一个音，这一条就红。
    /// </summary>
    private static void TestRobloxPiano()
    {
        var p = KeymapProfile.PresetByName("Roblox 钢琴键位");
        Check("Roblox 钢琴：预设存在、36 个白键、Shift 升半音、不绑八度键",
              p != null && p.Keys.Count == 36 && p.Sharp == "Shift" && p.Flat == null
              && p.ModifiersEnabled && p.OctaveUp == null && p.OctaveDown == null,
              p == null ? "取不到" : $"键 {p.Keys.Count} 个；Sharp=「{p.Sharp}」");
        if (p == null) return;

        // 白键：键名 → 音高。C2 = 36 起，自然音逐个往上，四排连成一条。
        var white = new (string Key, int Pitch)[]
        {
            ("1", 36), ("2", 38), ("3", 40), ("4", 41), ("5", 43), ("6", 45), ("7", 47),
            ("8", 48), ("9", 50), ("0", 52),
            ("Q", 53), ("W", 55), ("E", 57), ("R", 59), ("T", 60), ("Y", 62), ("U", 64),
            ("I", 65), ("O", 67), ("P", 69),
            ("A", 71), ("S", 72), ("D", 74), ("F", 76), ("G", 77), ("H", 79), ("J", 81),
            ("K", 83), ("L", 84),
            ("Z", 86), ("X", 88), ("C", 89), ("V", 91), ("B", 93), ("N", 95), ("M", 96),
        };
        string badWhite = "";
        foreach (var (key, pitch) in white)
            if (!p.TryKeyOfPitch(pitch, out string got, out _, out bool sharp)
                || got != key || sharp)
                badWhite += $"{pitch}→「{got}」(升={sharp}) 应为「{key}」 ";
        Check("Roblox 钢琴：36 个白键都对上", badWhite.Length == 0, badWhite);

        // 黑键：Shift + 下面那个白键。E、B 与最高的 C7 上面没有黑键，所以是 25 个而不是 36 个。
        var black = new (string Key, int Pitch)[]
        {
            ("1", 37), ("2", 39), ("4", 42), ("5", 44), ("6", 46), ("8", 49), ("9", 51),
            ("Q", 54), ("W", 56), ("E", 58), ("T", 61), ("Y", 63), ("I", 66), ("O", 68), ("P", 70),
            ("S", 73), ("D", 75), ("G", 78), ("H", 80), ("J", 82), ("L", 85),
            ("Z", 87), ("C", 90), ("V", 92), ("B", 94),
        };
        string badBlack = "";
        foreach (var (key, pitch) in black)
            if (!p.TryKeyOfPitch(pitch, out string got, out _, out bool sharp)
                || got != key || !sharp)
                badBlack += $"{pitch}→「{got}」(升={sharp}) 应为「{key}」+Shift ";
        Check("Roblox 钢琴：25 个黑键都是下面白键 + Shift", badBlack.Length == 0, badBlack);

        Check("Roblox 钢琴：音域 36..97（97 是 C7 上面那个半音，琴上没有键）",
              p.ResolveMinNote() == 36 && p.ResolveMaxNote() == 97,
              $"实际 {p.ResolveMinNote()}..{p.ResolveMaxNote()}");
    }

    // ================= 断言 =================

    private static void Check(string name, bool ok, string detail = "")
    {
        if (!ok) _failed++;
        Lines.Add((ok ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? $"   [{detail}]" : ""));
    }
}

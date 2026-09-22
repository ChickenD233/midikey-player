using System.Text;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
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
internal static partial class GameSelfTest
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
            TestFf14Piano();
            TestChordScheduling();
            TestChordMerge();
            TestTrackRoles();
            TestRemoteSync();
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
            // 口径是「按键 + 要不要 Shift」：自带 Shift 的键位（Roblox 钢琴的黑键）
            // 与它下面那个白键共用一个物理键，但发出去的字符不同，不算重键。
            var keys = p.Keys.Where(k => k != null && !string.IsNullOrWhiteSpace(k.Key))
                             .Select(k => (k.Shift ? "Shift+" : "") + KeymapProfile.CanonicalKeyName(k.Key))
                             .ToList();
            if (keys.Count == 0) emptyKeys++;
            if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Count) dupKey++;
        }
        Check("预设：每套都有按键", emptyKeys == 0, $"空方案 {emptyKeys} 套");
        Check("预设：每套的键名都不重复（带 Shift 的另算一条）", dupKey == 0, $"重键 {dupKey} 套");

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
    /// Roblox 钢琴键位（v1.0.27 新增，v1.0.28 改成「一个半音一条键位」）：
    /// 36 个白键 + 25 个黑键 = 61 条，黑键是「下面那个白键 + Shift」，显示成 ! @ $ % ^ * ( 与 Q W E …。
    /// 不用功能键区（开关关、四个功能键全空），但 Shift 仍然要按。
    /// 白键表与黑键表逐条写在这里：偏移改错一个音，这一条就红。
    /// </summary>
    private static void TestRobloxPiano()
    {
        var p = KeymapProfile.PresetByName("Roblox 钢琴键位");
        Check("Roblox 钢琴：61 条键位、功能键全关、不用升半音键",
              p != null && p.Keys.Count == 61 && p.Keys.Count(k => k.Shift) == 25
              && !p.ModifiersEnabled && p.Sharp == null && p.OctaveUp == null
              && p.OctaveDown == null && p.Flat == null,
              p == null ? "取不到" : $"键 {p.Keys.Count} 条（带 Shift {p.Keys.Count(k => k.Shift)} 条）"
                                      + $"；开关={p.ModifiersEnabled} Sharp=「{p.Sharp}」");
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
                badWhite += $"{pitch}→「{got}」(Shift={sharp}) 应为「{key}」 ";
        Check("Roblox 钢琴：36 个白键都对上", badWhite.Length == 0, badWhite);

        // 黑键：同一个白键 + Shift。E、B 与最高的 C7 上面没有黑键，所以是 25 条。
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
                badBlack += $"{pitch}→「{got}」(Shift={sharp}) 应为「{key}」+Shift ";
        Check("Roblox 钢琴：25 个黑键都是下面白键 + Shift", badBlack.Length == 0, badBlack);

        // 显示名：黑键在界面上要写成游戏里的那个字符。
        Check("Roblox 钢琴：黑键显示名是 ! @ * Q 这种上位字符",
              KeymapProfile.ShiftedNameOf("1") == "!" && KeymapProfile.ShiftedNameOf("8") == "*"
              && KeymapProfile.ShiftedNameOf("q") == "Q" && KeymapProfile.ShiftedNameOf(",") == "<",
              $"1→{KeymapProfile.ShiftedNameOf("1")} 8→{KeymapProfile.ShiftedNameOf("8")} "
              + $"q→{KeymapProfile.ShiftedNameOf("q")} ,→{KeymapProfile.ShiftedNameOf(",")}");

        Check("Roblox 钢琴：音域 36..96（C2..C7）",
              p.ResolveMinNote() == 36 && p.ResolveMaxNote() == 96,
              $"实际 {p.ResolveMinNote()}..{p.ResolveMaxNote()}");

        Check("Roblox 钢琴：演奏时要按住 Shift",
              p.SharpKeyToHold == "Shift" && p.HasSelfShiftKeys,
              $"SharpKeyToHold=「{p.SharpKeyToHold}」 自带 Shift={p.HasSelfShiftKeys}");

        // 方案 JSON 的第四项是 Shift 标记：写出去要写、读回来要还原，老文件（没有第四项）读成 false。
        var round = KeymapProfile.FromJson(p.ToJson());
        bool same = round.Keys.Count == p.Keys.Count;
        for (int i = 0; same && i < p.Keys.Count; i++)
            same = round.Keys[i].Key == p.Keys[i].Key
                && round.Keys[i].Offset == p.Keys[i].Offset
                && round.Keys[i].Row == p.Keys[i].Row
                && round.Keys[i].Shift == p.Keys[i].Shift;
        Check("Roblox 钢琴：方案 JSON 往返后 61 条键位一字不差", same,
              same ? "" : $"原 {p.Keys.Count} 条 / 回读 {round.Keys.Count} 条");

        var legacy = KeymapProfile.FromJson("{\"name\":\"老方案\",\"keys\":[[\"Z\",0,0],[\"X\",2,0]]}");
        Check("键位 JSON：没有第四项的老文件读成不按 Shift",
              legacy.Keys.Count == 2 && legacy.Keys.All(k => !k.Shift),
              string.Join(" | ", legacy.Keys.Select(k => k.ToString())));
    }

    // ================= FF14 钢琴键位 =================

    /// <summary>
    /// FF14 钢琴键位（本版新增）：演奏模式的 37 键，**一个半音一条键位**，不用功能键，也不按 Shift。
    /// 三排各 12 个半音（一行一个八度），最后再接一个最高的 do。
    /// 低八度 Z 1 X 2 C V 3 B 4 N 5 M、中八度 A 6 S 7 D F 8 G 9 H 0 J、高八度 K Y L U Q W I E O R P T、,
    /// 逐条写在这里：偏移或键名改错一个音，这一条就红。
    /// </summary>
    private static void TestFf14Piano()
    {
        var p = KeymapProfile.PresetByName("FF14 钢琴键位");
        Check("FF14 钢琴：37 条键位、功能键全关、不用 Shift",
              p != null && p.Keys.Count == 37 && p.Keys.Count(k => k.Shift) == 0
              && !p.ModifiersEnabled && p.Sharp == null && p.OctaveUp == null
              && p.OctaveDown == null && p.Flat == null && !p.HasSelfShiftKeys,
              p == null ? "取不到" : $"键 {p.Keys.Count} 条（带 Shift {p.Keys.Count(k => k.Shift)} 条）"
                                      + $"；开关={p.ModifiersEnabled} Sharp=「{p.Sharp}」");
        if (p == null) return;

        // 全部 37 个音：音高 → 键名。60..96 一个半音一条键位，音高连成一条不断档。
        var table = new (string Key, int Pitch)[]
        {
            ("Z", 60), ("1", 61), ("X", 62), ("2", 63), ("C", 64), ("V", 65), ("3", 66),
            ("B", 67), ("4", 68), ("N", 69), ("5", 70), ("M", 71),
            ("A", 72), ("6", 73), ("S", 74), ("7", 75), ("D", 76), ("F", 77), ("8", 78),
            ("G", 79), ("9", 80), ("H", 81), ("0", 82), ("J", 83),
            ("K", 84), ("Y", 85), ("L", 86), ("U", 87), ("Q", 88), ("W", 89), ("I", 90),
            ("E", 91), ("O", 92), ("R", 93), ("P", 94), ("T", 95), (",", 96),
        };
        string bad = "";
        foreach (var (key, pitch) in table)
            if (!p.TryKeyOfPitch(pitch, out string got, out _, out bool sharp, out bool flat, out _)
                || got != key || sharp || flat)
                bad += $"{pitch}→「{got}」(升={sharp} 降={flat}) 应为「{key}」 ";
        Check("FF14 钢琴：37 个音逐个对上键名", bad.Length == 0, bad);

        Check("FF14 钢琴：音域 60..96（C4..C7）",
              p.ResolveMinNote() == 60 && p.ResolveMaxNote() == 96,
              $"实际 {p.ResolveMinNote()}..{p.ResolveMaxNote()}");

        Check("FF14 钢琴：演奏时不按任何修饰键", p.SharpKeyToHold == null,
              $"SharpKeyToHold=「{p.SharpKeyToHold}」");

        // 一排 12 条、一行一个八度：行号必须刚好是偏移除以 12，界面才排得出三行十二列。
        bool rowsOk = p.Keys.All(k => k.Row == k.Offset / 12)
                      && p.Keys.Count(k => k.Row == 0) == 12 && p.Keys.Count(k => k.Row == 1) == 12
                      && p.Keys.Count(k => k.Row == 2) == 12 && p.Keys.Count(k => k.Row == 3) == 1;
        Check("FF14 钢琴：三行各 12 个半音、末行一个 do",
              rowsOk, string.Join(" | ", p.Keys.GroupBy(k => k.Row).OrderBy(g => g.Key)
                                                  .Select(g => $"第{g.Key}行 {g.Count()} 条")));

        // 方案 JSON 往返后键名、偏移、行号一字不差（预设要存成文件、要能分享）。
        var round = KeymapProfile.FromJson(p.ToJson());
        bool same = round.Keys.Count == p.Keys.Count;
        for (int i = 0; same && i < p.Keys.Count; i++)
            same = round.Keys[i].Key == p.Keys[i].Key
                && round.Keys[i].Offset == p.Keys[i].Offset
                && round.Keys[i].Row == p.Keys[i].Row
                && round.Keys[i].Shift == p.Keys[i].Shift;
        Check("FF14 钢琴：方案 JSON 往返后 37 条键位一字不差", same,
              same ? "" : $"原 {p.Keys.Count} 条 / 回读 {round.Keys.Count} 条");
    }

    // ================= 和弦调度 =================

    /// <summary>
    /// 和弦调度自检：修饰键状态相同的同刻音必须**同时按下**（和弦），不是被顺延成琶音；
    /// 不重叠的单音线不能被改坏；同一根键不会被同时按住两次。
    /// 用公开的 <see cref="PlaybackEngine.BuildSchedulePreview"/>（导出按键表走的就是它），
    /// 所以发布版也能跑，不需要 MIDIKEY_TEST。
    /// </summary>
    private static void TestChordScheduling()
    {
        // 三个同刻音（都在基准八度、不需要修饰键）→ 必须同时按住 3 根
        var chordEvs = PlaybackEngine.BuildSchedulePreview(new List<MappedNote>
        {
            ScheduleNote(60, 'Z', 1.0, 1.5),
            ScheduleNote(64, 'C', 1.0, 1.5),
            ScheduleNote(67, 'B', 1.0, 1.5),
        }, InputTiming.Standard, 1.0);
        int held = MaxHeldKeyCount(chordEvs);
        Check("和弦：三个同刻音同时按住", held == 3, $"实际最多同时按住 {held} 根");

        // 重叠但不同刻的两个音也要同时发声，长音不能被截断
        var overlapEvs = PlaybackEngine.BuildSchedulePreview(new List<MappedNote>
        {
            ScheduleNote(60, 'Z', 0.0, 2.0),
            ScheduleNote(64, 'C', 0.5, 1.0),
        }, InputTiming.Standard, 1.0);
        int overlapHeld = MaxHeldKeyCount(overlapEvs);
        Check("和弦：重叠的同组音同时按住", overlapHeld == 2, $"实际最多同时按住 {overlapHeld} 根");

        var longUp = overlapEvs.FirstOrDefault(e => e.Kind == "key" && e.Key == 'Z' && !e.Down);
        Check("和弦：长音不被截断（抬起仍是 2.0000）",
              longUp != null && Math.Abs(longUp.MusicTime - 2.0) < 1e-6,
              longUp == null ? "找不到 Z 的抬起" : $"Z 抬起 {longUp.MusicTime:F4}");

        // 不重叠的单音线不能被改坏：时刻与谱面一字不差
        var lineEvs = PlaybackEngine.BuildSchedulePreview(new List<MappedNote>
        {
            ScheduleNote(60, 'Z', 0.0, 0.5),
            ScheduleNote(62, 'X', 0.6, 1.0),
        }, InputTiming.Standard, 1.0);
        var lineDowns = lineEvs.Where(e => e.Kind == "key" && e.Down).Select(e => e.MusicTime).ToList();
        Check("单音线：不重叠的音时刻不变（0.0000 / 0.6000）",
              lineDowns.Count == 2 && Math.Abs(lineDowns[0]) < 1e-9 && Math.Abs(lineDowns[1] - 0.6) < 1e-9,
              $"实际 {string.Join(",", lineDowns.Select(t => t.ToString("F4")))}");

        // 同一根键的两个音：不能同时按住
        var repeatEvs = PlaybackEngine.BuildSchedulePreview(new List<MappedNote>
        {
            ScheduleNote(60, 'Z', 0.0, 0.4),
            ScheduleNote(60, 'Z', 0.4, 0.8),
        }, InputTiming.Standard, 1.0);
        int repeatHeld = MaxHeldKeyCount(repeatEvs);
        Check("和弦：同一根键不会被同时按住两次", repeatHeld == 1, $"实际最多同时按住 {repeatHeld} 根");
    }

    /// <summary>自检用：造一个不需要修饰键的可演奏音符。</summary>
    private static MappedNote ScheduleNote(int pitch, char key, double start, double end) => new()
    {
        Pitch = pitch,
        Start = start,
        End = end,
        Key = key,
        KeyName = key.ToString(),
        Sharp = false,
        Flat = false,
        OctaveOffset = 0,
        SoundingPitch = pitch,
        InRange = true,
    };

    // ================= 和弦保留（谱面合并） =================

    /// <summary>
    /// 谱面合并自检：同一条声轨（同一个 Rank）同一时刻的音是和弦，必须**一个不丢**。
    /// 用户报过「导入带和弦的 MIDI，和弦只剩一个音」，根因就在这里。
    /// 另外钉住合奏的既有规则：不同声部同刻相撞时，编号小的优先。
    /// </summary>
    private static void TestChordMerge()
    {
        // 1) 一个声部里的三音和弦：一个音都不许少
        var kept = NoteMapper.MergeVoicesByPriority(new List<(int, RawNote)>
        {
            (0, MergeNote(60, 0.0, 1.0)),
            (0, MergeNote(64, 0.0, 1.2)),
            (0, MergeNote(67, 0.0, 1.0)),
        });
        Check("和弦保留：一个声部的三音和弦全部留下", kept.Count == 3,
              $"实际 {kept.Count} 个音：" + string.Join(",", kept.Select(n => Music.NoteName(n.Pitch))));

        // 2) 和弦里各音长短不同：长音不能被截断，时值原样保留
        var longNote = kept.FirstOrDefault(n => n.Pitch == 64);
        Check("和弦保留：和弦里的长音时值不变（0.0000~1.2000）",
              longNote != null && Math.Abs(longNote.Start) < 1e-9 && Math.Abs(longNote.End - 1.2) < 1e-9,
              longNote == null ? "64 号音不见了" : $"{longNote.Start:F4}~{longNote.End:F4}");

        // 3) 更宽的叠置和弦同样一个不落
        var wide = new List<(int, RawNote)>();
        foreach (int p in new[] { 55, 59, 62, 65, 69 }) wide.Add((0, MergeNote(p, 2.0, 2.5)));
        int wideKept = NoteMapper.MergeVoicesByPriority(wide).Count;
        Check("和弦保留：五音和弦全部留下", wideKept == 5, $"实际 {wideKept} 个音");

        // 4) 同一音轨里「长音 + 和弦」同时响：也不能少
        var layered = NoteMapper.MergeVoicesByPriority(new List<(int, RawNote)>
        {
            (0, MergeNote(48, 0.0, 4.0)),   // 低音长音
            (0, MergeNote(60, 1.0, 1.5)),
            (0, MergeNote(64, 1.0, 1.5)),
            (0, MergeNote(67, 1.0, 1.5)),
        });
        Check("和弦保留：长音上叠三音和弦共 4 个音", layered.Count == 4, $"实际 {layered.Count} 个音");

        // 5) 不同声部同刻相撞：**两条声部的音都要留下**。
        //    这里曾经只留编号小的那个，那是三角洲口琴时代的规定（当时乐器一次只能发一个音）。
        //    现在面向全功能 MIDI 乐器，能同时按多个键，再压就是白丢音。
        var clash = NoteMapper.MergeVoicesByPriority(new List<(int, RawNote)>
        {
            (0, MergeNote(60, 0.0, 1.0)),
            (1, MergeNote(67, 0.0, 1.0)),
        });
        Check("合奏：不同声部同刻相撞时两个音都留下",
              clash.Count == 2 && clash.Any(n => n.Pitch == 60) && clash.Any(n => n.Pitch == 67),
              $"实际 {clash.Count} 个音：" + string.Join(",", clash.Select(n => Music.NoteName(n.Pitch))));
        Check("合奏：留下的音各自带着自己的声部序号",
              clash.Count == 2 && clash.Any(n => n.Pitch == 60 && n.Voice == 0)
              && clash.Any(n => n.Pitch == 67 && n.Voice == 1),
              string.Join(" ", clash.Select(n => $"{Music.NoteName(n.Pitch)}→声部{n.Voice}")));

        // 6) 不同声部、不同音高、时值交叠：两条都要完整保留，谁也不许被截断或补尾巴
        var overlap = NoteMapper.MergeVoicesByPriority(new List<(int, RawNote)>
        {
            (0, MergeNote(60, 0.0, 1.0)),
            (1, MergeNote(67, 0.0, 2.0)),
        });
        Check("合奏：交叠的另一个声部不再被截断",
              overlap.Count == 2, $"实际 {overlap.Count} 个音");
        var longOne = overlap.FirstOrDefault(n => n.Pitch == 67);
        Check("合奏：交叠音保持原时值 0.0000~2.0000（不被压掉前段）",
              longOne != null && Math.Abs(longOne.Start) < 1e-9 && Math.Abs(longOne.End - 2.0) < 1e-9,
              longOne == null ? "67 号音不见了" : $"{longOne.Start:F4}~{longOne.End:F4}");

        // 6b) 不同声部同一个音高、同一刻：这是真正的一个音，只留一次
        var unison = NoteMapper.MergeVoicesByPriority(new List<(int, RawNote)>
        {
            (0, MergeNote(64, 1.0, 2.0)),
            (1, MergeNote(64, 1.0, 2.0)),
        });
        Check("合奏：不同声部的同刻同音只留一个（真正的一个音不弹两次）",
              unison.Count == 1 && unison[0].Pitch == 64,
              $"实际 {unison.Count} 个音");
        Check("合奏：同刻同音留下的是优先级高的声部",
              unison.Count == 1 && unison[0].Voice == 0, $"声部 {unison.FirstOrDefault()?.Voice}");

        // 6c) 不同声部同一个音高但起音错开：这是两个音（先后各弹一次）
        var sequential = NoteMapper.MergeVoicesByPriority(new List<(int, RawNote)>
        {
            (0, MergeNote(64, 0.0, 1.0)),
            (1, MergeNote(64, 1.5, 2.5)),
        });
        Check("合奏：错开的同音高两个声部都保留", sequential.Count == 2, $"实际 {sequential.Count} 个音");

        // 6d) 同一个声部里同一个音高重复起音（键还没松开又按一次）：丢掉重复的那次
        var retrigger = NoteMapper.MergeVoicesByPriority(new List<(int, RawNote)>
        {
            (0, MergeNote(64, 0.0, 2.0)),
            (0, MergeNote(64, 1.0, 3.0)),
        });
        Check("合奏：同一个声部里同音高未松开就重复起音，只留一次",
              retrigger.Count == 1, $"实际 {retrigger.Count} 个音");

        // 7) 整条导入链路：写一份带和弦的 MIDI，解析后按界面口径（每行一个 Rank）合并
        string path = Path.Combine(Path.GetTempPath(), "midikey-chord-selftest.mid");
        try
        {
            WriteChordMidi(path);
            var parsed = MidiLoader.Parse(path);
            Check("和弦保留：和弦 MIDI 解析出一条声轨", parsed.Candidates.Count == 1,
                  $"实际 {parsed.Candidates.Count} 条");

            var voices = new List<(int, RawNote)>();
            for (int k = 0; k < parsed.Candidates.Count; k++)
                foreach (var n in parsed.Candidates[k].Notes) voices.Add((k, n));
            var merged = NoteMapper.MergeVoicesByPriority(voices);

            var atZero = merged.Where(n => Math.Abs(n.Start) < 0.001)
                               .Select(n => n.Pitch).OrderBy(p => p).ToList();
            Check("和弦保留：导入后 0 秒处三个音都在", atZero.Count == 3,
                  $"实际 {atZero.Count} 个音：" + string.Join(",", atZero.Select(Music.NoteName)));
            Check("和弦保留：三个音高是 C4 / E4 / G4",
                  atZero.SequenceEqual(new[] { 60, 64, 67 }),
                  string.Join(",", atZero.Select(Music.NoteName)));

            // 单声部演奏时合并不许丢音：出来的音数必须与原谱一样多
            int rawCount = parsed.Candidates.Sum(c => c.Notes.Count);
            Check("和弦保留：合并后的音数与原谱一致", merged.Count == rawCount,
                  $"原谱 {rawCount} 个音，合并后 {merged.Count} 个音");
        }
        finally
        {
            try { File.Delete(path); } catch { /* 临时文件删不掉不影响结论 */ }
        }
    }

    /// <summary>自检用：造一个音符（合并用例只关心音高、时刻与声部归属）。</summary>
    private static RawNote MergeNote(int pitch, double start, double end) => new()
    {
        Pitch = pitch,
        Start = start,
        End = end,
        Velocity = 90,
        Channel = 0,
    };

    /// <summary>
    /// 自检用：写一份格式 0 的 MIDI，一条轨道一个声道，开头是 C 大三和弦（C4 / E4 / G4）。
    /// 手工拼事件，不依赖 DryWetMidi 的写接口。
    /// </summary>
    private static void WriteChordMidi(string path)
    {
        var events = new List<MidiEvent>
        {
            new SequenceTrackNameEvent("和弦"),
            new ProgramChangeEvent((SevenBitNumber)0) { Channel = (FourBitNumber)0 },
        };
        foreach (int p in new[] { 60, 64, 67 })
            events.Add(new NoteOnEvent((SevenBitNumber)p, (SevenBitNumber)90) { Channel = (FourBitNumber)0 });
        // 和弦一起按下，一起抬起：长度 480 刻 = 一个四分音符（默认 480 刻/四分）
        for (int i = 0; i < 3; i++)
        {
            var off = new NoteOffEvent((SevenBitNumber)new[] { 60, 64, 67 }[i], (SevenBitNumber)0)
            {
                Channel = (FourBitNumber)0
            };
            if (i == 0) off.DeltaTime = 480;
            events.Add(off);
        }

        var file = new MidiFile();
        file.Chunks.Add(new TrackChunk(events));
        file.Write(path, overwriteFile: true);
    }

    /// <summary>自检用：扫事件流求"同时按住的音键"峰值（同一时刻先算抬起、再算按下）。</summary>
    private static int MaxHeldKeyCount(List<PlaybackEngine.ScheduledEvent> evs)
    {
        int held = 0, max = 0;
        var ordered = evs.Where(e => e.Kind == "key")
                         .OrderBy(e => e.MusicTime)
                         .ThenBy(e => e.Down ? 1 : 0);
        foreach (var e in ordered)
        {
            held += e.Down ? 1 : -1;
            if (held > max) max = held;
        }
        return max;
    }

    // v1.1.8 移除音频转 MIDI：WAV 解析、重采样、模型推理、音符解码共 10 条断言，
    // 随 Audio\ 整条链路一起删。

    // ================= 声部识别 =================

    /// <summary>
    /// 声部识别（GM 音色表 + 角色映射 + 无音色时的推断）。
    /// 这张表是左侧「声部」列的唯一来源，错一个号就会把鼓标成贝斯，所以逐条钉住。
    /// </summary>
    private static void TestTrackRoles()
    {
        // 音色表必须满 128 条，而且都要有名字：少一条就说明抄表时漏了行
        int named = 0;
        for (int p = 0; p < 128; p++)
            if (GmInstrument.ProgramName(p).Length > 0) named++;
        Check("GM 音色表 128 条都有名字", named == 128, $"有名字 {named} 条");
        Check("GM 音色表越界取名为空", GmInstrument.ProgramName(-1).Length == 0
                                        && GmInstrument.ProgramName(-2).Length == 0);
        Check("GM 0 = 大钢琴", GmInstrument.ProgramName(0) == "大钢琴", GmInstrument.ProgramName(0));
        // 音效组（121 起）是分段音色表里最容易错位的一段，钉住首尾与中间两条
        Check("GM 121 = 吉他品丝声", GmInstrument.ProgramName(120) == "吉他品丝声", GmInstrument.ProgramName(120));
        Check("GM 124 = 鸟鸣", GmInstrument.ProgramName(123) == "鸟鸣", GmInstrument.ProgramName(123));
        Check("GM 128 = 枪声", GmInstrument.ProgramName(127) == "枪声", GmInstrument.ProgramName(127));
        Check("GM 表里没有「鼓组」这一条（127 是音效组末尾）",
            GmInstrument.ProgramName(127) == "枪声", GmInstrument.ProgramName(127));

        // 角色映射：每组抽查一个号，边界取首尾
        var cases = new (int Program, TrackRole Role, string Tag)[]
        {
            (0,   TrackRole.Piano,           "键盘"),
            (7,   TrackRole.Piano,           "键盘"),
            (12,  TrackRole.Chromatic,       "音块"),
            (16,  TrackRole.Organ,           "风琴"),
            (24,  TrackRole.AcousticGuitar,  "吉他"),
            (27,  TrackRole.AcousticGuitar,  "吉他"),
            (28,  TrackRole.ElectricGuitar,  "电吉他"),
            (31,  TrackRole.ElectricGuitar,  "电吉他"),
            (32,  TrackRole.Bass,            "贝斯"),
            (39,  TrackRole.Bass,            "贝斯"),
            (40,  TrackRole.Strings,         "弦乐"),
            (48,  TrackRole.Strings,         "弦乐"),
            (52,  TrackRole.Vocals,          "人声"),
            (54,  TrackRole.Vocals,          "人声"),
            (56,  TrackRole.Brass,           "铜管"),
            (73,  TrackRole.Wind,            "管乐"),
            (80,  TrackRole.SynthLead,       "主音"),
            (88,  TrackRole.SynthPad,        "铺底"),
            (96,  TrackRole.Effects,         "效果"),
            (47,  TrackRole.Drums,           "鼓"),
            (117, TrackRole.Drums,           "鼓"),   // 太鼓 / 旋律鼓 / 合成鼓 / 反镲
            (119, TrackRole.Drums,           "鼓"),
            (120, TrackRole.Effects,         "效果"), // 吉他品丝声，音效组开头
            (127, TrackRole.Effects,         "效果"), // 枪声，音效组末尾
        };
        int wrong = 0;
        string first = "";
        foreach (var c in cases)
        {
            var got = GmInstrument.RoleOfProgram(c.Program);
            if (got == c.Role) continue;
            wrong++;
            if (first.Length == 0) first = $"GM{c.Program + 1} 期望 {c.Role} 实得 {got}";
        }
        Check("GM 音色号 → 声部角色", wrong == 0, wrong == 0 ? $"{cases.Length} 条" : first);

        int tagWrong = 0;
        string badTag = "";
        foreach (var c in cases)
        {
            string tag = GmInstrument.TagOf(c.Role);
            if (tag == c.Tag) continue;
            tagWrong++;
            if (badTag.Length == 0) badTag = $"{c.Role} 期望 {c.Tag} 实得 {tag}";
        }
        Check("声部标牌的字", tagWrong == 0, tagWrong == 0 ? $"{cases.Length} 条" : badTag);

        // 标牌最长三个字：列表那一列按它定宽，超了会被截断
        int tooLong = 0;
        string longTag = "";
        foreach (TrackRole r in Enum.GetValues<TrackRole>())
        {
            string tag = GmInstrument.TagOf(r);
            if (tag.Length <= 3) continue;
            tooLong++;
            if (longTag.Length == 0) longTag = tag;
        }
        Check("声部标牌不超过三个字", tooLong == 0, tooLong == 0 ? "" : longTag);

        // 取色分组：四类不能混，未知不上色
        Check("取色分组：鼓=节奏", GmInstrument.ToneOf(TrackRole.Drums) == RoleTone.Rhythm);
        Check("取色分组：贝斯=低音", GmInstrument.ToneOf(TrackRole.Bass) == RoleTone.Low);
        Check("取色分组：电吉他=和声", GmInstrument.ToneOf(TrackRole.ElectricGuitar) == RoleTone.Harmony);
        Check("取色分组：人声=旋律", GmInstrument.ToneOf(TrackRole.Vocals) == RoleTone.Lead);
        Check("取色分组：未知=不上色", GmInstrument.ToneOf(TrackRole.Unknown) == RoleTone.Neutral);

        // 没有音色号时的推断：通道 10 恒为鼓，其余按音域与节奏
        Check("推断：通道 10 恒为鼓",
            GmInstrument.Infer(9, "Track 1", MakeNotes(60, 40, 0.5)) == TrackRole.Drums);
        Check("推断：轨名写了「鼓」也算鼓",
            GmInstrument.Infer(0, "Drum Kit", MakeNotes(60, 40, 0.5)) == TrackRole.Drums);
        Check("推断：中音区密集 → 伴奏",
            GmInstrument.Infer(0, "Track 2", MakeNotes(60, 40, 0.4)) == TrackRole.Accompaniment);
        Check("推断：高音区稀疏 → 旋律",
            GmInstrument.Infer(0, "Track 3", MakeNotes(72, 24, 1.0)) == TrackRole.Melody);
        Check("推断：低音区 → 贝斯",
            GmInstrument.Infer(0, "Track 4", MakeNotes(40, 24, 0.5)) == TrackRole.Bass);
        Check("推断：又短又不动 → 鼓",
            GmInstrument.Infer(0, "Track 5", MakeNotes(38, 40, 0.06)) == TrackRole.Drums);

        // 通用轨名识别：这些名字要换成识别出的乐器名，说得清的名字原样保留
        var generic = new[] { "", "   ", "Track 1", "track 10", "声道 3", "声部 2", "Part 4", "Channel 7", "Piano 2" };
        var specific = new[] { "主旋律", "钢琴", "Piano", "贝斯", "电吉他", "鼓组", "和弦 1" };
        int genericMiss = 0;
        string missName = "";
        foreach (string n in generic)
        {
            if (GmInstrument.IsGenericTrackName(n)) continue;
            genericMiss++;
            if (missName.Length == 0) missName = n;
        }
        int specificHit = 0;
        string hitName = "";
        foreach (string n in specific)
        {
            if (!GmInstrument.IsGenericTrackName(n)) continue;
            specificHit++;
            if (hitName.Length == 0) hitName = n;
        }
        Check("通用轨名识别为「没写」", genericMiss == 0, genericMiss == 0 ? $"{generic.Length} 条" : missName);
        Check("说得清的轨名不算通用", specificHit == 0, specificHit == 0 ? $"{specific.Length} 条" : hitName);
    }

    /// <summary>造一段等长的测试音符：全部同一个音高，间隔固定（只在声部推断用例里用）。</summary>
    private static List<RawNote> MakeNotes(int pitch, int count, double seconds)
    {
        var list = new List<RawNote>(count);
        for (int i = 0; i < count; i++)
        {
            double start = i * seconds * 2;
            list.Add(new RawNote { Pitch = pitch, Start = start, End = start + seconds, Velocity = 90, Channel = 0 });
        }
        return list;
    }

    // ================= 断言 =================

    private static void Check(string name, bool ok, string detail = "")
    {
        if (!ok) _failed++;
        Lines.Add((ok ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? $"   [{detail}]" : ""));
    }
}

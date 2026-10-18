using System.Text;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using MidiKeyPlayer.Engine;
using MidiKeyPlayer.Midi;

namespace MidiKeyPlayer;

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

    private static bool IsOffValue(string value)
    {
        string v = value.Trim();
        return v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase)
            || v.Equals("no", StringComparison.OrdinalIgnoreCase)
            || v.Equals("off", StringComparison.OrdinalIgnoreCase);
    }

    private static string CustomPath(string value)
    {
        string v = value.Trim();
        if (v == "1" || IsOffValue(v)) return "";
        return v.Contains('\\') || v.Contains('/') ? v : "";
    }

    public static int Run()
    {
        string value = Environment.GetEnvironmentVariable(EnvVar) ?? "";
        string custom = CustomPath(value);
        string path = custom.Length > 0
            ? custom
            : Path.Combine(Path.GetTempPath(), "midikey-keymap-selftest.txt");
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
            TestXindong();
            TestLegacyCopyMigration();
            TestLegacyCopyRewrite();
            TestChordScheduling();
            TestChordMerge();
            TestTrackRoles();
            TestRemoteSync();
            TestPlaylistAdvance();
            TestPlaylistStore();
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
        catch { }
        try { Console.WriteLine(text); } catch { }

        return _failed == 0 ? 0 : 1;
    }

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
                             .Select(k => (k.Shift ? "Shift+" : "") + KeymapProfile.CanonicalKeyName(k.Key))
                             .ToList();
            if (keys.Count == 0) emptyKeys++;
            if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Count) dupKey++;
        }
        Check("预设：每套都有按键", emptyKeys == 0, $"空方案 {emptyKeys} 套");
        Check("预设：每套的键名都不重复（带 Shift 的另算一条）", dupKey == 0, $"重键 {dupKey} 套");

        var notFound = names.Where(n => KeymapProfile.PresetByName(n) == null).ToList();
        Check("预设：每套都能按名字取回", notFound.Count == 0, string.Join(" | ", notFound));

        string defaultName = KeymapProfile.Default.Name;
        bool defaultPresent = names.Any(n => string.Equals(n, defaultName, StringComparison.Ordinal));
        Check("预设：默认方案的名字取得回",
              !string.IsNullOrWhiteSpace(defaultName)
              && (!defaultPresent || KeymapProfile.PresetByName(defaultName) != null),
              $"默认「{defaultName}」；内置：{string.Join(" | ", names)}");

        var chromatic = presets.Where(p => Covers(p, 12)).ToList();
        bool oneOffsetPerKey = chromatic.All(p =>
            p.Keys.Where(k => k != null).Select(k => k.Offset).Distinct().Count()
            == p.Keys.Count(k => k != null));
        Check("预设：半音阶方案里一个偏移只占一个键", oneOffsetPerKey,
              $"半音阶方案 {chromatic.Count} 套");
    }

    private static bool Covers(KeymapProfile p, int n)
    {
        var offsets = new HashSet<int>(p.Keys.Where(k => k != null).Select(k => k.Offset));
        for (int i = 0; i < n; i++) if (!offsets.Contains(i)) return false;
        return true;
    }

    private static void TestKeyMapNames()
    {
        Check("键名：鼠标三键在方案里可用",
              IsUsableName("MouseLeft") && IsUsableName("MouseRight") && IsUsableName("MouseMiddle"),
              "MouseLeft / MouseRight / MouseMiddle");

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

        Check("键名：命名字符是私用区哨兵",
              KeymapProfile.KeyCharOf("PageUp") != '\0' && KeymapProfile.KeyCharOf("PageUp") != 'P',
              $"实际 U+{(int)KeymapProfile.KeyCharOf("PageUp"):X4}");
        Check("键名：单字符键原样返回字符",
              KeymapProfile.KeyCharOf("Z") == 'Z' && KeymapProfile.KeyCharOf(",") == ',',
              "Z=" + KeymapProfile.KeyCharOf("Z") + " ,=" + KeymapProfile.KeyCharOf(","));
    }

    private static bool IsUsableName(string name)
        => name.Length > 0 && KeymapProfile.IsKnownKeyName(name) && KeymapProfile.KeyCharOf(name) != '\0';

    private static bool IsBlankOrKnown(string? name)
        => string.IsNullOrWhiteSpace(name) || IsUsableName(KeymapProfile.CanonicalKeyName(name));

    private static void TestSolfegeNames()
    {
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

        Check("简谱：中音 do（60）= 1", Music.SolfegeName(60) == "1", $"实际「{Music.SolfegeName(60)}」");

        Check("简谱：高八度 do（72）= 1 加上点",
              Music.SolfegeName(72) == "1\u02D9", $"实际「{Music.SolfegeName(72)}」");
        Check("简谱：低八度 do（48）= 1 加下点",
              Music.SolfegeName(48) == "1.", $"实际「{Music.SolfegeName(48)}」");
        Check("简谱：八度点最多两个",
              Music.SolfegeName(96) == "1\u02D9\u02D9" && Music.SolfegeName(24) == "1..",
              $"96→「{Music.SolfegeName(96)}」 24→「{Music.SolfegeName(24)}」");

        Check("简谱：升号音（61）= #1", Music.SolfegeName(61) == "#1", $"实际「{Music.SolfegeName(61)}」");

        Check("简谱：音名 60 = C4", Music.NoteName(60) == "C4", $"实际「{Music.NoteName(60)}」");
        Check("简谱：音域低到高",
              Music.SolfegeRange(72, 60) == "1~1\u02D9",
              $"实际「{Music.SolfegeRange(72, 60)}」");

        Check("简谱：查表 SolfegeNames 与现算一致",
              Music.SolfegeNames.Length == 128 && Music.SolfegeNames[60] == "1"
              && Music.SolfegeNames[72] == "1\u02D9",
              $"长度 {Music.SolfegeNames.Length}");
    }

    private static void TestFlatModifier()
    {
        var p = KeymapProfile.PresetByName("21 键半音");
        Check("降半音：21 键半音预设存在且半音键绑对",
              p != null && p.Sharp == "Shift" && p.Flat == "Ctrl" && p.ModifiersEnabled,
              p == null ? "取不到" : $"Sharp=「{p.Sharp}」 Flat=「{p.Flat}」 开关={p.ModifiersEnabled}");
        if (p == null) return;

        var alias = KeymapProfile.PresetByName("异环键位");
        Check("降半音：老名字「异环键位」落到 21 键半音",
              alias != null && alias.Name == "21 键半音",
              alias == null ? "取不到" : $"落到「{alias.Name}」");

        Check("降半音：音域向下多扩一个半音",
              p.ResolveMinNote() == 47 && p.ResolveMaxNote() == 84,
              $"实际 {p.ResolveMinNote()}..{p.ResolveMaxNote()}");

        bool okSharp = p.TryKeyOfPitch(61, out string k61, out _, out bool s61, out bool f61, out _)
                       && k61 == "A" && s61 && !f61;
        Check("降半音：黑键固定优先升半音", okSharp, $"61 → {k61} 升={s61} 降={f61}");

        bool okLow = p.TryKeyOfPitch(47, out string k47, out _, out bool s47, out bool f47, out _)
                     && k47 == "Z" && !s47 && f47;
        Check("降半音：最低键下面半音用降半音", okLow, $"47 → {k47} 升={s47} 降={f47}");

        var onlyFlat = p.Clone();
        onlyFlat.Sharp = null;
        bool okOnly = onlyFlat.TryKeyOfPitch(61, out string k61b, out _, out bool s61b, out bool f61b, out _)
                      && k61b == "S" && !s61b && f61b;
        Check("降半音：没绑升半音时用上方邻键", okOnly, $"61 → {k61b} 升={s61b} 降={f61b}");
    }

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

        bool rowsOk = p.Keys.All(k => k.Row == k.Offset / 12)
                      && p.Keys.Count(k => k.Row == 0) == 12 && p.Keys.Count(k => k.Row == 1) == 12
                      && p.Keys.Count(k => k.Row == 2) == 12 && p.Keys.Count(k => k.Row == 3) == 1;
        Check("FF14 钢琴：三行各 12 个半音、末行一个 do",
              rowsOk, string.Join(" | ", p.Keys.GroupBy(k => k.Row).OrderBy(g => g.Key)
                                                  .Select(g => $"第{g.Key}行 {g.Count()} 条")));

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

    private static void TestXindong()
    {
        var p = KeymapProfile.PresetByName("心动小镇");
        Check("心动小镇：37 条键位、功能键全关、不用 Shift",
              p != null && p.Keys.Count == 37 && p.Keys.Count(k => k.Shift) == 0
              && !p.ModifiersEnabled && p.Sharp == null && p.OctaveUp == null
              && p.OctaveDown == null && p.Flat == null && !p.HasSelfShiftKeys,
              p == null ? "取不到" : $"键 {p.Keys.Count} 条（带 Shift {p.Keys.Count(k => k.Shift)} 条）"
                                      + $"；开关={p.ModifiersEnabled} Sharp=「{p.Sharp}」");
        if (p == null) return;

        var table = new (string Key, int Pitch)[]
        {
            (",", 48), ("L", 49), (".", 50), (";", 51), ("/", 52), ("O", 53), ("0", 54),
            ("P", 55), ("-", 56), ("[", 57), ("=", 58), ("]", 59),
            ("Z", 60), ("S", 61), ("X", 62), ("D", 63), ("C", 64), ("V", 65), ("G", 66),
            ("B", 67), ("H", 68), ("N", 69), ("J", 70), ("M", 71),
            ("Q", 72), ("2", 73), ("W", 74), ("3", 75), ("E", 76), ("R", 77), ("5", 78),
            ("T", 79), ("6", 80), ("Y", 81), ("7", 82), ("U", 83), ("I", 84),
        };
        string bad = "";
        foreach (var (key, pitch) in table)
            if (!p.TryKeyOfPitch(pitch, out string got, out _, out bool sharp, out bool flat, out _)
                || got != key || sharp || flat)
                bad += $"{pitch}→「{got}」(升={sharp} 降={flat}) 应为「{key}」 ";
        Check("心动小镇：37 个音逐个对上键名", bad.Length == 0, bad);

        Check("心动小镇：音域 48..84（C3..C6）",
              p.ResolveMinNote() == 48 && p.ResolveMaxNote() == 84,
              $"实际 {p.ResolveMinNote()}..{p.ResolveMaxNote()}");

        Check("心动小镇：演奏时不按任何修饰键", p.SharpKeyToHold == null,
              $"SharpKeyToHold=「{p.SharpKeyToHold}」");

        bool rowsOk = p.Keys.Count(k => k.Row == 0) == 12 && p.Keys.Count(k => k.Row == 1) == 12
                      && p.Keys.Count(k => k.Row == 2) == 13
                      && p.Keys.Where(k => k.Row == 0).All(k => k.Offset is >= -12 and <= -1)
                      && p.Keys.Where(k => k.Row == 1).All(k => k.Offset is >= 0 and <= 11)
                      && p.Keys.Where(k => k.Row == 2).All(k => k.Offset is >= 12 and <= 24);
        Check("心动小镇：三排各一个八度（低音 12 / 中音 12 / 高音 13）",
              rowsOk, string.Join(" | ", p.Keys.GroupBy(k => k.Row).OrderBy(g => g.Key)
                                                  .Select(g => $"第{g.Key}行 {g.Count()} 条")));

        var round = KeymapProfile.FromJson(p.ToJson());
        bool same = round.Keys.Count == p.Keys.Count;
        for (int i = 0; same && i < p.Keys.Count; i++)
            same = round.Keys[i].Key == p.Keys[i].Key
                && round.Keys[i].Offset == p.Keys[i].Offset
                && round.Keys[i].Row == p.Keys[i].Row
                && round.Keys[i].Shift == p.Keys[i].Shift;
        Check("心动小镇：方案 JSON 往返后 37 条键位一字不差", same,
              same ? "" : $"原 {p.Keys.Count} 条 / 回读 {round.Keys.Count} 条");
    }

    private static void TestLegacyCopyMigration()
    {
        var old = KeymapProfile.BuildXindongV1117();   
        Check("旧副本：v1.1.17 的心动小镇副本认得出是旧副本",
              KeymapProfile.IsUntouchedLegacyCopy("心动小镇", old));

        var now = KeymapProfile.PresetByName("心动小镇");
        Check("旧副本：当前内置键位不算旧副本（说明两版确实不同）",
              now != null && !KeymapProfile.IsUntouchedLegacyCopy("心动小镇", now));

        var mine = KeymapProfile.FromJson(old.ToJson());
        mine.Keys[0].Key = "A";
        Check("旧副本：用户改过一个键就不动他",
              !KeymapProfile.IsUntouchedLegacyCopy("心动小镇", mine));

        var mixed = KeymapProfile.FromJson(old.ToJson());
        mixed.Description = "我自己写的说明";
        Check("旧副本：说明动过也不动他",
              !KeymapProfile.IsUntouchedLegacyCopy("心动小镇", mixed));

        Check("旧副本：别的方案名不参与迁移",
              !KeymapProfile.IsUntouchedLegacyCopy("FF14 钢琴键位", old));
        Check("旧副本：没有方案时不算旧副本",
              !KeymapProfile.IsUntouchedLegacyCopy(null, old) && !KeymapProfile.IsUntouchedLegacyCopy("心动小镇", null));
    }

    private static void TestLegacyCopyRewrite()
    {
        string root = Path.Combine(Path.GetTempPath(), "midikey-selftest-legacy");
        string old = KeymapProfile.BuildXindongV1117().ToJson();
        var utf8 = new UTF8Encoding(false);
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.CreateDirectory(Path.Combine(root, "schemes"));
            KeymapProfile.DirPathForDev = root;

            var legacyObj = KeymapProfile.BuildXindongV1117();
            var round = KeymapProfile.FromJson(old);
            string diff = "";
            if (legacyObj.Description != round.Description) diff += "描述不同；";
            if (legacyObj.Keys.Count != round.Keys.Count) diff += $"条数 {legacyObj.Keys.Count}/{round.Keys.Count}；";
            for (int i = 0; i < Math.Min(legacyObj.Keys.Count, round.Keys.Count); i++)
            {
                var a = legacyObj.Keys[i];
                var b = round.Keys[i];
                if (a.Key != b.Key || a.Offset != b.Offset || a.Row != b.Row || a.Shift != b.Shift)
                    diff += $"第 {i} 条 {a} vs {b}；";
            }
            Check("旧副本：旧版本往返后一模一样", diff.Length == 0, diff);
            Check("旧副本：往返后的旧副本仍然认得出",
                  KeymapProfile.IsUntouchedLegacyCopy("心动小镇", round));

            File.WriteAllText(Path.Combine(root, "keymap.json"), old, utf8);
            Check("旧副本：开发用的目录开关生效", KeymapProfile.FilePath.StartsWith(root, StringComparison.OrdinalIgnoreCase),
                  "FilePath=" + KeymapProfile.FilePath + " root=" + root);
            var loaded = KeymapProfile.Load();
            var after = KeymapProfile.FromJson(File.ReadAllText(Path.Combine(root, "keymap.json")));
            bool memNew = loaded.Keys.Any(k => k.Key == "L" && k.Offset == -11);
            bool memOld = loaded.Keys.Any(k => k.Key == "1" && k.Offset == 13);
            bool fileNew = after.Keys.Any(k => k.Key == "L" && k.Offset == -11);
            bool fileOld = after.Keys.Any(k => k.Key == "1" && k.Offset == 13);
            Check("旧副本：keymap.json 的旧副本换成内置键位（内存与文件都要换）",
                  memNew && !memOld && fileNew && !fileOld,
                  $"内存 {loaded.Keys.Count} 条（新 {memNew} / 旧 {memOld}）；文件 {after.Keys.Count} 条（新 {fileNew} / 旧 {fileOld}）");

            string copy = Path.Combine(root, "schemes", "心动小镇.json");
            File.WriteAllText(copy, old, utf8);
            File.Delete(Path.Combine(root, "keymap.json"));
            var byName = KeymapProfile.LoadByName("心动小镇");
            var copyAfter = KeymapProfile.FromJson(File.ReadAllText(copy));
            Check("旧副本：schemes 里的旧副本换成内置键位（内存与文件都要换）",
                  byName != null && byName.Keys.Any(k => k.Key == "L" && k.Offset == -11)
                  && copyAfter.Keys.Any(k => k.Key == "L" && k.Offset == -11),
                  byName == null ? "取不到" : $"副本 {copyAfter.Keys.Count} 条");

            var mine = KeymapProfile.FromJson(old);
            mine.Keys[0].Key = "A";
            File.WriteAllText(copy, mine.ToJson(), utf8);
            var kept = KeymapProfile.LoadByName("心动小镇");
            var keptAfter = KeymapProfile.FromJson(File.ReadAllText(copy));
            Check("旧副本：用户改过的副本原样保留",
                  kept != null && kept.Keys[0].Key == "A" && keptAfter.Keys[0].Key == "A",
                  kept == null ? "取不到" : $"内存 {kept.Keys[0].Key} / 文件 {keptAfter.Keys[0].Key}");
        }
        finally
        {
            KeymapProfile.DirPathForDev = null;
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    private static void TestChordScheduling()
    {
        var chordEvs = PlaybackEngine.BuildSchedulePreview(new List<MappedNote>
        {
            ScheduleNote(60, 'Z', 1.0, 1.5),
            ScheduleNote(64, 'C', 1.0, 1.5),
            ScheduleNote(67, 'B', 1.0, 1.5),
        }, InputTiming.Standard, 1.0);
        int held = MaxHeldKeyCount(chordEvs);
        Check("和弦：三个同刻音同时按住", held == 3, $"实际最多同时按住 {held} 根");

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

        var lineEvs = PlaybackEngine.BuildSchedulePreview(new List<MappedNote>
        {
            ScheduleNote(60, 'Z', 0.0, 0.5),
            ScheduleNote(62, 'X', 0.6, 1.0),
        }, InputTiming.Standard, 1.0);
        var lineDowns = lineEvs.Where(e => e.Kind == "key" && e.Down).Select(e => e.MusicTime).ToList();
        Check("单音线：不重叠的音时刻不变（0.0000 / 0.6000）",
              lineDowns.Count == 2 && Math.Abs(lineDowns[0]) < 1e-9 && Math.Abs(lineDowns[1] - 0.6) < 1e-9,
              $"实际 {string.Join(",", lineDowns.Select(t => t.ToString("F4")))}");

        var repeatEvs = PlaybackEngine.BuildSchedulePreview(new List<MappedNote>
        {
            ScheduleNote(60, 'Z', 0.0, 0.4),
            ScheduleNote(60, 'Z', 0.4, 0.8),
        }, InputTiming.Standard, 1.0);
        int repeatHeld = MaxHeldKeyCount(repeatEvs);
        Check("和弦：同一根键不会被同时按住两次", repeatHeld == 1, $"实际最多同时按住 {repeatHeld} 根");
    }

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

    private static void TestChordMerge()
    {
        var kept = NoteMapper.MergeVoicesByPriority(new List<(int, RawNote)>
        {
            (0, MergeNote(60, 0.0, 1.0)),
            (0, MergeNote(64, 0.0, 1.2)),
            (0, MergeNote(67, 0.0, 1.0)),
        });
        Check("和弦保留：一个声部的三音和弦全部留下", kept.Count == 3,
              $"实际 {kept.Count} 个音：" + string.Join(",", kept.Select(n => Music.NoteName(n.Pitch))));

        var longNote = kept.FirstOrDefault(n => n.Pitch == 64);
        Check("和弦保留：和弦里的长音时值不变（0.0000~1.2000）",
              longNote != null && Math.Abs(longNote.Start) < 1e-9 && Math.Abs(longNote.End - 1.2) < 1e-9,
              longNote == null ? "64 号音不见了" : $"{longNote.Start:F4}~{longNote.End:F4}");

        var wide = new List<(int, RawNote)>();
        foreach (int p in new[] { 55, 59, 62, 65, 69 }) wide.Add((0, MergeNote(p, 2.0, 2.5)));
        int wideKept = NoteMapper.MergeVoicesByPriority(wide).Count;
        Check("和弦保留：五音和弦全部留下", wideKept == 5, $"实际 {wideKept} 个音");

        var layered = NoteMapper.MergeVoicesByPriority(new List<(int, RawNote)>
        {
            (0, MergeNote(48, 0.0, 4.0)),   
            (0, MergeNote(60, 1.0, 1.5)),
            (0, MergeNote(64, 1.0, 1.5)),
            (0, MergeNote(67, 1.0, 1.5)),
        });
        Check("和弦保留：长音上叠三音和弦共 4 个音", layered.Count == 4, $"实际 {layered.Count} 个音");

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

        var sequential = NoteMapper.MergeVoicesByPriority(new List<(int, RawNote)>
        {
            (0, MergeNote(64, 0.0, 1.0)),
            (1, MergeNote(64, 1.5, 2.5)),
        });
        Check("合奏：错开的同音高两个声部都保留", sequential.Count == 2, $"实际 {sequential.Count} 个音");

        var retrigger = NoteMapper.MergeVoicesByPriority(new List<(int, RawNote)>
        {
            (0, MergeNote(64, 0.0, 2.0)),
            (0, MergeNote(64, 1.0, 3.0)),
        });
        Check("合奏：同一个声部里同音高未松开就重复起音，只留一次",
              retrigger.Count == 1, $"实际 {retrigger.Count} 个音");

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

            int rawCount = parsed.Candidates.Sum(c => c.Notes.Count);
            Check("和弦保留：合并后的音数与原谱一致", merged.Count == rawCount,
                  $"原谱 {rawCount} 个音，合并后 {merged.Count} 个音");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static RawNote MergeNote(int pitch, double start, double end) => new()
    {
        Pitch = pitch,
        Start = start,
        End = end,
        Velocity = 90,
        Channel = 0,
    };

    private static void WriteChordMidi(string path)
    {
        var events = new List<MidiEvent>
        {
            new SequenceTrackNameEvent("和弦"),
            new ProgramChangeEvent((SevenBitNumber)0) { Channel = (FourBitNumber)0 },
        };
        foreach (int p in new[] { 60, 64, 67 })
            events.Add(new NoteOnEvent((SevenBitNumber)p, (SevenBitNumber)90) { Channel = (FourBitNumber)0 });
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

    private static void TestTrackRoles()
    {
        int named = 0;
        for (int p = 0; p < 128; p++)
            if (GmInstrument.ProgramName(p).Length > 0) named++;
        Check("GM 音色表 128 条都有名字", named == 128, $"有名字 {named} 条");
        Check("GM 音色表越界取名为空", GmInstrument.ProgramName(-1).Length == 0
                                        && GmInstrument.ProgramName(-2).Length == 0);
        Check("GM 0 = 大钢琴", GmInstrument.ProgramName(0) == "大钢琴", GmInstrument.ProgramName(0));
        Check("GM 121 = 吉他品丝声", GmInstrument.ProgramName(120) == "吉他品丝声", GmInstrument.ProgramName(120));
        Check("GM 124 = 鸟鸣", GmInstrument.ProgramName(123) == "鸟鸣", GmInstrument.ProgramName(123));
        Check("GM 128 = 枪声", GmInstrument.ProgramName(127) == "枪声", GmInstrument.ProgramName(127));
        Check("GM 表里没有「鼓组」这一条（127 是音效组末尾）",
            GmInstrument.ProgramName(127) == "枪声", GmInstrument.ProgramName(127));

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
            (117, TrackRole.Drums,           "鼓"),   
            (119, TrackRole.Drums,           "鼓"),
            (120, TrackRole.Effects,         "效果"), 
            (127, TrackRole.Effects,         "效果"), 
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

        Check("取色分组：鼓=节奏", GmInstrument.ToneOf(TrackRole.Drums) == RoleTone.Rhythm);
        Check("取色分组：贝斯=低音", GmInstrument.ToneOf(TrackRole.Bass) == RoleTone.Low);
        Check("取色分组：电吉他=和声", GmInstrument.ToneOf(TrackRole.ElectricGuitar) == RoleTone.Harmony);
        Check("取色分组：人声=旋律", GmInstrument.ToneOf(TrackRole.Vocals) == RoleTone.Lead);
        Check("取色分组：未知=不上色", GmInstrument.ToneOf(TrackRole.Unknown) == RoleTone.Neutral);

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

    private static void TestPlaylistAdvance()
    {
        Check("演奏模式有五种", PlayModeNames.All.Length == 5 && PlayModeNames.Short.Length == 5,
            string.Join(" / ", PlayModeNames.All));

        bool mapOk = true;
        for (int i = 0; i < PlayModeNames.All.Length; i++)
            if ((int)PlayModeNames.Clamp(i) != i || PlayModeNames.All[i].Length == 0) mapOk = false;
        Check("每个模式号都有自己的名字", mapOk && PlayModeNames.All[(int)PlayMode.Off] == "关闭");

        Check("越界的模式号夹回顺序播放",
            PlayModeNames.Clamp(-1) == PlayMode.Sequential && PlayModeNames.Clamp(99) == PlayMode.Sequential);

        var off = PlayAdvance.AfterFinish(PlayMode.Off, 3, 0);
        Check("关闭：放完这一首就停", off.Kind == PlayAdvance.Move.Stop, off.Reason);
        Check("关闭：放不出来也不换歌", PlayAdvance.NextAfterFailure(PlayMode.Off, 4, 1) == -1);

        var seq1 = PlayAdvance.AfterFinish(PlayMode.Sequential, 3, 0);
        Check("顺序播放：第 1 首之后放第 2 首",
            seq1.Kind == PlayAdvance.Move.Play && seq1.Index == 1, seq1.Reason);
        var seq2 = PlayAdvance.AfterFinish(PlayMode.Sequential, 3, 1);
        Check("顺序播放：第 2 首之后放第 3 首",
            seq2.Kind == PlayAdvance.Move.Play && seq2.Index == 2, seq2.Reason);
        var seqEnd = PlayAdvance.AfterFinish(PlayMode.Sequential, 3, 2);
        Check("顺序播放：最后一首放完就停", seqEnd.Kind == PlayAdvance.Move.Stop, seqEnd.Reason);

        var loopEnd = PlayAdvance.AfterFinish(PlayMode.ListLoop, 3, 2);
        Check("列表循环：最后一首之后回到第 1 首",
            loopEnd.Kind == PlayAdvance.Move.Play && loopEnd.Index == 0, loopEnd.Reason);
        var loopMid = PlayAdvance.AfterFinish(PlayMode.ListLoop, 3, 0);
        Check("列表循环：中间照常往后走",
            loopMid.Kind == PlayAdvance.Move.Play && loopMid.Index == 1, loopMid.Reason);

        var rng = new Random(20260928);
        bool shuffleOk = true;
        string shuffleBad = "";
        for (int i = 0; i < 200; i++)
        {
            var d = PlayAdvance.AfterFinish(PlayMode.Shuffle, 5, 2, null, rng);
            if (d.Kind == PlayAdvance.Move.Play && d.Index != 2 && d.Index >= 0 && d.Index < 5) continue;
            shuffleOk = false;
            shuffleBad = $"实得 {d.Kind}/{d.Index}";
            break;
        }
        Check("随机播放：200 次都不挑到当前这一首", shuffleOk, shuffleOk ? "200 次" : shuffleBad);
        Check("随机播放：列表只有一首时不前进",
            PlayAdvance.PickShuffle(1, 0) == -1);

        var rep = PlayAdvance.AfterFinish(PlayMode.RepeatOne, 3, 1);
        Check("单曲循环：连播不做前进", rep.Kind == PlayAdvance.Move.Stop, rep.Reason);

        Check("空列表不前进",
            PlayAdvance.AfterFinish(PlayMode.ListLoop, 0, 0).Kind == PlayAdvance.Move.Stop);
        Check("只有一首的列表不前进",
            PlayAdvance.AfterFinish(PlayMode.ListLoop, 1, 0).Kind == PlayAdvance.Move.Stop);
        Check("越界的当前下标被当成故障",
            PlayAdvance.AfterFinish(PlayMode.Sequential, 3, 5).Kind == PlayAdvance.Move.Fault);

        Check("连败上限是 3 次", PlayAdvance.MaxConsecutiveFailures == 3);
        Check("连败 1 次还继续跳", PlayAdvance.ShouldKeepSkipping(1));
        Check("连败 2 次还继续跳", PlayAdvance.ShouldKeepSkipping(2));
        Check("连败 3 次停连播", !PlayAdvance.ShouldKeepSkipping(3));
        Check("放不出来时顺序播放往后挪一首",
            PlayAdvance.NextAfterFailure(PlayMode.Sequential, 4, 1) == 2);
        Check("放不出来时最后一首回绕到第 1 首",
            PlayAdvance.NextAfterFailure(PlayMode.Sequential, 4, 3) == 0);

        var order = new List<string> { "A", "B", "C" };
        Check("上移：B 挪到 A 前面",
            PlaylistModel.MoveUp(order, 1) && string.Join("", order) == "BAC", string.Join("", order));

        order = new List<string> { "A", "B", "C" };
        Check("下移：A 挪到 B 后面",
            PlaylistModel.MoveDown(order, 0) && string.Join("", order) == "BAC", string.Join("", order));

        order = new List<string> { "A", "B", "C" };
        Check("上移：已经在最上面就不动",
            !PlaylistModel.MoveUp(order, 0) && string.Join("", order) == "ABC", string.Join("", order));

        order = new List<string> { "A", "B", "C" };
        Check("下移：已经在最下面就不动",
            !PlaylistModel.MoveDown(order, 2) && string.Join("", order) == "ABC", string.Join("", order));

        order = new List<string> { "A", "B", "C" };
        Check("下标越界不崩也不动",
            !PlaylistModel.Move(order, -1, 1) && !PlaylistModel.Move(order, 9, 1) &&
            string.Join("", order) == "ABC", string.Join("", order));

        order = new List<string> { "A", "B", "C" };
        Check("拖放：A 拖到 C 的位置上变成 BAC",
            PlaylistModel.MoveOnto(order, 0, 2) && string.Join("", order) == "BAC", string.Join("", order));

        order = new List<string> { "A", "B", "C" };
        Check("拖放：C 拖到 A 上变成 CAB",
            PlaylistModel.MoveOnto(order, 2, 0) && string.Join("", order) == "CAB", string.Join("", order));

        var cands = new List<MidiCandidate>
        {
            MakeCandidate(0, 9, TrackRole.Drums, "鼓"),
            MakeCandidate(1, 0, TrackRole.Bass, "贝斯"),
            MakeCandidate(2, 0, TrackRole.Melody, "主旋律"),
            MakeCandidate(3, 0, TrackRole.Melody, "副旋律"),
        };

        var pickExact = PlaylistModel.Pick(cands, new TrackRef { TrackIndex = 2, Channel = 0 });
        Check("挑音轨：按歌单预设精确命中",
            pickExact.Found && pickExact.Exact && pickExact.Candidate!.TrackIndex == 2,
            pickExact.Reason);

        var pickRole = PlaylistModel.Pick(cands, null, TrackRole.Bass);
        Check("挑音轨：没预设就接上一首的声部",
            pickRole.Found && pickRole.Candidate!.TrackIndex == 1, pickRole.Reason);

        var pickMissing = PlaylistModel.Pick(cands, new TrackRef { TrackIndex = 99, Channel = 0 }, TrackRole.Bass);
        Check("挑音轨：预设的行不在了就退到声部匹配",
            pickMissing.Found && !pickMissing.Exact && pickMissing.Candidate!.TrackIndex == 1,
            pickMissing.Reason);

        var onlyDrums = new List<MidiCandidate> { MakeCandidate(0, 9, TrackRole.Drums, "鼓") };
        var pickDrum = PlaylistModel.Pick(onlyDrums, null, TrackRole.Bass);
        Check("挑音轨：只剩打击乐轨也有兜底",
            pickDrum.Found && pickDrum.Candidate!.TrackIndex == 0, pickDrum.Reason);

        Check("挑音轨：一首没有候选行时挑不出来",
            !PlaylistModel.Pick(new List<MidiCandidate>()).Found);

        var twins = new List<MidiCandidate>
        {
            MakeCandidate(5, 0, TrackRole.Melody, "后一条"),
            MakeCandidate(1, 0, TrackRole.Melody, "前一条"),
        };
        var pickTwin = PlaylistModel.Pick(twins, null, TrackRole.Melody);
        Check("挑音轨：同声部取轨道号最小的",
            pickTwin.Found && pickTwin.Candidate!.TrackIndex == 1, pickTwin.Reason);
    }

    private static void TestPlaylistStore()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "midikey-selftest-playlists-" + Guid.NewGuid().ToString("N"));
        string dir = Path.Combine(tmp, "playlists");
        try
        {
            Directory.CreateDirectory(dir);
            PlaylistStore.DirPathForDev = dir;

            if (!string.Equals(PlaylistStore.DirPath, dir, StringComparison.OrdinalIgnoreCase))
            {
                Check("歌单：自检目录指向临时目录", false, $"实得 {PlaylistStore.DirPath}");
                return;
            }
            Check("歌单：自检目录指向临时目录", true, dir);

            string path = PlaylistStore.FilePathFor("回归歌单");
            var list = Playlist.Create("回归歌单");
            list.Entries.Add(new PlaylistEntry { Path = @"C:\示例\第一首.mid" });
            list.Entries[0].Tracks.Add(new TrackRef { TrackIndex = 1, Channel = 0, NameHint = "女声部" });
            list.Entries[0].Tracks.Add(new TrackRef { TrackIndex = 2, Channel = 9, NameHint = "鼓" });
            list.Entries.Add(new PlaylistEntry { Path = @"C:\示例\第二首.mid" });

            Check("歌单：保存成功", PlaylistStore.Save(list), path);
            Check("歌单：存完文件在", File.Exists(path), path);

            var back = PlaylistStore.Load("回归歌单");
            Check("歌单：读得回来", back != null);
            Check("歌单：曲目数对得上", back?.Count == 2, $"实得 {back?.Count}");
            Check("歌单：第一首的路径没丢",
                back?.Entries[0].Path == @"C:\示例\第一首.mid", back?.Entries[0].Path ?? "");
            Check("歌单：勾的两行都读得回来",
                back?.Entries[0].Tracks.Count == 2 &&
                back.Entries[0].Tracks[0] is { TrackIndex: 1, Channel: 0, NameHint: "女声部" } &&
                back.Entries[0].Tracks[1] is { TrackIndex: 2, Channel: 9, NameHint: "鼓" },
                back == null ? "null" : string.Join(" + ", back.Entries[0].Tracks.Select(t => t.Label)));
            Check("歌单：第二首没勾过（等于全弹）", back?.Entries[1].Tracks.Count == 0,
                $"实得 {back?.Entries[1].Tracks.Count} 行");

            string json = File.ReadAllText(path);
            Check("歌单：显示用的 label 不写进文件", !json.Contains("\"label\""), json.Length + " 字符");
            Check("歌单：单行字段只写第一行",
                json.Contains("\"track\"") && json.Contains("\"trackIndex\": 1"), "");

            File.WriteAllText(path, json.Replace("\"nameHint\": \"女声部\",", "\"nameHint\": \"女声部\", \"label\": \"轨道 2 / 声道 1\","), new UTF8Encoding(false));
            var legacy = PlaylistStore.Load("回归歌单");
            Check("歌单：旧文件里多出来的 label 字段不挡读盘",
                legacy?.Count == 2 && legacy.Entries[0].Tracks.Count == 2,
                legacy == null ? "读回来是 null" : $"实得 {legacy.Count} 首 / {legacy.Entries[0].Tracks.Count} 行");

            File.WriteAllText(path, """
                {
                  "name": "回归歌单",
                  "entries": [
                    { "path": "C:\\示例\\老文件.mid",
                      "track": { "trackIndex": 3, "channel": 1, "nameHint": "老单行" } }
                  ]
                }
                """, new UTF8Encoding(false));
            var older = PlaylistStore.Load("回归歌单");
            Check("歌单：只有单行字段的老文件读成一行勾选",
                older?.Entries[0].Tracks.Count == 1 &&
                older.Entries[0].Tracks[0] is { TrackIndex: 3, Channel: 1, NameHint: "老单行" },
                older == null ? "null" : $"{older.Entries[0].Tracks.Count} 行 / {older.Entries[0].Tracks.FirstOrDefault()?.Label}");

            var all = PlaylistStore.LoadAll();
            Check("歌单：列目录能列出来", all.Count == 1, $"实得 {all.Count} 份");
            Check("歌单：按名字找得到", all.Count > 0 && all[0].Name == "回归歌单",
                all.Count > 0 ? all[0].Name : "一份都没有");

            Check("歌单：删掉就没了", PlaylistStore.Delete("回归歌单") && !File.Exists(path));
        }
        catch (Exception ex)
        {
            Check("歌单：用例没抛异常", false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            PlaylistStore.DirPathForDev = null;
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
        }
    }

    private static MidiCandidate MakeCandidate(int track, int channel, TrackRole role, string name) =>
        new()
        {
            TrackIndex = track,
            Channel = channel,
            Role = role,
            Name = name,
            Notes = MakeNotes(60, 4, 0.5),
        };

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

    private static void Check(string name, bool ok, string detail = "")
    {
        if (!ok) _failed++;
        Lines.Add((ok ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? $"   [{detail}]" : ""));
    }
}

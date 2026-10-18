using System.Text.Json;
using System.Text.Json.Serialization;
using MidiKeyPlayer.Persist;

namespace MidiKeyPlayer.Engine;

[JsonConverter(typeof(OutOfRangeModeConverter))]
public enum OutOfRangeMode { Drop, Fold }

[JsonConverter(typeof(MissingNoteModeConverter))]
public enum MissingNoteMode { Skip, Up, Down }

[JsonConverter(typeof(KeyBindingConverter))]
public sealed class KeyBinding
{
    public string Key { get; set; } = "";

    public int Offset { get; set; }

    public int Row { get; set; }

    public bool Shift { get; set; }

    public override string ToString()
        => $"{Key}{(Shift ? "+Shift" : "")}{(Offset >= 0 ? "+" : "")}{Offset}@第{Row}行";
}

public sealed class KeymapFormatException : Exception
{
    public KeymapFormatException(string message) : base(message) { }
    public KeymapFormatException(string message, Exception inner) : base(message, inner) { }
}

public sealed class KeymapProfile
{
    public const string DefaultName = "8 键半音（五字搜打撤游戏）";

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

    public string? Flat { get; set; }

    public bool ModifiersEnabled { get; set; } = true;

    public int? MinNote { get; set; }

    public int? MaxNote { get; set; }

    public OutOfRangeMode OutOfRange { get; set; } = OutOfRangeMode.Drop;

    [JsonIgnore]
    public MissingNoteMode MissingNote
    {
        get => MissingNoteMode.Skip;
        set { }
    }

    internal static string? DirPathForDev { get; set; }

    private static string DirPath =>
        DirPathForDev ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MidiKeyPlayer");

    public static string FilePath => Path.Combine(DirPath, "keymap.json");

    private static KeymapProfile? _current;

    public static KeymapProfile Current
    {
        get => _current ??= Load();
        set => _current = value ?? BuildDefault();
    }

    private static IReadOnlyList<KeymapProfile>? _presets;

    public static IReadOnlyList<KeymapProfile> Presets => _presets ??= BuildPresets();

    public static KeymapProfile Default => BuildChromatic8();

    private static KeymapProfile BuildDefault() => BuildNatural21();

    private static KeymapProfile BuildNatural21()
    {
        var keys = new List<KeyBinding>();
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

    private static KeymapProfile BuildChromatic21()
    {
        var keys = new List<KeyBinding>();
        string[][] rows = { new[] { "Z", "X", "C", "V", "B", "N", "M" },
                            new[] { "A", "S", "D", "F", "G", "H", "J" },
                            new[] { "Q", "W", "E", "R", "T", "Y", "U" } };
        int[] degrees = { 0, 2, 4, 5, 7, 9, 11 };
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

    private static KeymapProfile BuildChromatic36()
    {
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

    private static KeymapProfile BuildChromatic8()
    {
        var keys = new List<KeyBinding>();
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

    private static KeymapProfile BuildHandpan()
    {
        var keys = new List<KeyBinding>();
        foreach (var (row, rowKeys, offsets) in new (int, string[], int[])[]
        {
            (0, new[] { "B" }, new[] { -3 }),                        
            (1, new[] { "F", "G", "H", "J", "K" }, new[] { 4, 5, 7, 9, 11 }),   
            (2, new[] { "T", "Y", "U" }, new[] { 12, 14, 16 }),      
        })
            for (int c = 0; c < rowKeys.Length; c++)
                keys.Add(new KeyBinding { Key = rowKeys[c], Offset = offsets[c], Row = row });

        return new KeymapProfile
        {
            Version = CurrentVersion,
            Description = "手碟键位：T Y U 是高音 do re mi，F G H J K 是中音 mi fa sol la si，B 是低音 la",
            BaseNote = 60,
            Keys = keys,
            ModifiersEnabled = false,
            OctaveUp = null,
            OctaveDown = null,
            Sharp = null,
        };
    }

    private static KeymapProfile BuildRobloxPiano()
    {
        var keys = new List<KeyBinding>();
        (int Row, string[] Whites, int[] Offsets)[] rows =
        {
            (0, new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" },
                new[] { -24, -22, -20, -19, -17, -15, -13, -12, -10, -8 }),      
            (1, new[] { "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P" },
                new[] { -7, -5, -3, -1, 0, 2, 4, 5, 7, 9 }),                      
            (2, new[] { "A", "S", "D", "F", "G", "H", "J", "K", "L" },
                new[] { 11, 12, 14, 16, 17, 19, 21, 23, 24 }),                    
            (3, new[] { "Z", "X", "C", "V", "B", "N", "M" },
                new[] { 26, 28, 29, 31, 33, 35, 36 }),                            
        };

        foreach (var (row, whites, offsets) in rows)
            for (int c = 0; c < whites.Length; c++)
            {
                keys.Add(new KeyBinding { Key = whites[c], Offset = offsets[c], Row = row });
                int pitch = 60 + offsets[c];
                if (HasSharpAbove(pitch) && pitch + 1 <= 96)
                    keys.Add(new KeyBinding { Key = whites[c], Offset = offsets[c] + 1, Row = row, Shift = true });
            }

        return new KeymapProfile
        {
            Version = CurrentVersion,
            Description = "Roblox 钢琴键位（61 键）：一个半音一条键位，白键是 1 2 3 4 5 6 7 8 9 0 / "
                        + "Q W E R T Y U I O P / A S D F G H J K L / Z X C V B N M，"
                        + "黑键是同一个键加 Shift（显示成 ! @ $ % ^ * ( 与 Q W E …）。能弹 C2 到 C7",
            BaseNote = 60,
            Keys = keys,
            ModifiersEnabled = false,
            OctaveUp = null,
            OctaveDown = null,
            Sharp = null,
        };
    }

    private static KeymapProfile BuildFf14Piano()
    {
        var keys = new List<KeyBinding>();
        (int Row, string[] Keys)[] rows =
        {
            (0, new[] { "Z", "1", "X", "2", "C", "V", "3", "B", "4", "N", "5", "M" }),   
            (1, new[] { "A", "6", "S", "7", "D", "F", "8", "G", "9", "H", "0", "J" }),   
            (2, new[] { "K", "Y", "L", "U", "Q", "W", "I", "E", "O", "R", "P", "T" }),   
            (3, new[] { "," }),                                                          
        };

        foreach (var (row, rowKeys) in rows)
            for (int c = 0; c < rowKeys.Length; c++)
                keys.Add(new KeyBinding { Key = rowKeys[c], Offset = 12 * row + c, Row = row });

        return new KeymapProfile
        {
            Version = CurrentVersion,
            Description = "FF14 钢琴键位（37 键）：一个半音一条键位，三排各 12 个半音。"
                        + "低八度 Z 1 X 2 C V 3 B 4 N 5 M、中八度 A 6 S 7 D F 8 G 9 H 0 J、"
                        + "高八度 K Y L U Q W I E O R P T，最后 , 是最高一个 do。能弹 C4 到 C7",
            BaseNote = 60,
            Keys = keys,
            ModifiersEnabled = false,
            OctaveUp = null,
            OctaveDown = null,
            Sharp = null,
        };
    }

    private static KeymapProfile BuildXindong()
    {
        var keys = new List<KeyBinding>();
        (int Row, string[] Keys)[] rows =
        {
            (0, new[] { ",", "L", ".", ";", "/", "O", "0", "P", "-", "[", "=", "]" }),   
            (1, new[] { "Z", "S", "X", "D", "C", "V", "G", "B", "H", "N", "J", "M" }),   
            (2, new[] { "Q", "2", "W", "3", "E", "R", "5", "T", "6", "Y", "7", "U", "I" }), 
        };

        foreach (var (row, rowKeys) in rows)
            for (int c = 0; c < rowKeys.Length; c++)
                keys.Add(new KeyBinding { Key = rowKeys[c], Offset = 12 * (row - 1) + c, Row = row });

        return new KeymapProfile
        {
            Version = CurrentVersion,
            Description = "心动小镇（37 键）：一个半音一条键位，三排各一个八度。"
                        + "低音排白键 , . / O P [ ] 加黑键 L ; 0 - =、"
                        + "中音排白键 Z X C V B N M 加黑键 S D G H J、"
                        + "高音排白键 Q W E R T Y U I 加黑键 2 3 5 6 7。能弹 C3 到 C6",
            BaseNote = 60,
            Keys = keys,
            ModifiersEnabled = false,
            OctaveUp = null,
            OctaveDown = null,
            Sharp = null,
        };
    }

    internal static KeymapProfile BuildXindongV1117()
    {
        var keys = new List<KeyBinding>();
        (int Row, string[] Keys)[] rows =
        {
            (0, new[] { ".", ",", ";", "'", "/", "O", "0", "P", "-", "[", "=", "]" }),   
            (1, new[] { "Z", "S", "X", "D", "C", "V", "G", "B", "H", "N", "J", "M" }),   
            (2, new[] { "Q", "1", "W", "2", "E", "R", "4", "T", "5", "Y", "6", "U", "I" }), 
        };

        foreach (var (row, rowKeys) in rows)
            for (int c = 0; c < rowKeys.Length; c++)
                keys.Add(new KeyBinding { Key = rowKeys[c], Offset = 12 * (row - 1) + c, Row = row });

        return new KeymapProfile
        {
            Version = CurrentVersion,
            Name = "心动小镇",
            Description = "心动小镇（37 键）：一个半音一条键位，三排各一个八度。"
                        + "低音排白键 . ; / O P [ ] 加黑键 , ' 0 - =、"
                        + "中音排白键 Z X C V B N M 加黑键 S D G H J、"
                        + "高音排白键 Q W E R T Y U I 加黑键 1 2 4 5 6。能弹 C3 到 C6",
            BaseNote = 60,
            Keys = keys,
            ModifiersEnabled = false,
            OctaveUp = null,
            OctaveDown = null,
            Sharp = null,
        };
    }

    private static readonly Dictionary<string, KeymapProfile> LegacyBuiltInRevisions = new(StringComparer.Ordinal)
    {
        ["心动小镇"] = BuildXindongV1117(),   
    };

    public static bool IsUntouchedLegacyCopy(string? name, KeymapProfile? profile)
    {
        if (string.IsNullOrWhiteSpace(name) || profile == null) return false;
        if (!LegacyBuiltInRevisions.TryGetValue(name, out var old)) return false;
        return string.Equals(KeySignature(old), KeySignature(profile), StringComparison.Ordinal);
    }

    private static string KeySignature(KeymapProfile profile)
    {
        var parts = new List<string>();
        foreach (var k in profile.Keys)
        {
            if (k == null) continue;
            parts.Add($"{CanonicalKeyName(k.Key)}/{k.Offset}/{k.Row}/{(k.Shift ? 1 : 0)}");
        }
        parts.Add("说明：" + (profile.Description ?? ""));
        return string.Join(";", parts);
    }

    private static bool HasSharpAbove(int pitch) => pitch % 12 is 0 or 2 or 5 or 7 or 9;

    public static string SchemeNameOf(KeymapProfile profile)
    {
        int keys = profile.Keys.Count(k => k != null && !string.IsNullOrWhiteSpace(k.Key));
        return $"{keys} 键 {RowCountOf(profile)} 排 {OctaveCountOf(profile)} 个八度";
    }

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

    private static IReadOnlyList<KeymapProfile> BuildPresets()
    {
        var list = new List<KeymapProfile>
        {
            Preset(BuildChromatic8(), DefaultName),           
            Preset(BuildDefault(), "21 键自然音"),             
            Preset(BuildChromatic21(), "21 键半音"),           
            Preset(BuildChromatic36(), "第五人格键位"),         
            Preset(BuildHandpan(), "洛克王国手碟"),             
            Preset(BuildRobloxPiano(), "Roblox 钢琴键位"),       
            Preset(BuildFf14Piano(), "FF14 钢琴键位"),           
            Preset(BuildXindong(), "心动小镇"),                  
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

    private static readonly Dictionary<string, string> LegacyPresetAlias = new(StringComparer.Ordinal)
    {
        ["21 键 3 排 3 个八度"] = "21 键自然音",
        ["21 键 3 排 2 个八度"] = "21 键半音",
        ["36 键 4 排 3 个八度"] = "第五人格键位",
        ["36 键 3 排 3 个八度"] = "第五人格键位",
        ["36 键半音三排"] = "第五人格键位",
        ["8 键单排（含高八度 do，默认）"] = DefaultName,
        ["8 键 1 排 3 个八度"] = DefaultName,
        ["8 键半音"] = DefaultName,
        ["异环键位"] = "21 键半音",
    };

    private static readonly HashSet<string> RemovedPresetNames = new(StringComparer.Ordinal)
    {
        "7 键 1 排 1 个八度",
        "7 键单排自然音阶",
        "15 键 3 排 2 个八度",
        "15 键三排",
        "23 键 3 排 3 个八度",
        "23 键三排自然音阶",
        "12 键 2 排 1 个八度",
        "24 键 2 排 2 个八度",
        "12 键半音阶双排",
        "5 键极简",
        "宽音域 8 八度折叠",
    };

    public static string? AliasOf(string? name)
        => !string.IsNullOrWhiteSpace(name) && LegacyPresetAlias.TryGetValue(name!, out string? now) ? now : null;

    public static bool IsRemovedPresetName(string? name)
        => !string.IsNullOrWhiteSpace(name) && RemovedPresetNames.Contains(name!);

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

    public static string SchemesDir =>
        Path.Combine(DirPath, "schemes");

    public static string SchemeFilePath(string? name)
    {
        string safe = (name ?? "").Trim();
        foreach (char bad in Path.GetInvalidFileNameChars()) safe = safe.Replace(bad, '_');
        if (safe.Length == 0) safe = "未命名方案";
        return Path.Combine(SchemesDir, safe + ".json");
    }

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

    public static bool IsBuiltInSchemeName(string? name) => PresetByName(name) != null;

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

    public static KeymapProfile? LoadByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        if (TryLoadSchemeFile(name, out var user))
        {
            if (user != null && FreshBuiltInForStaleCopy(user) is KeymapProfile fresh)
            {
                LogFile.Append($"[键位] 方案「{name}」的用户副本还是旧版内置键位，已换成当前内置键位。");
                TryWriteSchemeFile(fresh);
                return fresh;
            }
            return user;
        }

        var preset = PresetByName(name);
        if (preset != null) return preset.Clone();

        LogFile.Append($"[键位] 没有方案文件：{SchemeFilePath(name)}");
        return null;
    }

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

    private static KeymapProfile? FreshBuiltInForStaleCopy(KeymapProfile copy)
    {
        if (!IsUntouchedLegacyCopy(copy.Name, copy)) return null;
        return PresetByName(copy.Name)?.Clone();
    }

    public static void TryWriteSchemeFile(KeymapProfile profile)
    {
        try
        {
            Directory.CreateDirectory(SchemesDir);
            File.WriteAllText(SchemeFilePath(profile.Name), profile.ToJson(), new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            LogFile.Append($"[键位] 写方案文件失败（{profile.Name}）：{ex.Message}");
        }
    }

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
                    var use = fixedUp;
                    if (FreshBuiltInForStaleCopy(fixedUp) is KeymapProfile fresh)
                    {
                        use = fresh;
                        string extra = $"[键位] 活动方案「{fixedUp.Name}」还是旧版内置键位，已换成当前内置键位。";
                        note = note.Length > 0 ? note + "\n" + extra : extra;
                    }
                    if (note.Length > 0)
                    {
                        LogFile.Append(note);
                        try { use.Save(); } catch { }
                    }
                    return use;
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

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DirPath);
            WriteAllTextAtomic(FilePath, ToJson());
        }
        catch (Exception ex)
        {
            LogFile.Append("[键位] 保存 keymap.json 失败：" + ex.Message);
        }
    }

    private static void WriteAllTextAtomic(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    public bool TryExportFile(string path, out string error)
    {
        error = "";
        try
        {
            string dir = Path.GetDirectoryName(path) ?? "";
            if (dir.Length > 0) Directory.CreateDirectory(dir);
            WriteAllTextAtomic(path, ToJson());
            return true;
        }
        catch (Exception ex)
        {
            error = $"写文件失败：{ex.Message}";
            return false;
        }
    }

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

    public string ToJson() => JsonSerializer.Serialize(this, KeymapJson.Default.KeymapProfile);

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
        if (profile.MinNote.HasValue) profile.MinNote = Math.Clamp(profile.MinNote.Value, 0, 127);
        if (profile.MaxNote.HasValue) profile.MaxNote = Math.Clamp(profile.MaxNote.Value, 0, 127);
        return profile;
    }

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

    public int BaseOctave => BaseNote / 12 - 1;

    public bool CanUseOctaveUp => ModifiersEnabled && !string.IsNullOrWhiteSpace(OctaveUp);
    public bool CanUseOctaveDown => ModifiersEnabled && !string.IsNullOrWhiteSpace(OctaveDown);
    public bool CanUseSharp => ModifiersEnabled && !string.IsNullOrWhiteSpace(Sharp);
    public bool CanUseFlat => ModifiersEnabled && !string.IsNullOrWhiteSpace(Flat);

    public bool HasSelfShiftKeys => Keys.Any(k => k != null && k.Shift);

    public string? SharpKeyToHold
        => !string.IsNullOrWhiteSpace(Sharp) ? Sharp : (HasSelfShiftKeys ? "Shift" : null);

    public int ResolveMinNote() => ReachableExtent().Lo;

    public int ResolveMaxNote() => ReachableExtent().Hi;

    public (int Lo, int Hi) ReachableExtent()
    {
        if (!TryReachableBounds(out int lo, out int hi))
            return (Math.Clamp(BaseNote, 0, 127), Math.Clamp(BaseNote, 0, 127));
        if (lo > hi) (lo, hi) = (hi, lo);
        return (Math.Clamp(lo, 0, 127), Math.Clamp(hi, 0, 127));
    }

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

    public bool InRange(int pitch)
    {
        var (lo, hi) = ReachableExtent();
        return pitch >= lo && pitch <= hi;
    }

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

    public bool TryKeyOfPitch(int pitch, out string key, out int octaveOffset, out bool sharp)
        => TryKeyOfPitch(pitch, out key, out octaveOffset, out sharp, out _, out _);

    public bool TryKeyOfPitch(int pitch, out string key, out int octaveOffset, out bool sharp,
                              out int soundingPitch)
        => TryKeyOfPitch(pitch, out key, out octaveOffset, out sharp, out _, out soundingPitch);

    public bool TryKeyOfPitch(int pitch, out string key, out int octaveOffset,
                              out bool sharp, out bool flat, out int soundingPitch)
    {
        key = "";
        octaveOffset = 0;
        sharp = false;
        flat = false;
        soundingPitch = pitch;
        if (Keys.Count == 0) return false;
        if (!InRange(pitch)) return false;   

        int want = pitch;
        bool canSharp = CanUseSharp;
        bool canFlat = CanUseFlat;
        bool canUp = CanUseOctaveUp;
        bool canDown = CanUseOctaveDown;

        static int RankOf(int s) => s == 0 ? 0 : (s > 0 ? 1 : 2);

        bool found = false;
        int bestRank = int.MaxValue;
        int bestOffset = int.MaxValue;
        int bestModAbs = int.MaxValue;
        int bestIndex = int.MaxValue;
        string bestKey = "";
        int bestMod = 0;
        int bestS = 0;
        bool bestSelfShift = false;

        for (int mod = -1; mod <= 1; mod++)
        {
            if (mod < 0 && !canDown) continue;
            if (mod > 0 && !canUp) continue;

            for (int i = 0; i < Keys.Count; i++)
            {
                var kb = Keys[i];
                if (kb == null || string.IsNullOrEmpty(kb.Key)) continue;
                if (KeyCharOf(kb.Key) == '\0') continue;   

                int basePitch = BaseNote + kb.Offset + 12 * mod;
                for (int s = -1; s <= 1; s++)
                {
                    if (s > 0 && !canSharp) continue;
                    if (s < 0 && !canFlat) continue;
                    int p = basePitch + s;
                    if (p != want) continue;               

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
                    bestSelfShift = kb.Shift;
                }
            }
        }

        if (!found) return false;

        key = bestKey;
        octaveOffset = bestMod;
        sharp = bestS > 0 || bestSelfShift;
        flat = bestS < 0;
        soundingPitch = want;
        return true;
    }

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
            copy.Keys.Add(new KeyBinding { Key = k.Key, Offset = k.Offset, Row = k.Row, Shift = k.Shift });
        }
        return copy;
    }

    private const char NamedKeyBase = '\uE000';

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

    private const string SingleCharKeys = ",.;/'\\[]-=`";

    public const string SingleCharPool = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789" + SingleCharKeys;

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

    public static bool IsKnownKeyName(string? keyName) => CanonicalKeyName(keyName).Length > 0;

    private static readonly Dictionary<char, char> ShiftedChars = new()
    {
        ['1'] = '!', ['2'] = '@', ['3'] = '#', ['4'] = '$', ['5'] = '%',
        ['6'] = '^', ['7'] = '&', ['8'] = '*', ['9'] = '(', ['0'] = ')',
        ['-'] = '_', ['='] = '+', ['['] = '{', [']'] = '}', ['\\'] = '|',
        [';'] = ':', ['\''] = '"', [','] = '<', ['.'] = '>', ['/'] = '?', ['`'] = '~',
    };

    public static string ShiftedNameOf(string? keyName)
    {
        string name = CanonicalKeyName(keyName);
        if (name.Length == 0) return "";
        if (name.Length > 1) return $"Shift+{name}";
        char c = name[0];
        if (c is >= 'A' and <= 'Z') return c.ToString();                 
        if (ShiftedChars.TryGetValue(c, out char shifted)) return shifted.ToString();
        return $"Shift+{name}";
    }

    public static string DisplayNameOf(KeyBinding? binding)
        => binding == null || string.IsNullOrWhiteSpace(binding.Key)
            ? ""
            : (binding.Shift ? ShiftedNameOf(binding.Key) : CanonicalKeyName(binding.Key));

    public static char KeyCharOf(string? keyName)
    {
        string name = CanonicalKeyName(keyName);
        if (name.Length == 0) return '\0';
        if (name.Length == 1) return name[0];
        int i = Array.IndexOf(NamedKeys, name);
        return i < 0 ? '\0' : (char)(NamedKeyBase + i);
    }

    public static string NameOfKeyChar(char c)
    {
        if (c == '\0') return "";
        if (c >= NamedKeyBase && c < NamedKeyBase + NamedKeys.Length) return NamedKeys[c - NamedKeyBase];
        return c.ToString();
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(KeymapProfile))]
internal sealed partial class KeymapJson : JsonSerializerContext
{
}

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

    private static OutOfRangeMode LegacyFold()
    {
        LogFile.Append("[键位] outOfRange 旧值 fold 已停用：超出能弹范围的音固定不弹，不再折八度。");
        return OutOfRangeMode.Fold;
    }

    public override void Write(Utf8JsonWriter writer, OutOfRangeMode value, JsonSerializerOptions options)
        => writer.WriteStringValue(value == OutOfRangeMode.Fold ? "fold" : "drop");
}

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

        if (!reader.Read())
            throw new JsonException("keys 里的项不完整。");
        int row = 0;
        if (reader.TokenType == JsonTokenType.Number)
        {
            if (!reader.TryGetInt32(out row))
                throw new JsonException($"键「{name}」的行号要写整数。");
        }

        bool shift = false;
        if (reader.TokenType != JsonTokenType.EndArray)
        {
            if (!reader.Read())
                throw new JsonException("keys 里的项不完整。");
            if (reader.TokenType == JsonTokenType.True) shift = true;
            else if (reader.TokenType == JsonTokenType.False) shift = false;
            else if (reader.TokenType == JsonTokenType.Number)
            {
                if (!reader.TryGetInt32(out int shiftValue))
                    throw new JsonException($"键「{name}」的 Shift 标记要写整数或 true / false。");
                shift = shiftValue != 0;
            }
        }

        if (reader.TokenType != JsonTokenType.EndArray)
        {
            if (!reader.Read())
                throw new JsonException("keys 里的项不完整。");
            while (reader.TokenType != JsonTokenType.EndArray)
            {
                if (!reader.Read()) throw new JsonException("keys 里的项不完整。");   
            }
        }

        return new KeyBinding { Key = name, Offset = offset, Row = row, Shift = shift };
    }

    public override void Write(Utf8JsonWriter writer, KeyBinding value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteStringValue(value.Key);
        writer.WriteNumberValue(value.Offset);
        writer.WriteNumberValue(value.Row);
        if (value.Shift) writer.WriteBooleanValue(true);
        writer.WriteEndArray();
    }
}

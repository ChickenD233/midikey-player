using System.Text.Json;
using System.Text.Json.Serialization;
using MidiKeyPlayer.Engine;

namespace MidiKeyPlayer.Persist;

/// <summary>某个键位方案单独记住的速度 / 移调 / 输入兼容档。</summary>
public sealed class ProfileSettings
{
    /// <summary>速度（%）。</summary>
    public int Speed { get; set; } = 100;
    /// <summary>移调（半音）。</summary>
    public int Transpose { get; set; } = 0;
    /// <summary>输入兼容档位：0 稳健 / 1 标准 / 2 极限。</summary>
    public int TimingIndex { get; set; } = 1;
}

/// <summary>用户设置：退出后记住，下次启动自动恢复。</summary>
public sealed class AppConfig
{
    // 速度 / 移调的合法区间：界面滑块（10%–400%、±24）、引擎与实时演奏都用这一套口径（FN-02 / VR-10）。
    public const int MinSpeedPercent = 10;
    public const int MaxSpeedPercent = 400;
    public const int MaxTransposeSemitones = 24;

    private int _speed = 100;
    private int _transpose;

    public int Speed          // %
    {
        get => _speed;
        set => _speed = Math.Clamp(value, MinSpeedPercent, MaxSpeedPercent);
    }

    public int Transpose      // 半音
    {
        get => _transpose;
        set => _transpose = Math.Clamp(value, -MaxTransposeSemitones, MaxTransposeSemitones);
    }

    public int CountdownIndex { get; set; } = 1;   // 0秒/3/5/10
    public int ControlHotkeyIndex { get; set; } = 6; // 统一控制键（默认 F6：开始/暂停/继续）
    public int RewindHotkeyIndex { get; set; } = 5;  // 后退热键（默认 F5）
    public int ForwardHotkeyIndex { get; set; } = 7; // 前进热键（默认 F7）
    public int PrevSongHotkeyIndex { get; set; } = 8;   // 上一首热键（默认 F8，文件夹曲目）
    public int NextSongHotkeyIndex { get; set; } = 9;   // 下一首热键（默认 F9，文件夹曲目）
    public bool TrimLead { get; set; } = true;        // 去除开头空拍（首音平移到 0 秒）
    public bool FirstRunDone { get; set; } = false;   // 首次“快速上手”是否已看过
    public bool AutoMinimizeOnPlay { get; set; } = true;  // 播放开始后自动最小化窗口
    public bool FocusGuard { get; set; } = true;          // 焦点离开目标程序时自动暂停（按键只进游戏）
    public bool ShowPreflight { get; set; } = true;   // 主界面状态卡里显示播放前自检（默认开）
    public int ThemeMode { get; set; } = 0;           // 皮肤：0 自动（跟随系统）/ 1 浅色 / 2 深色
    public int TimingIndex { get; set; } = 1;         // 输入兼容档位：0稳健/1标准/2极限
    public int InputBackend { get; set; } = 0;        // 输入方式：0 SendInput（默认）/ 1 罗技 G HUB 驱动
    public string SkippedUpdateTag { get; set; } = "";   // 用户选择“跳过”的版本号（空=不跳过）
    public bool DisclaimerAccepted { get; set; } = false;  // 免责声明确认过一次后不再显示
    public string LastRunVersion { get; set; } = "";    // 上次运行的版本号（自动更新成功确认用）

    // —— 播放悬浮窗（倒计时 / 进度 / 当前音，置顶显示在目标程序上）——
    public bool OverlayEnabled { get; set; } = true;    // 悬浮窗开关（默认开）
    public int OverlayX { get; set; } = -1;             // 悬浮窗位置（-1 = 默认屏幕右上角）
    public int OverlayY { get; set; } = -1;

    // —— 键位方案 ——
    public string KeymapName { get; set; } = KeymapProfile.DefaultName;  // 当前键位方案名
    // 原来的 ChordMode（保留和弦）字段已删除：该功能移除后行为固定为「演奏 MIDI 里的全部音」。
    // 老设置文件里的 "ChordMode" 是未知成员，源生成默认 UnmappedMemberHandling.Skip，读盘时直接忽略。

    /// <summary>按方案名分别记住的速度 / 移调 / 输入兼容档。键是方案名。</summary>
    public Dictionary<string, ProfileSettings> PerProfile { get; set; } = new();

    // —— MIDI 设备接入（issue #4）——
    public string MidiDeviceName { get; set; } = "";     // 上次用的 MIDI 输入设备名（空=没选过）
    public bool MidiLiveEnabled { get; set; } = false;   // 设备实时演奏开关（默认关，避免误触发）
    public int MidiBaseOctave { get; set; } = 4;         // 基准八度（乐器中音 do 所在的 MIDI 八度）
    public int MidiMinVelocity { get; set; } = 1;        // 力度下限（1 = 不过滤）
    public bool MidiAutoFit { get; set; } = true;        // 自动贴合音域

    // —— MIDI 文件「最近打开」——
    /// <summary>「最近打开」最多记这么多条，超出丢最旧的。</summary>
    public const int MaxRecentFiles = 10;

    private List<string> _recentFiles = new();

    /// <summary>
    /// 「最近打开」的 MIDI 文件路径：最新的排最前，最多 <see cref="MaxRecentFiles"/> 条。
    /// 只记路径，不复制文件。老设置文件没有这个字段时是空列表。
    /// </summary>
    public List<string> RecentFiles
    {
        get => _recentFiles;
        set => _recentFiles = value ?? new List<string>();   // 设置文件里写成 null 也不能崩
    }

    // —— MIDI 文件「文件夹曲目」目录（左栏常驻卡，issue #57）——
    private string _folderPath = "";

    /// <summary>
    /// 上次列出曲目的文件夹：左栏「文件夹曲目」卡就列这个目录里的 MIDI。
    /// 空 = 没选过（或用户点过「关闭」），启动时卡片隐藏。
    /// 老设置文件没有这个字段时是空串。目录已经不在时由界面清空，见 MainWindow.RestoreFolderFromConfig。
    /// </summary>
    public string FolderPath
    {
        get => _folderPath;
        set => _folderPath = value ?? "";   // 设置文件里写成 null 也不能崩
    }

    /// <summary>旧版本程序（HarpAutoPlayer）的设置目录名。</summary>
    private const string LegacyDirName = "HarpAutoPlayer";

    private static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string DirPath => Path.Combine(LocalAppData, "MidiKeyPlayer");

    private static string FilePath => Path.Combine(DirPath, "settings.json");

    private static string LegacyFilePath =>
        Path.Combine(LocalAppData, LegacyDirName, "settings.json");

    // ================= 按方案记忆 =================

    /// <summary>
    /// 取某个方案单独记住的设置；没有就用当前的全局值当初始值建一条。
    /// name 为空时按默认方案名算。
    /// </summary>
    public ProfileSettings SettingsFor(string? name)
    {
        PerProfile ??= new Dictionary<string, ProfileSettings>();
        string key = string.IsNullOrWhiteSpace(name) ? KeymapProfile.DefaultName : name!;
        if (!PerProfile.TryGetValue(key, out var s) || s == null)
        {
            s = new ProfileSettings
            {
                Speed = Speed,
                Transpose = Transpose,
                TimingIndex = TimingIndex,
            };
            PerProfile[key] = s;
        }
        return s;
    }

    /// <summary>记住某个方案用的速度 / 移调 / 输入档位。</summary>
    public void RememberProfile(string? name, int speed, int transpose, int timingIndex)
    {
        var s = SettingsFor(name);
        // 与界面滑块、引擎同口径（FN-02 / VR-10）：速度 10%–400%，移调 ±24
        s.Speed = Math.Clamp(speed, MinSpeedPercent, MaxSpeedPercent);
        s.Transpose = Math.Clamp(transpose, -MaxTransposeSemitones, MaxTransposeSemitones);
        s.TimingIndex = Math.Clamp(timingIndex, 0, 2);
    }

    // ================= 最近打开 =================

    /// <summary>把一个路径记进「最近打开」：同路径只留一条、最新的排最前、超出上限丢最旧的。</summary>
    public void RememberRecentFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        // 大小写不敏感：Windows 上同一条路径可能写成不同的大小写
        _recentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _recentFiles.Insert(0, path);
        TrimRecentFiles();
    }

    /// <summary>把某个路径从「最近打开」里去掉（文件已被删或改名时用）。</summary>
    public void ForgetRecentFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        _recentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>清空「最近打开」列表。</summary>
    public void ClearRecentFiles() => _recentFiles.Clear();

    /// <summary>去掉空项并截到上限：读盘后（见 <see cref="Normalize"/>）与每次记录后都走一遍。</summary>
    private void TrimRecentFiles()
    {
        _recentFiles.RemoveAll(string.IsNullOrWhiteSpace);
        if (_recentFiles.Count > MaxRecentFiles)
            _recentFiles.RemoveRange(MaxRecentFiles, _recentFiles.Count - MaxRecentFiles);
    }

    /// <summary>
    /// 把读盘得到的值夹回合法区间：老设置文件里可能存着 500% 或 ±30。
    /// 写盘走 <see cref="Speed"/> / <see cref="Transpose"/> 的属性 setter，与这里同一套口径（FN-02 / VR-10）。
    /// </summary>
    public void Normalize()
    {
        Speed = Speed;
        Transpose = Transpose;
        TrimRecentFiles();   // 最近打开：老设置文件里可能超过 10 条，也可能带空串
        if (PerProfile == null) return;
        foreach (var s in PerProfile.Values)
        {
            if (s == null) continue;
            s.Speed = Math.Clamp(s.Speed, MinSpeedPercent, MaxSpeedPercent);
            s.Transpose = Math.Clamp(s.Transpose, -MaxTransposeSemitones, MaxTransposeSemitones);
            // D11：每方案的输入档位也要夹。漏了这一行，脏设置文件里的 99 会一路传到 InputTiming.FromIndex
            s.TimingIndex = Math.Clamp(s.TimingIndex, 0, 2);
        }
    }

    // ================= 读盘 / 写盘 =================

    public static AppConfig Load()
    {
        bool existed = File.Exists(FilePath);
        try
        {
            if (existed)
            {
                var cfg = JsonSerializer.Deserialize(File.ReadAllText(FilePath), ConfigJson.Default.AppConfig);
                if (cfg != null)
                {
                    // 老用户升级：设置文件已存在就不算“首次”，不弹快速上手
                    cfg.FirstRunDone = true;
                    cfg.PerProfile ??= new Dictionary<string, ProfileSettings>();
                    cfg.Normalize();   // 读盘与写盘同口径
                    return cfg;
                }
            }
        }
        catch (Exception ex) { LogFile.Append("[设置] 读取失败，用默认值：" + ex.Message); }

        // 首次启动（新目录还没有 settings.json）：旧目录有设置就迁移一次
        if (!existed)
        {
            var migrated = TryLoadLegacy();
            if (migrated != null)
            {
                migrated.FirstRunDone = true;
                migrated.PerProfile ??= new Dictionary<string, ProfileSettings>();
                migrated.Normalize();   // 旧设置文件可能是 500% / ±30，按新口径夹回
                migrated.Save();   // 只迁移一次：写进新目录后，下次就走新目录
                return migrated;
            }
        }
        return new AppConfig();
    }

    /// <summary>读旧目录 HarpAutoPlayer 的设置。字段对不上或读不动都只写日志，返回 null。</summary>
    private static AppConfig? TryLoadLegacy()
    {
        try
        {
            if (!File.Exists(LegacyFilePath)) return null;
            var cfg = JsonSerializer.Deserialize(File.ReadAllText(LegacyFilePath), ConfigJson.Default.AppConfig);
            if (cfg == null)
            {
                LogFile.Append("[设置] 旧目录设置是空的，不迁移。");
                return null;
            }
            LogFile.Append("[设置] 已从旧目录 HarpAutoPlayer 迁移设置（速度、移调、热键、MIDI 等）。");
            return cfg;
        }
        catch (Exception ex)
        {
            LogFile.Append("[设置] 旧目录设置迁移失败（忽略，不影响启动）：" + ex.Message);
            return null;
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DirPath);
            string json = JsonSerializer.Serialize(this, ConfigJson.Default.AppConfig);
            // 原子写入：先写临时文件再替换。直接覆盖写时若中途崩溃，settings.json 会被截断，
            // 下次启动读到半个 JSON，Load 的 catch 会静默回退默认值（速度、热键、方案记忆全丢）。
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) { LogFile.Append("[设置] 保存失败：" + ex.Message); }
    }
}

/// <summary>
/// 源生成的 JSON 上下文。设置读写必须走它，不能用 JsonSerializer 的反射重载。
///
/// 当前发布**开裁剪**（csproj 里 PublishTrimmed=true，反射相关程序集用
/// TrimmerRootAssembly 钉住）。设置这条链上则已经彻底不用反射：源生成在编译期产出读写代码。
///
/// 这条链原来踩过坑，所以固定成源生成：反射式序列化依赖的元数据一旦被裁掉，
/// 运行时抛异常。而 Save / Load 原先都静默吞掉异常 —— 结果是设置从未写盘，
/// 表现为「每次启动都弹快速上手」，而且速度、移调、热键全都不记忆。
/// 现在即便将来重新开裁剪，设置也不会跟着坏。
///
/// 键位方案（<see cref="KeymapProfile"/>）也一起登记，方案文件与设置走同一套源生成代码。
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(ProfileSettings))]
[JsonSerializable(typeof(KeymapProfile))]
// 「最近打开」的路径列表。它挂在 AppConfig 上，本来也会被连带生成；这里显式登记一次，
// 免得哪天元数据缺失时 Save() 只写一条日志，表现为「列表记不住」这种静默失效。
[JsonSerializable(typeof(List<string>))]
internal sealed partial class ConfigJson : JsonSerializerContext
{
}

/// <summary>本地运行日志（便于回传排查）。</summary>
public static class LogFile
{
    private static readonly object Gate = new();
    private const long MaxBytes = 2 * 1024 * 1024;

    private static string DirPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MidiKeyPlayer");

    private static string FilePath => Path.Combine(DirPath, "play.log");

    public static void Append(string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirPath);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    try { File.Copy(FilePath, FilePath + ".old", true); } catch { }
                    File.Delete(FilePath);
                }
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
        }
        catch { /* 日志失败不影响使用 */ }
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using MidiKeyPlayer.Engine;

namespace MidiKeyPlayer.Persist;

public sealed class ProfileSettings
{
    public int Speed { get; set; } = 100;
    public int Transpose { get; set; } = 0;
    public int TimingIndex { get; set; } = 1;
}

public sealed class AppConfig
{
    public const int MinSpeedPercent = 10;
    public const int MaxSpeedPercent = 400;
    public const int MaxTransposeSemitones = 24;

    private int _speed = 100;
    private int _transpose;

    public int Speed          
    {
        get => _speed;
        set => _speed = Math.Clamp(value, MinSpeedPercent, MaxSpeedPercent);
    }

    public int Transpose      
    {
        get => _transpose;
        set => _transpose = Math.Clamp(value, -MaxTransposeSemitones, MaxTransposeSemitones);
    }

    public int CountdownIndex { get; set; } = 1;   
    public int ControlHotkeyIndex { get; set; } = 6; 
    public int RewindHotkeyIndex { get; set; } = 5;  
    public int ForwardHotkeyIndex { get; set; } = 7; 
    public int PrevSongHotkeyIndex { get; set; } = 8;   
    public int NextSongHotkeyIndex { get; set; } = 9;   
    public bool TrimLead { get; set; } = true;
    public bool FoldOctave { get; set; } = false;
    public bool FirstRunDone { get; set; } = false;   
    public bool AutoMinimizeOnPlay { get; set; } = true;  
    public bool FocusGuard { get; set; } = true;          
    public bool ShowPreflight { get; set; } = true;   
    public int ThemeMode { get; set; } = 0;           
    public int TimingIndex { get; set; } = 1;         
    public int InputBackend { get; set; } = 0;        
    public string SkippedUpdateTag { get; set; } = "";   
    public bool DisclaimerAccepted { get; set; } = false;  
    public string LastRunVersion { get; set; } = "";
    public string NoticeShownVersion { get; set; } = "";
    public bool AuthorQuizPassed { get; set; } = false;

    public bool OverlayEnabled { get; set; } = true;
    public bool OverlayHideOnPause { get; set; } = true;
    public int OverlayX { get; set; } = -1;             
    public int OverlayY { get; set; } = -1;

    public int WindowW { get; set; } = 0;
    public int WindowH { get; set; } = 0;
    public bool WindowMax { get; set; } = false;
    public int LeftWidth { get; set; } = 0;
    public int FolderH { get; set; } = 0;
    public int PlaylistH { get; set; } = 0;
    public int OptionsH { get; set; } = 0;

    public string KeymapName { get; set; } = KeymapProfile.DefaultName;

    public int PlayModeIndex { get; set; } = 0;
    public bool PlaylistEnabled { get; set; } = false;
    public string ActivePlaylist { get; set; } = "";

    public Dictionary<string, ProfileSettings> PerProfile { get; set; } = new();

    public string MidiDeviceName { get; set; } = "";     
    public bool MidiLiveEnabled { get; set; } = false;   
    public int MidiBaseOctave { get; set; } = 4;         
    public int MidiMinVelocity { get; set; } = 1;        
    public bool MidiAutoFit { get; set; } = true;        

    public string SyncPeerName { get; set; } = "";       
    public string SyncPeerId { get; set; } = "";
    public string SyncBroker { get; set; } = "";
    public string SyncRoomName { get; set; } = "";
    public int SyncCountdownIndex { get; set; } = 1;
    public bool SyncIntroSeen { get; set; } = false;

    public const int MaxRecentFiles = 10;

    private List<string> _recentFiles = new();

    public List<string> RecentFiles
    {
        get => _recentFiles;
        set => _recentFiles = value ?? new List<string>();   
    }

    private string _folderPath = "";

    public string FolderPath
    {
        get => _folderPath;
        set => _folderPath = value ?? "";   
    }

    private const string LegacyDirName = "HarpAutoPlayer";

    private static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string DirPath => Path.Combine(LocalAppData, "MidiKeyPlayer");

    private static string FilePath => Path.Combine(DirPath, "settings.json");

    private static string LegacyFilePath =>
        Path.Combine(LocalAppData, LegacyDirName, "settings.json");

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

    public void RememberProfile(string? name, int speed, int transpose, int timingIndex)
    {
        var s = SettingsFor(name);
        s.Speed = Math.Clamp(speed, MinSpeedPercent, MaxSpeedPercent);
        s.Transpose = Math.Clamp(transpose, -MaxTransposeSemitones, MaxTransposeSemitones);
        s.TimingIndex = Math.Clamp(timingIndex, 0, 2);
    }

    public void RememberRecentFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        _recentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _recentFiles.Insert(0, path);
        TrimRecentFiles();
    }

    public void ForgetRecentFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        _recentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
    }

    public void ClearRecentFiles() => _recentFiles.Clear();

    private void TrimRecentFiles()
    {
        _recentFiles.RemoveAll(string.IsNullOrWhiteSpace);
        if (_recentFiles.Count > MaxRecentFiles)
            _recentFiles.RemoveRange(MaxRecentFiles, _recentFiles.Count - MaxRecentFiles);
    }

    public void Normalize()
    {
        Speed = Speed;
        Transpose = Transpose;
        PlayModeIndex = PlayModeIndex is >= 0 and <= 4 ? PlayModeIndex : 0;
        ActivePlaylist ??= "";
        TrimRecentFiles();   
        if (PerProfile == null) return;
        foreach (var s in PerProfile.Values)
        {
            if (s == null) continue;
            s.Speed = Math.Clamp(s.Speed, MinSpeedPercent, MaxSpeedPercent);
            s.Transpose = Math.Clamp(s.Transpose, -MaxTransposeSemitones, MaxTransposeSemitones);
            s.TimingIndex = Math.Clamp(s.TimingIndex, 0, 2);
        }
    }

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
                    cfg.FirstRunDone = true;
                    cfg.PerProfile ??= new Dictionary<string, ProfileSettings>();
                    cfg.Normalize();   
                    return cfg;
                }
            }
        }
        catch (Exception ex) { LogFile.Append("[设置] 读取失败，用默认值：" + ex.Message); }

        if (!existed)
        {
            var migrated = TryLoadLegacy();
            if (migrated != null)
            {
                migrated.FirstRunDone = true;
                migrated.PerProfile ??= new Dictionary<string, ProfileSettings>();
                migrated.Normalize();   
                migrated.Save();   
                return migrated;
            }
        }
        return new AppConfig();
    }

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
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) { LogFile.Append("[设置] 保存失败：" + ex.Message); }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(ProfileSettings))]
[JsonSerializable(typeof(KeymapProfile))]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class ConfigJson : JsonSerializerContext
{
}

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
        catch { }
    }
}

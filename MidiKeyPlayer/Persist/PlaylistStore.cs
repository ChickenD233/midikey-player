using System.Text.Json;
using System.Text.Json.Serialization;
using MidiKeyPlayer.Persist;

namespace MidiKeyPlayer.Engine;

public static class PlaylistStore
{
    internal static string? DirPathForDev { get; set; }

    public static string DirPath =>
        DirPathForDev ??
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MidiKeyPlayer",
            "playlists");

    private const string FallbackName = "未命名歌单";

    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    public static string SanitizeFileName(string? name)
    {
        string raw = (name ?? "").Trim();
        if (raw.Length == 0) return FallbackName;

        var chars = raw.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (Array.IndexOf(InvalidChars, chars[i]) >= 0) chars[i] = '_';

        string cleaned = new string(chars).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? FallbackName : cleaned;
    }

    public static string FilePathFor(string? name) =>
        Path.Combine(DirPath, SanitizeFileName(name) + ".json");

    public static List<Playlist> LoadAll()
    {
        var list = new List<Playlist>();
        try
        {
            if (!Directory.Exists(DirPath)) return list;
            foreach (var file in Directory.EnumerateFiles(DirPath, "*.json"))
            {
                try
                {
                    var p = JsonSerializer.Deserialize(File.ReadAllText(file), PlaylistJson.Default.Playlist);
                    if (p == null) continue;
                    p.Entries ??= new List<PlaylistEntry>();
                    if (string.IsNullOrWhiteSpace(p.Name))
                        p.Name = Path.GetFileNameWithoutExtension(file);
                    list.Add(p);
                }
                catch (Exception ex)
                {
                    LogFile.Append($"[歌单] 读不动 {Path.GetFileName(file)}：{ex.Message}");
                }
            }
        }
        catch (Exception ex) { LogFile.Append("[歌单] 列目录失败：" + ex.Message); }

        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return list;
    }

    public static Playlist? Load(string? name)
    {
        try
        {
            string path = FilePathFor(name);
            if (!File.Exists(path)) return null;
            var p = JsonSerializer.Deserialize(File.ReadAllText(path), PlaylistJson.Default.Playlist);
            if (p == null) return null;
            p.Entries ??= new List<PlaylistEntry>();
            if (string.IsNullOrWhiteSpace(p.Name)) p.Name = name ?? "";
            return p;
        }
        catch (Exception ex)
        {
            LogFile.Append($"[歌单] 读取失败：{ex.Message}");
            return null;
        }
    }

    public static bool Save(Playlist playlist)
    {
        try
        {
            Directory.CreateDirectory(DirPath);
            string json = JsonSerializer.Serialize(playlist, PlaylistJson.Default.Playlist);
            string path = FilePathFor(playlist.Name);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            LogFile.Append("[歌单] 保存失败：" + ex.Message);
            return false;
        }
    }

    public static bool Delete(string? name)
    {
        try
        {
            string path = FilePathFor(name);
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            LogFile.Append("[歌单] 删除失败：" + ex.Message);
            return false;
        }
    }

    public static bool Rename(string? oldName, Playlist playlist)
    {
        string oldFile = FilePathFor(oldName);
        string newFile = FilePathFor(playlist.Name);
        bool saved = Save(playlist);
        if (!saved) return false;

        try
        {
            if (!string.Equals(oldFile, newFile, StringComparison.OrdinalIgnoreCase) && File.Exists(oldFile))
                File.Delete(oldFile);
        }
        catch (Exception ex) { LogFile.Append("[歌单] 改名后清旧文件失败：" + ex.Message); }
        return true;
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(Playlist))]
internal sealed partial class PlaylistJson : JsonSerializerContext
{
}

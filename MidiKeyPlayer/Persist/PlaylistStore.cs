using System.Text.Json;
using System.Text.Json.Serialization;
using MidiKeyPlayer.Persist;

namespace MidiKeyPlayer.Engine;

public static class PlaylistStore
{
    /// <summary>【开发用】把歌单目录临时指到别处，默认 null = 真实目录。只在自检里赋值。</summary>
    internal static string? DirPathForDev { get; set; }

    /// <summary>歌单文件所在目录。</summary>
    public static string DirPath =>
        DirPathForDev ??
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MidiKeyPlayer",
            "playlists");

    /// <summary>名字洗不出可用文件名时的兜底名。</summary>
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

    /// <summary>某个歌单的完整文件路径。</summary>
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

    /// <summary>按名字读一份歌单；没有或读不动返回 null。</summary>
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

    /// <summary>保存一份歌单。失败只记日志，不抛。</summary>
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

    /// <summary>删除一份歌单。没有这个文件也算成功。</summary>
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

    /// <summary>改名：按新名字另存一份，再删旧文件。留着旧文件，下次启动会多出一份重名歌单。</summary>
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

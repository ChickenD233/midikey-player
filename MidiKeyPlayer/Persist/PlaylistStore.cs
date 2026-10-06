using System.Text.Json;
using System.Text.Json.Serialization;
using MidiKeyPlayer.Persist;

namespace MidiKeyPlayer.Engine;

/// <summary>
/// 歌单的读写。每份歌单一个 JSON 文件，放在
/// <c>%LOCALAPPDATA%\MidiKeyPlayer\playlists\&lt;歌单名&gt;.json</c>。
///
/// 为什么一份歌单一个文件（而不是全塞进 settings.json）：
///   1. 歌单数量没有上限，全塞一个文件会让每次保存都重写全部内容。
///   2. 一份歌单写坏了只坏它自己，不会带坏设置和别的歌单。
/// 这套做法照抄键位方案的 <c>schemes\</c> 目录：文件名清洗、原子写入都一样。
/// </summary>
public static class PlaylistStore
{
    /// <summary>歌单文件所在目录。</summary>
    public static string DirPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MidiKeyPlayer",
            "playlists");

    /// <summary>名字洗不出可用文件名时的兜底名。</summary>
    private const string FallbackName = "未命名歌单";

    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    /// <summary>
    /// 歌单名 → 文件名（不含扩展名）。非法字符换成下划线，空名给兜底名。
    /// 界面显示用歌单名本身，文件名只落地用。
    /// </summary>
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

    /// <summary>
    /// 读全部歌单，按名字排序。目录不存在、某个文件读不动，都只记一行日志并跳过，
    /// 不抛异常 —— 一份坏歌单不该让程序起不来。
    /// </summary>
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

    /// <summary>
    /// 改名：按新名字另存一份，再把旧文件删掉。
    /// 只改文件不删旧的会留下一个孤儿文件，下次启动就多出一份重名歌单。
    /// </summary>
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

/// <summary>
/// 歌单的 JSON 上下文。和设置一样走源生成：发布开了裁剪，反射式序列化随时可能被裁掉，
/// 而读写失败是静默的，表现成「歌单存不住」这种查不出来的毛病。
/// 命名策略是 camelCase + 大小写不敏感，与键位方案文件保持一致。
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Playlist))]
internal sealed partial class PlaylistJson : JsonSerializerContext
{
}

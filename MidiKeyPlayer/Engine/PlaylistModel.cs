using System.Text.Json.Serialization;
using MidiKeyPlayer.Midi;

namespace MidiKeyPlayer.Engine;

/// <summary>
/// 歌单条目里钉住的音轨。歌单可以预先替每一首记好「弹哪一行」，
/// 这样连播到这一首时不用再猜。两个字段一起用，光有轨道号不足以定位一行。
///
/// 存轨道号而不是存声部身份（旋律 / 人声 / 贝斯…）：身份会随识别结果漂移 ——
/// 同一首歌今天识别成「旋律」，改天按音域推断成「伴奏」，条目就指到别的行上去了。
/// </summary>
public sealed class TrackRef
{
    /// <summary>MIDI 里的轨道号（0 起）。</summary>
    public int TrackIndex { get; set; } = -1;

    /// <summary>声道号（0 起）。</summary>
    public int Channel { get; set; } = -1;

    /// <summary>加进歌单时的轨名，只用于界面显示与换文件后的兜底提示。</summary>
    public string NameHint { get; set; } = "";

    /// <summary>是否是一份有效的定位信息。</summary>
    [JsonIgnore]
    public bool IsSet => TrackIndex >= 0 && Channel >= 0;

    /// <summary>界面文案，与主界面右侧那句「主旋律：轨道 2 / 声道 1」同一套说法。</summary>
    public string Label => IsSet
        ? $"轨道 {TrackIndex + 1} / 声道 {Channel + 1}"
        : "未指定";
}

/// <summary>歌单里的一首曲子。</summary>
public sealed class PlaylistEntry
{
    /// <summary>MIDI 文件的完整路径。</summary>
    public string Path { get; set; } = "";

    /// <summary>预先选好的音轨。没设过就是空。</summary>
    public TrackRef? Track { get; set; }
}

/// <summary>
/// 一份命名的有序曲目表。顺序就是演奏顺序，用户可以上移下移或拖放来改。
/// </summary>
public sealed class Playlist
{
    /// <summary>歌单名，同时是文件名。</summary>
    public string Name { get; set; } = "";

    /// <summary>有序曲目。顺序即演奏顺序。</summary>
    public List<PlaylistEntry> Entries { get; set; } = new();

    /// <summary>只用于界面显示，不进 JSON。</summary>
    [JsonIgnore]
    public int Count => Entries.Count;

    /// <summary>新建一份空歌单。</summary>
    public static Playlist Create(string name) => new() { Name = name };
}

/// <summary>挑一行来演奏的结果。</summary>
/// <param name="Candidate">挑中的候选；没挑到是 null。</param>
/// <param name="Reason">给日志用的一句话。</param>
/// <param name="Exact">true = 按存下来的轨道号加声道精确命中的。</param>
public readonly record struct TrackPick(MidiCandidate? Candidate, string Reason, bool Exact)
{
    /// <summary>是否挑到了。</summary>
    public bool Found => Candidate != null;
}

/// <summary>
/// 歌单的数据与规则。规则都是纯函数，便于内置自检直接测。
/// </summary>
public static class PlaylistModel
{
    /// <summary>
    /// 从候选行里克出一个下标，用于「歌单没预设音轨」或「预设的行已经不在文件里」两种情况。
    ///
    /// 优先级：同声部身份的行（轨道号最小的那个）→ 第一条非打击乐行 → 第一条。
    /// 只认 <see cref="TrackRef"/> 里的轨道号加声道，不猜别的。
    /// </summary>
    /// <param name="candidates">当前文件解析出来的候选行。</param>
    /// <param name="preferred">歌单里预先选好的音轨，可以为空。</param>
    /// <param name="lastRole">上一首演奏行的声部身份，用来「接着上一首同一轨」。</param>
    public static TrackPick Pick(
        IReadOnlyList<MidiCandidate> candidates,
        TrackRef? preferred = null,
        TrackRole? lastRole = null)
    {
        if (candidates.Count == 0) return new TrackPick(null, "这首歌没有任何候选音轨", false);

        // 1) 歌单预选的行，精确匹配
        if (preferred is { IsSet: true })
        {
            foreach (var c in candidates)
                if (c.TrackIndex == preferred.TrackIndex && c.Channel == preferred.Channel)
                    return new TrackPick(c, $"按歌单预设：{preferred.Label}", true);
        }

        // 2) 上一首演奏行的声部身份
        if (lastRole is { } role && role != TrackRole.Unknown)
        {
            MidiCandidate? best = null;
            foreach (var c in candidates)
            {
                if (c.Role != role) continue;
                if (best == null || c.TrackIndex < best.TrackIndex) best = c;
            }
            if (best != null)
                return new TrackPick(best, $"接着上一首的声部「{GmInstrument.TagOf(role)}」", false);
        }

        // 3) 第一条非打击乐行
        foreach (var c in candidates)
            if (c.Role != TrackRole.Drums)
                return new TrackPick(c, "按第一条非打击乐行", false);

        // 4) 兜底：第一条
        return new TrackPick(candidates[0], "只剩打击乐轨，按第一条", false);
    }

    /// <summary>
    /// 把一份候选行里的下标为 <paramref name="from"/> 的条目挪到 <paramref name="to"/>。
    /// 上移、下移、拖放三条路都调这一个函数，自检测的也是它。
    /// 下标越界或原地不动返回 false，调用方据此跳过重画。
    /// </summary>
    public static bool Move<T>(IList<T> list, int from, int to)
    {
        if (list == null) return false;
        if (from < 0 || from >= list.Count) return false;
        if (to < 0 || to >= list.Count) return false;
        if (from == to) return false;

        var item = list[from];
        list.RemoveAt(from);
        list.Insert(to, item);
        return true;
    }

    /// <summary>上移一格。已经在最上面返回 false。</summary>
    public static bool MoveUp<T>(IList<T> list, int index) => Move(list, index, index - 1);

    /// <summary>下移一格。已经在最下面返回 false。</summary>
    public static bool MoveDown<T>(IList<T> list, int index) => Move(list, index, index + 1);

    /// <summary>
    /// 拖放用：把 <paramref name="from"/> 插到 <paramref name="target"/> 这个位置上。
    /// 往下拖时目标下标要减一 —— 元素先被抽走，后面的会整体前移一格。
    /// </summary>
    public static bool MoveOnto<T>(IList<T> list, int from, int target)
    {
        if (list == null) return false;
        if (from < 0 || from >= list.Count) return false;
        if (target < 0 || target >= list.Count) return false;
        int to = from < target ? target - 1 : target;
        return Move(list, from, to);
    }
}

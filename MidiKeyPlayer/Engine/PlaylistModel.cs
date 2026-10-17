using System.Text.Json.Serialization;
using MidiKeyPlayer.Midi;

namespace MidiKeyPlayer.Engine;

public sealed class TrackRef
{
    /// <summary>MIDI 里的轨道号（0 起）。</summary>
    public int TrackIndex { get; set; } = -1;

    /// <summary>声道号（0 起）。</summary>
    public int Channel { get; set; } = -1;

    /// <summary>选音轨时的轨名。只用于界面显示与换文件后的兜底提示。</summary>
    public string NameHint { get; set; } = "";

    /// <summary>是否是一份有效的定位信息。</summary>
    [JsonIgnore]
    public bool IsSet => TrackIndex >= 0 && Channel >= 0;

    /// <summary>界面文案，与主界面那句「主旋律：轨道 2 / 声道 1」同一套说法。现算，不存盘。</summary>
    [JsonIgnore]
    public string Label => IsSet
        ? $"轨道 {TrackIndex + 1} / 声道 {Channel + 1}"
        : "未指定";
}

/// <summary>歌单里的一首曲子。</summary>
public sealed class PlaylistEntry
{
    /// <summary>MIDI 文件的完整路径。</summary>
    public string Path { get; set; } = "";

    public List<TrackRef> Tracks { get; set; } = new();

    [JsonPropertyName("track")]
    public TrackRef? Track
    {
        get => Tracks.Count > 0 ? Tracks[0] : null;
        set
        {
            if (value is { IsSet: true } && !Tracks.Any(t => TrackRefs.Same(t, value)))
                Tracks.Insert(0, value);
        }
    }
}

/// <summary>歌单条目的音轨比较与小工具。</summary>
public static class TrackRefs
{
    /// <summary>是不是同一行（轨道号 + 声道）。</summary>
    public static bool Same(TrackRef a, TrackRef b) =>
        a.TrackIndex == b.TrackIndex && a.Channel == b.Channel;
}

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

public readonly record struct TrackPick(MidiCandidate? Candidate, string Reason, bool Exact)
{
    /// <summary>是否挑到了。</summary>
    public bool Found => Candidate != null;
}

public static class PlaylistModel
{
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

    public static bool MoveOnto<T>(IList<T> list, int from, int target)
    {
        if (list == null) return false;
        if (from < 0 || from >= list.Count) return false;
        if (target < 0 || target >= list.Count) return false;
        int to = from < target ? target - 1 : target;
        return Move(list, from, to);
    }
}

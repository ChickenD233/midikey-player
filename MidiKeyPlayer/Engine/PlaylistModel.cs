using System.Text.Json.Serialization;
using MidiKeyPlayer.Midi;

namespace MidiKeyPlayer.Engine;

public sealed class TrackRef
{
    public int TrackIndex { get; set; } = -1;

    public int Channel { get; set; } = -1;

    public string NameHint { get; set; } = "";

    [JsonIgnore]
    public bool IsSet => TrackIndex >= 0 && Channel >= 0;

    [JsonIgnore]
    public string Label => IsSet
        ? $"轨道 {TrackIndex + 1} / 声道 {Channel + 1}"
        : "未指定";
}

public sealed class PlaylistEntry
{
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

public static class TrackRefs
{
    public static bool Same(TrackRef a, TrackRef b) =>
        a.TrackIndex == b.TrackIndex && a.Channel == b.Channel;
}

public sealed class Playlist
{
    public string Name { get; set; } = "";

    public List<PlaylistEntry> Entries { get; set; } = new();

    [JsonIgnore]
    public int Count => Entries.Count;

    public static Playlist Create(string name) => new() { Name = name };
}

public readonly record struct TrackPick(MidiCandidate? Candidate, string Reason, bool Exact)
{
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

        if (preferred is { IsSet: true })
        {
            foreach (var c in candidates)
                if (c.TrackIndex == preferred.TrackIndex && c.Channel == preferred.Channel)
                    return new TrackPick(c, $"按歌单预设：{preferred.Label}", true);
        }

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

        foreach (var c in candidates)
            if (c.Role != TrackRole.Drums)
                return new TrackPick(c, "按第一条非打击乐行", false);

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

    public static bool MoveUp<T>(IList<T> list, int index) => Move(list, index, index - 1);

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

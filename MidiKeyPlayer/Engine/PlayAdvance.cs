namespace MidiKeyPlayer.Engine;

public enum PlayMode
{
    Sequential = 0,
    ListLoop = 1,
    Shuffle = 2,
    RepeatOne = 3,
    Off = 4,
}

public static class PlayModeNames
{
    public static readonly string[] All = { "顺序播放", "列表循环", "随机播放", "单曲循环", "关闭" };

    public static readonly string[] Short = { "▶ 顺序播放", "↻ 列表循环", "⤨ 随机播放", "↺ 单曲循环", "■ 关闭" };

    public static PlayMode Clamp(int index) =>
        index < 0 || index > (int)PlayMode.Off ? PlayMode.Sequential : (PlayMode)index;

    public static string ButtonText(PlayMode mode) =>
        $"{Short[(int)mode]} ▾";
}

public static class PlayAdvance
{
    public enum Move
    {
        Stop,
        Play,
        Fault,
    }

    public readonly record struct Decision(Move Kind, int Index, string Reason)
    {
        public bool ShouldPlay => Kind == Move.Play;
    }

    public static Decision AfterFinish(
        PlayMode mode, int count, int current,
        IReadOnlyCollection<int>? skipped = null, Random? rng = null)
    {
        if (count <= 0) return new Decision(Move.Stop, -1, "列表是空的");
        if (current < 0 || current >= count)
            return new Decision(Move.Fault, -1, $"当前下标 {current} 不在 0..{count - 1} 里");
        if (count == 1) return new Decision(Move.Stop, -1, "列表里只有这一首");

        switch (mode)
        {
            case PlayMode.Off:
                return new Decision(Move.Stop, -1, "关闭，放完这一首就停");

            case PlayMode.RepeatOne:
                return new Decision(Move.Stop, -1, "单曲循环不走连播");

            case PlayMode.ListLoop:
            {
                int next = (current + 1) % count;
                return new Decision(Move.Play, next, "列表循环，回到开头");
            }

            case PlayMode.Shuffle:
            {
                int next = PickShuffle(count, current, skipped, rng);
                return next < 0
                    ? new Decision(Move.Stop, -1, "没有别的可放了")
                    : new Decision(Move.Play, next, "随机挑的");
            }

            default:
            {
                if (current + 1 < count)
                    return new Decision(Move.Play, current + 1, "");
                return new Decision(Move.Stop, -1, "顺序播放到底了");
            }
        }
    }

    public static int PickShuffle(
        int count, int current, IReadOnlyCollection<int>? skipped = null, Random? rng = null)
    {
        if (count <= 1) return -1;
        rng ??= Random.Shared;

        var pool = new List<int>(count);
        for (int i = 0; i < count; i++)
        {
            if (i == current) continue;
            if (skipped != null && skipped.Contains(i)) continue;
            pool.Add(i);
        }
        if (pool.Count == 0) return -1;
        return pool[rng.Next(pool.Count)];
    }

    public const int MaxConsecutiveFailures = 3;

    public static bool ShouldKeepSkipping(int consecutiveFailures) =>
        consecutiveFailures < MaxConsecutiveFailures;

    public static int NextAfterFailure(PlayMode mode, int count, int current, Random? rng = null)
    {
        if (count <= 1 || current < 0 || current >= count) return -1;

        if (mode == PlayMode.Off) return -1;

        if (mode == PlayMode.Shuffle)
        {
            return PickShuffle(count, current, null, rng);
        }

        return (current + 1) % count;
    }
}

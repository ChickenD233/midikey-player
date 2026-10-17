namespace MidiKeyPlayer.Engine;

public enum PlayMode
{
    /// <summary>顺序播放：放完这一首，接着放列表里的下一首；到底就停。</summary>
    Sequential = 0,
    /// <summary>列表循环：放完最后一首回到第一首，一直放下去。</summary>
    ListLoop = 1,
    /// <summary>随机播放：放完一首随机挑列表里的另一首。</summary>
    Shuffle = 2,
    /// <summary>单曲循环：重播这一首，永不前进。这一项由引擎的 _loop 实现。</summary>
    RepeatOne = 3,
    /// <summary>关闭：放完这一首就停，不接下一首。</summary>
    Off = 4,
}

/// <summary>五种模式的中文名与按钮文案，一处定义，界面与文档都读这里。</summary>
public static class PlayModeNames
{
    /// <summary>模式全名，用于按钮、悬浮窗与日志。</summary>
    public static readonly string[] All = { "顺序播放", "列表循环", "随机播放", "单曲循环", "关闭" };

    /// <summary>带图标的名字，用在按钮与模式列表上。</summary>
    public static readonly string[] Short = { "▶ 顺序播放", "↻ 列表循环", "⤨ 随机播放", "↺ 单曲循环", "■ 关闭" };

    /// <summary>把任意整数收敛到合法模式，越界当作顺序播放。</summary>
    public static PlayMode Clamp(int index) =>
        index < 0 || index > (int)PlayMode.Off ? PlayMode.Sequential : (PlayMode)index;

    /// <summary>按钮文案：当前模式 + 下拉箭头。</summary>
    public static string ButtonText(PlayMode mode) =>
        $"{Short[(int)mode]} ▾";
}

public static class PlayAdvance
{
    /// <summary>连播一个列表时，一首歌放完之后做什么。</summary>
    public enum Move
    {
        /// <summary>没有下一首了：停在这首。</summary>
        Stop,
        /// <summary>前进到替换后的那个下标，并继续演奏。</summary>
        Play,
        /// <summary>向后找不到位置（传进来的下标本身就越界）：停下。</summary>
        Fault,
    }

    /// <summary>一次前进的结论：做什么、去第几首、为什么这么做。</summary>
    public readonly record struct Decision(Move Kind, int Index, string Reason)
    {
        /// <summary>是否要真的换歌开弹。</summary>
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
                // 单曲循环由引擎的 _loop 顶着，引擎压根不发 Finished，正常走不到这里。
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

        // 关闭模式不换歌
        if (mode == PlayMode.Off) return -1;

        if (mode == PlayMode.Shuffle)
        {
            // 随机播放没有「下一个」的概念，交给随机挑；挑到放不出来的再挑一次，
            // 由调用方用失败上限兜住。
            return PickShuffle(count, current, null, rng);
        }

        // 顺序、列表循环、单曲循环都往后走一格。单曲循环放不出来时没有「这一首」可言，
        // 只能换一首，否则永远卡在同一首上。
        return (current + 1) % count;
    }
}

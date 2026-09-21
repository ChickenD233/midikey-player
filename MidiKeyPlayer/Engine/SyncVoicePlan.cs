namespace MidiKeyPlayer.Engine;

/// <summary>
/// 【实验功能·远程同演】声部分配。
///
/// 声部序号与主界面左侧列表同口径（<c>MidiCandidate.TrackIndex</c>，0 起）。
/// 每台机器的曲目指纹一致，所以同一个序号指的是同一条轨。
///
/// 分配规则：
/// - 轮流发牌，不是连续切块。这样"谁弹得最多"不会全压在一个人身上：
///   4 个声部 2 个人拿到 1 和 3 号，另一个拿到 2 和 4 号。
/// - 沉一点的声部优先给前面的人。鼓轨默认整条给一个人，不拆开（它就是一条轨，本来也拆不开）。
/// - 人数多于声部数时，多出来的人拿到空列表，不报错。
/// </summary>
internal static class SyncVoicePlan
{
    /// <summary>
    /// 轮流发牌。返回"每人一个声部列表"，下标与传入的成员顺序一一对应。
    /// </summary>
    /// <param name="voiceCount">曲目里的声部总数。</param>
    /// <param name="playerCount">参与演奏的人数。</param>
    internal static List<List<int>> Allocate(int voiceCount, int playerCount)
    {
        var result = new List<List<int>>(Math.Max(0, playerCount));
        for (int i = 0; i < playerCount; i++) result.Add(new List<int>());
        if (voiceCount <= 0 || playerCount <= 0) return result;

        // 从低音区往上发：低音声部通常节奏稳、音少，适合当第一个人的基准。
        for (int v = 0; v < voiceCount; v++)
            result[v % playerCount].Add(v);

        return result;
    }

    /// <summary>
    /// 找出被别人占用的声部。key = 声部序号，value = 占用者的成员号。
    /// 传进来的 <paramref name="selfId"/> 自己的占用不算冲突。
    /// </summary>
    internal static Dictionary<int, string> Conflicts(IEnumerable<SyncPeer> peers, string selfId)
    {
        var taken = new Dictionary<int, string>();
        foreach (var p in peers)
        {
            if (p.Id == selfId) continue;
            foreach (int v in p.Voices) taken[v] = p.Id;
        }
        return taken;
    }

    /// <summary>
    /// 这个人想要的声部里，有没有被别人占着。返回第一个冲突的声部号，没有冲突返回 -1。
    /// </summary>
    internal static int FirstConflict(IEnumerable<int> wanted, IEnumerable<SyncPeer> peers, string selfId)
    {
        var taken = Conflicts(peers, selfId);
        foreach (int v in wanted) if (taken.ContainsKey(v)) return v;
        return -1;
    }

    /// <summary>
    /// 把声部整理成稳定的显示文本，例如 "2、4"。空列表返回"（无）"。
    /// </summary>
    internal static string Describe(IEnumerable<int> voices)
    {
        var list = new List<int>(voices);
        if (list.Count == 0) return "（无）";
        list.Sort();
        return string.Join("、", list.ConvertAll(v => (v + 1).ToString()));
    }

    /// <summary>
    /// 声部名表。指纹校验用：两边算出的表不一样，说明曲目不是同一份。
    /// </summary>
    internal static List<string> NamesFrom(IEnumerable<(int Index, string Name)> voices)
    {
        var list = new List<(int Index, string Name)>(voices);
        list.Sort((a, b) => a.Index.CompareTo(b.Index));
        return list.ConvertAll(v => v.Name);
    }
}

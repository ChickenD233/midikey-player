using System.Diagnostics;

namespace MidiKeyPlayer.Engine;

/// <summary>
/// 【实验功能·远程同演】时间基准与延迟统计。
///
/// 两条硬规则，来自参考项目 Bili-SyncPlay 的教训（它的 clock-sync.ts 明说：
/// "位置从本机单调时钟锚点推算，正因为没有滤波器能让双钟比较变得可信"）：
///
/// 1. **不比较两台机器的墙钟。** 锚点只记"本机的单调时刻"。
///    主机传的是"从你收到起再等多久"，收到方加到自己的单调时钟上。
/// 2. **位置只加本机测到的流逝量。** 不拿对端时间戳减本机时间戳。
///
/// 所以这里没有"时钟偏移"这个概念，只有：本机锚点、往返延迟、抖动。
/// 用 <see cref="Environment.TickCount64"/> 而不是 <see cref="Stopwatch.GetTimestamp()"/>：
/// 前者单位就是毫秒，不需要再换算频率，且同样是单调的、不受系统对时影响。
/// </summary>
internal sealed class SyncClock
{
    /// <summary>保留最近这么多个往返样本。8 个约等于两分钟（每 15 秒一个）。</summary>
    internal const int WindowSize = 8;

    /// <summary>超过这个年纪的样本作废（毫秒）。也用来在系统休眠后自动重新播种。</summary>
    internal const long MaxSampleAgeMs = 150_000;

    /// <summary>只让往返延迟不超过"窗口内最快值加这么多"的样本参与（毫秒）。</summary>
    internal const double RttToleranceMs = 20;

    /// <summary>少于这么多个可用样本时，最大延迟不过滤，直接取原始最大值。</summary>
    internal const int MinTrustedSamples = 3;

    /// <summary>一次往返采样。</summary>
    internal readonly record struct RttSample(double RttMs, long AtTick);

    private readonly List<RttSample> _samples = new();

    /// <summary>平滑后的往返延迟（毫秒）。0 表示还没测过。</summary>
    public double RttMs { get; private set; }

    /// <summary>抖动：最近几次往返延迟的平均绝对差（毫秒）。开演精度约等于它的一半。</summary>
    public double JitterMs { get; private set; }

    /// <summary>已经攒了几个样本。</summary>
    public int SampleCount => _samples.Count;

    /// <summary>现在这个单调时刻（毫秒）。全类只用这一个时间源，别处不许另取。</summary>
    public static long NowMs => Environment.TickCount64;

    /// <summary>
    /// 记一次往返采样。
    /// </summary>
    /// <param name="rttMs">本机测到的往返毫秒数。负数说明时间戳自相矛盾，直接丢掉。</param>
    public void AddSample(double rttMs)
    {
        if (double.IsNaN(rttMs) || double.IsInfinity(rttMs) || rttMs < 0) return;
        long now = NowMs;
        _samples.Add(new RttSample(rttMs, now));
        _samples.RemoveAll(s => now - s.AtTick > MaxSampleAgeMs);
        while (_samples.Count > WindowSize) _samples.RemoveAt(0);
        Recompute();
    }

    /// <summary>断线或换房间之后重新播种，别让旧样本拖住估计。</summary>
    public void Reset()
    {
        _samples.Clear();
        RttMs = 0;
        JitterMs = 0;
    }

    private void Recompute()
    {
        if (_samples.Count == 0)
        {
            RttMs = 0;
            JitterMs = 0;
            return;
        }
        double fastest = double.MaxValue;
        foreach (var s in _samples) if (s.RttMs < fastest) fastest = s.RttMs;

        // 慢样本的往返几乎总是不对称的，它的延迟会偏大半程。只让"够快"的样本参与。
        var competing = new List<double>();
        foreach (var s in _samples)
            if (_samples.Count < MinTrustedSamples || s.RttMs <= fastest + RttToleranceMs)
                competing.Add(s.RttMs);
        if (competing.Count == 0) competing.Add(fastest);

        RttMs = Median(competing);

        // 抖动：相邻竞争的样本之差的平均绝对值
        if (competing.Count < 2) { JitterMs = 0; return; }
        double sum = 0;
        int n = 0;
        for (int i = 1; i < competing.Count; i++)
        {
            sum += Math.Abs(competing[i] - competing[i - 1]);
            n++;
        }
        JitterMs = n == 0 ? 0 : sum / n;
    }

    internal static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = new List<double>(values);
        sorted.Sort();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}

/// <summary>
/// 本机的播放锚点："在 <see cref="AtTickMs"/> 这个本机单调时刻，曲子位置是 <see cref="PositionSec"/>"。
///
/// 求"现在应该在哪儿"就是：位置 + 本机流逝量 × 速度。
/// 注意流逝量用同一次采样算，不受系统对时、休眠、跨时区影响。
/// </summary>
internal readonly struct SyncAnchor
{
    public double PositionSec { get; init; }
    public long AtTickMs { get; init; }
    public double Speed { get; init; }
    public bool Valid { get; init; }

    public static SyncAnchor Idle => default;

    public static SyncAnchor At(double positionSec, double speed)
        => new() { PositionSec = positionSec, AtTickMs = SyncClock.NowMs, Speed = speed <= 0 ? 1 : speed, Valid = true };

    /// <summary>从现在这一刻算起的位置（秒）。锚点无效时返回 0。</summary>
    public double PositionNow()
    {
        if (!Valid) return 0;
        long elapsed = SyncClock.NowMs - AtTickMs;
        if (elapsed < 0) elapsed = 0;      // 单调时钟不会倒流，出现负数只能是调用方的错
        return PositionSec + elapsed / 1000.0 * Speed;
    }

    /// <summary>换速度时保留当前进度，只换基准。</summary>
    public SyncAnchor WithSpeed(double speed)
        => At(PositionNow(), speed);
}

/// <summary>
/// 漂移校正。把"本机位置"往"目标位置"拉，按偏差大小分三档。
///
/// 档位阈值来自设计文档：30 ms 内不动，30 到 120 ms 微调播放速率，超过 120 ms 直接跳。
/// 微调不改变界面上显示的速度数字：它只作用在内部积分上，用户看不到。
/// </summary>
internal static class SyncDrift
{
    /// <summary>小于这个偏差（秒）不动。别为了几十毫秒反复微调，反而听得出。</summary>
    internal const double DeadbandSec = 0.030;

    /// <summary>超过这个偏差（秒）直接跳到目标位置。</summary>
    internal const double JumpSec = 0.120;

    /// <summary>微调档允许的最大速率偏移比例（±2%）。太大听得出速度在变。</summary>
    internal const double TrimLimit = 0.02;

    internal enum Action
    {
        None,       // 什么都不做
        Trim,       // 按 TrimRate 微调速率
        Jump,       // 跳到目标位置
    }

    internal static Action Decide(double localSec, double targetSec, out double trimRate)
    {
        trimRate = 1.0;
        double err = targetSec - localSec;     // 正数 = 本机落后，要放快
        double abs = Math.Abs(err);
        if (abs < DeadbandSec) return Action.None;
        if (abs > JumpSec) return Action.Jump;

        // 偏差越大，速率偏移越大，上限 TrimLimit。
        // 系数 8 让 120 ms 的偏差正好打满上限：0.12 * ... 按比例给即可。
        double ratio = Math.Clamp(abs / JumpSec, 0, 1);
        double trim = 1.0 + Math.Sign(err) * ratio * TrimLimit;
        trimRate = Math.Clamp(trim, 1.0 - TrimLimit, 1.0 + TrimLimit);
        return Action.Trim;
    }
}

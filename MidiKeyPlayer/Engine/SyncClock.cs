using System.Diagnostics;

namespace MidiKeyPlayer.Engine;

internal sealed class SyncClock
{
    internal const int WindowSize = 8;

    internal const long MaxSampleAgeMs = 150_000;

    internal const double RttToleranceMs = 20;

    internal const int MinTrustedSamples = 3;

    internal readonly record struct RttSample(double RttMs, long AtTick);

    private readonly List<RttSample> _samples = new();

    public double RttMs { get; private set; }

    public double JitterMs { get; private set; }

    public int SampleCount => _samples.Count;

    public static long NowMs => Environment.TickCount64;

    public void AddSample(double rttMs)
    {
        if (double.IsNaN(rttMs) || double.IsInfinity(rttMs) || rttMs < 0) return;
        long now = NowMs;
        _samples.Add(new RttSample(rttMs, now));
        _samples.RemoveAll(s => now - s.AtTick > MaxSampleAgeMs);
        while (_samples.Count > WindowSize) _samples.RemoveAt(0);
        Recompute();
    }

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

        var competing = new List<double>();
        foreach (var s in _samples)
            if (_samples.Count < MinTrustedSamples || s.RttMs <= fastest + RttToleranceMs)
                competing.Add(s.RttMs);
        if (competing.Count == 0) competing.Add(fastest);

        RttMs = Median(competing);

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

internal readonly struct SyncAnchor
{
    public double PositionSec { get; init; }
    public long AtTickMs { get; init; }
    public double Speed { get; init; }
    public bool Valid { get; init; }

    public static SyncAnchor Idle => default;

    public static SyncAnchor At(double positionSec, double speed)
        => new() { PositionSec = positionSec, AtTickMs = SyncClock.NowMs, Speed = speed <= 0 ? 1 : speed, Valid = true };

    public double PositionNow()
    {
        if (!Valid) return 0;
        long elapsed = SyncClock.NowMs - AtTickMs;
        if (elapsed < 0) elapsed = 0;      
        return PositionSec + elapsed / 1000.0 * Speed;
    }

    public SyncAnchor WithSpeed(double speed)
        => At(PositionNow(), speed);
}

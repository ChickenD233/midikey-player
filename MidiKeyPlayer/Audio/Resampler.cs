using System;

namespace MidiKeyPlayer.Audio;

/// <summary>
/// 单声道重采样。模型只吃 22.05 kHz，而音频文件常见 44.1 / 48 kHz，
/// 所以下采样前必须先低通，否则折叠回来的高频会被模型当成音符。
///
/// 做法：加窗 sinc（Blackman 窗）查表 + 线性插值。核只算一次，之后每次查表，
/// 一首 5 分钟的歌在几十毫秒量级。
/// </summary>
internal static class Resampler
{
    private const int TapsPerSide = 16;      // 每侧 16 个抽头
    private const int TableSize = 4096;
    private static readonly double[] Table = BuildTable();

    /// <summary>
    /// 窗函数：Blackman。u 是「离核中心的距离 / 核半径」，取值 0..1。
    /// 中心必须是 1、边缘必须是 0：写成 0.42 + 0.5cos(πu) + 0.08cos(2πu)，
    /// u=0 → 1，u=1 → 0。（写成减号会得到一条从 0 升到 1 的斜坡，
    /// 而 sinc 的主瓣正好在中心，整条滤波器会把信号抹掉。）
    /// </summary>
    private static double Window(double u)
    {
        if (u >= 1.0) return 0.0;
        return 0.42 + 0.5 * Math.Cos(Math.PI * u) + 0.08 * Math.Cos(2 * Math.PI * u);
    }

    private static double Sinc(double x)
    {
        if (Math.Abs(x) < 1e-9) return 1.0;
        double p = Math.PI * x;
        return Math.Sin(p) / p;
    }

    /// <summary>表里存的是「归一化到输入采样率」的核，坐标 0..TapsPerSide。</summary>
    private static double[] BuildTable()
    {
        var table = new double[TableSize + 1];
        for (int i = 0; i <= TableSize; i++)
        {
            double x = (double)i / TableSize * TapsPerSide;
            table[i] = Sinc(x) * Window(x / TapsPerSide);
        }
        return table;
    }

    private static double Lookup(double x)
    {
        double ax = Math.Abs(x);
        if (ax >= TapsPerSide) return 0.0;
        double pos = ax / TapsPerSide * TableSize;
        int i = (int)pos;
        double frac = pos - i;
        if (i >= TableSize) return Table[TableSize];
        return Table[i] + (Table[i + 1] - Table[i]) * frac;
    }

    /// <summary>把单声道音频从 srcRate 变到 dstRate。</summary>
    public static float[] Resample(float[] input, int srcRate, int dstRate)
    {
        if (srcRate == dstRate || input.Length == 0) return input;

        double ratio = (double)dstRate / srcRate;
        int outLength = (int)Math.Floor(input.Length * ratio);
        if (outLength <= 0) return Array.Empty<float>();

        // 下采样时把核按新奈奎斯特展宽（低通），上采样时保持原核
        double cutoff = ratio < 1.0 ? ratio : 1.0;
        double step = 1.0 / ratio;                    // 每个输出采样在输入域的步长
        int half = (int)Math.Ceiling(TapsPerSide / cutoff);

        var output = new float[outLength];
        for (int n = 0; n < outLength; n++)
        {
            double center = n * step;
            int first = (int)Math.Floor(center) - half + 1;
            double sum = 0, norm = 0;
            for (int k = 0; k < 2 * half; k++)
            {
                int idx = first + k;
                if (idx < 0 || idx >= input.Length) continue;
                double w = Lookup((center - idx) * cutoff);
                sum += input[idx] * w;
                norm += w;
            }
            output[n] = norm > 1e-12 ? (float)(sum / norm) : 0f;
        }
        return output;
    }
}

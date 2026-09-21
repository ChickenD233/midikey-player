using System;

namespace MidiKeyPlayer.Audio;

/// <summary>
/// 极简 FFT：迭代式 radix-2，正反两个方向。分离模型要 4096 点、每首歌几千帧，
/// 为它单独引一个数学库不值当，这里自己写一个够用的。
///
/// 用法：<see cref="Forward"/> 吃 n 个实数、写 n/2 个复数（实部进 re、虚部进 im）；
/// <see cref="Inverse"/> 吃 n/2 个复数、写 n 个实数进 <see cref="Time"/>。
/// 补零、加窗、重叠相加都由调用方负责。
/// </summary>
internal sealed class Fft
{
    private readonly int _n;
    private readonly int _bins;
    private readonly float[] _cos;
    private readonly float[] _sin;
    private readonly float[] _re;
    private readonly float[] _im;

    /// <summary>实际使用的频点数（n/2）。</summary>
    public int Bins => _bins;

    /// <summary>逆变换的输出（长度 n）。</summary>
    public float[] Time { get; }

    public Fft(int n)
    {
        if (n < 4 || (n & (n - 1)) != 0) throw new ArgumentException("FFT 长度必须是 2 的幂", nameof(n));
        _n = n;
        _bins = n / 2;
        _cos = new float[n / 2];
        _sin = new float[n / 2];
        for (int i = 0; i < n / 2; i++)
        {
            _cos[i] = MathF.Cos(2f * MathF.PI * i / n);
            _sin[i] = MathF.Sin(2f * MathF.PI * i / n);
        }
        _re = new float[n];
        _im = new float[n];
        Time = new float[n];
    }

    /// <summary>前向：n 个采样 → <see cref="Bins"/> 个复数，写到 re/im 的 at 位置。</summary>
    public void Forward(float[] input, float[] re, float[] im, int at)
    {
        Array.Copy(input, _re, _n);
        Array.Clear(_im, 0, _n);
        Run(forward: true);
        for (int b = 0; b < Bins; b++)
        {
            re[at + b] = _re[b];
            im[at + b] = _im[b];
        }
    }

    /// <summary>逆变换：<see cref="Bins"/> 个复数 → n 个实数，写进 <see cref="Time"/>。</summary>
    public void Inverse(float[] re, float[] im, int at)
    {
        // 由前 Bins 个频点补出共轭对称的整谱（Bins = n/2 正好是半谱），
        // 再跑一次「共轭输入的正向变换」，最后取共轭并除以 n —— 这是逆变换的标准做法
        for (int b = 0; b < _n; b++) { _re[b] = 0f; _im[b] = 0f; }
        for (int b = 0; b < Bins; b++)
        {
            _re[b] = re[at + b];
            _im[b] = -im[at + b];      // 共轭
        }
        for (int b = 1; b < Bins; b++)
        {
            int mirror = _n - b;
            _re[mirror] = re[at + b];
            _im[mirror] = im[at + b];  // 共轭后再镜像
        }
        Run(forward: true);
        float scale = 1f / _n;
        for (int i = 0; i < _n; i++) Time[i] = _re[i] * scale;
    }

    /// <summary>原地迭代 FFT：先做位反转置换，再逐级蝶形。</summary>
    private void Run(bool forward)
    {
        int bits = (int)Math.Log2(_n);
        for (int i = 0; i < _n; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++) if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
            if (r <= i) continue;
            (_re[i], _re[r]) = (_re[r], _re[i]);
            (_im[i], _im[r]) = (_im[r], _im[i]);
        }
        for (int len = 2; len <= _n; len <<= 1)
        {
            int half = len >> 1;
            int step = _n / len;
            for (int start = 0; start < _n; start += len)
            {
                for (int j = 0; j < half; j++)
                {
                    int k = j * step;
                    float wr = _cos[k];
                    float wi = forward ? -_sin[k] : _sin[k];
                    int a = start + j;
                    int b = a + half;
                    float xr = _re[b] * wr - _im[b] * wi;
                    float xi = _re[b] * wi + _im[b] * wr;
                    _re[b] = _re[a] - xr;
                    _im[b] = _im[a] - xi;
                    _re[a] += xr;
                    _im[a] += xi;
                }
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MidiKeyPlayer.Audio;

/// <summary>
/// 人声 / 伴奏两轨分离：Spleeter 2-stem（两个 ONNX 模型），推理交给 ONNX Runtime。
///
/// 为什么不自己写执行器：Spleeter 的图比 basic-pitch 复杂得多（转置卷积、批归一、动态填充、
/// 半精度权重），自写执行器在这张图上数值不可信 —— 详见 docs\人声伴奏分轨-进行中.md。
/// basic-pitch 只有 230 KB，那条路保持自写执行器，不进 ONNX Runtime。
///
/// 流程（与 sherpa-onnx 的 C++ 实现逐行对齐）：
///   44.1 kHz 立体声 → 左右声道各自 STFT（n_fft 4096 / hop 1024 / 汉宁窗 / 居中）
///   → 幅度谱装成 [2 声道, 块数, 512 帧, 1024 频点] → 两个模型各跑一次
///   → 软掩码 → 掩码乘回复数谱 → 逐块 iSTFT 叠加 → 两条声部的波形
/// </summary>
internal sealed class SpleeterSeparator : IDisposable
{
    /// <summary>模型要求的采样率（Spleeter 官方口径）。</summary>
    public const int SampleRate = 44100;
    /// <summary>FFT 点数。模型入口最后一个维度是 1024 个频点，所以这里用 2048 点 FFT（=1024×2）。</summary>
    public const int NFft = 2048;
    /// <summary>帧移。</summary>
    public const int Hop = 1024;
    /// <summary>频点数（= NFft / 2）。</summary>
    public const int Bins = NFft / 2;
    /// <summary>模型一次处理的帧数（一块）。</summary>
    public const int FramesPerChunk = 512;

    private readonly InferenceSession _vocals;
    private readonly InferenceSession _accompaniment;
    private readonly string _inputName;

    public SpleeterSeparator(string vocalsModelPath, string accompanimentModelPath)
    {
        var options = new SessionOptions
        {
            InterOpNumThreads = 1,
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount - 1),
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        _vocals = new InferenceSession(vocalsModelPath, options);
        _accompaniment = new InferenceSession(accompanimentModelPath, options);
        _inputName = _vocals.InputMetadata.Keys.First();
    }

    /// <summary>一条声部（44.1 kHz，左右两路等长）。</summary>
    public sealed class Stem
    {
        public string Name { get; init; } = "";
        public float[] Left { get; init; } = Array.Empty<float>();
        public float[] Right { get; init; } = Array.Empty<float>();
    }

    public sealed class Result
    {
        public Stem Vocals { get; init; } = new();
        public Stem Accompaniment { get; init; } = new();
        public TimeSpan Elapsed { get; init; }
    }

    /// <summary>
    /// 分离。interleaved 是交错立体声（左、右、左、右…）；channels 为 1 时两路都复制同一份。
    /// progress 收到 0..1。
    /// </summary>
    public Result Separate(float[] interleaved, int channels, IProgress<double>? progress = null)
    {
        var started = DateTime.UtcNow;
        var (left, right) = SplitChannels(interleaved, channels);
        int samples = left.Length;
        if (samples == 0) throw new InvalidOperationException("这段音频里没有声音");

        int numFrames = samples / Hop + 1;                                  // center=true 的帧数
        int chunks = Math.Max(1, (numFrames + FramesPerChunk - 1) / FramesPerChunk);
        int paddedFrames = chunks * FramesPerChunk;
        int planeSize = paddedFrames * Bins;                                // 一个声道的幅度谱长度

        progress?.Report(0.05);

        // 1) 左右声道各自 STFT，同时装幅度谱（模型入口）
        var window = HannWindow(NFft);
        var fft = new Fft(NFft);
        var reL = new float[chunks * FramesPerChunk * Bins];
        var imL = new float[chunks * FramesPerChunk * Bins];
        var reR = new float[chunks * FramesPerChunk * Bins];
        var imR = new float[chunks * FramesPerChunk * Bins];
        var magnitude = new float[2 * planeSize];
        Spectrogram(left, window, fft, reL, imL, magnitude, 0, planeSize);
        Spectrogram(right, window, fft, reR, imR, magnitude, 1, planeSize);
        progress?.Report(0.35);

        // 2) 两个模型各跑一次（整首一次性喂：入口张量 = 2 × 块数 × 512 × 1024 个 float）
        var input = new DenseTensor<float>(new[] { 2, chunks, FramesPerChunk, Bins });
        for (int ch = 0; ch < 2; ch++)
            for (int g = 0; g < paddedFrames; g++)
                for (int b = 0; b < Bins; b++)
                    input[ch, g / FramesPerChunk, g % FramesPerChunk, b] = magnitude[ch * planeSize + g * Bins + b];
        progress?.Report(0.45);
        try
        {
            foreach (var kv in _vocals.InputMetadata)
                Console.WriteLine($"[分离] 模型输入 {kv.Key} 维 [{string.Join(",", kv.Value.Dimensions)}] 类型 {kv.Value.ElementType}");
            Console.WriteLine($"[分离] 常量 NFft={NFft} Bins={Bins} Frames={FramesPerChunk} chunks={chunks} paddedFrames={paddedFrames}");
            Console.WriteLine($"[分离] 入口张量 [{string.Join(",", input.Dimensions.ToArray())}] 长度 {input.Length}");
        }
        catch { }

        var vocalsMag = RunModel(_vocals, input);
        progress?.Report(0.7);
        var accompMag = RunModel(_accompaniment, input);
        progress?.Report(0.85);

        // 3) 软掩码 + iSTFT，叠回两条声部
        var vocalsL = new float[samples];
        var vocalsR = new float[samples];
        var accompL = new float[samples];
        var accompR = new float[samples];
        var maskL = new float[chunks * FramesPerChunk * Bins];
        var maskR = new float[chunks * FramesPerChunk * Bins];

        ApplyAndInverse(reL, imL, reR, imR, vocalsMag, accompMag, true,
            window, fft, vocalsL, vocalsR, maskL, maskR, paddedFrames, samples);
        progress?.Report(0.92);
        ApplyAndInverse(reL, imL, reR, imR, vocalsMag, accompMag, false,
            window, fft, accompL, accompR, maskL, maskR, paddedFrames, samples);
        progress?.Report(1.0);

        return new Result
        {
            Vocals = new Stem { Name = "人声", Left = vocalsL, Right = vocalsR },
            Accompaniment = new Stem { Name = "伴奏", Left = accompL, Right = accompR },
            Elapsed = DateTime.UtcNow - started,
        };
    }

    /// <summary>一个声道整首做 STFT，幅度写进 magnitude 的第 channel 个平面。</summary>
    private static void Spectrogram(float[] samples, float[] window, Fft fft,
        float[] re, float[] im, float[] magnitude, int channel, int planeSize)
    {
        var frame = new float[NFft];
        int numFrames = samples.Length / Hop + 1;
        for (int f = 0; f < numFrames; f++)
        {
            Array.Clear(frame);
            int start = f * Hop - NFft / 2;
            for (int i = 0; i < NFft; i++)
            {
                int s = start + i;
                if (s >= 0 && s < samples.Length) frame[i] = samples[s] * window[i];
            }
            int at = f * Bins;
            fft.Forward(frame, re, im, at);
            for (int b = 0; b < Bins; b++)
                magnitude[channel * planeSize + at + b] = MathF.Sqrt(re[at + b] * re[at + b] + im[at + b] * im[at + b]);
        }
    }

    /// <summary>跑一个模型，把输出从 [2, 块, 帧, 频] 拍平成两个平面。</summary>
    private float[] RunModel(InferenceSession session, DenseTensor<float> input)
    {
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, input) };
        using var results = session.Run(inputs);
        var tensor = results.First().AsTensor<float>();
        var dense = tensor.ToDenseTensor();
        var flat = new float[dense.Length];
        int at = 0;
        for (int ch = 0; ch < dense.Dimensions[0]; ch++)
            for (int c = 0; c < dense.Dimensions[1]; c++)
                for (int f = 0; f < dense.Dimensions[2]; f++)
                    for (int b = 0; b < dense.Dimensions[3]; b++)
                        flat[at++] = dense[ch, c, f, b];
        return flat;
    }

    /// <summary>软掩码 + 逐块 iSTFT。useVocals 为 true 取人声那条掩码，否则取伴奏。</summary>
    private static void ApplyAndInverse(float[] reL, float[] imL, float[] reR, float[] imR,
        float[] vocalsMag, float[] accompMag, bool useVocals,
        float[] window, Fft fft, float[] outL, float[] outR,
        float[] maskL, float[] maskR, int paddedFrames, int samples)
    {
        int planeSize = paddedFrames * Bins;
        int total = paddedFrames * Bins;
        for (int i = 0; i < total; i++)
        {
            float v = vocalsMag[i] * vocalsMag[i];
            float a = accompMag[i] * accompMag[i];
            float sum = v + a + 1e-10f;
            maskL[i] = useVocals ? (v + 5e-11f) / sum : (a + 5e-11f) / sum;
        }
        // 第二声道（右）的幅度在第二个平面，掩码对两个声道相同（与 sherpa 一致：按声道分别算，
        // 但这里两个声道的幅度分别来自同一模型的左右声道输出，所以要各自算一次）
        for (int i = 0; i < total; i++)
        {
            float v = vocalsMag[planeSize + i] * vocalsMag[planeSize + i];
            float a = accompMag[planeSize + i] * accompMag[planeSize + i];
            float sum = v + a + 1e-10f;
            maskR[i] = useVocals ? (v + 5e-11f) / sum : (a + 5e-11f) / sum;
        }

        var outRe = new float[total];
        var outIm = new float[total];
        for (int f = 0; f < paddedFrames; f++)
        {
            for (int b = 0; b < Bins; b++)
            {
                int i = f * Bins + b;
                outRe[i] = reL[i] * maskL[i];
                outIm[i] = imL[i] * maskL[i];
            }
        }
        InverseSpectrogram(outRe, outIm, window, fft, outL, samples);
        for (int f = 0; f < paddedFrames; f++)
        {
            for (int b = 0; b < Bins; b++)
            {
                int i = f * Bins + b;
                outRe[i] = reR[i] * maskR[i];
                outIm[i] = imR[i] * maskR[i];
            }
        }
        InverseSpectrogram(outRe, outIm, window, fft, outR, samples);
    }

    /// <summary>整首 iSTFT（重叠相加，再按窗能量归一）。</summary>
    private static void InverseSpectrogram(float[] re, float[] im, float[] window, Fft fft, float[] output, int samples)
    {
        var acc = new float[output.Length + NFft];
        var weight = new float[output.Length + NFft];
        int frames = output.Length / Hop + 1;
        for (int f = 0; f < frames; f++)
        {
            fft.Inverse(re, im, f * Bins);
            int start = f * Hop - NFft / 2;
            for (int i = 0; i < NFft; i++)
            {
                int s = start + i;
                if (s < 0 || s >= acc.Length) continue;
                acc[s] += fft.Time[i] * window[i];
                weight[s] += window[i] * window[i];
            }
        }
        for (int i = 0; i < output.Length; i++)
            output[i] = weight[i] > 1e-8f ? acc[i] / weight[i] : 0f;
    }

    /// <summary>交错采样拆成左右两路；单声道两路相同。</summary>
    private static (float[] Left, float[] Right) SplitChannels(float[] interleaved, int channels)
    {
        if (channels <= 1)
        {
            var copy = new float[interleaved.Length];
            Array.Copy(interleaved, copy, interleaved.Length);
            return (copy, (float[])interleaved.Clone());
        }
        int frames = interleaved.Length / channels;
        var left = new float[frames];
        var right = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            left[i] = interleaved[i * channels];
            right[i] = interleaved[i * channels + 1];
        }
        return (left, right);
    }

    private static float[] HannWindow(int n)
    {
        var w = new float[n];
        for (int i = 0; i < n; i++) w[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / n));
        return w;
    }

    public void Dispose()
    {
        _vocals.Dispose();
        _accompaniment.Dispose();
    }
}

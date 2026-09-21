using System;
using System.IO;
using MidiKeyPlayer.Audio.Onnx;

namespace MidiKeyPlayer.Audio;

/// <summary>
/// Spotify basic-pitch 的纯 C# 推理。模型（230 KB、3.6 万参数）内置在 exe 里，
/// 前置的 CQT 与谐波堆叠都在模型图内部，所以外部只要喂 22.05 kHz 单声道采样。
///
/// 与上游 Python 实现逐项对齐，常量见 basic_pitch/constants.py：
///   窗口 2 秒（43844 采样）、帧步 256、重叠 30 帧、输出 172 帧 ×（88 音高 / 264 轮廓）。
/// </summary>
internal sealed class BasicPitch
{
    public const int SampleRate = 22050;
    public const int FftHop = 256;
    public const int AnnotationsFps = SampleRate / FftHop;              // 86
    public const int WindowSeconds = 2;
    public const int AnnotFrames = AnnotationsFps * WindowSeconds;       // 172
    public const int AudioNSamples = SampleRate * WindowSeconds - FftHop; // 43844
    public const int OverlappingFrames = 30;
    public const int NoteBins = 88;
    public const int ContourBins = 264;

    /// <summary>模型一次推理的原始输出（未拼接）。</summary>
    public sealed class WindowOutput
    {
        public float[] Note = Array.Empty<float>();     // 172 × 88
        public float[] Onset = Array.Empty<float>();    // 172 × 88
        public float[] Contour = Array.Empty<float>();  // 172 × 264
    }

    /// <summary>整段音频推理并拼接后的结果。</summary>
    public sealed class Result
    {
        public float[] Note = Array.Empty<float>();     // 帧数 × 88
        public float[] Onset = Array.Empty<float>();
        public float[] Contour = Array.Empty<float>();  // 帧数 × 264
        public int Frames;
        public int NoteBins = BasicPitch.NoteBins;
        public int ContourBins = BasicPitch.ContourBins;
    }

    private readonly OnnxModel _model;
    private readonly OnnxGraphRunner _runner;
    private readonly float[] _inputBuffer = new float[AudioNSamples];
    private readonly OnnxTensor _inputTensor;

    private BasicPitch(OnnxModel model)
    {
        _model = model;
        _runner = new OnnxGraphRunner(model);
        _inputTensor = new OnnxTensor(OnnxDataType.Float, new[] { 1, AudioNSamples, 1 }, _inputBuffer);
    }

    /// <summary>
    /// 从内置资源读出模型。用嵌入资源（而不是 AvaloniaResource）是因为自检进程
    /// 在 Avalonia 初始化之前就跑，这条路径不依赖界面。
    /// </summary>
    public const string ResourceName = "MidiKeyPlayer.Assets.nmp.onnx";

    public static BasicPitch Load()
    {
        using var stream = typeof(BasicPitch).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"内置资源缺失：{ResourceName}");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return new BasicPitch(OnnxModel.Parse(ms.ToArray()));
    }

    public static BasicPitch FromBytes(byte[] onnx) => new(OnnxModel.Parse(onnx));

    public OnnxModel Model => _model;

    /// <summary>跑一窗音频（必须正好 AudioNSamples 个采样），返回三个原始输出。</summary>
    public WindowOutput RunWindow(float[] window)
    {
        if (window.Length < AudioNSamples)
            throw new ArgumentException($"窗口需要 {AudioNSamples} 个采样，只给了 {window.Length}");
        Array.Copy(window, _inputBuffer, AudioNSamples);

        var outputs = _runner.RunNamed(_inputTensor);
        var result = new WindowOutput();
        OnnxTensor? note = null, onset = null, contour = null;

        // 输出名沿用 TF 导出的 StatefulPartitionedCall:0/1/2（0=轮廓、1=音高、2=起音）
        foreach (var kv in outputs)
        {
            string name = kv.Key;
            if (name.EndsWith(":0", StringComparison.Ordinal)) contour ??= kv.Value;
            else if (name.EndsWith(":1", StringComparison.Ordinal)) note ??= kv.Value;
            else if (name.EndsWith(":2", StringComparison.Ordinal)) onset ??= kv.Value;
        }
        // 名字对不上就按形状挑：264 列是轮廓，剩下两个按出现顺序当音高与起音
        if (note == null || onset == null || contour == null)
        {
            note = onset = contour = null;
            foreach (var kv in outputs)
            {
                var t = kv.Value;
                if (t.Dims.Length != 3) continue;
                if (t.Dims[2] == ContourBins) contour ??= t;
                else if (note == null) note = t;
                else onset ??= t;
            }
        }

        if (note == null || onset == null || contour == null)
            throw new InvalidOperationException("模型输出与预期不符：需要 88 / 88 / 264 三路输出");
        result.Note = note.F!;
        result.Onset = onset.F!;
        result.Contour = contour.F!;
        return result;
    }

    /// <summary>
    /// 整段音频推理。audio 必须是 22.05 kHz 单声道。
    /// 返回的矩阵已按上游 unwrap_output 去掉每窗两端各 15 帧的重叠。
    /// </summary>
    public Result Predict(float[] audio, Action<double>? onProgress = null)
    {
        int overlapLen = OverlappingFrames * FftHop;              // 7680
        int hop = AudioNSamples - overlapLen;                     // 36164
        int originalLength = audio.Length;

        var padded = new float[originalLength + overlapLen / 2];
        Array.Copy(audio, 0, padded, overlapLen / 2, originalLength);

        int nWindows = 0;
        for (int i = 0; i < padded.Length; i += hop) nWindows++;
        if (nWindows == 0) nWindows = 1;

        var note = new float[nWindows * AnnotFrames * NoteBins];
        var onset = new float[nWindows * AnnotFrames * NoteBins];
        var contour = new float[nWindows * AnnotFrames * ContourBins];

        var window = new float[AudioNSamples];
        for (int wi = 0, start = 0; wi < nWindows; wi++, start += hop)
        {
            int copy = Math.Min(AudioNSamples, Math.Max(0, padded.Length - start));
            Array.Clear(window);
            if (copy > 0) Array.Copy(padded, start, window, 0, copy);

            var outw = RunWindow(window);
            Array.Copy(outw.Note, 0, note, wi * AnnotFrames * NoteBins, AnnotFrames * NoteBins);
            Array.Copy(outw.Onset, 0, onset, wi * AnnotFrames * NoteBins, AnnotFrames * NoteBins);
            Array.Copy(outw.Contour, 0, contour, wi * AnnotFrames * ContourBins, AnnotFrames * ContourBins);
            onProgress?.Invoke((wi + 1) / (double)nWindows);
        }

        var noteOut = Unwrap(note, nWindows, NoteBins, originalLength, hop);
        return new Result
        {
            Note = noteOut,
            Onset = Unwrap(onset, nWindows, NoteBins, originalLength, hop),
            Contour = Unwrap(contour, nWindows, ContourBins, originalLength, hop),
            Frames = noteOut.Length / NoteBins,
        };
    }

    /// <summary>
    /// 与上游 unwrap_output 一致：每窗去掉首尾各 15 帧重叠，再按窗口顺序拼起来，
    /// 最后按「原音频长度 / 窗跳」截断到应有帧数。
    /// </summary>
    private static float[] Unwrap(float[] batched, int nWindows, int bins, int originalLength, int hop)
    {
        int nOlap = OverlappingFrames / 2;                 // 15
        int keep = AnnotFrames - OverlappingFrames;        // 142
        int expected = (int)(originalLength / (double)hop * keep);
        var result = new float[expected * bins];
        int written = 0;
        for (int w = 0; w < nWindows && written < expected; w++)
        {
            int baseIdx = w * AnnotFrames * bins;
            for (int f = 0; f < keep && written < expected; f++, written++)
            {
                int src = baseIdx + (nOlap + f) * bins;
                Array.Copy(batched, src, result, written * bins, bins);
            }
        }
        if (written < expected)
            return result[..(written * bins)];
        return result;
    }

    /// <summary>帧号 → 秒。与上游 model_frames_to_time 一致（含窗口对齐补偿）。</summary>
    public static double FramesToTime(int frame)
    {
        double t = frame * (double)FftHop / SampleRate;
        double windowNumber = Math.Floor(frame / (double)AnnotFrames);
        double windowOffset = FftHop / (double)SampleRate * (AnnotFrames - AudioNSamples / (double)FftHop)
            + 0.0018;
        return t - windowOffset * windowNumber;
    }
}

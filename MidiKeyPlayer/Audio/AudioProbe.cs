using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace MidiKeyPlayer.Audio;

/// <summary>
/// 【开发用】音频转 MIDI 的数值探针：拿 Python 参考实现（同一份 ONNX 模型 + onnxruntime）
/// 导出的逐窗口输出，和这里纯 C# 执行器的结果逐元素比对。
///
/// 用法：设 <c>MIDIKEY_BP_PROBE=&lt;参考数据目录&gt;</c> 启动程序。目录里要有
/// <c>raw.json</c> 与 <c>raw.bin</c>（由开发脚本 ref_pipeline.py 生成）。
/// 与 GameSelfTest 一样属于可删的开发件：不建窗口、不发按键。
/// </summary>
internal static class AudioProbe
{
    public const string EnvVar = "MIDIKEY_BP_PROBE";

    /// <summary>整条链路（解码 → 推理 → 写 MIDI）的开发入口：值为音频文件路径。</summary>
    public const string ConvertEnvVar = "MIDIKEY_BP_CONVERT";

    public static bool Requested => (Environment.GetEnvironmentVariable(EnvVar) ?? "").Length > 0;
    public static bool ConvertRequested => (Environment.GetEnvironmentVariable(ConvertEnvVar) ?? "").Length > 0;

    /// <summary>跑一次真实文件转换，写完 MIDI 打印一行摘要。开发用。</summary>
    public static int RunConvert()
    {
        string path = Environment.GetEnvironmentVariable(ConvertEnvVar) ?? "";
        try
        {
            // 解码诊断：采样率、声道、重采样后的电平
            try
            {
                var (raw, sampleRate, channels) = AudioDecoder.Decode(path);
                var mono = AudioDecoder.ToMono(raw, channels);
                var rs = Resampler.Resample(mono, sampleRate, BasicPitch.SampleRate);
                Console.WriteLine($"原始：{raw.Length} 采样，峰值 {Peak(raw):F4}，RMS {Rms(raw):F4}");
                Console.WriteLine($"单声道：{mono.Length} 采样，峰值 {Peak(mono):F4}，RMS {Rms(mono):F4}");
                Console.WriteLine($"重采样：{sampleRate} Hz {channels} 声道 → 22.05 kHz；"
                    + $"{rs.Length} 采样（{rs.Length / 22050.0:F1}s）；峰值 {Peak(rs):F4}，RMS {Rms(rs):F4}");
            }
            catch (Exception ex)
            {
                Console.WriteLine("解码诊断失败：" + ex.Message);
            }

            var outcome = AudioToMidi.Transcribe(path, new Progress<double>(p =>
                Console.WriteLine($"  进度 {p * 100:F0}%")));
            string midi = AudioToMidi.SuggestPath(path);
            AudioToMidi.SaveMidi(midi, outcome);
            Console.WriteLine($"音频：{path}");
            Console.WriteLine($"时长：{outcome.DurationSec:F1}s；帧 {outcome.Frames}；音符 {outcome.Notes.Count}");
            Console.WriteLine($"耗时：{outcome.Elapsed.TotalSeconds:F1}s");
            Console.WriteLine($"写出：{midi}");
            foreach (var n in outcome.Notes)
                Console.WriteLine($"  {n.Pitch} {BasicPitch.FramesToTime(n.StartFrame):F3}~"
                    + $"{BasicPitch.FramesToTime(n.EndFrame):F3}s 能量 {n.Amplitude:F2}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("转换失败：" + ex);
            return 1;
        }
    }

    public static int Run()
    {
        string dir = Environment.GetEnvironmentVariable(EnvVar) ?? "";
        var sb = new StringBuilder();
        int failed = 0;
        try
        {
            string jsonPath = Path.Combine(dir, "raw.json");
            string binPath = Path.Combine(dir, "raw.bin");
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var root = doc.RootElement;
            int nWindows = root.GetProperty("n_windows").GetInt32();
            var shapes = root.GetProperty("shapes");

            var bytes = File.ReadAllBytes(binPath);
            int audioSamples = root.GetProperty("audio_n_samples").GetInt32();
            int noteCount = NoteCount(shapes, "note");
            int onsetCount = NoteCount(shapes, "onset");
            int contourCount = NoteCount(shapes, "contour");

            int offset = 0;
            var windows = new float[nWindows][];
            for (int w = 0; w < nWindows; w++)
            {
                windows[w] = new float[audioSamples];
                Buffer.BlockCopy(bytes, offset, windows[w], 0, audioSamples * 4);
                offset += audioSamples * 4;
            }
            var refNote = Slice(bytes, offset, nWindows * noteCount); offset += nWindows * noteCount * 4;
            var refOnset = Slice(bytes, offset, nWindows * onsetCount); offset += nWindows * onsetCount * 4;
            var refContour = Slice(bytes, offset, nWindows * contourCount); offset += nWindows * contourCount * 4;

            var bp = BasicPitch.Load();
            sb.AppendLine($"参考目录：{dir}");
            sb.AppendLine($"窗口数：{nWindows}；每窗采样 {audioSamples}");
            sb.AppendLine();

            double worst = 0;
            double totalMs = 0;
            for (int w = 0; w < nWindows; w++)
            {
                var sw = Stopwatch.StartNew();
                var got = bp.RunWindow(windows[w]);
                sw.Stop();
                totalMs += sw.Elapsed.TotalMilliseconds;

                double dNote = MaxDiff(got.Note, refNote, w * noteCount, noteCount);
                double dOnset = MaxDiff(got.Onset, refOnset, w * onsetCount, onsetCount);
                double dContour = MaxDiff(got.Contour, refContour, w * contourCount, contourCount);
                worst = Math.Max(worst, Math.Max(dNote, Math.Max(dOnset, dContour)));
                sb.AppendLine($"窗口 {w}: note {dNote:E3}  onset {dOnset:E3}  contour {dContour:E3}  "
                    + $"({sw.Elapsed.TotalMilliseconds:F0} ms)");
            }

            sb.AppendLine();
            sb.AppendLine($"最大绝对误差：{worst:E3}");
            sb.AppendLine($"平均每窗耗时：{totalMs / Math.Max(1, nWindows):F0} ms");
            if (worst > 1e-3)
            {
                failed++;
                sb.AppendLine("FAIL 与参考实现的误差超过 1e-3");
            }
            else
            {
                sb.AppendLine("结果：数值与参考实现一致");
            }

            failed += CompareEvents(dir, bp, sb);
        }
        catch (Exception ex)
        {
            failed++;
            sb.AppendLine("FAIL 探针抛异常：" + ex);
        }

        string text = sb.ToString();
        try { Console.WriteLine(text); } catch { }
        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "midikey-bp-probe.txt"), text,
                new UTF8Encoding(false));
        }
        catch { /* 写不出去也要把退出码给对 */ }
        return failed == 0 ? 0 : 1;
    }

    /// <summary>整段推理 + 解码，和参考实现的音符事件逐条比对。</summary>
    private static int CompareEvents(string dir, BasicPitch bp, StringBuilder sb)
    {
        string eventsPath = Path.Combine(dir, "events.json");
        string audioPath = Path.Combine(dir, "audio.bin");
        if (!File.Exists(eventsPath) || !File.Exists(audioPath)) return 0;

        var audioBytes = File.ReadAllBytes(audioPath);
        var audio = new float[audioBytes.Length / 4];
        Buffer.BlockCopy(audioBytes, 0, audio, 0, audio.Length * 4);

        var sw = Stopwatch.StartNew();
        var result = bp.Predict(audio);
        sw.Stop();

        var swDecode = Stopwatch.StartNew();
        var mine = NoteDecoder.Decode(result.Note, result.Onset, result.Frames);
        swDecode.Stop();

        using var doc = JsonDocument.Parse(File.ReadAllText(eventsPath));
        var expected = new List<(int Start, int End, int Pitch)>();
        foreach (var e in doc.RootElement.GetProperty("events").EnumerateArray())
            expected.Add((e.GetProperty("start_frame").GetInt32(),
                          e.GetProperty("end_frame").GetInt32(),
                          e.GetProperty("pitch").GetInt32()));

        var got = new List<(int Start, int End, int Pitch)>();
        foreach (var e in mine) got.Add((e.StartFrame, e.EndFrame, e.Pitch));

        sb.AppendLine();
        sb.AppendLine($"整段推理 {audio.Length / (double)BasicPitch.SampleRate:F1} 秒：{sw.Elapsed.TotalMilliseconds:F0} ms"
            + $"（解码 {swDecode.Elapsed.TotalMilliseconds:F0} ms）；参考 {expected.Count} 个音，本实现 {got.Count} 个音");

        expected.Sort();
        got.Sort();
        int i2 = 0, j2 = 0, missing = 0, extra = 0;
        while (i2 < expected.Count && j2 < got.Count)
        {
            int cmp = expected[i2].CompareTo(got[j2]);
            if (cmp == 0) { i2++; j2++; }
            else if (cmp < 0) { missing++; sb.AppendLine($"  参考有、本实现没有：{expected[i2]}"); i2++; }
            else { extra++; sb.AppendLine($"  本实现多出：{got[j2]}"); j2++; }
        }
        for (; i2 < expected.Count; i2++) { missing++; sb.AppendLine($"  参考有、本实现没有：{expected[i2]}"); }
        for (; j2 < got.Count; j2++) { extra++; sb.AppendLine($"  本实现多出：{got[j2]}"); }

        if (missing == 0 && extra == 0)
        {
            sb.AppendLine("结果：音符事件与参考实现完全一致");
            return 0;
        }
        sb.AppendLine($"事件不一致：缺 {missing}、多 {extra}（下面再用参考矩阵单独跑一遍解码器）");

        // 用参考实现自己的矩阵再解码一次，把「模型数值噪声」与「解码逻辑差异」分开
        string matrixPath = Path.Combine(dir, "matrix.bin");
        if (!File.Exists(matrixPath)) return 1;
        var mb = File.ReadAllBytes(matrixPath);
        int cells = result.Frames * BasicPitch.NoteBins;
        var refFrames = new float[cells];
        var refOnsets = new float[cells];
        Buffer.BlockCopy(mb, 0, refFrames, 0, cells * 4);
        Buffer.BlockCopy(mb, cells * 4, refOnsets, 0, cells * 4);
        var refDecoded = NoteDecoder.Decode(refFrames, refOnsets, result.Frames);
        var refList = new List<(int Start, int End, int Pitch)>();
        foreach (var e in refDecoded) refList.Add((e.StartFrame, e.EndFrame, e.Pitch));
        refList.Sort();
        int same = 0;
        foreach (var e in refList) if (expected.Contains(e)) same++;
        sb.AppendLine($"用参考矩阵解码：{refList.Count} 个音，其中 {same} 个与参考事件一致");

        bool exact = same == expected.Count && refList.Count == expected.Count;
        if (exact)
        {
            sb.AppendLine("结论：解码逻辑一致，差异来自模型数值噪声（1e-4 量级）");
            return 0;
        }
        sb.AppendLine("FAIL 解码逻辑本身不一致");
        return 1;
    }

    private static double Peak(float[] data)
    {
        double peak = 0;
        foreach (float v in data) { double a = Math.Abs(v); if (a > peak) peak = a; }
        return peak;
    }

    private static double Rms(float[] data)
    {
        if (data.Length == 0) return 0;
        double sum = 0;
        foreach (float v in data) sum += v * (double)v;
        return Math.Sqrt(sum / data.Length);
    }

    /// <summary>单个窗口的元素数：形状是 [窗口数, 帧数, 频点数]，跳过第一维。</summary>
    private static int NoteCount(JsonElement shapes, string key)
    {
        var dims = shapes.GetProperty(key);
        int n = 1;
        for (int i = 1; i < dims.GetArrayLength(); i++) n *= dims[i].GetInt32();
        return n;
    }

    private static float[] Slice(byte[] bytes, int offset, int count)
    {
        var f = new float[count];
        Buffer.BlockCopy(bytes, offset, f, 0, count * 4);
        return f;
    }

    private static double MaxDiff(float[] got, float[] reference, int start, int count)
    {
        double max = 0;
        for (int i = 0; i < count; i++)
        {
            double d = Math.Abs(got[i] - reference[start + i]);
            if (d > max) max = d;
        }
        return max;
    }
}

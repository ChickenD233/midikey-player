using System;
using System.IO;
using System.Text;

namespace MidiKeyPlayer.Audio;

/// <summary>
///【开发用，可删】分离探针：拿一个音频文件跑一遍「人声 / 伴奏」分离，
/// 报告两条声部的能量、以及「人声 + 伴奏 ≈ 原音」的重建误差。
///
/// 用法：设 <c>MIDIKEY_STEM_PROBE=&lt;模型目录&gt;</c>
/// 与 <c>MIDIKEY_STEM_PROBE_AUDIO=&lt;音频路径&gt;</c> 启动程序。
/// 结果写 <c>MIDIKEY_STEM_PROBE_OUT</c>（默认 %TEMP%\midikey-stem-probe.txt）。
/// </summary>
internal static class StemProbe
{
    public const string EnvVar = "MIDIKEY_STEM_PROBE";

    public static bool Requested => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVar));

    private static readonly StringBuilder Log = new();

    private static void Say(string line)
    {
        Log.AppendLine(line);
        try { Console.WriteLine(line); } catch { }
    }

    public static int Run()
    {
        string dir = Environment.GetEnvironmentVariable(EnvVar) ?? "";
        string audio = Environment.GetEnvironmentVariable("MIDIKEY_STEM_PROBE_AUDIO") ?? "";
        string outPath = Environment.GetEnvironmentVariable("MIDIKEY_STEM_PROBE_OUT") ?? "";
        if (outPath.Length == 0) outPath = Path.Combine(Path.GetTempPath(), "midikey-stem-probe.txt");

        Say($"分离探针 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        try
        {
            string vocals = Path.Combine(dir, "vocals.fp16.onnx");
            string accomp = Path.Combine(dir, "accompaniment.fp16.onnx");
            if (!File.Exists(vocals) || !File.Exists(accomp))
            {
                Say($"模型不全：{dir}（要 vocals.fp16.onnx 与 accompaniment.fp16.onnx）");
                return 1;
            }
            if (audio.Length == 0 || !File.Exists(audio))
            {
                Say($"音频不存在：{audio}");
                return 1;
            }

            var (samples, rate, channels) = AudioDecoder.Decode(audio);
            Say($"音频：{Path.GetFileName(audio)} {rate} Hz {channels} 声道");
            // 模型要 44.1 kHz：按声道分别重采样
            int frames = samples.Length / Math.Max(1, channels);
            var l = new float[frames];
            var r = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                l[i] = samples[i * channels];
                r[i] = channels > 1 ? samples[i * channels + 1] : l[i];
            }
            l = Resampler.Resample(l, rate, SpleeterSeparator.SampleRate);
            r = Resampler.Resample(r, rate, SpleeterSeparator.SampleRate);
            var stereo = new float[l.Length * 2];
            for (int i = 0; i < l.Length; i++) { stereo[i * 2] = l[i]; stereo[i * 2 + 1] = r[i]; }
            Say($"重采样到 {SpleeterSeparator.SampleRate} Hz：{l.Length} 帧（{l.Length / (double)SpleeterSeparator.SampleRate:F1} 秒）");

            var progressCount = 0;
            var progress = new Progress<double>(p =>
            {
                progressCount++;
                if (progressCount % 20 == 0) Say($"  进度 {p * 100:F0}%");
            });
            using var separator = new SpleeterSeparator(vocals, accomp);
            var result = separator.Separate(stereo, 2, progress);
            Say($"分离完成，用时 {result.Elapsed.TotalSeconds:F1} 秒");

            ReportStem("人声", result.Vocals, stereo, l.Length);
            ReportStem("伴奏", result.Accompaniment, stereo, l.Length);

            double sumVocals = 0, sumAccomp = 0, sumBoth = 0;
            for (int i = 0; i < l.Length; i++)
            {
                double v = result.Vocals.Left[i], a = result.Accompaniment.Left[i], o = stereo[i * 2];
                sumVocals += (v - o) * (v - o);
                sumAccomp += (a - o) * (a - o);
                sumBoth += (v + a - o) * (v + a - o);
            }
            Say($"重建误差（人声+伴奏 对 原音，左声道）：RMS {Math.Sqrt(sumBoth / Math.Max(1, l.Length)):E3}");
            Say($"  单看人声与原音的 RMS 差 {Math.Sqrt(sumVocals / Math.Max(1, l.Length)):E3}；" +
                $"伴奏与原音的 RMS 差 {Math.Sqrt(sumAccomp / Math.Max(1, l.Length)):E3}");

            // 导出分离结果供试听（16 位 WAV）
            string outDir = Path.GetDirectoryName(outPath) ?? Path.GetTempPath();
            WriteWav(Path.Combine(outDir, "stem-vocals.wav"), result.Vocals, l.Length);
            WriteWav(Path.Combine(outDir, "stem-accompaniment.wav"), result.Accompaniment, l.Length);
            Say($"已导出：{Path.Combine(outDir, "stem-vocals.wav")} 与 stem-accompaniment.wav");
        }
        catch (Exception ex)
        {
            var parts = new System.Collections.Generic.List<string>();
            Exception? e = ex;
            while (e != null) { parts.Add($"{e.GetType().Name}: {e.Message}"); e = e.InnerException; }
            Say("跑不通：" + string.Join("  <-  ", parts));
            try { File.WriteAllText(outPath, Log.ToString(), new UTF8Encoding(false)); } catch { }
            return 1;
        }

        try { File.WriteAllText(outPath, Log.ToString(), new UTF8Encoding(false)); } catch { }
        Say($"报告：{outPath}");
        return 0;
    }

    private static void ReportStem(string name, SpleeterSeparator.Stem stem, float[] original, int frames)
    {
        double energy = 0, energyAll = 0;
        for (int i = 0; i < frames; i++)
        {
            energy += stem.Left[i] * stem.Left[i] + stem.Right[i] * stem.Right[i];
            energyAll += original[i * 2] * original[i * 2] + original[i * 2 + 1] * original[i * 2 + 1];
        }
        double rms = Math.Sqrt(energy / Math.Max(1, frames * 2));
        double rmsAll = Math.Sqrt(energyAll / Math.Max(1, frames * 2));
        Say($"{name}：RMS {rms:F5}（原音 RMS {rmsAll:F5}，占比 {(rmsAll > 0 ? rms / rmsAll * 100 : 0):F1}%）");
    }

    private static void WriteWav(string path, SpleeterSeparator.Stem stem, int frames)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        int dataBytes = frames * 2 * 2;
        w.Write(new[] { 'R', 'I', 'F', 'F' });
        w.Write(36 + dataBytes);
        w.Write(new[] { 'W', 'A', 'V', 'E' });
        w.Write(new[] { 'f', 'm', 't', ' ' });
        w.Write(16);
        w.Write((short)1);
        w.Write((short)2);
        w.Write(SpleeterSeparator.SampleRate);
        w.Write(SpleeterSeparator.SampleRate * 4);
        w.Write((short)4);
        w.Write((short)16);
        w.Write(new[] { 'd', 'a', 't', 'a' });
        w.Write(dataBytes);
        for (int i = 0; i < frames; i++)
        {
            w.Write((short)Math.Clamp(stem.Left[i] * 32767f, -32768f, 32767f));
            w.Write((short)Math.Clamp(stem.Right[i] * 32767f, -32768f, 32767f));
        }
    }
}

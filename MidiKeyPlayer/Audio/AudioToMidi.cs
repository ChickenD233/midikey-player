using System;
using System.Collections.Generic;
using System.IO;
using MidiKeyPlayer.Engine;
using MidiKeyPlayer.Midi;

namespace MidiKeyPlayer.Audio;

/// <summary>
/// 音频转 MIDI 的总入口：解码 → 模型推理 → 解码成音符 → 写标准 MIDI 文件。
///
/// 面向界面的一句话用法：<c>Transcribe(path, progress)</c> 拿到音符，
/// 再用 <c>SuggestPath</c> + <c>SaveMidi</c> 落盘。
/// </summary>
internal static class AudioToMidi
{
    /// <summary>转写结果。</summary>
    public sealed class Outcome
    {
        public string AudioPath { get; init; } = "";
        public List<NoteDecoder.NoteEvent> Notes { get; init; } = new();
        public double DurationSec { get; init; }
        public int Frames { get; init; }
        public TimeSpan Elapsed { get; init; }
    }

    /// <summary>支持导入的音频扩展名（能不能解由系统解码器决定）。</summary>
    public static readonly string[] AudioExtensions =
    {
        ".wav", ".mp3", ".m4a", ".aac", ".wma", ".flac", ".mp4", ".ogg", ".opus", ".aiff", ".aif"
    };

    /// <summary>
    /// 转写。progress 收到 0..1 的进度；每转完一窗报一次。
    /// 解码与推理都是纯计算，调用方放到后台线程上跑。
    /// </summary>
    public static Outcome Transcribe(string audioPath, IProgress<double>? progress = null)
    {
        var started = DateTime.UtcNow;
        float[] audio = AudioDecoder.DecodeToModelRate(audioPath);
        if (audio.Length == 0) throw new InvalidOperationException("这个文件里没有声音");
        // 太长的音频先拦住：一窗约 0.3 秒，一小时的文件要跑十几分钟
        double seconds = audio.Length / (double)BasicPitch.SampleRate;
        if (seconds > MaxSeconds)
            throw new InvalidOperationException($"音频太长（{seconds / 60:F0} 分钟）。请先剪到 {MaxSeconds / 60:F0} 分钟以内");

        progress?.Report(0.05);
        var bp = BasicPitch.Load();
        var result = bp.Predict(audio, p => progress?.Report(0.05 + 0.9 * p));
        progress?.Report(0.96);

        var notes = NoteDecoder.Decode(result.Note, result.Onset, result.Frames);
        progress?.Report(1.0);

        return new Outcome
        {
            AudioPath = audioPath,
            Notes = notes,
            DurationSec = seconds,
            Frames = result.Frames,
            Elapsed = DateTime.UtcNow - started,
        };
    }

    /// <summary>上限 20 分钟：再长就不适合这个工具了（推理要跑很久）。</summary>
    public const int MaxSeconds = 20 * 60;

    /// <summary>音符事件 → 界面用的 RawNote（速度按模型给的能量换算）。</summary>
    public static List<RawNote> ToRawNotes(IReadOnlyList<NoteDecoder.NoteEvent> notes)
    {
        var list = new List<RawNote>(notes.Count);
        foreach (var n in notes)
        {
            double start = BasicPitch.FramesToTime(n.StartFrame);
            double end = BasicPitch.FramesToTime(n.EndFrame);
            if (end <= start) continue;
            int velocity = (int)Math.Round(NoteDecoder.VelocityScale * n.Amplitude);
            list.Add(new RawNote
            {
                Pitch = Math.Clamp(n.Pitch, 0, 127),
                Start = Math.Max(0, start),
                End = end,
                Velocity = Math.Clamp(velocity, 1, 127),
                Channel = 0,
                Voice = 0,
            });
        }
        list.Sort((a, b) => a.Start.CompareTo(b.Start));
        return list;
    }

    /// <summary>不覆盖已有文件：重名就加序号。</summary>
    public static string SuggestPath(string audioPath)
    {
        string dir = Path.GetDirectoryName(audioPath) ?? "";
        string name = Path.GetFileNameWithoutExtension(audioPath);
        if (dir.Length == 0) dir = Path.GetTempPath();
        string candidate = Path.Combine(dir, name + ".mid");
        for (int i = 2; i < 100 && File.Exists(candidate); i++)
            candidate = Path.Combine(dir, $"{name} ({i}).mid");
        return candidate;
    }

    /// <summary>写成标准 MIDI 文件（格式 0、120 BPM、480 PPQ）。</summary>
    public static void SaveMidi(string midiPath, Outcome outcome)
        => MidiExporter.Write(midiPath, ToRawNotes(outcome.Notes), "MidiKeyPlayer 转写");
}

using System;
using System.Collections.Generic;
using System.Threading;
using MidiKeyPlayer.Engine;
using MidiKeyPlayer.Midi;

namespace MidiKeyPlayer.Audio;

/// <summary>
/// 「音频 → 人声 / 伴奏」两轨 MIDI 的整条链路：
/// 解码 → 分离（<see cref="SpleeterSeparator"/>）→ 两条声部各自 basic-pitch 转写
/// → 写成一个两轨 MIDI（轨名「人声」「伴奏」）。
///
/// 分离模型不在 exe 里，首次使用时按需下载（见 <see cref="StemModels"/>）。
/// </summary>
internal static class StemPipeline
{
    /// <summary>结果：写出的 MIDI 路径 + 两条声部的音符数。</summary>
    public sealed class Outcome
    {
        public string MidiPath { get; init; } = "";
        public int VocalNotes { get; init; }
        public int AccompanimentNotes { get; init; }
        public double DurationSec { get; init; }
        public TimeSpan Elapsed { get; init; }
    }

    /// <summary>分离后的两条声部（44.1 kHz 立体声）转写并写成两轨 MIDI。</summary>
    public static Outcome Run(string audioPath, string midiPath, IProgress<double>? progress = null,
                              CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;

        // 1) 解码 + 重采样到模型要的 44.1 kHz 立体声
        progress?.Report(0.02);
        var (samples, rate, channels) = AudioDecoder.Decode(audioPath);
        if (samples.Length == 0) throw new InvalidOperationException("这个文件里没有声音");
        int frames = samples.Length / Math.Max(1, channels);
        double seconds = frames / (double)rate;
        if (seconds > AudioToMidi.MaxSeconds)
            throw new InvalidOperationException(
                $"音频太长（{seconds / 60:F0} 分钟）。分离比转写慢，请先剪到 {AudioToMidi.MaxSeconds / 60:F0} 分钟以内");

        var stereo = ResampleTo48(samples, channels, rate);
        ct.ThrowIfCancellationRequested();
        progress?.Report(0.08);

        // 2) 分离
        using var separator = new SpleeterSeparator(StemModels.VocalsPath, StemModels.AccompanimentPath);
        var result = separator.Separate(stereo, 2,
            new Progress<double>(p => progress?.Report(0.08 + p * 0.62)));
        ct.ThrowIfCancellationRequested();
        progress?.Report(0.72);

        // 3) 两条声部各自转写（同一个 basic-pitch 实例复用）
        var vocals = TranscribeStem(result.Vocals, 0.72, 0.14, progress);
        ct.ThrowIfCancellationRequested();
        var accompaniment = TranscribeStem(result.Accompaniment, 0.86, 0.12, progress);

        // 4) 写成两轨 MIDI
        MidiExporter.WriteMulti(midiPath, new (string, IReadOnlyList<RawNote>)[]
        {
            ("人声", vocals),
            ("伴奏", accompaniment),
        });
        progress?.Report(1.0);

        return new Outcome
        {
            MidiPath = midiPath,
            VocalNotes = vocals.Count,
            AccompanimentNotes = accompaniment.Count,
            DurationSec = seconds,
            Elapsed = DateTime.UtcNow - started,
        };
    }

    /// <summary>一条声部转写：左右混成单声道、重采样到 22.05 kHz，再跑 basic-pitch。</summary>
    private static List<RawNote> TranscribeStem(SpleeterSeparator.Stem stem, double progressFrom,
        double progressSpan, IProgress<double>? progress)
    {
        int frames = stem.Left.Length;
        var mono = new float[frames];
        for (int i = 0; i < frames; i++) mono[i] = 0.5f * (stem.Left[i] + stem.Right[i]);
        var atModelRate = Resampler.Resample(mono, SpleeterSeparator.SampleRate, BasicPitch.SampleRate);

        var bp = BasicPitch.Load();
        var predicted = bp.Predict(atModelRate, p => progress?.Report(progressFrom + progressSpan * p));
        var notes = NoteDecoder.Decode(predicted.Note, predicted.Onset, predicted.Frames);
        var list = AudioToMidi.ToRawNotes(notes);
        // 声部序号写进每个音：卷帘按声轨上色，两轨不会串色
        int voice = stem.Name == "人声" ? 0 : 1;
        for (int i = 0; i < list.Count; i++) list[i] = WithVoice(list[i], voice);
        return list;
    }

    private static RawNote WithVoice(RawNote n, int voice) => new()
    {
        Pitch = n.Pitch,
        Start = n.Start,
        End = n.End,
        Velocity = n.Velocity,
        Channel = n.Channel,
        Voice = voice,
    };

    /// <summary>把任意采样率的交错采样变成 44.1 kHz 交错立体声。</summary>
    private static float[] ResampleTo48(float[] samples, int channels, int rate)
    {
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
        for (int i = 0; i < l.Length; i++)
        {
            stereo[i * 2] = l[i];
            stereo[i * 2 + 1] = r[i];
        }
        return stereo;
    }
}

using System;
using System.Collections.Generic;

namespace MidiKeyPlayer.Audio;

/// <summary>
/// 把模型输出解码成音符。逐行对应上游 basic_pitch/note_creation.py 的
/// <c>output_to_notes_polyphonic</c>（Apache-2.0，Spotify AB），阈值与循环结构保持一致。
///
/// 相对上游删掉了弯音（pitch bend）一支：按键播放器发不出弯音，
/// 卷帘也不显示它，留着只会让 MIDI 变大。
/// </summary>
internal static class NoteDecoder
{
    /// <summary>音符事件；帧号是以 22050/256 ≈ 86.13 Hz 计的时间轴。</summary>
    public sealed class NoteEvent
    {
        public int StartFrame { get; init; }
        public int EndFrame { get; init; }
        public int Pitch { get; init; }        // 已加 MIDI 偏移，21 = 最低键
        public float Amplitude { get; init; }  // 0..1，乘 127 就是力度
    }

    public const int MidiOffset = 21;
    public const int MaxFreqIdx = 87;
    public const int EnergyTolerance = 11;
    public const int DefaultMinNoteLen = 11;      // 帧。约 127 ms
    public const float DefaultOnsetThreshold = 0.5f;
    public const float DefaultFrameThreshold = 0.3f;
    public const int VelocityScale = 127;

    /// <summary>
    /// frames / onsets 都是「帧数 × 88」的行主序矩阵（每帧 88 个半音，21..108）。
    /// </summary>
    public static List<NoteEvent> Decode(
        float[] frames, float[] onsets, int nFrames,
        float onsetThreshold = DefaultOnsetThreshold,
        float frameThreshold = DefaultFrameThreshold,
        int minNoteLen = DefaultMinNoteLen,
        bool inferOnsets = true,
        bool melodiaTrick = true,
        int energyTol = EnergyTolerance)
    {
        const int bins = BasicPitch.NoteBins;
        var events = new List<NoteEvent>();

        // 用帧能量的突变补一批起音（上游 get_infered_onsets）
        if (inferOnsets) onsets = InferOnsets(onsets, frames, nFrames, bins, 2);

        // 时间轴上的局部极大（scipy.signal.argrelmax，order=1、两端按边缘裁剪）
        var isPeak = new bool[nFrames * bins];
        for (int f = 0; f < nFrames; f++)
        {
            int prev = f > 0 ? f - 1 : 0;
            int next = f < nFrames - 1 ? f + 1 : f;
            int rowBase = f * bins;
            int prevBase = prev * bins;
            int nextBase = next * bins;
            for (int b = 0; b < bins; b++)
            {
                float v = onsets[rowBase + b];
                isPeak[rowBase + b] = v > onsets[prevBase + b] && v > onsets[nextBase + b];
            }
        }

        // 收集过阈值的峰，按上游顺序：时间倒序、同帧内频点倒序
        var starts = new List<(int Frame, int Bin)>();
        for (int f = nFrames - 1; f >= 0; f--)
        {
            int rowBase = f * bins;
            for (int b = bins - 1; b >= 0; b--)
            {
                if (isPeak[rowBase + b] && onsets[rowBase + b] >= onsetThreshold)
                    starts.Add((f, b));
            }
        }

        var remaining = new float[frames.Length];
        Array.Copy(frames, remaining, frames.Length);

        foreach (var (startFrame, bin) in starts)
        {
            if (startFrame >= nFrames - 1) continue;

            // 沿这个频点往后走，直到能量低于阈值并连续 energyTol 帧都不回升
            int i = startFrame + 1;
            int k = 0;
            while (i < nFrames - 1 && k < energyTol)
            {
                if (remaining[i * bins + bin] < frameThreshold) k++;
                else k = 0;
                i++;
            }
            i -= k;

            if (i - startFrame <= minNoteLen) continue;

            Zero(remaining, nFrames, bins, startFrame, i, bin);
            events.Add(new NoteEvent
            {
                StartFrame = startFrame,
                EndFrame = i,
                Pitch = bin + MidiOffset,
                Amplitude = Mean(frames, bins, startFrame, i, bin),
            });
        }

        // melodia 后处理：起音漏掉的音，用剩余能量里的最大值补
        if (melodiaTrick)
        {
            while (true)
            {
                int bestIdx = -1;
                float best = frameThreshold;
                for (int idx = 0; idx < remaining.Length; idx++)
                {
                    if (remaining[idx] > best) { best = remaining[idx]; bestIdx = idx; }
                }
                if (bestIdx < 0) break;

                int mid = bestIdx / bins;
                int bin = bestIdx % bins;
                remaining[bestIdx] = 0;

                int i = mid + 1;
                int k = 0;
                while (i < nFrames - 1 && k < energyTol)
                {
                    if (remaining[i * bins + bin] < frameThreshold) k++;
                    else k = 0;
                    Zero(remaining, nFrames, bins, i, i + 1, bin);
                    i++;
                }
                int end = i - 1 - k;

                i = mid - 1;
                k = 0;
                while (i > 0 && k < energyTol)
                {
                    if (remaining[i * bins + bin] < frameThreshold) k++;
                    else k = 0;
                    Zero(remaining, nFrames, bins, i, i + 1, bin);
                    i--;
                }
                int start = i + 1 + k;

                if (start < 0) start = 0;
                if (end >= nFrames) end = nFrames;
                if (end - start <= minNoteLen) continue;

                events.Add(new NoteEvent
                {
                    StartFrame = start,
                    EndFrame = end,
                    Pitch = bin + MidiOffset,
                    Amplitude = Mean(frames, bins, start, end, bin),
                });
            }
        }

        return events;
    }

    /// <summary>清掉一个频点及其左右相邻频点在 [from, to) 帧上的能量。</summary>
    private static void Zero(float[] energy, int nFrames, int bins, int from, int to, int bin)
    {
        if (from < 0) from = 0;
        if (to > nFrames) to = nFrames;
        for (int f = from; f < to; f++)
        {
            int row = f * bins;
            energy[row + bin] = 0;
            if (bin < MaxFreqIdx) energy[row + bin + 1] = 0;
            if (bin > 0) energy[row + bin - 1] = 0;
        }
    }

    private static float Mean(float[] frames, int bins, int from, int to, int bin)
    {
        if (to <= from) return 0f;
        double sum = 0;
        for (int f = from; f < to; f++) sum += frames[f * bins + bin];
        return (float)(sum / (to - from));
    }

    /// <summary>上游 get_infered_onsets：帧能量的下降沿也算起音，幅度对齐到起音矩阵的最大值。</summary>
    private static float[] InferOnsets(float[] onsets, float[] frames, int nFrames, int bins, int nDiff)
    {
        var diff = new float[frames.Length];
        for (int n = 1; n <= nDiff; n++)
        {
            for (int f = 0; f < nFrames; f++)
            {
                int cur = f * bins;
                int prevRow = (f - n) * bins;
                for (int b = 0; b < bins; b++)
                {
                    float v = frames[cur + b] - (f - n >= 0 ? frames[prevRow + b] : 0f);
                    if (n == 1 || v < diff[cur + b]) diff[cur + b] = v;
                }
            }
        }

        float maxOnset = 0f;
        for (int i = 0; i < onsets.Length; i++) if (onsets[i] > maxOnset) maxOnset = onsets[i];

        // 负数清零、再把最前面 nDiff 帧清零，然后才取最大值（顺序不能换：最大值参与缩放）
        for (int i = 0; i < diff.Length; i++) if (diff[i] < 0) diff[i] = 0;
        for (int f = 0; f < nDiff && f < nFrames; f++)
            for (int b = 0; b < bins; b++) diff[f * bins + b] = 0;

        float maxDiff = 0f;
        for (int i = 0; i < diff.Length; i++) if (diff[i] > maxDiff) maxDiff = diff[i];

        var result = new float[onsets.Length];
        float scale = maxDiff > 0 ? maxOnset / maxDiff : 0f;
        for (int i = 0; i < result.Length; i++)
        {
            float scaled = diff[i] * scale;
            result[i] = onsets[i] > scaled ? onsets[i] : scaled;
        }
        return result;
    }
}

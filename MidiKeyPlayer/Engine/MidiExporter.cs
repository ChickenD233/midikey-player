using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using MidiKeyPlayer.Midi;

namespace MidiKeyPlayer.Engine;

/// <summary>把编辑后的旋律写成标准 MIDI（格式 0，120 BPM，480 PPQ）。</summary>
public static class MidiExporter
{
    private const short Ppq = 480;
    private const int MicrosecondsPerQuarter = 500_000;   // 120 BPM

    /// <summary>秒 → tick。120 BPM 下 1 秒 = 2 拍 = 960 tick，所以绝对时值与源曲一致。</summary>
    private static long Tick(double seconds) => (long)Math.Round(Math.Max(0, seconds) * 2.0 * Ppq);

    public static void Write(string path, IReadOnlyList<RawNote> notes, string trackName = "MidiKeyPlayer")
    {
        WriteMulti(path, new[] { (trackName, notes) });
    }

    /// <summary>
    /// 写多音轨 MIDI（格式 1）。每条轨一个名字（写进 SequenceTrackName，播放器与编辑器都能读到），
    /// 用于「人声 / 伴奏」这类分离结果 —— 载入后左侧列表会按轨名显示两行。
    /// </summary>
    public static void WriteMulti(string path, IReadOnlyList<(string Name, IReadOnlyList<RawNote> Notes)> tracks)
    {
        var chunks = new List<TrackChunk>();
        bool tempoWritten = false;
        foreach (var (name, notes) in tracks)
        {
            var chunk = new TrackChunk();
            chunk.Events.Add(new SequenceTrackNameEvent(name.Length > 0 ? name : "MidiKeyPlayer"));
            if (!tempoWritten)
            {
                // 速度事件只写一次：多轨都写会让部分播放器按最后一个解释
                chunk.Events.Add(new SetTempoEvent(MicrosecondsPerQuarter));
                tempoWritten = true;
            }

            var events = new List<(long At, MidiEvent Event)>();
            foreach (var n in notes)
            {
                int pitch = Math.Clamp(n.Pitch, 0, 127);
                int velocity = Math.Clamp(n.Velocity > 0 ? n.Velocity : 90, 1, 127);
                long on = Tick(n.Start);
                long off = Math.Max(on + 1, Tick(n.End));   // 至少 1 tick，否则是零长度音
                events.Add((on, new NoteOnEvent((SevenBitNumber)pitch, (SevenBitNumber)velocity)));
                events.Add((off, new NoteOffEvent((SevenBitNumber)pitch, (SevenBitNumber)0)));
            }
            // 同刻先关后开：同音重叠时不会把前一个音提前掐掉
            events.Sort((a, b) => a.At != b.At
                ? a.At.CompareTo(b.At)
                : (a.Event is NoteOffEvent ? 0 : 1).CompareTo(b.Event is NoteOffEvent ? 0 : 1));

            long last = 0;
            foreach (var (at, ev) in events)
            {
                ev.DeltaTime = at - last;
                last = at;
                chunk.Events.Add(ev);
            }
            chunks.Add(chunk);
        }
        if (chunks.Count == 0)
        {
            var empty = new TrackChunk();
            empty.Events.Add(new SequenceTrackNameEvent("MidiKeyPlayer"));
            empty.Events.Add(new SetTempoEvent(MicrosecondsPerQuarter));
            chunks.Add(empty);
        }

        // 必须显式指定每四分音符 tick 数：MidiFile 的默认分割不是 480，
        // 不指定会让读回的时间整体差一个固定倍数。
        // 文本编码也要显式给 UTF-8：默认写不出去非 ASCII 的轨名（「人声」会变成 ??），
        // 而本程序读回时优先按 UTF-8 解。
        var file = new MidiFile(chunks)
        {
            TimeDivision = new TicksPerQuarterNoteTimeDivision(Ppq),
        };
        file.Write(path, overwriteFile: true, settings: new WritingSettings
        {
            TextEncoding = new System.Text.UTF8Encoding(false),
        });
    }
}

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
        var events = new List<(long At, MidiEvent Event)>();
        foreach (var n in notes)
        {
            int pitch = Math.Clamp(n.Pitch, 0, 127);
            // 用力度的调用方（音频转写）会带上真实力度；没带就沿用旧的固定值
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

        var chunk = new TrackChunk();
        chunk.Events.Add(new SequenceTrackNameEvent(trackName));
        chunk.Events.Add(new SetTempoEvent(MicrosecondsPerQuarter));

        long last = 0;
        foreach (var (at, ev) in events)
        {
            ev.DeltaTime = at - last;
            last = at;
            chunk.Events.Add(ev);
        }
        // EndOfTrack 由 DryWetMidi 的事件集合自动维护，手动加会抛 ArgumentException

        // 必须显式指定每四分音符 tick 数：MidiFile 的默认分割不是 480，
        // 不指定会让读回的时间整体差一个固定倍数。
        var file = new MidiFile(chunk)
        {
            TimeDivision = new TicksPerQuarterNoteTimeDivision(Ppq)
        };
        file.Write(path, overwriteFile: true);   // 覆盖确认由保存对话框负责
    }
}

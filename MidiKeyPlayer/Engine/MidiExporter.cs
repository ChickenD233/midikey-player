using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using MidiKeyPlayer.Midi;

namespace MidiKeyPlayer.Engine;

public static class MidiExporter
{
    private const short Ppq = 480;
    private const int MicrosecondsPerQuarter = 500_000;   

    private static long Tick(double seconds) => (long)Math.Round(Math.Max(0, seconds) * 2.0 * Ppq);

    public static void Write(string path, IReadOnlyList<RawNote> notes, string trackName = "MidiKeyPlayer")
    {
        WriteMulti(path, new[] { (trackName, notes) });
    }

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
                chunk.Events.Add(new SetTempoEvent(MicrosecondsPerQuarter));
                tempoWritten = true;
            }

            var events = new List<(long At, MidiEvent Event)>();
            foreach (var n in notes)
            {
                int pitch = Math.Clamp(n.Pitch, 0, 127);
                int velocity = Math.Clamp(n.Velocity > 0 ? n.Velocity : 90, 1, 127);
                long on = Tick(n.Start);
                long off = Math.Max(on + 1, Tick(n.End));   
                events.Add((on, new NoteOnEvent((SevenBitNumber)pitch, (SevenBitNumber)velocity)));
                events.Add((off, new NoteOffEvent((SevenBitNumber)pitch, (SevenBitNumber)0)));
            }
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

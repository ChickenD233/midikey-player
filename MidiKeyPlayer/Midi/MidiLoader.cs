using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.Common;

namespace MidiKeyPlayer.Midi;

public static class MidiLoader
{
    static MidiLoader()
    {
        try { System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); }
        catch { }
    }

    public static ParsedMidi Parse(string path)
    {
        byte[] data = File.ReadAllBytes(path);

        if (data.Length == 0)
            throw new InvalidDataException("文件是 0 字节：多半是下载失败或网盘“占位文件”尚未同步完成。");

        string sig = System.Text.Encoding.ASCII.GetString(data, 0, Math.Min(4, data.Length));
        if (sig == "RIFF")
        {
            int p = IndexOf(data, new byte[] { (byte)'M', (byte)'T', (byte)'h', (byte)'d' }, 12);
            if (p < 0)
                throw new InvalidDataException("是 RIFF(.rmi) 格式但内部找不到 MIDI 数据，文件可能损坏。");
            data = data[p..];
            sig = "MThd";
        }
        if (sig != "MThd")
            throw new InvalidDataException($"不是标准 MIDI 文件：开头是“{sig}”。可能文件已损坏、被改名，或根本不是 MIDI（如其实是其它格式）。");

        using var stream = new MemoryStream(data);
        var settings = new ReadingSettings
        {
            TextEncoding = System.Text.Encoding.UTF8,
            DecodeTextCallback = DecodeTextSmart,
            NotEnoughBytesPolicy = NotEnoughBytesPolicy.Ignore,
            InvalidMetaEventParameterValuePolicy =
                Melanchall.DryWetMidi.Core.InvalidMetaEventParameterValuePolicy.SnapToLimits,
            InvalidChannelEventParameterValuePolicy =
                Melanchall.DryWetMidi.Core.InvalidChannelEventParameterValuePolicy.SnapToLimits,
            InvalidSystemCommonEventParameterValuePolicy =
                Melanchall.DryWetMidi.Core.InvalidSystemCommonEventParameterValuePolicy.SnapToLimits
        };
        MidiFile file;
        try
        {
            file = MidiFile.Read(stream, settings);
        }
        catch (Exception ex) when (ex is Melanchall.DryWetMidi.Core.NotEnoughBytesException
                                || ex is Melanchall.DryWetMidi.Core.InvalidChunkSizeException)
        {
            byte[]? trimmed = TryTrimToCompleteChunks(data);
            if (trimmed == null)
                throw new InvalidDataException(
                    "文件不是完整可用的 MIDI（数据损坏或被截断）。常见原因：" +
                    "网站“免积分/试听”给的是残缺或非 MIDI 内容，请重新完整下载。");
            try
            {
                using var ms2 = new MemoryStream(trimmed);
                file = MidiFile.Read(ms2, settings);
            }
            catch
            {
                throw new InvalidDataException(
                    "文件不是完整可用的 MIDI（数据损坏或被截断）。常见原因：" +
                    "网站“免积分/试听”给的是残缺或非 MIDI 内容，请重新完整下载。");
            }
        }
        var tempoMap = file.GetTempoMap();

        double secPerBeat = 0.5;
        int beatsPerBar = 4;
        try
        {
            var tempoChange = tempoMap.GetTempoChanges().FirstOrDefault();
            if (tempoChange != null && tempoChange.Value.BeatsPerMinute > 1)
                secPerBeat = 60.0 / tempoChange.Value.BeatsPerMinute;
            var tsChange = tempoMap.GetTimeSignatureChanges().FirstOrDefault();
            if (tsChange != null && tsChange.Value.Numerator >= 1)
                beatsPerBar = tsChange.Value.Numerator;
        }
        catch { }

        string divisionLabel;
        switch (file.TimeDivision)
        {
            case TicksPerQuarterNoteTimeDivision td:
                divisionLabel = $"{td.TicksPerQuarterNote} 刻/四分音符";
                break;
            case SmpteTimeDivision sd:
                divisionLabel = $"SMPTE {sd.Format} {sd.Resolution}";
                break;
            default:
                divisionLabel = "未知";
                break;
        }

        var candidates = new List<MidiCandidate>();
        int trackIdx = 0;
        double fileEndSec = 0;

        foreach (var chunk in file.GetTrackChunks())
        {
            string trackName = chunk.Events.OfType<SequenceTrackNameEvent>()
                                      .FirstOrDefault()?.Text ?? "";

            var programs = ReadPrograms(chunk);

            var allNotes = chunk.GetNotes().ToList();

            foreach (var grp in allNotes.GroupBy(n => (int)n.Channel))
            {
                var notes = new List<RawNote>();
                foreach (var n in grp)
                {
                    double s = TicksToSeconds(tempoMap, n.Time);
                    double e = TicksToSeconds(tempoMap, n.EndTime);
                    if (e - s < 0.02) e = s + 0.02; 

                    notes.Add(new RawNote
                    {
                        Pitch = n.NoteNumber,
                        Start = s,
                        End = e,
                        Velocity = n.Velocity,
                        Channel = grp.Key
                    });
                    if (e > fileEndSec) fileEndSec = e;
                }

                if (notes.Count == 0) continue;

                int program = programs.TryGetValue(grp.Key, out int p) ? p : -1;
                TrackRole role;
                bool guessed;
                if (grp.Key == 9)
                {
                    role = TrackRole.Drums;
                    guessed = false;
                }
                else if (program >= 0)
                {
                    role = GmInstrument.RoleOfProgram(program);
                    guessed = false;
                }
                else
                {
                    role = GmInstrument.Infer(grp.Key, trackName, notes);
                    bool nameSaysDrums = trackName.Contains("鼓") || trackName.Contains("打击")
                                         || trackName.ToLowerInvariant().Contains("drum")
                                         || trackName.ToLowerInvariant().Contains("perc");
                    guessed = !(role == TrackRole.Drums && nameSaysDrums);
                }

                var cand = new MidiCandidate
                {
                    TrackIndex = trackIdx,
                    Channel = grp.Key,
                    TrackName = trackName,
                    Program = program,
                    Role = role,
                    RoleGuessed = guessed,
                    Name = BuildCandidateName(trackName, grp.Key, program, role),
                    Notes = notes,
                    DurationSec = notes.Max(x => x.End) - notes.Min(x => x.Start)
                };
                candidates.Add(cand);
            }

            trackIdx++;
        }

        return new ParsedMidi
        {
            FilePath = path,
            DivisionLabel = divisionLabel,
            DurationSec = fileEndSec,
            SecondsPerBeat = secPerBeat,
            BeatsPerBar = beatsPerBar,
            Candidates = candidates
        };
    }

    private static string DecodeTextSmart(byte[] raw, ReadingSettings settings)
    {
        if (raw == null || raw.Length == 0) return "";
        try
        {
            var utf8 = new System.Text.UTF8Encoding(false, true);
            string s = utf8.GetString(raw);
            if (!s.Contains('\uFFFD')) return s;
        }
        catch { }
        try
        {
            return System.Text.Encoding.GetEncoding(936).GetString(raw);
        }
        catch { }
        return System.Text.Encoding.Latin1.GetString(raw);
    }

    private static Dictionary<int, int> ReadPrograms(TrackChunk chunk)
    {
        var map = new Dictionary<int, int>();
        foreach (var ev in chunk.Events)
        {
            if (ev is ProgramChangeEvent pc)
                map[(int)pc.Channel] = (int)pc.ProgramNumber;
        }
        return map;
    }

    private static string BuildCandidateName(string trackName, int channel, int program, TrackRole role)
    {
        string trimmed = (trackName ?? "").Trim();
        if (!GmInstrument.IsGenericTrackName(trimmed)) return trimmed;
        if (role != TrackRole.Unknown) return GmInstrument.TagOf(role);
        string inst = GmInstrument.ProgramName(program);
        if (inst.Length > 0) return inst;
        return channel == 9 ? "打击乐" : $"声道 {channel + 1}";
    }

    private static double TicksToSeconds(TempoMap tempoMap, long ticks)
    {
        var metric = TimeConverter.ConvertTo<MetricTimeSpan>(ticks, tempoMap);
        return metric.TotalSeconds;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        for (int i = start; i <= haystack.Length - needle.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { ok = false; break; }
            }
            if (ok) return i;
        }
        return -1;
    }

    private static byte[]? TryTrimToCompleteChunks(byte[] data)
    {
        try
        {
            if (data.Length < 14) return null;
            int headerLen = BE32(data, 4);
            if (headerLen < 6) return null;
            int headerTotal = 8 + headerLen;
            if (headerTotal > data.Length) return null;

            var chunks = new List<byte[]>();
            int i = headerTotal;
            while (i + 8 <= data.Length)
            {
                if (data[i] != (byte)'M' || data[i + 1] != (byte)'T' ||
                    data[i + 2] != (byte)'r' || data[i + 3] != (byte)'k') break;
                long size = BE32(data, i + 4);
                long end = (long)i + 8 + size;
                if (end > data.Length) break;          
                chunks.Add(data[i..(int)end]);
                i = (int)end;
            }
            if (chunks.Count == 0) return null;

            byte[] head = data[..headerTotal];
            head[10] = (byte)(chunks.Count >> 8);      
            head[11] = (byte)(chunks.Count & 0xFF);

            using var ms = new MemoryStream();
            ms.Write(head, 0, head.Length);
            foreach (var c in chunks) ms.Write(c, 0, c.Length);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static int BE32(byte[] b, int o) =>
        (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
}

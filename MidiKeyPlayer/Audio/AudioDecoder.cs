using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MidiKeyPlayer.Audio;

/// <summary>
/// 音频解码：WAV 自己解，其它格式（MP3 / M4A / WMA / FLAC 等）走 Windows 自带的
/// Media Foundation。不引第三方解码库，也不往包里塞 ffmpeg。
/// </summary>
internal static class AudioDecoder
{
    /// <summary>解码成 22.05 kHz 单声道浮点采样。失败抛异常，消息可直接给用户看。</summary>
    public static float[] DecodeToModelRate(string path)
    {
        var (samples, rate, channels) = Decode(path);
        var mono = ToMono(samples, channels);
        return Resampler.Resample(mono, rate, BasicPitch.SampleRate);
    }

    /// <summary>解码成原始交错浮点采样。返回 (采样, 采样率, 声道数)。</summary>
    public static (float[] Samples, int Rate, int Channels) Decode(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("音频文件不存在", path);

        // 扩展名是 .wav 就先自己解：不初始化 Media Foundation，启动更快，也不受系统解码器影响
        if (path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            if (TryReadWav(path, out var wav)) return wav;
        }

        if (MediaFoundationDecoder.TryDecode(path, out var samples, out int rate, out int channels))
            return (samples, rate, channels);

        if (TryReadWav(path, out var wav2)) return wav2;
        var reasons = new List<string>();
        if (MediaFoundationDecoder.LastError.Length > 0) reasons.Add("系统解码器：" + MediaFoundationDecoder.LastError);
        if (LastWavError.Length > 0) reasons.Add("WAV 直解：" + LastWavError);
        string reason = reasons.Count > 0 ? "（" + string.Join("；", reasons) + "）" : "";
        throw new NotSupportedException("系统解不开这个音频文件（格式不支持，或文件损坏）" + reason);
    }

    /// <summary>多声道取平均变单声道（与 librosa 的 mono=True 一致）。</summary>
    public static float[] ToMono(float[] interleaved, int channels)
    {
        if (channels <= 1) return interleaved;
        int frames = interleaved.Length / channels;
        var mono = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            float sum = 0;
            int baseIdx = i * channels;
            for (int c = 0; c < channels; c++) sum += interleaved[baseIdx + c];
            mono[i] = sum / channels;
        }
        return mono;
    }

    // ===================== WAV =====================

    /// <summary>WAV 直解失败的原因（诊断用，界面也会把它写进日志）。</summary>
    public static string LastWavError { get; private set; } = "";

    /// <summary>WAV 解析：PCM 8/16/24/32 位与 IEEE float 32/64 位，任意采样率与声道数。</summary>
    public static bool TryReadWav(string path, out (float[] Samples, int Rate, int Channels) result)
    {
        result = (Array.Empty<float>(), 0, 0);
        LastWavError = "";
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            if (br.ReadUInt32() != 0x46464952) return false;      // "RIFF"
            br.ReadUInt32();                                       // 文件长度
            if (br.ReadUInt32() != 0x45564157) return false;       // "WAVE"

            int format = 0, channels = 0, rate = 0, bits = 0;
            float[]? samples = null;

            while (fs.Position + 8 <= fs.Length)
            {
                uint id = br.ReadUInt32();
                int size = br.ReadInt32();
                long chunkStart = fs.Position;                 // 数据起点（读完 id 与长度之后）
                long next = chunkStart + size + (size & 1);     // 块长度补齐到偶数

                if (id == 0x20746D66)                                  // "fmt "
                {
                    format = br.ReadUInt16();
                    channels = br.ReadUInt16();
                    rate = br.ReadInt32();
                    br.ReadInt32();                                    // byte rate
                    br.ReadUInt16();                                   // block align
                    bits = br.ReadUInt16();
                    if (format == 0xFFFE && size >= 40)                // WAVE_FORMAT_EXTENSIBLE
                    {
                        br.ReadUInt16();                               // cbSize
                        br.ReadUInt16();                               // 有效位数
                        br.ReadUInt32();                               // 声道掩码
                        format = br.ReadUInt16();                      // 子格式前两字节：1=PCM，3=float
                    }
                }
                else if (id == 0x61746164)                             // "data"
                {
                    int available = (int)Math.Min(size, fs.Length - fs.Position);
                    var raw = br.ReadBytes(available);
                    samples = ConvertPcm(raw, format, bits, channels);
                }

                // 长度不合法就停：不能往回跳，也不能越过文件末尾
                if (next < chunkStart || next > fs.Length) break;
                fs.Position = next;
            }

            if (samples == null || channels <= 0 || rate <= 0)
            {
                LastWavError = $"头部不完整（格式 {format}、位深 {bits}、声道 {channels}、采样率 {rate}）";
                return false;
            }
            result = (samples, rate, channels);
            return true;
        }
        catch (Exception ex)
        {
            LastWavError = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static float[]? ConvertPcm(byte[] raw, int format, int bits, int channels)
    {
        if (channels <= 0) return null;
        if (format == 3)   // IEEE float
        {
            int width = bits / 8;
            if (width != 4 && width != 8) return null;
            int count = raw.Length / width;
            var f = new float[count];
            for (int i = 0; i < count; i++)
                f[i] = width == 4 ? BitConverter.ToSingle(raw, i * 4) : (float)BitConverter.ToDouble(raw, i * 8);
            return f;
        }
        if (format != 1) return null;   // 只认 PCM

        switch (bits)
        {
            case 8:
            {
                var f = new float[raw.Length];
                for (int i = 0; i < raw.Length; i++) f[i] = (raw[i] - 128) / 128f;
                return f;
            }
            case 16:
            {
                int count = raw.Length / 2;
                var f = new float[count];
                for (int i = 0; i < count; i++) f[i] = BitConverter.ToInt16(raw, i * 2) / 32768f;
                return f;
            }
            case 24:
            {
                int count = raw.Length / 3;
                var f = new float[count];
                for (int i = 0; i < count; i++)
                {
                    int v = raw[i * 3] | (raw[i * 3 + 1] << 8) | (raw[i * 3 + 2] << 16);
                    if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                    f[i] = v / 8388608f;
                }
                return f;
            }
            case 32:
            {
                int count = raw.Length / 4;
                var f = new float[count];
                for (int i = 0; i < count; i++) f[i] = BitConverter.ToInt32(raw, i * 4) / 2147483648f;
                return f;
            }
            default:
                return null;
        }
    }
}

/// <summary>
/// Media Foundation 的 Source Reader：让系统自带解码器把任意音频解成 PCM。
/// 只声明用得到的方法，vtable 顺序必须与头文件一致（不能删前面的占位方法）。
/// </summary>
internal static class MediaFoundationDecoder
{
    private const int MF_VERSION = 0x00020070;
    private const uint MF_SOURCE_READER_FIRST_AUDIO_STREAM = 0xFFFFFFFD;
    private const uint MF_SOURCE_READERF_ENDOFSTREAM = 0x2;
    private const uint MF_SOURCE_READERF_ERROR = 0x4;

    /// <summary>Media Foundation 解码失败的原因（诊断与日志用）。</summary>
    public static string LastError { get; private set; } = "";

    private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    private static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFAudioFormat_PCM = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFAudioFormat_Float = new("00000003-0000-0010-8000-00aa00389b71");

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(int version, int flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFShutdown();

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MFCreateSourceReaderFromURL(string url, IMFAttributes? attributes,
        out IMFSourceReader reader);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMediaType(out IMFMediaType type);

    public static bool TryDecode(string path, out float[] samples, out int rate, out int channels)
    {
        samples = Array.Empty<float>();
        rate = 0;
        channels = 0;
        LastError = "";
        bool started = false;
        try
        {
            int hr = MFStartup(MF_VERSION, 0);
            started = hr >= 0;
            if (hr < 0) { LastError = $"MFStartup 0x{hr:X8}"; return false; }

            hr = MFCreateSourceReaderFromURL(path, null, out var reader);
            if (hr < 0) { LastError = $"MFCreateSourceReaderFromURL 0x{hr:X8}"; return false; }

            // 只要 PCM，采样率与声道数听系统的：源读取器会插入系统自带解码器，
            // 重采样与混音由本工程的 Resampler 做（那条路已在自检里覆盖）。
            var target = CreatePcmType(null, null);
            hr = reader.SetCurrentMediaType(MF_SOURCE_READER_FIRST_AUDIO_STREAM, IntPtr.Zero, target);
            if (hr < 0) { LastError = $"SetCurrentMediaType(PCM) 0x{hr:X8}"; return false; }

            hr = reader.GetCurrentMediaType(MF_SOURCE_READER_FIRST_AUDIO_STREAM, out var current);
            if (hr < 0) return false;
            current.GetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, out uint sr);
            current.GetUINT32(MF_MT_AUDIO_NUM_CHANNELS, out uint ch);
            current.GetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, out uint bits);
            current.GetGUID(MF_MT_SUBTYPE, out Guid subtype);
            if (bits == 0) bits = subtype == MFAudioFormat_Float ? 32u : 16u;
            if (sr == 0 || ch == 0) { LastError = "输出格式没有采样率或声道数"; return false; }

            var all = new List<float>();
            while (true)
            {
                hr = reader.ReadSample(MF_SOURCE_READER_FIRST_AUDIO_STREAM, 0,
                    out _, out uint flags, out _, out var sample);
                if (hr < 0 || (flags & MF_SOURCE_READERF_ERROR) != 0) break;
                if ((flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0) break;
                if (sample == null) continue;

                hr = sample.ConvertToContiguousBuffer(out var buffer);
                if (hr < 0) continue;
                hr = buffer.Lock(out IntPtr ptr, out _, out uint length);
                if (hr < 0) continue;
                try
                {
                    if (length > 0)
                    {
                        var raw = new byte[length];
                        Marshal.Copy(ptr, raw, 0, (int)length);
                        all.AddRange(ToFloat(raw, bits, subtype));
                    }
                }
                finally
                {
                    buffer.Unlock();
                    Marshal.ReleaseComObject(buffer);
                }
                Marshal.ReleaseComObject(sample);
            }

            if (all.Count == 0) { LastError = "解码器没有输出采样"; return false; }
            samples = all.ToArray();
            rate = (int)sr;
            channels = (int)ch;
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
        finally
        {
            if (started) { try { MFShutdown(); } catch { } }
        }
    }

    /// <summary>要 PCM 输出。给定采样率与声道数时让源读取器直接重采样/混音，给 null 就听系统的。</summary>
    private static IMFMediaType CreatePcmType(int? sampleRate, int? channels)
    {
        MFCreateMediaType(out var type);
        type.SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
        type.SetGUID(MF_MT_SUBTYPE, MFAudioFormat_PCM);
        if (sampleRate.HasValue) type.SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)sampleRate.Value);
        if (channels.HasValue) type.SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, (uint)channels.Value);
        return type;
    }

    private static float[] ToFloat(byte[] raw, uint bits, Guid subtype)
    {
        bool isFloat = subtype == MFAudioFormat_Float;
        if (isFloat && bits == 32)
        {
            var f = new float[raw.Length / 4];
            for (int i = 0; i < f.Length; i++) f[i] = BitConverter.ToSingle(raw, i * 4);
            return f;
        }
        switch (bits)
        {
            case 8:
            {
                var f = new float[raw.Length];
                for (int i = 0; i < raw.Length; i++) f[i] = (raw[i] - 128) / 128f;
                return f;
            }
            case 24:
            {
                var f = new float[raw.Length / 3];
                for (int i = 0; i < f.Length; i++)
                {
                    int v = raw[i * 3] | (raw[i * 3 + 1] << 8) | (raw[i * 3 + 2] << 16);
                    if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                    f[i] = v / 8388608f;
                }
                return f;
            }
            case 32:
            {
                var f = new float[raw.Length / 4];
                for (int i = 0; i < f.Length; i++) f[i] = BitConverter.ToInt32(raw, i * 4) / 2147483648f;
                return f;
            }
            default:
            {
                var f = new float[raw.Length / 2];
                for (int i = 0; i < f.Length; i++) f[i] = BitConverter.ToInt16(raw, i * 2) / 32768f;
                return f;
            }
        }
    }

    // ===================== COM 声明 =====================

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFAttributes
    {
        [PreserveSig] int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
        [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out int result);
        [PreserveSig] int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out uint length);
        [PreserveSig] int GetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, uint size, out uint length);
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
        [PreserveSig] int GetBlobSize(ref Guid key, out uint size);
        [PreserveSig] int GetBlob(ref Guid key, IntPtr buf, uint size, out uint blobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buf, out uint size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid riid, out IntPtr value);
        [PreserveSig] int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, uint value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(ref Guid key, byte[] buf, uint size);
        [PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IMFAttributes dest);
    }

    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaType
    {
        // IMFAttributes 的全部方法（顺序不能动）
        [PreserveSig] int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
        [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out int result);
        [PreserveSig] int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out uint length);
        [PreserveSig] int GetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, uint size, out uint length);
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
        [PreserveSig] int GetBlobSize(ref Guid key, out uint size);
        [PreserveSig] int GetBlob(ref Guid key, IntPtr buf, uint size, out uint blobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buf, out uint size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid riid, out IntPtr value);
        [PreserveSig] int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, uint value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(ref Guid key, byte[] buf, uint size);
        [PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IMFAttributes dest);
        // IMFMediaType
        [PreserveSig] int GetMajorType(out Guid majorType);
        [PreserveSig] int IsCompressedFormat(out int compressed);
        [PreserveSig] int IsEqual(IMFMediaType other, out uint flags);
        [PreserveSig] int GetRepresentation(ref Guid representation, out IntPtr value);
        [PreserveSig] int FreeRepresentation(ref Guid representation, IntPtr value);
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSample
    {
        [PreserveSig] int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
        [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out int result);
        [PreserveSig] int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out uint length);
        [PreserveSig] int GetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, uint size, out uint length);
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
        [PreserveSig] int GetBlobSize(ref Guid key, out uint size);
        [PreserveSig] int GetBlob(ref Guid key, IntPtr buf, uint size, out uint blobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buf, out uint size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid riid, out IntPtr value);
        [PreserveSig] int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, uint value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(ref Guid key, byte[] buf, uint size);
        [PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IMFAttributes dest);
        // IMFSample
        [PreserveSig] int GetSampleFlags(out uint flags);
        [PreserveSig] int SetSampleFlags(uint flags);
        [PreserveSig] int GetSampleTime(out long time);
        [PreserveSig] int SetSampleTime(long time);
        [PreserveSig] int GetSampleDuration(out long duration);
        [PreserveSig] int SetSampleDuration(long duration);
        [PreserveSig] int GetBufferCount(out uint count);
        [PreserveSig] int GetBufferByIndex(uint index, out IMFMediaBuffer buffer);
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
        [PreserveSig] int RemoveBufferByIndex(uint index);
        [PreserveSig] int RemoveAllBuffers();
        [PreserveSig] int GetTotalLength(out uint length);
        [PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
    }

    [ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out uint length);
        [PreserveSig] int SetCurrentLength(uint length);
        [PreserveSig] int GetMaxLength(out uint maxLength);
    }

    [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSourceReader
    {
        [PreserveSig] int GetStreamSelection(uint index, out int selected);
        [PreserveSig] int SetStreamSelection(uint index, int selected);
        [PreserveSig] int GetNativeMediaType(uint index, uint typeIndex, out IMFMediaType type);
        [PreserveSig] int GetCurrentMediaType(uint index, out IMFMediaType type);
        [PreserveSig] int SetCurrentMediaType(uint index, IntPtr reserved, IMFMediaType type);
        [PreserveSig] int SetCurrentPosition(ref Guid format, IntPtr position);
        [PreserveSig] int ReadSample(uint index, uint controlFlags, out uint actualIndex,
            out uint streamFlags, out long timestamp, out IMFSample sample);
        [PreserveSig] int Flush(uint index);
        [PreserveSig] int GetServiceForStream(uint index, ref Guid service, ref Guid riid, out IntPtr obj);
        [PreserveSig] int GetPresentationAttribute(uint index, ref Guid attribute, IntPtr value);
    }
}

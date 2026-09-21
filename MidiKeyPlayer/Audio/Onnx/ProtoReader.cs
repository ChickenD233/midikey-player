using System;
using System.Collections.Generic;
using System.Text;

namespace MidiKeyPlayer.Audio.Onnx;

/// <summary>
/// 极简 protobuf 读取器：只覆盖 ONNX 模型文件用到的线格式（varint / 定长 32 位 / 长度前缀）。
/// 不引第三方库：模型只有 230 KB，靠这一个读取器就能把图与权重全部读出来。
/// </summary>
internal sealed class ProtoReader
{
    private readonly byte[] _buf;
    private readonly int _end;
    private int _pos;

    public ProtoReader(byte[] buf) : this(buf, 0, buf.Length) { }

    public ProtoReader(byte[] buf, int start, int end)
    {
        _buf = buf;
        _pos = start;
        _end = end;
    }

    public bool Eof => _pos >= _end;

    /// <summary>底层字节（子读取器与父读取器共享同一份，用于原地引用权重）。</summary>
    public byte[] Buffer => _buf;

    /// <summary>读下一个字段的标签。返回 (字段号, 线格式)；读到结尾返回 (0, 0)。</summary>
    public (int Field, int Wire) ReadTag()
    {
        if (Eof) return (0, 0);
        ulong tag = ReadVarint();
        return ((int)(tag >> 3), (int)(tag & 7));
    }

    public ulong ReadVarint()
    {
        ulong result = 0;
        int shift = 0;
        while (_pos < _end)
        {
            byte b = _buf[_pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift > 63) throw new InvalidOperationException("protobuf varint 过长");
        }
        throw new InvalidOperationException("protobuf varint 越界");
    }

    public long ReadInt64() => unchecked((long)ReadVarint());
    public int ReadInt32() => unchecked((int)ReadVarint());

    public float ReadFloat()
    {
        Ensure(4);
        float v = BitConverter.ToSingle(_buf, _pos);
        _pos += 4;
        return v;
    }

    /// <summary>读长度前缀的字节段（不复制）。返回 (起点, 长度)。</summary>
    public (int Start, int Length) ReadSpan()
    {
        int len = checked((int)ReadVarint());
        Ensure(len);
        int start = _pos;
        _pos += len;
        return (start, len);
    }

    public string ReadString()
    {
        var (start, len) = ReadSpan();
        return Encoding.UTF8.GetString(_buf, start, len);
    }

    /// <summary>读一段字节并复制。</summary>
    public byte[] ReadBytes()
    {
        var (start, len) = ReadSpan();
        var copy = new byte[len];
        Array.Copy(_buf, start, copy, 0, len);
        return copy;
    }

    /// <summary>在读出的字节段上开一个子读取器（用于嵌套消息）。</summary>
    public ProtoReader SubMessage()
    {
        var (start, len) = ReadSpan();
        return new ProtoReader(_buf, start, start + len);
    }

    /// <summary>跳过未知字段。</summary>
    public void SkipField(int wire)
    {
        switch (wire)
        {
            case 0: ReadVarint(); break;
            case 1: Ensure(8); _pos += 8; break;
            case 2: ReadSpan(); break;
            case 5: Ensure(4); _pos += 4; break;
            default: throw new InvalidOperationException($"不支持的 protobuf 线格式 {wire}");
        }
    }

    private void Ensure(int n)
    {
        if (_pos + n > _end) throw new InvalidOperationException("protobuf 读取越界");
    }
}

/// <summary>ONNX TensorProto 的数据类型编号（只列用得到的）。</summary>
internal enum OnnxDataType
{
    Float = 1,
    Int32 = 6,
    Int64 = 7,
    Bool = 9,
}

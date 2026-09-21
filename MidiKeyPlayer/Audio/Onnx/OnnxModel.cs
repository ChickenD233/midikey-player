using System;
using System.Collections.Generic;

namespace MidiKeyPlayer.Audio.Onnx;

/// <summary>图里的一个算子节点。</summary>
internal sealed class OnnxNode
{
    public string OpType = "";
    public string[] Inputs = Array.Empty<string>();
    public string[] Outputs = Array.Empty<string>();
    public Dictionary<string, object> Attributes = new(StringComparer.Ordinal);

    public long Int(string name, long fallback = 0)
        => Attributes.TryGetValue(name, out var v) && v is long l ? l : fallback;

    public long[] Ints(string name)
        => Attributes.TryGetValue(name, out var v) && v is long[] a ? a : Array.Empty<long>();

    public string Str(string name, string fallback = "")
        => Attributes.TryGetValue(name, out var v) && v is string s ? s : fallback;

    /// <summary>
    /// 取一个浮点属性。属性值可能是 float、double 或整型包装（不同导出器不一样），一律认。
    /// 缺属性或类型不认识时给 fallback。
    /// </summary>
    public float Float(string name, float fallback = 0f)
    {
        if (!Attributes.TryGetValue(name, out var v) || v == null) return fallback;
        return v switch
        {
            float f => f,
            double d => (float)d,
            long l => l,
            int i => i,
            long[] la when la.Length > 0 => la[0],
            float[] fa when fa.Length > 0 => fa[0],
            _ => fallback,
        };
    }

    public bool Has(string name) => Attributes.ContainsKey(name);

    /// <summary>取 TensorProto 属性（Constant / ConstantOfShape 的 value）。没有就返回 null。</summary>
    public OnnxTensor? Tensor(string name)
        => Attributes.TryGetValue(name, out var v) && v is OnnxTensor t ? t : null;
}

/// <summary>
/// 只读的 ONNX 模型：图结构 + 权重。权重直接指向模型文件的字节（raw_data），不复制。
/// </summary>
internal sealed class OnnxModel
{
    public readonly List<OnnxNode> Nodes = new();
    public readonly Dictionary<string, OnnxTensor> Initializers = new(StringComparer.Ordinal);
    public string InputName = "";
    public string[] OutputNames = Array.Empty<string>();
    /// <summary>图的输入形状（有名字的输入都记一条：名字 → 各维）。动态维记 0。</summary>
    public readonly Dictionary<string, int[]> InputShapes = new(StringComparer.Ordinal);
    /// <summary>图的输出形状，顺序与 <see cref="OutputNames"/> 一致。</summary>
    public readonly List<int[]> OutputShapes = new();
    /// <summary>ONNX 元数据（ModelProto.metadata_props）：模型自报的采样率、STFT 参数等。</summary>
    public readonly Dictionary<string, string> Metadata = new(StringComparer.Ordinal);

    /// <summary>取一条元数据；没有就返回 fallback。</summary>
    public string Meta(string key, string fallback = "")
        => Metadata.TryGetValue(key, out var v) ? v : fallback;

    /// <summary>取一条整数元数据。</summary>
    public int MetaInt(string key, int fallback = 0)
        => Metadata.TryGetValue(key, out var v) && int.TryParse(v, out int n) ? n : fallback;

    /// <summary>从 ONNX 字节解析模型。</summary>
    public static OnnxModel Parse(byte[] bytes)
    {
        var model = new OnnxModel();
        model._bytes = bytes;
        var r = new ProtoReader(bytes);
        while (!r.Eof)
        {
            var (field, wire) = r.ReadTag();
            if (field == 7 && wire == 2)   // ModelProto.graph
                model.ReadGraph(r.SubMessage(), bytes);
            else if (field == 14 && wire == 2)   // ModelProto.metadata_props
                model.ReadMetadata(r.SubMessage());
            else
                r.SkipField(wire);
        }
        return model;
    }

    /// <summary>整份模型字节。算子属性里的 TensorProto（Constant 的 value）要按偏移切它。</summary>
    private byte[] _bytes = Array.Empty<byte>();

    /// <summary>
    /// 读 ModelProto.metadata_props 里的一条 StringStringEntryProto：
    /// key（field 1）、value（field 2）。
    /// </summary>
    private void ReadMetadata(ProtoReader m)
    {
        string key = "", value = "";
        while (!m.Eof)
        {
            var (field, wire) = m.ReadTag();
            if (field == 1 && wire == 2) key = m.ReadString();
            else if (field == 2 && wire == 2) value = m.ReadString();
            else m.SkipField(wire);
        }
        if (key.Length > 0) Metadata[key] = value;
    }

    private void ReadGraph(ProtoReader g, byte[] bytes)
    {
        var inputs = new List<string>();
        var outputs = new List<string>();
        while (!g.Eof)
        {
            var (field, wire) = g.ReadTag();
            switch (field)
            {
                case 1 when wire == 2:   // node
                    Nodes.Add(ReadNode(g.SubMessage()));
                    break;
                case 5 when wire == 2:   // initializer
                {
                    var (name, tensor) = ReadTensorNamed(g.SubMessage(), bytes);
                    if (name.Length > 0) Initializers[name] = tensor;
                    break;
                }
                case 11 when wire == 2:  // graph input
                {
                    var (name, shape, _) = ReadValueInfo(g.SubMessage());
                    if (name.Length > 0) { inputs.Add(name); InputShapes[name] = shape; }
                    break;
                }
                case 12 when wire == 2:  // graph output
                {
                    var (name, shape, _) = ReadValueInfo(g.SubMessage());
                    if (name.Length > 0) { outputs.Add(name); OutputShapes.Add(shape); }
                    break;
                }
                default:
                    g.SkipField(wire);
                    break;
            }
        }
        // 有初始值的输入不算真输入（TF 转出来的图里第一项是真正的音频入口）
        foreach (var name in inputs)
        {
            if (!Initializers.ContainsKey(name)) { InputName = name; break; }
        }
        OutputNames = outputs.ToArray();
        if (InputName.Length == 0 && Nodes.Count > 0)
            InputName = Nodes[0].Inputs.Length > 0 ? Nodes[0].Inputs[0] : "";
    }

    private static string ReadValueInfoName(ProtoReader v) => ReadValueInfo(v).Name;

    /// <summary>
    /// 读 ValueInfoProto：名字（1）、类型（2）。类型里 shape（field 2）→ dim（field 1）→ dim_value（field 1）。
    /// 动态维（dim_param）记 0，调用方按需自己填。
    /// </summary>
    private static (string Name, int[] Shape, OnnxDataType Type) ReadValueInfo(ProtoReader v)
    {
        string name = "";
        int[] shape = Array.Empty<int>();
        OnnxDataType type = OnnxDataType.Float;
        while (!v.Eof)
        {
            var (field, wire) = v.ReadTag();
            if (field == 1 && wire == 2) name = v.ReadString();
            else if (field == 2 && wire == 2)
            {
                var t = v.SubMessage();
                while (!t.Eof)
                {
                    var (tf, tw) = t.ReadTag();
                    if (tf == 1 && tw == 0) type = (OnnxDataType)t.ReadInt32();   // elem_type
                    else if (tf == 2 && tw == 2) shape = ReadTensorShape(t.SubMessage());
                    else t.SkipField(tw);
                }
            }
            else v.SkipField(wire);
        }
        return (name, shape, type);
    }

    private static int[] ReadTensorShape(ProtoReader s)
    {
        var dims = new List<int>();
        while (!s.Eof)
        {
            var (field, wire) = s.ReadTag();
            if (field == 1 && wire == 2)
            {
                var d = s.SubMessage();
                int value = 0;
                while (!d.Eof)
                {
                    var (df, dw) = d.ReadTag();
                    if (df == 1 && dw == 0) value = (int)d.ReadInt64();   // dim_value
                    else d.SkipField(dw);                                  // dim_param = 动态维
                }
                dims.Add(value);
            }
            else s.SkipField(wire);
        }
        return dims.ToArray();
    }

    private static OnnxNode ReadNode(ProtoReader n)
    {
        var node = new OnnxNode();
        var ins = new List<string>();
        var outs = new List<string>();
        while (!n.Eof)
        {
            var (field, wire) = n.ReadTag();
            switch (field)
            {
                case 1 when wire == 2: ins.Add(n.ReadString()); break;
                case 2 when wire == 2: outs.Add(n.ReadString()); break;
                case 4 when wire == 2: node.OpType = n.ReadString(); break;
                case 5 when wire == 2: ReadAttribute(n.SubMessage(), node); break;
                default: n.SkipField(wire); break;
            }
        }
        node.Inputs = ins.ToArray();
        node.Outputs = outs.ToArray();
        return node;
    }

    private static void ReadAttribute(ProtoReader a, OnnxNode node)
    {
        string name = "";
        object? value = null;
        var floats = new List<float>();
        var ints = new List<long>();
        while (!a.Eof)
        {
            var (field, wire) = a.ReadTag();
            switch (field)
            {
                case 1 when wire == 2: name = a.ReadString(); break;
                case 2 when wire == 5: value = a.ReadFloat(); break;
                case 3 when wire == 0: value = a.ReadInt64(); break;
                case 4 when wire == 2: value = a.ReadString(); break;
                case 5 when wire == 2: value = ReadTensor(a.SubMessage(), a.Buffer); break;   // TensorProto
                case 7:  // repeated float（packed 或逐个）
                    if (wire == 2)
                    {
                        var (s, len) = a.ReadSpan();
                        var sub = new ProtoReader(a.Buffer, s, s + len);
                        while (!sub.Eof) floats.Add(sub.ReadFloat());
                    }
                    else floats.Add(a.ReadFloat());
                    break;
                case 8:  // repeated int64
                    if (wire == 2)
                    {
                        var (s, len) = a.ReadSpan();
                        var sub = new ProtoReader(a.Buffer, s, s + len);
                        while (!sub.Eof) ints.Add(sub.ReadInt64());
                    }
                    else ints.Add(a.ReadInt64());
                    break;
                default:
                    a.SkipField(wire);
                    break;
            }
        }
        if (ints.Count > 0) value = ints.ToArray();
        else if (floats.Count > 0) value = floats.ToArray();
        if (name.Length > 0 && value != null) node.Attributes[name] = value;
    }

    /// <summary>读 TensorProto；raw_data 直接切片引用，不复制。</summary>
    private static OnnxTensor ReadTensor(ProtoReader t, byte[] bytes) => ReadTensorNamed(t, bytes).Tensor;

    /// <summary>读 TensorProto，连名字一起给（初始值的键就是这个名字）。</summary>
    private static (string Name, OnnxTensor Tensor) ReadTensorNamed(ProtoReader t, byte[] bytes)
    {
        string name = "";
        var dims = new List<int>();
        var doubleValues = new List<double>();
        OnnxDataType dtype = OnnxDataType.Float;
        int rawStart = -1, rawLen = 0;
        while (!t.Eof)
        {
            var (field, wire) = t.ReadTag();
            switch (field)
            {
                case 1:
                    if (wire == 2)
                    {
                        var (s, len) = t.ReadSpan();
                        var sub = new ProtoReader(bytes, s, s + len);
                        while (!sub.Eof) dims.Add((int)sub.ReadInt64());
                    }
                    else dims.Add((int)t.ReadInt64());
                    break;
                case 2 when wire == 0: dtype = (OnnxDataType)t.ReadInt32(); break;
                case 4:   // float_data
                    if (wire == 2)
                    {
                        var (s, len) = t.ReadSpan();
                        var sub = new ProtoReader(bytes, s, s + len);
                        while (!sub.Eof) doubleValues.Add(sub.ReadFloat());
                    }
                    else doubleValues.Add(t.ReadFloat());
                    break;
                case 5:   // int32_data
                case 7:   // int64_data
                    if (wire == 2)
                    {
                        var (s, len) = t.ReadSpan();
                        var sub = new ProtoReader(bytes, s, s + len);
                        while (!sub.Eof) doubleValues.Add(sub.ReadInt64());
                    }
                    else doubleValues.Add(t.ReadInt64());
                    break;
                case 8 when wire == 2: name = t.ReadString(); break;
                case 9 when wire == 2:
                {
                    var (s, len) = t.ReadSpan();
                    rawStart = s; rawLen = len;
                    break;
                }
                default:
                    t.SkipField(wire);
                    break;
            }
        }

        int[] shape = dims.ToArray();
        OnnxTensor tensor = BuildTensor(dtype, shape, bytes, rawStart, rawLen, doubleValues);
        return (name, tensor);
    }

    private static OnnxTensor BuildTensor(OnnxDataType dtype, int[] shape, byte[] bytes,
        int rawStart, int rawLen, List<double> values)
    {
        // 标量张量（shape 为空）有 1 个元素；空维度（某一维是 0）才是 0 个
        int count = OnnxTensor.ElementCount(shape);
        if (count < 0) count = 0;
        bool half = dtype is OnnxDataType.Float16 or OnnxDataType.BFloat16;
        // 数据区长度决定实际元素数：标量张量（shape 为空）或元数据没写形状时，
        // 按数据长度建数组，否则会得到一个「有形状没数据」的张量（标量常量就是这样）
        if (rawLen > 0)
        {
            int width = dtype switch
            {
                OnnxDataType.Float => 4,
                OnnxDataType.Float16 or OnnxDataType.BFloat16 or OnnxDataType.Int16 or OnnxDataType.UInt16 => 2,
                OnnxDataType.Int32 or OnnxDataType.UInt32 => 4,
                OnnxDataType.Bool or OnnxDataType.Int8 or OnnxDataType.UInt8 => 1,
                _ => 8,
            };
            if (rawLen / width > count) count = rawLen / width;
        }
        if (values.Count > count) count = values.Count;

        if (rawLen > 0)
        {
            if (dtype == OnnxDataType.Float)
            {
                var f = new float[Math.Max(count, rawLen / 4)];
                Buffer.BlockCopy(bytes, rawStart, f, 0, Math.Min(f.Length * 4, rawLen));
                return new OnnxTensor(dtype, shape, f);
            }
            if (half)
            {
                // fp16 / bf16 权重一律解成 float32：本执行器只有 float32 与整数两种表示
                int n = Math.Max(count, rawLen / 2);
                var f = new float[n];
                for (int i = 0; i < n && rawStart + (i + 1) * 2 <= rawStart + rawLen; i++)
                {
                    ushort bits = (ushort)(bytes[rawStart + i * 2] | (bytes[rawStart + i * 2 + 1] << 8));
                    f[i] = dtype == OnnxDataType.Float16 ? FromHalf(bits) : FromBFloat16(bits);
                }
                return new OnnxTensor(OnnxDataType.Float, shape, f);
            }
            else
            {
                // int64 / int32：按元素宽度读成 long
                int width = dtype == OnnxDataType.Int32 || dtype == OnnxDataType.UInt32 ? 4 : 8;
                int n = Math.Max(count, rawLen / width);
                var l = new long[n];
                for (int i = 0; i < n && rawStart + (i + 1) * width <= rawStart + rawLen; i++)
                    l[i] = width == 4 ? BitConverter.ToInt32(bytes, rawStart + i * 4)
                                      : BitConverter.ToInt64(bytes, rawStart + i * 8);
                return new OnnxTensor(dtype, shape, l);
            }
        }

        if (dtype == OnnxDataType.Float || half)
        {
            var f = new float[count];
            for (int i = 0; i < f.Length && i < values.Count; i++) f[i] = (float)values[i];
            return new OnnxTensor(OnnxDataType.Float, shape, f);
        }
        else
        {
            var l = new long[count];
            for (int i = 0; i < l.Length && i < values.Count; i++) l[i] = (long)values[i];
            return new OnnxTensor(dtype, shape, l);
        }
    }

    /// <summary>IEEE 754 半精度（fp16）→ float32。含零、非规格化数、无穷与 NaN。</summary>
    private static float FromHalf(ushort h)
    {
        int sign = (h >> 15) & 1;
        int exp = (h >> 10) & 0x1F;
        int mant = h & 0x3FF;
        double value;
        if (exp == 0) value = mant * Math.Pow(2, -24);          // 0 与非规格化数
        else if (exp == 31) value = mant == 0 ? double.PositiveInfinity : double.NaN;
        else value = (1.0 + mant / 1024.0) * Math.Pow(2, exp - 15);
        return (float)(sign == 1 ? -value : value);
    }

    /// <summary>bfloat16 → float32（就是 float32 的高 16 位）。</summary>
    private static float FromBFloat16(ushort h)
        => BitConverter.Int32BitsToSingle(h << 16);
}

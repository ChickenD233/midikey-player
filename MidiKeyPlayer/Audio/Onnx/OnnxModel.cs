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

    public bool Has(string name) => Attributes.ContainsKey(name);
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

    /// <summary>从 ONNX 字节解析模型。</summary>
    public static OnnxModel Parse(byte[] bytes)
    {
        var model = new OnnxModel();
        var r = new ProtoReader(bytes);
        while (!r.Eof)
        {
            var (field, wire) = r.ReadTag();
            if (field == 7 && wire == 2)   // ModelProto.graph
                model.ReadGraph(r.SubMessage(), bytes);
            else
                r.SkipField(wire);
        }
        return model;
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
                    var (name, tensor) = ReadTensor(g.SubMessage(), bytes);
                    if (name.Length > 0) Initializers[name] = tensor;
                    break;
                }
                case 11 when wire == 2:  // graph input
                    inputs.Add(ReadValueInfoName(g.SubMessage()));
                    break;
                case 12 when wire == 2:  // graph output
                    outputs.Add(ReadValueInfoName(g.SubMessage()));
                    break;
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

    private static string ReadValueInfoName(ProtoReader v)
    {
        string name = "";
        while (!v.Eof)
        {
            var (field, wire) = v.ReadTag();
            if (field == 1 && wire == 2) name = v.ReadString();
            else v.SkipField(wire);
        }
        return name;
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
    private static (string Name, OnnxTensor Tensor) ReadTensor(ProtoReader t, byte[] bytes)
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
        int count = 1;
        foreach (int d in shape) count *= d;
        if (count < 0) count = 0;

        if (rawLen > 0)
        {
            if (dtype == OnnxDataType.Float)
            {
                var f = new float[rawLen / 4];
                Buffer.BlockCopy(bytes, rawStart, f, 0, f.Length * 4);
                return new OnnxTensor(dtype, shape, f);
            }
            else
            {
                // int64 / int32：按元素宽度读成 long
                int width = dtype == OnnxDataType.Int32 ? 4 : 8;
                var l = new long[rawLen / width];
                for (int i = 0; i < l.Length; i++)
                    l[i] = width == 4 ? BitConverter.ToInt32(bytes, rawStart + i * 4)
                                      : BitConverter.ToInt64(bytes, rawStart + i * 8);
                return new OnnxTensor(dtype, shape, l);
            }
        }

        if (dtype == OnnxDataType.Float)
        {
            var f = new float[count];
            for (int i = 0; i < f.Length && i < values.Count; i++) f[i] = (float)values[i];
            return new OnnxTensor(dtype, shape, f);
        }
        else
        {
            var l = new long[count];
            for (int i = 0; i < l.Length && i < values.Count; i++) l[i] = (long)values[i];
            return new OnnxTensor(dtype, shape, l);
        }
    }
}

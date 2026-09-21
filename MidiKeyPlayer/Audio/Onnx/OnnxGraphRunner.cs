using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MidiKeyPlayer.Audio.Onnx;

/// <summary>
/// 极简 ONNX 执行器：只实现 basic-pitch 模型图里出现的那 23 个算子。
///
/// 为什么自己写：模型只有 3.6 万参数、230 KB，官方 ONNX Runtime 原生库却有十几 MB。
/// 这个执行器一次只服务一个固定图，所以不追求通用：算子够用即止、张量按行主序连续存放。
/// </summary>
internal sealed class OnnxGraphRunner
{
    private readonly OnnxModel _model;
    private readonly Dictionary<string, OnnxTensor> _values = new(StringComparer.Ordinal);

    public OnnxGraphRunner(OnnxModel model) => _model = model;

    public List<OnnxNode> Nodes => _model.Nodes;

    /// <summary>值名 → 产出它的节点。图里节点的排列顺序不保证依赖在前（TF 导出的图会乱），
    /// 所以取值时不按顺序硬跑，而是按这个名字表按需回溯。</summary>
    private readonly Dictionary<string, OnnxNode> _producer = new(StringComparer.Ordinal);

    /// <summary>解析完图后建一次产出者表，供按需回溯用。</summary>
    private void EnsureProducerMap()
    {
        if (_producer.Count > 0) return;
        foreach (var node in _model.Nodes)
            foreach (string o in node.Outputs)
                if (o.Length > 0) _producer[o] = node;
    }

    /// <summary>算出一个值：常量/入口直接取，其余回溯到产出它的节点再算。</summary>
    private void Materialize(string name)
    {
        if (name.Length == 0 || _values.ContainsKey(name)) return;
        EnsureProducerMap();
        if (!_producer.TryGetValue(name, out var node))
            throw new InvalidOperationException($"张量 {name} 既不是初始值，也没有节点产出它");
        foreach (string i in node.Inputs) Materialize(i);
        if (node.Outputs.Length > 0 && node.Outputs[0].Length > 0)
        {
            var t = Execute(node);
            _values[node.Outputs[0]] = t;
            if (DumpValues.TryGetValue(node.Outputs[0], out var key))
                DumpValues[node.Outputs[0]] = key + " => " + Describe(Snapshot(t));
            if (Pinned.ContainsKey(node.Outputs[0])) Pinned[node.Outputs[0]] = Snapshot(t);
        }
    }

    /// <summary>
    /// 复制一张张量。诊断打点必须复制：图里后面的算子会原地改写张量，
    /// 只存引用的话，等整图跑完再打印会打出被改过的值（曾经因此误判过一层算子有错）。
    /// </summary>
    private static OnnxTensor Snapshot(OnnxTensor t)
    {
        var dims = (int[])t.Dims.Clone();
        if (t.F != null) return new OnnxTensor(t.Type, dims, (float[])t.F.Clone());
        if (t.L != null) return new OnnxTensor(t.Type, dims, (long[])t.L.Clone());
        return t;
    }

    /// <summary>把一张张量的形状与统计量写成一行（大张量不打全部值），供开发诊断用。</summary>
    private static string Describe(OnnxTensor t)
    {
        // 大张量：只给形状与前几个数 + 均值，避免刷屏
        if (t.Length > 64)
        {
            double sum = 0;
            float min = float.MaxValue, max = float.MinValue;
            if (t.F != null)
            {
                foreach (float v in t.F) { sum += v; if (v < min) min = v; if (v > max) max = v; }
            }
            double mean = t.F != null && t.F.Length > 0 ? sum / t.F.Length : 0;
            string head = t.F != null && t.F.Length > 0
                ? string.Join(",", t.F.Take(4).Select(v => v.ToString("G4")))
                : "(整数)";
            return $"{t.ShapeText()} 均值={mean:G5} 最小={min:G4} 最大={max:G4} 头4={head}";
        }
        string values;
        if (t.L != null)
        {
            values = string.Join(",", t.L);
        }
        else if (t.F != null)
        {
            values = string.Join(",", t.F.Select(v => v.ToString("G4")));
        }
        else values = "(空)";
        return $"{t.ShapeText()} 值[{values}]";
    }

    /// <summary>跑一遍图。input 的形状必须与模型声明的入口一致。</summary>
    public OnnxTensor Run(OnnxTensor input)
    {
        _values.Clear();
        foreach (var kv in _model.Initializers) _values[kv.Key] = kv.Value;
        _values[_model.InputName] = input;

        foreach (string name in _model.OutputNames) Materialize(name);

        return _values[_model.OutputNames[0]];
    }

    /// <summary>跑一遍图，返回全部输出（按模型声明的顺序）。用于自检比对。</summary>
    public List<OnnxTensor> RunAll(OnnxTensor input)
    {
        Run(input);
        var list = new List<OnnxTensor>();
        foreach (string name in _model.OutputNames)
            list.Add(_values.TryGetValue(name, out var t) ? t : throw new InvalidOperationException($"缺少输出 {name}"));
        return list;
    }

    /// <summary>跑一遍图，返回全部输出（输出名 → 张量）。</summary>
    public Dictionary<string, OnnxTensor> RunNamed(OnnxTensor input)
    {
        Run(input);
        var map = new Dictionary<string, OnnxTensor>(StringComparer.Ordinal);
        foreach (string name in _model.OutputNames)
            if (_values.TryGetValue(name, out var t)) map[name] = t;
        return map;
    }

    private OnnxTensor Get(string name)
    {
        if (_values.TryGetValue(name, out var t)) return t;
        throw new InvalidOperationException($"张量 {name} 还未计算");
    }

    /// <summary>每算完一个节点回调一次（类型, 输出形状文本）。只有开发探针会挂它，正常使用时为 null。</summary>
    internal Action<string, string>? Trace;

    /// <summary>【开发诊断】强制串行：用来判断并行分块是否引入错误。只有探针会开。</summary>
    internal static bool SerialOnly;

    /// <summary>【开发诊断】把这些名字的张量算出来后打一份值。只给探针用，正常使用为空。</summary>
    internal readonly Dictionary<string, string> DumpValues = new(StringComparer.Ordinal);

    /// <summary>【开发诊断】钉住的张量：算出来立刻深拷贝留底，避免被后续算子原地改写。只给探针用。</summary>
    internal readonly Dictionary<string, OnnxTensor> Pinned = new(StringComparer.Ordinal);

    private OnnxTensor Execute(OnnxNode node)
    {
        try
        {
            var result = ExecuteCore(node);
            if (Trace != null) Trace(node.OpType, result.ShapeText());
            return result;
        }
        catch (Exception ex)
        {
            var shapes = new System.Text.StringBuilder();
            foreach (string name in node.Inputs)
                if (name.Length > 0 && _values.TryGetValue(name, out var t))
                    shapes.Append($"{name}{t.ShapeText()} ");
            throw new InvalidOperationException(
                $"算子 {node.OpType} 失败：{ex.Message}；输入 {shapes}", ex);
        }
    }

    private OnnxTensor ExecuteCore(OnnxNode node)
    {
        switch (node.OpType)
        {
            case "Conv": return Conv(node);
            case "ConvTranspose": return ConvTranspose(node);
            case "BatchNormalization": return BatchNormalization(node);
            case "Identity": return Get(node.Inputs[0]);
            case "LeakyRelu": return LeakyRelu(node);
            case "Constant": return Constant(node);
            case "ConstantOfShape": return ConstantOfShape(node);
            case "Reshape": return Reshape(node);
            case "Unsqueeze": return Unsqueeze(node);
            case "Transpose": return Transpose(node);
            case "Concat": return Concat(node);
            case "Slice": return Slice(node);
            case "Pad": return Pad(node);
            case "Neg": return Unary(node, v => -v);
            case "Relu": return Unary(node, v => v < 0f ? 0f : v);
            case "Sigmoid": return Unary(node, v => 1f / (1f + (float)Math.Exp(-v)));
            case "Sqrt": return Unary(node, v => (float)Math.Sqrt(v));
            case "Log": return Unary(node, v => (float)Math.Log(v));
            case "Mul": return Binary(node, (a, b) => a * b);
            case "Add": return Binary(node, (a, b) => a + b);
            case "Sub": return Binary(node, (a, b) => a - b);
            case "Div": return Binary(node, (a, b) => a / b);
            case "Equal": return Equal(node);
            case "Where": return Where(node);
            case "Shape": return Shape(node);
            case "Cast": return Cast(node);
            case "ReduceSum": return Reduce(node, ReduceKind.Sum);
            case "ReduceMin": return Reduce(node, ReduceKind.Min);
            case "ReduceMax": return Reduce(node, ReduceKind.Max);
            default:
                throw new NotSupportedException($"执行器没有实现算子 {node.OpType}");
        }
    }

    // ===================== 逐元素 =====================

    private OnnxTensor Unary(OnnxNode node, Func<float, float> op)
    {
        var x = Get(node.Inputs[0]);
        var y = new float[x.Length];
        var src = x.F!;
        for (int i = 0; i < y.Length; i++) y[i] = op(src[i]);
        return new OnnxTensor(x.Type, x.Dims, y);
    }

    private OnnxTensor Binary(OnnxNode node, Func<float, float, float> op)
    {
        var a = Get(node.Inputs[0]);
        var b = Get(node.Inputs[1]);
        int[] dims = BroadcastDims(a.Dims, b.Dims);
        var outT = new OnnxTensor(OnnxDataType.Float, dims);
        var (as_, bs, os) = (BroadcastStrides(a.Dims, dims), BroadcastStrides(b.Dims, dims), OnnxTensor.Strides(dims));
        Elementwise(a.F!, as_, b.F!, bs, outT.F!, os, dims, op);
        return outT;
    }

    private static void Elementwise(float[] x, int[] xs, float[] y, int[] ys, float[] o, int[] os,
        int[] dims, Func<float, float, float> op)
    {
        int rank = dims.Length;
        int total = o.Length;
        var idx = new int[rank];
        int ox = 0, oy = 0, oo = 0;
        for (int i = 0; i < total; i++)
        {
            o[oo] = op(x[ox], y[oy]);
            for (int d = rank - 1; d >= 0; d--)
            {
                idx[d]++;
                oo += os[d]; ox += xs[d]; oy += ys[d];
                if (idx[d] < dims[d]) break;
                idx[d] = 0;
                oo -= os[d] * dims[d]; ox -= xs[d] * dims[d]; oy -= ys[d] * dims[d];
            }
        }
    }

    private OnnxTensor Equal(OnnxNode node)
    {
        var a = Get(node.Inputs[0]);
        var b = Get(node.Inputs[1]);
        int[] dims = BroadcastDims(a.Dims, b.Dims);
        var outT = new OnnxTensor(OnnxDataType.Bool, dims);
        var (as_, bs, os) = (BroadcastStrides(a.Dims, dims), BroadcastStrides(b.Dims, dims), OnnxTensor.Strides(dims));
        int rank = dims.Length;
        var idx = new int[rank];
        int ox = 0, oy = 0, oo = 0;
        for (int i = 0; i < outT.Length; i++)
        {
            outT.L![oo] = a.F![ox] == b.F![oy] ? 1L : 0L;
            for (int d = rank - 1; d >= 0; d--)
            {
                idx[d]++;
                oo += os[d]; ox += as_[d]; oy += bs[d];
                if (idx[d] < dims[d]) break;
                idx[d] = 0;
                oo -= os[d] * dims[d]; ox -= as_[d] * dims[d]; oy -= bs[d] * dims[d];
            }
        }
        return outT;
    }

    private OnnxTensor Where(OnnxNode node)
    {
        var c = Get(node.Inputs[0]);
        var a = Get(node.Inputs[1]);
        var b = Get(node.Inputs[2]);
        int[] dims = BroadcastDims(BroadcastDims(c.Dims, a.Dims), b.Dims);
        var outT = new OnnxTensor(OnnxDataType.Float, dims);
        var cs = BroadcastStrides(c.Dims, dims);
        var as_ = BroadcastStrides(a.Dims, dims);
        var bs = BroadcastStrides(b.Dims, dims);
        var os = OnnxTensor.Strides(dims);
        int rank = dims.Length;
        var idx = new int[rank];
        int oc = 0, oa = 0, ob = 0, oo = 0;
        for (int i = 0; i < outT.Length; i++)
        {
            outT.F![oo] = c.L![oc] != 0 ? a.F![oa] : b.F![ob];
            for (int d = rank - 1; d >= 0; d--)
            {
                idx[d]++;
                oo += os[d]; oc += cs[d]; oa += as_[d]; ob += bs[d];
                if (idx[d] < dims[d]) break;
                idx[d] = 0;
                oo -= os[d] * dims[d]; oc -= cs[d] * dims[d]; oa -= as_[d] * dims[d]; ob -= bs[d] * dims[d];
            }
        }
        return outT;
    }

    /// <summary>NumPy 风格广播后的形状。</summary>
    private static int[] BroadcastDims(int[] a, int[] b)
    {
        int rank = Math.Max(a.Length, b.Length);
        var dims = new int[rank];
        for (int i = 0; i < rank; i++)
        {
            int da = i < rank - a.Length ? 1 : a[i - (rank - a.Length)];
            int db = i < rank - b.Length ? 1 : b[i - (rank - b.Length)];
            if (da != db && da != 1 && db != 1)
                throw new InvalidOperationException($"广播失败：{da} 与 {db}");
            dims[i] = Math.Max(da, db);
        }
        return dims;
    }

    /// <summary>把一张张量的步长对齐到目标形状；被广播的维度步长为 0。</summary>
    private static int[] BroadcastStrides(int[] dims, int[] outDims)
    {
        var own = OnnxTensor.Strides(dims);
        var res = new int[outDims.Length];
        int shift = outDims.Length - dims.Length;
        for (int i = 0; i < outDims.Length; i++)
        {
            if (i < shift) res[i] = 0;
            else
            {
                int d = i - shift;
                res[i] = dims[d] == 1 ? 0 : own[d];
            }
        }
        return res;
    }

    // ===================== 形状 =====================

    private OnnxTensor Shape(OnnxNode node)
    {
        var x = Get(node.Inputs[0]);
        var data = new long[x.Dims.Length];
        for (int i = 0; i < data.Length; i++) data[i] = x.Dims[i];
        return new OnnxTensor(OnnxDataType.Int64, new[] { data.Length }, data);
    }

    private OnnxTensor Cast(OnnxNode node)
    {
        var x = Get(node.Inputs[0]);
        // to 属性有时被读成 int，有时被读成 long[]：三种都认；都没有就推断成 float32
        OnnxDataType to;
        if (node.Attributes.TryGetValue("to", out var rawTo))
        {
            to = rawTo switch
            {
                long l => (OnnxDataType)l,
                int i => (OnnxDataType)i,
                long[] la when la.Length > 0 => (OnnxDataType)la[0],
                _ => OnnxDataType.Float,
            };
        }
        else to = OnnxDataType.Float;
        // 半精度目标一律按 float32 处理：本执行器只认 float32 与整数两种表示
        if (to == OnnxDataType.Float16 || to == OnnxDataType.BFloat16 || to == OnnxDataType.Double)
            to = OnnxDataType.Float;

        int n = x.Length;
        // 源数据只有一边有值：两边都要判空，不能看着目标类型就假定源是另一边
        var srcLong = x.L;
        var srcFloat = x.F;
        if (to == OnnxDataType.Float)
        {
            var f = new float[n];
            if (srcFloat != null && srcFloat.Length > 0) Array.Copy(srcFloat, f, Math.Min(n, srcFloat.Length));
            else if (srcLong != null) for (int i = 0; i < n && i < srcLong.Length; i++) f[i] = srcLong[i];
            return new OnnxTensor(to, x.Dims, f);
        }
        var longData = new long[n];
        if (srcLong != null && srcLong.Length > 0) Array.Copy(srcLong, longData, Math.Min(n, srcLong.Length));
        else if (srcFloat != null) for (int i = 0; i < n && i < srcFloat.Length; i++) longData[i] = (long)srcFloat[i];
        return new OnnxTensor(to, x.Dims, longData);
    }

    private OnnxTensor Reshape(OnnxNode node)
    {
        var x = Get(node.Inputs[0]);
        var shapeT = Get(node.Inputs[1]);
        var target = new int[shapeT.Length];
        for (int i = 0; i < target.Length; i++) target[i] = (int)shapeT.L![i];

        int inferIdx = -1;
        int known = 1;
        for (int i = 0; i < target.Length; i++)
        {
            if (target[i] == -1) { inferIdx = i; continue; }
            if (target[i] == 0) target[i] = i < x.Dims.Length ? x.Dims[i] : 1;   // allowzero 默认关；越界按 1
            known *= target[i];
        }
        if (inferIdx >= 0) target[inferIdx] = known == 0 ? 0 : x.Length / known;

        int total = OnnxTensor.ElementCount(target);
        if (total != x.Length)
            throw new InvalidOperationException($"Reshape 元素数不符：{x.Length} → {total}");
        return x.WithDims(target);
    }

    private OnnxTensor Unsqueeze(OnnxNode node)
    {
        var x = Get(node.Inputs[0]);
        long[] axes = node.Inputs.Length > 1 && node.Inputs[1].Length > 0
            ? Get(node.Inputs[1]).L!
            : node.Ints("axes");
        int outRank = x.Dims.Length + axes.Length;
        var flags = new bool[outRank];
        foreach (long a in axes)
        {
            int idx = (int)(a < 0 ? a + outRank : a);
            if (idx < 0 || idx >= outRank) throw new InvalidOperationException($"Unsqueeze 轴越界：{a}");
            flags[idx] = true;
        }
        var dims = new int[outRank];
        int src = 0;
        for (int i = 0; i < outRank; i++) dims[i] = flags[i] ? 1 : x.Dims[src++];
        return x.WithDims(dims);
    }

    private OnnxTensor Transpose(OnnxNode node)
    {
        var x = Get(node.Inputs[0]);
        var perm = node.Ints("perm");
        int rank = x.Dims.Length;
        var p = new int[rank];
        if (perm.Length == rank) for (int i = 0; i < rank; i++) p[i] = (int)perm[i];
        else for (int i = 0; i < rank; i++) p[i] = rank - 1 - i;

        var outDims = new int[rank];
        for (int i = 0; i < rank; i++) outDims[i] = x.Dims[p[i]];
        var inStrides = OnnxTensor.Strides(x.Dims);
        var outStrides = OnnxTensor.Strides(outDims);
        int total = x.Length;
        var idx = new int[rank];
        int srcOff = 0;

        if (x.F != null)
        {
            var f = new float[total];
            for (int i = 0; i < total; i++)
            {
                int dstOff = 0;
                for (int d = 0; d < rank; d++) dstOff += idx[p[d]] * outStrides[d];
                f[dstOff] = x.F[srcOff];
                srcOff = Advance(idx, x.Dims, inStrides, srcOff);
            }
            return new OnnxTensor(x.Type, outDims, f);
        }
        else
        {
            var l = new long[total];
            for (int i = 0; i < total; i++)
            {
                int dstOff = 0;
                for (int d = 0; d < rank; d++) dstOff += idx[p[d]] * outStrides[d];
                l[dstOff] = x.L![srcOff];
                srcOff = Advance(idx, x.Dims, inStrides, srcOff);
            }
            return new OnnxTensor(x.Type, outDims, l);
        }
    }

    /// <summary>行主序自增一个下标，返回新的偏移。</summary>
    private static int Advance(int[] idx, int[] dims, int[] strides, int offset)
    {
        for (int d = dims.Length - 1; d >= 0; d--)
        {
            idx[d]++;
            offset += strides[d];
            if (idx[d] < dims[d]) return offset;
            idx[d] = 0;
            offset -= strides[d] * dims[d];
        }
        return offset;
    }

    private OnnxTensor Concat(OnnxNode node)
    {
        var first = Get(node.Inputs[0]);
        int axis = (int)node.Int("axis");
        int rank = first.Dims.Length;
        if (axis < 0) axis += rank;

        var outDims = (int[])first.Dims.Clone();
        for (int i = 1; i < node.Inputs.Length; i++)
        {
            var t = Get(node.Inputs[i]);
            outDims[axis] += t.Dims[axis];
        }

        int outer = 1, inner = 1;
        for (int i = 0; i < axis; i++) outer *= outDims[i];
        for (int i = axis + 1; i < rank; i++) inner *= outDims[i];

        var outT = AllocLike(first, outDims);
        int write = 0;
        for (int o = 0; o < outer; o++)
        {
            foreach (string name in node.Inputs)
            {
                var t = Get(name);
                int block = t.Dims[axis] * inner;
                if (t.F != null) Array.Copy(t.F, o * block, outT.F!, write, block);
                else Array.Copy(t.L!, o * block, outT.L!, write, block);
                write += block;
            }
        }
        return outT;
    }

    /// <summary>按源张量的类型与形状开一块新数据（形状类数据也要能切片/拼接）。</summary>
    private static OnnxTensor AllocLike(OnnxTensor x, int[] dims)
    {
        int n = OnnxTensor.ElementCount(dims);
        return x.F != null
            ? new OnnxTensor(x.Type, dims, new float[n])
            : new OnnxTensor(x.Type, dims, new long[n]);
    }

    private static void CopyAt(OnnxTensor src, int srcOff, OnnxTensor dst, int dstOff)
    {
        if (src.F != null) dst.F![dstOff] = src.F[srcOff];
        else dst.L![dstOff] = src.L![srcOff];
    }

    private OnnxTensor Slice(OnnxNode node)
    {
        try
        {
            return SliceCore(node);
        }
        catch (Exception ex)
        {
            string Dump(string name)
            {
                if (!_values.TryGetValue(name, out var t)) return name + "(未算)";
                if (t.L != null) return $"{name}{t.ShapeText()}=[{string.Join(",", t.L)}]";
                if (t.F != null) return $"{name}{t.ShapeText()}=[{string.Join(",", t.F)}]";
                return name + t.ShapeText();
            }
            throw new InvalidOperationException(
                $"Slice 参数原样：{string.Join(" ", Array.ConvertAll(node.Inputs, Dump))}", ex);
        }
    }

    private OnnxTensor SliceCore(OnnxNode node)
    {
        var x = Get(node.Inputs[0]);
        long[] starts = ReadInts(node, 1, "starts");
        long[] ends = ReadInts(node, 2, "ends");
        long[]? axes = node.Inputs.Length > 3 && node.Inputs[3].Length > 0 ? Get(node.Inputs[3]).L : null;
        long[]? steps = node.Inputs.Length > 4 && node.Inputs[4].Length > 0 ? Get(node.Inputs[4]).L : null;
        int stage = 0;
        int kAxes = axes?.Length ?? -1;
        int kSteps = steps?.Length ?? -1;
        try
        {
        int rank = x.Dims.Length;
        stage = 1;
        int k = starts.Length;
        if (axes != null && axes.Length != k)
            throw new InvalidOperationException($"Slice 参数长度不一致：starts {k}，axes {axes.Length}");
        if (steps != null && steps.Length != k)
            throw new InvalidOperationException($"Slice 参数长度不一致：starts {k}，steps {steps.Length}；输入 {x.ShapeText()}");
        if (ends.Length != k)
            throw new InvalidOperationException($"Slice 参数长度不一致：starts {k}，ends {ends.Length}");

        var begin = new int[rank];
        var step = new int[rank];
        var count = new int[rank];
        for (int i = 0; i < rank; i++) { begin[i] = 0; step[i] = 1; count[i] = x.Dims[i]; }

        for (int kk = 0; kk < k; kk++)
        {
            int axis = axes != null ? (int)axes[kk] : kk;
            if (axis < 0) axis += rank;
            if (axis < 0 || axis >= rank)
                throw new InvalidOperationException($"Slice 轴越界：{axis}，输入 {x.ShapeText()}");
            long st = steps != null ? steps[kk] : 1;
            long start = starts[kk];
            long end = ends[kk];
            int dim = x.Dims[axis];

            if (st > 0)
            {
                // 步长为正：先夹到 [-dim, dim]（哨兵值 INT64_MIN / INT64_MAX 落到边界），
                // 负数再加维度长度，最后起点夹到 [0, dim-1]、终点夹到 [0, dim] 再按步数算个数。
                start = SliceIndex(start, -dim, dim);
                end = SliceIndex(end, -dim, dim);
                if (start < 0) start += dim;
                if (end < 0) end += dim;
                start = Math.Clamp(start, 0, Math.Max(0, dim - 1));
                end = Math.Clamp(end, 0, dim);
                begin[axis] = (int)start;
                step[axis] = (int)st;
                count[axis] = end > start ? (int)((end - start + st - 1) / st) : 0;
                Trace?.Invoke("Slice分支", $"轴{axis} 正 起{start} 止{end} 个{count[axis]} 维{dim}");
            }
            else
            {
                // 步长为负：起点先按负数加维度长度（-1 → dim-1），再夹到 [0, dim-1]；
                // 终点夹到 [-1, dim-1] 后**原样用负数**算个数（-1 表示「一直走到头」）。
                // 这套口径逐条比对过 onnxruntime（含 starts=[-1] ends=[INT64_MIN] steps=[-1] 这类反向切片）。
                start = SliceIndex(start, -dim, dim - 1);
                if (start < 0) start += dim;
                start = Math.Clamp(start, 0, dim - 1);
                end = SliceIndex(end, -1, dim - 1);
                long span = start - end;
                begin[axis] = (int)start;
                step[axis] = (int)st;
                // 跨几个位置就是几个元素（不再加一）：starts=[-1] 在长度 4 上要正好取 4 个
                count[axis] = span < 0 ? 0 : (int)(span / (-st));
                Trace?.Invoke("Slice分支", $"轴{axis} 负 起{start} 止{end} 个{count[axis]} 维{dim}");
            }
        }

        var outDims = (int[])count.Clone();
        stage = 2;
        var outT = AllocLike(x, outDims);
        Trace?.Invoke("Slice", $"{x.ShapeText()} axes[{(axes == null ? "-" : string.Join(",", axes))}]" +
            $" starts[{string.Join(",", starts)}] ends[{string.Join(",", ends)}]" +
            $" steps[{(steps == null ? "-" : string.Join(",", steps))}]" +
            $" begin[{string.Join(",", begin)}] count[{string.Join(",", count)}] step[{string.Join(",", step)}]" +
            $" -> {outT.ShapeText()}");
        if (outT.Length == 0) return outT;   // 切空了：没有元素可拷，直接返回
        var inStrides = OnnxTensor.Strides(x.Dims);
        stage = 3;
        // 逐个输出元素算回输入坐标：比顺手改一个可变偏移稳，越界也当场看得见
        var oi = new int[rank];
        for (int i = 0; i < outT.Length; i++)
        {
            int srcOff = 0;
            for (int d = 0; d < rank; d++)
            {
                int s = begin[d] + oi[d] * step[d];
                if (s < 0 || s >= x.Dims[d])
                    throw new InvalidOperationException(
                        $"Slice 下标越界：轴 {d} 取 {s}，输入 {x.ShapeText()} 的第 {d} 轴长度 {x.Dims[d]}" +
                        $"；begin[{string.Join(",", begin)}] step[{string.Join(",", step)}]" +
                        $" count[{string.Join(",", count)}] oi[{string.Join(",", oi)}]" +
                        $" starts[{string.Join(",", starts)}] ends[{string.Join(",", ends)}]" +
                        $" axes[{(axes == null ? "-" : string.Join(",", axes))}] steps[{(steps == null ? "-" : string.Join(",", steps))}]");
                srcOff += s * inStrides[d];
            }
            CopyAt(x, srcOff, outT, i);
            for (int d = rank - 1; d >= 0; d--)
            {
                oi[d]++;
                if (oi[d] < outDims[d]) break;
                oi[d] = 0;
            }
        }
        return outT;
        }
        catch (IndexOutOfRangeException ex)
        {
            throw new InvalidOperationException(
                $"Slice 内部越界：阶段 {stage}，rank={x.Dims.Length}，starts[{starts.Length}] ends[{ends.Length}]" +
                $" axes[{kAxes}] steps[{kSteps}] 输入 {x.ShapeText()}；栈 {ex.StackTrace}");
        }
    }

    /// <summary>
    /// Slice 下标夹取。ONNX 用 INT64_MIN / INT64_MAX 当「切到头」的哨兵值，
    /// 直接 Math.Clamp 会把它们当成普通数字（结果是错的方向），所以哨兵一律落到边界。
    /// </summary>
    private static long SliceIndex(long v, long lo, long hi)
    {
        if (v == long.MinValue) return lo;
        if (v == long.MaxValue) return hi;
        return Math.Clamp(v, lo, hi);
    }

    private OnnxTensor Pad(OnnxNode node)
    {
        return PadCore(node);
    }

    private OnnxTensor PadCore(OnnxNode node)
    {
        var x = Get(node.Inputs[0]);
        long[] pads = ReadInts(node, 1, "pads");
        // 填充值是可选的第三入口；标量常量在有些导出里是空形状，F / L 都为空，这时按 0 处理
        float constant = 0f;
        if (node.Inputs.Length > 2 && node.Inputs[2].Length > 0)
        {
            var fill = Get(node.Inputs[2]);
            if (fill.F is { Length: > 0 } ff) constant = ff[0];
            else if (fill.L is { Length: > 0 } fl) constant = fl[0];
        }
        string mode = node.Str("mode", "constant");

        int rank = x.Dims.Length;
        var begin = new int[rank];
        var endPad = new int[rank];
        Trace?.Invoke("Pad参数", $"出[{node.Outputs[0]}] 入 {x.ShapeText()} pads({pads.Length})=[{string.Join(",", pads)}]");
        // pads 的长度有三种常见口径：2*rank（前后各 rank）、rank（只在末尾补）、1（两边同值）
        for (int i = 0; i < rank; i++)
        {
            if (pads.Length >= rank * 2) { begin[i] = (int)pads[i]; endPad[i] = (int)pads[rank + i]; }
            else if (pads.Length >= rank) { begin[i] = 0; endPad[i] = (int)pads[i]; }
            else { int v = pads.Length > 0 ? (int)pads[pads.Length - 1] : 0; begin[i] = v; endPad[i] = v; }
        }

        var outDims = new int[rank];
        for (int i = 0; i < rank; i++) outDims[i] = x.Dims[i] + begin[i] + endPad[i];
        // 输出跟着输入的实际数据走：只按声明类型建数组，遇到「声明与实际不一致」的张量会建错边
        var outT = x.F is { Length: > 0 }
            ? new OnnxTensor(OnnxDataType.Float, outDims, new float[OnnxTensor.ElementCount(outDims)])
            : new OnnxTensor(OnnxDataType.Int64, outDims, new long[OnnxTensor.ElementCount(outDims)]);
        if (outT.Length == 0) return outT;

        var inStrides = OnnxTensor.Strides(x.Dims);
        var outStrides = OnnxTensor.Strides(outDims);
        var idx = new int[rank];
        int total = outT.Length;
        for (int i = 0; i < total; i++)
        {
            int srcOff = 0;
            bool inside = true;
            for (int d = 0; d < rank; d++)
            {
                int s = idx[d] - begin[d];
                if (s < 0 || s >= x.Dims[d])
                {
                    if (mode == "constant") { inside = false; break; }
                    s = mode == "edge" ? Math.Clamp(s, 0, x.Dims[d] - 1) : Reflect(s, x.Dims[d]);
                }
                srcOff += s * inStrides[d];
            }
            if (inside) CopyAt(x, srcOff, outT, i);
            else if (outT.F != null) outT.F[i] = constant;
            else outT.L![i] = (long)constant;
            Advance(idx, outDims, outStrides, 0);
        }
        return outT;
    }

    /// <summary>ONNX reflect 模式（与 numpy 'reflect' 相同：不重复边缘值）。</summary>
    private static int Reflect(int i, int n)
    {
        if (n <= 1) return 0;
        int period = 2 * n - 2;
        i = ((i % period) + period) % period;
        return i >= n ? period - i : i;
    }

    // ===================== 归约 =====================

    private enum ReduceKind { Sum, Min, Max }

    private OnnxTensor Reduce(OnnxNode node, ReduceKind kind)
    {
        var x = Get(node.Inputs[0]);
        long[]? axes = node.Inputs.Length > 1 && node.Inputs[1].Length > 0 ? Get(node.Inputs[1]).L : null;
        if (axes == null && node.Has("axes")) axes = node.Ints("axes");
        bool keepDims = node.Int("keepdims", 1) != 0;
        bool noopEmpty = node.Int("noop_with_empty_axes", 0) != 0;

        int rank = x.Dims.Length;
        var flags = new bool[rank];
        if (axes == null || axes.Length == 0)
        {
            if (noopEmpty) return x;
            for (int i = 0; i < rank; i++) flags[i] = true;
        }
        else
        {
            foreach (long a in axes)
            {
                int axisIdx = (int)(a < 0 ? a + rank : a);
                flags[axisIdx] = true;
            }
        }

        var outDims = new List<int>();
        for (int i = 0; i < rank; i++)
        {
            if (flags[i]) { if (keepDims) outDims.Add(1); }
            else outDims.Add(x.Dims[i]);
        }
        var inStrides = OnnxTensor.Strides(x.Dims);
        int total = x.Length;
        var idx = new int[rank];
        var acc = new float[OnnxTensor.ElementCount(outDims.ToArray())];
        var seen = new bool[acc.Length];

        // 非归约维在输出里的位置（行主序）
        var map = new int[rank];
        int w = 0;
        for (int i = 0; i < rank; i++) map[i] = flags[i] ? -1 : w++;

        for (int i = 0; i < total; i++)
        {
            int outIdx = 0;
            int mult = 1;
            for (int d = rank - 1; d >= 0; d--)
            {
                if (map[d] >= 0)
                {
                    outIdx += idx[d] * mult;
                    mult *= x.Dims[d];
                }
            }
            float v = x.F![i];
            if (kind == ReduceKind.Sum) acc[outIdx] += v;
            else if (!seen[outIdx]) { acc[outIdx] = v; seen[outIdx] = true; }
            else if (kind == ReduceKind.Min) acc[outIdx] = Math.Min(acc[outIdx], v);
            else acc[outIdx] = Math.Max(acc[outIdx], v);

            Advance(idx, x.Dims, inStrides, 0);
        }

        return new OnnxTensor(OnnxDataType.Float, outDims.ToArray(), acc);
    }

    // ===================== 常量与激活 =====================

    /// <summary>Constant：值放在 value 属性里（TensorProto），没有就按 value_float / value_int 取。</summary>
    private static OnnxTensor Constant(OnnxNode node)
    {
        var t = node.Tensor("value");
        if (t != null) return t;
        if (node.Attributes.TryGetValue("value_float", out var vf))
            return new OnnxTensor(OnnxDataType.Float, new[] { 1 }, new[] { ToFloat(vf) });
        if (node.Attributes.TryGetValue("value_floats", out var vfs))
            return new OnnxTensor(OnnxDataType.Float, new[] { 1, Len(vfs) }, ToFloats(vfs));
        if (node.Attributes.TryGetValue("value_int", out var vi))
            return new OnnxTensor(OnnxDataType.Int64, new[] { 1 }, new[] { ToLong(vi) });
        if (node.Attributes.TryGetValue("value_ints", out var vis))
            return new OnnxTensor(OnnxDataType.Int64, new[] { 1, Len(vis) }, ToLongs(vis));
        throw new NotSupportedException("Constant 只支持 value（张量）与 value_float / value_floats / value_int / value_ints");
    }

    private static int Len(object v) => v is Array a ? a.Length : 1;

    private static float ToFloat(object v) => v switch
    {
        float f => f,
        long l => l,
        long[] la => la.Length > 0 ? la[0] : 0f,
        float[] fa => fa.Length > 0 ? fa[0] : 0f,
        _ => 0f,
    };

    private static long ToLong(object v) => v switch
    {
        long l => l,
        float f => (long)f,
        long[] la => la.Length > 0 ? la[0] : 0L,
        float[] fa => fa.Length > 0 ? (long)fa[0] : 0L,
        _ => 0L,
    };

    private static float[] ToFloats(object v) => v switch
    {
        float[] fa => fa,
        long[] la => Array.ConvertAll(la, x => (float)x),
        _ => new[] { ToFloat(v) },
    };

    private static long[] ToLongs(object v) => v switch
    {
        long[] la => la,
        float[] fa => Array.ConvertAll(fa, x => (long)x),
        _ => new[] { ToLong(v) },
    };

    /// <summary>ConstantOfShape：入口是 int64 形状，值取 value 属性（默认 float 0）。</summary>
    private OnnxTensor ConstantOfShape(OnnxNode node)
    {
        string shapeName = node.Inputs.Length > 0 ? node.Inputs[0] : "";
        OnnxTensor shapeT;
        if (shapeName.Length == 0) throw new InvalidOperationException("ConstantOfShape 缺少形状入口");
        if (!_values.TryGetValue(shapeName, out shapeT!))
            throw new InvalidOperationException($"ConstantOfShape 的形状 {shapeName} 还没算出来");
        var dims = new int[shapeT.Length];
        for (int i = 0; i < dims.Length; i++) dims[i] = (int)shapeT.L![i];

        var value = node.Tensor("value");
        var ty = value?.Type ?? OnnxDataType.Float;
        var outT = new OnnxTensor(ty, dims);
        if (ty == OnnxDataType.Float)
        {
            float fill = value?.F is { Length: > 0 } f ? f[0] : 0f;
            if (fill != 0f) Array.Fill(outT.F!, fill);
        }
        else
        {
            long fill = value?.L is { Length: > 0 } l ? l[0] : 0L;
            if (fill != 0L) Array.Fill(outT.L!, fill);
        }
        return outT;
    }

    private OnnxTensor LeakyRelu(OnnxNode node)
    {
        float alpha = node.Float("alpha", 0.01f);
        return Unary(node, v => v < 0f ? alpha * v : v);
    }

    /// <summary>
    /// BatchNormalization（推理态）：y = scale * (x - mean) / sqrt(var + eps) + bias。
    /// scale / bias / mean / var 都是一维（按通道），逐元素那把公式展开即可，不用另开张量。
    /// </summary>
    private OnnxTensor BatchNormalization(OnnxNode node)
    {
        var x = Get(node.Inputs[0]);
        var scale = Get(node.Inputs[1]);
        var bias = Get(node.Inputs[2]);
        var mean = Get(node.Inputs[3]);
        var varT = Get(node.Inputs[4]);
        float eps = node.Float("epsilon", 1e-5f);
        Trace?.Invoke("BN参数", $"{node.Outputs[0]} eps={eps} 通道={scale.Length}");

        int n = x.Length;
        int c = scale.Length;
        if (c <= 0 || n % c != 0)
            throw new InvalidOperationException($"BatchNormalization 通道数对不上：{x.ShapeText()} / {c}");

        var y = new float[n];
        var xd = x.F!;
        var sc = scale.F!;
        var bi = bias.F!;
        var me = mean.F!;
        var va = varT.F!;
        for (int i = 0; i < n; i++)
        {
            int ch = (i / (n / c)) % c;
            y[i] = sc[ch] * (xd[i] - me[ch]) / (float)Math.Sqrt(va[ch] + eps) + bi[ch];
        }
        return new OnnxTensor(OnnxDataType.Float, x.Dims, y);
    }

    // ===================== 卷积 =====================

    /// <summary>按输出元素累加的通用卷积（支持 groups 与 dilation）。核与输入都按行主序连续存放。</summary>
    private static void ConvAccumulate(float[] xd, float[] wd, float[] y, int total, int m, int cIn,
        int h, int wIn, int outH, int outW, int kh, int kw, int sh, int sw, int dh, int dw,
        int pt, int pl, int groups)
    {
        Block(total, (from, to) =>
        {
            int strideN = m * outH * outW;
            int strideC = h * wIn;
            for (int dst = from; dst < to; dst++)
            {
                int ow = dst % outW;
                int oh = (dst / outW) % outH;
                int mi = (dst / (outW * outH)) % m;
                int ni = dst / strideN;
                int cPerGroup = cIn / groups;
                int outPerGroup = m / groups;
                int g = mi / outPerGroup;
                float sum = 0f;
                for (int ci = 0; ci < cPerGroup; ci++)
                {
                    int c = g * cPerGroup + ci;
                    int xPlane = (ni * cIn + c) * strideC;
                    int wPlane = (mi * cPerGroup + ci) * kh * kw;
                    for (int r = 0; r < kh; r++)
                    {
                        int ih = oh * sh - pt + r * dh;
                        if (ih < 0 || ih >= h) continue;
                        int xRow = xPlane + ih * wIn;
                        int wRow = wPlane + r * kw;
                        for (int c2 = 0; c2 < kw; c2++)
                        {
                            int iw = ow * sw - pl + c2 * dw;
                            if (iw < 0 || iw >= wIn) continue;
                            sum += xd[xRow + iw] * wd[wRow + c2];
                        }
                    }
                }
                y[dst] = sum;
            }
        });
    }

    /// <summary>大数组按核数切块跑 Parallel.For：块大小随数据量走，小张量用串行避免调度开销。</summary>
    private static void Block(int total, Action<int, int> body)
    {
        if (total <= 32768 || SerialOnly)
        {
            body(0, total);
            return;
        }
        int chunks = Math.Max(1, Math.Min(Environment.ProcessorCount * 4, total / 8192));
        int per = (total + chunks - 1) / chunks;
        Parallel.For(0, chunks, k =>
        {
            int from = k * per;
            int to = Math.Min(total, from + per);
            if (from < to) body(from, to);
        });
    }

    /// <summary>
    /// 卷积。本执行器统一按 NCHW：输入 [N, C, H, W]，权重 [M, C/group, kH, kW]。
    /// 之前按「通道在最后」处理是错的：Keras 导出的图会被导出器转成 NCHW，权重也是这个口径。
    /// </summary>
    private OnnxTensor Conv(OnnxNode node)
    {
        var x = Get(node.Inputs[0]);
        var wT = Get(node.Inputs[1]);
        OnnxTensor? bT = node.Inputs.Length > 2 && node.Inputs[2].Length > 0 ? Get(node.Inputs[2]) : null;

        if (x.Dims.Length != 4)
            throw new NotSupportedException($"Conv 只支持 4 维输入，收到 {x.ShapeText()}");

        int n = x.Dims[0], cIn = x.Dims[1], h = x.Dims[2], wIn = x.Dims[3];
        int m = wT.Dims[0], cPerGroup = wT.Dims[1], kh = wT.Dims[2], kw = wT.Dims[3];
        long group = node.Int("group", 1);
        if (cPerGroup * group != cIn)
            throw new InvalidOperationException(
                $"Conv 输入通道数与权重不符：输入 {x.ShapeText()}，权重 {wT.ShapeText()}，group={group}");

        var strides = node.Ints("strides");
        var dil = node.Ints("dilations");
        var pads = node.Ints("pads");
        int sh = strides.Length > 0 ? (int)strides[0] : 1;
        int sw = strides.Length > 1 ? (int)strides[1] : 1;
        int dh = dil.Length > 0 ? (int)dil[0] : 1;
        int dw = dil.Length > 1 ? (int)dil[1] : 1;
        int pt = pads.Length > 0 ? (int)pads[0] : 0;
        int pl = pads.Length > 1 ? (int)pads[1] : 0;
        int pb = pads.Length > 2 ? (int)pads[2] : 0;
        int pr = pads.Length > 3 ? (int)pads[3] : 0;

        int outH = (h + pt + pb - (dh * (kh - 1) + 1)) / sh + 1;
        int outW = (wIn + pl + pr - (dw * (kw - 1) + 1)) / sw + 1;

        var y = new float[n * m * outH * outW];
        ConvAccumulate(x.F!, wT.F!, y, y.Length, m, cIn, h, wIn, outH, outW, kh, kw,
            sh, sw, dh, dw, pt, pl, (int)group);

        if (bT?.F is { } bd)
        {
            int plane = outH * outW;
            Block(y.Length, (from, to) =>
            {
                for (int i = from; i < to; i++) y[i] += bd[(i / plane) % m];
            });
        }

        return new OnnxTensor(OnnxDataType.Float, new[] { n, m, outH, outW }, y);
    }


    /// <summary>
    /// 转置卷积（上采样用）。权重按 ONNX 规范是 [C_in, C_out/group, kH, kW]，
    /// 与 Conv 的 [M, C/group, kH, kW] 相反 —— 两个算子绝不能共用一份索引代码。
    ///
    /// 输出尺寸：out = (in - 1) * stride - padBegin - padEnd + dilation * (k - 1) + outputPadding + 1
    /// </summary>
    private OnnxTensor ConvTranspose(OnnxNode node)
    {
        var x = Get(node.Inputs[0]);
        var wT = Get(node.Inputs[1]);
        OnnxTensor? bT = node.Inputs.Length > 2 && node.Inputs[2].Length > 0 ? Get(node.Inputs[2]) : null;

        if (x.Dims.Length != 4)
            throw new NotSupportedException($"ConvTranspose 只支持 4 维输入，收到 {x.ShapeText()}");

        int n = x.Dims[0], cIn = x.Dims[1], h = x.Dims[2], wIn = x.Dims[3];
        int kcIn = wT.Dims[0], cPerGroup = wT.Dims[1], kh = wT.Dims[2], kw = wT.Dims[3];
        if (kcIn != cIn)
            throw new InvalidOperationException($"ConvTranspose 权重与输入通道数不符：{wT.ShapeText()} / {cIn}");

        long group = node.Int("group", 1);
        var strides = node.Ints("strides");
        var dil = node.Ints("dilations");
        var pads = node.Ints("pads");
        var outPad = node.Ints("output_padding");
        int sh = strides.Length > 0 ? (int)strides[0] : 1;
        int sw = strides.Length > 1 ? (int)strides[1] : 1;
        int dh = dil.Length > 0 ? (int)dil[0] : 1;
        int dw = dil.Length > 1 ? (int)dil[1] : 1;
        int pt = pads.Length > 0 ? (int)pads[0] : 0;
        int pl = pads.Length > 1 ? (int)pads[1] : 0;
        int pb = pads.Length > 2 ? (int)pads[2] : 0;
        int pr = pads.Length > 3 ? (int)pads[3] : 0;
        int oph = outPad.Length > 0 ? (int)outPad[0] : 0;
        int opw = outPad.Length > 1 ? (int)outPad[1] : 0;

        int m = cPerGroup * (int)group;
        int outH = (h - 1) * sh - pt - pb + dh * (kh - 1) + oph + 1;
        int outW = (wIn - 1) * sw - pl - pr + dw * (kw - 1) + opw + 1;
        int outChPerGroup = m / (int)group;

        var y = new float[n * m * outH * outW];
        var xd = x.F!;
        var wd = wT.F!;
        var bd = bT?.F;

        Parallel.For(0, n, ni =>
        {
            int xBase = ni * cIn * h * wIn;
            int yBase = ni * m * outH * outW;
            for (int ci = 0; ci < cIn; ci++)
            {
                int g = ci / (cIn / (int)group);
                int xPlane = xBase + ci * h * wIn;
                int wPlane = ci * cPerGroup * kh * kw;
                for (int ih = 0; ih < h; ih++)
                {
                    for (int iw = 0; iw < wIn; iw++)
                    {
                        float v = xd[xPlane + ih * wIn + iw];
                        if (v == 0f) continue;
                        for (int co = 0; co < cPerGroup; co++)
                        {
                            int mi = g * outChPerGroup + co;
                            int yPlane = yBase + mi * outH * outW;
                            int wK = wPlane + co * kh * kw;
                            for (int r = 0; r < kh; r++)
                            {
                                int oh = ih * sh - pt + r * dh;
                                if (oh < 0 || oh >= outH) continue;
                                int yRow = yPlane + oh * outW;
                                int wRow = wK + r * kw;
                                for (int c2 = 0; c2 < kw; c2++)
                                {
                                    int ow = iw * sw - pl + c2 * dw;
                                    if (ow < 0 || ow >= outW) continue;
                                    y[yRow + ow] += v * wd[wRow + c2];
                                }
                            }
                        }
                    }
                }
            }
            if (bd == null) return;
            for (int mi = 0; mi < m; mi++)
            {
                float bias = bd[mi];
                int yPlane = yBase + mi * outH * outW;
                for (int i = 0; i < outH * outW; i++) y[yPlane + i] += bias;
            }
        });

        return new OnnxTensor(OnnxDataType.Float, new[] { n, m, outH, outW }, y);
    }

    // ===================== 工具 =====================

    private long[] ReadInts(OnnxNode node, int inputIndex, string attrName)
    {
        if (node.Inputs.Length > inputIndex && node.Inputs[inputIndex].Length > 0)
            return Get(node.Inputs[inputIndex]).L!;
        return node.Ints(attrName);
    }
}

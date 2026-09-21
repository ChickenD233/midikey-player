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

    /// <summary>跑一遍图。input 的形状必须与模型声明的入口一致。</summary>
    public OnnxTensor Run(OnnxTensor input)
    {
        _values.Clear();
        foreach (var kv in _model.Initializers) _values[kv.Key] = kv.Value;
        _values[_model.InputName] = input;

        foreach (var node in _model.Nodes)
        {
            OnnxTensor result = Execute(node);
            if (node.Outputs.Length > 0 && node.Outputs[0].Length > 0)
                _values[node.Outputs[0]] = result;
        }

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

    private OnnxTensor Execute(OnnxNode node)
    {
        try
        {
            return ExecuteCore(node);
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
        var to = (OnnxDataType)node.Int("to", (long)OnnxDataType.Float);
        int n = x.Length;
        if (to == OnnxDataType.Float)
        {
            var f = new float[n];
            if (x.F != null) Array.Copy(x.F, f, n);
            else for (int i = 0; i < n; i++) f[i] = x.L![i];
            return new OnnxTensor(to, x.Dims, f);
        }
        var l = new long[n];
        if (x.L != null) Array.Copy(x.L, l, n);
        else for (int i = 0; i < n; i++) l[i] = (long)x.F![i];
        return new OnnxTensor(to, x.Dims, l);
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
            if (target[i] == 0) target[i] = i < x.Dims.Length ? x.Dims[i] : 1;   // allowzero 默认关
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
        var x = Get(node.Inputs[0]);
        long[] starts = ReadInts(node, 1, "starts");
        long[] ends = ReadInts(node, 2, "ends");
        long[]? axes = node.Inputs.Length > 3 && node.Inputs[3].Length > 0 ? Get(node.Inputs[3]).L : null;
        long[]? steps = node.Inputs.Length > 4 && node.Inputs[4].Length > 0 ? Get(node.Inputs[4]).L : null;

        int rank = x.Dims.Length;
        var begin = new int[rank];
        var step = new int[rank];
        var count = new int[rank];
        for (int i = 0; i < rank; i++) { begin[i] = 0; step[i] = 1; count[i] = x.Dims[i]; }

        for (int k = 0; k < starts.Length; k++)
        {
            int axis = axes != null ? (int)axes[k] : k;
            if (axis < 0) axis += rank;
            long st = steps != null ? steps[k] : 1;
            long start = starts[k];
            long end = ends[k];
            int dim = x.Dims[axis];

            if (st > 0)
            {
                if (start < 0) start += dim;
                if (end < 0) end += dim;
                start = Math.Clamp(start, 0, dim);
                end = Math.Clamp(end, 0, dim);
                begin[axis] = (int)start;
                step[axis] = (int)st;
                count[axis] = (int)Math.Max(0, (end - start + st - 1) / st);
            }
            else
            {
                // 负步长：起点落在 [0, dim-1]，终点落在 [-1, dim-1]
                if (start < 0) start += dim;
                if (end < 0) end += dim;
                start = Math.Clamp(start, -1, dim - 1);
                end = Math.Clamp(end, -1, dim - 1);
                long span = start - end;
                begin[axis] = (int)start;
                step[axis] = (int)st;
                count[axis] = span < 0 ? 0 : (int)(span / (-st) + 1);
            }
        }

        var outDims = (int[])count.Clone();
        var outT = AllocLike(x, outDims);
        var inStrides = OnnxTensor.Strides(x.Dims);
        var outStrides = OnnxTensor.Strides(outDims);
        var idx = new int[rank];
        int srcOff = 0;
        for (int i = 0; i < rank; i++) srcOff += begin[i] * inStrides[i];
        int total = outT.Length;
        for (int i = 0; i < total; i++)
        {
            CopyAt(x, srcOff, outT, i);
            for (int d = rank - 1; d >= 0; d--)
            {
                idx[d]++;
                srcOff += step[d] * inStrides[d];
                if (idx[d] < outDims[d]) break;
                idx[d] = 0;
                srcOff -= step[d] * inStrides[d] * outDims[d];
            }
        }
        return outT;
    }

    private OnnxTensor Pad(OnnxNode node)
    {
        var x = Get(node.Inputs[0]);
        long[] pads = ReadInts(node, 1, "pads");
        float constant = node.Inputs.Length > 2 && node.Inputs[2].Length > 0
            ? Get(node.Inputs[2]).ScalarFloat
            : 0f;
        string mode = node.Str("mode", "constant");

        int rank = x.Dims.Length;
        var begin = new int[rank];
        var endPad = new int[rank];
        for (int i = 0; i < rank; i++) begin[i] = (int)pads[i];
        for (int i = 0; i < rank; i++) endPad[i] = (int)pads[rank + i];

        var outDims = new int[rank];
        for (int i = 0; i < rank; i++) outDims[i] = x.Dims[i] + begin[i] + endPad[i];
        var outT = AllocLike(x, outDims);

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

    // ===================== 卷积 =====================

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
        int outChPerGroup = m / (int)group;

        var y = new float[n * m * outH * outW];
        var xd = x.F!;
        var wd = wT.F!;
        var bd = bT?.F;

        // 按「批 × 输出通道」并行：模型固定 batch=1，若按 batch 并行等于没并行。
        // 卷积占了整个推理的绝大部分时间，这一层并行是转换速度的关键。
        Parallel.For(0, n * m, idx =>
        {
            int ni = idx / m;
            int mi = idx % m;
            int g = mi / outChPerGroup;
            int cBase = g * cPerGroup;
            float bias = bd != null ? bd[mi] : 0f;
            for (int oh = 0; oh < outH; oh++)
            {
                int ihBase = oh * sh - pt;
                for (int ow = 0; ow < outW; ow++)
                {
                    int iwBase = ow * sw - pl;
                    float sum = bias;
                    for (int ci = 0; ci < cPerGroup; ci++)
                    {
                        int c = cBase + ci;
                        int xPlane = (ni * cIn + c) * h * wIn;
                        int wPlane = (mi * cPerGroup + ci) * kh * kw;
                        for (int r = 0; r < kh; r++)
                        {
                            int ih = ihBase + r * dh;
                            if (ih < 0 || ih >= h) continue;
                            int xRow = xPlane + ih * wIn;
                            int wRow = wPlane + r * kw;
                            for (int c2 = 0; c2 < kw; c2++)
                            {
                                int iw = iwBase + c2 * dw;
                                if (iw < 0 || iw >= wIn) continue;
                                sum += xd[xRow + iw] * wd[wRow + c2];
                            }
                        }
                    }
                    y[(ni * m + mi) * outH * outW + oh * outW + ow] = sum;
                }
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

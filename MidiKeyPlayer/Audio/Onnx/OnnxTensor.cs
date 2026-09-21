using System;
using System.Text;

namespace MidiKeyPlayer.Audio.Onnx;

/// <summary>
/// 一张张量：形状 + 数据。模型只用到 float 与整数（含 bool）两类，
/// 整数一律按 long 存，省去为每种整型各写一套算子。
/// </summary>
internal sealed class OnnxTensor
{
    public OnnxDataType Type { get; }
    public int[] Dims { get; }
    public float[]? F { get; }
    public long[]? L { get; }

    public OnnxTensor(OnnxDataType type, int[] dims)
    {
        Type = type;
        Dims = dims;
        int n = ElementCount(dims);
        if (type == OnnxDataType.Float) F = new float[n];
        else L = new long[n];
    }

    public OnnxTensor(OnnxDataType type, int[] dims, float[] data)
    {
        Type = type; Dims = dims; F = data;
    }

    public OnnxTensor(OnnxDataType type, int[] dims, long[] data)
    {
        Type = type; Dims = dims; L = data;
    }

    /// <summary>两个数据数组只给其中一个（另一个传 null）。</summary>
    public OnnxTensor(OnnxDataType type, int[] dims, float[]? data, long[]? dataLong)
    {
        Type = type; Dims = dims; F = data; L = dataLong;
    }

    public int Length => ElementCount(Dims);

    /// <summary>换一个形状、共用同一份数据（Reshape / Unsqueeze 这类不改数据的算子用）。</summary>
    public OnnxTensor WithDims(int[] dims)
    {
        if (ElementCount(dims) != Length)
            throw new InvalidOperationException($"换形状后元素数不符：{Length} → {ElementCount(dims)}");
        return new OnnxTensor(Type, dims, F!, L!);
    }

    public static int ElementCount(int[] dims)
    {
        // 标量（dims 为空）有 1 个元素，不是 0
        if (dims.Length == 0) return 1;
        int n = 1;
        for (int i = 0; i < dims.Length; i++) n *= dims[i];
        return n;
    }

    public static int[] Strides(int[] dims)
    {
        var s = new int[dims.Length];
        int acc = 1;
        for (int i = dims.Length - 1; i >= 0; i--) { s[i] = acc; acc *= dims[i]; }
        return s;
    }

    /// <summary>按索引取浮点值（越界不检查，热路径用）。</summary>
    public float At(int flat) => F![flat];

    public long ScalarLong => L![0];
    public float ScalarFloat => F![0];

    public string ShapeText()
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < Dims.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Dims[i]);
        }
        return sb.Append(']').ToString();
    }

    /// <summary>把数据平铺到目标数组（形状必须一致）。校验用。</summary>
    public float[] ToFloatArray() => F ?? throw new InvalidOperationException($"张量类型是 {Type}，不是 float");
}

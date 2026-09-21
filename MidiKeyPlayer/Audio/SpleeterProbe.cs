using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using MidiKeyPlayer.Audio.Onnx;

namespace MidiKeyPlayer.Audio;

/// <summary>
///【开发用，可删】分离模型的探针：把 vocals / accompaniment 两个 ONNX 文件的入口形状、
/// 出口形状、算子清单与自写执行器能不能跑通一次算出来。
///
/// 用法：设 <c>MIDIKEY_SPLEETER_PROBE=&lt;含模型的目录&gt;</c> 启动程序。
/// 目录里放 <c>vocals.fp16.onnx</c> 与 <c>accompaniment.fp16.onnx</c>（或非量化版）。
/// 可选 <c>MIDIKEY_SPLEETER_PROBE_AUDIO=&lt;音频路径&gt;</c>：真跑一遍推理并打印输出统计。
/// 结果写 <c>MIDIKEY_SPLEETER_PROBE_OUT</c>（默认 %TEMP%\midikey-spleeter-probe.txt）。
/// </summary>
internal static class SpleeterProbe
{
    public const string EnvVar = "MIDIKEY_SPLEETER_PROBE";

    public static bool Requested => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVar));

    private static readonly StringBuilder Log = new();

    private static void Say(string line)
    {
        Log.AppendLine(line);
        try { Console.WriteLine(line); } catch { }
    }

    public static int Run()
    {
        string dir = Environment.GetEnvironmentVariable(EnvVar) ?? "";
        string audio = Environment.GetEnvironmentVariable("MIDIKEY_SPLEETER_PROBE_AUDIO") ?? "";
        string outPath = Environment.GetEnvironmentVariable("MIDIKEY_SPLEETER_PROBE_OUT") ?? "";
        if (outPath.Length == 0)
            outPath = Path.Combine(Path.GetTempPath(), "midikey-spleeter-probe.txt");

        Say($"分离模型探针 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Say($"目录：{dir}");
        Say("");

        // 单算子核对模式不读音频，直接跑
        if (Environment.GetEnvironmentVariable("MIDIKEY_SPLEETER_PROBE_BN") is { Length: > 0 }
            || Environment.GetEnvironmentVariable("MIDIKEY_SPLEETER_PROBE_FP16") is { Length: > 0 }
            || Environment.GetEnvironmentVariable("MIDIKEY_SPLEETER_PROBE_CT") is { Length: > 0 })
        {
            RunSpecialChecks(outPath);
            return 0;
        }


        // 只跑指定的一支模型：MIDIKEY_SPLEETER_PROBE_ONLY=vocals / accompaniment
        string only = Environment.GetEnvironmentVariable("MIDIKEY_SPLEETER_PROBE_ONLY") ?? "";
        foreach (string name in new[] { "vocals", "accompaniment" })
        {
            if (only.Length > 0 && !string.Equals(only, name, StringComparison.OrdinalIgnoreCase)) continue;
            string file = FindModel(dir, name);
            if (file.Length == 0) { Say($"[{name}] 找不到模型文件"); continue; }
            Inspect(name, file, audio);
            Say("");
        }

        try { File.WriteAllText(outPath, Log.ToString(), new UTF8Encoding(false)); } catch { }
        Say($"报告：{outPath}");
        return 0;
    }

    /// <summary>
    /// 核对一个卷积算子：在内存里拼一个只有一个节点的图，
    /// 输入/权重/偏置从 &lt;前缀&gt;_x.f32 / _w.f32 / _b.f32 读，
    /// 形状与步长/填充从 &lt;前缀&gt;_meta.txt 读（首行：算子 输入形状 权重形状 步长 填充），
    /// 输出与前缀 _y.f32 逐元素比。
    /// </summary>
    private static void ConvCheck(string prefix, string metaSuffix, bool withBn, string? opOverride = null)
    {
        string metaPath = prefix + metaSuffix;
        if (!File.Exists(metaPath)) return;
        string[] parts = File.ReadAllText(metaPath).Trim().Split(' ');
        if (parts.Length < 6) return;
        string opType = opOverride ?? parts[0];
        if (parts[0] != opType && opOverride == null) return;

        int[] inShape = Array.ConvertAll(parts[1].Split(','), int.Parse);
        int[] wShape = Array.ConvertAll(parts[2].Split(','), int.Parse);
        long[] strides = Array.ConvertAll(parts[3].Split(','), long.Parse);
        long[] pads = Array.ConvertAll(parts[4].Split(','), long.Parse);
        long[] dil = Array.ConvertAll(parts[5].Split(','), long.Parse);
        int cout = opType == "Conv" ? wShape[0] : wShape[1];

        var x = ReadFloats(prefix + "_x.f32");
        var w = ReadFloats(prefix + "_w.f32");
        var b = ReadFloats(prefix + "_b.f32");
        var expect = ReadFloats(prefix + (withBn ? "_y.f32" : "_y.f32"));
        if (x.Length == 0 || w.Length == 0 || expect.Length == 0)
        {
            Say($"{opType}{(withBn ? " + BN" : "")} 核对：输入文件不全（缺 {prefix}_x.f32 之类）");
            return;
        }

        var model = new OnnxModel();
        var conv = new OnnxNode { OpType = opType };
        conv.Inputs = withBn ? new[] { "x", "w", "b" } : new[] { "x", "w", "b" };
        conv.Outputs = new[] { withBn ? "c" : "y" };
        conv.Attributes["strides"] = strides;
        conv.Attributes["pads"] = pads;
        conv.Attributes["dilations"] = dil;
        model.Nodes.Add(conv);
        model.Initializers["w"] = new OnnxTensor(OnnxDataType.Float, wShape, w);
        model.Initializers["b"] = new OnnxTensor(OnnxDataType.Float, new[] { cout }, b);

        if (withBn)
        {
            var bn = new OnnxNode { OpType = "BatchNormalization" };
            bn.Inputs = new[] { "c", "s", "bb", "mu", "va" };
            bn.Outputs = new[] { "y" };
            model.Nodes.Add(bn);
            model.Initializers["s"] = new OnnxTensor(OnnxDataType.Float, new[] { cout }, ReadFloats(prefix + "_s.f32"));
            model.Initializers["bb"] = new OnnxTensor(OnnxDataType.Float, new[] { cout }, ReadFloats(prefix + "_bb.f32"));
            model.Initializers["mu"] = new OnnxTensor(OnnxDataType.Float, new[] { cout }, ReadFloats(prefix + "_mu.f32"));
            model.Initializers["va"] = new OnnxTensor(OnnxDataType.Float, new[] { cout }, ReadFloats(prefix + "_va.f32"));
        }

        model.InputName = "x";
        model.OutputNames = new[] { "y" };

        var runner = new OnnxGraphRunner(model);
        var input = new OnnxTensor(OnnxDataType.Float, inShape, x);
        OnnxTensor output;
        try { output = runner.Run(input); }
        catch (Exception ex) { Say($"{opType}{(withBn ? " + BN" : "")} 核对：跑不通 {ex.Message}"); return; }

        double maxAbs = 0, sumAbs = 0;
        int n = Math.Min(expect.Length, output.F!.Length);
        for (int i = 0; i < n; i++)
        {
            double d = Math.Abs(output.F[i] - expect[i]);
            sumAbs += d;
            if (d > maxAbs) maxAbs = d;
        }
        Say($"{opType}{(withBn ? " + BN" : "")} 核对：形状 {output.ShapeText()}（参考 {expect.Length} 个数）");
        Say($"  maxAbs={maxAbs:E3} meanAbs={(n > 0 ? sumAbs / n : 0):E3}");
        Say($"  本实现头8：{string.Join(",", output.F.Take(8).Select(v => v.ToString("G5")))}");
        Say($"  参考头8  ：{string.Join(",", expect.Take(8).Select(v => v.ToString("G5")))}");
    }

    private static float[] ReadFloats(string path)
    {
        if (!File.Exists(path)) return Array.Empty<float>();
        var raw = File.ReadAllBytes(path);
        var f = new float[raw.Length / 4];
        Buffer.BlockCopy(raw, 0, f, 0, f.Length * 4);
        return f;
    }

    private static byte[] Floats(float[] data)
    {
        var b = new byte[data.Length * 4];
        Buffer.BlockCopy(data, 0, b, 0, b.Length);
        return b;
    }

    /// <summary>fp16 解码（与 OnnxModel.FromHalf 同一口径，供核对用）。</summary>
    private static float HalfToFloat(ushort h)
    {
        int sign = (h >> 15) & 1;
        int exp = (h >> 10) & 0x1F;
        int mant = h & 0x3FF;
        double value;
        if (exp == 0) value = mant * Math.Pow(2, -24);
        else if (exp == 31) value = mant == 0 ? double.PositiveInfinity : double.NaN;
        else value = (1.0 + mant / 1024.0) * Math.Pow(2, exp - 15);
        return (float)(sign == 1 ? -value : value);
    }

    /// <summary>单算子核对模式的入口：批归一 / fp16 解码 / 卷积。</summary>
    private static void RunSpecialChecks(string outPath)
    {
        BatchNormCheck();
        Fp16Check();
        ConvCheckMode();
        try { File.WriteAllText(outPath, Log.ToString(), new UTF8Encoding(false)); } catch { }
        Say($"报告：{outPath}");
    }

    private static void BatchNormCheck()
    {
        string bnPrefix = Environment.GetEnvironmentVariable("MIDIKEY_SPLEETER_PROBE_BN") ?? "";
        if (bnPrefix.Length == 0) return;
        var bx = ReadFloats(bnPrefix + "_x.f32");
        var bs = ReadFloats(bnPrefix + "_weight.f32");
        var bbb = ReadFloats(bnPrefix + "_bias.f32");
        var bmu = ReadFloats(bnPrefix + "_running_mean.f32");
        var bva = ReadFloats(bnPrefix + "_running_var.f32");
        var by = ReadFloats(bnPrefix + "_y.f32");
        if (bx.Length == 0 || bs.Length == 0)
        {
            Say($"单独批归一核对：文件不全（前缀 {bnPrefix}）");
            return;
        }
        int c = bs.Length;
        int n = bx.Length;
        var y = new float[n];
        for (int i = 0; i < n; i++)
        {
            int ch = (i / Math.Max(1, n / c)) % c;
            y[i] = bs[ch] * (bx[i] - bmu[ch]) / MathF.Sqrt(bva[ch] + 1e-3f) + bbb[ch];
        }
        double maxAbs = 0, sumAbs = 0;
        for (int i = 0; i < Math.Min(n, by.Length); i++)
        {
            double d = Math.Abs(y[i] - by[i]);
            sumAbs += d;
            if (d > maxAbs) maxAbs = d;
        }
        double my = 0, ry = 0;
        foreach (float v in y) my += v;
        foreach (float v in by) ry += v;
        Say($"单独批归一核对：n={n} 本均值={(n > 0 ? my / n : 0):G6} 参考均值={(by.Length > 0 ? ry / by.Length : 0):G6}");
        Say($"  maxAbs={maxAbs:E3} meanAbs={(n > 0 ? sumAbs / n : 0):E3}");
        Say($"  本头4：{string.Join(",", y.Take(4).Select(v => v.ToString("G6")))}");
        Say($"  参考头4：{string.Join(",", by.Take(4).Select(v => v.ToString("G6")))}");
    }

    private static void Fp16Check()
    {
        string fp16 = Environment.GetEnvironmentVariable("MIDIKEY_SPLEETER_PROBE_FP16") ?? "";
        if (fp16.Length == 0) return;
        var raw = File.ReadAllBytes(fp16 + "_in.f16");
        var f = new float[raw.Length / 2];
        for (int i = 0; i < f.Length; i++)
        {
            ushort h = (ushort)(raw[i * 2] | (raw[i * 2 + 1] << 8));
            f[i] = HalfToFloat(h);
        }
        File.WriteAllBytes(fp16 + "_out.f32", Floats(f));
        Say($"fp16 解码核对：读 {f.Length} 个，写出 {fp16}_out.f32");
    }

    private static void ConvCheckMode()
    {
        string ct = Environment.GetEnvironmentVariable("MIDIKEY_SPLEETER_PROBE_CT") ?? "";
        if (ct.Length == 0) return;
        ConvCheck(ct, "_meta.txt", withBn: false);
        ConvCheck(ct, "_bn_meta.txt", withBn: true);
        ConvCheck(ct, "_meta.txt", withBn: false, opOverride: "ConvTranspose");
    }

    private static string FindModel(string dir, string stem)
    {
        foreach (string n in new[] { stem + ".fp16.onnx", stem + ".onnx", stem + "_fp16.onnx" })
        {
            string p = Path.Combine(dir, n);
            if (File.Exists(p)) return p;
        }
        return "";
    }

    private static void Inspect(string label, string file, string? audioPath)
    {
        Say($"[{label}] {Path.GetFileName(file)}（{new FileInfo(file).Length / 1024.0 / 1024.0:F1} MB）");
        var sw = Stopwatch.StartNew();
        OnnxModel model;
        try
        {
            model = OnnxModel.Parse(File.ReadAllBytes(file));
        }
        catch (Exception ex)
        {
            Say($"  解析失败：{ex.GetType().Name}: {ex.Message}");
            return;
        }
        Say($"  解析耗时 {sw.ElapsedMilliseconds} ms；节点 {model.Nodes.Count} 个；初始值 {model.Initializers.Count} 个");

        foreach (var kv in model.InputShapes) Say($"  输入 {kv.Key}{Shape(kv.Value)}");
        for (int i = 0; i < model.OutputNames.Length; i++)
            Say($"  输出 {model.OutputNames[i]}{(i < model.OutputShapes.Count ? Shape(model.OutputShapes[i]) : "")}");
        var meta = new List<string>();
        foreach (var kv in model.Metadata) meta.Add($"{kv.Key}={kv.Value}");
        meta.Sort(StringComparer.Ordinal);
        Say("  元数据：" + (meta.Count == 0 ? "（无）" : string.Join(" ", meta)));

        var ops = new Dictionary<string, int>(StringComparer.Ordinal);
        var unsupported = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var n in model.Nodes)
        {
            ops.TryGetValue(n.OpType, out int c);
            ops[n.OpType] = c + 1;
            if (!Supported(n.OpType)) unsupported.Add(n.OpType);
        }
        var list = new List<string>();
        foreach (var kv in ops) list.Add($"{kv.Key}×{kv.Value}");
        list.Sort(StringComparer.Ordinal);
        Say("  算子：" + string.Join(" ", list));
        Say("  执行器不支持的算子：" + (unsupported.Count == 0 ? "（无）" : string.Join(" ", unsupported)));

        // 前 12 个节点：看输入是不是原始波形（Conv 直接吃 audio 的话形状就是 [N, C, T]）
        Say("  节点摘录：");
        for (int i = 0; i < Math.Min(12, model.Nodes.Count); i++)
        {
            var n = model.Nodes[i];
            Say($"    {i}: {n.OpType} 入[{string.Join(",", n.Inputs)}] 出[{string.Join(",", n.Outputs)}]");
        }

        // 卷积类权重形状：判定输入输出通道与 kernel 布局（Conv 与 ConvTranspose 的轴序相反）
        foreach (var n in model.Nodes)
        {
            if (n.OpType != "Conv" && n.OpType != "ConvTranspose") continue;
            string wName = n.Inputs.Length > 1 ? n.Inputs[1] : "";
            string wShape = model.Initializers.TryGetValue(wName, out var w) ? w.ShapeText() : "(不是初始值)";
            string a = $"strides[{string.Join(",", n.Ints("strides"))}] pads[{string.Join(",", n.Ints("pads"))}]" +
                       $" dil[{string.Join(",", n.Ints("dilations"))}] outpad[{string.Join(",", n.Ints("output_padding"))}]" +
                       $" group={n.Int("group", 1)}";
            Say($"  {n.OpType} {n.Outputs[0]} 权重 {wName}{wShape} {a}");
        }

        // 前几个 Constant：形状类常量能看出图里用的是哪套维度口径
        int shown = 0;
        foreach (var n in model.Nodes)
        {
            if (n.OpType != "Constant" || shown >= 6) continue;
            var t = n.Tensor("value");
            string text = t == null ? "(无 value)" : $"{t.Type} {t.ShapeText()}";
            if (t?.L != null && t.L.Length <= 8) text += " = [" + string.Join(",", t.L) + "]";
            else if (t?.F != null && t.F.Length <= 8) text += " = [" + string.Join(",", t.F) + "]";
            if (t == null)
            {
                var keys = new List<string>();
                foreach (var kv in n.Attributes)
                    keys.Add($"{kv.Key}:{kv.Value?.GetType().Name ?? "null"}");
                text += " 属性[" + string.Join(",", keys) + "]";
            }
            Say($"  Constant {n.Outputs[0]} {text}");
            shown++;
        }

        // 切片类节点：starts / ends 往往来自 Constant，打出来才看得出切片口径
        foreach (var n in model.Nodes)
        {
            if (n.OpType != "Slice") continue;
            string Desc(int i)
            {
                if (n.Inputs.Length <= i || n.Inputs[i].Length == 0) return "(缺)";
                string nm = n.Inputs[i];
                if (model.Initializers.TryGetValue(nm, out var t))
                {
                    if (t.L != null) return $"{nm}{t.ShapeText()}={string.Join(",", t.L)}";
                    if (t.F != null) return $"{nm}{t.ShapeText()}={string.Join(",", t.F)}";
                    return $"{nm}{t.ShapeText()}";
                }
                return nm + "(非常量)";
            }
            Say($"  Slice 入[{string.Join(",", n.Inputs)}] axes={Desc(3)} starts={Desc(1)} ends={Desc(2)} steps={Desc(4)}");
        }

        Say("  与入口相连的节点：");
        int linked = 0;
        foreach (var n in model.Nodes)
        {
            if (linked >= 8) break;
            bool hit = false;
            foreach (string i in n.Inputs) if (i == model.InputName) hit = true;
            if (!hit) continue;
            string attrs = n.OpType == "Concat" ? $" axis={n.Int("axis")}"
                : n.OpType == "Slice" ? $" starts[{string.Join(",", n.Ints("starts"))}] ends[{string.Join(",", n.Ints("ends"))}]"
                : "";
            Say($"    {n.OpType} 入[{string.Join(",", n.Inputs)}] 出[{string.Join(",", n.Outputs)}]{attrs}");
            linked++;
        }
        foreach (string on in model.OutputNames)
            Say($"  输出节点：{on}");

        // 有音频、或者给了固定输入文件，才真跑一遍
        string inFile = Environment.GetEnvironmentVariable("MIDIKEY_SPLEETER_PROBE_IN") ?? "";
        if (audioPath == null && !(inFile.Length > 0 && File.Exists(inFile))) return;
        RunOnce(label, model, audioPath ?? "");
    }

    private static void RunOnce(string label, OnnxModel model, string audioPath)
    {
        try
        {
            float[] samples = Array.Empty<float>();
            if (audioPath.Length > 0)
            {
                var (decoded, rate, channels) = AudioDecoder.Decode(audioPath);
                samples = decoded;
                Say($"  音频：{Path.GetFileName(audioPath)} {rate} Hz {channels} 声道 {decoded.Length / Math.Max(1, channels)} 帧");
            }
            var runner = new OnnxGraphRunner(model);
            // 诊断开关：强制串行（判断并行分块有没有引入错误）
            OnnxGraphRunner.SerialOnly = Environment.GetEnvironmentVariable("MIDIKEY_SPLEETER_PROBE_SERIAL") == "1";
            // 想看的中间张量：MIDIKEY_SPLEETER_PROBE_DUMP 里给名字（逗号分隔）
            string dumpNames = Environment.GetEnvironmentVariable("MIDIKEY_SPLEETER_PROBE_DUMP") ?? "";
            foreach (string dn in dumpNames.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                runner.DumpValues[dn] = dn;
            var trace = new List<string>();
            runner.Trace = (op, shape) =>
            {
                if (trace.Count < 400) trace.Add($"{op} {shape}");
            };

            // 按模型声明的入口形状喂：动态维（0）按音频长度补；通道不足就复制
            // Spleeter 的入口是 [声道, 帧数/512, 512(时间), 1024(频点)]，图里没声明形状就用这个已知口径
            var shapes = new List<int[]>();
            foreach (var kv in model.InputShapes)
            {
                var dims = (int[])kv.Value.Clone();
                if (dims.Length == 0) { shapes.Add(new[] { 2, 2, 512, 1024 }); continue; }
                for (int i = 0; i < dims.Length; i++) if (dims[i] == 0) dims[i] = 1;
                shapes.Add(dims);
            }
            if (shapes.Count == 0) shapes.Add(new[] { 2, 2, 512, 1024 });

            // 可选：从文件读固定输入（MIDIKEY_SPLEETER_PROBE_IN=<float32 原始数据>），
            // 并与参考输出（_REF=<float32 原始数据>）逐元素比对。用来核对执行器的数值正确性。
            string inFile = Environment.GetEnvironmentVariable("MIDIKEY_SPLEETER_PROBE_IN") ?? "";
            if (inFile.Length > 0 && File.Exists(inFile))
            {
                shapes.Clear();
                shapes.Add(new[] { 2, 2, 512, 1024 });
            }

            foreach (int[] dims in shapes)
            {
                Say($"  试跑形状 {Shape(dims)}");
                int want = 1;
                foreach (int d in dims) want *= d;
                var data = new float[want];
                string inFile2 = Environment.GetEnvironmentVariable("MIDIKEY_SPLEETER_PROBE_IN") ?? "";
                if (inFile2.Length > 0 && File.Exists(inFile2))
                {
                    var raw = File.ReadAllBytes(inFile2);
                    Buffer.BlockCopy(raw, 0, data, 0, Math.Min(raw.Length, want * 4));
                    Say($"  输入来自文件 {Path.GetFileName(inFile2)}（{raw.Length / 4} 个 float）");
                }
                else
                {
                    for (int i = 0; i < want && i < samples.Length; i++) data[i] = samples[i];
                }
                var input = new OnnxTensor(OnnxDataType.Float, dims, data);
                var sw = Stopwatch.StartNew();
                try
                {
                    var outputs = runner.RunAll(input);
                    Say($"  跑通：{sw.ElapsedMilliseconds} ms；输出 {outputs.Count} 个");
                    foreach (var o in outputs)
                    {
                        float min = float.MaxValue, max = float.MinValue;
                        double sum = 0;
                        foreach (float v in o.F!)
                        {
                            if (v < min) min = v;
                            if (v > max) max = v;
                            sum += v;
                        }
                        double mean = o.Length > 0 ? sum / o.Length : 0;
                        Say($"    {o.ShapeText()} min={min:F4} max={max:F4} mean={mean:F5}");

                        // 有参考输出就逐元素比：maxAbs 是最大绝对差，meanAbs 是平均绝对差
                        string refFile = Environment.GetEnvironmentVariable("MIDIKEY_SPLEETER_PROBE_REF") ?? "";
                        if (refFile.Length > 0 && File.Exists(refFile) && o.F != null)
                        {
                            var raw = File.ReadAllBytes(refFile);
                            int n = Math.Min(raw.Length / 4, o.F.Length);
                            var refF = new float[n];
                            Buffer.BlockCopy(raw, 0, refF, 0, n * 4);
                            double maxAbs = 0, sumAbs = 0;
                            int worst = -1;
                            for (int i = 0; i < n; i++)
                            {
                                double d = Math.Abs(o.F[i] - refF[i]);
                                sumAbs += d;
                                if (d > maxAbs) { maxAbs = d; worst = i; }
                            }
                            Say($"    与参考比：n={n} maxAbs={maxAbs:E3} meanAbs={(n > 0 ? sumAbs / n : 0):E3}" +
                                (worst >= 0 ? $" 最大差在 {worst}：本 {o.F[worst]:F6} / 参考 {refF[worst]:F6}" : ""));
                            if (o.F.Length > 0)
                                Say($"    本实现头8：{string.Join(",", o.F.Take(8).Select(v => v.ToString("G6")))}" +
                                    $" / 参考头8：{string.Join(",", refF.Take(8).Select(v => v.ToString("G6")))}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    var parts = new List<string>();
                    Exception? e = ex;
                    while (e != null) { parts.Add($"{e.GetType().Name}: {e.Message}"); e = e.InnerException; }
                    Say("  跑不通：" + string.Join("  <-  ", parts));
                    Say("  执行轨迹（节点类型+输出形状，前 40 个）：");
                    for (int i = 0; i < Math.Min(40, trace.Count); i++) Say($"    {i}: {trace[i]}");
                }
                // 诊断：把指定名字的张量值打出来（成功失败都打，失败了才最需要看）
                foreach (var kv in runner.DumpValues)
                    Say($"  张量 {kv.Key} {kv.Value}");
            }
        }
        catch (Exception ex)
        {
            Say($"  音频处理失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string Shape(int[] dims) => "[" + string.Join(",", dims) + "]";

    private static bool Supported(string op) => op switch
    {
        "Conv" or "ConvTranspose" or "BatchNormalization" or "Identity" or "LeakyRelu"
            or "Constant" or "ConstantOfShape"
            or "Reshape" or "Unsqueeze" or "Transpose" or "Concat" or "Slice" or "Pad"
            or "Neg" or "Relu" or "Sigmoid" or "Sqrt" or "Log"
            or "Mul" or "Add" or "Sub" or "Div" or "Equal" or "Where" or "Shape" or "Cast"
            or "ReduceSum" or "ReduceMin" or "ReduceMax" => true,
        _ => false,
    };
}

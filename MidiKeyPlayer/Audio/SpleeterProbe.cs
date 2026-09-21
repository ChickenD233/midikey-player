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

        foreach (string name in new[] { "vocals", "accompaniment" })
        {
            string file = FindModel(dir, name);
            if (file.Length == 0) { Say($"[{name}] 找不到模型文件"); continue; }
            Inspect(name, file, audio.Length > 0 ? audio : null);
            Say("");
        }

        try { File.WriteAllText(outPath, Log.ToString(), new UTF8Encoding(false)); } catch { }
        Say($"报告：{outPath}");
        return 0;
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

        if (audioPath == null) return;
        RunOnce(label, model, audioPath);
    }

    private static void RunOnce(string label, OnnxModel model, string audioPath)
    {
        try
        {
            var (samples, rate, channels) = AudioDecoder.Decode(audioPath);
            Say($"  音频：{Path.GetFileName(audioPath)} {rate} Hz {channels} 声道 {samples.Length / Math.Max(1, channels)} 帧");
            var runner = new OnnxGraphRunner(model);
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
                if (dims.Length == 0) { shapes.Add(new[] { 2, 1, 512, 1024 }); continue; }
                for (int i = 0; i < dims.Length; i++) if (dims[i] == 0) dims[i] = 1;
                shapes.Add(dims);
            }
            if (shapes.Count == 0) shapes.Add(new[] { 2, 1, 512, 1024 });

            foreach (int[] dims in shapes)
            {
                Say($"  试跑形状 {Shape(dims)}");
                int want = 1;
                foreach (int d in dims) want *= d;
                var data = new float[want];
                for (int i = 0; i < want && i < samples.Length; i++) data[i] = samples[i];
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

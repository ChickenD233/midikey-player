using System.Globalization;
using System.Text;
using MidiKeyPlayer.Input;
using MidiKeyPlayer.Midi;

namespace MidiKeyPlayer.Engine;

/// <summary>
/// 把演奏事件表导出成外部工具能用的按键脚本。
/// 与「实际演奏」共用同一套调度（<see cref="PlaybackEngine.BuildSchedulePreview"/>），导出结果与播放时发出的按键完全一致。
/// 按键名按当前键位方案（<see cref="KeymapProfile.Current"/>）来，键表换了导出的键名跟着换。
/// 传进来的音符已经映射过：没有对应键的音在 <see cref="NoteMapper"/> 里就被跳过了，
/// 这里只按方案里的修饰键开关决定要不要写八度键与升半音键，不做任何改音高的处理。
/// 支持 LogitechGHub（罗技 G HUB 的 Lua）与 KeystrokeCsv（通用 CSV 时刻/动作/按键）。
/// 雷蛇 Synapse 宏是私有格式、无官方规范，硬编很容易导入失败，故不提供一键导入，建议用 CSV 或宏录制。
/// </summary>
public static class MacroExporter
{
    /// <summary>导出格式。</summary>
    public enum Format { LogitechGHub, KeystrokeCsv }

    public static string Extension(Format f) => f switch
    {
        Format.LogitechGHub => ".lua",
        _ => ".csv"
    };

    /// <summary>
    /// 由映射后的音符生成按键脚本。notes 可含超音域音（会自动跳过）；
    /// speed 用于把音乐时间换算成实际播放时刻；timing 决定修饰键提前量，需与实际演奏一致。
    /// </summary>
    public static string Build(IReadOnlyList<MappedNote> notes, Format format,
                               double speed = 1.0, InputTiming? timing = null,
                               string songName = "")
    {
        var events = PlaybackEngine.BuildSchedulePreview(notes, timing, speed);
        return format == Format.LogitechGHub
            ? BuildLua(events, songName, speed)
            : BuildCsv(events);
    }

    // ---------------------------------------------------------------- 公共：按键名映射

    /// <summary>字符形式的按键 → 真实键名（命名键的哨兵字符在这里还原）。</summary>
    private static string KeyNameOf(char c) => InputSender.KeyNameOf(c);

    /// <summary>
    /// 键名 → 罗技 G HUB Lua 的按键名。
    /// G HUB 用 "a".."z"、"0".."9" 这类名字，标点与功能键用固定单词（comma、lshift…）。
    /// </summary>
    private static string LuaKeyName(string name) => name switch
    {
        "," => "comma",
        "." => "period",
        "/" => "slash",
        ";" => "semicolon",
        "'" => "quote",
        "[" => "lbracket",
        "]" => "rbracket",
        "\\" => "backslash",
        "-" => "minus",
        "=" => "equal",
        "`" => "grave",
        "Space" => "spacebar",
        "Enter" => "enter",
        "Tab" => "tab",
        "Back" => "backspace",
        "Escape" => "escape",
        "Shift" => "lshift",
        "Ctrl" => "lctrl",
        "Alt" => "lalt",
        "PageUp" => "pageup",
        "PageDown" => "pagedown",
        "Home" => "home",
        "End" => "end",
        "Insert" => "insert",
        "Delete" => "delete",
        "Up" => "up",
        "Down" => "down",
        "Left" => "left",
        "Right" => "right",
        _ => name.ToLowerInvariant(),     // 字母、数字、F1..F12、NumPad0..9 直接小写
    };

    private static string MouseName(string kind) => kind switch
    {
        "mouse-left" => "左键",
        "mouse-right" => "右键",
        _ => "中键"
    };

    // ---------------------------------------------------------------- G HUB Lua

    /// <summary>
    /// 罗技 G HUB 的 Lua 脚本。
    /// G HUB 官方脚本 API：PressKey / ReleaseKey / PressMouseButton / ReleaseMouseButton / Sleep。
    /// 按键名用字符串（"z"…"comma"）；鼠标键用 1=左、2=右、3=中。
    /// </summary>
    private static string BuildLua(IReadOnlyList<PlaybackEngine.ScheduledEvent> events,
                                   string songName, double speed)
    {
        var sb = new StringBuilder();
        sb.AppendLine("-- MIDI 按键播放器 导出的按键脚本（罗技 G HUB）");
        sb.AppendLine("-- 用法：G HUB → 选中设备 → 「应用程序」→ 添加应用程序 → 编写脚本 → 编辑脚本");
        sb.AppendLine("--       把本文件内容整段粘贴进去，保存；在目标程序里按下你绑定的脚本触发键即可播放。");
        sb.AppendLine("-- 注意：G HUB 的 Lua 环境不保证暴露鼠标中键（3）。若脚本里出现鼠标中键而无效，");
        sb.AppendLine("--       请把「升半音」改用键盘键，或改用 CSV 形式交给其它工具。");
        sb.AppendLine($"-- 键位方案：{KeymapProfile.Current.Name}");
        if (!string.IsNullOrWhiteSpace(songName))
            sb.AppendLine($"-- 曲目：{songName}");
        sb.AppendLine($"-- 速度：{speed * 100:F0}%（导出的时刻已按此速度换算）");
        sb.AppendLine($"-- 事件数：{events.Count}");
        sb.AppendLine();
        sb.AppendLine("local events = {");

        // G HUB 的 Sleep 只取整数毫秒：直接写 0.1ms 精度的浮点会被逐个截断，
        // 整首累积可达 1–2s 漂移。改为「误差进位」量化：
        // 每段等待 = round(累计毫秒) − 上一段的取整值，总漂移恒小于 1ms。
        long lastMsInt = 0;
        foreach (var e in events)
        {
            long msInt = (long)Math.Round(e.MusicTime * 1000.0);
            long wait = msInt - lastMsInt;
            if (wait < 0) wait = 0;
            lastMsInt = msInt;

            string name = KeyNameOf(e.Key);
            string action;
            if (e.Kind == "key")
            {
                // 方案里的音乐键也可以是鼠标键（MouseLeft 等）：那种要写鼠标按下，不能写 PressKey
                if (InputSender.TryMouseButton(name, out var button))
                    action = e.Down ? $"PressMouseButton({MouseButtonNo(button)})"
                                    : $"ReleaseMouseButton({MouseButtonNo(button)})";
                else
                    action = e.Down ? $"PressKey(\"{LuaKeyName(name)}\")"
                                    : $"ReleaseKey(\"{LuaKeyName(name)}\")";
            }
            else
            {
                action = e.Down ? $"PressMouseButton({MouseButtonNo(e.Kind)})"
                                : $"ReleaseMouseButton({MouseButtonNo(e.Kind)})";
            }

            sb.AppendLine($"  {{{wait}, function() {action} end}},");
        }

        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("-- 触发方式：在 G HUB 里把本脚本绑定到某个 G 键或鼠标键，");
        sb.AppendLine("-- 按下它就开始演奏（建议单独绑一个不常用的键，避免和目标程序操作冲突）。");
        sb.AppendLine("function OnEvent(event, arg)");
        sb.AppendLine("  if event == \"G_PRESSED\" or event == \"MOUSE_BUTTON_PRESSED\" then");
        sb.AppendLine("    for i = 1, #events do");
        sb.AppendLine("      Sleep(events[i][1])");
        sb.AppendLine("      events[i][2]()");
        sb.AppendLine("    end");
        sb.AppendLine("  end");
        sb.AppendLine("end");
        return sb.ToString();
    }

    private static int MouseButtonNo(string kind) => kind switch
    {
        "mouse-left" => 1,
        "mouse-right" => 2,
        _ => 3
    };

    private static int MouseButtonNo(InputSender.MouseButton button) => button switch
    {
        InputSender.MouseButton.Left => 1,
        InputSender.MouseButton.Right => 2,
        _ => 3
    };

    /// <summary>鼠标键的中文名。</summary>
    private static string MouseName(InputSender.MouseButton button) => button switch
    {
        InputSender.MouseButton.Left => "左键",
        InputSender.MouseButton.Right => "右键",
        _ => "中键"
    };

    /// <summary>键名 → CSV 里的目标名：鼠标键写中文，其余写键名本身。</summary>
    private static string MouseOrKeyName(string name)
        => InputSender.TryMouseButton(name, out var button) ? MouseName(button) : name;

    // ---------------------------------------------------------------- CSV

    private static string BuildCsv(IReadOnlyList<PlaybackEngine.ScheduledEvent> events)
    {
        var sb = new StringBuilder();
        sb.AppendLine("time_ms,action,target,note");
        foreach (var e in events)
        {
            string target = e.Kind == "key" ? MouseOrKeyName(KeyNameOf(e.Key)) : MouseName(e.Kind);
            string action = e.Down ? "down" : "up";
            sb.AppendLine($"{(e.MusicTime * 1000.0).ToString("F1", CultureInfo.InvariantCulture)}," +
                          $"{action},{target},");
        }
        return sb.ToString();
    }

    /// <summary>导出时给出的事件摘要（用于界面提示）。跳过 = 传进来的音里不发声的那些。</summary>
    public static ExportSummary Summarize(
        IReadOnlyList<MappedNote> notes, double speed = 1.0, InputTiming? timing = null)
    {
        var events = PlaybackEngine.BuildSchedulePreview(notes, timing, speed);
        int playable = notes.Count(n => n.InRange);
        double end = events.Count == 0 ? 0 : events[^1].MusicTime;
        return new ExportSummary(playable, events.Count, end, notes.Count - playable);
    }
}

/// <summary>
/// 导出摘要。带 3 元与 4 元两种解构：老调用点 <c>var (音数, 事件数, 秒) = Summarize(...)</c> 照样编译，
/// 新代码可以多取一个「跳过音数」（<see cref="Skipped"/>）。
/// </summary>
public readonly struct ExportSummary
{
    public ExportSummary(int notes, int events, double seconds, int skipped)
    {
        Notes = notes;
        Events = events;
        Seconds = seconds;
        Skipped = skipped;
    }

    /// <summary>会发声的音符数。</summary>
    public int Notes { get; }
    /// <summary>派发的按键/鼠标事件数。</summary>
    public int Events { get; }
    /// <summary>最后一个事件的音乐时刻（秒）。</summary>
    public double Seconds { get; }
    /// <summary>不发声音符数（超界丢音、缺音丢弃等）。</summary>
    public int Skipped { get; }

    public void Deconstruct(out int notes, out int events, out double seconds)
    {
        notes = Notes;
        events = Events;
        seconds = Seconds;
    }

    public void Deconstruct(out int notes, out int events, out double seconds, out int skipped)
    {
        notes = Notes;
        events = Events;
        seconds = Seconds;
        skipped = Skipped;
    }

    public override string ToString()
        => $"{Notes} 音 / {Events} 事件 / {Seconds:F1}s / 跳过 {Skipped}";
}

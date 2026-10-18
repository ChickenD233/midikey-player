using System.Globalization;
using System.Text;
using MidiKeyPlayer.Input;
using MidiKeyPlayer.Midi;

namespace MidiKeyPlayer.Engine;

public static class MacroExporter
{
    public enum Format { LogitechGHub, KeystrokeCsv }

    public static string Extension(Format f) => f switch
    {
        Format.LogitechGHub => ".lua",
        _ => ".csv"
    };

    public static string Build(IReadOnlyList<MappedNote> notes, Format format,
                               double speed = 1.0, InputTiming? timing = null,
                               string songName = "")
    {
        var events = PlaybackEngine.BuildSchedulePreview(notes, timing, speed);
        return format == Format.LogitechGHub
            ? BuildLua(events, songName, speed)
            : BuildCsv(events);
    }

    private static string KeyNameOf(char c) => InputSender.KeyNameOf(c);

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
        _ => name.ToLowerInvariant(),     
    };

    private static string MouseName(string kind) => kind switch
    {
        "mouse-left" => "左键",
        "mouse-right" => "右键",
        _ => "中键"
    };

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

    private static string MouseName(InputSender.MouseButton button) => button switch
    {
        InputSender.MouseButton.Left => "左键",
        InputSender.MouseButton.Right => "右键",
        _ => "中键"
    };

    private static string MouseOrKeyName(string name)
        => InputSender.TryMouseButton(name, out var button) ? MouseName(button) : name;

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

    public static ExportSummary Summarize(
        IReadOnlyList<MappedNote> notes, double speed = 1.0, InputTiming? timing = null)
    {
        var events = PlaybackEngine.BuildSchedulePreview(notes, timing, speed);
        int playable = notes.Count(n => n.InRange);
        double end = events.Count == 0 ? 0 : events[^1].MusicTime;
        return new ExportSummary(playable, events.Count, end, notes.Count - playable);
    }
}

public readonly struct ExportSummary
{
    public ExportSummary(int notes, int events, double seconds, int skipped)
    {
        Notes = notes;
        Events = events;
        Seconds = seconds;
        Skipped = skipped;
    }

    public int Notes { get; }
    public int Events { get; }
    public double Seconds { get; }
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

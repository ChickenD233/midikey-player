using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using MidiKeyPlayer.Persist;

namespace MidiKeyPlayer.Midi;

public static class MidiInputService
{
    private static readonly object Gate = new();
    private static InputDevice? _device;
    private static string _deviceName = "";

    /// <summary>设备送来的音符事件：(音高, 力度, 是否按下)。</summary>
    public static event Action<int, int, bool>? NoteEvent;
    /// <summary>设备被拔掉、启动失败等异常的说明。</summary>
    public static event Action<string>? Error;
    public static event Action? DeviceLost;

    public static bool IsWindows => OperatingSystem.IsWindows();
    public static string CurrentDeviceName => _deviceName;
    public static bool IsListening => _device != null;

    public static bool Start(string deviceName)
    {
        Stop();
        if (string.IsNullOrWhiteSpace(deviceName)) return false;

        // 先枚举一次：设备名不在列表里就没必要走启动（也能给出更清楚的提示）
        var names = ListDevices();
        if (!names.Contains(deviceName))
        {
            Error?.Invoke($"找不到 MIDI 设备「{deviceName}」，请重新选择"
                          + (names.Count == 0 ? "（当前没有可用设备）" : ""));
            return false;
        }

        InputDevice? device = null;
        try
        {
            device = InputDevice.GetByName(deviceName);
            if (device == null)
            {
                Error?.Invoke($"打开 MIDI 设备「{deviceName}」失败：设备返回空");
                return false;
            }
            device.EventReceived += OnEventReceived;
            device.ErrorOccurred += OnDeviceError;
            // 0x90 力度 0 是常见的"音符抬起"写法，按抬起处理
            device.SilentNoteOnPolicy = SilentNoteOnPolicy.NoteOff;
            device.StartEventsListening();
        }
        catch (Exception ex)
        {
            Error?.Invoke($"打开 MIDI 设备「{deviceName}」失败：{ex.Message}");
            CleanUpAfterFailedStart(device);
            return false;
        }

        lock (Gate)
        {
            _device = device;
            _deviceName = deviceName;
        }
        return true;
    }

    /// <summary>停止监听并释放设备。</summary>
    public static void Stop()
    {
        InputDevice? device;
        lock (Gate)
        {
            device = _device;
            _device = null;
            _deviceName = "";
        }
        if (device == null) return;
        try
        {
            device.EventReceived -= OnEventReceived;
            device.ErrorOccurred -= OnDeviceError;
            if (device.IsListeningForEvents) device.StopEventsListening();
            device.Dispose();
        }
        catch (Exception ex)
        {
            // 设备已经拔掉时释放会抛异常：不影响使用，但写一条日志便于排查（A09）
            LogFile.Append($"[MIDI] 释放设备失败（已忽略）：{ex.Message}");
        }
    }

    private static void CleanUpAfterFailedStart(InputDevice? device)
    {
        if (device == null) return;
        try
        {
            device.EventReceived -= OnEventReceived;
            device.ErrorOccurred -= OnDeviceError;
            if (device.IsListeningForEvents) device.StopEventsListening();
            device.Dispose();
        }
        catch (Exception ex)
        {
            LogFile.Append($"[MIDI] 启动失败后清理设备出错（已忽略）：{ex.Message}");
        }
    }

    /// <summary>系统里的 MIDI 输入设备名列表。没有设备时返回空列表。</summary>
    public static List<string> ListDevices()
    {
        var list = new List<string>();
        if (!IsWindows) return list;
        try
        {
            foreach (var d in InputDevice.GetAll())
            {
                try
                {
                    string name = d.Name ?? "";
                    if (!string.IsNullOrWhiteSpace(name) && !list.Contains(name)) list.Add(name);
                }
                finally
                {
                    try { d.Dispose(); }
                    catch (Exception ex)
                    {
                        LogFile.Append($"[MIDI] 释放枚举到的设备失败（已忽略）：{ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Error?.Invoke($"枚举 MIDI 设备失败：{ex.Message}");
        }
        return list;
    }

    public static Task<bool> StartAsync(string deviceName) => Task.Run(() => Start(deviceName));

    private static void OnEventReceived(object? sender, MidiEventReceivedEventArgs e)
    {
        switch (e.Event)
        {
            case NoteOnEvent on:
                NoteEvent?.Invoke((int)on.NoteNumber, (int)on.Velocity, true);
                break;
            case NoteOffEvent off:
                NoteEvent?.Invoke((int)off.NoteNumber, (int)off.Velocity, false);
                break;
        }
    }

    private static void OnDeviceError(object? sender, ErrorOccurredEventArgs e)
    {
        Error?.Invoke($"MIDI 设备异常：{e.Exception?.Message ?? "未知错误"}");
        lock (Gate) { if (sender == null || !ReferenceEquals(sender, _device)) return; }
        DeviceLost?.Invoke();
    }
}

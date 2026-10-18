using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using MidiKeyPlayer.Persist;

namespace MidiKeyPlayer.Midi;

public static class MidiInputService
{
    private static readonly object Gate = new();
    private static InputDevice? _device;
    private static string _deviceName = "";

    public static event Action<int, int, bool>? NoteEvent;
    public static event Action<string>? Error;
    public static event Action? DeviceLost;

    public static bool IsWindows => OperatingSystem.IsWindows();
    public static string CurrentDeviceName => _deviceName;
    public static bool IsListening => _device != null;

    public static bool Start(string deviceName)
    {
        Stop();
        if (string.IsNullOrWhiteSpace(deviceName)) return false;

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

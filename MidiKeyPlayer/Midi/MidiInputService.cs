using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using MidiKeyPlayer.Persist;

namespace MidiKeyPlayer.Midi;

/// <summary>
/// MIDI 输入设备接入（issue #4）：枚举系统里的 MIDI 输入设备，监听它们的音符事件。
/// 用后台线程启动设备，避免界面卡住。事件在设备自己的线程上触发，界面需自行 marshal。
/// </summary>
public static class MidiInputService
{
    private static readonly object Gate = new();
    private static InputDevice? _device;
    private static string _deviceName = "";

    /// <summary>设备送来的音符事件：(音高, 力度, 是否按下)。</summary>
    public static event Action<int, int, bool>? NoteEvent;
    /// <summary>设备被拔掉、启动失败等异常的说明。</summary>
    public static event Action<string>? Error;
    /// <summary>
    /// 设备中途断开或报错（不是启动失败）：引擎收到后要松开所有键并停实时演奏（IN-08）。
    /// 在设备线程上触发，订阅方必须自己保证线程安全。
    /// </summary>
    public static event Action? DeviceLost;

    public static bool IsWindows => OperatingSystem.IsWindows();
    public static string CurrentDeviceName => _deviceName;
    public static bool IsListening => _device != null;

    /// <summary>
    /// 打开设备并开始监听。返回 false 表示打开失败（原因通过 <see cref="Error"/> 给出）。
    /// 设备不存在、被占用、驱动异常都会走到这里，不抛异常给调用方。
    /// </summary>
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
            // A09：走到这里时可能已经订阅了事件、甚至已经开了监听。不清理就泄漏设备与订阅，
            // 下次 Start 会重复挂一份回调。统一走 Stop 的清理路径。
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

    /// <summary>
    /// 启动失败后的清理：退订事件、停监听、释放设备（A09）。
    /// 启动失败路径可能已经订阅了事件甚至开了监听，不清理就会泄漏设备与订阅。
    /// 复用 <see cref="Stop"/> 没取到锁时的同一套动作。
    /// </summary>
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
                    // GetAll 返回的设备是 IDisposable：只枚举名字也必须释放，否则每次刷新都漏一个
                    // 设备句柄，有些驱动之后会拒绝再打开（报"设备忙"）。防御风格同 Stop（A09）。
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

    /// <summary>
    /// 要在后台线程上调用 <see cref="Start"/>：枚举与打开设备可能耗时几百毫秒，
    /// 放在界面线程上会让窗口短暂卡住。
    /// </summary>
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
        // 只在报错的设备就是当前监听设备时算"断开"：IsListening 只看「有没有设备在听」，
        // 旧设备清理失败后残留的回调可能在新设备演奏时到达，不能误伤正在演奏的设备。
        lock (Gate) { if (sender == null || !ReferenceEquals(sender, _device)) return; }
        DeviceLost?.Invoke();
    }
}

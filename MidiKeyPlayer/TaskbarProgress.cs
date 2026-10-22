using System;
using System.Runtime.InteropServices;

namespace MidiKeyPlayer;

internal static class TaskbarProgress
{
    private enum TbpFlag
    {
        NoProgress = 0,
        Indeterminate = 1,
        Normal = 2,
        Error = 4,
        Paused = 8,
    }

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    private class TaskbarList
    {
    }

    [ComImport]
    [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEA84")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, TbpFlag flags);
    }

    private static ITaskbarList3? _list;
    private static bool _failed;

    private static ITaskbarList3? Get()
    {
        if (_failed) return null;
        if (_list != null) return _list;
        try
        {
            _list = (ITaskbarList3)new TaskbarList();
            _list.HrInit();
        }
        catch
        {
            _failed = true;
            _list = null;
        }
        return _list;
    }

    internal static void Set(IntPtr hwnd, double done, double total)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero) return;
        var list = Get();
        if (list == null) return;
        try
        {
            list.SetProgressState(hwnd, TbpFlag.Normal);
            list.SetProgressValue(hwnd,
                (ulong)Math.Max(0, done * 1000),
                (ulong)Math.Max(1, total * 1000));
        }
        catch
        {
        }
    }

    internal static void Clear(IntPtr hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero) return;
        var list = Get();
        if (list == null) return;
        try
        {
            list.SetProgressState(hwnd, TbpFlag.NoProgress);
        }
        catch
        {
        }
    }
}

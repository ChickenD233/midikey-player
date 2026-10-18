using System.Diagnostics;
using System.Runtime.InteropServices;
using MidiKeyPlayer.Persist;

namespace MidiKeyPlayer.Input;

internal static class IbDriver
{
    private const uint SendTypeLogitech = 2;          
    private const uint SendTypeLogitechGHubNew = 6;   

    private const uint ErrorSuccess = 0;
    private const uint ErrorDeviceNotFound = 6;
    private const uint ErrorDeviceOpenFailed = 7;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint IbSendInitDelegate(uint type, uint flags, IntPtr argument);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void IbSendDestroyDelegate();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool IbSendKeybdDelegate(ushort vk);

    private static readonly object Gate = new();
    private static IbSendInitDelegate? _init;
    private static IbSendKeybdDelegate? _keybdDown;
    private static IbSendKeybdDelegate? _keybdUp;
    private static IbSendDestroyDelegate? _destroy;
    private static bool _ready;         
    private static bool _dllMissing;    

    private static string DllPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "MidiKeyPlayer", "IbInputSimulator.dll");

    public static bool TryInit(out string error, out string? warn)
    {
        lock (Gate)
        {
            warn = null;
            if (_ready) { error = ""; return true; }
            if (!OperatingSystem.IsWindows()) { error = "当前平台不支持驱动注入。"; return false; }
            if (_dllMissing)
            {
                error = "程序内嵌的 IbInputSimulator.dll 释放失败（杀毒软件可能拦截了写文件）。"
                        + "请把 %LOCALAPPDATA%\\MidiKeyPlayer 加入白名单后重试。";
                return false;
            }

            if (!EnsureDllExtracted(out error)) { _dllMissing = true; return false; }
            if (!LoadExports(out error)) { _dllMissing = true; return false; }

            uint err = _init!(SendTypeLogitechGHubNew, 0, IntPtr.Zero);
            if (err != ErrorSuccess)
                err = _init!(SendTypeLogitech, 0, IntPtr.Zero);

            if (err != ErrorSuccess)
            {
                error = err switch
                {
                    ErrorDeviceNotFound or ErrorDeviceOpenFailed =>
                        "没找到罗技驱动设备：请先安装 Logitech G HUB（不需要罗技硬件，装完重启一次电脑），"
                        + "再切回「罗技 G HUB 驱动」。",
                    _ => $"罗技驱动初始化失败（错误码 {err}）。",
                };
                LogFile.Append("[输入] IbInputSimulator 初始化失败：错误码 " + err);
                return false;
            }

            _ready = true;
            error = "";

            if (!GHubAgentRunning())
            {
                warn = "检测到 Logitech G HUB 没在运行：驱动设备在，但按键发不出去。"
                       + "请启动 Logitech G HUB（装好后默认开机自启），再开始播放。";
                LogFile.Append("[输入] G HUB 代理（lghub_agent）未在运行，按键可能发不出去。");
            }

            LogFile.Append("[输入] 已切到罗技 G HUB 驱动注入（IbInputSimulator）。");
            return true;
        }
    }

    private static bool GHubAgentRunning()
    {
        try { return Process.GetProcessesByName("lghub_agent").Length > 0; }
        catch { return true; }   
    }

    public static bool Keybd(bool down, ushort vk)
    {
        if (!_ready) return false;
        try
        {
            return down ? _keybdDown!(vk) : _keybdUp!(vk);
        }
        catch (Exception ex)
        {
            LogFile.Append("[输入] IbInputSimulator 发键异常：" + ex.Message);
            return false;
        }
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            if (!_ready) return;
            try { _destroy?.Invoke(); } catch { }
            _ready = false;
        }
    }

    private static bool EnsureDllExtracted(out string error)
    {
        error = "";
        try
        {
            var uri = new Uri("avares://MidiKeyPlayer/Libs/IbInputSimulator.dll");
            using var stream = Avalonia.Platform.AssetLoader.Open(uri);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            byte[] bytes = ms.ToArray();

            if (File.Exists(DllPath) && new FileInfo(DllPath).Length == bytes.Length)
                return true;

            Directory.CreateDirectory(Path.GetDirectoryName(DllPath)!);
            string tmp = DllPath + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, DllPath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            LogFile.Append("[输入] 释放 IbInputSimulator.dll 失败：" + ex.Message);
            error = "释放内嵌的 IbInputSimulator.dll 失败：" + ex.Message;
            return false;
        }
    }

    private static bool LoadExports(out string error)
    {
        error = "";
        if (_init != null) return true;
        try
        {
            IntPtr h = NativeLibrary.Load(DllPath);
            _init = GetProc<IbSendInitDelegate>(h, "IbSendInit");
            _destroy = GetProc<IbSendDestroyDelegate>(h, "IbSendDestroy");
            _keybdDown = GetProc<IbSendKeybdDelegate>(h, "IbSendKeybdDown");
            _keybdUp = GetProc<IbSendKeybdDelegate>(h, "IbSendKeybdUp");
            return true;
        }
        catch (Exception ex)
        {
            LogFile.Append("[输入] 加载 IbInputSimulator.dll 失败：" + ex.Message);
            error = "加载 IbInputSimulator.dll 失败：" + ex.Message;
            return false;
        }
    }

    private static T GetProc<T>(IntPtr module, string name) where T : Delegate
    {
        IntPtr p = NativeLibrary.GetExport(module, name);
        return Marshal.GetDelegateForFunctionPointer<T>(p);
    }
}

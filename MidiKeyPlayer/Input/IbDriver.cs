using System.Runtime.InteropServices;
using MidiKeyPlayer.Persist;

namespace MidiKeyPlayer.Input;

/// <summary>
/// IbInputSimulator 驱动注入（第三方组件，MIT 许可，作者 Chaoses-Ib）。
/// 用途：目标程序屏蔽 SendInput（注入事件带「已注入」标记，会被过滤）时，
/// 改走罗技 G HUB / LGS 的驱动级注入 —— 直接向罗技驱动设备发标准 HID 键盘报告，
/// 目标程序收到的是「驱动层上来的键」，与 SendInput 的用户态注入不是一条链路。
///
/// 用法：先 <see cref="TryInit"/>（选「罗技 G HUB 驱动」时调用一次），之后
/// <see cref="Keybd"/> 发按下/抬起。不需要罗技硬件，但要求装过 G HUB 或 LGS
/// （驱动设备由它们的驱动注册；没装就报 DeviceNotFound）。
///
/// 限制（来自该库的 Logitech 后端）：
/// - 只发键盘；新版 G HUB（≥2022.3）砍掉了鼠标驱动，鼠标键发不了。
/// - 标准 8 字节 HID 键盘报告：最多 6 个普通键同时按住（+修饰键另算）。
///   和弦超过 6 个音时，多出来的键会被该后端悄悄丢掉。
///
/// DLL（244 KB，x64）作为 Avalonia 资源嵌在 exe 里（Libs/IbInputSimulator.dll），
/// 首次使用时释放到 %LOCALAPPDATA%\MidiKeyPlayer\ 再 LoadLibrary —— 发布包保持
/// 「只有一个 exe」的形态不变。
/// </summary>
internal static class IbDriver
{
    // Send::SendType（Simulator/include/IbInputSimulator/InputSimulator.hpp）
    private const uint SendTypeLogitech = 2;          // LGS / 旧版 G HUB
    private const uint SendTypeLogitechGHubNew = 6;   // 新版 G HUB

    // Send::Error
    private const uint ErrorSuccess = 0;
    private const uint ErrorDeviceNotFound = 6;
    private const uint ErrorDeviceOpenFailed = 7;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint IbSendInitDelegate(uint type, uint flags, IntPtr argument);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void IbSendDestroyDelegate();

    // 注意：C++ bool 是 1 字节，必须按 I1 封送（默认的 4 字节 BOOL 会读错）。
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool IbSendKeybdDelegate(ushort vk);

    private static readonly object Gate = new();
    private static IbSendInitDelegate? _init;
    private static IbSendKeybdDelegate? _keybdDown;
    private static IbSendKeybdDelegate? _keybdUp;
    private static IbSendDestroyDelegate? _destroy;
    private static bool _ready;         // TryInit 成功过
    private static bool _dllMissing;    // 资源或文件出了问题，别再反复试

    /// <summary>资源里嵌的 DLL 释放到哪。与设置文件同目录。</summary>
    private static string DllPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "MidiKeyPlayer", "IbInputSimulator.dll");

    /// <summary>
    /// 初始化罗技驱动注入。成功返回 true；失败时 error 是中文原因（可直接弹给用户）。
    /// 线程安全；重复调用只在未成功时真正重试。
    /// </summary>
    public static bool TryInit(out string error)
    {
        lock (Gate)
        {
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

            // 先试新版 G HUB 后端，再退回 LGS/旧 G HUB。两边设备 GUID 不同，库里分开处理。
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
            LogFile.Append("[输入] 已切到罗技 G HUB 驱动注入（IbInputSimulator）。");
            return true;
        }
    }

    /// <summary>发一次键盘按下/抬起。未初始化或发送失败返回 false（调用方按「注入失败」处理）。</summary>
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

    /// <summary>关掉驱动设备句柄（切回 SendInput 时调用）。之后再用会自动重新 TryInit。</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            if (!_ready) return;
            try { _destroy?.Invoke(); } catch { /* 关句柄失败无碍 */ }
            _ready = false;
        }
    }

    /// <summary>把 exe 里嵌的 DLL 释放到本地目录；已存在且大小一致就直接用。</summary>
    private static bool EnsureDllExtracted(out string error)
    {
        error = "";
        try
        {
            // AvaloniaResource 清单里叫 Libs/IbInputSimulator.dll（见 csproj 的 Link）
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

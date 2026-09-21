using Avalonia;
using MidiKeyPlayer;

internal static class Program
{
    // 单实例锁：避免两个实例同时模拟按键
    private static System.IO.FileStream? _singleLock;

    // 初始化代码：不要用依赖 Avalonia、第三方库或其它服务端代码的 API
    [STAThread]
    public static void Main(string[] args)
    {
        // 未捕获异常写入本地日志，便于回传排查
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try { MidiKeyPlayer.Persist.LogFile.Append("[FATAL] " + (e.ExceptionObject?.ToString() ?? "未知异常")); }
            catch { }
        };

        // 键位自检（MIDIKEY_GAME_SELFTEST，见 GameSelfTest.cs）：纯逻辑，不建窗口、不注册热键。
        if (GameSelfTest.Requested)
        {
            Environment.Exit(GameSelfTest.Run());
            return;
        }

        // 音频转 MIDI 数值探针（MIDIKEY_BP_PROBE，见 Audio\AudioProbe.cs）：同样纯逻辑。
        if (MidiKeyPlayer.Audio.AudioProbe.Requested)
        {
            Environment.Exit(MidiKeyPlayer.Audio.AudioProbe.Run());
            return;
        }

        // 音频转 MIDI 整链路探针（MIDIKEY_BP_CONVERT=<音频路径>）：解码 → 推理 → 写 MIDI。
        if (MidiKeyPlayer.Audio.AudioProbe.ConvertRequested)
        {
            Environment.Exit(MidiKeyPlayer.Audio.AudioProbe.RunConvert());
            return;
        }

        // 人声 / 伴奏分离探针（MIDIKEY_STEM_PROBE=<模型目录>）：见 Audio\StemProbe.cs。
        if (MidiKeyPlayer.Audio.StemProbe.Requested)
        {
            Environment.Exit(MidiKeyPlayer.Audio.StemProbe.Run());
            return;
        }

        // 人声 / 伴奏分离模型探针（MIDIKEY_SPLEETER_PROBE=<模型目录>）：见 Audio\SpleeterProbe.cs。
        if (MidiKeyPlayer.Audio.SpleeterProbe.Requested)
        {
            Environment.Exit(MidiKeyPlayer.Audio.SpleeterProbe.Run());
            return;
        }

        // 试听探针（MIDIKEY_PREVIEW_PROBE=1，见 DevPreviewProbe.cs）只放音频、不发按键、
        // 不注册全局热键，允许与用户正在用的实例并存。
        // 界面快照与文件夹换歌回归（MIDIKEY_UI_SNAPSHOT*，见 DevUISnapshot.cs）同理：
        // 只建窗口、载入文件、拍图，不演奏、不发按键，所以也跳过单实例检查 ——
        // 否则用户开着程序时，发布流程里的自检与回归场景全都会被单实例挡掉。
        if (!PreviewProbeMode.On && !DevSnapshotMode.On && !TryAcquireSingleInstance())
        {
            try { MidiKeyPlayer.Persist.LogFile.Append("检测到已有一个实例在运行，本实例直接退出。"); }
            catch { }
            return;   // 已有实例在跑；防两个程序同时按键
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>独占锁文件保证单实例（跨平台）。</summary>
    private static bool TryAcquireSingleInstance()
    {
        try
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MidiKeyPlayer");
            System.IO.Directory.CreateDirectory(dir);
            string lockFile = System.IO.Path.Combine(dir, "instance.lock");
            _singleLock = new System.IO.FileStream(lockFile,
                System.IO.FileMode.OpenOrCreate, System.IO.FileAccess.ReadWrite,
                System.IO.FileShare.None);   // 第二个进程打开会失败
            return true;
        }
        catch (System.IO.IOException)
        {
            return false;   // 已被占用
        }
        catch
        {
            return true;    // 其它异常不阻塞启动
        }
    }

    // Avalonia 配置，勿移除；可视化设计器也要用
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}

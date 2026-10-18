using Avalonia;
using MidiKeyPlayer;

internal static class Program
{
    private static System.IO.FileStream? _singleLock;

    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try { MidiKeyPlayer.Persist.LogFile.Append("[FATAL] " + (e.ExceptionObject?.ToString() ?? "未知异常")); }
            catch { }
        };

        if (GameSelfTest.Requested)
        {
            Environment.Exit(GameSelfTest.Run());
            return;
        }

        if (!PreviewProbeMode.On && !DevSnapshotMode.On && !TryAcquireSingleInstance())
        {
            try { MidiKeyPlayer.Persist.LogFile.Append("检测到已有一个实例在运行，本实例直接退出。"); }
            catch { }
            return;   
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

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
                System.IO.FileShare.None);   
            return true;
        }
        catch (System.IO.IOException)
        {
            return false;   
        }
        catch
        {
            return true;    
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}

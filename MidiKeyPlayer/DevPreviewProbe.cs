using System.Diagnostics;
using Avalonia.Threading;
using MidiKeyPlayer.Engine;

namespace MidiKeyPlayer;

internal static class PreviewProbeMode
{
    internal static readonly bool On =
        Environment.GetEnvironmentVariable("MIDIKEY_PREVIEW_PROBE") == "1";
}

public partial class MainWindow
{
    private DispatcherTimer? _probeMonitor;
    private double _probeStartStamp;
    private double _probeUiMaxPosition;
    private readonly List<double> _probeSeeks = new();
    private int _probeSeekIndex;
    private bool _probeFinished;
    private bool _probeSeekLogPending;
    private double _probeSeekLogTarget;
    private double _probeLastTickStamp;

    internal static void InstallPreviewProbe(MainWindow window)
    {
        if (!PreviewProbeMode.On) return;
        window.Opened += (_, _) => window.BeginPreviewProbe();
    }

    private static string ProbeEnv(string name) => Environment.GetEnvironmentVariable(name) ?? "";

    private static double ProbeNow() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private static double ProbeNum(string name, double fallback)
        => double.TryParse(ProbeEnv(name), out double v) ? v : fallback;

    private void ProbeLog(string line)
    {
        PreviewJitterProbe.Note(line);
        try
        {
            File.AppendAllText(ProbeEnv("MIDIKEY_PREVIEW_PROBE_OUT"),
                $"[probe] {line}{Environment.NewLine}");
        }
        catch { }
    }

    private void BeginPreviewProbe()
    {
        ShowActivated = false;
        string file = ProbeEnv("MIDIKEY_PREVIEW_PROBE_FILE");
        string mix = ProbeEnv("MIDIKEY_PREVIEW_PROBE_MIX");
        string seeks = ProbeEnv("MIDIKEY_PREVIEW_PROBE_SEEKS");
        _probeStartStamp = ProbeNow();

        foreach (string part in seeks.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (double.TryParse(part, out double s)) _probeSeeks.Add(s);

        var prep = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        prep.Tick += (_, _) =>
        {
            prep.Stop();
            if (StartupOverlay != null) StartupOverlay.IsVisible = false;
            if (!string.IsNullOrWhiteSpace(file))
            {
                bool ok = LoadMidiFile(file);
                ProbeLog($"载入 {file} → {ok}");
            }
            if (string.Equals(mix, "all", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var row in _tracks)
                {
                    if (row.IsPercussion) continue;
                    row.IsMix = true;
                    if (!_mixOrder.Contains(row)) _mixOrder.Add(row);
                }
                SyncMixOrder();
                RefreshPreview();
                ProbeLog($"已勾合奏 {_mixOrder.Count} 条声轨");
            }

            var start = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            start.Tick += (_, _) =>
            {
                start.Stop();
                string probeTitle = ProbeEnv("MIDIKEY_PREVIEW_PROBE_TITLE");
                PreviewJitterProbe.Reset(string.IsNullOrEmpty(probeTitle) ? Path.GetFileName(file) : probeTitle);
                if (ProbeEnv("MIDIKEY_PREVIEW_PROBE_CLICK") == "1")
                {
                    BtnPreview.IsEnabled = true;
                    BtnPreview.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                    ProbeLog("已用真实按钮点击「试听」");
                }
                else
                {
                    StartPreviewAudio();
                }
                if (!_previewOn)
                {
                    ProbeLog("试听没有开始（设备不可用或谱面为空）");
                    FinishPreviewProbe("试听未开始");
                    return;
                }
                ProbeLog($"试听已开始：事件 {PreviewEventCount} 条，总长 {_previewTotal:F2}s");
                _probeStartStamp = ProbeNow();
                _probeLastTickStamp = ProbeNow();
                StartProbeMonitor();
            };
            start.Start();
        };
        prep.Start();

        var guard = new DispatcherTimer { Interval = TimeSpan.FromMinutes(6) };
        guard.Tick += (_, _) =>
        {
            guard.Stop();
            ProbeLog("兜底计时器触发（6 分钟）");
            FinishPreviewProbe("超时");
        };
        guard.Start();
    }

    private void StartProbeMonitor()
    {
        _probeMonitor = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _probeMonitor.Tick += (_, _) => ProbeMonitorTick();
        _probeMonitor.Start();
    }

    private void ProbeMonitorTick()
    {
        double now = ProbeNow();
        double late = (now - _probeLastTickStamp - 0.1) * 1000.0;
        _probeLastTickStamp = now;
        PreviewJitterProbe.UiTick(late);

        double pos = CurrentPosition();
        if (pos > _probeUiMaxPosition) _probeUiMaxPosition = pos;

        if (_probeSeekLogPending)
        {
            _probeSeekLogPending = false;
            ProbeLog($"跳转 {_probeSeekLogTarget}s 之后：sounding={_preview?.SoundingCount ?? -1}，"
                     + $"noteOn={_preview?.NoteOnCount ?? -1}，noteOff={_preview?.NoteOffCount ?? -1}");
        }

        if (_previewOn && BtnPreview.IsEnabled) BtnPreview.IsEnabled = false;

        double elapsed = now - _probeStartStamp;

        double closeAt = ProbeNum("MIDIKEY_PREVIEW_PROBE_CLOSEAT", 0);
        if (closeAt > 0 && elapsed >= closeAt)
        {
            ProbeLog($"关窗口：elapsed={elapsed:F2}s，关之前 sounding={_preview?.SoundingCount ?? -1}，"
                     + $"noteOn={_preview?.NoteOnCount ?? -1}，noteOff={_preview?.NoteOffCount ?? -1}");
            _probeFinished = true;
            WriteProbeSummary("关窗口前");
            Close();
            return;
        }

        double switchAt = ProbeNum("MIDIKEY_PREVIEW_PROBE_SWITCHAT", 3);
        string switchTo = ProbeEnv("MIDIKEY_PREVIEW_PROBE_SWITCH");
        if (!string.IsNullOrWhiteSpace(switchTo) && elapsed >= switchAt)
        {
            ProbeLog($"换曲：elapsed={elapsed:F2}s → 载入 {switchTo}");
            LoadMidiFile(switchTo);
            ProbeLog($"换曲后 sounding={_preview?.SoundingCount ?? -1}（应为 0），"
                     + $"noteOn={_preview?.NoteOnCount ?? -1}，noteOff={_preview?.NoteOffCount ?? -1}");
            Environment.SetEnvironmentVariable("MIDIKEY_PREVIEW_PROBE_SWITCH", "");
            if (ProbeEnv("MIDIKEY_PREVIEW_PROBE_RESTART") == "1")
            {
                StartPreviewAudio();
                ProbeLog($"换曲后重新开始试听：{_previewOn}");
            }
            return;
        }

        while (_probeSeekIndex < _probeSeeks.Count && pos >= _probeSeeks[_probeSeekIndex])
        {
            double target = _probeSeeks[_probeSeekIndex++];
            ApplySeek(target);
            ProbeLog($"跳转 {target}s（界面位置 {pos:F2}s）");
            _probeSeekLogPending = true;
            _probeSeekLogTarget = target;
        }

        string shot = ProbeEnv("MIDIKEY_PREVIEW_PROBE_SHOT");
        if (!string.IsNullOrWhiteSpace(shot) && pos >= 2 && !File.Exists(shot))
        {
            try
            {
                var root = (Avalonia.Visual?)Content ?? this;
                int w = Math.Max(1, (int)Math.Ceiling(root.Bounds.Width));
                int h = Math.Max(1, (int)Math.Ceiling(root.Bounds.Height));
                var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(
                    new Avalonia.PixelSize(w, h), new Avalonia.Vector(96, 96));
                rtb.Render(root);
                rtb.Save(shot);
                ProbeLog($"播放中截图已写：{shot}（{w}x{h}）按钮文字={BtnPreview.Content}");
            }
            catch (Exception ex) { ProbeLog("截图失败：" + ex.Message); }
        }

        double stopAt = ProbeNum("MIDIKEY_PREVIEW_PROBE_STOPAT", 0);
        if (stopAt > 0 && elapsed >= stopAt)
        {
            if (ProbeEnv("MIDIKEY_PREVIEW_PROBE_CLICK") == "1")
            {
                BtnPreview.IsEnabled = true;
                BtnPreview.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                ProbeLog("已用真实按钮点击「停止试听」");
            }
            else
            {
                StopPreviewAudio();
            }
            ProbeLog($"停止：elapsed={elapsed:F2}s，停止后 sounding={_preview?.SoundingCount ?? -1}（应为 0），"
                     + $"noteOn={_preview?.NoteOnCount ?? -1}，noteOff={_preview?.NoteOffCount ?? -1}，"
                     + $"按钮文字={BtnPreview.Content}");
            FinishPreviewProbe("正常停止");
            return;
        }

        if (!_previewOn)
        {
            double stopAtCfg = ProbeNum("MIDIKEY_PREVIEW_PROBE_STOPAT", 0);
            string why = stopAtCfg > 0 && elapsed < stopAtCfg - 0.5
                ? $"外部打断（有人在 {elapsed:F2}s 停掉了试听，本次数据存疑）"
                : "自然结束（或按钮被外部点击）";
            ProbeLog($"试听结束：{why}；界面位置 {pos:F2}s");
            FinishPreviewProbe(why);
        }
    }

    private void WriteProbeSummary(string stage)
    {
        string path = ProbeEnv("MIDIKEY_PREVIEW_PROBE_OUT");
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            File.AppendAllText(path,
                $"[probe] 阶段={stage}；界面最大位置={_probeUiMaxPosition:F2}s；"
                + $"sounding={_preview?.SoundingCount ?? -1}；noteOn={_preview?.NoteOnCount ?? -1}；"
                + $"noteOff={_preview?.NoteOffCount ?? -1}；失败发送={_preview?.SendFailureCount ?? -1}"
                + Environment.NewLine);
        }
        catch { }
    }

    private void FinishPreviewProbe(string reason)
    {
        if (_probeFinished) return;
        _probeFinished = true;
        _probeMonitor?.Stop();
        _probeMonitor = null;
        ProbeLog($"结束原因：{reason}；界面最大位置={_probeUiMaxPosition:F2}s");
        PreviewJitterProbe.WriteReport();
        WriteProbeSummary(reason);
        try { File.AppendAllText(ProbeEnv("MIDIKEY_PREVIEW_PROBE_OUT"), "[probe] 探针退出" + Environment.NewLine); } catch { }
        DevCleanUpForExit();
        Environment.Exit(0);
    }
}

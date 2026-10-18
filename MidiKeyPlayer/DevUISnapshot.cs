using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MidiKeyPlayer.Engine;
using MidiKeyPlayer.Midi;

namespace MidiKeyPlayer;

internal static class DevSnapshotMode
{
    private static readonly string[] Known =
    {
        "MIDIKEY_UI_SNAPSHOT",
        "MIDIKEY_UI_SNAPSHOT_KEYMAP",
        "MIDIKEY_UI_SNAPSHOT_MIDI",
        "MIDIKEY_UI_SNAPSHOT_MIX",
        "MIDIKEY_UI_SNAPSHOT_REPORT",
        "MIDIKEY_UI_SNAPSHOT_FOLDER",
        "MIDIKEY_UI_SNAPSHOT_FOLDER_REPORT",
        "MIDIKEY_UI_SNAPSHOT_ADVANCED",
        "MIDIKEY_UI_SNAPSHOT_SPONSOR",
        "MIDIKEY_UI_SNAPSHOT_SYNC",
        "MIDIKEY_UI_SNAPSHOT_THEME",
        "MIDIKEY_UI_SNAPSHOT_PLAYLIST",
        "MIDIKEY_UI_SNAPSHOT_PLAYLIST_DIR",
    };

    internal static readonly bool On = Detect();

    private static bool Detect()
    {
        foreach (string name in Known)
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))) return true;

        try
        {
            foreach (System.Collections.DictionaryEntry item in Environment.GetEnvironmentVariables())
            {
                string name = item.Key as string ?? "";
                if (name.StartsWith("MIDIKEY_UI_SNAPSHOT", StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(item.Value as string))
                    return true;
            }
        }
        catch { }
        return false;
    }
}

public partial class MainWindow
{
    internal static void InstallDevSnapshot(MainWindow window)
    {
        var path = Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT");
        var keymapPath = Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT_KEYMAP");
        var midiPath = Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT_MIDI");
        var mixMode = Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT_MIX");
        var reportPath = Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT_REPORT");
        var folderProbe = Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT_FOLDER");
        var advancedPath = Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT_ADVANCED");
        var sponsorPath = Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT_SPONSOR");
        var syncPath = Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT_SYNC");
        var themeMode = Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT_THEME");
        var playlistPath = Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT_PLAYLIST");
        var playlistDir = Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT_PLAYLIST_DIR");
        if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(keymapPath)
            && string.IsNullOrWhiteSpace(midiPath) && string.IsNullOrWhiteSpace(reportPath)
            && string.IsNullOrWhiteSpace(folderProbe) && string.IsNullOrWhiteSpace(advancedPath)
            && string.IsNullOrWhiteSpace(sponsorPath) && string.IsNullOrWhiteSpace(syncPath)
            && string.IsNullOrWhiteSpace(themeMode) && string.IsNullOrWhiteSpace(playlistPath)) return;

        window.Opened += (_, _) =>
        {
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "midikey-snap.log"),
                    $"[{DateTime.Now:HH:mm:ss}] Opened；main={path ?? "(未设)"}；keymap={keymapPath ?? "(未设)"}"
                    + $"；midi={midiPath ?? "(未设)"}；mix={mixMode ?? "(未设)"}；theme={themeMode ?? "(未设)"}\n");
            }
            catch { }

            if (!string.IsNullOrWhiteSpace(themeMode) && int.TryParse(themeMode, out int themeIndex))
            {
                window.SetThemeForDev(themeIndex);
                Log($"已切皮肤：档位 {themeIndex}");
            }

            var sizeOverride = Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT_SIZE");
            if (!string.IsNullOrWhiteSpace(sizeOverride))
            {
                var parts = sizeOverride!.Split('x');
                if (parts.Length == 2 && double.TryParse(parts[0], out double ow)
                                      && double.TryParse(parts[1], out double oh))
                {
                    window.Width = ow;
                    window.Height = oh;
                    Log($"已设主窗尺寸：{ow}x{oh}");
                }
            }
            if (!string.IsNullOrWhiteSpace(midiPath))
            {
                bool ok = window.LoadMidiFile(midiPath!);
                Log($"自动载入 MIDI：{midiPath} 结果={ok}");
            }
            if (string.Equals(mixMode, "all", StringComparison.OrdinalIgnoreCase))
            {
                window.MixAllPlayableTracksForDev();
            }

            var entrySpec = Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT_PLAYLIST_ENTRY");
            if (!string.IsNullOrWhiteSpace(entrySpec))
            {
                Log("歌单条目场景：" + window.LoadPlaylistEntryForDev(entrySpec!));
            }

            var overlayMode = Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT_OVERLAY");
            if (!string.IsNullOrWhiteSpace(overlayMode))
            {
                if (window.StartupOverlay != null) window.StartupOverlay.IsVisible = false;
                ShowOverlayForDev(window, overlayMode!);
                Log($"浮层快照场景：{overlayMode}");
            }

            if (!string.IsNullOrWhiteSpace(folderProbe))
            {
                RunFolderClickProbe(window, folderProbe!,
                    Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT_FOLDER_REPORT"));
                return;
            }

            if (!string.IsNullOrWhiteSpace(advancedPath))
            {
                CaptureAdvanced(window, advancedPath!);
                return;
            }
            if (!string.IsNullOrWhiteSpace(sponsorPath))
            {
                CaptureSponsor(window, sponsorPath!);
                return;
            }
            if (!string.IsNullOrWhiteSpace(syncPath))
            {
                CaptureSync(window, syncPath!);
                return;
            }
            if (!string.IsNullOrWhiteSpace(playlistPath))
            {
                CapturePlaylist(window, playlistPath!, playlistDir);
                return;
            }
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    ReportRowHeights(window);
                    if (!string.IsNullOrWhiteSpace(reportPath)) WriteColorReport(window, reportPath!);

                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        if (window.StartupOverlay != null)
                            window.StartupOverlay.IsVisible = false;
                        CaptureMain(window, path!);

                        if (window.StartupOverlay != null)
                        {
                            window.StartupOverlay.IsVisible = true;
                            CaptureMain(window, Suffix(path!, "-quickstart"));
                        }
                        Console.WriteLine($"UI snapshot saved: {path}");
                    }

                    if (!string.IsNullOrWhiteSpace(keymapPath))
                    {
                        if (window.StartupOverlay != null)
                            window.StartupOverlay.IsVisible = false;
                        CaptureKeymap(window, keymapPath!);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("UI snapshot failed: " + ex);
                }
                window.DevCleanUpForExit();
                Environment.Exit(0);
            };
            timer.Start();
        };
    }

    private static void ShowOverlayForDev(MainWindow w, string mode)
    {
        if (string.Equals(mode, "forced", StringComparison.OrdinalIgnoreCase))
        {
            w.ShowForcedUpdate("1.0.30", AutoUpdate.ReleasesUrl, "");
            return;
        }
        w.ShowTutorialForDev();
        if (string.Equals(mode, "quiz", StringComparison.OrdinalIgnoreCase) && w.PanelStartupQuiz != null)
            w.PanelStartupQuiz.IsVisible = true;
    }

    private static async void RunFolderClickProbe(MainWindow window, string dir, string? reportPath)
    {
        var sb = new System.Text.StringBuilder();
        void Line(string s)
        {
            sb.AppendLine(s);
            Console.WriteLine("[folder-probe] " + s);
        }

        try
        {
            if (string.IsNullOrWhiteSpace(reportPath))
                reportPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "midikey-folder-probe.txt");

            window.ScanMidiFolderForDev(dir);
            Line($"目录：{dir}");
            Line($"列表行数：{window.FolderList.Items.Count}（含子文件夹与占位行）");

            var fileRows = new List<ListBoxItem>();
            foreach (var o in window.FolderList.Items)
                if (o is ListBoxItem it && it.Tag is FolderRow row && !row.IsDir)
                    fileRows.Add(it);

            Line($"可点的 MIDI 行：{fileRows.Count} 首");
            if (fileRows.Count < 2)
            {
                Line("文件少于 2 首，这个场景测不了。");
                System.IO.File.WriteAllText(reportPath, sb.ToString());
                window.DevCleanUpForExit();
                Environment.Exit(2);
            }

            int initial = fileRows.Count;
            int steps = Math.Min(3, initial);
            int bad = 0;
            int done = 0;
            var wanted = new List<string>();
            for (int i = 0; i < steps; i++) wanted.Add(((FolderRow)fileRows[i].Tag!).Path);

            for (int step = 0; step < steps; step++)
            {
                string want = wanted[step];
                var target = fileRows[step];
                window.FolderList.SelectedItem = target;   
                await Task.Delay(900);
                done++;

                string actual = window.ParsedPathForDev;
                bool switched = string.Equals(actual, want, StringComparison.OrdinalIgnoreCase);
                if (!switched) bad++;

                fileRows = new List<ListBoxItem>();
                foreach (var o in window.FolderList.Items)
                    if (o is ListBoxItem it && it.Tag is FolderRow row && !row.IsDir)
                        fileRows.Add(it);

                bool alive = fileRows.Count == initial;      
                if (!alive) bad++;
                Line($"第 {step + 1} 次：点「{Path.GetFileName(want)}」→ 实际「{Path.GetFileName(actual)}」"
                     + $" {(switched ? "换过去了" : "没换过去")}；剩下可点 {fileRows.Count}/{initial} 首"
                     + $" {(alive ? "" : "← 列表塌了")}");

                if (!alive) break;
            }

            if (done < steps) { bad++; Line($"只跑到第 {done} 步就停了，剩下 {steps - done} 步没跑到。"); }
            Line(bad == 0 ? "结果：全部通过" : $"结果：{bad} 处不对");
            System.IO.File.WriteAllText(reportPath, sb.ToString());
            window.DevCleanUpForExit();
            Environment.Exit(bad == 0 ? 0 : 1);
        }
        catch (Exception ex)
        {
            Line("场景异常：" + ex);
            try { System.IO.File.WriteAllText(reportPath!, sb.ToString()); } catch { }
            Environment.Exit(1);
        }
    }

    private static void CaptureAdvanced(MainWindow owner, string path)
    {
        owner.OpenSettingsForDev();
        var first = owner.SettingsWindowForDev;
        first?.Close();
        owner.OpenSettingsForDev();

        var win = owner.SettingsWindowForDev;
        if (win == null)
        {
            Console.Error.WriteLine("设置窗口没打开");
            owner.DevCleanUpForExit();
            Environment.Exit(1);
        }
        win.SelectPageForDev(0);   

        if (Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT_ADVANCED_BOTTOM") == "1")
            owner.TxtQqCopied?.BringIntoView();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ShotVisual(win!, path);   
            owner.DevCleanUpForExit();
            Environment.Exit(0);
        };
        timer.Start();

        var guard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        guard.Tick += (_, _) =>
        {
            guard.Stop();
            ShotVisual(win!, path);
            owner.DevCleanUpForExit();
            Environment.Exit(0);
        };
        guard.Start();
    }

    private static void CapturePlaylist(MainWindow owner, string path, string? dir)
    {
        const string DevListName = "快照歌单";

        string folder = string.IsNullOrWhiteSpace(dir)
            ? System.IO.Path.Combine(AppContext.BaseDirectory, "示例MIDI")
            : dir!;
        if (!System.IO.Directory.Exists(folder))
        {
            string up = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "示例MIDI"));
            if (System.IO.Directory.Exists(up)) folder = up;
        }
        if (!System.IO.Directory.Exists(folder))
        {
            Console.Error.WriteLine($"歌单快照：找不到示例 MIDI 目录：{folder}");
            owner.DevCleanUpForExit();
            Environment.Exit(2);
        }

        owner.BuildPlaylistForDev(DevListName, folder);
        var win = owner.OpenPlaylistForDev(DevListName);
        if (win == null)
        {
            Console.Error.WriteLine("歌单窗口没打开");
            owner.DevCleanUpForExit();
            Environment.Exit(1);
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ShotVisual(win!, path);
            owner.DevCleanUpForExit();
            Environment.Exit(0);
        };
        timer.Start();

        var guard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        guard.Tick += (_, _) =>
        {
            guard.Stop();
            ShotVisual(win!, path);
            owner.DevCleanUpForExit();
            Environment.Exit(0);
        };
        guard.Start();
    }

    private static void ShotVisual(Visual visual, string path)
    {
        try
        {
            int w = Math.Max(1, (int)Math.Ceiling(visual.Bounds.Width));
            int h = Math.Max(1, (int)Math.Ceiling(visual.Bounds.Height));
            Save(visual, path, new PixelSize(w, h), new Vector(96, 96));
            Console.WriteLine($"Snapshot saved: {path} ({w}x{h})");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("snapshot failed: " + ex);
            Log("snapshot failed: " + ex);
        }
    }

    internal void ScanMidiFolderForDev(string dir) => ScanMidiFolder(dir);
    internal string ParsedPathForDev => _parsed?.FilePath ?? "";

    internal void MixAllPlayableTracksForDev()
    {
        foreach (var row in _tracks)
        {
            row.IsMix = true;
            if (!_mixOrder.Contains(row)) _mixOrder.Add(row);
        }
        SyncMixOrder();
        Log($"MIX=all：已勾 {_mixOrder.Count} 条合奏声部");
    }

    private static void WriteColorReport(MainWindow window, string reportPath)
    {
        var sb = new System.Text.StringBuilder();
        var rows = window._tracks;
        var active = window.ActiveRows();

        sb.AppendLine($"文件：{window._parsed?.FilePath ?? "(未载入)"}");
        sb.AppendLine($"参与演奏的行数：{active.Count}");
        sb.AppendLine();
        sb.AppendLine("== 左侧列表（每行文字颜色）==");
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            string listHex = HexOf(r.VoiceBrush);
            sb.AppendLine($"row{i} 轨{r.TrackLabel} 声道{r.ChannelLabel} 「{r.Name}」"
                          + $" 打击乐={r.IsPercussion} 合奏={r.IsMix} 优先级={r.MixRank}"
                          + $" 参与={r.IsVoiceActive} VoiceIndex={r.VoiceIndex} 列表文字色={listHex}");
        }

        sb.AppendLine();
        sb.AppendLine("== 每条参与演奏的声轨自带的音符（音高:个数）==");
        for (int k = 0; k < active.Count; k++)
        {
            var hist = new SortedDictionary<int, int>();
            foreach (var n in active[k].Candidate.Notes)
            {
                hist.TryGetValue(n.Pitch, out int c);
                hist[n.Pitch] = c + 1;
            }
            sb.AppendLine($"{k}号声部「{active[k].Name}」共 {active[k].Candidate.Notes.Count} 音："
                          + string.Join("、", hist.Select(kv => $"{Music.NoteName(kv.Key)}×{kv.Value}")));
        }

        sb.AppendLine();
        sb.AppendLine("== 卷帘音符（画出来的颜色）==");
        var rollNotes = window.Roll.Notes;
        for (int i = 0; i < rollNotes.Count; i++)
        {
            var n = rollNotes[i];
            bool inRange = window.Roll.IsInRangePitch(n.Pitch);
            int v = window.Roll.VoiceOfNote(n);
            string hex = inRange ? HexOf(ResourceBrush("BrushVoice" + Music.Mod(v, PianoRoll.VoiceCount)))
                                 : "灰(#C8D0D9)";
            var owners = new List<string>();
            for (int k = 0; k < active.Count; k++)
                foreach (var c in active[k].Candidate.Notes)
                    if (c.Pitch == n.Pitch && c.Start < n.End - 1e-6 && n.Start < c.End - 1e-6)
                    {
                        owners.Add(k + "号");
                        break;
                    }
            string flag = owners.Count > 1 ? $"  ★归属歧义({string.Join("/", owners)})" : "";
            sb.AppendLine($"note{i} {Music.NoteName(n.Pitch)}({n.Pitch}) {n.Start:F2}-{n.End:F2}s"
                          + $" Voice={n.Voice} 可演奏={inRange} 颜色号={v} 色={hex}{flag}");
        }

        sb.AppendLine();
        sb.AppendLine("== 汇总：每个颜色号在两侧是否一致 ==");
        var voiceColors = new SortedDictionary<int, int>();
        foreach (var n in rollNotes)
        {
            if (!window.Roll.IsInRangePitch(n.Pitch)) continue;
            int v = window.Roll.VoiceOfNote(n);
            voiceColors.TryGetValue(v, out int c);
            voiceColors[v] = c + 1;
        }
        foreach (var kv in voiceColors)
        {
            var row = rows.FirstOrDefault(r => r.VoiceIndex == kv.Key);
            string rollHex = HexOf(ResourceBrush("BrushVoice" + Music.Mod(kv.Key, PianoRoll.VoiceCount)));
            string rowHex = row == null ? "(没有这一行)" : HexOf(row.VoiceBrush);
            bool match = row != null && rowHex == rollHex;
            sb.AppendLine($"颜色号{kv.Key}：卷帘 {kv.Value} 个音 色={rollHex}；"
                          + $"左侧 {(row == null ? "(无)" : "「" + row.Name + "」")} 色={rowHex} → "
                          + (match ? "一致" : "不一致"));
        }

        sb.AppendLine();
        sb.AppendLine("== 同一音高被多条声轨占用时，各音符实际颜色 ==");
        var dupPitches = new SortedSet<int>();
        var pitchRows = new Dictionary<int, List<string>>();
        for (int k = 0; k < active.Count; k++)
        {
            foreach (var n in active[k].Candidate.Notes)
            {
                if (!pitchRows.TryGetValue(n.Pitch, out var list))
                {
                    list = new List<string>();
                    pitchRows[n.Pitch] = list;
                }
                string tag = $"{k}号声部「{active[k].Name}」";
                if (!list.Contains(tag)) list.Add(tag);
            }
        }
        foreach (var kv in pitchRows)
            if (kv.Value.Count > 1) dupPitches.Add(kv.Key);

        if (dupPitches.Count == 0)
        {
            sb.AppendLine("（本次合奏里没有被两条声轨共用的音高）");
        }
        else
        {
            foreach (int p in dupPitches)
            {
                sb.AppendLine($"{Music.NoteName(p)}({p}) 被 {string.Join("、", pitchRows[p])} 占用；卷帘里的音符：");
                int found = 0;
                foreach (var n in rollNotes)
                {
                    if (n.Pitch != p) continue;
                    found++;
                    int v = window.Roll.VoiceOfNote(n);
                    string hex = window.Roll.IsInRangePitch(p)
                        ? HexOf(ResourceBrush("BrushVoice" + Music.Mod(v, PianoRoll.VoiceCount)))
                        : "灰";
                    sb.AppendLine($"    {n.Start:F2}-{n.End:F2}s Voice={n.Voice} 颜色号={v} 色={hex}");
                }
                if (found == 0) sb.AppendLine("    （卷帘里没有这个音高：被让位规则或合并丢掉了）");
            }
        }

        System.IO.File.WriteAllText(reportPath, sb.ToString());
        Console.WriteLine($"UI color report saved: {reportPath}");
    }

    private static string HexOf(Avalonia.Media.IBrush? brush) =>
        brush is Avalonia.Media.SolidColorBrush s ? s.Color.ToString() : "(无画刷)";

    private static void ReportRowHeights(MainWindow window)
    {
        try
        {
            string line;
            if (window.RightColumn is not Grid g || g.RowDefinitions.Count < 3)
            {
                line = "[高度] 右栏 Grid 没找到";
            }
            else
            {
                double h0 = g.RowDefinitions[0].ActualHeight;
                double h1 = g.RowDefinitions.Count > 1 ? g.RowDefinitions[1].ActualHeight : 0;
                double h2 = g.RowDefinitions.Count > 3 ? g.RowDefinitions[3].ActualHeight : 0;
                string kind = window.OptionsArea?.GetType().Name ?? "缺失";
                double rollH = window.Roll?.Bounds.Height ?? -1;
                line = $"[高度] 右栏总高 {g.Bounds.Height:F0}；第0行(状态) {h0:F0}；"
                       + $"第1行(卷帘) {h1:F0}；第3行(选项) {h2:F0}；第3行根元素 {kind}；卷帘控件 {rollH:F0}";
            }
            Console.WriteLine(line);
            System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "midikey-snap.log"),
                line + "\n");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("row height report failed: " + ex.Message);
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "midikey-snap.log"),
                    "row height report failed: " + ex + "\n");
            }
            catch { }
        }
    }

    private static void CaptureMain(MainWindow window, string path)
    {
        var root = (Visual?)window.Content ?? window;
        int w = Math.Max(1, (int)Math.Ceiling(root.Bounds.Width));
        int h = Math.Max(1, (int)Math.Ceiling(root.Bounds.Height));
        Console.WriteLine($"bounds={w}x{h} windowScaling={window.RenderScaling}");
        Save(root, path, new PixelSize(w, h), new Vector(96, 96));
    }

    private static void CaptureKeymap(MainWindow owner, string path)
    {
        owner.OpenSettingsForDev();
        var win = owner.SettingsWindowForDev;
        if (win == null)
        {
            Console.Error.WriteLine("设置窗口没打开");
            owner.DevCleanUpForExit();
            Environment.Exit(1);
        }
        win.SelectPageForDev(1);   
        Log($"设置窗口已打开 IsVisible={win.IsVisible}，当前页 = 键位");

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Log("键位页快照计时器触发");
            ShotVisual(win, path);   
            owner.DevCleanUpForExit();   
            Environment.Exit(0);
        };
        timer.Start();

        var guard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        guard.Tick += (_, _) =>
        {
            guard.Stop();
            Log("兜底计时器触发");
            ShotVisual(win, path);
            owner.DevCleanUpForExit();   
            Environment.Exit(0);
        };
        guard.Start();
    }

    private static void CaptureSponsor(MainWindow owner, string path)
    {
        owner.OpenSettingsForDev();
        var win = owner.SettingsWindowForDev;
        if (win == null)
        {
            Console.Error.WriteLine("设置窗口没打开");
            owner.DevCleanUpForExit();
            Environment.Exit(1);
        }
        win.SelectPageForDev(2);   
        Log("设置窗口已打开，当前页 = 赞助");

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ShotVisual(win!, path);
            owner.DevCleanUpForExit();
            Environment.Exit(0);
        };
        timer.Start();

        var guard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        guard.Tick += (_, _) =>
        {
            guard.Stop();
            ShotVisual(win!, path);
            owner.DevCleanUpForExit();
            Environment.Exit(0);
        };
        guard.Start();
    }

    private static void CaptureSync(MainWindow owner, string path)
    {
        Log("CaptureSync 开始，目标 " + path);
        owner.OpenSettingsForDev();
        var win = owner.SettingsWindowForDev;
        if (win == null)
        {
            Console.Error.WriteLine("设置窗口没打开");
            owner.DevCleanUpForExit();
            Environment.Exit(1);
        }
        Log("设置窗口已打开，准备切到实验功能页");
        win.SelectPageForDev(3);
        string syncState = Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT_SYNC_STATE") ?? "idle";
        win.FillSyncPageForDev(syncState);
        Log($"设置窗口已打开，当前页 = 实验功能（假房间状态 = {syncState}）");

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Log("实验功能页快照计时器触发");
            ShotVisual(win!, path);
            Log("实验功能页快照已写 " + path);
            owner.DevCleanUpForExit();
            Environment.Exit(0);
        };
        timer.Start();

        var guard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        guard.Tick += (_, _) =>
        {
            guard.Stop();
            ShotVisual(win!, path);
            owner.DevCleanUpForExit();
            Environment.Exit(0);
        };
        guard.Start();
    }

    private static void Shot(Window win, string path)    {
        try
        {
            var root = (Visual?)win.Content ?? win;
            int w = Math.Max(1, (int)Math.Ceiling(root.Bounds.Width));
            int h = Math.Max(1, (int)Math.Ceiling(root.Bounds.Height));
            Log($"拍窗口 {w}x{h} IsVisible={win.IsVisible}");
            Save(root, path, new PixelSize(w, h), new Vector(96, 96));
            Log($"快照已写：{path} 存在={System.IO.File.Exists(path)}");
            Console.WriteLine($"Window snapshot saved: {path}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("window snapshot failed: " + ex);
            Log("window snapshot failed: " + ex);
        }
    }

    private static string LogPath() =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "midikey-snap.log");

    private static void Log(string line)
    {
        try { System.IO.File.AppendAllText(LogPath(), line + "\n"); } catch { }
    }

    private static void Save(Visual root, string path, PixelSize size, Vector dpi)
    {
        var rtb = new RenderTargetBitmap(size, dpi);
        rtb.Render(root);
        rtb.Save(path);
    }

    private static string Suffix(string path, string suffix)
    {
        int dot = path.LastIndexOf('.');
        return dot < 0 ? path + suffix : path[..dot] + suffix + path[dot..];
    }
}

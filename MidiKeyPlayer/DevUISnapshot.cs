using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MidiKeyPlayer.Engine;
using MidiKeyPlayer.Midi;

namespace MidiKeyPlayer;

/// <summary>
/// 界面快照 / 文件夹换歌回归的开关。设了任意一个 MIDIKEY_UI_SNAPSHOT* 变量就为 true。
/// 用途：<see cref="Program"/> 里跳过单实例锁 —— 这些探针不演奏、不发按键，
/// 允许与用户正在用的实例并存（与 PreviewProbeMode 同一个理由）。
/// </summary>
internal static class DevSnapshotMode
{
    internal static readonly bool On =
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT"))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT_KEYMAP"))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT_MIDI"))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT_MIX"))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT_REPORT"))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT_FOLDER"))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT_FOLDER_REPORT"))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT_ADVANCED"))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT_SPONSOR"))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT_THEME"));
}

/// <summary>
/// 【开发用，可删】无显示器时把界面渲染成 PNG 再退出。
///
/// MIDIKEY_UI_SNAPSHOT=/path/main.png
///     在主窗打开后拍主界面（“快速上手”浮层先关掉拍一张、再打开拍一张）。
/// MIDIKEY_UI_SNAPSHOT_KEYMAP=/path/keymap.png
///     额外打开「键位设置」窗口并把它也拍成 PNG。
/// MIDIKEY_UI_SNAPSHOT_MIDI=/path/song.mid
///     主窗打开后自动走「打开 MIDI 文件」这条链路载入该文件（同一个 LoadMidiFile）。
/// MIDIKEY_UI_SNAPSHOT_MIX=all
///     载入后把左侧所有非打击乐候选按列表顺序勾进合奏（第一个 = 0 号声部）。
/// MIDIKEY_UI_SNAPSHOT_REPORT=/path/report.txt
///     拍图前把「左侧每行文字颜色 ↔ 卷帘每个音符颜色」的对照表写成文本，便于逐行核对。
/// MIDIKEY_UI_SNAPSHOT_THEME=0|1|2
///     拍图前先切皮肤（0 自动 / 1 浅色 / 2 深色），走的是设置里那个下拉框的同一条链路。
/// MIDIKEY_UI_SNAPSHOT_OVERLAY=notice|forced|quiz
///     配合 MIDIKEY_UI_SNAPSHOT 用：把「免费声明」或「强制更新」浮层打开再拍图。
///     强制更新那条传空资产地址，所以不会真的联网下载。
/// MIDIKEY_UI_SNAPSHOT_ADVANCED=/path/advanced.png
///     打开设置窗口，切到「常规」页拍一张（并顺带走一遍「开 → 关 → 再开」）。
/// MIDIKEY_UI_SNAPSHOT_SPONSOR=/path/sponsor.png
///     打开设置窗口，切到第三页「赞助」拍一张（爱发电置顶 + B 站 / GitHub）。
///
/// 各变量可以只设一个；全都不设则本文件无任何行为。
/// 删本文件时记得同时删 MainWindow 里的 InstallDevSnapshot(this)。
/// </summary>
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
        var themeMode = Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT_THEME");
        if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(keymapPath)
            && string.IsNullOrWhiteSpace(midiPath) && string.IsNullOrWhiteSpace(reportPath)
            && string.IsNullOrWhiteSpace(folderProbe) && string.IsNullOrWhiteSpace(advancedPath)
            && string.IsNullOrWhiteSpace(sponsorPath) && string.IsNullOrWhiteSpace(themeMode)) return;

        window.Opened += (_, _) =>
        {
            // 无控制台时也能留证据：把诊断写到临时目录
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "midikey-snap.log"),
                    $"[{DateTime.Now:HH:mm:ss}] Opened；main={path ?? "(未设)"}；keymap={keymapPath ?? "(未设)"}"
                    + $"；midi={midiPath ?? "(未设)"}；mix={mixMode ?? "(未设)"}；theme={themeMode ?? "(未设)"}\n");
            }
            catch { }

            // 换皮肤：走设置里那个下拉框的同一条链路（用户改档位就是这个入口），
            // 用来验证「不重启就能换色」，而不是只看启动时读设置的结果。
            if (!string.IsNullOrWhiteSpace(themeMode) && int.TryParse(themeMode, out int themeIndex))
            {
                window.SetThemeForDev(themeIndex);
                Log($"已切皮肤：档位 {themeIndex}");
            }

            // 载入 MIDI 与勾选合奏都在拍图之前做完：截图看到的就是用户操作后的真实状态
            if (!string.IsNullOrWhiteSpace(midiPath))
            {
                bool ok = window.LoadMidiFile(midiPath!);
                Log($"自动载入 MIDI：{midiPath} 结果={ok}");
            }
            if (string.Equals(mixMode, "all", StringComparison.OrdinalIgnoreCase))
            {
                window.MixAllPlayableTracksForDev();
            }

            // 浮层快照（MIDIKEY_UI_SNAPSHOT_OVERLAY=notice|forced）：必须在这里挂上。
            // 900ms 那个计时器是在同一帧里改可见性再拍图，拍到的还是上一帧的画面，
            // 所以浮层要提前开，等下一次渲染完再拍。
            var overlayMode = Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT_OVERLAY");
            if (!string.IsNullOrWhiteSpace(overlayMode))
            {
                if (window.QuickStartOverlay != null) window.QuickStartOverlay.IsVisible = false;
                ShowOverlayForDev(window, overlayMode!);
                Log($"浮层快照场景：{overlayMode}");
            }

            // 文件夹曲目卡换歌回归场景：这条链路自己收尾并退出，不跟快照计时器混在一起
            if (!string.IsNullOrWhiteSpace(folderProbe))
            {
                RunFolderClickProbe(window, folderProbe!,
                    Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT_FOLDER_REPORT"));
                return;
            }

            // 高级设置窗口快照：先按「高级设置…」把内容搬过去，再拍那个窗口
            if (!string.IsNullOrWhiteSpace(advancedPath))
            {
                CaptureAdvanced(window, advancedPath!);
                return;
            }
            // 赞助页快照：设置窗口的第三页，内容不多，直接切过去拍
            if (!string.IsNullOrWhiteSpace(sponsorPath))
            {
                CaptureSponsor(window, sponsorPath!);
                return;
            }
            // 等布局就绪，900ms 是实测够用的值
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    ReportRowHeights(window);
                    if (!string.IsNullOrWhiteSpace(reportPath)) WriteColorReport(window, reportPath!);

                    // “快速上手”浮层会盖住主界面：先关掉拍主界面，再开它拍浮层
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        if (window.QuickStartOverlay != null)
                            window.QuickStartOverlay.IsVisible = false;
                        CaptureMain(window, path!);

                        if (window.QuickStartOverlay != null)
                        {
                            window.QuickStartOverlay.IsVisible = true;
                            CaptureMain(window, Suffix(path!, "-quickstart"));
                        }
                        Console.WriteLine($"UI snapshot saved: {path}");
                    }

                    if (!string.IsNullOrWhiteSpace(keymapPath))
                    {
                        if (window.QuickStartOverlay != null)
                            window.QuickStartOverlay.IsVisible = false;
                        // 键位窗口的拍照与退出都在它自己的计时器里做：
                        // 这里绝不能立刻 Environment.Exit，否则那个计时器根本跑不到。
                        CaptureKeymap(window, keymapPath!);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("UI snapshot failed: " + ex);
                }
                // A19：退出前先走一遍主窗的显式清理（停播放与试听、停热键与 MIDI 服务、
                // 释放按键、移除托盘图标），不再直接 Environment.Exit 把清理全部跳过
                window.DevCleanUpForExit();
                Environment.Exit(0);
            };
            timer.Start();
        };
    }

    /// <summary>
    /// 【开发用，可删】浮层快照：用真实代码把免费声明 / 强制更新浮层铺出来拍图。
    /// 强制更新那条故意传空资产地址，所以不会真的联网下载，只走文案与失败提示。
    /// </summary>
    private static void ShowOverlayForDev(MainWindow w, string mode)
    {
        if (string.Equals(mode, "forced", StringComparison.OrdinalIgnoreCase))
            w.ShowForcedUpdate("1.0.30", AutoUpdate.ReleasesUrl, "");
        else if (string.Equals(mode, "quiz", StringComparison.OrdinalIgnoreCase))
            w.ShowQuizOverlay();
        else
            w.ShowFreeNoticeOverlay();
    }

    /// <summary>
    /// 【开发用】回归场景：在「文件夹曲目」卡里连续换歌三次，检查换过一首之后还能不能接着换。
    ///
    /// MIDIKEY_UI_SNAPSHOT_FOLDER=&lt;目录&gt;
    /// MIDIKEY_UI_SNAPSHOT_FOLDER_REPORT=&lt;报告路径&gt;（不写就落 %TEMP%\midikey-folder-probe.txt）
    ///
    /// 退出码：0 = 三次都换成功；1 = 有一步没换过去，或列表塌了。
    ///
    /// 背景：用户报过「选了一首之后，别的就点不动了」。根因是换歌时
    /// RefreshFolderUi 带着选中项清空 FolderList.Items，ListBox 的选择模型随后
    /// 拿旧下标回查条目，抛 ArgumentOutOfRangeException；异常被 LoadMidiFile 接住只写日志，
    /// 界面上留下的是曲目卡再也点不动。修复见 FolderList_SelectionChanged 的说明。
    /// 这个场景就是那个 bug 的回归测试：连续换三首，每步都要换过去，且列表行数不塌。
    /// </summary>
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

            // 只取 MIDI 文件行（子文件夹行单击不载入，要双击才进）
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
                window.FolderList.SelectedItem = target;   // 等价于用户点这一行
                await Task.Delay(900);
                done++;

                string actual = window.ParsedPathForDev;
                bool switched = string.Equals(actual, want, StringComparison.OrdinalIgnoreCase);
                if (!switched) bad++;

                // 每一步之后重新取行对象：列表每次重建，旧对象已经不在 Items 里
                fileRows = new List<ListBoxItem>();
                foreach (var o in window.FolderList.Items)
                    if (o is ListBoxItem it && it.Tag is FolderRow row && !row.IsDir)
                        fileRows.Add(it);

                bool alive = fileRows.Count == initial;      // 列表还能点：行数没塌
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

    /// <summary>
    /// 【开发用】打开「高级设置」窗口并拍成 PNG。
    /// MIDIKEY_UI_SNAPSHOT_ADVANCED=&lt;png 路径&gt;
    /// </summary>
    private static void CaptureAdvanced(MainWindow owner, string path)
    {
        // 先走一遍「开 → 关 → 再开」：关窗要把常规页的内容还给主窗，再开要能再搬一次。
        // 这一圈跑通，才说明内容归属来回搬是干净的。
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
        win.SelectPageForDev(0);   // 常规页

        // MIDIKEY_UI_SNAPSHOT_ADVANCED_BOTTOM=1：拍图前把「关于」卡滚到底。
        // 常规页比窗口高时默认只能拍到上面几张卡，免费声明与作者链接（卡片最下面）就拍不到。
        if (Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT_ADVANCED_BOTTOM") == "1")
            owner.TxtQqCopied?.BringIntoView();

        // 与键位页快照同一个做法：计时器里拍照再退出；另配一个兜底计时器
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ShotVisual(win!, path);   // 拍窗口本体：拍 Content 会把搬过来的控件画重影
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

    /// <summary>把任意可视化元素渲染成 PNG（按元素自身尺寸）。</summary>
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

    /// <summary>【开发用】按目录扫描并刷新曲目卡（回归场景用）。</summary>
    internal void ScanMidiFolderForDev(string dir) => ScanMidiFolder(dir);
    /// <summary>【开发用】当前已载入的文件路径。没载入就是空串。</summary>
    internal string ParsedPathForDev => _parsed?.FilePath ?? "";

    /// <summary>
    /// 【开发用】把左侧所有候选（含打击乐轨）按列表顺序勾进合奏：第一个勾的 = 0 号声部，
    /// 颜色号依次 0,1,2…。收尾走 <see cref="SyncMixOrder"/>，与用户手点勾选框完全同一条链路。
    /// 打击乐轨现在也可以勾选，所以这里不再跳过它们。
    /// </summary>
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

    /// <summary>
    /// 【开发用】把「左侧每行文字颜色 ↔ 卷帘里每个音符的颜色」写成对照表。
    /// 颜色号来源：左侧 = VoiceIndexOf，卷帘 = PianoRoll.VoiceOfNote（渲染用的同一个函数）。
    /// 两者都取主题里同一份 BrushVoiceN 资源，所以文本里的色值就是画面上的色值。
    /// </summary>
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
            // 这个音同时落在哪些声轨的音符区间里 = 颜色归属本来就有歧义
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

    /// <summary>
    /// 拍图前把右栏三行的高度打到控制台，便于核对「卷帘是否拿回完整剩余高度」。
    /// 只在设了快照变量时运行，正常使用无输出、无行为。
    /// </summary>
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
                double h1 = g.RowDefinitions[1].ActualHeight;
                double h2 = g.RowDefinitions[2].ActualHeight;
                string kind = window.OptionsArea?.GetType().Name ?? "缺失";
                double rollH = window.Roll?.Bounds.Height ?? -1;
                line = $"[高度] 右栏总高 {g.Bounds.Height:F0}；第0行(状态) {h0:F0}；"
                       + $"第1行(卷帘) {h1:F0}；第2行(选项) {h2:F0}；第2行根元素 {kind}；卷帘控件 {rollH:F0}";
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

    /// <summary>
    /// 把主窗内容渲染成 PNG。96 DPI + 与布局等大的画布，和真实窗口 1:1 对应
    /// （试过 192 DPI + 双倍画布，会被窗口 RenderScaling 再乘一次只剩左上角，别用）。
    /// </summary>
    private static void CaptureMain(MainWindow window, string path)
    {
        var root = (Visual?)window.Content ?? window;
        int w = Math.Max(1, (int)Math.Ceiling(root.Bounds.Width));
        int h = Math.Max(1, (int)Math.Ceiling(root.Bounds.Height));
        Console.WriteLine($"bounds={w}x{h} windowScaling={window.RenderScaling}");
        Save(root, path, new PixelSize(w, h), new Vector(96, 96));
    }

    /// <summary>
    /// 打开「键位设置」窗口并拍图。
    /// 用 Show() 而不是 ShowDialog()：模态对话框会一直阻塞到用户关闭，而这里拍完就要退出。
    /// 拍照走 DispatcherTimer（主窗快照用的就是这个办法，实测能跑到），不用 Post。
    /// </summary>
    private static void CaptureKeymap(MainWindow owner, string path)
    {
        // 键位页现在是设置窗口的第二页：打开设置，切到「键位」，再拍整窗。
        owner.OpenSettingsForDev();
        var win = owner.SettingsWindowForDev;
        if (win == null)
        {
            Console.Error.WriteLine("设置窗口没打开");
            owner.DevCleanUpForExit();
            Environment.Exit(1);
        }
        win.SelectPageForDev(1);   // 键位页
        Log($"设置窗口已打开 IsVisible={win.IsVisible}，当前页 = 键位");

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Log("键位页快照计时器触发");
            ShotVisual(win, path);   // 拍窗口本体：拍 Content 会把搬过来的控件画重影
            owner.DevCleanUpForExit();   // A19：退出前走一遍显式清理
            Environment.Exit(0);
        };
        timer.Start();

        // 双保险：万一 700ms 那个没跑到，1.5s 这个也要落盘并退出
        var guard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        guard.Tick += (_, _) =>
        {
            guard.Stop();
            Log("兜底计时器触发");
            ShotVisual(win, path);
            owner.DevCleanUpForExit();   // A19：退出前走一遍显式清理
            Environment.Exit(0);
        };
        guard.Start();
    }

    /// <summary>
    /// 【开发用，可删】赞助页快照：设置窗口的第三页（爱发电置顶 + B 站 / GitHub）。
    /// 与键位页、常规页同一个做法：切页 → 等布局 → 拍窗口本体 → 退出，另配一个兜底计时器。
    /// </summary>
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
        win.SelectPageForDev(2);   // 赞助页
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

    /// <summary>把任意窗口渲染成 PNG。</summary>
    private static void Shot(Window win, string path)
    {
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

    /// <summary>无控制台（WinExe）时把诊断写进临时目录的日志文件。</summary>
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

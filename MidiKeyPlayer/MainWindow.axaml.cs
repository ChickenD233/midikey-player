using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Selection;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Text;
using MidiKeyPlayer.Engine;
using MidiKeyPlayer.Input;
using MidiKeyPlayer.Midi;
using MidiKeyPlayer.Persist;
// Avalonia.Input 也有 KeyBinding（手势绑定），这里给键位方案的绑定起个别名，避免歧义
using KeyBinding = MidiKeyPlayer.Engine.KeyBinding;

namespace MidiKeyPlayer;

/// <summary>主窗口：选主旋律与播放控制、全局热键、进度跳转、实时变速/移调，托盘后台与设置记忆。</summary>
public partial class MainWindow : Window
{
    private readonly ObservableCollection<TrackRowVM> _tracks = new();
    private readonly List<TrackRowVM> _mixOrder = new();   // 勾选合奏的顺序 = 主次（先勾=主）

    private ParsedMidi? _parsed;
    private TrackRowVM? _selected;
    private List<MappedNote> _playNotes = new();
    private PlaybackEngine? _engine;
    private DispatcherTimer? _uiTimer;
    private DispatcherTimer? _countdownTimer;
    private DispatcherTimer? _liveTimer;
    private DispatcherTimer? _saveDeb;
    private readonly DispatcherTimer _previewDeb;
    private int _countdownLeft;
    private bool _busy;

    private bool _seeking;          // 用户正在拖进度条
    private List<MappedNote> _previewNotes = new();   // 全量音符（含超音域），供卷帘与定位使用
    private double _previewSeconds;                   // 未播放时的定位秒数
    private int _noteCount;                           // 当前谱面音符数（避免每次点击都重算）
    private readonly ScoreEditor _editor = new();     // 手动编辑后的谱面
    private bool _editing;                            // true = 用编辑结果，不再用自动提取
    private bool _editsExported;                      // true = 这份手动改动已经导出过 MIDI（UI-01 / UI-02）
    private bool _discardPrompting;                   // RV-09：丢弃改动确认框正在显示，不重复弹
    private bool _revertingTrack;                     // RV-09：取消后回退主旋律轨选中，忽略自触发的事件
    private DispatcherTimer? _focusTimer;             // 焦点守卫：焦点离开目标程序自动暂停
    private bool _focusAutoPaused;                    // 当前暂停是焦点守卫触发的（只有这种才自动继续）
    private uint _gamePid;                            // 目标程序进程 PID（同进程窗口都算「在游戏里」）
    private bool _revertingMix;                       // RV-09：取消后回退合奏勾选，忽略自触发的事件
    private bool _exitConfirming;                     // 关窗确认框正在显示，避免连点 × 弹多个
    private bool _helpOn;                             // 卷帘右侧操作说明：默认收起，保持界面干净
    private bool _liveQueued;       // 已排队待应用的实时移调
    private readonly LivePlayback _livePlay = new();   // MIDI 设备实时演奏（issue #4）
    private bool _midiStarting;     // 正在后台打开 MIDI 设备
    private IntPtr _gameHwnd;       // 播放期间记住的目标窗口（用于停止时把焦点还给它）
    private double _removedLeadSec; // “去除开头空拍”实际剪掉的秒数（本轮）
    private readonly AppConfig _cfg;
    private TrayIcon? _tray;
    private bool _quitNow;

    // —— 歌单与连播（v1.1.21）——
    /// <summary>左栏正在显示的那份歌单；null = 没选过，歌单卡隐藏。</summary>
    private Playlist? _playlist;
    /// <summary>歌单窗口，同一时间只开一个（和设置窗口一个规矩）。</summary>
    private PlaylistWindow? _playlistWindow;
    /// <summary>曲目卡的铺界面守卫：程序化改选中项会触发 SelectionChanged，那一次不是用户点击。</summary>
    private bool _playlistSyncing;
    /// <summary>歌单曲目列表最多显示多少行，超过的合并成一条说明。</summary>
    private const int PlaylistMenuMax = 50;
    private bool _sourceIsPlaylist;
    /// <summary>连播连续失败了几首。放不出来的曲子跳过它，连败到上限就停（见 PlayAdvance）。</summary>
    private int _consecutiveFailures;
    /// <summary>本轮连播已经放了几首、列表共几首，只为状态行那句「连播 3/12」。</summary>
    private int _playlistPos = -1;
    /// <summary>随机播放用的发生器。每次开始连播重新播种，免得每次都同一条随机串。</summary>
    private Random _shuffleRng = new();
    private string _nextSongLabel = "";

    private bool _advancing;

    private static readonly int[] CountdownOptions = { 0, 3, 5, 10 };
    private const int FixedLeadMs = 25;
    private const double SeekStepSeconds = 5;   // 快进/后退热键的步长

    private bool _uiReady;   // 构造期各下拉框初始化会触发 *Changed，此时不应写日志
    private bool _warnIsOk;  // 提示行（LblWarn）现在是不是「全部有键」那种绿色；换皮肤时按它重涂
    private bool _devThemeOverride;   // 【开发用】探针切皮肤中：只上色，不写设置
    private string _updateUrl = "";     // 有新版本时的下载页
    private string _updateTag = "";     // 有新版本时的版本号
    private string _updateAssetUrl = "";   // 更新包（zip）直链；空 = 退回手动下载
    private string _updateNewExe = "";     // 已下载且校验通过的新 exe；空 = 尚未就绪
    private bool _updateBusy;              // 正在下载或正在应用更新
    private CancellationTokenSource? _updateCts;
    private OverlayWindow? _overlay;        // 播放悬浮窗（倒计时 / 进度 / 当前音）
    private bool _overlayMuted;             // 用户点过浮窗上的 ✕：本次播放不再弹它（设置里的开关不动，下次播放恢复）
    private double _overlayLastElapsed = -1;   // 上次推给悬浮窗的进度（节流用；-1 = 还没推过）
    private bool _overlayLastPaused;
    private int _overlayLastLoop = -1;
    private MappingResult? _lastMapping;    // 最近一次映射结果（LblWarn 的「跳过明细」用）

    private bool HasUnexportedEdits => _editing && !_editsExported;

    // —— 键位方案（控件在 KeymapWindow 里，这里只存当前方案与方案名） ——
    private KeymapProfile _keymap = KeymapProfile.Default;

    public MainWindow()
    {
        InitializeComponent();

        TrackList.ItemsSource = _tracks;

        // 倒计时下拉：0/3/5/10 秒，默认 3 秒
        CountdownCombo.ItemsSource = new List<string> { "0秒(立即)", "3秒", "5秒", "10秒" };
        CountdownCombo.SelectedIndex = 1;

        // 统一控制热键下拉：F1..F12（默认 F6 = 开始/暂停/继续）
        var fkeys = new List<string> { "无" };
        for (int i = 1; i <= 12; i++) fkeys.Add("F" + i);
        HotkeyControlCombo.ItemsSource = fkeys;
        HotkeyControlCombo.SelectedIndex = 6;   // F6
        HotkeyRewindCombo.ItemsSource = fkeys;
        HotkeyRewindCombo.SelectedIndex = 5;    // F5
        HotkeyForwardCombo.ItemsSource = fkeys;
        HotkeyForwardCombo.SelectedIndex = 7;   // F7
        HotkeyPrevCombo.ItemsSource = fkeys;
        HotkeyPrevCombo.SelectedIndex = 8;      // F8：上一首
        HotkeyNextCombo.ItemsSource = fkeys;
        HotkeyNextCombo.SelectedIndex = 9;      // F9：下一首

        // 输入兼容档位：决定修饰键与音键之间的物理时间余量
        TimingCombo.ItemsSource = InputTiming.Names;
        TimingCombo.SelectedIndex = 1;          // 标准

        // 输入方式：SendInput（默认）或罗技 G HUB 驱动（绕过 SendInput 屏蔽）
        BackendCombo.ItemsSource = new[] { "SendInput（Windows 自带）", "罗技 G HUB 驱动" };

        // —— 记住上次设置 ——
        _cfg = AppConfig.Load();
        CountdownCombo.SelectedIndex = Math.Clamp(_cfg.CountdownIndex, 0, 3);
        HotkeyControlCombo.SelectedIndex = Math.Clamp(_cfg.ControlHotkeyIndex, 0, 12);
        HotkeyRewindCombo.SelectedIndex = Math.Clamp(_cfg.RewindHotkeyIndex, 0, 12);
        HotkeyForwardCombo.SelectedIndex = Math.Clamp(_cfg.ForwardHotkeyIndex, 0, 12);
        HotkeyPrevCombo.SelectedIndex = Math.Clamp(_cfg.PrevSongHotkeyIndex, 0, 12);
        HotkeyNextCombo.SelectedIndex = Math.Clamp(_cfg.NextSongHotkeyIndex, 0, 12);
        SliderSpeed.Value = Math.Clamp(_cfg.Speed, 10, 400);
        SliderTranspose.Value = Math.Clamp(_cfg.Transpose, -24, 24);
        ChkTrimLead.IsChecked = _cfg.TrimLead;
        ChkFoldOctave.IsChecked = _cfg.FoldOctave;
        ChkAutoMinimize.IsChecked = _cfg.AutoMinimizeOnPlay;
        ChkFocusGuard.IsChecked = _cfg.FocusGuard;   // 焦点守卫默认开：按键只进游戏
        ChkShowPreflight.IsChecked = _cfg.ShowPreflight;
        PreflightRow.IsVisible = _cfg.ShowPreflight;   // 默认开：自检常驻主界面状态卡
        ChkOverlay.IsChecked = _cfg.OverlayEnabled;    // 悬浮窗默认开
        ChkOverlayHideOnPause.IsChecked = _cfg.OverlayHideOnPause;   // 暂停后收起来（默认开）
        TxtAboutVersion.Text = $"MIDI 按键播放器 v{AutoUpdate.CurrentVersion}";
        FillAboutLinks();
        if (_cfg.DisclaimerAccepted) DisclaimerBar.IsVisible = false;   // 确认过一次就不再显示
        ThemeCombo.ItemsSource = ThemeSwitch.Names;    // 自动 / 浅色 / 深色，下标就是设置里的取值
        ThemeCombo.SelectedIndex = ThemeSwitch.Clamp(_cfg.ThemeMode);
        TimingCombo.SelectedIndex = Math.Clamp(_cfg.TimingIndex, 0, 2);
        BackendCombo.SelectedIndex = Math.Clamp(_cfg.InputBackend, 0, 1);   // 触发 Backend_Changed → 应用后端
        RefreshRecentUi();
        RestoreFolderFromConfig();
        RefreshFolderUi();   // 上面没恢复出目录时：左栏的文件夹曲目卡保持隐藏
        RestorePlaylistFromConfig();   // 同上：没选过歌单时歌单卡保持隐藏
        ApplyPlayModeUi();             // 上次选的演奏模式写回按钮

        _keymap = KeymapProfile.Current ?? KeymapProfile.Default;
        if (TxtKeymapName != null) TxtKeymapName.Text = _keymap.Name;
        ApplyProfileSettings(_keymap.Name);

        // MIDI 设备接入（issue #4）
        _livePlay.Timing = InputTiming.FromIndex(TimingCombo.SelectedIndex);
        _livePlay.BaseOctave = Math.Clamp(_cfg.MidiBaseOctave, 1, 6);
        _livePlay.MinVelocity = Math.Clamp(_cfg.MidiMinVelocity, 1, 127);
        _livePlay.AutoFit = _cfg.MidiAutoFit;
        SliderMidiOctave.Value = _livePlay.BaseOctave;
        SliderMidiVelocity.Value = _livePlay.MinVelocity;
        ChkMidiAutoFit.IsChecked = _livePlay.AutoFit;
        ChkMidiLive.IsChecked = false;   // 实时演奏默认关：设置里记住的设备名只用来预选
        UpdateMidiLabels();
        MidiInputService.NoteEvent += OnMidiNote;
        MidiInputService.Error += s => UiPost(() =>
        {
            InsertLog("[MIDI] " + s);
            if (ChkMidiLive.IsChecked != true) return;
            _livePlay.ReleaseAll();
            ChkMidiLive.IsChecked = false;   // 走 MidiLive_Changed → StopMidiLive，顺带停掉设备监听
            InsertLog("[MIDI] 设备异常，已松开全部按键并关闭「MIDI 设备实时演奏」。修好后点「刷新」重新接入。");
        });
        _livePlay.Log += s => UiPost(() => InsertLog(s));
        _livePlay.NoteObserved += OnMidiObserved;
        RefreshMidiDevices();

        _saveDeb = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveDeb.Tick += (_, _) =>
        {
            _saveDeb!.Stop();
            SaveSettings();
        };

        // 探针模式不注册全局热键：探针只放音频，不碰键盘（见 DevPreviewProbe.cs）
        if (Input.GlobalHotkeys.IsAvailable && !PreviewProbeMode.On)
        {
            Input.GlobalHotkeys.Status += s => UiPost(() => InsertLog(s));
            Input.GlobalHotkeys.KeyState += OnGlobalKeyWorker;
            ReconfigureHotkeys();
            Input.GlobalHotkeys.Start();
        }

        // 捕获“已被滑块内部处理”的指针事件，实现任意位置点击/拖动跳转
        SliderProgress.AddHandler(InputElement.PointerPressedEvent, Progress_PointerPressed,
            RoutingStrategies.Bubble, handledEventsToo: true);
        SliderProgress.AddHandler(InputElement.PointerMovedEvent, Progress_PointerMoved,
            RoutingStrategies.Bubble, handledEventsToo: true);
        SliderProgress.AddHandler(InputElement.PointerReleasedEvent, Progress_PointerReleased,
            RoutingStrategies.Bubble, handledEventsToo: true);

        Roll.SeekPreview += OnRollPreview;
        Roll.SeekCommitted += OnRollSeek;
        Roll.SelectionChanged += UpdateEditUi;
        Roll.EditCommitted += OnRollEditCommitted;
        Roll.ViewChanged += OnRollViewChanged;
        ChkFollow.IsCheckedChanged += (_, _) => Roll.SetFollow(ChkFollow.IsChecked == true);
        KeyDown += OnWindowKeyDown;
        Roll.SnapEnabled = ChkSnap.IsChecked == true;
        Roll.SetFollow(ChkFollow.IsChecked == true);
        if (RollHelp != null) UpdateHelpVisibility();
        SizeChanged += (_, _) => UpdateHelpVisibility();

        ActualThemeVariantChanged += (_, _) =>
        {
            if (!_uiReady) return;   // 构造期资源还没就位，主题变体也在初始化
            ApplyThemeColors();
        };

        _previewDeb = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _previewDeb.Tick += (_, _) =>
        {
            _previewDeb.Stop();
            RefreshPreview(keepView: true);
        };

        UpdateSettingLabels();
        UpdateVoiceRoles();
        RefreshPreview();
        SetIdleHint();
        UpdateTransportUi();
        _uiReady = true;

        // 启动即自检，状态行从一开始就有结论
        RunPreflight();

        // 后台静默查更新（不阻塞界面；没网就跳过）
        _ = CheckUpdateAsync();

        InsertLog("欢迎使用 MIDI 按键播放器");
        InsertLog("用法：打开 MIDI → 点一行作为主旋律 → 按 F6，倒计时内切到目标程序并装备乐器。");
        InsertLog("控制热键：F6 = 开始 / 暂停 / 继续（目标程序中生效，可改）。");

        // 自动更新成功确认：版本号与上次运行不同 → 刚被更新流程替换过，写日志留证
        string curVer = AutoUpdate.CurrentVersion;
        if (_cfg.LastRunVersion.Length > 0 && _cfg.LastRunVersion != curVer)
        {
            InsertLog($"已自动更新：v{_cfg.LastRunVersion} → v{curVer}");
            global::MidiKeyPlayer.Persist.LogFile.Append($"[更新] 已从 v{_cfg.LastRunVersion} 更新到 v{curVer}");
        }
        if (_cfg.LastRunVersion != curVer) { _cfg.LastRunVersion = curVer; _cfg.Save(); }

        // 免费声明：文案版本变了就弹一次（老用户更新上来也会看到）。链接顺手写进日志。
        InsertLog($"本程序免费开源。{AutoUpdate.FreeNotice}");
        InsertLog($"作者 B 站：{AutoUpdate.AuthorSpaceUrlShort}　反馈 QQ 群：{AutoUpdate.QqGroupNumber}");

        // 探针模式不建托盘图标：无人值守跑测，不往用户托盘里塞东西
        if (OperatingSystem.IsWindows() && !PreviewProbeMode.On)
        {
            SetupTray();
            Opened += (_, _) => EnsureTray();
        }
        Opened += (_, _) => ShowStartupOnce();

        InstallDevSnapshot(this);   // 【开发用，可删】设了 MIDIKEY_UI_SNAPSHOT 才生效，见 DevUISnapshot.cs
        InstallPreviewProbe(this);  // 【开发用，可删】设了 MIDIKEY_PREVIEW_PROBE=1 才生效，见 DevPreviewProbe.cs
    }

    private void ShowStartupOnce()
    {
        if (_cfg == null || StartupOverlay == null) return;
        bool firstRun = !_cfg.FirstRunDone;
        bool noticeDue = !string.Equals(_cfg.NoticeShownVersion, AutoUpdate.NoticeVersion,
                                        StringComparison.Ordinal);
        if (!firstRun && !noticeDue && _cfg.AuthorQuizPassed) return;
        ShowStartupOverlay();
    }

    /// <summary>把启动浮层铺出来。验证题只在没答对过时显示。</summary>
    private void ShowStartupOverlay()
    {
        if (StartupOverlay == null) return;
        if (TxtStartupVersion != null)
            TxtStartupVersion.Text = $"v{AutoUpdate.CurrentVersion}　免费开源";
        if (PanelStartupQuiz != null)
            PanelStartupQuiz.IsVisible = _cfg != null && !_cfg.AuthorQuizPassed;
        if (TxtQuizError != null) TxtQuizError.IsVisible = false;
        if (TxtQuizAnswer != null) TxtQuizAnswer.Text = "";
        StartupOverlay.IsVisible = true;
    }

    /// <summary>「使用教程」：再弹一遍启动浮层（验证题已答过就不再显示）。</summary>
    private void Tutorial_Click(object? sender, RoutedEventArgs e)
    {
        ShowStartupOverlay();
        BtnStartupOk?.Focus();
    }

    /// <summary>【开发用】浮层快照走这条：与「使用教程」按钮同一条链路。</summary>
    internal void ShowTutorialForDev() => Tutorial_Click(null, new RoutedEventArgs());

    /// <summary>启动浮层上的「知道了」：记下已看过，关掉浮层。</summary>
    private void StartupDone_Click(object? sender, RoutedEventArgs e)
    {
        if (StartupOverlay != null) StartupOverlay.IsVisible = false;
        if (_cfg != null)
        {
            _cfg.FirstRunDone = true;
            _cfg.NoticeShownVersion = AutoUpdate.NoticeVersion;
            _cfg.Save();
        }
        InsertLog($"本程序免费开源，作者是 B 站 {AutoUpdate.AuthorName}。");
    }

    // ================= 全局热键 =================

    private void Hotkey_Changed(object? sender, SelectionChangedEventArgs e)
    {
        ScheduleSave();
        if (!Input.GlobalHotkeys.IsAvailable) return;
        ReconfigureHotkeys();
    }

    private void CountdownCombo_Changed(object? sender, SelectionChangedEventArgs e)
    {
        ScheduleSave();
    }

    private void ReconfigureHotkeys()
    {
        var codes = new List<int>();
        foreach (var combo in new[] { HotkeyControlCombo, HotkeyRewindCombo, HotkeyForwardCombo,
                                      HotkeyPrevCombo, HotkeyNextCombo })
        {
            int code = CodeOf(combo);
            if (code != 0) codes.Add(code);
        }
        Input.GlobalHotkeys.SetActive(codes);
    }

    /// <summary>下拉项 → 虚拟键码。索引 0 = 无。</summary>
    private static int CodeOf(ComboBox combo)
    {
        int idx = Math.Max(0, combo.SelectedIndex);
        return idx > 0 ? Input.GlobalHotkeys.FunctionKeyCode(idx) : 0;
    }

    private void OnGlobalKeyWorker(int code, bool down)
    {
        UiPost(() => HandleGlobalKey(code));
    }

    private void HandleGlobalKey(int code)
    {
        if (code == 0) return;
        if (code == CodeOf(HotkeyControlCombo)) { ToggleControl(); return; }
        if (code == CodeOf(HotkeyRewindCombo)) { SeekRelative(-SeekStepSeconds); return; }
        if (code == CodeOf(HotkeyForwardCombo)) { SeekRelative(SeekStepSeconds); return; }
        if (code == CodeOf(HotkeyPrevCombo)) { SwitchSong(-1); return; }
        if (code == CodeOf(HotkeyNextCombo)) SwitchSong(+1);
    }

    private void SwitchSong(int delta)
    {
        bool usePlaylist = _sourceIsPlaylist && _playlist is { Count: > 0 };
        int count = usePlaylist ? _playlist!.Count : _folderFiles.Count;

        if (count == 0)
        {
            InsertLog("切歌需要先「打开 MIDI 文件 / 文件夹 ▾ → 打开文件夹…」，或选一份歌单。曲目表是空的。");
            return;
        }
        if (HasUnexportedEdits)
        {
            InsertLog("有未导出的卷帘改动：先点「导出 MIDI…」保存，再切歌。");
            return;
        }

        int idx = usePlaylist ? CurrentPlaylistIndex() : CurrentFolderIndex();
        int next = idx < 0 ? (delta > 0 ? 0 : count - 1)
                           : (idx + delta + count) % count;

        string path = usePlaylist ? _playlist!.Entries[next].Path : _folderFiles[next];
        string name = System.IO.Path.GetFileName(path);

        if (!System.IO.File.Exists(path))
        {
            InsertLog($"文件已不在：{name}");
            if (usePlaylist) RefreshPlaylistCard();
            else ScanMidiFolder(_folderPath);   // 重扫一次，列表跟着变成当前目录的内容
            return;
        }

        bool wasPlaying = _engine is { IsRunning: true };   // 暂停中也算：接着弹新的一首
        InsertLog($"切歌：{(delta > 0 ? "下一首" : "上一首")} → {name}（{next + 1}/{count}）");

        _advancing = true;
        try
        {
            if (usePlaylist)
            {
                _playlistPos = next;
                var list = _playlist;
                if (list != null && next < list.Count && LoadMidiFile(path))
                    SelectTrackForEntry(list.Entries[next]);
            }
            else
            {
                if (LoadMidiFile(path)) AutoSelectTrack();
            }
        }
        finally { _advancing = false; }

        RefreshPlaylistCard();
        if (wasPlaying) RequestPlay(skipCountdown: true);
    }

    private double CurrentPosition()
    {
        if (_previewOn) return PreviewNow();
        var eng = _engine;
        if (eng is { IsRunning: true }) return eng.ElapsedSeconds;
        return _previewSeconds;
    }

    /// <summary>当前总时长。与 <see cref="CurrentPosition"/> 必须取同一套状态，否则夹取会错。</summary>
    private double CurrentTotal()
    {
        if (_previewOn) return _previewTotal;
        var eng = _engine;
        if (eng is { IsRunning: true }) return Math.Max(0.001, eng.TotalSeconds);
        return PreviewTotalSeconds;
    }

    /// <summary>快进/后退固定步长：从当前位置相对移动。试听中、演奏中、空闲都可用。</summary>
    private void SeekRelative(double delta)
    {
        double total = CurrentTotal();
        if (total <= 0) return;
        double cur = Math.Clamp(CurrentPosition(), 0, total);
        double target = Math.Clamp(cur + delta, 0, total);
        ApplySeek(target);
        InsertLog($"{(delta < 0 ? "后退" : "前进")} {Math.Abs(delta):F0} 秒："
                  + $"{cur:F1} → {target:F1} s"
                  + (_previewOn ? "（试听）" : _engine is { IsRunning: true } ? "" : "（未播放，只改了起始位置）"));
    }

    /// <summary>统一控制键（默认 F6）：空闲=开始、倒计时中=取消、播放中=暂停、暂停中=继续；按钮与托盘项共用。</summary>
    private void ToggleControl()
    {
        if (_forcedUpdateOn)
        {
            InsertLog("必须先更新到最新版本，更新完就能照常用。");
            ForcedUpdateOverlay.IsVisible = true;
            return;
        }
        var eng = _engine;
        if (eng is { IsRunning: true })
        {
            TogglePause();
            return;
        }
        if (_busy)   // 正在倒计时：再按一次 = 取消本次开始
        {
            InsertLog("已取消本次开始，可换好歌后再按一次。");
            _countdownTimer?.Stop();
            _countdownTimer = null;
            ResetUi();
            return;
        }
        RequestPlay();
    }

    private void TogglePause()
    {
        var eng = _engine;
        if (eng is not { IsRunning: true })
        {
            InsertLog("当前没有在播放，无法暂停。");
            return;
        }
        _focusAutoPaused = false;   // 手动暂停/继续：焦点守卫不再把这次暂停当成自己触发的
        if (eng.IsPaused)
        {
            eng.Resume();
            SetPauseUi(false);
        }
        else
        {
            eng.Pause();
            SetPauseUi(true);
        }
    }

    /// <summary>暂停/继续的状态区与按钮（手动 F6 与焦点守卫共用）。</summary>
    private void SetPauseUi(bool paused)
    {
        LblStatus.Foreground = paused ? FailBrush : OkBrush;
        LblStatus.FontSize = ResourceFontSize("FontDisplay", 22);
        LblStatus.Text = paused ? "已暂停 —— 按 F6 或点「▶ 继续」" : "演奏中…";
        UpdateTransportUi();

        if (_cfg?.OverlayHideOnPause == true)
        {
            if (paused) HideOverlay();
            else if (_engine is { IsRunning: true } eng) ShowOverlayProgress(eng);
        }
    }

    // ================= 焦点守卫：按键只进游戏 =================

    private void StartFocusGuard()
    {
        _focusAutoPaused = false;
        _focusTimer?.Stop();
        _focusTimer = null;
        _gamePid = Input.InputSender.PidOfWindow(_gameHwnd);
        if (ChkFocusGuard.IsChecked != true || _gamePid == 0) return;

        _focusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _focusTimer.Tick += FocusGuardTick;
        _focusTimer.Start();
    }

    private void FocusGuardTick(object? sender, EventArgs e)
    {
        var eng = _engine;
        if (eng is not { IsRunning: true })
        {
            _focusTimer?.Stop();
            _focusTimer = null;
            return;
        }

        uint fg = Input.InputSender.PidOfWindow(Input.InputSender.ForegroundWindow);
        if (fg == 0 || fg == Environment.ProcessId) return;   // 读不到 / 本程序自己的窗口：中立
        bool inGame = fg == _gamePid;

        if (!inGame && !eng.IsPaused)
        {
            eng.Pause();
            _focusAutoPaused = true;
            InsertLog("焦点离开目标程序，已自动暂停（切回去自动继续；不需要可在「演奏参数」里关掉）。");
            SetPauseUi(true);
        }
        else if (inGame && eng.IsPaused && _focusAutoPaused)
        {
            eng.Resume();
            _focusAutoPaused = false;
            InsertLog("焦点回到目标程序，自动继续。");
            SetPauseUi(false);
        }
    }

    private void FocusGuard_Changed(object? sender, RoutedEventArgs e)
    {
        if (_cfg != null) _cfg.FocusGuard = ChkFocusGuard.IsChecked == true;
        ScheduleSave();
        if (!_uiReady) return;
        // 播放中改开关：勾上立刻补挂守卫，取消就拆掉（不改当前暂停状态）
        if (_engine is { IsRunning: true })
        {
            if (ChkFocusGuard.IsChecked == true) StartFocusGuard();
            else { _focusTimer?.Stop(); _focusTimer = null; }
        }
    }

    // ================= 状态区 / 按钮提示 =================

    /// <summary>空闲时的状态区提示。</summary>
    private void SetIdleHint()
    {
        LblStatus.Foreground = NeutralBrush;
        LblStatus.FontSize = ResourceFontSize("FontTitle", 15);
        LblStatus.Text = "打开 MIDI 并点选主旋律 → 按 F6 或点 ▶ 播放";
    }

    /// <summary>按状态切换播放按钮与热键提示；停止按钮在倒计时里也可用。</summary>
    private void UpdateTransportUi()
    {
        var eng = _engine;
        // 播放头跟随只在播放中生效，这里统一告知卷帘
        if (Roll != null) Roll.IsPlaying = eng is { IsRunning: true };
        if (eng is { IsRunning: true })
        {
            BtnPlay.IsEnabled = true;
            BtnStop.IsEnabled = true;
            BtnPlay.Content = eng.IsPaused ? "▶ 继续 (F6)" : "⏸ 暂停";
            TxtHotHint.Text = eng.IsPaused ? "F6 继续" : "F6 暂停";
            return;
        }
        BtnPlay.Content = "▶ 播放 (F6)";
        BtnStop.IsEnabled = _busy;   // 倒计时中允许点停止取消

        bool hasTrack = ActiveRows().Count > 0;
        bool hasPlayable = hasTrack && BuildMapping().InRangeCount > 0;
        BtnPlay.IsEnabled = !_busy && hasPlayable;

        string reason = hasTrack
            ? (hasPlayable ? "" : "这首歌没有可弹的音：换个键位方案，或调一下「移调」")
            : "先点「打开 MIDI 文件 / 文件夹」，再在左侧点一行作为主旋律";
        TxtHotHint.Text = BtnPlay.IsEnabled ? "F6：开始 / 暂停 / 继续" : reason;
        // ToolTip 在 Avalonia 里是附加属性，必须走 SetTip
        Avalonia.Controls.ToolTip.SetTip(BtnPlay, BtnPlay.IsEnabled ? null : reason);
    }

    // ================= 日志 / 设置持久化 =================

    private void UiPost(Action a) => Dispatcher.UIThread.Post(a);

    private void InsertLog(string msg)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        if (TxtLastMsg != null) TxtLastMsg.Text = msg;   // 界面只留最近一条
        Persist.LogFile.Append(line);                    // 完整历史仍然落盘
    }

    private void ScheduleSave()
    {
        _saveDeb?.Stop();
        _saveDeb?.Start();
    }

    private void SaveSettings()
    {
        if (_cfg == null) return;
        _cfg.Speed = (int)SliderSpeed.Value;
        _cfg.Transpose = (int)SliderTranspose.Value;
        _cfg.CountdownIndex = Math.Clamp(CountdownCombo.SelectedIndex, 0, 3);
        _cfg.ControlHotkeyIndex = Math.Clamp(HotkeyControlCombo.SelectedIndex, 0, 12);
        _cfg.RewindHotkeyIndex = Math.Clamp(HotkeyRewindCombo.SelectedIndex, 0, 12);
        _cfg.ForwardHotkeyIndex = Math.Clamp(HotkeyForwardCombo.SelectedIndex, 0, 12);
        _cfg.PrevSongHotkeyIndex = Math.Clamp(HotkeyPrevCombo.SelectedIndex, 0, 12);
        _cfg.NextSongHotkeyIndex = Math.Clamp(HotkeyNextCombo.SelectedIndex, 0, 12);
        _cfg.TrimLead = ChkTrimLead.IsChecked == true;
        _cfg.FoldOctave = ChkFoldOctave.IsChecked == true;
        _cfg.AutoMinimizeOnPlay = ChkAutoMinimize.IsChecked == true;
        _cfg.ShowPreflight = ChkShowPreflight.IsChecked == true;
        _cfg.TimingIndex = Math.Clamp(TimingCombo.SelectedIndex, 0, 2);
        _cfg.MidiDeviceName = MidiDeviceCombo.SelectedItem as string ?? "";
        if (_cfg.MidiDeviceName.StartsWith('（')) _cfg.MidiDeviceName = "";
        _cfg.MidiLiveEnabled = ChkMidiLive.IsChecked == true;
        _cfg.MidiBaseOctave = (int)Math.Round(SliderMidiOctave.Value);
        _cfg.MidiMinVelocity = (int)Math.Round(SliderMidiVelocity.Value);
        _cfg.MidiAutoFit = ChkMidiAutoFit.IsChecked == true;
        _cfg.FolderPath = _folderPath;      // 曲目卡目录：内存里的当前目录就是设置里的那一份
        RememberCurrentProfileSettings();   // 速度 / 移调 / 输入档位按方案名同时记一份
        _cfg.Save();
    }

    private void UpdateSettingLabels()
    {
        if (TxtSpeed is null || TxtTranspose is null) return;
        TxtSpeed.Text = $"{SliderSpeed.Value:0}%";
        double tr = SliderTranspose.Value;
        TxtTranspose.Text = tr > 0 ? $"+{tr:0}" : $"{tr:0}";
    }

    /// <summary>把当前速度 / 移调 / 输入档位写回当前方案名下。</summary>
    private void RememberCurrentProfileSettings()
    {
        if (_cfg == null) return;
        _cfg.RememberProfile(_keymap.Name,
            (int)SliderSpeed.Value,
            (int)SliderTranspose.Value,
            Math.Clamp(TimingCombo.SelectedIndex, 0, 2));
    }

    private void ApplyProfileSettings(string? profileName)
    {
        if (_cfg == null) return;
        var s = _cfg.SettingsFor(profileName);

        SliderSpeed.Value = Math.Clamp(s.Speed, 10, 400);        // 滑块自身的范围（与 Engine / AppConfig 同口径）
        SliderTranspose.Value = Math.Clamp(s.Transpose, -24, 24);
        TimingCombo.SelectedIndex = Math.Clamp(s.TimingIndex, 0, 2);

        // 档位跟着改：播放引擎与实时演奏都要用新的时序预算
        var timing = InputTiming.FromIndex(TimingCombo.SelectedIndex);
        _livePlay.Timing = timing;

        // 全局字段一并同步，保证旧设置文件与旧版本程序读到的还是这套值
        _cfg.Speed = (int)SliderSpeed.Value;
        _cfg.Transpose = (int)SliderTranspose.Value;
        _cfg.TimingIndex = TimingCombo.SelectedIndex;

        UpdateSettingLabels();
    }

    private void SpeedChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateSettingLabels();

        if (_engine is { IsRunning: true } eng)
        {
            if (ReferenceEquals(sender, SliderSpeed))
            {
                // 播放中实时变速：立即生效
                eng.Speed = SliderSpeed.Value / 100.0;
            }
            else if (ReferenceEquals(sender, SliderTranspose))
            {
                // 播放中实时移调：等拖动停顿 150ms 再换谱，避免逐刻度反复重建
                QueueLiveTranspose();
            }
            return;
        }

        if (!_busy && _previewDeb is not null)
        {
            _previewDeb.Stop();
            _previewDeb.Start();
        }
        ScheduleSave();
    }

    /// <summary>播放中移调：停顿后重建剩余音符并应用到引擎。</summary>
    private void QueueLiveTranspose()
    {
        if (_liveQueued) return;
        _liveQueued = true;
        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _liveTimer.Tick += (_, _) =>
        {
            _liveTimer!.Stop();
            _liveTimer = null;
            _liveQueued = false;
            ApplyLiveTranspose();
        };
        _liveTimer.Start();
    }

    private void ApplyLiveTranspose()
    {
        var eng = _engine;
        if (eng == null || _selected == null) return;

        var map = BuildMapping();
        var notes = NoteMapper.Playable(map.Notes);
        eng.UpdateNotes(notes);
        InsertLog($"移调 {CurrentTranspose:+#;-#;0}：可演奏 {notes.Count} 音" +
                  (map.SkipCount > 0 ? $" / 跳过 {map.SkipCount}" : ""));
        RefreshPreview();
    }

    // ================= 进度条拖拽（播放中跳转） =================

    private void Progress_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!SliderProgress.IsEnabled) return;
        _seeking = true;
        SeekThumbTo(e);            // 点哪跳到哪（不用先抓滑块）
        PreviewSeekFromSlider();
    }

    private void Progress_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_seeking) return;
        SeekThumbTo(e);            // 按住拖动 = 预览位置（不打断播放）
        PreviewSeekFromSlider();
    }

    private void Progress_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_seeking) return;
        _seeking = false;
        ApplySeek(SliderProgress.Value);
    }

    private void SeekThumbTo(PointerEventArgs e)
    {
        double w = SliderProgress.Bounds.Width;
        if (w <= 0) return;
        double x = e.GetPosition(SliderProgress).X;
        double frac = Math.Clamp(x / w, 0.0, 1.0);
        SliderProgress.Value = frac * SliderProgress.Maximum;
    }

    /// <summary>拖动进度条时只更新显示，松手才跳转。</summary>
    private void PreviewSeekFromSlider() => ShowPosition(SliderProgress.Value);

    /// <summary>卷帘拖动中：只挪指针与音符显示，不打断播放。</summary>
    private void OnRollPreview(double seconds)
    {
        _seeking = true;          // 让 UI 定时器别把指针拽回去
        ShowPosition(seconds);
    }

    /// <summary>卷帘松手：真正跳转。seconds 是谱面秒（卷帘轴就是谱面时间）。</summary>
    private void OnRollSeek(double seconds)
    {
        _seeking = false;
        // 试听时钟走真实秒（谱面秒 / 速度）：先换算，与进度条拖动（真实秒量程）行为一致
        if (_previewOn) seconds /= _previewSpeed;
        ApplySeek(seconds);
    }

    /// <summary>定位到某个秒数：试听中跳转试听，演奏中跳转引擎，都没有只记住位置。</summary>
    private void ApplySeek(double seconds)
    {
        if (_previewOn)
        {
            PreviewSeekTo(seconds);
            ShowPosition(seconds);
            return;
        }

        var eng = _engine;
        if (eng is { IsRunning: true })
        {
            double total = Math.Max(0.001, eng.TotalSeconds);
            eng.SeekFraction(Math.Clamp(seconds / total, 0, 1));
        }
        else
        {
            _previewSeconds = Math.Clamp(seconds, 0, PreviewTotalSeconds);
        }
        ShowPosition(seconds);
    }

    private double PreviewTotalSeconds =>
        _previewNotes.Count == 0 ? 0 : _previewNotes.Max(n => n.End);

    /// <summary>进度条/卷帘/时间/定位音一起摆到某个秒数（不动引擎，也不动试听时钟）。</summary>
    private void ShowPosition(double seconds)
    {
        double total = CurrentTotal();
        double t = Math.Clamp(seconds, 0, total);
        SliderProgress.Value = t;
        Roll.SetPosition(t);
        TxtTime.Text = $"{t:F1} / {total:F1} s";
        UpdateSeekNote(t);
        // 空闲时把位置记下来。否则 F5/F7 取到过期的 0，表现就是"F7 跳到 5 秒、F5 回开头"。
        if (!_previewOn && _engine is not { IsRunning: true }) _previewSeconds = t;
    }

    /// <summary>显示某个时刻的音：简谱 + 要按的键。不传则取当前指针位置。</summary>
    private void UpdateSeekNote(double? atSeconds = null)
    {
        if (TxtSeekNote == null) return;
        double t = atSeconds ?? (_engine is { IsRunning: true } ? _engine.ElapsedSeconds : _previewSeconds);
        MappedNote? note = _previewNotes.LastOrDefault(n => n.Start <= t && t < n.End);
        if (note == null) { TxtSeekNote.Text = "—"; return; }
        string name = Music.SolfegeName(note.Pitch);
        if (!note.InRange) { TxtSeekNote.Text = $"{name} 没有对应键"; return; }
        // 按键与修饰键一律按当前键位方案算，和实际演奏一致
        _keymap.TryKeyOfPitch(note.Pitch, out string key, out int octaveOffset, out bool sharp);
        var parts = new List<string>();
        if (octaveOffset < 0) parts.Add("降八度键+");
        else if (octaveOffset > 0) parts.Add("升八度键+");
        if (sharp) parts.Add("升半音键+");
        parts.Add(string.IsNullOrEmpty(key) ? "（无按键）" : DisplayKey(key));
        TxtSeekNote.Text = $"{name} · {string.Join("", parts)}";
    }

    /// <summary>按键名显示：逗号写成全角逗号，其余原样。</summary>
    private static string DisplayKey(string key) => key == "," ? "，" : key;

    // ================= 卷帘编辑 =================

    /// <summary>UI-01 / UI-02：常驻的「有未导出改动」提示条。只在真的还没导出时显示。</summary>
    private void UpdateEditMark()
    {
        if (TxtEditMark != null) TxtEditMark.IsVisible = HasUnexportedEdits;
    }

    private async Task<bool> ConfirmDiscardEditsAsync(string what)
    {
        if (!HasUnexportedEdits) return true;

        var ok = new Button { Content = "丢弃改动并继续", Classes = { "accent" }, Padding = new Thickness(16, 4) };
        var cancel = new Button { Content = "取消", Classes = { "secondary" }, Padding = new Thickness(16, 4) };
        var dlg = new Window
        {
            Title = "有未导出的手动改动",
            Width = 400,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = this.Background,
            FontFamily = this.FontFamily,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"{what}会丢弃当前 {_editor.Notes.Count} 个手动改动，撤销栈也会一起清空，丢弃后无法恢复。\n"
                             + "想留住这份改动，先点「取消」，再点「导出 MIDI…」存一份。",
                        TextWrapping = TextWrapping.Wrap,
                        LineHeight = 20,
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancel, ok },
                    },
                },
            },
        };
        ok.Click += (_, _) => dlg.Close(true);
        cancel.Click += (_, _) => dlg.Close(false);
        return await dlg.ShowDialog<bool>(this);
    }

    private async Task<bool> ConfirmDiscardEditsForActionAsync(string what)
    {
        if (!HasUnexportedEdits) return true;
        if (_discardPrompting) return false;
        _discardPrompting = true;
        try { return await ConfirmDiscardEditsAsync(what); }
        finally { _discardPrompting = false; }
    }

    /// <summary>谱面来源要变了：有手动改动就先丢弃并说明，否则用户会以为点了没反应。</summary>
    private void DropEditsIfAny(string why)
    {
        if (!_editing) return;
        ResetEdits();
        InsertLog($"已丢弃手动改动（{why}）。");
    }

    /// <summary>第一次编辑时，把当前自动结果冻结成可编辑谱面。</summary>
    private void BeginEditIfNeeded()
    {
        if (_editing) return;
        _editor.Reset(ComputeAutoNotes());
        _editing = true;
        _editsExported = false;
        UpdateEditMark();
        InsertLog("已进入编辑模式：自动提取的选项不再影响谱面，点「还原为自动」可退出。");
    }

    /// <summary>换歌或点「还原为自动」时丢弃全部手动改动。</summary>
    private void ResetEdits()
    {
        _editing = false;
        _editsExported = false;
        _editor.Clear();
        Roll.ClearSelection();
        UpdateEditMark();
    }

    private void OnRollEditCommitted(IReadOnlyList<RawNote> notes, string what)
    {
        BeginEditIfNeeded();
        _editsExported = false;   // 又改了谱面：已导出标记作废
        _editor.ReplaceAll(notes);
        RefreshPreview(pushToRoll: false);
        InsertLog($"已{what}。");
    }

    /// <summary>卷帘视口/缩放/跟随变化：刷新工具栏读数。</summary>
    private void OnRollViewChanged()
    {
        if (TxtZoom == null) return;
        TxtZoom.Text = $"{Roll.ZoomPercent:F0}%";
        if (ChkFollow != null && ChkFollow.IsChecked != Roll.FollowPlayhead)
            ChkFollow.IsChecked = Roll.FollowPlayhead;
    }

    /// <summary>加音用的默认长度：取现有音符的中位长度，夹在 0.1-1.0 秒。</summary>
    private double MedianNoteLength()
    {
        var src = _editing ? _editor.Notes : ComputeAutoNotes();
        var lens = src.Select(n => n.End - n.Start).Where(l => l > 0.02).OrderBy(l => l).ToList();
        if (lens.Count == 0) return 0.25;
        return Math.Clamp(lens[lens.Count / 2], 0.1, 1.0);
    }

    private void DoUndo()
    {
        if (!_editing || !_editor.Undo()) { InsertLog("没有可撤销的操作。"); return; }
        _editsExported = false;   // 谱面又变了：已导出标记作废
        RefreshPreview(keepView: true);
        Roll.ClearSelection();
        InsertLog("已撤销。");
    }

    private void DoRedo()
    {
        if (!_editing || !_editor.Redo()) { InsertLog("没有可重做的操作。"); return; }
        _editsExported = false;   // 谱面又变了：已导出标记作废
        RefreshPreview(keepView: true);
        Roll.ClearSelection();
        InsertLog("已重做。");
    }

    /// <summary>删除卷帘里选中的音（工具栏按钮 / Delete 键）。</summary>
    private void DeleteSelectedNote()
    {
        if (!Roll.HasSelection) { InsertLog("先在卷帘上点一个音（或框选几个），再删除。"); return; }
        Roll.DeleteSelected();
    }

    private void UpdateEditUi()
    {
        if (BtnUndo == null) return;
        BtnUndo.IsEnabled = _editing && _editor.CanUndo;
        BtnRedo.IsEnabled = _editing && _editor.CanRedo;
        BtnDeleteNote.IsEnabled = Roll.HasSelection;
        BtnResetEdits.IsEnabled = _editing;
        BtnExportMidi.IsEnabled = _noteCount > 0;
        UpdateEditMark();   // UI-01：常驻提示跟着编辑状态走
    }

    private void Undo_Click(object? sender, RoutedEventArgs e) => DoUndo();
    private void ZoomIn_Click(object? sender, RoutedEventArgs e) => Roll.ZoomCenter(0.6);
    private void ZoomOut_Click(object? sender, RoutedEventArgs e) => Roll.ZoomCenter(1.67);
    private void ZoomFit_Click(object? sender, RoutedEventArgs e) => Roll.FitAll();
    private void Redo_Click(object? sender, RoutedEventArgs e) => DoRedo();
    private void DeleteNote_Click(object? sender, RoutedEventArgs e) => DeleteSelectedNote();

    private void Snap_Changed(object? sender, RoutedEventArgs e)
    {
        if (Roll != null) Roll.SnapEnabled = ChkSnap.IsChecked == true;
    }

    /// <summary>帮助按钮：显示 / 收起卷帘右侧的操作说明。</summary>
    private void Help_Click(object? sender, RoutedEventArgs e)
    {
        _helpOn = !_helpOn;
        UpdateHelpVisibility();
    }

    private void UpdateHelpVisibility()
    {
        if (RollHelp == null) return;
        bool roomy = Bounds.Width >= 1080;
        RollHelp.IsVisible = _helpOn && roomy;
        if (BtnHelp != null)
        {
            BtnHelp.IsEnabled = roomy;
            ToolTip.SetTip(BtnHelp, roomy
                ? (_helpOn ? "收起操作说明，把宽度让给卷帘" : "显示操作说明")
                : "窗口太窄：说明已自动收起，避免把缩放按钮挤掉。把窗口拉宽就会恢复。");
        }
    }

    private MidiPreview? _preview;
    private MidiPreviewSequencer? _previewSeq;
    /// <summary>界面刷新定时器：低频（60ms），只把位置画到界面上。音频派发不在这里。</summary>
    private DispatcherTimer? _previewTimer;
    private double _previewTotal;
    private double _previewSpeed = 1.0;
    private bool _previewOn;
    /// <summary>事件表条数。诊断用（探针要打印它）。</summary>
    internal int PreviewEventCount => _previewSeq?.EventCount ?? 0;

    /// <summary>试听当前所在秒数（真实秒，已含速度）。位置由调度器的单调时钟给出，UI 线程直接读。</summary>
    private double PreviewNow()
    {
        var seq = _previewSeq;
        return seq != null && _previewOn ? seq.PositionSeconds : _previewSeconds;
    }

    private void PreviewSeekTo(double seconds)
    {
        _previewSeq?.Seek(Math.Clamp(seconds, 0, _previewTotal));
    }

    /// <summary>试听按钮：点一下开始放声音，再点一下停止。</summary>
    private void Preview_Click(object? sender, RoutedEventArgs e)
    {
        if (_previewOn) StopPreviewAudio();
        else StartPreviewAudio();
    }

    private void StartPreviewAudio()
    {
        // 不能用 _playNotes：那个只在"开始播放"时才填。用户刚打开文件就点试听时它是空的。
        if (_busy || _engine is { IsRunning: true })
        {
            InsertLog("试听与演奏不能同时进行，先停止当前演奏。");
            return;
        }
        var notes = NoteMapper.Playable(BuildMapping().Notes);
        if (notes.Count == 0)
        {
            InsertLog("当前谱面没有可演奏的音，无法试听。先选一行主旋律。");
            return;
        }

        if (_preview == null)
        {
            _preview = new MidiPreview();
            if (!_preview.IsAvailable)
            {
                InsertLog($"试听不可用：{_preview.LastError}。" +
                          "系统可能没有可用的 MIDI 输出设备（正常应有 Microsoft GS Wavetable Synth）。");
                _preview.Dispose();
                _preview = null;
                BtnPreview.IsEnabled = false;
                return;
            }
        }

        // 与演奏同一套时间基准：谱面时间除以速度 = 真实秒。
        // 事件表只在 Load 里建一次，播放中不再重建。
        double speed = Math.Max(0.1, SliderSpeed.Value / 100.0);
        _previewSpeed = speed;
        var spans = new List<(double Start, double End, int Pitch)>(notes.Count);
        foreach (var n in notes) spans.Add((n.Start, n.End, n.Pitch));

        _previewSeq ??= new MidiPreviewSequencer(_preview);
        _previewSeq.Load(spans, speed);
        _previewTotal = _previewSeq.TotalSeconds;

        double startAt = Math.Clamp(_previewSeconds / speed, 0, _previewTotal);
        _previewOn = true;
        BtnPreview.Content = "⏹ 停止试听";
        SliderProgress.Maximum = Math.Max(0.1, _previewTotal);
        _previewSeq.Start(startAt);   // 起独立调度线程，并按需 timeBeginPeriod(1)

        // 进度条 / 时间 / 卷帘用低频定时器读位置：音频怎么派发都不影响界面，界面忙了也不影响音频
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _previewTimer.Tick += (_, _) => PreviewUiTick();
        _previewTimer.Start();

        if (Roll != null)
        {
            Roll.IsPlaying = true;
            // 卷帘时间轴比谱面末音长一点（留了右键余量）：换算比例，让播放头与音符严格对齐。
            Roll.SetPlayheadScale(_previewTotal > 0 ? PreviewTotalSeconds / _previewTotal : 1.0);
        }

        InsertLog($"试听开始：{notes.Count} 个音，约 {_previewTotal:F1}s（不发送按键）");
    }

    private void PreviewUiTick()
    {
        if (!_previewOn) return;

        // 用户正在拖进度条/卷帘：这一帧不推进也不覆盖，把画面交给拖动
        if (_seeking) return;

        double t = PreviewNow();
        double shown = Math.Min(t, _previewTotal);   // 真实秒（调度器时钟）
        if (_previewTotal > 0)
        {
            SliderProgress.Value = shown;
            TxtTime.Text = $"{shown:F1} / {_previewTotal:F1} s";
            Roll.SetPosition(shown);
            UpdateSeekNote(shown);
        }

        _previewSeconds = shown * _previewSpeed;

        if (_previewSeq is { IsFinished: true }) StopPreviewAudio();
    }

    private void StopPreviewAudio()
    {
        bool wasOn = _previewOn || (_previewSeq?.IsRunning ?? false);
        // 【诊断用，可删】谁把试听停掉的（探针模式才记）
        if (wasOn && PreviewJitterProbe.Enabled)
        {
            var st = new System.Diagnostics.StackTrace(1, false);
            var frames = st.GetFrames() ?? Array.Empty<System.Diagnostics.StackFrame>();
            PreviewJitterProbe.Note("StopPreviewAudio 调用点："
                + string.Join(" <- ", frames.Take(4).Select(f => f.GetMethod()?.DeclaringType?.Name + "." + f.GetMethod()?.Name)));
        }
        _previewOn = false;
        _previewTimer?.Stop();
        _previewTimer = null;
        _previewSeq?.Stop();        // 唤醒线程 → 等它退出 → 复原 1ms 定时器精度 → AllNotesOff
        _preview?.AllNotesOff();    // 双保险：任何残余发声都关掉（CC123 + CC120）
        if (BtnPreview != null) BtnPreview.Content = "试听";
        // 试听结束就不再跟随播放头（演奏中的状态由 UpdateTransportUi 负责）
        if (Roll != null && _engine is not { IsRunning: true })
        {
            Roll.IsPlaying = false;
            Roll.SetPlayheadScale(1.0);
        }
    }

    private void ResetEdits_Click(object? sender, RoutedEventArgs e)
    {
        if (!_editing) { InsertLog("当前就是自动结果，没有可还原的改动。"); return; }
        ResetEdits();
        RefreshPreview();
        InsertLog("已还原为自动提取结果。");
    }

    /// <summary>窗口级快捷键：删除、撤销、重做。卷帘不必先获得焦点。</summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (ctrl && e.Key == Key.Z)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) DoRedo(); else DoUndo();
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.Y) { DoRedo(); e.Handled = true; return; }
        if (e.Key == Key.Delete) { DeleteSelectedNote(); e.Handled = true; }
    }

    /// <summary>把当前谱面写成标准 MIDI 文件。</summary>
    private async void ExportMidi_Click(object? sender, RoutedEventArgs e)
    {
        var raw = GetActiveRawNotes();
        if (raw.Count == 0) { InsertLog("没有音符可导出。"); return; }
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出编辑后的 MIDI",
                SuggestedFileName = SuggestMidiName(),
                DefaultExtension = "mid",
                FileTypeChoices = new List<FilePickerFileType>
                {
                    new("MIDI 文件") { Patterns = new List<string> { "*.mid" } }
                }
            });
            if (file == null) return;
            string? path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            MidiExporter.Write(path, raw, "MidiKeyPlayer 编辑");
            _editsExported = true;   // UI-01：已存盘，常驻提示可以收起
            UpdateEditMark();
            InsertLog($"已导出 MIDI：{raw.Count} 个音 → {System.IO.Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            InsertLog($"导出 MIDI 失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private string SuggestMidiName()
    {
        string name = "edited";
        if (_parsed != null && !string.IsNullOrEmpty(_parsed.FilePath))
            name = System.IO.Path.GetFileNameWithoutExtension(_parsed.FilePath) + "-edited";
        return name + ".mid";
    }

    /// <summary>没有可定位的谱面时，清空进度条、卷帘与音符显示。</summary>
    private void ResetSeekUi()
    {
        Roll.SetNotes(Array.Empty<RawNote>(), Array.Empty<int>(), 0);
        Roll.SetPosition(0);
        SliderProgress.Maximum = 0.1;
        SliderProgress.Value = 0;
        SliderProgress.IsEnabled = false;
        TxtTime.Text = "0.0 / 0.0 s";
        TxtSeekNote.Text = "—";
    }

    // ================= 文件载入 =================

    /// <summary>打开文件对话框。既是 SplitButton 主体的处理函数，也是托盘与热键那条链路的入口。</summary>
    private async void BtnOpen_Click(object? sender, RoutedEventArgs e)
    {
        HideOpenMenu();   // 从下拉菜单里点进来时先收菜单：对话框与确认框不压在菜单上面
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择 MIDI 文件",
                AllowMultiple = false,
                SuggestedStartLocation = await LastMidiFolderAsync(),
                FileTypeFilter = new List<FilePickerFileType>
                {
                    new("MIDI 文件")
                    {
                        Patterns = new List<string> { "*.mid", "*.midi", "*.kar", "*.rmi" }
                    },
                    new("所有文件") { Patterns = new List<string> { "*.*" } }
                }
            });
            if (files.Count == 0) return;

            var path = files[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            // UI-01：换歌会清空手动改动与撤销栈，所以先确认，别静默丢掉
            if (!await ConfirmDiscardEditsAsync("打开新 MIDI 文件")) return;

            LoadMidiFile(path);
        }
        catch (Exception ex)
        {
            InsertLog($"打开文件对话框失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<Avalonia.Platform.Storage.IStorageFolder?> LastMidiFolderAsync()
    {
        try
        {
            var recent = _cfg?.RecentFiles;
            if (recent == null || recent.Count == 0) return null;
            string? dir = System.IO.Path.GetDirectoryName(recent[0]);
            if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)) return null;
            return await StorageProvider.TryGetFolderFromPathAsync(dir);
        }
        catch { return null; }   // 起始目录只是方便，失败不挡打开
    }

    private readonly List<string> _folderFiles = new();   // 当前文件夹里的 MIDI（完整路径）
    private readonly List<string> _folderDirs = new();    // 当前文件夹里的子文件夹（完整路径），排在文件前面
    private string _folderPath = "";                      // 当前目录；每次扫描都写进 AppConfig.FolderPath
    private const int FolderMenuMax = 50;                 // 卡片默认最多列多少行（子文件夹 + MIDI），点「显示其余 N 项」就全列出来
    private bool _folderShowAll;                          // 用户点过「显示其余 N 项」：这一份列表整份列出，不再封顶
    private string _folderQuery = "";                     // 搜索框里的词（空 = 不过滤）；只比显示名，不递归
    private static readonly string[] MidiExtensions = { ".mid", ".midi", ".kar", ".rmi" };

    private bool _folderSyncing;

    private void RestoreFolderFromConfig()
    {
        string path = _cfg.FolderPath;
        if (string.IsNullOrEmpty(path)) return;   // 没选过（或点过「关闭」）：卡片保持隐藏

        if (!System.IO.Directory.Exists(path))
        {
            _cfg.FolderPath = "";
            _cfg.Save();   // 立刻落盘：这个目录不会自己回来，别每次启动都白试一遍
            InsertLog($"上次的曲目文件夹已不在，曲目卡保持关闭：{path}");
            return;
        }

        ScanMidiFolder(path);
    }

    /// <summary>「打开文件夹…」：选一个文件夹，扫描它本身。</summary>
    private async void BtnOpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        HideOpenMenu();
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择放 MIDI 的文件夹",
                AllowMultiple = false,
            });
            if (folders.Count == 0) return;

            string? path = folders[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(path))
            {
                // 网络位置、压缩包内、权限不足都可能拿不到本地路径。说清原因，不弹空菜单。
                InsertLog("打开文件夹失败：这个位置拿不到本地路径（可能是网络位置，或权限不足）。");
                return;
            }

            ScanMidiFolder(path);
        }
        catch (Exception ex)
        {
            InsertLog($"打开文件夹对话框失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ScanMidiFolder(string path)
    {
        _folderPath = path;
        _cfg.FolderPath = path;
        ScheduleSave();
        _folderDirs.Clear();
        _folderFiles.Clear();
        // 换了目录：搜索词与「显示全部」都清零，新目录从完整列表开始（搜索框里的字也一起清掉）。
        _folderShowAll = false;
        _folderQuery = "";
        if (TxtFolderSearch != null && TxtFolderSearch.Text is { Length: > 0 })
            TxtFolderSearch.Text = "";   // 触发 FolderSearch_Changed，那里会再刷一次，无害

        var dir = new System.IO.DirectoryInfo(path);
        bool failed = false;

        // 子目录与文件分开枚举：其中一项失败（权限、被占用、目录被删）不影响另一项
        try
        {
            foreach (var d in dir.EnumerateDirectories()) _folderDirs.Add(d.FullName);
        }
        catch (Exception ex)
        {
            failed = true;
            InsertLog($"读取子文件夹失败：{ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            foreach (var f in dir.EnumerateFiles())
                if (IsMidiFile(f.Extension)) _folderFiles.Add(f.FullName);
        }
        catch (Exception ex)
        {
            failed = true;
            InsertLog($"读取文件夹失败：{ex.GetType().Name}: {ex.Message}");
        }

        // 各自按名字排序（忽略大小写）：顺序与用户在资源管理器里看到的一致。
        _folderDirs.Sort((a, b) => string.Compare(
            System.IO.Path.GetFileName(a), System.IO.Path.GetFileName(b), StringComparison.OrdinalIgnoreCase));
        _folderFiles.Sort((a, b) => string.Compare(
            System.IO.Path.GetFileName(a), System.IO.Path.GetFileName(b), StringComparison.OrdinalIgnoreCase));

        if (_folderDirs.Count == 0 && _folderFiles.Count == 0)
        {
            // 读取失败时上面已经写了真实原因，不再补一句会误导人的「没有找到」
            if (!failed) InsertLog($"{dir.Name}：没有找到 MIDI 文件或子文件夹。");
        }
        else
        {
            InsertLog($"{dir.Name}：{_folderDirs.Count} 个子文件夹、{_folderFiles.Count} 首 MIDI，已列在左侧的文件夹曲目里。");
        }

        RefreshFolderUi();
    }

    /// <summary>文件夹扫描只认这四种扩展名，与文件对话框的过滤器同一套。</summary>
    private static bool IsMidiFile(string ext)
    {
        foreach (string e in MidiExtensions)
            if (e.Equals(ext, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private void RefreshFolderUi()
    {
        if (FolderCard == null || FolderList == null) return;   // 构造早期的防御：控件还没建好

        bool hasFolder = !string.IsNullOrEmpty(_folderPath);
        FolderCard.IsVisible = hasFolder;
        UpdateFolderSplitter();
        if (!hasFolder) return;

        // 标题只写文件夹名；根目录（C:\ 这类）取不到名字时退回完整路径，不显示空标题
        string trimmed = _folderPath.TrimEnd(
            System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        string leaf = System.IO.Path.GetFileName(trimmed);
        TxtFolderName.Text = leaf.Length > 0 ? leaf : _folderPath;
        Avalonia.Controls.ToolTip.SetTip(TxtFolderName, _folderPath);   // 附加属性，必须走 SetTip

        // 先按搜索词过滤：只比显示名（文件名 / 子文件夹名），不递归进子文件夹。
        string query = _folderQuery.Trim();
        List<string> dirs = query.Length == 0
            ? _folderDirs
            : _folderDirs.Where(d => NameMatches(System.IO.Path.GetFileName(d), query)).ToList();
        List<string> files = query.Length == 0
            ? _folderFiles
            : _folderFiles.Where(f => NameMatches(System.IO.Path.GetFileName(f), query)).ToList();

        // 子文件夹在前、MIDI 文件在后。默认封顶 FolderMenuMax 行，点过「显示其余 N 项」就整份列出。
        int total = dirs.Count + files.Count;
        int shown = _folderShowAll ? total : Math.Min(total, FolderMenuMax);

        _folderSyncing = true;   // 下面改选中项会触发 SelectionChanged，那一次不是用户点击
        try
        {
            FolderList.SelectedItem = null;
            FolderList.Items.Clear();
            for (int i = 0; i < shown; i++)
            {
                FolderList.Items.Add(i < dirs.Count
                    ? BuildFolderDirRow(dirs[i])
                    : BuildFolderRow(files[i - dirs.Count]));
            }

            // 截断时末尾放一条**可点**的「显示其余 N 项」：点一下就把这一份列表整份列出来。
            if (total > shown)
                FolderList.Items.Add(BuildFolderShowMoreRow(total - shown, total));

            if (total == 0)
            {
                TxtFolderEmpty.Text = query.Length == 0
                    ? "这个文件夹里没有找到 MIDI 文件或子文件夹。"
                    : $"没有名字里带「{query}」的曲目或子文件夹。";
            }
            TxtFolderEmpty.IsVisible = total == 0;

            // 载入过的那一首保持选中：换歌后高亮跟着走，用户一眼看到当前是哪首。
            string current = _parsed?.FilePath ?? "";
            if (current.Length > 0)
            {
                for (int i = 0; i < FolderList.Items.Count; i++)
                {
                    if (FolderList.Items[i] is ListBoxItem it
                        && it.Tag is FolderRow row && !row.IsDir && row.Path == current)
                    {
                        FolderList.SelectedItem = it;
                        break;
                    }
                }
            }
        }
        finally
        {
            _folderSyncing = false;
        }
    }

    /// <summary>曲目卡里一行的身份：路径 + 是不是子文件夹（两类行的点击行为不同）。</summary>
    private sealed record FolderRow(string Path, bool IsDir);

    private sealed record FolderShowMore(int Hidden, int Total);

    private void FolderSearch_Changed(object? sender, TextChangedEventArgs e)
    {
        string text = TxtFolderSearch?.Text ?? "";
        if (string.Equals(text, _folderQuery, StringComparison.Ordinal)) return;   // 扫描时清空搜索框会绕回来，去重
        _folderQuery = text;
        _folderShowAll = false;   // 换了搜索词：列表重新按默认行数显示，需要就再点「显示其余 N 项」
        RefreshFolderUi();
    }

    /// <summary>显示名里含这个词就算命中（忽略大小写）。</summary>
    private static bool NameMatches(string name, string query)
        => name.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private ListBoxItem BuildFolderShowMoreRow(int hidden, int total)
    {
        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(new TextBlock { Text = "显示其余 " + hidden + " 项", FontSize = 12.5 });
        stack.Children.Add(new TextBlock
        {
            Text = $"这个文件夹一共 {total} 项",
            FontSize = 11,
            Foreground = ResourceBrush("BrushTextMuted"),
        });
        var item = new ListBoxItem
        {
            Content = stack,
            Tag = new FolderShowMore(hidden, total),
        };
        Avalonia.Controls.ToolTip.SetTip(item, "点这一行把剩下的曲目也列出来（只影响这一份列表；换目录或改搜索词会回到默认行数）");
        return item;
    }

    /// <summary>曲目卡里的 MIDI 行：只显示文件名，完整路径放在 Tag 与悬浮提示里。</summary>
    private ListBoxItem BuildFolderRow(string path)
        => BuildFolderItem(path, System.IO.Path.GetFileName(path), isDir: false);

    /// <summary>曲目卡里的子文件夹行：前缀「📁」与文件区分，单击不载入，双击进入。</summary>
    private ListBoxItem BuildFolderDirRow(string path)
        => BuildFolderItem(path, "📁 " + System.IO.Path.GetFileName(path), isDir: true);

    private ListBoxItem BuildFolderItem(string path, string text, bool isDir)
    {
        var item = new ListBoxItem
        {
            Content = new TextBlock
            {
                Text = text,
                FontSize = 12.5,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            },
            Tag = new FolderRow(path, isDir),
        };
        Avalonia.Controls.ToolTip.SetTip(item, path);   // 悬浮显示完整路径
        // 双击只挂在子文件夹行上：双击列表空白处不会误进入目录
        if (isDir) item.DoubleTapped += FolderDirRow_DoubleTapped;
        return item;
    }

    private void FolderList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_folderSyncing) return;                                        // 程序化选中，不是用户点的
        if (FolderList?.SelectedItem is not ListBoxItem it) return;
        if (it.Tag is FolderShowMore more)                                 // 「显示其余 N 项」：点开就整份列出
        {
            Dispatcher.UIThread.Post(() => { _folderShowAll = true; RefreshFolderUi(); });
            return;
        }
        if (it.Tag is not FolderRow row) return;
        if (row.IsDir) return;                                             // 子文件夹：等双击
        // 抑制标记复位之后仍可能到达的重复选中事件：已经是当前这首就返回，别重复载入
        if (row.Path == _parsed?.FilePath) return;

        string pending = row.Path;
        Dispatcher.UIThread.Post(() => _ = OpenFolderFileAsync(pending));
    }

    /// <summary>双击子文件夹行：进入该目录（重新扫描并刷新卡片）。MIDI 行没有挂这个处理器，单击已经载入。</summary>
    private void FolderDirRow_DoubleTapped(object? sender, TappedEventArgs e)
    {
        // 与单击换歌同一个理由：进目录也会重建列表，不能在这个指针事件里做。
        if (sender is ListBoxItem it && it.Tag is FolderRow row && row.IsDir)
        {
            string pending = row.Path;
            Dispatcher.UIThread.Post(() => EnterFolder(pending));
        }
    }

    /// <summary>进入一个子文件夹：目录还在就重扫它；已经不在就重扫当前目录，把失效的那一行去掉。</summary>
    private void EnterFolder(string path)
    {
        try
        {
            if (!System.IO.Directory.Exists(path))
            {
                InsertLog($"文件夹已不在：{path}");
                ScanMidiFolder(_folderPath);
                return;
            }
            ScanMidiFolder(path);
        }
        catch (Exception ex)
        {
            InsertLog($"进入文件夹失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void FolderClose_Click(object? sender, RoutedEventArgs e)
    {
        _folderDirs.Clear();
        _folderFiles.Clear();
        _folderPath = "";
        _cfg.FolderPath = "";
        if (FolderList != null) FolderList.Items.Clear();
        if (FolderCard != null) FolderCard.IsVisible = false;
        UpdateFolderSplitter();
        SaveSettings();   // 立刻落盘：下次启动这张卡不再出现
        InsertLog("已收起左侧的文件夹曲目（下次启动不再显示）。");
    }

    // 拖动期间按「按下时的位置 + 起始高度」算：布局要等下一轮消息循环，读 Bounds 会慢一拍。
    private object? _splitDrag;
    private double _splitStartY;
    private double _splitStartFolder;
    private double _splitStartPlaylist;
    private double _splitStartTracks;
    private GridLength _folderRowBeforeHide = GridLength.Auto;

    private const double FolderMinH = 165;
    private const double PlaylistMinH = 135;
    private const double TrackMinH = 140;

    private void FolderSplitter_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border band || LeftColumn == null) return;
        if (!e.GetCurrentPoint(band).Properties.IsLeftButtonPressed) return;

        _splitStartFolder = FolderCard.Bounds.Height;
        _splitStartPlaylist = PlaylistCard.Bounds.Height;
        FolderListHost.Height = double.NaN;
        PlaylistListHost.Height = double.NaN;
        LeftColumn.RowDefinitions[1].Height = new GridLength(_splitStartFolder);
        LeftColumn.RowDefinitions[3].Height = new GridLength(_splitStartPlaylist);
        BeginSplitDrag(band, e);
    }

    private void FolderSplitter_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_splitDrag != sender || LeftColumn == null) return;

        double dy = e.GetPosition(LeftColumn).Y - _splitStartY;
        double total = _splitStartFolder + _splitStartPlaylist;
        double folder = Math.Clamp(_splitStartFolder + dy, FolderMinH, Math.Max(FolderMinH, total - PlaylistMinH));
        LeftColumn.RowDefinitions[1].Height = new GridLength(folder);
        LeftColumn.RowDefinitions[3].Height = new GridLength(total - folder);
    }

    private void TrackSplitter_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border band || LeftColumn == null) return;
        if (!e.GetCurrentPoint(band).Properties.IsLeftButtonPressed) return;

        _splitStartPlaylist = PlaylistCard.Bounds.Height;
        _splitStartTracks = TrackCard.Bounds.Height;
        PlaylistListHost.Height = double.NaN;
        LeftColumn.RowDefinitions[3].Height = new GridLength(_splitStartPlaylist);
        BeginSplitDrag(band, e);
    }

    private void TrackSplitter_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_splitDrag != sender || LeftColumn == null) return;

        double dy = e.GetPosition(LeftColumn).Y - _splitStartY;
        double max = _splitStartPlaylist + _splitStartTracks - TrackMinH;
        double playlist = Math.Clamp(_splitStartPlaylist + dy, PlaylistMinH, Math.Max(PlaylistMinH, max));
        LeftColumn.RowDefinitions[3].Height = new GridLength(playlist);
    }

    private void BeginSplitDrag(Border band, PointerEventArgs e)
    {
        _splitDrag = band;
        _splitStartY = e.GetPosition(LeftColumn).Y;
        e.Pointer.Capture(band);
        e.Handled = true;
    }

    private void Splitter_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_splitDrag != sender) return;
        e.Pointer.Capture(null);
        _splitDrag = null;
    }

    private void Splitter_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_splitDrag == sender) _splitDrag = null;
    }

    /// <summary>曲库卡收起时，分隔条和它的行一起收掉，别留一段空白。</summary>
    private void UpdateFolderSplitter()
    {
        if (FolderSplitter == null || FolderCard == null || LeftColumn == null) return;

        var row = LeftColumn.RowDefinitions[1];
        bool hasFolder = FolderCard.IsVisible;
        FolderSplitter.IsVisible = hasFolder;
        if (!hasFolder)
        {
            if (row.Height.IsAbsolute && row.Height.Value > 0) _folderRowBeforeHide = row.Height;
            row.Height = new GridLength(0);
        }
        else if (row.Height.IsAbsolute && row.Height.Value <= 0)
        {
            row.Height = _folderRowBeforeHide;
        }
    }

    private async Task OpenFolderFileAsync(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path))
            {
                InsertLog($"文件已不在：{path}");
                ScanMidiFolder(_folderPath);
                return;
            }
            if (!await ConfirmDiscardEditsAsync("打开文件夹里的文件")) return;
            // 从文件夹曲目卡点开的：F9 与播完前进都走文件夹列表，不走歌单（Q18）
            _sourceIsPlaylist = false;
            LoadMidiFile(path);
        }
        catch (Exception ex)
        {
            InsertLog($"打开文件失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>收起「打开」下拉菜单。点菜单项后先收菜单，对话框与确认框不会压在菜单上面。</summary>
    private void HideOpenMenu() => BtnOpen?.Flyout?.Hide();

    private void RememberRecentFile(string path)
    {
        _cfg.RememberRecentFile(path);
        SaveSettings();      // 立刻落盘：下次启动就能直接再打开
        RefreshRecentUi();
        InsertLog($"已记入「最近打开」：{System.IO.Path.GetFileName(path)}");
    }

    /// <summary>把一个路径移出「最近打开」并落盘（文件已被删或改名时用）。</summary>
    private void ForgetRecentFile(string path)
    {
        _cfg.ForgetRecentFile(path);
        SaveSettings();
        RefreshRecentUi();
    }

    private void RefreshRecentUi()
    {
        if (BtnOpen?.Flyout is not MenuFlyout menu) return;

        menu.Items.Clear();

        // 不再重复放「打开文件…」：SplitButton 的主体就是它，菜单里再放一条是同一个动作两个入口。
        var openFolder = new MenuItem { Header = "打开文件夹…" };
        openFolder.Click += BtnOpenFolder_Click;
        menu.Items.Add(openFolder);

        var playlist = new MenuItem { Header = "歌单…" };
        playlist.Click += (_, _) => { HideOpenMenu(); PlaylistOpen_Click(null, new RoutedEventArgs()); };
        menu.Items.Add(playlist);

        menu.Items.Add(new Separator());

        menu.Items.Add(BuildRecentMenu());
    }

    /// <summary>「最近打开 ▸」子菜单：内容与旧版那个平铺列表相同（文件名 + 完整路径 + 清空列表）。</summary>
    private MenuItem BuildRecentMenu()
    {
        var root = new MenuItem { Header = "最近打开 ▸" };
        var files = _cfg.RecentFiles;
        if (files.Count == 0)
        {
            // 一次都没打开过：给一条禁用的说明，不留空菜单
            root.Items.Add(new MenuItem { Header = "还没有打开过文件", IsEnabled = false });
            return root;
        }
        foreach (string path in files)
        {
            var item = new MenuItem
            {
                Header = System.IO.Path.GetFileName(path),   // 菜单里只显示文件名
                Tag = path,
            };
            Avalonia.Controls.ToolTip.SetTip(item, path);    // 悬浮显示完整路径
            item.Click += RecentFile_Click;
            root.Items.Add(item);
        }
        root.Items.Add(new Separator());
        var clear = new MenuItem { Header = "清空列表" };
        clear.Click += ClearRecent_Click;
        root.Items.Add(clear);
        return root;
    }

    /// <summary>点「最近打开 ▸」里的某一项：先收起菜单，再走打开流程。</summary>
    private void RecentFile_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string path) return;
        HideOpenMenu();
        _ = OpenRecentFileAsync(path);
    }

    private async Task OpenRecentFileAsync(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path))
            {
                InsertLog($"文件已不在：{path}");
                ForgetRecentFile(path);
                return;
            }
            if (!await ConfirmDiscardEditsAsync("打开最近文件")) return;
            LoadMidiFile(path);
        }
        catch (Exception ex)
        {
            InsertLog($"打开最近文件失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static string DescribeRoles(IReadOnlyList<MidiCandidate> candidates)
    {
        var parts = new List<string>();
        foreach (var c in candidates)
        {
            string name = c.RoleTag + (c.RoleGuessed ? "?" : "");
            parts.Add($"轨{c.TrackIndex + 1} {name}");
            if (parts.Count >= 12) break;
        }
        return parts.Count == 0 ? "" : string.Join("、", parts)
            + (candidates.Count > parts.Count ? $" 等 {candidates.Count} 条" : "");
    }

    /// <summary>清空「最近打开」列表并落盘。</summary>
    private void ClearRecent_Click(object? sender, RoutedEventArgs e)
    {
        HideOpenMenu();
        _cfg.ClearRecentFiles();
        SaveSettings();
        RefreshRecentUi();
        InsertLog("已清空「最近打开」列表。");
    }

    internal bool LoadMidiFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        // 暂停/播放中也能换歌：先停掉当前播放，避免新旧串曲
        StopPlaybackForNewFile();

        try
        {
            var parsed = MidiLoader.Parse(path);
            _parsed = parsed;
            _tracks.Clear();
            _mixOrder.Clear();
            foreach (var c in parsed.Candidates) _tracks.Add(new TrackRowVM(c));

            LblFile.Text = System.IO.Path.GetFileName(path);
            InsertLog($"已载入 {System.IO.Path.GetFileName(path)}：{parsed.Candidates.Count} 个候选，时长 ≈ {parsed.DurationSec:F1}s");
            // 识别出的声部：左侧「声部」列已经写着，这里再给一行汇总，方便直接看出这首曲子有哪些声部
            string parts = DescribeRoles(parsed.Candidates);
            if (parts.Length > 0) InsertLog($"声部识别：{parts}");

            _selected = null;
            _previewSeconds = 0;      // 换歌必须回到 0，否则上一首的位置会夹到新曲末尾 → 一播放就结束
            Roll.FitAll();
            DropEditsIfAny("打开了新文件");
            // 自动推荐轨已移除（猜得不准反而误导）：载入后不预选，请用户自己点一行
            InsertLog("点左侧一行作为主旋律轨（选中的行带 ● 标记）。");
            RefreshPreview();
            // 这首歌在歌单里勾过音轨就按勾选来：文件夹曲目卡、最近打开、双击歌单，
            // 三条路载入同一首看到的勾选与画面必须一样
            ApplyPlaylistTracksFor(path);
            RememberRecentFile(path);
            string dir = System.IO.Path.GetDirectoryName(path) ?? "";
            if (dir.Length > 0 && !string.Equals(dir, _folderPath, StringComparison.OrdinalIgnoreCase))
                ScanMidiFolder(dir);
            // 曲目卡里给这首打上选中标记（当前已载入的那一首）
            RefreshFolderUi();
            return true;
        }
        catch (Exception ex)
        {
            var parts = new List<string>();
            Exception? inner = ex;
            while (inner != null)
            {
                parts.Add($"{inner.GetType().Name}: {inner.Message}");
                inner = inner.InnerException;
            }
            InsertLog($"载入失败：{path}");
            InsertLog($"  原因：{string.Join("  <-  ", parts)}（错误码 0x{ex.HResult:X8}）");
            // 记位置：这类异常多数出在「载入之后的界面刷新」，只有原因看不出来是哪一行。
            InsertLog($"  位置：{FirstStackFrames(ex, 4)}");
            return false;
        }
    }

    /// <summary>把异常调用栈的前几帧合成一行，供日志定位。没有栈就返回「（无）」。</summary>
    internal static string FirstStackFrames(Exception ex, int count)
    {
        string? stack = ex.StackTrace;
        if (string.IsNullOrWhiteSpace(stack)) return "（无）";
        var frames = new List<string>();
        foreach (string raw in stack.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            frames.Add(line);
            if (frames.Count >= count) break;
        }
        return frames.Count == 0 ? "（无）" : string.Join(" | ", frames);
    }

    /// <summary>换歌前停掉旧曲（松开按键、释放引擎）。</summary>
    private void StopPlaybackForNewFile()
    {
        if (_countdownTimer != null)
        {
            _countdownTimer.Stop();
            _countdownTimer = null;
            ResetUi();   // 走现有停止路径：恢复 _busy、按钮与倒计时配色
            InsertLog("已取消倒计时（换歌）。");
            return;
        }
        if (_engine is { IsRunning: true })
        {
            StopPlaybackNow();
            InsertLog("已停止当前播放（换歌）。");
            return;
        }
        if (_previewOn || (_previewSeq?.IsRunning ?? false))
        {
            StopPreviewAudio();
            InsertLog("已停止试听（换歌）。");
        }
    }

    // ================= 主旋律选择 =================

    private async void TrackList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_revertingTrack) return;
        if (_advancing) return;
        if (TrackList.SelectedItem is not TrackRowVM row) return;
        try
        {
            await SetMain(row);
        }
        catch (Exception ex)
        {
            // async void 里漏出的异常会直接崩进程：与其他处理器一样只记日志
            InsertLog($"换主旋律轨失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task SetMain(TrackRowVM? row)
    {
        if (row == null) return;
        // RV-09：换主旋律轨会重建谱面并丢弃手动改动，先确认一次；取消则保持原状。
        if (!await ConfirmDiscardEditsForActionAsync("换主旋律轨"))
        {
            _revertingTrack = true;
            try { TrackList.SelectedItem = _selected; }
            finally { _revertingTrack = false; }
            return;
        }
        DropEditsIfAny("换了主旋律轨");
        foreach (var r in _tracks) r.IsMain = ReferenceEquals(r, row);
        _selected = row;
        UpdateVoiceRoles();   // 未勾合奏时主旋律轨就是 0 号色
        RefreshPreview();
    }

    // ================= 映射与预览 =================

    private int CurrentTranspose => _syncTranspose ?? (int)SliderTranspose.Value;

    private List<TrackRowVM> ActiveRows()
    {
        var mix = _mixOrder.Where(r => r.IsMix).ToList();
        if (mix.Count == 0 && _selected != null) mix = new List<TrackRowVM> { _selected };

        if (_syncVoiceMask != null)
        {
            var allowed = new List<TrackRowVM>();
            for (int i = 0; i < mix.Count; i++)
                if (_syncVoiceMask.Contains(i)) allowed.Add(mix[i]);
            return allowed;
        }
        return mix;
    }

    private int VoiceIndexOf(TrackRowVM row)
    {
        if (!row.IsVoiceActive) return -1;
        if (_syncVoiceMask != null)
        {
            var rows = ActiveRows();
            int idx = rows.IndexOf(row);
            return idx >= 0 ? idx : -1;
        }
        if (row.IsMix)
        {
            int i = _mixOrder.IndexOf(row);
            return i >= 0 ? i : -1;
        }
        return ReferenceEquals(row, _selected) ? 0 : -1;
    }

    /// <summary>重算每行的「参与演奏 / 声部序号」，并按序号写名称列文字颜色。</summary>
    private void UpdateVoiceRoles()
    {
        bool mixing = _mixOrder.Any(r => r.IsMix);
        if (_syncVoiceMask != null)
        {
            var rows = ActiveRows();
            foreach (var row in _tracks) row.IsVoiceActive = rows.Contains(row);
        }
        else
        {
            foreach (var row in _tracks)
                row.IsVoiceActive = mixing ? row.IsMix : ReferenceEquals(row, _selected);
        }

        ApplyVoiceBrushes();

        // 卷帘：每个音自带 Voice（合奏时写死），这里只补一份「音高 → 声部」兜底表给手加的音
        Roll.SetVoiceColors(BuildVoiceMap(GetActiveRawNotes()));
    }

    /// <summary>这一首的候选行（不进主界面）。歌单窗口勾音轨时要按轨号取音符，不能靠 _tracks。</summary>
    private ParsedMidi? _previewParsed;

    internal void PreviewTracks(string path, IReadOnlyList<TrackRef> picks)
    {
        if (path.Length == 0) return;
        try
        {
            _previewParsed = MidiLoader.Parse(path);
        }
        catch (Exception ex)
        {
            InsertLog($"预览读不动「{System.IO.Path.GetFileName(path)}」：{ex.Message}");
            return;
        }

        var cands = _previewParsed.Candidates;
        if (cands.Count == 0) { InsertLog("这一首没有任何候选音轨。"); return; }

        var chosen = new List<MidiCandidate>();
        if (picks.Count == 0)
        {
            chosen.AddRange(cands);
        }
        else
        {
            foreach (var t in picks)
            {
                var c = cands.FirstOrDefault(x => x.TrackIndex == t.TrackIndex && x.Channel == t.Channel);
                if (c != null) chosen.Add(c);
            }
        }
        if (chosen.Count == 0) return;

        // 与真正演奏时同一套合并与编号：颜色号 = 勾选顺序，与主界面「合」列同口径
        var voices = new List<(int Rank, RawNote Note)>();
        for (int k = 0; k < chosen.Count; k++)
            foreach (var n in chosen[k].Notes) voices.Add((k, n));
        var merged = NoteMapper.MergeVoicesByPriority(voices);

        Roll.BeginPreview();
        Roll.SetNotes(merged, merged.Select(n => n.Pitch).Distinct().ToList(), _previewParsed.DurationSec);
        Roll.SetVoiceColors(BuildVoiceMap(merged));
        InsertLog($"预览这一首勾的 {chosen.Count} 行：共 {merged.Count} 个音（只看，不改谱）。");
    }

    /// <summary>【歌单预览】结束预览，卷帘回到当前演奏的那份谱面。</summary>
    internal void EndPreviewTracks()
    {
        if (!Roll.PreviewOnly) return;
        _previewParsed = null;
        Roll.RestoreAfterPreview();
        InsertLog("结束预览，卷帘回到当前演奏的谱面。");
    }

    /// <summary>【开发用】跑一次歌单预览，返回卷帘上的状态，供回归探针断言。</summary>
    internal string PreviewTracksForDev(string path, params (int Track, int Channel)[] picks)
    {
        var list = picks.Select(p => new TrackRef { TrackIndex = p.Track, Channel = p.Channel }).ToList();
        PreviewTracks(path, list);
        string state = $"卷帘 {Roll.NoteCount} 个音；只看模式={Roll.PreviewOnly}；"
                     + $"勾了 {list.Count} 行；标题={LblMelody.Text}";
        EndPreviewTracks();
        return state + $"；收尾后 卷帘 {Roll.NoteCount} 个音，只看模式={Roll.PreviewOnly}";
    }

    private void ApplyVoiceBrushes()
    {
        foreach (var row in _tracks)
        {
            row.VoiceIndex = VoiceIndexOf(row);
            IBrush brush;
            if (row.IsVoiceActive)
            {
                brush = ResourceBrush("BrushVoice" + Music.Mod(row.VoiceIndex, PianoRoll.VoiceCount));
            }
            else
            {
                brush = ResourceBrush("BrushTextMuted");
            }
            row.VoiceBrush = brush;

            row.RoleBrush = RoleBrushOf(row.Candidate.Role);
            row.RoleActive = row.IsVoiceActive;
        }
    }

    private static IBrush? RoleBrushOf(TrackRole role)
    {
        string key = GmInstrument.ToneOf(role) switch
        {
            RoleTone.Rhythm => "BrushRoleRhythm",
            RoleTone.Low => "BrushRoleLow",
            RoleTone.Harmony => "BrushRoleHarmony",
            RoleTone.Lead => "BrushRoleLead",
            RoleTone.Other => "BrushRoleOther",
            _ => "",
        };
        if (key.Length == 0) return null;
        return ThemeSwitch.BrushOf(key);
    }

    private static IBrush ResourceBrush(string key) => ThemeSwitch.BrushOf(key) ?? FallbackMuted;

    private static double ResourceFontSize(string key, double fallback)
    {
        if (Application.Current?.TryFindResource(key, out var found) == true && found is double d)
            return d;
        return fallback;
    }

    private Dictionary<int, int> BuildVoiceMap(IReadOnlyList<RawNote> kept)
    {
        var map = new Dictionary<int, int>();
        for (int i = 0; i < kept.Count; i++)
        {
            var n = kept[i];
            int v = n.Voice >= 0 ? n.Voice : 0;
            if (map.TryGetValue(n.Pitch, out int old) && old <= v) continue;
            map[n.Pitch] = v;
        }
        return map;
    }

    /// <summary>当前谱面：手动编辑过就用编辑结果，否则用自动提取结果。</summary>
    private List<RawNote> GetActiveRawNotes() =>
        _editing ? _editor.Notes.ToList() : ComputeAutoNotes();

    private List<RawNote> ComputeAutoNotes()
    {
        var rows = ActiveRows();
        if (rows.Count == 0)
        {
            _removedLeadSec = 0;
            return new List<RawNote>();
        }

        var voices = new List<(int Rank, RawNote Note)>();
        for (int k = 0; k < rows.Count; k++)
            foreach (var n in rows[k].Candidate.Notes) voices.Add((k, n));  // 0 最优先
        var merged = NoteMapper.MergeVoicesByPriority(voices);

        // 「去除开头空拍」：整条旋律平移到第一个音从 0 秒开始（开头常有休止）
        if (ChkTrimLead.IsChecked == true)
        {
            double before = merged.Count == 0 ? 0 : merged.Min(n => n.Start);
            merged = NoteMapper.TrimLeadingSilence(merged);
            double after = merged.Count == 0 ? 0 : merged.Min(n => n.Start);
            _removedLeadSec = Math.Max(0, before - after);
        }
        else
        {
            _removedLeadSec = 0;
        }
        return merged;
    }

    private MappingResult MapAt(int transpose) =>
        NoteMapper.Map(GetActiveRawNotes(), transpose, manualBaseOctave: null,
                       foldOctave: _cfg.FoldOctave);

    private MappingResult BuildMapping() =>
        GetActiveRawNotes().Count == 0 ? new MappingResult() : MapAt(CurrentTranspose);

    private void ExportGhub_Click(object? sender, RoutedEventArgs e)
        => ExportSchedule(MacroExporter.Format.LogitechGHub);

    private void ExportCsv_Click(object? sender, RoutedEventArgs e)
        => ExportSchedule(MacroExporter.Format.KeystrokeCsv);

    private async void ExportSchedule(MacroExporter.Format format)
    {
        try
        {
            var map = BuildMapping();
            var playable = NoteMapper.Playable(map.Notes);
            if (playable.Count == 0)
            {
                InsertLog("没有可演奏的音，无法导出。请调整「移调」或换一行。");
                return;
            }

            double speed = SliderSpeed.Value / 100.0;
            var timing = InputTiming.FromIndex(TimingCombo.SelectedIndex);

            string songName = _parsed == null ? "" : Path.GetFileNameWithoutExtension(_parsed.FilePath);
            string content = MacroExporter.Build(playable, format, speed, timing, songName);

            var (nCount, nEvents, seconds) = MacroExporter.Summarize(playable, speed, timing);
            string suggested = (string.IsNullOrWhiteSpace(songName) ? "midikey" : songName) +
                               MacroExporter.Extension(format);

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出按键表",
                SuggestedFileName = suggested,
                DefaultExtension = MacroExporter.Extension(format).TrimStart('.'),
                FileTypeChoices = new List<FilePickerFileType>
                {
                    new("导出文件") { Patterns = new List<string> { "*" + MacroExporter.Extension(format) } }
                }
            });
            if (file == null) return;

            string outPath = file.TryGetLocalPath() ?? "";
            if (string.IsNullOrEmpty(outPath))
            {
                InsertLog("导出失败：拿不到目标路径。");
                return;
            }
            await File.WriteAllTextAsync(outPath, content, new UTF8Encoding(true));

            InsertLog($"已导出按键表：{playable.Count} 音 / {nEvents} 事件 / {seconds:F1}s" +
                      $"（速度 {speed * 100:F0}%、档位 {timing.Name}）");
            InsertLog($"　文件：{outPath}");
            if (format == MacroExporter.Format.LogitechGHub)
                InsertLog("　G HUB 用法：设备 → 应用程序 → 添加应用程序 → 编写脚本 → 整段粘贴保存。");
            else
                InsertLog("　提示：雷蛇 Synapse 宏是私有格式，请用其宏录制功能代替。");
        }
        catch (Exception ex)
        {
            InsertLog($"导出失败：{ex.Message}");
        }
    }

    private void BtnAutoTranspose_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy || GetActiveRawNotes().Count == 0) return;

        int best = 0;
        int bestSkip = int.MaxValue;
        for (int t = -6; t <= 6; t++)
        {
            var m = MapAt(t);
            if (m.SkipCount < bestSkip ||
                (m.SkipCount == bestSkip && Math.Abs(t) < Math.Abs(best)))
            {
                bestSkip = m.SkipCount;
                best = t;
            }
        }

        SliderTranspose.Value = best;   // 触发滑块事件：保存设置并刷新预览
        string sign = best > 0 ? "+" : "";
        if (bestSkip == 0)
            InsertLog($"一键移调：整体 {sign}{best} 半音后全部音都有对应的键。");
        else
            InsertLog($"一键移调：整体 {sign}{best} 半音后仍有 {bestSkip} 个音没有对应的键（跨度太大，会被跳过）");
        RefreshPreview();
    }

    private void BtnNaturalTranspose_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;

        // 谱面只算一次：MapAt 每次都会重建（GetActiveRawNotes 会跑一遍合并与提取）
        var raw = GetActiveRawNotes();
        if (raw.Count == 0) return;

        // 自然音判据的基准音：当前键位方案的 BaseNote（默认 60 = C4）
        int baseNote = KeymapProfile.Current.BaseNote;
        int lo = -AppConfig.MaxTransposeSemitones;
        int hi = AppConfig.MaxTransposeSemitones;

        int bestT = 0, bestNatural = -1, bestAccidental = 0, bestSkip = int.MaxValue;
        int total = -1;   // 基准音高集合的音符数，用真实映射结果填（含被合并/丢弃的音）

        for (int t = lo; t <= hi; t++)
        {
            var map = NoteMapper.Map(raw, t, manualBaseOctave: null, foldOctave: _cfg.FoldOctave);
            total = map.Notes.Count;   // 所有候选都映射同一份音符，每轮相等

            int natural = 0, accidental = 0, skip = 0;
            foreach (var n in map.Notes)
            {
                if (!n.InRange) { skip++; continue; }
                if (IsNaturalPitch(n.SoundingPitch - baseNote)) natural++;
                else accidental++;
            }

            bool win = natural > bestNatural
                       || (natural == bestNatural && skip < bestSkip)
                       || (natural == bestNatural && skip == bestSkip && Math.Abs(t) < Math.Abs(bestT));
            if (win)
            {
                bestNatural = natural;
                bestAccidental = accidental;
                bestSkip = skip;
                bestT = t;
            }
        }

        if (bestNatural < 0) return;

        int playable = bestNatural + bestAccidental;
        if (playable == 0)
        {
            InsertLog("自然音移调：当前谱面没有一个音在能弹范围内，无法比较。请先调「移调」或换一行。");
            return;
        }

        SliderTranspose.Value = bestT;   // 触发滑块事件：保存设置并刷新预览
        string sign = bestT > 0 ? "+" : "";
        InsertLog($"自然音移调：{sign}{bestT} 半音后 {bestNatural} / {playable} 个音落在自然键上，"
                  + $"{bestAccidental} 个需要半音键，{bestSkip} 个没有对应的键。");
        if (bestAccidental > 0)
            InsertLog("　其中需要半音键的音，换个带升音的方案才能弹（例如「第五人格键位」）。");
        if (total > playable)
            InsertLog($"　另有 {total - playable} 个音在移调后超出 MIDI 音域（0 ~ 127）。");
        RefreshPreview();
    }

    /// <summary>自然音（白键）判据：相对基准音的半音数落在 {0,2,4,5,7,9,11}。</summary>
    private static bool IsNaturalPitch(int semitoneFromBase) => (semitoneFromBase % 12 + 12) % 12
        is 0 or 2 or 4 or 5 or 7 or 9 or 11;

    /// <summary>勾选“合”：勾选先后即主次（先勾=1 主）；勾完立即刷新，可直接播放。</summary>
    private async void Mix_Changed(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is CheckBox cb && cb.DataContext is TrackRowVM row)
            {
                if (_revertingMix) return;   // RV-09：取消后回退勾选，忽略自触发的事件
                bool on = cb.IsChecked == true;
                // RV-09：改合奏勾选会重建谱面并丢弃手动改动，先确认一次；取消则把勾选退回原值。
                if (!await ConfirmDiscardEditsForActionAsync("改合奏声部"))
                {
                    _revertingMix = true;
                    try { cb.IsChecked = !on; }   // 双向绑定同时把 IsMix 退回
                    finally { _revertingMix = false; }
                    return;
                }
                DropEditsIfAny("改了合奏声部");
                row.IsMix = on;   // 保证模型状态一致
                if (on)
                {
                    if (!_mixOrder.Contains(row)) _mixOrder.Add(row);
                }
                else
                {
                    _mixOrder.Remove(row);
                }
            }
            SyncMixOrder();
        }
        catch (Exception ex)
        {
            // async void 里漏出的异常会直接崩进程：与其他处理器一样只记日志
            InsertLog($"改合奏声部失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    internal void SyncMixOrder()
    {
        // 兜底同步 IsMix 状态
        foreach (var r in _tracks)
            if (r.IsMix && !_mixOrder.Contains(r)) _mixOrder.Add(r);
        _mixOrder.RemoveAll(r => !r.IsMix);

        for (int i = 0; i < _mixOrder.Count; i++) _mixOrder[i].MixRank = i + 1; // 1=主，2=次，3=更次
        foreach (var r in _tracks)
            if (!_mixOrder.Contains(r)) r.MixRank = 0;

        UpdateVoiceRoles();   // 声轨颜色号跟着勾选顺序走

        if (_busy || _previewDeb is null) return;
        _previewDeb.Stop();
        RefreshPreview();   // 立即刷新，勾完即可播放
    }

    private void Option_Changed(object? sender, RoutedEventArgs e)
    {
        ScheduleSave();
        if (_busy || _previewDeb is null) return;
        _previewDeb.Stop();
        _previewDeb.Start();
    }

    // ================= 播放前自检 =================

    /// <summary>播放前自检管理员权限与前台输入法，结论显示在主界面状态卡（✔ / ✘），明细写日志。</summary>
    private void RunPreflight()
    {
        PreflightCheck.Report report;
        try
        {
            report = PreflightCheck.Run();
        }
        catch
        {
            // 检测失败就不显示结论，避免给出误导性的 ✘
            TxtCheckAdminMark.Text = "–";
            TxtCheckAdminMark.Foreground = NeutralBrush;
            TxtCheckImeMark.Text = "–";
            TxtCheckImeMark.Foreground = NeutralBrush;
            TxtCheckHint.Text = "";
            TxtCheckHint.IsVisible = false;
            return;
        }

        PaintCheck(report.Admin, TxtCheckAdminMark, TxtCheckAdmin);
        PaintCheck(report.Ime, TxtCheckImeMark, TxtCheckIme);
        BtnElevate.IsVisible = !report.Admin.Passed;   // 未提权时给「以管理员重启」入口（按需提权）

        // 全通过就没有要说的；留一行空文字会白占主界面状态卡的高度
        string hint = report.HasBlocked
            ? string.Join("；", new[] { report.Admin, report.Ime }
                .Where(c => c.Blocked)
                .Select(c => c.Detail))
            : string.Join("；", new[] { report.Admin, report.Ime }
                .Where(c => !c.Passed)
                .Select(c => c.Detail));
        TxtCheckHint.Text = hint;
        TxtCheckHint.IsVisible = hint.Length > 0;

        // 输入法状态受系统影响，读不出来时把原始证据写进日志，方便远程排查
        try
        {
            var (_, probe) = PreflightCheck.DetectImeModeDetailed();
            Persist.LogFile.Append($"[自检] 输入法状态={report.Ime.Status}；{PreflightCheck.Describe(probe)}");
        }
        catch { /* 检测失败不影响使用 */ }
    }

    /// <summary>手动刷新自检（切换输入法后点一下即可）。</summary>
    private void BtnRecheck_Click(object? sender, RoutedEventArgs e)
    {
        RunPreflight();
        InsertLog("已重新检测管理员权限与输入法。");
    }

    private void BtnElevate_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? "MidiKeyPlayer.exe",
                Verb = "runas",
                UseShellExecute = true,
            };
            System.Diagnostics.Process.Start(psi);
            InsertLog("已用管理员身份重新启动，本实例退出。");
            QuitApp();
        }
        catch (Exception ex)
        {
            InsertLog("提权重启已取消或失败：" + ex.Message);
        }
    }

    private static readonly Avalonia.Media.IBrush FallbackMuted =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#6B7480"));

    private static Avalonia.Media.IBrush OkBrush => ResourceBrush("BrushSuccess");
    private static Avalonia.Media.IBrush FailBrush => ResourceBrush("BrushDanger");
    private static Avalonia.Media.IBrush NeutralBrush => ResourceBrush("BrushTextMuted");
    private static Avalonia.Media.IBrush WarningBrush => ResourceBrush("BrushWarn");

    /// <summary>通过打勾、提醒打问号、未通过打叉。</summary>
    private static void PaintCheck(PreflightCheck.Check c,
                                   Avalonia.Controls.TextBlock mark,
                                   Avalonia.Controls.TextBlock label)
    {
        mark.Text = c.Status switch
        {
            PreflightCheck.Status.Pass => "✔",
            PreflightCheck.Status.Warn => "•",
            _ => "✘"
        };
        mark.Foreground = c.Status switch
        {
            PreflightCheck.Status.Pass => OkBrush,
            PreflightCheck.Status.Warn => NeutralBrush,
            _ => FailBrush
        };
        label.Foreground = mark.Foreground;
        // 行内只留短名，细节统一放在下面那行 TxtCheckHint 里 —— 否则同一句话会出现两次
        label.Text = c.Name;
    }

    // ================= 自动检查更新 =================

    /// <summary>后台查询 GitHub 最新 Release，有新版则顶部显示提示条；没网/被墙静默忽略。</summary>
    private async Task CheckUpdateAsync()
    {
        try
        {
            // 无人值守的探针 / 快照模式不查更新：不往测试环境引网络请求
            if (PreviewProbeMode.On || DevSnapshotMode.On) return;

            var r = await AutoUpdate.CheckAsync(_cfg?.SkippedUpdateTag);
            if (r.Error != null || !r.HasUpdate) return;

            // 强制更新：最新版说明里写了 [强制更新]，或当前版本低于硬编码的强制线。
            // 两种情况都不吃「跳过」，直接进强制更新浮层。
            bool required = r.Mandatory || AutoUpdate.IsRequiredVersion(r.CurrentTag);
            if (r.Skipped && !required) return;

            UiPost(() =>
            {
                if (required)
                {
                    ShowForcedUpdate(r.LatestTag, r.ReleaseUrl, r.AssetUrl);
                    return;
                }

                _updateUrl = r.ReleaseUrl;
                _updateTag = r.LatestTag;
                _updateAssetUrl = r.AssetUrl;

                TxtUpdate.Text = $"发现新版本 v{r.LatestTag}（当前 v{r.CurrentTag}）——" +
                                 "点此自动下载并更新；右键跳过本版本。";
                UpdateBanner.IsVisible = true;
                InsertLog($"发现新版本：v{r.LatestTag}（当前 v{r.CurrentTag}）　下载页：{r.ReleaseUrl}");
            });
        }
        catch
        {
            // 检查更新失败不影响任何功能
        }
    }

    /// <summary>左键点提示条 = 自动下载并更新（下好后 = 重启完成更新）；右键点 = 跳过本版本。</summary>
    private async void UpdateBanner_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (string.IsNullOrEmpty(_updateUrl)) return;

        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed)
        {
            if (_cfg != null && !string.IsNullOrEmpty(_updateTag))
            {
                _cfg.SkippedUpdateTag = _updateTag;
                _cfg.Save();
                UpdateBanner.IsVisible = false;
                InsertLog($"已跳过 v{_updateTag}；下个新版本仍会提示。");
            }
            return;
        }

        if (_updateBusy)
        {
            // 下载中再点一次 = 取消下载（正在应用更新的那一下不可取消）
            if (_updateNewExe.Length == 0 && _updateCts != null)
            {
                _updateCts.Cancel();
                _updateBusy = false;
                TxtUpdate.Text = "已取消下载——点此重新下载；右键跳过本版本。";
                InsertLog("已取消更新包下载。");
            }
            return;
        }

        // 更新包已下载并校验通过：这一次点击 = 重启并完成更新
        if (_updateNewExe.Length > 0)
        {
            ApplyDownloadedUpdate();
            return;
        }

        // 没有更新包直链（老 Release 或资产缺失）：退回打开下载页手动下载
        if (_updateAssetUrl.Length == 0)
        {
            AutoUpdate.OpenUrl(_updateUrl);
            InsertLog($"已打开下载页：{_updateUrl}");
            return;
        }

        await DownloadUpdateAsync();
    }

    /// <summary>下载更新包并校验。完成后提示条变成「点此重启完成更新」；失败时可再点重试。</summary>
    private async Task DownloadUpdateAsync()
    {
        _updateBusy = true;
        _updateCts = new CancellationTokenSource();
        SetUpdateText($"正在下载 v{_updateTag} …… 0%");
        InsertLog($"开始下载更新包：{_updateAssetUrl}");

        int lastPct = -1;
        var progress = new Progress<double>(p =>
        {
            int pct = (int)Math.Round(p * 100);
            if (pct == lastPct) return;
            lastPct = pct;
            SetUpdateText($"正在下载 v{_updateTag} …… {pct}%");
        });

        string? zip = await AutoUpdate.DownloadAsync(_updateAssetUrl, progress, _updateCts.Token);
        if (zip == null)
        {
            _updateBusy = false;
            // 关程序触发的取消不刷新提示条（窗口已经在关）
            if (!_updateCts.IsCancellationRequested)
            {
                SetUpdateText("更新包下载失败——点此重试；右键跳过本版本。");
                InsertLog("更新包下载失败，详见 play.log。也可以点提示条重试，或去下载页手动下载。");
            }
            return;
        }

        string? newExe = AutoUpdate.ExtractNewExe(zip);
        if (newExe == null)
        {
            _updateBusy = false;
            SetUpdateText("更新包校验失败——点此重试；右键跳过本版本。");
            InsertLog("更新包校验失败，详见 play.log。也可以点提示条重试，或去下载页手动下载。");
            return;
        }

        _updateNewExe = newExe;
        _updateBusy = false;
        SetUpdateText($"v{_updateTag} 已下载完成——点此重启并完成更新；右键跳过本版本。");
        InsertLog($"v{_updateTag} 更新包已就绪，重启程序后生效。");
    }

    private void SetUpdateText(string text)
    {
        TxtUpdate.Text = text;
        if (_forcedUpdateOn && TxtForcedProgress != null) TxtForcedProgress.Text = text;
    }

    /// <summary>启动更新脚本并退出程序：脚本等本进程退出后覆盖 exe 并重启到新版。</summary>
    private void ApplyDownloadedUpdate()
    {
        // 有未导出的卷帘改动时不直接退出（UI-02 的口径：改动只存在内存里，退出就没了）
        if (HasUnexportedEdits)
        {
            SetUpdateText("有未导出的卷帘改动：请先「导出 MIDI…」保存，再点这里更新。");
            return;
        }
        _updateBusy = true;
        if (!AutoUpdate.StartUpdater(_updateNewExe))
        {
            _updateBusy = false;
            _updateNewExe = "";
            _updateAssetUrl = "";   // 自动更新走不通，退回手动：下次点击打开下载页
            SetUpdateText("自动更新启动失败——点此打开下载页手动下载；右键跳过本版本。");
            InsertLog("自动更新启动失败，详见 play.log。");
            return;
        }
        InsertLog("正在应用更新：程序将退出并自动重启到新版……");
        if (_engine != null) StopPlaybackNow();
        Close();
    }

    // ================= 「关于」：手动检查更新 + 内置文档 =================

    /// <summary>手动检查更新：结果直说（静默检查失败是吞掉的，手动不行）。</summary>
    private async void CheckUpdateManual_Click(object? sender, RoutedEventArgs e)
    {
        BtnCheckUpdate.IsEnabled = false;
        InsertLog("正在检查更新……");
        try
        {
            var r = await AutoUpdate.CheckAsync(_cfg?.SkippedUpdateTag);
            if (r.Error != null)
            {
                InsertLog("检查更新失败（网络不通或被拦截），稍后再试。原因见 play.log。");
                global::MidiKeyPlayer.Persist.LogFile.Append($"[更新] 手动检查失败：{r.Error}");
                return;
            }
            if (r.HasUpdate)
            {
                if (r.Mandatory || AutoUpdate.IsRequiredVersion(r.CurrentTag))
                {
                    // 手动检查也不例外：低于强制线的版本只能更新
                    ShowForcedUpdate(r.LatestTag, r.ReleaseUrl, r.AssetUrl);
                    return;
                }

                _updateUrl = r.ReleaseUrl;
                _updateTag = r.LatestTag;
                _updateAssetUrl = r.AssetUrl;
                // 已跳过的版本也摆出来：手动检查就是用户反悔的机会
                TxtUpdate.Text = r.Skipped
                    ? $"v{r.LatestTag}（当前 v{r.CurrentTag}）已被你跳过——点此仍可下载并更新；右键继续跳过。"
                    : $"发现新版本 v{r.LatestTag}（当前 v{r.CurrentTag}）——点此自动下载并更新；右键跳过本版本。";
                UpdateBanner.IsVisible = true;
                InsertLog($"发现新版本：v{r.LatestTag}（当前 v{r.CurrentTag}）");
            }
            else
            {
                InsertLog($"已是最新版本（v{r.CurrentTag}）。");
            }
        }
        finally
        {
            BtnCheckUpdate.IsEnabled = true;
        }
    }

    // ================= 免费声明 / 作者链接 / 强制更新 =================

    private bool _forcedUpdateOn;      // 强制更新浮层正在显示：所有「开始播放」的入口都挡住
    private int _quizFails;             // 验证题答错次数：第一次只提示，第二次才把答案说出来

    /// <summary>把免费声明与作者链接从常量填进界面（常量只有一份，见 AutoUpdate）。</summary>
    private void FillAboutLinks()
    {
        if (TxtFreeNotice != null) TxtFreeNotice.Text = AutoUpdate.FreeNoticeShort;
        if (BtnAuthorSpace != null) ToolTip.SetTip(BtnAuthorSpace, AutoUpdate.AuthorSpaceUrl);
        if (BtnCopyQq != null) BtnCopyQq.Content = $"复制 QQ 群号 {AutoUpdate.QqGroupNumber}";
        // 启动浮层里的免费声明：用正式版全文，不用常规页那两行缩写
        if (TxtFreeNoticeBody != null) TxtFreeNoticeBody.Text = AutoUpdate.FreeNotice;
        if (TxtForcedNotice != null) TxtForcedNotice.Text = AutoUpdate.FreeNotice;
    }

    private void QuizConfirm_Click(object? sender, RoutedEventArgs e) => SubmitQuizAnswer();

    /// <summary>验证题输入框：直接回车等于点「确认」。</summary>
    private void QuizAnswer_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        SubmitQuizAnswer();
    }

    private void SubmitQuizAnswer()
    {
        if (_cfg == null) return;
        if (!AutoUpdate.IsAuthorAnswer(TxtQuizAnswer.Text))
        {
            _quizFails++;
            // 不把答案写在题面上：答错只指路，让人自己去作者主页看
            TxtQuizError.Text = _quizFails == 1
                ? "不对。作者就是 B 站上做这个程序的人，点下面的「B 站主页」看一眼再填。"
                : "还是不对。答案就在下面的「B 站主页」里，填作者在 B 站的名字。";
            TxtQuizError.IsVisible = true;
            TxtQuizAnswer.SelectAll();
            return;
        }

        _cfg.AuthorQuizPassed = true;
        _cfg.FirstRunDone = true;
        _cfg.NoticeShownVersion = AutoUpdate.NoticeVersion;
        _cfg.Save();
        if (PanelStartupQuiz != null) PanelStartupQuiz.IsVisible = false;
        if (StartupOverlay != null) StartupOverlay.IsVisible = false;   // 答对就进主界面
        InsertLog($"验证通过：本程序免费开源，作者是 B 站 {AutoUpdate.AuthorName}。");
        InsertLog($"作者 B 站：{AutoUpdate.AuthorSpaceUrlShort}　反馈 QQ 群：{AutoUpdate.QqGroupNumber}");
    }

    private void OpenAuthorSpace_Click(object? sender, RoutedEventArgs e)
    {
        AutoUpdate.OpenUrl(AutoUpdate.AuthorSpaceUrl);
        InsertLog($"已打开作者 B 站主页：{AutoUpdate.AuthorSpaceUrl}");
    }

    /// <summary>打开爱发电赞助页。所有「爱发电」按钮都走这里，含设置里「赞助」页那个。</summary>
    private void OpenAfdian_Click(object? sender, RoutedEventArgs e) => OpenAfdianPage();

    private void OpenAfdianPage()
    {
        AutoUpdate.OpenUrl(AutoUpdate.AfdianUrl);
        InsertLog($"已打开爱发电赞助页：{AutoUpdate.AfdianUrl}");
    }

    /// <summary>设置窗口「赞助」页里的爱发电按钮：窗口拿不到主窗的私有处理器，由它转进来。</summary>
    internal void OpenAfdianForSettings() => OpenAfdianPage();

    /// <summary>设置窗口「赞助」页里的 B 站按钮：复用主窗那个处理器，开链接与写日志只有一份。</summary>
    internal void OpenAuthorSpaceForSettings() => OpenAuthorSpace_Click(null, new RoutedEventArgs());

    /// <summary>设置窗口「赞助」页里的 GitHub 按钮。地址白名单只放行本仓库页面，这里是仓库主页。</summary>
    internal void OpenGitHubForSettings()
    {
        AutoUpdate.OpenUrl(AutoUpdate.GitHubUrl);
        InsertLog($"已打开作者 GitHub 主页：{AutoUpdate.GitHubUrl}");
    }

    private void OpenReleases_Click(object? sender, RoutedEventArgs e)
    {
        AutoUpdate.OpenUrl(AutoUpdate.ReleasesUrl);
        InsertLog($"已打开下载页：{AutoUpdate.ReleasesUrl}");
    }

    /// <summary>复制 QQ 群号：新版本发布与问题排查都在群里。</summary>
    private async void CopyQq_Click(object? sender, RoutedEventArgs e) => await CopyQqToClipboard(this);

    internal Task<bool> CopyQqForSettingsAsync(TopLevel owner) => CopyQqToClipboard(owner);

    private async Task<bool> CopyQqToClipboard(Visual from)
    {
        string qq = AutoUpdate.QqGroupNumber;
        try
        {
            var clipboard = TopLevel.GetTopLevel(from)?.Clipboard;
            if (clipboard == null)
            {
                InsertLog($"剪贴板不可用，QQ 群号：{qq}");
                return false;
            }
            await clipboard.SetTextAsync(qq);
            if (TxtQqCopied != null) TxtQqCopied.Text = $"已复制：{qq}";
            InsertLog($"QQ 群号已复制：{qq}");
            return true;
        }
        catch (Exception ex)
        {
            InsertLog($"复制 QQ 群号失败（{ex.GetType().Name}），群号：{qq}");
            return false;
        }
    }

    internal async Task<bool> CopyTextToClipboard(TopLevel from, string text)
    {
        try
        {
            var clipboard = from?.Clipboard;
            if (clipboard == null)
            {
                InsertLog("剪贴板不可用，请手动选中那一段文字复制。");
                return false;
            }
            await clipboard.SetTextAsync(text);
            return true;
        }
        catch (Exception ex)
        {
            InsertLog($"复制到剪贴板失败（{ex.GetType().Name}），请手动选中那一段文字复制。");
            return false;
        }
    }

    private void ShowForcedUpdate(string latestTag, string releaseUrl, string assetUrl)
    {
        _forcedUpdateOn = true;
        _updateUrl = releaseUrl;
        _updateTag = latestTag;
        _updateAssetUrl = assetUrl;

        TxtForcedTitle.Text = $"必须更新到 v{latestTag} 才能继续使用";
        TxtForcedBody.Text =
            $"你用的 v{AutoUpdate.CurrentVersion} 低于强制更新线 v{AutoUpdate.RequiredVersion}。"
            + "更新包正在自动下载；下好之后点「重启并完成更新」，程序会退出并自动重启到新版。"
            + "下载不动就点「手动下载（浏览器）」，或到 QQ 群里问。";
        ForcedUpdateOverlay.IsVisible = true;
        SetUpdateText($"正在下载 v{latestTag} …… 0%");
        if (MainBody != null) MainBody.IsEnabled = false;    // 底下的界面一律不可操作
        Input.GlobalHotkeys.SetActive(Array.Empty<int>());   // 热键也停掉：F6 不能再开弹
        InsertLog($"[更新] v{AutoUpdate.CurrentVersion} 低于强制更新线 v{AutoUpdate.RequiredVersion}，"
                  + $"必须先更新到 v{latestTag}。");
        global::MidiKeyPlayer.Persist.LogFile.Append(
            $"[更新] 强制更新：当前 v{AutoUpdate.CurrentVersion}，强制线 v{AutoUpdate.RequiredVersion}，最新 v{latestTag}");

        _ = BeginForcedDownloadAsync();
    }

    private async Task BeginForcedDownloadAsync()
    {
        if (_updateAssetUrl.Length == 0)
        {
            SetUpdateText("这个版本没有自动更新包——请点「手动下载（浏览器）」。");
            return;
        }
        await DownloadUpdateAsync();
    }

    /// <summary>强制更新浮层的「重启并完成更新」：还没下好就（重新）下载，下载中再点 = 取消。</summary>
    private void ForcedApply_Click(object? sender, RoutedEventArgs e)
    {
        if (_updateNewExe.Length > 0)
        {
            ApplyDownloadedUpdate();
            return;
        }
        if (_updateBusy)
        {
            _updateCts?.Cancel();
            _updateBusy = false;
            SetUpdateText("已取消下载——点「重启并完成更新」重新下载。");
            return;
        }
        _ = BeginForcedDownloadAsync();
    }

    private void ForcedManual_Click(object? sender, RoutedEventArgs e)
    {
        string url = string.IsNullOrEmpty(_updateUrl) ? AutoUpdate.ReleasesUrl : _updateUrl;
        AutoUpdate.OpenUrl(url);
        SetUpdateText($"已在浏览器里打开下载页：{url}　下载 zip、解出 MidiKeyPlayer.exe，覆盖当前程序即可。");
    }

    private void ForcedExit_Click(object? sender, RoutedEventArgs e) => QuitApp();

    // ================= 跳过音明细（LblWarn 点击查看） =================

    /// <summary>LblWarn：有音被跳过时点击看明细；全部有键或没载入时不响应。</summary>
    private void LblWarn_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var m = _lastMapping;
        if (m == null || m.SkipCount == 0) return;
        ShowSkippedNotes(m);
    }

    /// <summary>弹出被跳过音符的明细：时间、音高、原因（数据来自最近一次映射，最多列 500 条）。</summary>
    private void ShowSkippedNotes(MappingResult m)
    {
        var skipped = m.Notes.Where(n => !n.InRange).OrderBy(n => n.Start).ToList();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"共 {skipped.Count} 个音被跳过（按时间排序）。这些音演奏时不发声；");
        sb.AppendLine("可以用「移调」或换键位方案把它们拉回能弹的范围。");
        sb.AppendLine();
        foreach (var n in skipped.Take(500))
            sb.AppendLine($"{n.Start,8:F2}s    {Music.SolfegeName(n.Pitch)}    {n.SkipReason}");
        if (skipped.Count > 500)
            sb.AppendLine($"…… 其余 {skipped.Count - 500} 条从略。");
        new DocWindow().ShowDoc(this, "被跳过的音", sb.ToString());
    }

    // ================= 播放悬浮窗 =================

    /// <summary>悬浮窗开关：勾上时若正在播放立刻补出；取消时立刻关掉。设置即时生效。</summary>
    private void Overlay_Changed(object? sender, RoutedEventArgs e)
    {
        if (_cfg != null) _cfg.OverlayEnabled = ChkOverlay.IsChecked == true;
        ScheduleSave();
        if (!_uiReady) return;
        if (ChkOverlay.IsChecked == true)
        {
            if (_engine is { IsRunning: true } eng) ShowOverlayProgress(eng);
        }
        else
        {
            HideOverlay();
        }
    }

    private void OverlayHideOnPause_Changed(object? sender, RoutedEventArgs e)
    {
        if (_cfg != null) _cfg.OverlayHideOnPause = ChkOverlayHideOnPause.IsChecked == true;
        ScheduleSave();
        if (!_uiReady) return;
        if (_engine is not { IsRunning: true } eng) return;
        if (eng.IsPaused && _cfg?.OverlayHideOnPause == true) HideOverlay();
        else ShowOverlayProgress(eng);
    }

    private bool ShouldShowOverlay(bool paused)
        => ChkOverlay.IsChecked == true
           && !_overlayMuted
           && !(paused && _cfg?.OverlayHideOnPause == true);

    /// <summary>创建（或取出）悬浮窗，并接上位置记忆。</summary>
    private OverlayWindow EnsureOverlay()
    {
        if (_overlay != null) return _overlay;
        var w = new OverlayWindow();
        w.DragFinished += (x, y) =>
        {
            if (_cfg == null) return;
            _cfg.OverlayX = x;
            _cfg.OverlayY = y;
            ScheduleSave();
        };
        w.CloseRequested += () => { _overlayMuted = true; HideOverlay(); };
        w.Closed += (_, _) => { if (ReferenceEquals(_overlay, w)) _overlay = null; };
        _overlay = w;
        return w;
    }

    /// <summary>倒计时期间的悬浮窗（开关关掉、或本次播放被 ✕ 静音时不显示）。</summary>
    private void ShowOverlayCountdown(int secondsLeft, string nextLabel = "")
    {
        if (!ShouldShowOverlay(paused: false)) return;
        var w = EnsureOverlay();
        w.ShowCountdown(secondsLeft, nextLabel);
        w.RestorePosition(_cfg?.OverlayX ?? -1, _cfg?.OverlayY ?? -1);
    }

    /// <summary>演奏期间的悬浮窗（开关关掉、被 ✕ 静音、或暂停且勾了「暂停后关闭」时不显示）。</summary>
    private void ShowOverlayProgress(PlaybackEngine eng)
    {
        if (!ShouldShowOverlay(eng.IsPaused)) return;
        var w = EnsureOverlay();
        w.ShowProgress(eng.ElapsedSeconds, eng.TotalSeconds, eng.LoopCount, eng.IsPaused);
        w.SetQueueHint(QueueHintText());
        w.RestorePosition(_cfg?.OverlayX ?? -1, _cfg?.OverlayY ?? -1);
    }

    private string QueueHintText()
    {
        bool usePlaylist = _sourceIsPlaylist && _playlist is { Count: > 0 };
        int count = usePlaylist ? _playlist!.Count : _folderFiles.Count;
        if (count == 0) return "";

        int pos = usePlaylist ? CurrentPlaylistIndex() : CurrentFolderIndex();
        string head = pos >= 0 ? $"{pos + 1}/{count}" : $"—/{count}";

        // 单曲循环不会前进，写「下一首」会骗人，改成写明在重播
        if (CurrentPlayMode == PlayMode.RepeatOne) return $"{head} · 单曲循环";

        var decision = PlayAdvance.AfterFinish(
            CurrentPlayMode, count, pos < 0 ? 0 : pos, null, null);
        if (!decision.ShouldPlay) return $"{head} · {PlayModeNames.All[(int)CurrentPlayMode]}";

        string next = usePlaylist
            ? System.IO.Path.GetFileName(_playlist!.Entries[decision.Index].Path)
            : System.IO.Path.GetFileName(_folderFiles[decision.Index]);
        return $"{head} · 下一首 {next}";
    }

    private void HideOverlay()
    {
        _overlay?.Close();
        _overlayLastElapsed = -1;   // 节流基线一起清，下一轮播放从第一次推送开始
    }

    private void PushOverlayThrottled(PlaybackEngine eng)
    {
        double elapsed = eng.ElapsedSeconds;
        bool paused = eng.IsPaused;
        if (!ShouldShowOverlay(paused)) { _overlayLastElapsed = -1; return; }
        int loop = eng.LoopCount;
        bool dirty = _overlayLastElapsed < 0
                     || Math.Abs(elapsed - _overlayLastElapsed) >= 0.15
                     || paused != _overlayLastPaused
                     || loop != _overlayLastLoop;
        if (!dirty) return;
        _overlayLastElapsed = elapsed;
        _overlayLastPaused = paused;
        _overlayLastLoop = loop;
        ShowOverlayProgress(eng);
    }

    /// <summary>输入兼容档位：只影响下一次开始播放时的事件时序，不需要刷新预览。</summary>
    private void Timing_Changed(object? sender, SelectionChangedEventArgs e)
    {
        ScheduleSave();
        if (!_uiReady) return;
        var t = InputTiming.FromIndex(TimingCombo.SelectedIndex);
        _livePlay.Timing = t;
        InsertLog($"输入兼容档位：{t.Name}（帧 {t.FrameMs:F0}ms、修饰键提前 {t.ModLeadMs:F0}ms、" +
                  $"重触发 {t.RetriggerMs:F0}ms）");
    }

    private void Backend_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_cfg != null) _cfg.InputBackend = Math.Clamp(BackendCombo.SelectedIndex, 0, 1);
        ScheduleSave();
        ApplyBackendFromUi();
    }

    private void ApplyBackendFromUi()
    {
        var kind = BackendCombo.SelectedIndex == 1
            ? Input.InputSender.BackendKind.LogitechGHub
            : Input.InputSender.BackendKind.SendInput;
        if (Input.InputSender.SetBackend(kind, out string err, out string? warn))
        {
            if (kind == Input.InputSender.BackendKind.LogitechGHub)
            {
                InsertLog("输入方式：罗技 G HUB 驱动。注意：只发键盘，鼠标键无效；同时按住的音最多 6 个。");
                if (warn != null) InsertLog(warn);
            }
        }
        else
        {
            InsertLog("罗技 G HUB 驱动初始化失败：" + err.Replace("\n", "") + "（已保持 SendInput）");
        }
    }

    /// <summary>「播放后自动最小化窗口」：只影响开始播放时是否缩窗，不需要刷新预览。</summary>
    private void AutoMinimize_Changed(object? sender, RoutedEventArgs e)
    {
        ScheduleSave();
    }

    /// <summary>设置里的开关：主界面状态卡是否常驻显示播放前自检（默认开）。</summary>
    private void ShowPreflight_Changed(object? sender, RoutedEventArgs e)
    {
        bool on = ChkShowPreflight.IsChecked == true;
        PreflightRow.IsVisible = on;
        if (_cfg != null) _cfg.ShowPreflight = on;
        ScheduleSave();
    }

    // ================= 皮肤（浅色 / 深色 / 自动） =================

    private void Theme_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;

        int mode = ThemeSwitch.Clamp(ThemeCombo.SelectedIndex);
        ThemeSwitch.Apply(mode);
        ApplyThemeColors();
        _settingsWindow?.RefreshThemeBrushes();   // 键位页的键帽是绑定到资源快照上的，要显式通知
        InsertLog($"皮肤：{ThemeSwitch.Names[mode]}");

        // 探针切皮肤只为拍图（见 SetThemeForDev）：不写设置，免得改掉开发机的档位
        if (_devThemeOverride) return;
        _cfg.ThemeMode = mode;
        ScheduleSave();
    }

    internal void SetThemeForDev(int mode)
    {
        _devThemeOverride = true;
        try { ThemeCombo.SelectedIndex = ThemeSwitch.Clamp(mode); }
        finally { _devThemeOverride = false; }
    }

    private void ApplyThemeColors()
    {
        if (Roll != null) Roll.InvalidateVisual();   // 卷帘是自绘的，颜色由它自己按主题取
        ApplyVoiceBrushes();                         // 未参与演奏的声轨用弱化色，随皮肤变
        RunPreflight();                              // 自检的 ✔ / ✘ / • 是代码赋色的
        UpdateMidiStatus();                          // 「实时演奏中」也用语义色
        if (!_busy) SetIdleHint();                   // 空闲时状态大字用弱化色
        // 提示行只有「有话说」和「全部有键」两种颜色，按换皮肤前的状态重涂一次
        LblWarn.Foreground = _warnIsOk ? OkBrush : WarningBrush;
    }

    private SettingsWindow? _settingsWindow;

    private void Settings_Click(object? sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>打开设置窗口（已开着就提到前面）。演奏中不换键位：这一轮的按键表已经算好。</summary>
    internal void OpenSettings()
    {
        if (_busy) return;
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        AdvancedStash.Child = null;

        var win = new SettingsWindow(_keymap, OnKeymapPath, RememberCurrentProfileSettings,
                                     ApplyProfileSettingsFromDialog);
        win.AttachAdvancedBody(AdvancedBody, this);
        _settingsWindow = win;
        win.Show(this);
        InsertLog("已打开设置：常规、键位、赞助三页都在这里。");
    }

    /// <summary>设置窗口关窗时回调：把常规页的内容还回主窗，并同步键位改动。</summary>
    internal void ReturnAdvancedBody(Control body)
    {
        if (body.Parent == null) AdvancedStash.Child = body;
        _settingsWindow = null;
        // 关窗兜底再同步一次：窗口里改过键位、音域或策略都要落到卷帘与状态行上
        SyncKeymapUi();
        ScheduleSave();
    }

    /// <summary>【开发用】走与按钮同一条链路打开设置窗口（快照用）。</summary>
    internal void OpenSettingsForDev() => OpenSettings();

    internal PlaylistWindow? OpenPlaylistForDev(string playlistName)
    {
        if (playlistName.Length > 0)
        {
            _playlist = PlaylistStore.Load(playlistName);
            RefreshPlaylistCard();
        }
        PlaylistOpen_Click(null, new RoutedEventArgs());

        string openRow = Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT_PLAYLIST_OPEN") ?? "";
        string pickCand = Environment.GetEnvironmentVariable("MIDIKEY_UI_SNAPSHOT_PLAYLIST_PICK") ?? "";
        if (_playlistWindow != null && int.TryParse(openRow, out int row))
            Dispatcher.UIThread.Post(() =>
            {
                _playlistWindow?.OpenTrackPickerForDev(row);
                if (int.TryParse(pickCand, out int cand)) _playlistWindow?.PickTrackForDev(row, cand);
            });

        return _playlistWindow;
    }

    /// <summary>【开发用】歌单窗口快照的场景准备：建一份歌单并放进 <paramref name="dir"/> 里的曲目。</summary>
    internal void BuildPlaylistForDev(string name, string dir)
    {
        if (name.Length == 0 || !System.IO.Directory.Exists(dir)) return;

        var list = Playlist.Create(name);
        var files = System.IO.Directory.EnumerateFiles(dir)
            .Where(f => System.IO.Path.GetExtension(f).ToLowerInvariant()
                        is ".mid" or ".midi" or ".kar" or ".rmi")
            .OrderBy(System.IO.Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        foreach (var f in files) list.Entries.Add(new PlaylistEntry { Path = f });
        if (!PlaylistStore.Save(list))
            InsertLog($"【开发】歌单存盘失败：{name}");
    }

    /// <summary>【开发用】当前的设置窗口。没开就是 null。</summary>
    internal SettingsWindow? SettingsWindowForDev => _settingsWindow;

    internal string LoadPlaylistEntryForDev(string spec)
    {
        // 末尾可以再带一个 |preview：拍「勾音轨时的卷帘预览」那一态，拍完不收
        bool keepPreview = spec.EndsWith("|preview", StringComparison.OrdinalIgnoreCase);
        if (keepPreview) spec = spec.Substring(0, spec.Length - "|preview".Length);

        // 「folder:<路径>」= 按文件夹曲目那条路载入（不是从歌单点开），
        // 用来验证「同一首歌从别的入口打开，勾选照样上身」。
        if (spec.StartsWith("folder:", StringComparison.OrdinalIgnoreCase))
        {
            string p = spec.Substring("folder:".Length);
            LoadMidiFile(p);
            int m2 = _tracks.Count(t => t.IsMix);
            return $"文件夹入口载入 {System.IO.Path.GetFileName(p)}：界面勾中 {m2} 行，"
                 + $"实际演奏 {ActiveRows().Count} 行，卷帘 {Roll.NoteCount} 个音";
        }

        var parts = spec.Split('|');
        string name = parts[0];
        int index = parts.Length > 1 && int.TryParse(parts[1], out int i) ? i : 0;

        _playlist = PlaylistStore.Load(name);
        if (_playlist == null) return $"歌单「{name}」读不到";
        if (index < 0 || index >= _playlist.Count) return $"歌单「{name}」没有第 {index + 1} 首";
        _cfg.ActivePlaylist = name;
        RefreshPlaylistCard();

        var entry = _playlist.Entries[index];
        string file = System.IO.Path.GetFileName(entry.Path);
        LoadPlaylistEntry(entry.Path, playNow: false);

        string extra = "";
        if (keepPreview)
        {
            PreviewTracks(entry.Path, entry.Tracks);
            extra = $"，预览卷帘 {Roll.NoteCount} 个音（只看模式={Roll.PreviewOnly}）";
        }

        int mix = _tracks.Count(t => t.IsMix);
        var active = ActiveRows();
        return $"歌单「{name}」第 {index + 1} 首 {file}：文件里勾了 {entry.Tracks.Count} 行，"
             + $"界面勾中 {mix} 行，实际演奏 {active.Count} 行，卷帘 {Roll.NoteCount} 个音{extra}";
    }

    /// <summary>设置窗口的回调：saved 非空表示刚保存的方案，message 是要记进日志的中文说明。</summary>
    private void OnKeymapPath(KeymapProfile? saved, string message)
    {
        if (saved != null)
        {
            _keymap = saved;
            KeymapProfile.Current = saved;
            if (_cfg != null) _cfg.KeymapName = saved.Name;
            SyncKeymapUi();   // 新方案立刻上身：方案名、卷帘颜色、音域提示、状态行都跟着走，不必等关窗
        }
        else
        {
            // 只带消息的调用（键位窗口的 Say）不带方案：这里用真源校正本地字段，别留陈旧值
            _keymap = KeymapProfile.Current ?? _keymap;
        }
        if (!string.IsNullOrEmpty(message)) InsertLog(message);
    }

    /// <summary>键位窗口换方案后读新方案的速度 / 移调 / 输入档位，返回展示说明；null = 没换。</summary>
    private string? ApplyProfileSettingsFromDialog(KeymapProfile profile)
    {
        if (profile == null) return null;
        ApplyProfileSettings(profile.Name);
        return $"速度 {SliderSpeed.Value:0}%、移调 {SliderTranspose.Value:0}、档位 {TimingCombo.SelectedIndex + 1}";
    }

    /// <summary>把当前方案同步到主界面：方案名小字、实时演奏、卷帘颜色与谱面。</summary>
    private void SyncKeymapUi()
    {
        _keymap = KeymapProfile.Current ?? _keymap;
        if (TxtKeymapName != null) TxtKeymapName.Text = _keymap.Name;
        KeymapProfile.Current = _keymap;
        if (TxtSeekNote != null) UpdateSeekNote();
        // 换键位方案不改音符与时序：保住卷帘视口与选择（与撤销/重做同一理由）
        RefreshPreview(keepView: true);
    }

    // ================= MIDI 设备接入（issue #4） =================

    /// <summary>重新扫描设备并尽量保持当前选择。热插拔后点「刷新」走这里。</summary>
    private void RefreshMidiDevices()
    {
        var names = MidiInputService.ListDevices();
        string want = MidiInputService.CurrentDeviceName;
        if (string.IsNullOrEmpty(want)) want = MidiDeviceCombo.SelectedItem as string ?? "";
        if (string.IsNullOrEmpty(want)) want = _cfg?.MidiDeviceName ?? "";

        var items = new List<string>();
        if (names.Count == 0) items.Add("（没有检测到 MIDI 设备）");
        else items.AddRange(names);

        MidiDeviceCombo.ItemsSource = items;
        int idx = items.IndexOf(want);
        MidiDeviceCombo.SelectedIndex = idx >= 0 ? idx : (names.Count > 0 ? 0 : 0);
        MidiDeviceCombo.IsEnabled = names.Count > 0;
        BtnMidiRefresh.IsEnabled = true;
        UpdateMidiStatus();
        UpdateMidiPanels();
    }

    private void MidiRefresh_Click(object? sender, RoutedEventArgs e)
    {
        bool wasLive = ChkMidiLive.IsChecked == true;
        if (wasLive) StopMidiLive();
        RefreshMidiDevices();
        if (wasLive) StartMidiLive();
        InsertLog($"[MIDI] 已重新扫描：找到 {MidiInputService.ListDevices().Count} 个输入设备。");
    }

    private void MidiDevice_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        ScheduleSave();
        if (ChkMidiLive.IsChecked != true) return;
        // 换了设备：先关旧的，再开新的
        StopMidiLive();
        StartMidiLive();
    }

    private void MidiLive_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        if (ChkMidiLive.IsChecked == true) StartMidiLive();
        else StopMidiLive();
        UpdateMidiPanels();
        ScheduleSave();
    }

    private void MidiOption_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_uiReady) return;
        _livePlay.BaseOctave = (int)Math.Round(SliderMidiOctave.Value);
        _livePlay.MinVelocity = (int)Math.Round(SliderMidiVelocity.Value);
        UpdateMidiLabels();
        ScheduleSave();
    }

    private void MidiAutoFit_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        _livePlay.AutoFit = ChkMidiAutoFit.IsChecked == true;
        ScheduleSave();
    }

    private void UpdateMidiLabels()
    {
        if (TxtMidiOctave != null)
            TxtMidiOctave.Text = Music.SolfegeName((_livePlay.BaseOctave + 1) * 12);
        if (TxtMidiVelocity != null)
            TxtMidiVelocity.Text = _livePlay.MinVelocity <= 1 ? "1" : _livePlay.MinVelocity.ToString();
    }

    private void UpdateMidiStatus()
    {
        if (TxtMidiStatus == null) return;
        bool on = ChkMidiLive.IsChecked == true;
        if (on && MidiInputService.IsListening)
        {
            TxtMidiStatus.Text = "实时演奏中";
            TxtMidiStatus.Foreground = OkBrush;
        }
        else if (on)
        {
            TxtMidiStatus.Text = "启动中…";
            TxtMidiStatus.Foreground = NeutralBrush;
        }
        else
        {
            TxtMidiStatus.Text = MidiInputService.IsListening ? "未启用" : "已停止";
            TxtMidiStatus.Foreground = NeutralBrush;
        }
    }

    private void UpdateMidiPanels()
    {
        bool hasDevice = MidiInputService.ListDevices().Count > 0;
        if (PanelMidiControls != null) PanelMidiControls.IsVisible = hasDevice;
        if (TxtMidiNoDevice != null) TxtMidiNoDevice.IsVisible = !hasDevice;
        if (PanelMidiOptions != null) PanelMidiOptions.IsVisible = hasDevice && ChkMidiLive.IsChecked == true;
    }

    private void StartMidiLive()
    {
        string name = MidiDeviceCombo.SelectedItem as string ?? "";
        if (string.IsNullOrEmpty(name) || name.StartsWith('（'))
        {
            InsertLog("[MIDI] 没有可用设备。接上设备后点「刷新」。");
            ChkMidiLive.IsChecked = false;
            UpdateMidiStatus();
            return;
        }

        _livePlay.Timing = InputTiming.FromIndex(TimingCombo.SelectedIndex);
        _livePlay.BaseOctave = (int)Math.Round(SliderMidiOctave.Value);
        _livePlay.MinVelocity = (int)Math.Round(SliderMidiVelocity.Value);
        _livePlay.AutoFit = ChkMidiAutoFit.IsChecked == true;
        _livePlay.Start();
        UpdateMidiStatus();

        if (_midiStarting) return;
        _midiStarting = true;
        _ = MidiInputService.StartAsync(name).ContinueWith(t =>
        {
            bool ok = t.IsCompletedSuccessfully && t.Result;
            UiPost(() =>
            {
                _midiStarting = false;
                if (ok)
                {
                    var range = LivePlayback.PlayableRange(_livePlay.BaseOctave);
                    InsertLog($"[MIDI] 已接入设备「{name}」。按键会直接发送到目标程序，音符范围 "
                              + $"{Music.SolfegeRange(range.Lo, range.Hi)}。");
                }
                else
                {
                    InsertLog($"[MIDI] 打开设备「{name}」失败，实时演奏已关闭。");
                    ChkMidiLive.IsChecked = false;
                    _livePlay.Stop();
                }
                UpdateMidiStatus();
            });
        }, TaskScheduler.Default);
    }

    private void StopMidiLive()
    {
        bool wasListening = MidiInputService.IsListening;
        MidiInputService.Stop();
        _livePlay.Stop();
        UpdateMidiStatus();
        UpdateMidiPanels();
        if (!wasListening) return;
        InsertLog($"[MIDI] 实时演奏已停止（本次收到 {_livePlay.NoteOnCount} 个音，"
                  + $"重复丢弃 {_livePlay.DuplicateCount}，"
                  + $"没键可弹 {_livePlay.OutOfRangeCount}，顶音 {_livePlay.StolenCount}，"
                  + $"太短补足 {_livePlay.TooShortCount}）。");
    }

    /// <summary>设备事件在设备线程上触发：只做转发，界面更新交给 <see cref="OnMidiObserved"/>。</summary>
    private void OnMidiNote(int pitch, int velocity, bool down)
    {
        if (down) _livePlay.NoteOn(pitch, velocity);
        else _livePlay.NoteOff(pitch);
    }

    /// <summary>实时演奏时把最近一个音显示在状态行上，方便确认设备真的通了。</summary>
    private void OnMidiObserved(LiveMapping map, int pitch, int velocity)
    {
        string text = map.Playable
            ? $"[MIDI] {Music.SolfegeName(pitch)} → {KeyLabelOf(map)}"
            : $"[MIDI] {Music.SolfegeName(pitch)} → 超出音域";
        // 本函数跑在 MIDI 设备线程上：Avalonia 对象非线程安全，IsChecked 必须到 UI 线程再读
        UiPost(() =>
        {
            if (ChkMidiLive.IsChecked != true) return;
            UpdateMidiStatus(text);
        });
    }

    private void UpdateMidiStatus(string note)
    {
        if (TxtMidiStatus == null) return;
        TxtMidiStatus.Text = MidiInputService.IsListening ? note : "未启用";
        TxtMidiStatus.Foreground = MidiInputService.IsListening ? OkBrush : NeutralBrush;
    }

    /// <summary>实时演奏的提示文字：按当前键位方案的键名显示，鼠标键用中文。</summary>
    private static string KeyLabelOf(LiveMapping map)
    {
        string key = map.KeyLabel;
        if (string.IsNullOrEmpty(key)) key = "（无按键）";
        var parts = new List<string> { key };
        if (map.Low) parts.Add("+降八度键");
        if (map.High) parts.Add("+升八度键");
        if (map.Sharp) parts.Add("+升半音键");
        if (map.Flat) parts.Add("+降半音键");
        return string.Join("", parts);
    }

    /// <summary>刷新需选中声轨才能用的按钮（一键移调、导出），播放中也能导出。</summary>
    private void UpdateActionButtons()
    {
        bool hasRows = ActiveRows().Count > 0;
        BtnAutoTranspose.IsEnabled = hasRows;
        BtnNaturalTranspose.IsEnabled = hasRows;
        BtnExport.IsEnabled = hasRows;
    }

    private void RefreshPreview(bool pushToRoll = true, bool keepView = false)
    {
        UpdateActionButtons();

        // 空状态引导：没有轨道时显示提示，别留一大片空白
        if (EmptyHint != null) EmptyHint.IsVisible = _tracks.Count == 0;

        var okColor = OkBrush;
        var warnColor = WarningBrush;

        var rows = ActiveRows();
        if (rows.Count == 0 || _parsed == null)
        {
            LblMelody.Text = _parsed == null
                ? "打开 MIDI 文件并单击一行作为主旋律（可勾选“合”多声部一起演奏）。"
                : "已载入 —— 单击一行作为主旋律；勾选“合”可按 1、2、3 优先级合奏。";
            LblWarn.Text = "";
            LblWarn.Foreground = warnColor;
            _warnIsOk = false;
            _previewNotes = new List<MappedNote>();
            _previewSeconds = 0;

            if (_parsed == null) ResetSeekUi();
            else InsertLog("当前没有任何声部被勾选，卷帘保留上一次的内容。勾选一行即可恢复。");

            UpdateEditUi();
            UpdateTransportUi();
            return;
        }

        var raw = GetActiveRawNotes();
        var m = raw.Count == 0 ? new MappingResult() : MapAt(CurrentTranspose);
        _lastMapping = m;   // LblWarn 的「跳过明细」按这份结果显示
        // 声轨颜色：左侧列表的文字色与卷帘音符色用同一个序号
        var voiceMap = BuildVoiceMap(raw);
        ApplyVoiceBrushes();

        // 载入后即可定位：进度条与卷帘按谱面时间摆好（不必先播放）
        _previewNotes = m.Notes;
        double totalSec = PreviewTotalSeconds;
        _previewSeconds = Math.Clamp(_previewSeconds, 0, totalSec);
        // 卷帘轴上留 2% 余量，末尾才好双击加音
        _noteCount = raw.Count;
        int rollTranspose = CurrentTranspose;
        var inRangePitches = NoteMapper.Playable(m.Notes)
                                    .Select(n => n.Pitch - rollTranspose)
                                    .Distinct().ToList();
        if (pushToRoll)
        {
            Roll.SetTempo(_parsed.SecondsPerBeat, _parsed.BeatsPerBar);
            Roll.DefaultNoteSeconds = MedianNoteLength();
            Roll.SetVoiceColors(voiceMap);
            Roll.SetNotes(raw, inRangePitches, totalSec * 1.02 + 0.3, preserveView: keepView);
            Roll.SetTrimInfo(_removedLeadSec);
            Roll.SetPosition(_previewSeconds);
            OnRollViewChanged();
        }
        else
        {
            // 编辑提交：只同步进度条、音高颜色与读数，卷帘保持自己的视口与选择
            Roll.SetInRangePitches(inRangePitches);
            Roll.SetVoiceColors(voiceMap);
            SliderProgress.IsEnabled = m.Notes.Count > 0;
            UpdateSeekNote();
        }
        SliderProgress.Maximum = Math.Max(0.1, totalSec);
        SliderProgress.Value = _previewSeconds;
        SliderProgress.IsEnabled = m.Notes.Count > 0;
        TxtTime.Text = $"{_previewSeconds:F1} / {totalSec:F1} s";
        UpdateSeekNote();
        UpdateEditUi();

        if (rows.Count > 1)
        {
            string order = string.Join(" > ", rows.Select(r => $"{r.MixRank}「{r.Name}」"));
            LblMelody.Text = $"合奏 {rows.Count} 个声部（优先级 {order}）：冲突时先演奏编号小的";
        }
        else
        {
            var cand = rows[0].Candidate;
            LblMelody.Text = $"主旋律：轨道 {cand.TrackIndex + 1} / 声道 {cand.Channel + 1}「{cand.Name}」" +
                             $"（共 {cand.NoteCount} 音）";
        }

        if (m.InRangeCount == 0)
        {
            LblWarn.Text = m.Notes.Count == 0
                ? "该轨道/声道没有音符，请换一行。"
                : "这些音在键位上都没有对应的键。请换个方案，或把「移调」调到 0 附近再试。";
            LblWarn.Foreground = warnColor;
            _warnIsOk = false;
        }
        else if (m.SkipCount > 0)
        {
            LblWarn.Text = $"有 {m.SkipCount} 个音没有对应的键，会跳过不弹（可用「移调」或换方案调整）。";
            LblWarn.Foreground = warnColor;
            _warnIsOk = false;
        }
        else
        {
            LblWarn.Text = "全部音都有对应的键，可直接演奏。";
            LblWarn.Foreground = okColor;
            _warnIsOk = true;
        }
        UpdateTransportUi();
    }

    private void OnEngineFinished()
    {
        var eng = _engine;
        _engine = null;
        if (eng != null)
        {
            _uiTimer?.Stop();
            _uiTimer = null;
            InsertLog("播放结束。");
            InsertLog(eng.Probe.Summary());
        }
        ResetUi();

        if (eng != null) AdvanceAfterFinish();
    }

    // ================= 演奏模式与连播 =================

    /// <summary>当前演奏模式。存在设置里（AppConfig.PlayModeIndex），按钮上显示的也是它。</summary>
    private PlayMode CurrentPlayMode => PlayModeNames.Clamp(_cfg.PlayModeIndex);

    /// <summary>模式列表里点了一项，按 Tag 里的模式号切换。</summary>
    private void PlayModePick_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not string tag) return;
        if (!int.TryParse(tag, out int index)) return;
        SetPlayMode(PlayModeNames.Clamp(index));
    }

    private void SetPlayMode(PlayMode mode)
    {
        if (mode == CurrentPlayMode) return;

        _cfg.PlayModeIndex = (int)mode;
        SaveSettings();
        ApplyPlayModeUi();
        InsertLog($"演奏模式：{PlayModeNames.All[(int)mode]}。" + mode switch
        {
            PlayMode.Off => "放完这一首就停。",
            PlayMode.Sequential => "一首放完接着放下一首，到列表末尾就停。",
            PlayMode.ListLoop => "一首放完接着放下一首，到列表末尾回到第一首，一直放。",
            PlayMode.Shuffle => "一首放完随机挑列表里的另一首。",
            _ => "一直重播当前这一首。",
        });
    }

    /// <summary>把当前模式写到按钮与模式列表的勾选状态上。</summary>
    private void ApplyPlayModeUi()
    {
        if (BtnPlayMode != null) BtnPlayMode.Content = PlayModeNames.ButtonText(CurrentPlayMode);

        var items = new[] { ModeItemSequential, ModeItemListLoop, ModeItemShuffle, ModeItemRepeatOne, ModeItemOff };
        foreach (var item in items)
        {
            if (item == null) continue;
            item.IsChecked = int.TryParse(item.Tag as string, out int i) && i == (int)CurrentPlayMode;
        }
    }

    private void AdvanceAfterFinish()
    {
        // 关闭模式不接下一首；单曲循环由引擎自己重播。两者都不走连播。
        if (CurrentPlayMode is PlayMode.Off or PlayMode.RepeatOne) return;

        // 没打开任何列表就没什么可前进的，停在原地。
        bool usePlaylist = _sourceIsPlaylist && _playlist is { Count: > 0 };
        bool useFolder = !usePlaylist && _folderFiles.Count > 0;
        if (!usePlaylist && !useFolder) return;

        int count = usePlaylist ? _playlist!.Count : _folderFiles.Count;
        int current = usePlaylist ? CurrentPlaylistIndex() : CurrentFolderIndex();

        var decision = PlayAdvance.AfterFinish(CurrentPlayMode, count, current, null, _shuffleRng);
        if (!decision.ShouldPlay)
        {
            if (decision.Kind == PlayAdvance.Move.Stop && decision.Reason.Length > 0)
                InsertLog($"连播结束：{decision.Reason}。");
            _consecutiveFailures = 0;
            return;
        }

        AdvanceTo(usePlaylist, decision.Index);
    }

    private void AdvanceTo(bool usePlaylist, int index)
    {
        int count = usePlaylist ? _playlist?.Count ?? 0 : _folderFiles.Count;
        if (index < 0 || index >= count) return;

        string path;
        PlaylistEntry? entry = null;
        if (usePlaylist)
        {
            _playlistPos = index;
            entry = _playlist!.Entries[index];
            path = entry.Path;
        }
        else
        {
            path = _folderFiles[index];
        }

        string what = System.IO.Path.GetFileName(path);
        if (!System.IO.File.Exists(path))
        {
            if (!usePlaylist)
            {
                InsertLog($"文件已不在：{what}");
                ScanMidiFolder(_folderPath);
            }
            AdvanceSkippingFailures(usePlaylist, count, index, "文件已不在");
            return;
        }

        bool loaded;
        _advancing = true;
        try
        {
            loaded = LoadMidiFile(path);
            if (loaded)
            {
                if (entry != null) SelectTrackForEntry(entry);
                else AutoSelectTrack();
            }
        }
        finally { _advancing = false; }

        if (!loaded)
        {
            AdvanceSkippingFailures(usePlaylist, count, index, "载入失败");
            return;
        }

        InsertLog($"连播 {index + 1}/{count}：{what}");
        _nextSongLabel = System.IO.Path.GetFileNameWithoutExtension(what);
        UiPost(RefreshPlaylistCard);

        int here = index;
        RequestPlay(onFail: why => AdvanceSkippingFailures(usePlaylist, count, here, why));
    }

    private bool AdvanceSkippingFailures(bool usePlaylist, int count, int current, string why)
    {
        _consecutiveFailures++;
        if (!PlayAdvance.ShouldKeepSkipping(_consecutiveFailures))
        {
            int failed = _consecutiveFailures;
            _consecutiveFailures = 0;
            InsertLog($"连播已停：连续 {failed} 首无法演奏（最后一次：{why}）。");
            return false;
        }

        InsertLog($"这一首放不出来（{why}），跳过它继续下一首。已连跳 {_consecutiveFailures} 首。");
        int next = PlayAdvance.NextAfterFailure(CurrentPlayMode, count, current, _shuffleRng);
        if (next < 0)
        {
            int failed = _consecutiveFailures;
            _consecutiveFailures = 0;
            InsertLog($"连播已停：列表里没有别的可放了（最后一次：{why}）。");
            return false;
        }

        Dispatcher.UIThread.Post(() => AdvanceTo(usePlaylist, next));
        return true;
    }

    /// <summary>当前这一首在文件夹列表里的下标；不在列表里返回 -1。</summary>
    private int CurrentFolderIndex()
    {
        string cur = _parsed?.FilePath ?? "";
        if (cur.Length == 0) return -1;
        return _folderFiles.FindIndex(f => string.Equals(f, cur, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>当前这一首在歌单里的下标。先认记下来的位置，再按路径找。</summary>
    private int CurrentPlaylistIndex()
    {
        if (_playlist == null) return -1;
        if (_playlistPos >= 0 && _playlistPos < _playlist.Count &&
            string.Equals(_playlist.Entries[_playlistPos].Path, _parsed?.FilePath ?? "",
                          StringComparison.OrdinalIgnoreCase))
            return _playlistPos;

        string cur = _parsed?.FilePath ?? "";
        if (cur.Length == 0) return -1;
        return _playlist.Entries.FindIndex(e =>
            string.Equals(e.Path, cur, StringComparison.OrdinalIgnoreCase));
    }

    private void AutoSelectTrack()
    {
        if (_parsed == null || _tracks.Count == 0) return;

        var pick = PlaylistModel.Pick(_parsed.Candidates, null, _lastPlayedRole);
        if (pick.Candidate is not { } cand) return;

        int idx = _parsed.Candidates.IndexOf(cand);
        if (idx < 0 || idx >= _tracks.Count) return;

        _selected = _tracks[idx];
        _mixOrder.Clear();
        for (int i = 0; i < _tracks.Count; i++) _tracks[i].IsMain = ReferenceEquals(_tracks[i], _selected);
        if (TrackList != null) TrackList.SelectedIndex = idx;
        RefreshPreview();
        InsertLog($"自动选了主旋律轨：{pick.Reason}。想换就点左侧那一行。");
    }

    private void SelectTrackForEntry(PlaylistEntry entry)
    {
        if (_parsed == null || _tracks.Count == 0) return;

        // 清单空 = 全选
        if (entry.Tracks.Count == 0)
        {
            SelectRowsForPlaylist(_tracks.ToList(), null);
            InsertLog("这份歌单没给这一首勾过音轨，按全部音轨弹（想挑就点曲目右边的按钮）。");
            return;
        }

        // 按轨道号加声道找行；找不到的行列在 missing 里，写一行日志
        var wanted = new List<TrackRowVM>();
        var missing = new List<TrackRef>();
        foreach (var t in entry.Tracks)
        {
            var row = _tracks.FirstOrDefault(r =>
                r.Candidate.TrackIndex == t.TrackIndex && r.Candidate.Channel == t.Channel);
            if (row != null) wanted.Add(row);
            else missing.Add(t);
        }

        if (wanted.Count == 0)
        {
            // 勾的行一行都不在这份文件里：退回自动挑一行，让连播还能开弹
            AutoSelectTrack();
            InsertLog("歌单勾的那几行都不在这份文件里，改成自动挑一行。");
            return;
        }

        SelectRowsForPlaylist(wanted, entry.Tracks);
        InsertLog(missing.Count == 0
            ? $"按歌单勾的 {wanted.Count} 行来弹。"
            : $"按歌单勾的 {wanted.Count} 行来弹；另有 {missing.Count} 行不在这份文件里。");
    }

    private bool ApplyPlaylistTracksFor(string path)
    {
        if (path.Length == 0) return false;
        var list = _playlist ??= (_cfg.ActivePlaylist ?? "").Length > 0
            ? PlaylistStore.Load(_cfg.ActivePlaylist)
            : null;
        if (list == null) return false;

        var entry = list.Entries.FirstOrDefault(x =>
            string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
        if (entry == null || entry.Tracks.Count == 0) return false;

        SelectTrackForEntry(entry);
        return true;
    }

    private void SelectRowsForPlaylist(List<TrackRowVM> rows, List<TrackRef>? picks)
    {
        foreach (var r in _tracks) r.IsMix = false;
        foreach (var r in rows) r.IsMix = true;

        _mixOrder.Clear();
        foreach (var r in rows) _mixOrder.Add(r);

        // 至少留一行：一行都不勾，ActiveRows 会返回空，RequestPlay 静默不弹
        _selected = rows.Count > 0 ? rows[0] : (_tracks.Count > 0 ? _tracks[0] : null);
        for (int i = 0; i < _tracks.Count; i++)
            _tracks[i].IsMain = ReferenceEquals(_tracks[i], _selected);
        if (TrackList != null && _selected != null) TrackList.SelectedIndex = _tracks.IndexOf(_selected);

        SyncMixOrder();
        // SyncMixOrder 在忙的时候只更新颜色不刷谱面，这里补一次
        RefreshPreview();
    }

    private TrackRole? _lastPlayedRole
    {
        get
        {
            var rows = _mixOrder.Where(r => r.IsMix).ToList();
            if (rows.Count > 0) return rows[0].Candidate.Role;
            return _selected?.Candidate.Role;
        }
    }

    // ================= 歌单卡 =================

    /// <summary>恢复上次选中的歌单（启动时调一次）。没选过或文件没了就当没选过。</summary>
    private void RestorePlaylistFromConfig()
    {
        string name = _cfg.ActivePlaylist ?? "";
        if (name.Length == 0) { _playlist = null; RefreshPlaylistCard(); return; }

        _playlist = PlaylistStore.Load(name);
        if (_playlist == null)
        {
            InsertLog($"上次那份歌单「{name}」找不到了，歌单卡先收起。");
            _cfg.ActivePlaylist = "";
        }
        RefreshPlaylistCard();
    }

    private void PlaylistOpen_Click(object? sender, RoutedEventArgs e)
    {
        if (_playlistWindow != null)
        {
            _playlistWindow.Activate();
            return;
        }

        var win = new PlaylistWindow { ActiveName = _playlist?.Name ?? "" };
        win.EntryActivated += path =>
        {
            // 双击歌单里的一行：载入它，并在演奏中接着弹（和切歌一个规矩）
            if (HasUnexportedEdits)
            {
                InsertLog("有未导出的卷帘改动：先点「导出 MIDI…」保存，再换歌。");
                return;
            }
            LoadPlaylistEntry(path, playNow: _engine is { IsRunning: true });
            win.RefreshPlayingMark();
        };
        win.Changed += () =>
        {
            ReloadPlaylistFromDisk();
            win.RefreshPlayingMark();
        };
        // 左栏选了哪份歌单，立刻记成当前歌单：不记的话重开程序这张卡是空的
        win.ActiveChanged += name => SetActivePlaylist(name);
        // 勾音轨时把勾的行画到卷帘上：先看音符再决定勾哪一轨
        win.PreviewTracks += (path, picks) => PreviewTracks(path, picks);
        win.PreviewEnded += EndPreviewTracks;
        win.Closed += (_, _) =>
        {
            _playlistWindow = null;
            // 关窗时再同步一次：新建 / 改名 / 删除之后选中的那份才是该记住的
            if (win.ActiveNameNow.Length > 0) SetActivePlaylist(win.ActiveNameNow);
        };

        _playlistWindow = win;
        win.SelectInitial();
        win.Show(this);
    }

    /// <summary>歌单窗口改了内容：重新读回来，并刷新左栏那张卡。</summary>
    private void ReloadPlaylistFromDisk()
    {
        string name = _cfg.ActivePlaylist ?? "";
        if (name.Length == 0) { RefreshPlaylistCard(); return; }
        _playlist = PlaylistStore.Load(name);
        if (_playlist == null) _cfg.ActivePlaylist = "";
        RefreshPlaylistCard();
    }

    private void SetActivePlaylist(string name)
    {
        if (string.Equals(_cfg.ActivePlaylist, name, StringComparison.Ordinal)) return;

        _cfg.ActivePlaylist = name;
        _playlist = PlaylistStore.Load(name);
        SaveSettings();
        RefreshPlaylistCard();
        if (_playlist != null)
            InsertLog($"当前歌单：{name}（{_playlist.Count} 首），下次启动自动恢复。");
    }

    private void PlaylistClose_Click(object? sender, RoutedEventArgs e)
    {
        _playlist = null;
        _playlistPos = -1;
        _cfg.ActivePlaylist = "";
        SaveSettings();
        RefreshPlaylistCard();
        InsertLog("已收起歌单卡。歌单文件没删，下次在歌单窗口里还能选。");
    }

    /// <summary>点歌单卡里的一首：载入它。演奏中（含暂停中）接着弹，空闲时只载入。</summary>
    private void PlaylistList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_playlistSyncing) return;   // 铺界面时的程序化选中，不是用户点的
        if (PlaylistList?.SelectedItem is not ListBoxItem it) return;
        if (it.Tag is not int idx || _playlist == null) return;
        if (idx < 0 || idx >= _playlist.Count) return;

        string path = _playlist.Entries[idx].Path;
        bool wasPlaying = _engine is { IsRunning: true };
        Dispatcher.UIThread.Post(() => LoadPlaylistEntry(path, playNow: wasPlaying));
    }

    /// <summary>载入歌单里的一首，并把连播来源标成歌单。</summary>
    private void LoadPlaylistEntry(string path, bool playNow)
    {
        if (_playlist == null)
        {
            LoadMidiFile(path);
            return;
        }

        _playlistPos = _playlist.Entries.FindIndex(x =>
            string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
        if (_playlistPos < 0) return;

        var entry = _playlist.Entries[_playlistPos];
        if (!System.IO.File.Exists(entry.Path))
        {
            InsertLog($"「{System.IO.Path.GetFileName(entry.Path)}」已经不在磁盘上了，换一首或把它移出歌单。");
            RefreshPlaylistCard();
            return;
        }

        _sourceIsPlaylist = true;
        bool loaded = LoadMidiFile(entry.Path);   // 勾选由 LoadMidiFile 里的 ApplyPlaylistTracksFor 统一应用

        RefreshPlaylistCard();
        if (playNow && loaded) RequestPlay(skipCountdown: true);
    }

    private void RefreshPlaylistCard()
    {
        if (PlaylistCard == null || PlaylistList == null) return;   // 构造早期的防御：控件还没建好

        PlaylistCard.IsVisible = true;
        bool has = _playlist != null;
        if (!has)
        {
            TxtPlaylistName.Text = "歌单";
            TxtPlaylistCount.Text = "";
            _playlistSyncing = true;
            try
            {
                PlaylistList.SelectedItem = null;
                PlaylistList.Items.Clear();
                if (TxtPlaylistEmpty != null) TxtPlaylistEmpty.IsVisible = true;
            }
            finally { _playlistSyncing = false; }
            return;
        }

        TxtPlaylistName.Text = _playlist!.Name;
        TxtPlaylistCount.Text = _playlist.Count > 0 ? $"{_playlist.Count} 首" : "";

        _playlistSyncing = true;
        try
        {
            PlaylistList.SelectedItem = null;
            PlaylistList.Items.Clear();

            int total = _playlist.Count;
            int shown = Math.Min(total, PlaylistMenuMax);
            string cur = _parsed?.FilePath ?? "";

            for (int i = 0; i < shown; i++)
            {
                var entry = _playlist.Entries[i];
                string file = System.IO.Path.GetFileName(entry.Path);
                bool missing = !System.IO.File.Exists(entry.Path);
                bool playing = cur.Length > 0 &&
                               string.Equals(entry.Path, cur, StringComparison.OrdinalIgnoreCase);

                string prefix = playing ? "▶ " : missing ? "⚠ " : "";
                var item = new ListBoxItem
                {
                    Content = new TextBlock
                    {
                        Text = $"{prefix}{i + 1}. {file}",
                        FontSize = 12.5,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                    Tag = i,
                    Opacity = missing ? 0.55 : 1.0,
                };
                Avalonia.Controls.ToolTip.SetTip(item,
                    entry.Path + (missing ? "\n文件已不在：连播时会跳过这一首" : ""));
                PlaylistList.Items.Add(item);
            }

            if (total > shown)
            {
                PlaylistList.Items.Add(new ListBoxItem
                {
                    Content = new TextBlock
                    {
                        Text = $"（还有 {total - shown} 首，点「管理…」看全部）",
                        FontSize = 12.5,
                        Opacity = 0.7,
                    },
                    IsEnabled = false,
                });
            }

            if (TxtPlaylistEmpty != null)
            {
                // 歌单是空的：卡里别写着「还没有歌单」—— 歌单在，只是没歌，下一步不一样
                TxtPlaylistEmpty.Text = "点「管理…」加入曲目。";
                TxtPlaylistEmpty.IsVisible = total == 0;
            }

            // 把正在演奏的那一首标成选中
            int pos = CurrentPlaylistIndex();
            if (pos >= 0 && pos < shown) PlaylistList.SelectedIndex = pos;
        }
        finally { _playlistSyncing = false; }

        if (_playlistWindow != null)
        {
            _playlistWindow.PlayingPath = _parsed?.FilePath ?? "";
        }
    }

    // ================= 播放 =================

    private void BtnPlay_Click(object? sender, RoutedEventArgs e)
    {
        // ▶ 播放 / ⏸ 暂停 二合一：播放中点它=暂停，暂停中点它=继续
        if (_engine is { IsRunning: true })
        {
            TogglePause();
            return;
        }
        RequestPlay();
    }

    private void RequestPlay(bool skipCountdown = false, Action<string>? onFail = null)
    {
        if (_busy || ActiveRows().Count == 0)
        {
            onFail?.Invoke(ActiveRows().Count == 0 ? "没有选中任何一行" : "上一次播放还没收尾");
            return;
        }

        // 强制更新期间不给开弹：热键、按钮、托盘、切歌热键四条入口最后都走这里
        if (_forcedUpdateOn)
        {
            InsertLog("必须先更新到最新版本，更新完就能照常用。");
            ForcedUpdateOverlay.IsVisible = true;
            onFail?.Invoke("必须先更新版本");
            return;
        }

        if (!Input.InputSender.EnsureBackend(out string backendErr, out string? backendWarn))
        {
            InsertLog(backendErr.Replace("\n", ""));
            onFail?.Invoke("输入后端不可用");
            return;
        }
        if (backendWarn != null) InsertLog(backendWarn);   // 这次补初始化发现 G HUB 没在运行

        // 试听与演奏不能同时进行（反向检查在 StartPreviewAudio）：先停试听再开弹
        if (_previewOn) StopPreviewAudio();

        var map = BuildMapping();
        if (map.InRangeCount == 0)
        {
            InsertLog("没有可演奏的音，无法播放。请调整“移调”或换一行。");
            onFail?.Invoke("键位方案里一个音都对应不上");
            return;
        }
        _playNotes = NoteMapper.Playable(map.Notes);
        _consecutiveFailures = 0;   // 走到这里说明这一首真的开弹了，失败计数清零
        _nextSongLabel = "";        // 非连播的播放路径：别把上一次的「下一首」留在倒计时上

        SetBusy(true);
        _overlayMuted = false;   // 新一次播放：✕ 的「本次不显示」作废，浮窗该出还出
        // 切歌热键已经守在目标程序里，不走倒计时，直接开弹
        int cd = skipCountdown ? 0 : SelectedCountdownSeconds;
        if (cd > 0)
        {
            _countdownLeft = cd;
            UpdateCountdownText();
            string nextLabel = _nextSongLabel.Length > 0 ? $"下一首：{_nextSongLabel}" : "";
            ShowOverlayCountdown(cd, nextLabel);
            InsertLog($"{cd} 秒后开始——请切到目标窗口并装备乐器…");
            _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _countdownTimer.Tick += (_, _) =>
            {
                _countdownLeft--;
                if (_countdownLeft <= 0)
                {
                    _countdownTimer!.Stop();
                    _countdownTimer = null;
                    StartPlayback();
                }
                else
                {
                    UpdateCountdownText();
                    ShowOverlayCountdown(_countdownLeft);
                }
            };
            _countdownTimer.Start();
        }
        else
        {
            StartPlayback();
        }
    }

    private int SelectedCountdownSeconds
    {
        get
        {
            int i = CountdownCombo.SelectedIndex;
            if (i < 0 || i >= CountdownOptions.Length) return 3;
            return CountdownOptions[i];
        }
    }

    private void UpdateCountdownText()
    {
        LblStatus.Foreground = FailBrush;
        LblStatus.FontSize = ResourceFontSize("FontCountdown", 38);   // 切目标程序前的最后几秒必须一眼看到
        LblStatus.Text = $"{_countdownLeft} 秒后开始 —— 请切到目标程序并装备乐器（F6 可取消）";
        SetCountdownChrome(true);
    }

    private void SetCountdownChrome(bool on)
    {
        string key = on ? "BrushCountdownChrome" : "BrushCanvas";
        if (ThemeSwitch.BrushOf(key) is { } b) Background = b;
    }

    private void StartPlayback()
    {
        if (_playNotes.Count == 0) return;

        if (_removedLeadSec > 0.05)
            InsertLog($"已去除开头空拍 {_removedLeadSec:F1} 秒，旋律从第 0 秒开始。");

        // 和弦开关已移除：谱面固定为「演奏 MIDI 里的全部音」（见 ComputeAutoNotes）。
        var engine = new PlaybackEngine();
        _engine = engine;
        engine.Log += s => UiPost(() => InsertLog(s));
        engine.Finished += () => UiPost(OnEngineFinished);

        double speed = SliderSpeed.Value / 100.0;
        bool loop = CurrentPlayMode == PlayMode.RepeatOne;
        _nextSongLabel = "";   // 这一首已经开弹了，倒计时那句「下一首」用完就清

        if (!Input.InputSender.IsSupported)
            InsertLog("（当前平台不支持输入模拟，仅流程演示）");

        // 播放前可能已把进度条或卷帘拖到某个位置，从那里开始
        double startFrac = SliderProgress.Maximum > 0
            ? Math.Clamp(SliderProgress.Value / SliderProgress.Maximum, 0, 1) : 0;

        _gameHwnd = IntPtr.Zero;
        IntPtr fgAtStart = Input.InputSender.ForegroundWindow;
        if (fgAtStart != IntPtr.Zero && fgAtStart != SelfHwnd) _gameHwnd = fgAtStart;

        if (Roll != null) Roll.SetPlayheadScale(1.0);

        engine.Timing = InputTiming.FromIndex(TimingCombo.SelectedIndex);
        engine.Play(_playNotes, speed, FixedLeadMs, loop);
        SliderProgress.Maximum = Math.Max(0.1, engine.TotalSeconds);
        if (startFrac > 0.0005)
        {
            engine.SeekFraction(startFrac);
            SliderProgress.Value = startFrac * SliderProgress.Maximum;
            InsertLog($"从 {startFrac * engine.TotalSeconds:F1} 秒开始播放。");
        }
        // 自检把“按键发不进目标程序”的常见原因指出来，省得用户逐个猜
        RunPreflight();

        string fgTitle = InputSender.ForegroundWindowTitle;
        InsertLog($"开始演奏；前台窗口：{(string.IsNullOrEmpty(fgTitle) ? "（读不到，可能未切到目标程序）" : fgTitle)}");
        LblStatus.Foreground = OkBrush;
        LblStatus.FontSize = ResourceFontSize("FontDisplay", 22);
        SetCountdownChrome(false);
        LblStatus.Text = "演奏中…";

        // 是否自动最小化由选项决定（副屏看进度时可保持窗口）
        if (ChkAutoMinimize.IsChecked == true)
        {
            if (WindowState != WindowState.Minimized)
                WindowState = WindowState.Minimized;
        }
        else
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;   // 关掉该选项时，若本来缩着就恢复出来
            InsertLog("（未自动最小化：请点一下目标窗口让它获得焦点，否则按键发到本窗口）");
        }

        UpdateTransportUi();
        SliderProgress.IsEnabled = true;

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _uiTimer.Tick += (_, _) =>
        {
            var eng = _engine;
            if (eng == null || !eng.IsRunning) return;

            if (!_seeking)   // 拖动进度条或卷帘时不要覆盖用户位置
            {
                SliderProgress.Value = Math.Min(eng.ElapsedSeconds, SliderProgress.Maximum);
                TxtTime.Text = eng.LoopCount > 0
                    ? $"{eng.ElapsedSeconds:F1} / {eng.TotalSeconds:F1} s（第 {eng.LoopCount + 1} 遍）"
                    : $"{eng.ElapsedSeconds:F1} / {eng.TotalSeconds:F1} s";
                Roll?.SetPosition(eng.ElapsedSeconds);   // 实参是音乐时间，与进度条、卷帘同一时间轴（A06）
                UpdateSeekNote();
            }
            if (!string.IsNullOrEmpty(eng.CurrentNote))
            {
                LblStatus.Foreground = OkBrush;
                LblStatus.Text = eng.CurrentNote;
            }
            PushOverlayThrottled(eng);   // 悬浮窗跟随（节流：进度变 ≥0.15s 或状态变了才推）
        };
        _uiTimer.Start();
        _overlayLastElapsed = -1;   // 新一轮播放：重置节流基线
        StartFocusGuard();   // 焦点守卫：勾选时焦点离开目标程序自动暂停（内部自己判断要不要挂）
        if (ChkOverlay.IsChecked == true)
            EnsureOverlay().SetNotes(_playNotes, engine.TotalSeconds);   // 迷你卷帘的音符
        ShowOverlayProgress(engine);   // 倒计时是 0 秒时这里没有等待期，立刻摆出进度
    }

    private void BtnStop_Click(object? sender, RoutedEventArgs e) => StopPlaybackNow();

    private void StopPlaybackNow()
    {
        _countdownTimer?.Stop();
        _countdownTimer = null;

        var eng = _engine;
        _engine = null;
        eng?.Stop();
        StopPreviewAudio();

        _uiTimer?.Stop();
        _uiTimer = null;
        // C05：消息条只留最新一条，所以先写“已停止”，再写诊断。
        // 反过来（诊断在前）用户只会看到“已停止。”，诊断被覆盖掉。
        InsertLog("已停止。");
        if (eng != null) InsertLog(eng.Probe.Summary());
        ForceReleaseKeysForGame();
        ResetUi();
    }

    private void Disclaimer_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is Border b) b.IsVisible = false;
        // 确认过一次就记住：条款没有变，之后启动不再显示
        if (_cfg != null && !_cfg.DisclaimerAccepted)
        {
            _cfg.DisclaimerAccepted = true;
            _cfg.Save();
        }
    }

    private void ResetUi()
    {
        _liveTimer?.Stop();
        _liveTimer = null;
        _liveQueued = false;
        _seeking = false;
        _focusTimer?.Stop();
        _focusTimer = null;
        _focusAutoPaused = false;
        SetBusy(false);
        // 停止后按谱面时间换算当前位置，方便直接重新定位
        double frac = SliderProgress.Maximum > 0 ? SliderProgress.Value / SliderProgress.Maximum : 0;
        _previewSeconds = frac * PreviewTotalSeconds;
        RefreshPreview();
        Roll.SetPosition(_previewSeconds);
        LblStatus.FontSize = ResourceFontSize("FontDisplay", 22);
        SetCountdownChrome(false);
        SetIdleHint();
        HideOverlay();
        _gameHwnd = IntPtr.Zero;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        // 暂停中允许打开新 MIDI（载入时会自动先停止当前播放）
        BtnOpen.IsEnabled = !busy || (_engine is { IsRunning: true, IsPaused: true });
        TrackList.IsEnabled = !busy;
        BtnPlayMode.IsEnabled = !busy;   // 演奏中不换模式：一轮演奏的走向在开始时已定
        ChkTrimLead.IsEnabled = !busy;
        ChkFoldOctave.IsEnabled = !busy;
        ChkAutoMinimize.IsEnabled = !busy;
        BtnPreview.IsEnabled = !busy;
        CountdownCombo.IsEnabled = !busy;
        // 键位方案在演奏中不换：一轮演奏的按键表在开始时就已经算好（键位页在设置窗口里，这里只锁入口）
        if (BtnSettings != null) BtnSettings.IsEnabled = !busy;
        // 一键移调 / 导出的可用性统一由 UpdateActionButtons() 决定，这里不再覆盖。
        UpdateActionButtons();

        UpdateTransportUi();
        // 速度 / 移调两个滑条：空闲与播放中都可调（播放中实时生效）
        SliderSpeed.IsEnabled = true;
        SliderTranspose.IsEnabled = true;
    }

    // ================= 托盘 =================

    private void SetupTray()
    {
        if (!OperatingSystem.IsWindows() || _quitNow) return;
        if (_tray != null)
        {
            try { _tray.IsVisible = true; } catch { }
            return;
        }
        try
        {
            // 图标必须走内置资源：单文件发布时旁边没有 Assets 目录
            WindowIcon? icon = null;
            try
            {
                using var s = Avalonia.Platform.AssetLoader.Open(
                    new Uri("avares://MidiKeyPlayer/Assets/app.ico"));
                icon = new WindowIcon(s);
            }
            catch
            {
                // 内置资源缺失时尝试工作目录旁的 Assets/app.ico
                string p = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
                if (File.Exists(p)) icon = new WindowIcon(File.OpenRead(p));
            }

            var menu = new NativeMenu();
            var miShow = new NativeMenuItem { Header = "显示主窗口" };
            miShow.Click += (_, _) => ShowMainWindow();
            var miToggle = new NativeMenuItem { Header = "开始 / 暂停 / 继续（F6）" };
            miToggle.Click += (_, _) => ToggleControl();
            var miStop = new NativeMenuItem { Header = "停止" };
            miStop.Click += (_, _) => StopPlaybackNow();
            var miQuit = new NativeMenuItem { Header = "退出" };
            miQuit.Click += (_, _) => QuitApp();
            menu.Items.Add(miShow);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(miToggle);
            menu.Items.Add(miStop);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(miQuit);

            _tray = new TrayIcon
            {
                ToolTipText = "MIDI 按键播放器（免费开源）",
                Icon = icon,
                Menu = menu,
                IsVisible = true
            };
            InsertLog("托盘图标已创建（如未显示，看任务栏通知区“上箭头”内）");
        }
        catch (Exception ex)
        {
            _tray = null;
            InsertLog($"托盘初始化失败：{ex.Message}（稍后自动重试）");
        }
    }

    /// <summary>窗口显示后再确认托盘图标，Windows 偶发注册慢则延迟重试一次。</summary>
    private void EnsureTray()
    {
        SetupTray();
        if (_tray != null && _tray.IsVisible) return;
        var retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        retry.Tick += (_, _) =>
        {
            retry.Stop();
            SetupTray();
        };
        retry.Start();
    }

    private void ShowMainWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void QuitApp()
    {
        _quitNow = true;
        Close();
    }

    /// <summary>UI-02：关窗确认。用户确认丢弃改动才真的退出。</summary>
    private async Task ConfirmExitWithEditsAsync()
    {
        if (_exitConfirming) return;
        _exitConfirming = true;
        try
        {
            if (!await ConfirmDiscardEditsAsync("退出程序")) return;
            _quitNow = true;   // 已经确认过，第二次关窗不再询问
            Close();
        }
        finally
        {
            _exitConfirming = false;
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (HasUnexportedEdits && !_quitNow && !_exitConfirming)
        {
            e.Cancel = true;
            _ = ConfirmExitWithEditsAsync();
            return;
        }

        SaveSettings();

        // 点 × = 彻底退出（托盘图标一并移除）。清理步骤与开发快照的退出走同一段代码。
        DevCleanUpForExit();
    }

    internal void DevCleanUpForExit()
    {
        _updateCts?.Cancel();   // 退出时取消进行中的更新包下载
        _overlay?.Close();
        _overlay = null;
        _countdownTimer?.Stop();
        _liveTimer?.Stop();
        _uiTimer?.Stop();
        _saveDeb?.Stop();     // 去抖计时器也会顶着拆窗口触发：一并停掉
        _previewDeb?.Stop();
        _engine?.Stop();
        StopPreviewAudio();
        // 【诊断用，可删】关窗口这条路的清音证据（探针模式才写）
        if (PreviewProbeMode.On)
        {
            PreviewJitterProbe.Note($"关窗口：StopPreviewAudio 之后 sounding={_preview?.SoundingCount ?? -1}，"
                + $"noteOn={_preview?.NoteOnCount ?? -1}，noteOff={_preview?.NoteOffCount ?? -1}");
            PreviewJitterProbe.WriteReport();
        }
        _previewSeq?.Dispose();
        _previewSeq = null;
        _preview?.Dispose();
        _preview = null;
        Input.GlobalHotkeys.Stop();
        MidiInputService.Stop();
        _livePlay.Dispose();
        _tray?.Dispose();
        _tray = null;
        ForceReleaseKeysForGame();
    }

    /// <summary>彻底松开按键/鼠标键：把焦点还给记住的目标窗口，再补发一次。</summary>
    private void ForceReleaseKeysForGame()
    {
        try
        {
            Input.InputSender.ReleaseEverything();
            IntPtr game = _gameHwnd;
            if (game != IntPtr.Zero && game != SelfHwnd)
            {
                Input.InputSender.BringToForeground(game);
                Input.InputSender.ReleaseEverything();
            }
        }
        catch
        {
            // 释放失败不阻断流程
        }
    }

    private IntPtr SelfHwnd
    {
        get
        {
            var ph = TryGetPlatformHandle();
            return ph?.Handle ?? IntPtr.Zero;
        }
    }
}

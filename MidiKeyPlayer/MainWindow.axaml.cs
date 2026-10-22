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
using KeyBinding = MidiKeyPlayer.Engine.KeyBinding;

namespace MidiKeyPlayer;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<TrackRowVM> _tracks = new();
    private readonly List<TrackRowVM> _mixOrder = new();   

    private ParsedMidi? _parsed;
    private TrackRowVM? _selected;
    private List<MappedNote> _playNotes = new();
    private PlaybackEngine? _engine;
    private DispatcherTimer? _uiTimer;
    private DispatcherTimer? _countdownTimer;
    private DispatcherTimer? _liveTimer;
    private DispatcherTimer? _saveDeb;
    private DispatcherTimer? _preflightTimer;
    private string _preflightSig = "";
    private bool _speedFromInput;
    private int _titleSecond = -1;
    private IntPtr _hwnd;
    private readonly DispatcherTimer _previewDeb;
    private int _countdownLeft;
    private bool _busy;

    private bool _seeking;          
    private List<MappedNote> _previewNotes = new();   
    private double _previewSeconds;                   
    private int _noteCount;                           
    private readonly ScoreEditor _editor = new();     
    private bool _editing;                            
    private bool _editsExported;                      
    private bool _discardPrompting;                   
    private bool _revertingTrack;                     
    private DispatcherTimer? _focusTimer;             
    private bool _focusAutoPaused;                    
    private uint _gamePid;                            
    private bool _revertingMix;                       
    private bool _exitConfirming;                     
    private bool _helpOn;                             
    private bool _liveQueued;       
    private readonly LivePlayback _livePlay = new();   
    private bool _midiStarting;     
    private IntPtr _gameHwnd;       
    private double _removedLeadSec; 
    private readonly AppConfig _cfg;
    private TrayIcon? _tray;
    private bool _quitNow;

    private Playlist? _playlist;
    private PlaylistWindow? _playlistWindow;
    private bool _playlistSyncing;
    private const int PlaylistMenuMax = 50;
    private bool _sourceIsPlaylist;
    private int _consecutiveFailures;
    private int _playlistPos = -1;
    private Random _shuffleRng = new();
    private string _nextSongLabel = "";

    private bool _advancing;

    private static readonly int[] CountdownOptions = { 0, 3, 5, 10 };
    private const int FixedLeadMs = 25;
    private const double SeekStepSeconds = 5;   

    private bool _uiReady;   
    private bool _warnIsOk;  
    private bool _devThemeOverride;   
    private string _updateUrl = "";     
    private string _updateTag = "";     
    private string _updateAssetUrl = "";   
    private string _updateNewExe = "";     
    private bool _updateBusy;              
    private CancellationTokenSource? _updateCts;
    private OverlayWindow? _overlay;        
    private bool _overlayMuted;             
    private double _overlayLastElapsed = -1;   
    private bool _overlayLastPaused;
    private int _overlayLastLoop = -1;
    private MappingResult? _lastMapping;    

    private bool HasUnexportedEdits => _editing && !_editsExported;

    private KeymapProfile _keymap = KeymapProfile.Default;

    private void ClampRightRows()
    {
        if (RightColumn == null || RightColumn.RowDefinitions.Count < 4) return;
        var status = RightColumn.RowDefinitions[0];
        var splitter = RightColumn.RowDefinitions[2];
        var options = RightColumn.RowDefinitions[3];
        if (!options.Height.IsAbsolute) return;

        double room = RightColumn.Bounds.Height - status.ActualHeight - splitter.ActualHeight - EditorMinH;
        if (!double.IsFinite(room)) return;
        double cap = Math.Max(OptionsMinH, room);
        if (options.Height.Value > cap + 0.5)
            options.Height = new GridLength(cap);
    }

    private void ClampToScreen()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen == null) return;

        double maxW = screen.WorkingArea.Width / screen.Scaling - 24;
        double maxH = screen.WorkingArea.Height / screen.Scaling - 64;
        if (maxW < 700) maxW = 700;
        if (maxH < 620) maxH = 620;

        MinWidth = Math.Min(MinWidth, maxW);
        MinHeight = Math.Min(MinHeight, maxH);
        if (Width > maxW) Width = maxW;
        if (Height > maxH) Height = maxH;
    }

    private void ApplySavedWindowGeometry()
    {
        if (_cfg == null) return;
        if (_cfg.WindowW >= 900 && _cfg.WindowH >= 640)
        {
            Width = _cfg.WindowW;
            Height = _cfg.WindowH;
            ClampToScreen();
        }
        if (_cfg.WindowX >= 0 && _cfg.WindowY >= 0)
        {
            bool onScreen = false;
            foreach (var screen in Screens.All)
            {
                var wa = screen.WorkingArea;
                if (_cfg.WindowX >= wa.X && _cfg.WindowX < wa.X + wa.Width
                    && _cfg.WindowY >= wa.Y && _cfg.WindowY < wa.Y + wa.Height)
                {
                    onScreen = true;
                    break;
                }
            }
            if (onScreen)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = new PixelPoint(_cfg.WindowX, _cfg.WindowY);
            }
        }
    }

    private void ApplySavedLayout()
    {
        if (_cfg == null || LeftColumn == null || RightColumn == null || MainBody == null) return;

        if (_cfg.LeftWidth >= 420)
            LeftColumn.Width = Math.Clamp(_cfg.LeftWidth, 420, Math.Max(420, MainBody.Bounds.Width - 440));

        double openH = LeftColumn.RowDefinitions[0].ActualHeight;
        double splitH = LeftColumn.RowDefinitions[2].ActualHeight + LeftColumn.RowDefinitions[4].ActualHeight;
        double avail = LeftColumn.Bounds.Height - openH - splitH - TrackMinH;

        double folderH = FolderCard is { IsVisible: true } ? _cfg.FolderH : 0;
        double playlistH = PlaylistCard is { IsVisible: true } ? _cfg.PlaylistH : 0;
        if (folderH + playlistH > 0 && folderH + playlistH <= avail)
        {
            if (folderH >= FolderMinH) LeftColumn.RowDefinitions[1].Height = new GridLength(folderH);
            if (playlistH >= PlaylistMinH) LeftColumn.RowDefinitions[3].Height = new GridLength(playlistH);
        }

        if (_cfg.OptionsH >= OptionsMinH && RightColumn.RowDefinitions.Count > 3)
            RightColumn.RowDefinitions[3].Height = new GridLength(_cfg.OptionsH);
    }

    private void FileDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer?.Contains(DataFormat.File) == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void FileDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        string? midi = null;
        var files = e.DataTransfer?.TryGetFiles();
        if (files != null)
        {
            foreach (var item in files)
            {
                string? path = item.TryGetLocalPath();
                if (path != null && IsMidiFile(System.IO.Path.GetExtension(path)))
                {
                    midi = path;
                    break;
                }
            }
        }

        if (midi == null)
        {
            InsertLog("拖入的文件不是 MIDI（.mid / .midi / .kar / .rmi）。");
            return;
        }
        _ = OpenDroppedFileAsync(midi);
    }

    private async Task OpenDroppedFileAsync(string path)
    {
        if (!await ConfirmDiscardEditsAsync("打开拖入的文件")) return;
        _sourceIsPlaylist = false;
        LoadMidiFile(path);
    }

    public MainWindow()
    {
        InitializeComponent();
        ClampToScreen();
        SizeChanged += (_, _) => ClampRightRows();
        LayoutUpdated += (_, _) => ClampRightRows();

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, FileDragOver);
        AddHandler(DragDrop.DropEvent, FileDrop);

        TrackList.ItemsSource = _tracks;

        CountdownCombo.ItemsSource = new List<string> { "0秒(立即)", "3秒", "5秒", "10秒" };
        CountdownCombo.SelectedIndex = 1;

        var fkeys = new List<string> { "无" };
        for (int i = 1; i <= 12; i++) fkeys.Add("F" + i);
        HotkeyControlCombo.ItemsSource = fkeys;
        HotkeyControlCombo.SelectedIndex = 6;   
        HotkeyRewindCombo.ItemsSource = fkeys;
        HotkeyRewindCombo.SelectedIndex = 5;    
        HotkeyForwardCombo.ItemsSource = fkeys;
        HotkeyForwardCombo.SelectedIndex = 7;   
        HotkeyPrevCombo.ItemsSource = fkeys;
        HotkeyPrevCombo.SelectedIndex = 8;      
        HotkeyNextCombo.ItemsSource = fkeys;
        HotkeyNextCombo.SelectedIndex = 9;      

        TimingCombo.ItemsSource = InputTiming.Names;
        TimingCombo.SelectedIndex = 1;          

        BackendCombo.ItemsSource = new[] { "SendInput（Windows 自带）", "罗技 G HUB 驱动" };

        _cfg = AppConfig.Load();
        ApplySavedWindowGeometry();
        Opened += (_, _) =>
        {
            if (_cfg.WindowMax) WindowState = WindowState.Maximized;
            Dispatcher.UIThread.Post(ApplySavedLayout);
        };
        CountdownCombo.SelectedIndex = Math.Clamp(_cfg.CountdownIndex, 0, 3);
        HotkeyControlCombo.SelectedIndex = Math.Clamp(_cfg.ControlHotkeyIndex, 0, 12);
        HotkeyRewindCombo.SelectedIndex = Math.Clamp(_cfg.RewindHotkeyIndex, 0, 12);
        HotkeyForwardCombo.SelectedIndex = Math.Clamp(_cfg.ForwardHotkeyIndex, 0, 12);
        HotkeyPrevCombo.SelectedIndex = Math.Clamp(_cfg.PrevSongHotkeyIndex, 0, 12);
        HotkeyNextCombo.SelectedIndex = Math.Clamp(_cfg.NextSongHotkeyIndex, 0, 12);
        _speedFromInput = true;
        SliderSpeed.Value = Math.Clamp(_cfg.Speed, 10, 400);
        _speedFromInput = false;
        SliderTranspose.Value = Math.Clamp(_cfg.Transpose, -24, 24);
        ChkTrimLead.IsChecked = _cfg.TrimLead;
        ChkFoldOctave.IsChecked = _cfg.FoldOctave;
        ChkAutoMinimize.IsChecked = _cfg.AutoMinimizeOnPlay;
        ChkFocusGuard.IsChecked = _cfg.FocusGuard;   
        ChkShowPreflight.IsChecked = _cfg.ShowPreflight;
        PreflightRow.IsVisible = _cfg.ShowPreflight;   
        ChkOverlay.IsChecked = _cfg.OverlayEnabled;    
        ChkOverlayHideOnPause.IsChecked = _cfg.OverlayHideOnPause;   
        ChkEnablePlaylist.IsChecked = _cfg.PlaylistEnabled;           
        TxtAboutVersion.Text = $"MIDI 按键播放器 v{AutoUpdate.CurrentVersion}";
        FillAboutLinks();
        if (_cfg.DisclaimerAccepted) DisclaimerBar.IsVisible = false;   
        ThemeCombo.ItemsSource = ThemeSwitch.Names;    
        ThemeCombo.SelectedIndex = ThemeSwitch.Clamp(_cfg.ThemeMode);
        TimingCombo.SelectedIndex = Math.Clamp(_cfg.TimingIndex, 0, 2);
        BackendCombo.SelectedIndex = Math.Clamp(_cfg.InputBackend, 0, 1);   
        RefreshRecentUi();
        RestoreFolderFromConfig();
        RefreshFolderUi();   
        RestorePlaylistFromConfig();   
        ApplyPlayModeUi();             

        _keymap = KeymapProfile.Current ?? KeymapProfile.Default;
        if (TxtKeymapName != null) TxtKeymapName.Text = _keymap.Name;
        ApplyProfileSettings(_keymap.Name);

        _livePlay.Timing = InputTiming.FromIndex(TimingCombo.SelectedIndex);
        _livePlay.BaseOctave = Math.Clamp(_cfg.MidiBaseOctave, 1, 6);
        _livePlay.MinVelocity = Math.Clamp(_cfg.MidiMinVelocity, 1, 127);
        _livePlay.AutoFit = _cfg.MidiAutoFit;
        SliderMidiOctave.Value = _livePlay.BaseOctave;
        SliderMidiVelocity.Value = _livePlay.MinVelocity;
        ChkMidiAutoFit.IsChecked = _livePlay.AutoFit;
        ChkMidiLive.IsChecked = false;   
        UpdateMidiLabels();
        MidiInputService.NoteEvent += OnMidiNote;
        MidiInputService.Error += s => UiPost(() =>
        {
            InsertLog("[MIDI] " + s);
            if (ChkMidiLive.IsChecked != true) return;
            _livePlay.ReleaseAll();
            ChkMidiLive.IsChecked = false;   
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

        if (Input.GlobalHotkeys.IsAvailable && !PreviewProbeMode.On)
        {
            Input.GlobalHotkeys.Status += s => UiPost(() => InsertLog(s));
            Input.GlobalHotkeys.KeyState += OnGlobalKeyWorker;
            ReconfigureHotkeys();
            Input.GlobalHotkeys.Start();
        }

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
            if (!_uiReady) return;   
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

        RunPreflight();

        _preflightTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _preflightTimer.Tick += (_, _) => RunPreflight(auto: true);
        _preflightTimer.Start();
        Activated += (_, _) => RunPreflight(auto: true);

        _ = CheckUpdateAsync();

        InsertLog("欢迎使用 MIDI 按键播放器");
        InsertLog("用法：打开 MIDI → 点一行作为主旋律 → 按 F6，倒计时内切到目标程序并装备乐器。");
        InsertLog("控制热键：F6 = 开始 / 暂停 / 继续（目标程序中生效，可改）。");

        string curVer = AutoUpdate.CurrentVersion;
        if (_cfg.LastRunVersion.Length > 0 && _cfg.LastRunVersion != curVer)
        {
            InsertLog($"已自动更新：v{_cfg.LastRunVersion} → v{curVer}");
            global::MidiKeyPlayer.Persist.LogFile.Append($"[更新] 已从 v{_cfg.LastRunVersion} 更新到 v{curVer}");
        }
        if (_cfg.LastRunVersion != curVer) { _cfg.LastRunVersion = curVer; _cfg.Save(); }

        InsertLog($"本程序免费开源。{AutoUpdate.FreeNotice}");
        InsertLog($"作者 B 站：{AutoUpdate.AuthorSpaceUrlShort}　反馈 QQ 群：{AutoUpdate.QqGroupNumber}");

        if (OperatingSystem.IsWindows() && !PreviewProbeMode.On)
        {
            SetupTray();
            Opened += (_, _) => EnsureTray();
        }
        Opened += (_, _) => ShowStartupOnce();

        InstallDevSnapshot(this);   
        InstallPreviewProbe(this);  
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

    private void Tutorial_Click(object? sender, RoutedEventArgs e)
    {
        ShowStartupOverlay();
        BtnStartupOk?.Focus();
    }

    internal void ShowTutorialForDev() => Tutorial_Click(null, new RoutedEventArgs());

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
            else ScanMidiFolder(_folderPath);   
            return;
        }

        bool wasPlaying = _engine is { IsRunning: true };   
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

    private double CurrentTotal()
    {
        if (_previewOn) return _previewTotal;
        var eng = _engine;
        if (eng is { IsRunning: true }) return Math.Max(0.001, eng.TotalSeconds);
        return PreviewTotalSeconds;
    }

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
        if (_busy)   
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
        _focusAutoPaused = false;   
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

    private void SetPauseUi(bool paused)
    {
        LblStatus.Foreground = paused ? FailBrush : OkBrush;
        LblStatus.FontSize = ResourceFontSize("FontDisplay", 22);
        LblStatus.Text = paused ? "已暂停，按 F6 或点「▶ 继续」" : "演奏中…";
        UpdateTransportUi();

        if (_cfg?.OverlayHideOnPause == true)
        {
            if (paused) HideOverlay();
            else if (_engine is { IsRunning: true } eng) ShowOverlayProgress(eng);
        }
    }

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
        if (fg == 0 || fg == Environment.ProcessId) return;   
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
        if (_engine is { IsRunning: true })
        {
            if (ChkFocusGuard.IsChecked == true) StartFocusGuard();
            else { _focusTimer?.Stop(); _focusTimer = null; }
        }
    }

    private void SetIdleHint()
    {
        LblStatus.Foreground = NeutralBrush;
        LblStatus.FontSize = ResourceFontSize("FontTitle", 15);
        LblStatus.Text = "打开 MIDI，点一行作为主旋律";
    }

    private void UpdateTransportUi()
    {
        var eng = _engine;
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
        BtnStop.IsEnabled = _busy;   

        bool hasTrack = ActiveRows().Count > 0;
        bool hasPlayable = hasTrack && BuildMapping().InRangeCount > 0;
        BtnPlay.IsEnabled = !_busy && hasPlayable;

        string reason = hasTrack
            ? (hasPlayable ? "" : "这首歌没有可弹的音，换个键位方案或调移调")
            : "先打开 MIDI，再点一行";
        TxtHotHint.Text = "";
        Avalonia.Controls.ToolTip.SetTip(BtnPlay, BtnPlay.IsEnabled ? null : reason);
    }

    private void UiPost(Action a) => Dispatcher.UIThread.Post(a);

    private void InsertLog(string msg)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        if (TxtLastMsg != null) TxtLastMsg.Text = msg;   
        Persist.LogFile.Append(line);                    
    }

    private void ScheduleSave()
    {
        _saveDeb?.Stop();
        _saveDeb?.Start();
    }

    private void SaveSettings()
    {
        if (_cfg == null) return;
        if (DevSnapshotMode.On) return;
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
        _cfg.FolderPath = _folderPath;      
        RememberCurrentProfileSettings();   
        _cfg.Save();
    }

    private void UpdateSettingLabels()
    {
        if (TxtSpeed is null || TxtTranspose is null) return;
        TxtSpeed.Text = $"{SliderSpeed.Value:0}%";
        double tr = SliderTranspose.Value;
        TxtTranspose.Text = tr > 0 ? $"+{tr:0}" : $"{tr:0}";
    }

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

        _speedFromInput = true;
        SliderSpeed.Value = Math.Clamp(s.Speed, 10, 400);
        _speedFromInput = false;        
        SliderTranspose.Value = Math.Clamp(s.Transpose, -24, 24);
        TimingCombo.SelectedIndex = Math.Clamp(s.TimingIndex, 0, 2);

        var timing = InputTiming.FromIndex(TimingCombo.SelectedIndex);
        _livePlay.Timing = timing;

        _cfg.Speed = (int)SliderSpeed.Value;
        _cfg.Transpose = (int)SliderTranspose.Value;
        _cfg.TimingIndex = TimingCombo.SelectedIndex;

        UpdateSettingLabels();
    }

    private void SpeedChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (ReferenceEquals(sender, SliderSpeed) && !_speedFromInput)
        {
            double snapped = Math.Round(SliderSpeed.Value / 5.0) * 5;
            if (Math.Abs(snapped - SliderSpeed.Value) > 0.001)
            {
                _speedFromInput = true;
                SliderSpeed.Value = snapped;
                _speedFromInput = false;
                return;
            }
        }

        UpdateSettingLabels();

        if (_engine is { IsRunning: true } eng)
        {
            if (ReferenceEquals(sender, SliderSpeed))
            {
                eng.Speed = SliderSpeed.Value / 100.0;
            }
            else if (ReferenceEquals(sender, SliderTranspose))
            {
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

    private void NumberInput_GotFocus(object? sender, GotFocusEventArgs e)
    {
        if (sender is TextBox tb) tb.SelectAll();
    }

    private void NumberInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (e.Key == Key.Enter)
        {
            CommitNumberInput(tb);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            UpdateSettingLabels();
            tb.SelectAll();
            e.Handled = true;
        }
    }

    private void NumberInput_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) CommitNumberInput(tb);
    }

    private void CommitNumberInput(TextBox tb)
    {
        string raw = (tb.Text ?? "").Trim().TrimEnd('%').Trim();
        if (raw.StartsWith('+')) raw = raw[1..];
        if (!int.TryParse(raw, out int v))
        {
            UpdateSettingLabels();
            return;
        }

        if (ReferenceEquals(tb, TxtSpeed))
        {
            v = Math.Clamp(v, 10, 400);
            if (Math.Abs(SliderSpeed.Value - v) > 0.001)
            {
                _speedFromInput = true;
                SliderSpeed.Value = v;
                _speedFromInput = false;
            }
        }
        else if (ReferenceEquals(tb, TxtTranspose))
        {
            v = Math.Clamp(v, -24, 24);
            if (Math.Abs(SliderTranspose.Value - v) > 0.001) SliderTranspose.Value = v;
        }
        UpdateSettingLabels();
    }

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

    private void Progress_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!SliderProgress.IsEnabled) return;
        _seeking = true;
        SeekThumbTo(e);            
        PreviewSeekFromSlider();
    }

    private void Progress_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_seeking) return;
        SeekThumbTo(e);            
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

    private void PreviewSeekFromSlider() => ShowPosition(SliderProgress.Value);

    private void OnRollPreview(double seconds)
    {
        _seeking = true;          
        ShowPosition(seconds);
    }

    private void OnRollSeek(double seconds)
    {
        _seeking = false;
        if (_previewOn) seconds /= _previewSpeed;
        ApplySeek(seconds);
    }

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

    private void ShowPosition(double seconds)
    {
        double total = CurrentTotal();
        double t = Math.Clamp(seconds, 0, total);
        SliderProgress.Value = t;
        Roll.SetPosition(t);
        TxtTime.Text = $"{t:F1} / {total:F1} s";
        UpdateSeekNote(t);
        if (!_previewOn && _engine is not { IsRunning: true }) _previewSeconds = t;
        if (_engine is { IsRunning: true } eng) UpdatePlaybackChrome(eng.ElapsedSeconds, eng.TotalSeconds);
    }

    private string SongDisplayName =>
        _parsed == null ? "" : System.IO.Path.GetFileNameWithoutExtension(_parsed.FilePath);

    private IntPtr Hwnd
    {
        get
        {
            if (_hwnd == IntPtr.Zero && TryGetPlatformHandle() is { } handle) _hwnd = handle.Handle;
            return _hwnd;
        }
    }

    private void UpdateWindowTitle()
    {
        string name = SongDisplayName;
        Title = name.Length == 0 ? "MIDI 按键播放器（免费开源）" : $"{name} — MIDI 按键播放器";
        _titleSecond = -1;
    }

    private void UpdatePlaybackChrome(double elapsed, double total)
    {
        int sec = (int)Math.Floor(elapsed);
        if (sec != _titleSecond)
        {
            _titleSecond = sec;
            string name = SongDisplayName;
            int totalSec = (int)Math.Round(total);
            string clock = $"{sec / 60}:{sec % 60:00} / {totalSec / 60}:{totalSec % 60:00}";
            Title = name.Length == 0 ? $"MIDI 按键播放器 — {clock}" : $"{name} — {clock}";
            TaskbarProgress.Set(Hwnd, elapsed, total);
        }
    }

    private void ClearPlaybackChrome()
    {
        TaskbarProgress.Clear(Hwnd);
        UpdateWindowTitle();
    }

    private void UpdateSeekNote(double? atSeconds = null)
    {
        if (TxtSeekNote == null) return;
        double t = atSeconds ?? (_engine is { IsRunning: true } ? _engine.ElapsedSeconds : _previewSeconds);
        MappedNote? note = _previewNotes.LastOrDefault(n => n.Start <= t && t < n.End);
        if (note == null) { TxtSeekNote.Text = "—"; return; }
        string name = Music.SolfegeName(note.Pitch);
        if (!note.InRange) { TxtSeekNote.Text = $"{name} 没有对应键"; return; }
        _keymap.TryKeyOfPitch(note.Pitch, out string key, out int octaveOffset, out bool sharp);
        var parts = new List<string>();
        if (octaveOffset < 0) parts.Add("降八度键+");
        else if (octaveOffset > 0) parts.Add("升八度键+");
        if (sharp) parts.Add("升半音键+");
        parts.Add(string.IsNullOrEmpty(key) ? "（无按键）" : DisplayKey(key));
        TxtSeekNote.Text = $"{name} · {string.Join("", parts)}";
    }

    private static string DisplayKey(string key) => key == "," ? "，" : key;

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

    private void DropEditsIfAny(string why)
    {
        if (!_editing) return;
        ResetEdits();
        InsertLog($"已丢弃手动改动（{why}）。");
    }

    private void BeginEditIfNeeded()
    {
        if (_editing) return;
        _editor.Reset(ComputeAutoNotes());
        _editing = true;
        _editsExported = false;
        UpdateEditMark();
        InsertLog("已进入编辑模式：自动提取的选项不再影响谱面，点「还原为自动」可退出。");
    }

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
        _editsExported = false;   
        _editor.ReplaceAll(notes);
        RefreshPreview(pushToRoll: false);
        InsertLog($"已{what}。");
    }

    private void OnRollViewChanged()
    {
        if (TxtZoom == null) return;
        TxtZoom.Text = $"{Roll.ZoomPercent:F0}%";
        if (ChkFollow != null && ChkFollow.IsChecked != Roll.FollowPlayhead)
            ChkFollow.IsChecked = Roll.FollowPlayhead;
    }

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
        _editsExported = false;   
        RefreshPreview(keepView: true);
        Roll.ClearSelection();
        InsertLog("已撤销。");
    }

    private void DoRedo()
    {
        if (!_editing || !_editor.Redo()) { InsertLog("没有可重做的操作。"); return; }
        _editsExported = false;   
        RefreshPreview(keepView: true);
        Roll.ClearSelection();
        InsertLog("已重做。");
    }

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
        UpdateEditMark();   
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
    private DispatcherTimer? _previewTimer;
    private double _previewTotal;
    private double _previewSpeed = 1.0;
    private bool _previewOn;
    internal int PreviewEventCount => _previewSeq?.EventCount ?? 0;

    private double PreviewNow()
    {
        var seq = _previewSeq;
        return seq != null && _previewOn ? seq.PositionSeconds : _previewSeconds;
    }

    private void PreviewSeekTo(double seconds)
    {
        _previewSeq?.Seek(Math.Clamp(seconds, 0, _previewTotal));
    }

    private void Preview_Click(object? sender, RoutedEventArgs e)
    {
        if (_previewOn) StopPreviewAudio();
        else StartPreviewAudio();
    }

    private void StartPreviewAudio()
    {
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
        _previewSeq.Start(startAt);   

        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _previewTimer.Tick += (_, _) => PreviewUiTick();
        _previewTimer.Start();

        if (Roll != null)
        {
            Roll.IsPlaying = true;
            Roll.SetPlayheadScale(_previewTotal > 0 ? PreviewTotalSeconds / _previewTotal : 1.0);
        }

        InsertLog($"试听开始：{notes.Count} 个音，约 {_previewTotal:F1}s（不发送按键）");
    }

    private void PreviewUiTick()
    {
        if (!_previewOn) return;

        if (_seeking) return;

        double t = PreviewNow();
        double shown = Math.Min(t, _previewTotal);   
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
        _previewSeq?.Stop();        
        _preview?.AllNotesOff();    
        if (BtnPreview != null) BtnPreview.Content = "试听";
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
            _editsExported = true;   
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

    private async void BtnOpen_Click(object? sender, RoutedEventArgs e)
    {
        HideOpenMenu();   
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
        catch { return null; }   
    }

    private readonly List<string> _folderFiles = new();   
    private readonly List<string> _folderDirs = new();    
    private string _folderPath = "";                      
    private const int FolderMenuMax = 50;                 
    private bool _folderShowAll;                          
    private string _folderQuery = "";                     
    private static readonly string[] MidiExtensions = { ".mid", ".midi", ".kar", ".rmi" };

    private bool _folderSyncing;

    private void RestoreFolderFromConfig()
    {
        string path = _cfg.FolderPath;
        if (string.IsNullOrEmpty(path)) return;   

        if (!System.IO.Directory.Exists(path))
        {
            _cfg.FolderPath = "";
            _cfg.Save();   
            InsertLog($"上次的曲目文件夹已不在，曲目卡保持关闭：{path}");
            return;
        }

        ScanMidiFolder(path);
    }

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
        _folderShowAll = false;
        _folderQuery = "";
        if (TxtFolderSearch != null && TxtFolderSearch.Text is { Length: > 0 })
            TxtFolderSearch.Text = "";   

        var dir = new System.IO.DirectoryInfo(path);
        bool failed = false;

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

        _folderDirs.Sort((a, b) => string.Compare(
            System.IO.Path.GetFileName(a), System.IO.Path.GetFileName(b), StringComparison.OrdinalIgnoreCase));
        _folderFiles.Sort((a, b) => string.Compare(
            System.IO.Path.GetFileName(a), System.IO.Path.GetFileName(b), StringComparison.OrdinalIgnoreCase));

        if (_folderDirs.Count == 0 && _folderFiles.Count == 0)
        {
            if (!failed) InsertLog($"{dir.Name}：没有找到 MIDI 文件或子文件夹。");
        }
        else
        {
            InsertLog($"{dir.Name}：{_folderDirs.Count} 个子文件夹、{_folderFiles.Count} 首 MIDI，已列在左侧的文件夹曲目里。");
        }

        RefreshFolderUi();
    }

    private static bool IsMidiFile(string ext)
    {
        foreach (string e in MidiExtensions)
            if (e.Equals(ext, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private void RefreshFolderUi()
    {
        if (FolderCard == null || FolderList == null) return;   

        bool hasFolder = !string.IsNullOrEmpty(_folderPath);
        FolderCard.IsVisible = hasFolder;
        UpdateSplitterLayout();
        if (!hasFolder) return;

        string trimmed = _folderPath.TrimEnd(
            System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        string leaf = System.IO.Path.GetFileName(trimmed);
        TxtFolderName.Text = leaf.Length > 0 ? leaf : _folderPath;
        Avalonia.Controls.ToolTip.SetTip(TxtFolderName, _folderPath);   

        string query = _folderQuery.Trim();
        List<string> dirs = query.Length == 0
            ? _folderDirs
            : _folderDirs.Where(d => NameMatches(System.IO.Path.GetFileName(d), query)).ToList();
        List<string> files = query.Length == 0
            ? _folderFiles
            : _folderFiles.Where(f => NameMatches(System.IO.Path.GetFileName(f), query)).ToList();

        int total = dirs.Count + files.Count;
        int shown = _folderShowAll ? total : Math.Min(total, FolderMenuMax);

        _folderSyncing = true;   
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

            if (total > shown)
                FolderList.Items.Add(BuildFolderShowMoreRow(total - shown, total));

            if (total == 0)
            {
                TxtFolderEmpty.Text = query.Length == 0
                    ? "这个文件夹里没有找到 MIDI 文件或子文件夹。"
                    : $"没有名字里带「{query}」的曲目或子文件夹。";
            }
            TxtFolderEmpty.IsVisible = total == 0;

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

    private sealed record FolderRow(string Path, bool IsDir);

    private sealed record FolderShowMore(int Hidden, int Total);

    private void FolderSearch_Changed(object? sender, TextChangedEventArgs e)
    {
        string text = TxtFolderSearch?.Text ?? "";
        if (string.Equals(text, _folderQuery, StringComparison.Ordinal)) return;   
        _folderQuery = text;
        _folderShowAll = false;   
        RefreshFolderUi();
    }

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

    private ListBoxItem BuildFolderRow(string path)
        => BuildFolderItem(path, System.IO.Path.GetFileName(path), isDir: false);

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
        Avalonia.Controls.ToolTip.SetTip(item, path);   
        if (isDir) item.DoubleTapped += FolderDirRow_DoubleTapped;
        return item;
    }

    private void FolderList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_folderSyncing) return;                                        
        if (FolderList?.SelectedItem is not ListBoxItem it) return;
        if (it.Tag is FolderShowMore more)                                 
        {
            Dispatcher.UIThread.Post(() => { _folderShowAll = true; RefreshFolderUi(); });
            return;
        }
        if (it.Tag is not FolderRow row) return;
        if (row.IsDir) return;                                             
        if (row.Path == _parsed?.FilePath) return;

        string pending = row.Path;
        Dispatcher.UIThread.Post(() => _ = OpenFolderFileAsync(pending));
    }

    private void FolderDirRow_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBoxItem it && it.Tag is FolderRow row && row.IsDir)
        {
            string pending = row.Path;
            Dispatcher.UIThread.Post(() => EnterFolder(pending));
        }
    }

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
        UpdateSplitterLayout();
        SaveSettings();   
        InsertLog("已收起左侧的文件夹曲目（下次启动不再显示）。");
    }

    private void FolderRefresh_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_folderPath)) return;

        string path = _folderPath;
        string query = TxtFolderSearch?.Text ?? "";
        bool showAll = _folderShowAll;

        ScanMidiFolder(path);

        if (TxtFolderSearch != null && query.Length > 0) TxtFolderSearch.Text = query;
        if (showAll)
        {
            _folderShowAll = true;
            RefreshFolderUi();
        }
        InsertLog("已刷新文件夹列表。");
    }

    private void FolderUp_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_folderPath)) return;
        try
        {
            var parent = System.IO.Directory.GetParent(_folderPath);
            if (parent == null)
            {
                InsertLog("已经在磁盘根目录，没有上一级。");
                return;
            }
            ScanMidiFolder(parent.FullName);
        }
        catch (Exception ex)
        {
            InsertLog($"返回上一级失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private object? _splitDrag;
    private double _splitStartX;
    private double _splitStartY;
    private double _splitStartLeftWidth;
    private double _splitStartFolder;
    private double _splitStartPlaylist;
    private double _splitStartTracks;
    private double _splitStartOptionsH;
    private double _splitStartEditorH;
    private GridLength _folderRowBeforeHide = GridLength.Auto;
    private GridLength _playlistRowBeforeHide = GridLength.Auto;

    private const double FolderMinH = 165;
    private const double PlaylistMinH = 135;
    private const double TrackMinH = 140;
    private const double OptionsMinH = 180;
    private const double EditorMinH = 200;

    private void WidthSplitter_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border band || LeftColumn == null || MainBody == null) return;
        if (!e.GetCurrentPoint(band).Properties.IsLeftButtonPressed) return;

        _splitStartLeftWidth = LeftColumn.Width;
        _splitDrag = band;
        _splitStartX = e.GetPosition(MainBody).X;
        e.Pointer.Capture(band);
        e.Handled = true;
    }

    private void WidthSplitter_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_splitDrag != sender || LeftColumn == null || MainBody == null) return;

        double dx = e.GetPosition(MainBody).X - _splitStartX;
        double max = Math.Max(420, MainBody.Bounds.Width - 440);
        LeftColumn.Width = Math.Clamp(_splitStartLeftWidth + dx, 420, Math.Min(640, max));
    }

    private void BottomSplitter_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border band || RightColumn == null || OptionsArea == null || EditorArea == null) return;
        if (!e.GetCurrentPoint(band).Properties.IsLeftButtonPressed) return;

        _splitStartOptionsH = OptionsArea.Bounds.Height;
        _splitStartEditorH = EditorArea.Bounds.Height;
        RightColumn.RowDefinitions[3].Height = new GridLength(_splitStartOptionsH);
        BeginSplitDrag(band, e);
        _splitStartY = e.GetPosition(RightColumn).Y;
    }

    private void BottomSplitter_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_splitDrag != sender || RightColumn == null) return;

        double dy = e.GetPosition(RightColumn).Y - _splitStartY;
        double max = _splitStartOptionsH + _splitStartEditorH - EditorMinH;
        double h = Math.Clamp(_splitStartOptionsH - dy, OptionsMinH, Math.Max(OptionsMinH, max));
        RightColumn.RowDefinitions[3].Height = new GridLength(h);
    }

    private void FolderSplitter_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border band || LeftColumn == null) return;
        if (!e.GetCurrentPoint(band).Properties.IsLeftButtonPressed) return;

        _splitStartFolder = FolderCard.Bounds.Height;
        FolderListHost.Height = double.NaN;
        if (PlaylistCard.IsVisible)
        {
            _splitStartPlaylist = PlaylistCard.Bounds.Height;
            PlaylistListHost.Height = double.NaN;
            LeftColumn.RowDefinitions[3].Height = new GridLength(_splitStartPlaylist);
        }
        else
        {
            _splitStartTracks = TrackCard.Bounds.Height;
        }
        LeftColumn.RowDefinitions[1].Height = new GridLength(_splitStartFolder);
        BeginSplitDrag(band, e);
    }

    private void FolderSplitter_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_splitDrag != sender || LeftColumn == null) return;

        double dy = e.GetPosition(LeftColumn).Y - _splitStartY;
        if (PlaylistCard.IsVisible)
        {
            double total = _splitStartFolder + _splitStartPlaylist;
            double folder = Math.Clamp(_splitStartFolder + dy, FolderMinH, Math.Max(FolderMinH, total - PlaylistMinH));
            LeftColumn.RowDefinitions[1].Height = new GridLength(folder);
            LeftColumn.RowDefinitions[3].Height = new GridLength(total - folder);
        }
        else
        {
            double max = _splitStartFolder + _splitStartTracks - TrackMinH;
            double folder = Math.Clamp(_splitStartFolder + dy, FolderMinH, Math.Max(FolderMinH, max));
            LeftColumn.RowDefinitions[1].Height = new GridLength(folder);
        }
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

    private void UpdateSplitterLayout()
    {
        if (LeftColumn == null || FolderCard == null || PlaylistCard == null) return;

        ApplyRow(FolderCard, 1, ref _folderRowBeforeHide);
        ApplyRow(PlaylistCard, 3, ref _playlistRowBeforeHide);

        FolderSplitter.IsVisible = FolderCard.IsVisible;
        TrackSplitter.IsVisible = PlaylistCard.IsVisible;

        var gap = this.TryFindResource("CardGap", out var g) && g is Thickness t ? t : new Thickness(0, 12, 0, 0);
        PlaylistCard.Margin = FolderCard.IsVisible ? new Thickness(0) : gap;
        TrackCard.Margin = FolderCard.IsVisible || PlaylistCard.IsVisible ? new Thickness(0) : gap;

        void ApplyRow(Control card, int index, ref GridLength saved)
        {
            var row = LeftColumn.RowDefinitions[index];
            if (!card.IsVisible)
            {
                if (row.Height.IsAbsolute && row.Height.Value > 0) saved = row.Height;
                row.Height = new GridLength(0);
            }
            else if (row.Height.IsAbsolute && row.Height.Value <= 0)
            {
                row.Height = saved;
            }
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
            _sourceIsPlaylist = false;
            LoadMidiFile(path);
        }
        catch (Exception ex)
        {
            InsertLog($"打开文件失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void HideOpenMenu() => BtnOpen?.Flyout?.Hide();

    private void RememberRecentFile(string path)
    {
        _cfg.RememberRecentFile(path);
        SaveSettings();      
        RefreshRecentUi();
        InsertLog($"已记入「最近打开」：{System.IO.Path.GetFileName(path)}");
    }

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

        var openFolder = new MenuItem { Header = "打开文件夹…" };
        openFolder.Click += BtnOpenFolder_Click;
        menu.Items.Add(openFolder);

        var playlist = new MenuItem { Header = _cfg.PlaylistEnabled ? "歌单…" : "启用歌单…" };
        playlist.Click += (_, _) => { HideOpenMenu(); PlaylistOpen_Click(null, new RoutedEventArgs()); };
        menu.Items.Add(playlist);

        menu.Items.Add(new Separator());

        menu.Items.Add(BuildRecentMenu());
    }

    private MenuItem BuildRecentMenu()
    {
        var root = new MenuItem { Header = "最近打开 ▸" };
        var files = _cfg.RecentFiles;
        if (files.Count == 0)
        {
            root.Items.Add(new MenuItem { Header = "还没有打开过文件", IsEnabled = false });
            return root;
        }
        foreach (string path in files)
        {
            var item = new MenuItem
            {
                Header = System.IO.Path.GetFileName(path),   
                Tag = path,
            };
            Avalonia.Controls.ToolTip.SetTip(item, path);    
            item.Click += RecentFile_Click;
            root.Items.Add(item);
        }
        root.Items.Add(new Separator());
        var clear = new MenuItem { Header = "清空列表" };
        clear.Click += ClearRecent_Click;
        root.Items.Add(clear);
        return root;
    }

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

        StopPlaybackForNewFile();

        try
        {
            var parsed = MidiLoader.Parse(path);
            _parsed = parsed;
            _tracks.Clear();
            _mixOrder.Clear();
            foreach (var c in parsed.Candidates) _tracks.Add(new TrackRowVM(c));

            LblFile.Text = System.IO.Path.GetFileName(path);
            UpdateWindowTitle();
            InsertLog($"已载入 {System.IO.Path.GetFileName(path)}：{parsed.Candidates.Count} 个候选，时长 ≈ {parsed.DurationSec:F1}s");
            string parts = DescribeRoles(parsed.Candidates);
            if (parts.Length > 0) InsertLog($"声部识别：{parts}");

            _selected = null;
            _previewSeconds = 0;      
            Roll.FitAll();
            DropEditsIfAny("打开了新文件");
            InsertLog("点左侧一行作为主旋律轨（选中的行带 ● 标记）。");
            RefreshPreview();
            ApplyPlaylistTracksFor(path);
            RememberRecentFile(path);
            string dir = System.IO.Path.GetDirectoryName(path) ?? "";
            if (dir.Length > 0 && !string.Equals(dir, _folderPath, StringComparison.OrdinalIgnoreCase))
                ScanMidiFolder(dir);
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
            InsertLog($"  位置：{FirstStackFrames(ex, 4)}");
            return false;
        }
    }

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

    private void StopPlaybackForNewFile()
    {
        if (_countdownTimer != null)
        {
            _countdownTimer.Stop();
            _countdownTimer = null;
            ResetUi();   
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
            InsertLog($"换主旋律轨失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task SetMain(TrackRowVM? row)
    {
        if (row == null) return;
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
        UpdateVoiceRoles();   
        RefreshPreview();
    }

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

        Roll.SetVoiceColors(BuildVoiceMap(GetActiveRawNotes()));
    }

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

        var voices = new List<(int Rank, RawNote Note)>();
        for (int k = 0; k < chosen.Count; k++)
            foreach (var n in chosen[k].Notes) voices.Add((k, n));
        var merged = NoteMapper.MergeVoicesByPriority(voices);

        Roll.BeginPreview();
        Roll.SetNotes(merged, merged.Select(n => n.Pitch).Distinct().ToList(), _previewParsed.DurationSec);
        Roll.SetVoiceColors(BuildVoiceMap(merged));
        InsertLog($"预览这一首勾的 {chosen.Count} 行：共 {merged.Count} 个音（只看，不改谱）。");
    }

    internal void EndPreviewTracks()
    {
        if (!Roll.PreviewOnly) return;
        _previewParsed = null;
        Roll.RestoreAfterPreview();
        InsertLog("结束预览，卷帘回到当前演奏的谱面。");
    }

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
            foreach (var n in rows[k].Candidate.Notes) voices.Add((k, n));  
        var merged = NoteMapper.MergeVoicesByPriority(voices);

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

        SliderTranspose.Value = best;   
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

        var raw = GetActiveRawNotes();
        if (raw.Count == 0) return;

        int baseNote = KeymapProfile.Current.BaseNote;
        int lo = -AppConfig.MaxTransposeSemitones;
        int hi = AppConfig.MaxTransposeSemitones;

        int bestT = 0, bestNatural = -1, bestAccidental = 0, bestSkip = int.MaxValue;
        int total = -1;   

        for (int t = lo; t <= hi; t++)
        {
            var map = NoteMapper.Map(raw, t, manualBaseOctave: null, foldOctave: _cfg.FoldOctave);
            total = map.Notes.Count;   

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

        SliderTranspose.Value = bestT;   
        string sign = bestT > 0 ? "+" : "";
        InsertLog($"自然音移调：{sign}{bestT} 半音后 {bestNatural} / {playable} 个音落在自然键上，"
                  + $"{bestAccidental} 个需要半音键，{bestSkip} 个没有对应的键。");
        if (bestAccidental > 0)
            InsertLog("　其中需要半音键的音，换个带升音的方案才能弹（例如「第五人格键位」）。");
        if (total > playable)
            InsertLog($"　另有 {total - playable} 个音在移调后超出 MIDI 音域（0 ~ 127）。");
        RefreshPreview();
    }

    private static bool IsNaturalPitch(int semitoneFromBase) => (semitoneFromBase % 12 + 12) % 12
        is 0 or 2 or 4 or 5 or 7 or 9 or 11;

    private async void Mix_Changed(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is CheckBox cb && cb.DataContext is TrackRowVM row)
            {
                if (_revertingMix) return;   
                bool on = cb.IsChecked == true;
                if (!await ConfirmDiscardEditsForActionAsync("改合奏声部"))
                {
                    _revertingMix = true;
                    try { cb.IsChecked = !on; }   
                    finally { _revertingMix = false; }
                    return;
                }
                DropEditsIfAny("改了合奏声部");
                row.IsMix = on;   
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
            InsertLog($"改合奏声部失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    internal void SyncMixOrder()
    {
        foreach (var r in _tracks)
            if (r.IsMix && !_mixOrder.Contains(r)) _mixOrder.Add(r);
        _mixOrder.RemoveAll(r => !r.IsMix);

        for (int i = 0; i < _mixOrder.Count; i++) _mixOrder[i].MixRank = i + 1; 
        foreach (var r in _tracks)
            if (!_mixOrder.Contains(r)) r.MixRank = 0;

        UpdateVoiceRoles();   

        if (_busy || _previewDeb is null) return;
        _previewDeb.Stop();
        RefreshPreview();   
    }

    private void Option_Changed(object? sender, RoutedEventArgs e)
    {
        ScheduleSave();
        if (_busy || _previewDeb is null) return;
        _previewDeb.Stop();
        _previewDeb.Start();
    }

    private void RunPreflight(bool auto = false)
    {
        PreflightCheck.Report report;
        try
        {
            report = PreflightCheck.Run();
        }
        catch
        {
            TxtCheckAdminMark.Text = "–";
            TxtCheckAdminMark.Foreground = NeutralBrush;
            TxtCheckImeMark.Text = "–";
            TxtCheckImeMark.Foreground = NeutralBrush;
            TxtCheckHint.Text = "";
            TxtCheckHint.IsVisible = false;
            return;
        }

        string hint = report.HasBlocked
            ? string.Join("；", new[] { report.Admin, report.Ime }
                .Where(c => c.Blocked)
                .Select(c => c.Detail))
            : string.Join("；", new[] { report.Admin, report.Ime }
                .Where(c => !c.Passed)
                .Select(c => c.Detail));

        string sig = $"{report.Admin.Status}|{report.Ime.Status}|{report.Admin.Detail}|{report.Ime.Detail}|{hint}";
        bool changed = sig != _preflightSig;
        _preflightSig = sig;
        if (auto && !changed) return;

        PaintCheck(report.Admin, TxtCheckAdminMark, TxtCheckAdmin);
        PaintCheck(report.Ime, TxtCheckImeMark, TxtCheckIme);
        BtnElevate.IsVisible = !report.Admin.Passed;
        TxtCheckHint.Text = hint;
        TxtCheckHint.IsVisible = hint.Length > 0;

        if (auto) return;
        try
        {
            var (_, probe) = PreflightCheck.DetectImeModeDetailed();
            Persist.LogFile.Append($"[自检] 输入法状态={report.Ime.Status}；{PreflightCheck.Describe(probe)}");
        }
        catch { }
    }

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
        label.Text = c.Name;
    }

    private async Task CheckUpdateAsync()
    {
        try
        {
            if (PreviewProbeMode.On || DevSnapshotMode.On) return;

            var r = await AutoUpdate.CheckAsync(_cfg?.SkippedUpdateTag);
            if (r.Error != null || !r.HasUpdate) return;

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

                TxtUpdate.Text = $"发现新版本 v{r.LatestTag}（当前 v{r.CurrentTag}），" +
                                 "点此自动下载并更新；右键跳过本版本。";
                UpdateBanner.IsVisible = true;
                InsertLog($"发现新版本：v{r.LatestTag}（当前 v{r.CurrentTag}）　下载页：{r.ReleaseUrl}");
            });
        }
        catch
        {
        }
    }

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
            if (_updateNewExe.Length == 0 && _updateCts != null)
            {
                _updateCts.Cancel();
                _updateBusy = false;
                TxtUpdate.Text = "已取消下载，点此重新下载；右键跳过本版本。";
                InsertLog("已取消更新包下载。");
            }
            return;
        }

        if (_updateNewExe.Length > 0)
        {
            ApplyDownloadedUpdate();
            return;
        }

        if (_updateAssetUrl.Length == 0)
        {
            AutoUpdate.OpenUrl(_updateUrl);
            InsertLog($"已打开下载页：{_updateUrl}");
            return;
        }

        await DownloadUpdateAsync();
    }

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
            if (!_updateCts.IsCancellationRequested)
            {
                SetUpdateText("更新包下载失败，点此重试；右键跳过本版本。");
                InsertLog("更新包下载失败，详见 play.log。也可以点提示条重试，或去下载页手动下载。");
            }
            return;
        }

        string? newExe = AutoUpdate.ExtractNewExe(zip);
        if (newExe == null)
        {
            _updateBusy = false;
            SetUpdateText("更新包校验失败，点此重试；右键跳过本版本。");
            InsertLog("更新包校验失败，详见 play.log。也可以点提示条重试，或去下载页手动下载。");
            return;
        }

        _updateNewExe = newExe;
        _updateBusy = false;
        SetUpdateText($"v{_updateTag} 已下载完成，点此重启并完成更新；右键跳过本版本。");
        InsertLog($"v{_updateTag} 更新包已就绪，重启程序后生效。");
    }

    private void SetUpdateText(string text)
    {
        TxtUpdate.Text = text;
        if (_forcedUpdateOn && TxtForcedProgress != null) TxtForcedProgress.Text = text;
    }

    private void ApplyDownloadedUpdate()
    {
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
            _updateAssetUrl = "";   
            SetUpdateText("自动更新启动失败，点此打开下载页手动下载；右键跳过本版本。");
            InsertLog("自动更新启动失败，详见 play.log。");
            return;
        }
        InsertLog("正在应用更新：程序将退出并自动重启到新版……");
        if (_engine != null) StopPlaybackNow();
        Close();
    }

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
                    ShowForcedUpdate(r.LatestTag, r.ReleaseUrl, r.AssetUrl);
                    return;
                }

                _updateUrl = r.ReleaseUrl;
                _updateTag = r.LatestTag;
                _updateAssetUrl = r.AssetUrl;
                TxtUpdate.Text = r.Skipped
                    ? $"v{r.LatestTag}（当前 v{r.CurrentTag}）已被你跳过，点此仍可下载并更新；右键继续跳过。"
                    : $"发现新版本 v{r.LatestTag}（当前 v{r.CurrentTag}），点此自动下载并更新；右键跳过本版本。";
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

    private bool _forcedUpdateOn;      
    private int _quizFails;             

    private void FillAboutLinks()
    {
        if (TxtFreeNotice != null) TxtFreeNotice.Text = AutoUpdate.FreeNoticeShort;
        if (BtnAuthorSpace != null) ToolTip.SetTip(BtnAuthorSpace, AutoUpdate.AuthorSpaceUrl);
        if (BtnCopyQq != null) BtnCopyQq.Content = $"复制 QQ 群号 {AutoUpdate.QqGroupNumber}";
        if (TxtFreeNoticeBody != null) TxtFreeNoticeBody.Text = AutoUpdate.FreeNotice;
        if (TxtForcedNotice != null) TxtForcedNotice.Text = AutoUpdate.FreeNotice;
    }

    private void QuizConfirm_Click(object? sender, RoutedEventArgs e) => SubmitQuizAnswer();

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
        if (StartupOverlay != null) StartupOverlay.IsVisible = false;   
        InsertLog($"验证通过：本程序免费开源，作者是 B 站 {AutoUpdate.AuthorName}。");
        InsertLog($"作者 B 站：{AutoUpdate.AuthorSpaceUrlShort}　反馈 QQ 群：{AutoUpdate.QqGroupNumber}");
    }

    private void OpenAuthorSpace_Click(object? sender, RoutedEventArgs e)
    {
        AutoUpdate.OpenUrl(AutoUpdate.AuthorSpaceUrl);
        InsertLog($"已打开作者 B 站主页：{AutoUpdate.AuthorSpaceUrl}");
    }

    private void OpenAfdian_Click(object? sender, RoutedEventArgs e) => OpenAfdianPage();

    private void OpenAfdianPage()
    {
        AutoUpdate.OpenUrl(AutoUpdate.AfdianUrl);
        InsertLog($"已打开爱发电赞助页：{AutoUpdate.AfdianUrl}");
    }

    internal void OpenAfdianForSettings() => OpenAfdianPage();

    internal void OpenAuthorSpaceForSettings() => OpenAuthorSpace_Click(null, new RoutedEventArgs());

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
        if (MainBody != null) MainBody.IsEnabled = false;    
        Input.GlobalHotkeys.SetActive(Array.Empty<int>());   
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
            SetUpdateText("这个版本没有自动更新包，请点「手动下载（浏览器）」。");
            return;
        }
        await DownloadUpdateAsync();
    }

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
            SetUpdateText("已取消下载，点「重启并完成更新」重新下载。");
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

    private void LblWarn_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var m = _lastMapping;
        if (m == null || m.SkipCount == 0) return;
        ShowSkippedNotes(m);
    }

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

    private void ShowOverlayCountdown(int secondsLeft, string nextLabel = "")
    {
        if (!ShouldShowOverlay(paused: false)) return;
        var w = EnsureOverlay();
        w.ShowCountdown(secondsLeft, nextLabel);
        w.RestorePosition(_cfg?.OverlayX ?? -1, _cfg?.OverlayY ?? -1);
    }

    private void ShowOverlayProgress(PlaybackEngine eng)
    {
        if (!ShouldShowOverlay(eng.IsPaused)) return;
        var w = EnsureOverlay();
        w.SetSong(SongDisplayName);
        w.ShowProgress(eng.ElapsedSeconds, eng.TotalSeconds, eng.LoopCount, eng.IsPaused);
        w.SetQueueHint(QueueHintText());
        w.RestorePosition(_cfg?.OverlayX ?? -1, _cfg?.OverlayY ?? -1);
    }

    private string QueueHintText()
    {
        if (!_cfg.PlaylistEnabled) return "";

        bool usePlaylist = _sourceIsPlaylist && _playlist is { Count: > 0 };
        int count = usePlaylist ? _playlist!.Count : _folderFiles.Count;
        if (count == 0) return "";

        int pos = usePlaylist ? CurrentPlaylistIndex() : CurrentFolderIndex();
        string head = pos >= 0 ? $"{pos + 1}/{count}" : $"—/{count}";

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
        _overlayLastElapsed = -1;   
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

    private void AutoMinimize_Changed(object? sender, RoutedEventArgs e)
    {
        ScheduleSave();
    }

    private void ShowPreflight_Changed(object? sender, RoutedEventArgs e)
    {
        bool on = ChkShowPreflight.IsChecked == true;
        PreflightRow.IsVisible = on;
        if (_cfg != null) _cfg.ShowPreflight = on;
        ScheduleSave();
    }

    private void Theme_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;

        int mode = ThemeSwitch.Clamp(ThemeCombo.SelectedIndex);
        ThemeSwitch.Apply(mode);
        ApplyThemeColors();
        _settingsWindow?.RefreshThemeBrushes();   
        InsertLog($"皮肤：{ThemeSwitch.Names[mode]}");

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
        if (Roll != null) Roll.InvalidateVisual();   
        ApplyVoiceBrushes();                         
        RunPreflight();                              
        UpdateMidiStatus();                          
        if (!_busy) SetIdleHint();                   
        LblWarn.Foreground = _warnIsOk ? OkBrush : WarningBrush;
    }

    private SettingsWindow? _settingsWindow;

    private void Settings_Click(object? sender, RoutedEventArgs e) => OpenSettings();

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

    internal void ReturnAdvancedBody(Control body)
    {
        if (body.Parent == null) AdvancedStash.Child = body;
        _settingsWindow = null;
        SyncKeymapUi();
        ScheduleSave();
    }

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

    internal SettingsWindow? SettingsWindowForDev => _settingsWindow;

    internal string LoadPlaylistEntryForDev(string spec)
    {
        bool keepPreview = spec.EndsWith("|preview", StringComparison.OrdinalIgnoreCase);
        if (keepPreview) spec = spec.Substring(0, spec.Length - "|preview".Length);

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

    private void OnKeymapPath(KeymapProfile? saved, string message)
    {
        if (saved != null)
        {
            _keymap = saved;
            KeymapProfile.Current = saved;
            if (_cfg != null) _cfg.KeymapName = saved.Name;
            SyncKeymapUi();   
        }
        else
        {
            _keymap = KeymapProfile.Current ?? _keymap;
        }
        if (!string.IsNullOrEmpty(message)) InsertLog(message);
    }

    private string? ApplyProfileSettingsFromDialog(KeymapProfile profile)
    {
        if (profile == null) return null;
        ApplyProfileSettings(profile.Name);
        return $"速度 {SliderSpeed.Value:0}%、移调 {SliderTranspose.Value:0}、档位 {TimingCombo.SelectedIndex + 1}";
    }

    private void SyncKeymapUi()
    {
        _keymap = KeymapProfile.Current ?? _keymap;
        if (TxtKeymapName != null) TxtKeymapName.Text = _keymap.Name;
        KeymapProfile.Current = _keymap;
        if (TxtSeekNote != null) UpdateSeekNote();
        RefreshPreview(keepView: true);
    }

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

    private void OnMidiNote(int pitch, int velocity, bool down)
    {
        if (down) _livePlay.NoteOn(pitch, velocity);
        else _livePlay.NoteOff(pitch);
    }

    private void OnMidiObserved(LiveMapping map, int pitch, int velocity)
    {
        string text = map.Playable
            ? $"[MIDI] {Music.SolfegeName(pitch)} → {KeyLabelOf(map)}"
            : $"[MIDI] {Music.SolfegeName(pitch)} → 超出音域";
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

        if (EmptyHint != null) EmptyHint.IsVisible = _tracks.Count == 0;

        var okColor = OkBrush;
        var warnColor = WarningBrush;

        var rows = ActiveRows();
        if (rows.Count == 0 || _parsed == null)
        {
            LblMelody.Text = _parsed == null
                ? "按 F6 或点 ▶ 播放"
                : "勾「合」可多声部合奏";
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
        _lastMapping = m;   
        var voiceMap = BuildVoiceMap(raw);
        ApplyVoiceBrushes();

        _previewNotes = m.Notes;
        double totalSec = PreviewTotalSeconds;
        _previewSeconds = Math.Clamp(_previewSeconds, 0, totalSec);
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
            LblMelody.Text = $"合奏 {rows.Count} 个声部：冲突时编号小的优先";
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
        ClearPlaybackChrome();

        if (eng != null) AdvanceAfterFinish();
    }

    private PlayMode CurrentPlayMode =>
        _cfg.PlaylistEnabled ? PlayModeNames.Clamp(_cfg.PlayModeIndex) : PlayMode.Off;

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

    private void ApplyPlayModeUi()
    {
        bool on = _cfg.PlaylistEnabled;
        if (BtnPlayMode != null)
        {
            BtnPlayMode.IsVisible = on;
            if (on) BtnPlayMode.Content = PlayModeNames.ButtonText(CurrentPlayMode);
        }

        var items = new[] { ModeItemSequential, ModeItemListLoop, ModeItemShuffle, ModeItemRepeatOne, ModeItemOff };
        foreach (var item in items)
        {
            if (item == null) continue;
            item.IsChecked = int.TryParse(item.Tag as string, out int i) && i == (int)CurrentPlayMode;
        }
    }

    private void AdvanceAfterFinish()
    {
        if (CurrentPlayMode is PlayMode.Off or PlayMode.RepeatOne) return;

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

    private int CurrentFolderIndex()
    {
        string cur = _parsed?.FilePath ?? "";
        if (cur.Length == 0) return -1;
        return _folderFiles.FindIndex(f => string.Equals(f, cur, StringComparison.OrdinalIgnoreCase));
    }

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

        if (entry.Tracks.Count == 0)
        {
            SelectRowsForPlaylist(_tracks.ToList(), null);
            InsertLog("这份歌单没给这一首勾过音轨，按全部音轨弹（想挑就点曲目右边的按钮）。");
            return;
        }

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

        _selected = rows.Count > 0 ? rows[0] : (_tracks.Count > 0 ? _tracks[0] : null);
        for (int i = 0; i < _tracks.Count; i++)
            _tracks[i].IsMain = ReferenceEquals(_tracks[i], _selected);
        if (TrackList != null && _selected != null) TrackList.SelectedIndex = _tracks.IndexOf(_selected);

        SyncMixOrder();
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
        if (!_cfg.PlaylistEnabled) SetPlaylistEnabled(true);

        if (_playlistWindow != null)
        {
            _playlistWindow.Activate();
            return;
        }

        var win = new PlaylistWindow { ActiveName = _playlist?.Name ?? "" };
        win.EntryActivated += path =>
        {
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
        win.ActiveChanged += name => SetActivePlaylist(name);
        win.PreviewTracks += (path, picks) => PreviewTracks(path, picks);
        win.PreviewEnded += EndPreviewTracks;
        win.Closed += (_, _) =>
        {
            _playlistWindow = null;
            if (win.ActiveNameNow.Length > 0) SetActivePlaylist(win.ActiveNameNow);
        };

        _playlistWindow = win;
        win.SelectInitial();
        win.Show(this);
    }

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
        SetPlaylistEnabled(false);
    }

    private void EnablePlaylist_Changed(object? sender, RoutedEventArgs e)
    {
        if (_cfg == null || ChkEnablePlaylist == null) return;
        bool on = ChkEnablePlaylist.IsChecked == true;
        if (_cfg.PlaylistEnabled == on) return;
        SetPlaylistEnabled(on);
    }

    private void SetPlaylistEnabled(bool on)
    {
        _cfg.PlaylistEnabled = on;
        SaveSettings();
        if (ChkEnablePlaylist != null) ChkEnablePlaylist.IsChecked = on;
        RefreshRecentUi();
        RefreshPlaylistCard();
        ApplyPlayModeUi();
        UpdateSplitterLayout();
        InsertLog(on
            ? "已启用歌单：左栏显示歌单卡，也可以选演奏模式。"
            : "已关闭歌单：左栏只留曲库，放完一首就停。歌单文件没删。");
    }

    private void PlaylistList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_playlistSyncing) return;   
        if (PlaylistList?.SelectedItem is not ListBoxItem it) return;
        if (it.Tag is not int idx || _playlist == null) return;
        if (idx < 0 || idx >= _playlist.Count) return;

        string path = _playlist.Entries[idx].Path;
        bool wasPlaying = _engine is { IsRunning: true };
        Dispatcher.UIThread.Post(() => LoadPlaylistEntry(path, playNow: wasPlaying));
    }

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
        bool loaded = LoadMidiFile(entry.Path);   

        RefreshPlaylistCard();
        if (playNow && loaded) RequestPlay(skipCountdown: true);
    }

    private void RefreshPlaylistCard()
    {
        if (PlaylistCard == null || PlaylistList == null) return;   

        PlaylistCard.IsVisible = _cfg.PlaylistEnabled;
        if (!PlaylistCard.IsVisible)
        {
            UpdateSplitterLayout();
            return;
        }

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
                TxtPlaylistEmpty.Text = "点「管理…」加入曲目。";
                TxtPlaylistEmpty.IsVisible = total == 0;
            }

            int pos = CurrentPlaylistIndex();
            if (pos >= 0 && pos < shown) PlaylistList.SelectedIndex = pos;
        }
        finally { _playlistSyncing = false; }

        if (_playlistWindow != null)
        {
            _playlistWindow.PlayingPath = _parsed?.FilePath ?? "";
        }
    }

    private void BtnPlay_Click(object? sender, RoutedEventArgs e)
    {
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
        if (backendWarn != null) InsertLog(backendWarn);   

        if (_previewOn) StopPreviewAudio();

        var map = BuildMapping();
        if (map.InRangeCount == 0)
        {
            InsertLog("没有可演奏的音，无法播放。请调整“移调”或换一行。");
            onFail?.Invoke("键位方案里一个音都对应不上");
            return;
        }
        _playNotes = NoteMapper.Playable(map.Notes);
        _consecutiveFailures = 0;   
        _nextSongLabel = "";        

        SetBusy(true);
        _overlayMuted = false;   
        int cd = skipCountdown ? 0 : SelectedCountdownSeconds;
        if (cd > 0)
        {
            _countdownLeft = cd;
            UpdateCountdownText();
            string nextLabel = _nextSongLabel.Length > 0 ? $"下一首：{_nextSongLabel}" : "";
            ShowOverlayCountdown(cd, nextLabel);
            InsertLog($"{cd} 秒后开始，请切到目标窗口并装备乐器…");
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
        LblStatus.FontSize = ResourceFontSize("FontCountdown", 38);   
        LblStatus.Text = $"{_countdownLeft} 秒后开始，请切到目标程序并装备乐器（F6 可取消）";
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

        var engine = new PlaybackEngine();
        _engine = engine;
        engine.Log += s => UiPost(() => InsertLog(s));
        engine.Finished += () => UiPost(OnEngineFinished);

        double speed = SliderSpeed.Value / 100.0;
        bool loop = CurrentPlayMode == PlayMode.RepeatOne;
        _nextSongLabel = "";   

        if (!Input.InputSender.IsSupported)
            InsertLog("（当前平台不支持输入模拟，仅流程演示）");

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
        RunPreflight();

        string fgTitle = InputSender.ForegroundWindowTitle;
        InsertLog($"开始演奏；前台窗口：{(string.IsNullOrEmpty(fgTitle) ? "（读不到，可能未切到目标程序）" : fgTitle)}");
        LblStatus.Foreground = OkBrush;
        LblStatus.FontSize = ResourceFontSize("FontDisplay", 22);
        SetCountdownChrome(false);
        LblStatus.Text = "演奏中…";

        if (ChkAutoMinimize.IsChecked == true)
        {
            if (WindowState != WindowState.Minimized)
                WindowState = WindowState.Minimized;
        }
        else
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;   
            InsertLog("（未自动最小化：请点一下目标窗口让它获得焦点，否则按键发到本窗口）");
        }

        UpdateTransportUi();
        SliderProgress.IsEnabled = true;

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _uiTimer.Tick += (_, _) =>
        {
            var eng = _engine;
            if (eng == null || !eng.IsRunning) return;

            if (!_seeking)   
            {
                SliderProgress.Value = Math.Min(eng.ElapsedSeconds, SliderProgress.Maximum);
                TxtTime.Text = eng.LoopCount > 0
                    ? $"{eng.ElapsedSeconds:F1} / {eng.TotalSeconds:F1} s（第 {eng.LoopCount + 1} 遍）"
                    : $"{eng.ElapsedSeconds:F1} / {eng.TotalSeconds:F1} s";
                Roll?.SetPosition(eng.ElapsedSeconds);   
                UpdateSeekNote();
                UpdatePlaybackChrome(eng.ElapsedSeconds, eng.TotalSeconds);
            }
            if (!string.IsNullOrEmpty(eng.CurrentNote))
            {
                LblStatus.Foreground = OkBrush;
                LblStatus.Text = eng.CurrentNote;
            }
            PushOverlayThrottled(eng);   
        };
        _uiTimer.Start();
        _overlayLastElapsed = -1;   
        StartFocusGuard();   
        if (ChkOverlay.IsChecked == true)
            EnsureOverlay().SetNotes(_playNotes, engine.TotalSeconds);   
        ShowOverlayProgress(engine);   
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
        ClearPlaybackChrome();

        _uiTimer?.Stop();
        _uiTimer = null;
        InsertLog("已停止。");
        if (eng != null) InsertLog(eng.Probe.Summary());
        ForceReleaseKeysForGame();
        ResetUi();
    }

    private void Disclaimer_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is Border b) b.IsVisible = false;
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
        BtnOpen.IsEnabled = !busy || (_engine is { IsRunning: true, IsPaused: true });
        TrackList.IsEnabled = !busy;
        BtnPlayMode.IsEnabled = !busy;   
        ChkTrimLead.IsEnabled = !busy;
        ChkFoldOctave.IsEnabled = !busy;
        ChkAutoMinimize.IsEnabled = !busy;
        BtnPreview.IsEnabled = !busy;
        CountdownCombo.IsEnabled = !busy;
        if (BtnSettings != null) BtnSettings.IsEnabled = !busy;
        UpdateActionButtons();

        UpdateTransportUi();
        SliderSpeed.IsEnabled = true;
        SliderTranspose.IsEnabled = true;
    }

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
            WindowIcon? icon = null;
            try
            {
                using var s = Avalonia.Platform.AssetLoader.Open(
                    new Uri("avares://MidiKeyPlayer/Assets/app.ico"));
                icon = new WindowIcon(s);
            }
            catch
            {
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

    private async Task ConfirmExitWithEditsAsync()
    {
        if (_exitConfirming) return;
        _exitConfirming = true;
        try
        {
            if (!await ConfirmDiscardEditsAsync("退出程序")) return;
            _quitNow = true;   
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

        if (WindowState == WindowState.Normal)
        {
            _cfg.WindowW = (int)Math.Round(ClientSize.Width);
            _cfg.WindowH = (int)Math.Round(ClientSize.Height);
            _cfg.WindowX = Position.X;
            _cfg.WindowY = Position.Y;
        }
        _cfg.WindowMax = WindowState == WindowState.Maximized;

        if (LeftColumn != null && LeftColumn.RowDefinitions.Count > 5)
        {
            _cfg.LeftWidth = (int)Math.Round(LeftColumn.Width);
            _cfg.FolderH = LeftColumn.RowDefinitions[1].Height.IsAbsolute && FolderCard is { IsVisible: true }
                ? (int)Math.Round(FolderCard.Bounds.Height) : 0;
            _cfg.PlaylistH = LeftColumn.RowDefinitions[3].Height.IsAbsolute && PlaylistCard is { IsVisible: true }
                ? (int)Math.Round(PlaylistCard.Bounds.Height) : 0;
        }
        if (RightColumn != null && RightColumn.RowDefinitions.Count > 3
            && RightColumn.RowDefinitions[3].Height.IsAbsolute)
            _cfg.OptionsH = (int)Math.Round(RightColumn.RowDefinitions[3].ActualHeight);

        SaveSettings();

        DevCleanUpForExit();
    }

    internal void DevCleanUpForExit()
    {
        _updateCts?.Cancel();   
        _overlay?.Close();
        _overlay = null;
        _countdownTimer?.Stop();
        _liveTimer?.Stop();
        _uiTimer?.Stop();
        _saveDeb?.Stop();     
        _previewDeb?.Stop();
        _engine?.Stop();
        StopPreviewAudio();
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

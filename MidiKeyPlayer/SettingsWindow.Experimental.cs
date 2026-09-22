using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MidiKeyPlayer.Engine;

namespace MidiKeyPlayer;

/// <summary>
/// 设置窗口「实验功能」页：远程同演。
///
/// 这一页只做三件事，同步逻辑全在 <see cref="SyncSession"/> 里：
/// 1. 把界面操作翻成对会话的调用（建房、加入、改声部、开演…）。
/// 2. 把会话推上来的名单翻成界面文字。
/// 3. 每 2 秒把名单刷新一次（名单只在有人进出时变，刷新只是为了显示得及时）。
///
/// 刻意没有的东西：不测延迟、不显示往返时间、不做漂移校正、没有周期上报。
/// </summary>
public partial class SettingsWindow
{
    private SyncSession? _sync;

    private readonly ObservableCollection<SyncPeerRow> _syncPeerRows = new();
    private readonly ObservableCollection<SyncVoiceRow> _syncVoiceRows = new();

    /// <summary>名单刷新计时器。只读名单，不发消息。</summary>
    private DispatcherTimer? _syncUiTimer;

    /// <summary>本机曲目指纹（加入房间时算一次，用于核对两边是不是同一份文件）。</summary>
    private string _syncFingerprint = "";

    /// <summary>自己的移调。房主统一规定时以统一值为准。</summary>
    private int _syncTranspose;

    /// <summary>正在按程序改控件，别把这次变化当成用户操作。</summary>
    private bool _syncUiUpdating;

    // ================= 初始化与收尾 =================

    private void InitSyncPage()
    {
        if (SyncPeerList != null) SyncPeerList.ItemsSource = _syncPeerRows;
        if (SyncVoiceList != null) SyncVoiceList.ItemsSource = _syncVoiceRows;
        if (SliderSyncTranspose != null) SliderSyncTranspose.Value = _syncTranspose;

        var cfg = _mainWindow?.SyncConfigForSettings;
        if (cfg != null)
        {
            if (TxtSyncName != null && cfg.SyncPeerName.Length > 0) TxtSyncName.Text = cfg.SyncPeerName;
            if (TxtSyncBroker != null && cfg.SyncBroker.Length > 0) TxtSyncBroker.Text = cfg.SyncBroker;
            if (TxtSyncRoom != null && cfg.SyncRoomName.Length > 0) TxtSyncRoom.Text = cfg.SyncRoomName;
            if (CmbSyncCountdown != null)
                CmbSyncCountdown.SelectedIndex = Math.Clamp(cfg.SyncCountdownIndex, 0, 4);
        }
        if (CmbSyncCountdown != null && CmbSyncCountdown.SelectedIndex < 0) CmbSyncCountdown.SelectedIndex = 1;
        RefreshSyncUi();
    }

    /// <summary>关窗收尾：把会话与计时器收干净。不收的话窗口关了还在连服务器。</summary>
    private void CloseSyncSession()
    {
        _syncUiTimer?.Stop();
        _syncUiTimer = null;
        if (_sync != null)
        {
            _sync.Dispose();
            _sync = null;
        }
        _mainWindow?.ClearSyncMaskForSettings();
    }

    // ================= 界面刷新 =================

    private void RefreshSyncUi()
    {
        bool connected = _sync != null && _sync.Connection is not SyncConnState.Offline;

        if (SyncConnectCard != null) SyncConnectCard.IsVisible = !connected;
        if (SyncRoomCard != null) SyncRoomCard.IsVisible = connected;
        if (!connected) return;

        var session = _sync!;
        bool isHost = session.IsHost;

        if (TxtSyncStatus != null)
        {
            string conn = session.Connection switch
            {
                SyncConnState.Connecting => "正在连接…",
                SyncConnState.Reconnecting => "连接中断，正在重连…",
                SyncConnState.Online => "已连接",
                _ => "未连接",
            };
            int ready = 0;
            foreach (var p in session.SnapshotPeers()) if (p.Ready) ready++;
            TxtSyncStatus.Text = $"{conn}　{session.SnapshotPeers().Count} 人　{ready} 人就绪";
        }

        if (SyncInviteRow != null) SyncInviteRow.IsVisible = isHost;
        if (TxtSyncInviteShown != null && isHost) TxtSyncInviteShown.Text = session.InviteText;

        if (TxtSyncTrack != null)
        {
            string local = _syncFingerprint.Length == 0 ? "（本机还没载入 MIDI）" : _syncFingerprint;
            TxtSyncTrack.Text = _syncFingerprint.Length == 0
                ? $"曲目：{local}。先导入一首 MIDI，再进来。"
                : $"本机曲目指纹 {local}。请自己确认与其他人是同一份文件。";
        }

        // 房主才能按的按钮
        if (BtnSyncStart != null) BtnSyncStart.IsEnabled = isHost;
        if (BtnSyncPause != null) BtnSyncPause.IsEnabled = isHost && session.Transport == SyncTransport.Playing;
        if (BtnSyncResume != null) BtnSyncResume.IsEnabled = isHost && session.Transport == SyncTransport.Paused;
        if (BtnSyncSeek != null) BtnSyncSeek.IsEnabled = isHost;
        if (BtnSyncStop != null) BtnSyncStop.IsEnabled = isHost && session.Transport != SyncTransport.Idle;

        // 统一移调只有房主能按；被别人统一之后，自己的滑块禁用
        if (BtnSyncUnifyTranspose != null) BtnSyncUnifyTranspose.IsVisible = isHost;
        if (BtnSyncClearTranspose != null) BtnSyncClearTranspose.IsVisible = isHost;
        bool locked = session.UnifiedTranspose != null;
        if (SliderSyncTranspose != null) SliderSyncTranspose.IsEnabled = !locked && !isHost;
        if (ChkSyncReady != null && ChkSyncReady.IsChecked != (session.SelfPeer()?.Ready ?? false))
        {
            _syncUiUpdating = true;
            ChkSyncReady.IsChecked = session.SelfPeer()?.Ready ?? false;
            _syncUiUpdating = false;
        }

        int shown = session.SelfTranspose;
        if (TxtSyncTranspose != null) TxtSyncTranspose.Text = shown > 0 ? $"+{shown}" : shown.ToString();
        if (SliderSyncTranspose != null && Math.Abs(SliderSyncTranspose.Value - shown) > 0.5)
        {
            _syncUiUpdating = true;
            SliderSyncTranspose.Value = shown;
            _syncUiUpdating = false;
        }

        if (TxtSyncSpeed != null)
        {
            string state = session.Transport switch
            {
                SyncTransport.Playing => "演奏中",
                SyncTransport.Paused => "已暂停",
                SyncTransport.Stopped => "已停止",
                _ => "等待开演",
            };
            string unified = session.UnifiedTranspose == null
                ? ""
                : $"　（房主统一移调 {session.UnifiedTranspose:+#;-#;0}）";
            TxtSyncSpeed.Text = $"{state}　倒数 {session.CountdownSec} 秒　速度 {session.Speed * 100:F0}%{unified}";
        }

        if (TxtSyncHint != null)
            TxtSyncHint.Text = isHost
                ? "你是房主。把邀请串发给朋友，等他们都进来、都点「我就绪了」，再点「开演」。"
                : "你是房间成员。等房主开演即可。移调只有房主能统一规定。";

        RebuildSyncPeerRows();
        RebuildSyncVoiceRows();
    }

    private void RebuildSyncPeerRows()
    {
        if (_sync == null) return;
        var peers = _sync.SnapshotPeers();
        string selfId = _sync.PeerId;
        _syncPeerRows.Clear();
        foreach (var p in peers)
        {
            _syncPeerRows.Add(new SyncPeerRow
            {
                Mark = p.Id == selfId ? "●" : "",
                Name = p.Name + (p.Id == selfId ? "（我）" : ""),
                Voices = p.Voice.Length > 0 ? p.Voice : "（未选）",
                Transpose = p.Xpose == 0 ? "移调 0" : $"移调 {p.Xpose:+#;-#;0}",
                ReadyText = p.Ready ? "已就绪" : "未就绪",
            });
        }
    }

    /// <summary>
    /// 重建声部勾选表。声部由每个人自己选，所以这里不做占用检查 ——
    /// 名单上看得见各自选了什么，重了是人自己看着办。
    /// </summary>
    private void RebuildSyncVoiceRows()
    {
        if (_sync == null) return;
        int voiceCount = _mainWindow?.SyncVoiceCountForSettings() ?? 0;
        var mine = new HashSet<int>(ParseVoices(_sync.SelfVoice));

        _syncVoiceRows.Clear();
        for (int v = 0; v < voiceCount; v++)
        {
            string name = _mainWindow?.SyncVoiceNameForSettings(v) ?? $"声部 {v + 1}";
            _syncVoiceRows.Add(new SyncVoiceRow
            {
                Index = v,
                Label = $"{v + 1}. {name}",
                IsMine = mine.Contains(v),
                Tip = "勾上就是由你弹这个声部。别人也可以勾同一个，名单上看得见。",
            });
        }
    }

    /// <summary>把 "1、3" 这样的显示文本解析成声部序号。1 起改成 0 起。</summary>
    internal static List<int> ParseVoices(string text)
    {
        var list = new List<int>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (string part in text.Split('、', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part.Trim(), out int n) && n >= 1) list.Add(n - 1);
        }
        return list;
    }

    /// <summary>把声部序号拼成显示文本，例如 0、2 → "1、3"。</summary>
    internal static string FormatVoices(IEnumerable<int> voices)
    {
        var list = new List<int>(voices);
        if (list.Count == 0) return "";
        list.Sort();
        return string.Join("、", list.ConvertAll(v => (v + 1).ToString()));
    }

    // ================= 建房与加入 =================

    private void SyncHost_Click(object? sender, RoutedEventArgs e)
    {
        var cfg = _mainWindow?.SyncConfigForSettings;
        string roomName = (TxtSyncRoom?.Text ?? "").Trim();
        string password = TxtSyncPass?.Text ?? "";
        string host = SyncRoomInfo.NormalizeHost(TxtSyncBroker?.Text);
        if (host.Length == 0) host = SyncRoomInfo.DefaultRelayHost;

        if (host.Length == 0)
        {
            SaySync("还没有中转站地址。展开「高级：中转站地址」填上你的 Worker 地址。");
            return;
        }
        if (!SyncRoomInfo.IsValidRoomName(roomName))
        {
            SaySync("房间名要 1 到 32 个字符。");
            return;
        }
        if (!SyncRoomInfo.IsValidPassword(password))
        {
            SaySync("密码太长了（上限 128 个字符）。");
            return;
        }

        var room = new SyncRoomInfo { Host = host, RoomName = roomName, Password = password };
        if (cfg != null)
        {
            cfg.SyncRoomName = roomName;
            cfg.SyncBroker = host;
        }
        StartSyncSession(room, isHost: true);
    }

    private void SyncJoin_Click(object? sender, RoutedEventArgs e)
    {
        string text = TxtSyncInvite?.Text ?? "";
        if (!SyncRoomInfo.TryParse(text, out var room, out string error) || room == null)
        {
            SaySync(error);
            return;
        }
        StartSyncSession(room, isHost: false);
    }

    /// <summary>开一次会话。房主用界面上填的地址，成员用邀请串里带的地址。</summary>
    private void StartSyncSession(SyncRoomInfo room, bool isHost)
    {
        CloseSyncSession();

        var cfg = _mainWindow?.SyncConfigForSettings;
        string name = (TxtSyncName?.Text ?? "").Trim();
        if (name.Length == 0) name = Environment.UserName;

        string peerId = cfg?.SyncPeerId ?? "";
        if (peerId.Length == 0)
        {
            peerId = SyncRoomInfo.NewPeerId();
            if (cfg != null) cfg.SyncPeerId = peerId;
        }
        if (cfg != null)
        {
            cfg.SyncPeerName = name;
            cfg.SyncBroker = room.Host;
            cfg.SyncRoomName = room.RoomName;
            cfg.SyncIntroSeen = true;
            _mainWindow?.SaveConfigForSettings();
        }

        var session = new SyncSession(room, peerId, name, isHost);
        if (CmbSyncCountdown != null) session.CountdownSec = CountdownFromIndex(CmbSyncCountdown.SelectedIndex);
        _sync = session;

        session.ConnectionChanged += OnSyncConnectionChanged;
        session.RoomChanged += OnSyncRoomChanged;
        session.StartRequested += OnSyncStartRequested;
        session.PauseRequested += OnSyncPauseRequested;
        session.ResumeRequested += OnSyncResumeRequested;
        session.SeekRequested += OnSyncSeekRequested;
        session.SpeedRequested += OnSyncSpeedRequested;
        session.StopRequested += OnSyncStopRequested;
        session.Log += OnSyncLog;
        session.Faulted += OnSyncFaulted;

        // 声部编号全场一致：按文件里的列表顺序编号，各人勾自己那份。
        _mainWindow?.PrepareSyncTracksForSettings();
        _syncFingerprint = _mainWindow?.SyncTrackFingerprintForSettings() ?? "";

        session.SetSelfVoice(FormatVoices(CurrentVoiceSelection()));
        session.Connect(FormatVoices(CurrentVoiceSelection()));
        PushMaskToMainWindow();

        StartSyncUiTimer();
        RefreshSyncUi();
    }

    private void SyncLeave_Click(object? sender, RoutedEventArgs e)
    {
        CloseSyncSession();
        RefreshSyncUi();
    }

    private static int CountdownFromIndex(int index)
        => index switch { 0 => 2, 1 => 3, 2 => 5, 3 => 10, 4 => 15, _ => 3 };

    private void SyncCountdown_Changed(object? sender, SelectionChangedEventArgs e)
    {
        int sec = CountdownFromIndex(CmbSyncCountdown?.SelectedIndex ?? 1);
        if (_sync != null) _sync.CountdownSec = sec;
        var cfg = _mainWindow?.SyncConfigForSettings;
        if (cfg != null)
        {
            cfg.SyncCountdownIndex = Math.Clamp(CmbSyncCountdown?.SelectedIndex ?? 1, 0, 4);
            _mainWindow?.SaveConfigForSettings();
        }
        RefreshSyncUi();
    }

    private async void SyncCopyInvite_Click(object? sender, RoutedEventArgs e)
    {
        if (_sync == null) return;
        bool ok = _mainWindow != null && await _mainWindow.CopyTextForSettingsAsync(this, _sync.InviteText);
        if (TxtSyncCopied != null) TxtSyncCopied.Text = ok ? "已复制" : "复制失败，请手动选中那一行";
    }

    // ================= 事件接线 =================

    private void OnSyncConnectionChanged(SyncConnState state)
        => Dispatcher.UIThread.Post(() => { RefreshSyncUi(); PushMaskToMainWindow(); });

    private void OnSyncRoomChanged()
        => Dispatcher.UIThread.Post(() => { RefreshSyncUi(); PushMaskToMainWindow(); });

    private void OnSyncLog(string message)
        => Dispatcher.UIThread.Post(() => _mainWindow?.InsertSyncLogForSettings(message));

    private void OnSyncFaulted(string message)
        => Dispatcher.UIThread.Post(() =>
        {
            _mainWindow?.InsertSyncLogForSettings(message);
            if (TxtSyncHint != null) TxtSyncHint.Text = message;
            RefreshSyncUi();
        });

    private void OnSyncStartRequested(double positionSec)
        => Dispatcher.UIThread.Post(() =>
        {
            _mainWindow?.StartSyncPlaybackForSettings(positionSec);
            RefreshSyncUi();
        });

    private void OnSyncPauseRequested()
        => Dispatcher.UIThread.Post(() => { _mainWindow?.PauseSyncPlaybackForSettings(); RefreshSyncUi(); });

    private void OnSyncResumeRequested()
        => Dispatcher.UIThread.Post(() => { _mainWindow?.ResumeSyncPlaybackForSettings(); RefreshSyncUi(); });

    private void OnSyncSeekRequested(double positionSec)
        => Dispatcher.UIThread.Post(() => { _mainWindow?.SeekSyncPlaybackForSettings(positionSec); RefreshSyncUi(); });

    private void OnSyncSpeedRequested(double speed)
        => Dispatcher.UIThread.Post(() => { _mainWindow?.SetSyncSpeedForSettings(speed); RefreshSyncUi(); });

    private void OnSyncStopRequested()
        => Dispatcher.UIThread.Post(() => { _mainWindow?.StopSyncPlaybackForSettings(); RefreshSyncUi(); });

    // ================= 声部与移调 =================

    /// <summary>界面上勾了哪几个声部（0 起序号）。</summary>
    private List<int> CurrentVoiceSelection()
    {
        var list = new List<int>();
        foreach (var row in _syncVoiceRows) if (row.IsMine) list.Add(row.Index);
        return list;
    }

    private void SyncVoiceToggle_Click(object? sender, RoutedEventArgs e)
    {
        if (_sync == null) return;
        string voices = FormatVoices(CurrentVoiceSelection());
        _sync.SetSelfVoice(voices);
        PushMaskToMainWindow();
        RefreshSyncUi();
    }

    private void SyncTranspose_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncUiUpdating) return;
        int t = (int)(SliderSyncTranspose?.Value ?? 0);
        _syncTranspose = t;
        if (TxtSyncTranspose != null) TxtSyncTranspose.Text = t > 0 ? $"+{t}" : t.ToString();
        _sync?.SetSelfTranspose(t);
        PushMaskToMainWindow();
    }

    /// <summary>房主：把自己当前的移调规定给全场。</summary>
    private void SyncUnifyTranspose_Click(object? sender, RoutedEventArgs e)
    {
        if (_sync is not { IsHost: true }) return;
        _sync.HostSetUnifiedTranspose(_syncTranspose);
        RefreshSyncUi();
    }

    /// <summary>房主：取消统一移调，各人回到自己的设置。</summary>
    private void SyncClearTranspose_Click(object? sender, RoutedEventArgs e)
    {
        if (_sync is not { IsHost: true }) return;
        _sync.HostSetUnifiedTranspose(null);
        RefreshSyncUi();
    }

    private void SyncReady_Click(object? sender, RoutedEventArgs e)
    {
        if (_sync == null || _syncUiUpdating) return;
        _sync.SetSelfReady(ChkSyncReady?.IsChecked == true);
        RefreshSyncUi();
    }

    // ================= 演出控制 =================

    private void SyncStart_Click(object? sender, RoutedEventArgs e)
    {
        if (_sync is not { IsHost: true }) return;
        if (_syncFingerprint.Length == 0)
        {
            SaySync("本机还没载入 MIDI，无法开演。");
            return;
        }
        if (!_sync.AllReady() && TxtSyncHint != null)
            TxtSyncHint.Text = "还有人没就绪，仍然开演。";
        _sync.HostStart(_mainWindow?.SyncProgressSecondsForSettings() ?? 0);
        RefreshSyncUi();
    }

    private void SyncPause_Click(object? sender, RoutedEventArgs e)
    {
        _sync?.HostPause();
        RefreshSyncUi();
    }

    private void SyncResume_Click(object? sender, RoutedEventArgs e)
    {
        _sync?.HostResume(_mainWindow?.SyncProgressSecondsForSettings() ?? 0);
        RefreshSyncUi();
    }

    private void SyncSeek_Click(object? sender, RoutedEventArgs e)
    {
        _sync?.HostSeek(_mainWindow?.SyncProgressSecondsForSettings() ?? 0);
        RefreshSyncUi();
    }

    private void SyncStop_Click(object? sender, RoutedEventArgs e)
    {
        _sync?.HostStop();
        RefreshSyncUi();
    }

    /// <summary>展开 / 收起「怎么看这个功能」那段说明。</summary>
    private void SyncIntro_Click(object? sender, RoutedEventArgs e)
    {
        if (TxtSyncIntro == null) return;
        TxtSyncIntro.IsVisible = !TxtSyncIntro.IsVisible;
    }

    // ================= 小工具 =================

    /// <summary>往页面上那行提示写字。没地方写就记日志。
    /// 名字不叫 Say：键位页已经有一个 Say（写键位页的提示行），同类里重名会编译不过。</summary>
    private void SaySync(string message)
    {
        if (TxtSyncJoinHint != null) TxtSyncJoinHint.Text = message;
        else _mainWindow?.InsertSyncLogForSettings("远程同演：" + message);
    }

    /// <summary>
    /// 每 2 秒刷一次界面。**只读名单，不发任何消息** ——
    /// 名单变的时候服务器已经推过新名单了，这个计时器只是让"连接中断"这类状态显示得及时。
    /// </summary>
    private void StartSyncUiTimer()
    {
        _syncUiTimer?.Stop();
        _syncUiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _syncUiTimer.Tick += (_, _) => RefreshSyncUi();
        _syncUiTimer.Start();
    }

    /// <summary>把本机声部与移调推给主窗。</summary>
    private void PushMaskToMainWindow()
    {
        var session = _sync;
        if (session == null)
        {
            _mainWindow?.ClearSyncMaskForSettings();
            return;
        }
        _mainWindow?.ApplySyncMaskForSettings(ParseVoices(session.SelfVoice), session.SelfTranspose);
    }

    /// <summary>【开发用】切到这一页后填一套假的房间状态，供界面快照拍出有内容的一页。</summary>
    internal void FillSyncPageForDev()
    {
        if (SyncConnectCard != null) SyncConnectCard.IsVisible = false;
        if (SyncRoomCard != null) SyncRoomCard.IsVisible = true;
        if (TxtSyncStatus != null) TxtSyncStatus.Text = "已连接　3 人　2 人就绪";
        if (SyncInviteRow != null) SyncInviteRow.IsVisible = true;
        if (TxtSyncInviteShown != null)
            TxtSyncInviteShown.Text = "mkp://midikeyplayer-sync.example.workers.dev|小明的琴房|letmein";
        if (TxtSyncTrack != null)
            TxtSyncTrack.Text = "本机曲目指纹 3f2a91c4d8e05b76。请自己确认与其他人是同一份文件。";
        if (TxtSyncHint != null)
            TxtSyncHint.Text = "你是房主。把邀请串发给朋友，等他们都进来、都点「我就绪了」，再点「开演」。";
        if (TxtSyncSpeed != null)
            TxtSyncSpeed.Text = "演奏中　倒数 3 秒　速度 100%　（房主统一移调 -5）";
        if (TxtSyncTranspose != null) TxtSyncTranspose.Text = "-5";
        if (SliderSyncTranspose != null) { SliderSyncTranspose.Value = -5; SliderSyncTranspose.IsEnabled = false; }
        if (ChkSyncReady != null) ChkSyncReady.IsChecked = true;
        if (CmbSyncCountdown != null) CmbSyncCountdown.SelectedIndex = 1;
        if (TxtSyncJoinHint != null) TxtSyncJoinHint.Text = "";
        if (BtnSyncUnifyTranspose != null) BtnSyncUnifyTranspose.IsVisible = true;
        if (BtnSyncClearTranspose != null) BtnSyncClearTranspose.IsVisible = true;

        _syncPeerRows.Clear();
        _syncPeerRows.Add(new SyncPeerRow { Mark = "●", Name = "小明（我）", Voices = "1、3", Transpose = "移调 -5", ReadyText = "已就绪" });
        _syncPeerRows.Add(new SyncPeerRow { Mark = "", Name = "小红", Voices = "2、4", Transpose = "移调 -5", ReadyText = "已就绪" });
        _syncPeerRows.Add(new SyncPeerRow { Mark = "", Name = "老王", Voices = "（未选）", Transpose = "移调 0", ReadyText = "未就绪" });

        _syncVoiceRows.Clear();
        string[] names = { "主旋律", "和声", "贝斯", "鼓" };
        for (int v = 0; v < names.Length; v++)
        {
            _syncVoiceRows.Add(new SyncVoiceRow
            {
                Index = v,
                Label = $"{v + 1}. {names[v]}",
                IsMine = v == 0 || v == 2,
                Tip = "勾上就是由你弹这个声部。别人也可以勾同一个，名单上看得见。",
            });
        }
    }
}

/// <summary>名单的一行。</summary>
public sealed class SyncPeerRow
{
    public string Mark { get; init; } = "";
    public string Name { get; init; } = "";
    public string Voices { get; init; } = "";
    public string Transpose { get; init; } = "";
    public string ReadyText { get; init; } = "";
}

/// <summary>声部勾选表的一行。</summary>
public sealed class SyncVoiceRow
{
    public int Index { get; init; }
    public string Label { get; init; } = "";
    public bool IsMine { get; set; }
    public string Tip { get; init; } = "";
}

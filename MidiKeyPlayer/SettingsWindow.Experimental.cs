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
        // 连接之后收起顶上的说明卡：它会把这页底部的「下一步做什么」与演出按钮
        // 挤到滚动条外面 —— 那两样才是暂停之后要看的东西。
        if (SyncIntroCard != null) SyncIntroCard.IsVisible = !connected;
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

        // 演出控制：只按「现在是什么状态」决定按钮与提示，见 ApplySyncTransportUi
        ApplySyncTransportUi(BuildSyncUiState(session, isHost));

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
            // 倒数报「还剩几秒」，不报设置里那个总数：开演消息已经带回了剩余等待时间，
            // 报总数会让人以为刚点完开演。
            string state = session.Transport switch
            {
                SyncTransport.Playing when session.LeadInMs > 0 => $"倒数 {session.LeadInMs / 1000.0:F1} 秒",
                SyncTransport.Playing => "演奏中",
                SyncTransport.Paused => "已暂停",
                SyncTransport.Stopped => "已停止",
                _ => "等待开演",
            };
            string unified = session.UnifiedTranspose == null
                ? ""
                : $"　（房主统一移调 {session.UnifiedTranspose:+#;-#;0}）";
            TxtSyncSpeed.Text = $"{state}　速度 {session.Speed * 100:F0}%{unified}";
        }

        // 提示行由 ApplySyncTransportUi 按状态写（旧写法是一句固定的「再点开演」，
        // 停演之后还挂着，等于告诉用户一件已经做完的事）。

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
            // 把「还差多久才到全场开演时刻」一起交出去：各人收到的时刻不一样，
            // 这段等待就是用来抹平网络快慢的（会话那边已经减掉了传输耗时）。
            _mainWindow?.StartSyncPlaybackForSettings(positionSec, _sync?.LeadInMs ?? 0);
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

    /// <summary>
    /// 一个按钮管三件事：开演 / 暂停 / 继续。按一下先看现在是什么状态，再做对应的事。
    ///
    /// 为什么合并：旧界面把「开演 / 暂停 / 继续 / 跳转 / 停止」平铺成五个按钮，
    /// 暂停与继续互斥却并排摆着。暂停之后要在一排长得差不多的按钮里找「继续」，
    /// 找不到就以为卡住了。现在暂停之后，同一个大按钮自己写着「▶ 继续」。
    /// </summary>
    private void SyncPrimary_Click(object? sender, RoutedEventArgs e)
    {
        if (_sync is not { IsHost: true } session) return;
        switch (session.Transport)
        {
            case SyncTransport.Playing:
                session.HostPause();
                break;
            case SyncTransport.Paused:
                session.HostResume(_mainWindow?.SyncProgressSecondsForSettings() ?? 0);
                break;
            default:
                if (_syncFingerprint.Length == 0)
                {
                    SaySync("本机还没载入 MIDI，无法开演。先回主界面导入一首。");
                    return;
                }
                // 允许抢开演，但要留一条日志：之后对不上拍时能看出是少人等过。
                if (!session.AllReady())
                    _mainWindow?.InsertSyncLogForSettings("远程同演：还有人没就绪，房主仍然开演。");
                session.HostStart(_mainWindow?.SyncProgressSecondsForSettings() ?? 0);
                break;
        }
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

    // ================= 状态 → 界面 =================
    //
    // 演出控制只认「现在处于哪个状态」：主按钮按哪个、次要按钮能不能按、下一步做什么。
    // 真会话（RefreshSyncUi）与开发快照（FillSyncPageForDev）走同一个入口，
    // 所以快照拍到的就是真状态下用户会看到的界面。

    /// <summary>一次界面判定要用到的全部状态。</summary>
    private sealed class SyncUiState
    {
        public bool IsHost;
        public SyncTransport Transport = SyncTransport.Idle;
        public bool HasTrack;
        public int Peers;
        public int Ready;
        public string NotReadyNames = "";
        public bool LeadIn;
    }

    private SyncUiState BuildSyncUiState(SyncSession session, bool isHost)
    {
        var peers = session.SnapshotPeers();
        int ready = 0;
        var notReady = new List<string>();
        foreach (var p in peers)
        {
            if (p.Ready) ready++;
            else notReady.Add(p.Name.Length > 0 ? p.Name : "（没写名字）");
        }
        return new SyncUiState
        {
            IsHost = isHost,
            Transport = session.Transport,
            HasTrack = _syncFingerprint.Length > 0,
            Peers = peers.Count,
            Ready = ready,
            NotReadyNames = string.Join("、", notReady),
            LeadIn = session.Transport == SyncTransport.Playing && session.LeadInMs > 0,
        };
    }

    private void ApplySyncTransportUi(SyncUiState s)
    {
        if (BtnSyncPrimary != null)
        {
            BtnSyncPrimary.IsVisible = s.IsHost;
            BtnSyncPrimary.Content = s.Transport switch
            {
                SyncTransport.Playing => "暂停",
                SyncTransport.Paused => "▶ 继续",
                SyncTransport.Stopped => "重新开演",
                _ => "开演",
            };
            BtnSyncPrimary.IsEnabled = s.IsHost && s.Transport switch
            {
                SyncTransport.Playing => true,      // 播放中：暂停
                SyncTransport.Paused => true,       // 暂停中：继续
                _ => s.HasTrack,                    // 空闲 / 已停止：开演，得先载入曲目
            };
            Avalonia.Controls.ToolTip.SetTip(BtnSyncPrimary, s.IsHost
                ? "按当前状态来：没开演就是开演，演奏中就是暂停，暂停中就是继续"
                : null);
        }
        // 跳转与停止是次要动作：只有正在演或暂停中才有意义。
        // 成员看不到这三个按钮，改看右边那行字 —— 一排灰按钮只会让人以为自己点错了。
        bool hostCanControl = s.IsHost && s.Transport is SyncTransport.Playing or SyncTransport.Paused;
        if (BtnSyncSeek != null)
        {
            BtnSyncSeek.IsVisible = s.IsHost;
            BtnSyncSeek.IsEnabled = hostCanControl;
        }
        if (BtnSyncStop != null)
        {
            BtnSyncStop.IsVisible = s.IsHost;
            BtnSyncStop.IsEnabled = hostCanControl;
        }
        if (TxtSyncMemberControl != null) TxtSyncMemberControl.IsVisible = !s.IsHost;
        if (TxtSyncHint != null) TxtSyncHint.Text = SyncHintText(s);
    }

    /// <summary>
    /// 「下一步做什么」。房主与成员分开写：成员那三个按钮是隐藏的，
    /// 必须有一句话明确告诉他「不用你操作、等房主」，否则他只能猜。
    /// </summary>
    private static string SyncHintText(SyncUiState s)
    {
        switch (s.Transport)
        {
            case SyncTransport.Playing when s.LeadIn:
                return s.IsHost ? "倒数中 —— 到点全场一起开始。" : "倒数中 —— 到点跟大家一起开始。";
            case SyncTransport.Playing:
                return s.IsHost
                    ? "演奏中 —— 要停就点「暂停」；要挪位置，先拖主界面的进度条再点「跳转」。"
                    : "演奏中 —— 暂停 / 继续 / 跳转 / 停止都由房主控制，你不用操作。";
            case SyncTransport.Paused:
                return s.IsHost
                    ? "已暂停 —— 点「▶ 继续」接着弹（会再倒数几秒），或点「停止」结束本轮。"
                    : "已暂停 —— 等房主点「继续」，你不用操作。";
            case SyncTransport.Stopped:
                return s.IsHost
                    ? "已停止 —— 点「重新开演」从当前位置再来一轮。"
                    : "已停止 —— 等房主重新开演。";
        }

        // 还没开演
        if (!s.HasTrack) return "主界面还没载入 MIDI。先回主界面导入一首，再回来开演。";
        if (!s.IsHost) return "等房主开演。你先勾好自己的声部，再点「我就绪了」。";
        if (s.Peers < 2) return "还没有别人进来 —— 点「复制邀请串」把朋友叫进来。";
        if (s.Ready < s.Peers) return $"还有 {s.Peers - s.Ready} 人没就绪：{s.NotReadyNames}。都就绪后点「开演」。";
        return "所有人都就绪 —— 点「开演」，倒数结束后全场一起开始。";
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

    /// <summary>
    /// 【开发用】切到这一页后填一套假的房间状态，供界面快照拍出有内容的一页。
    ///
    /// <paramref name="state"/> 选拍哪个演出状态：idle（默认，等待开演）/ playing / paused / stopped，
    /// 前面加 <c>member:</c> 拍成员视角（例：<c>member:paused</c>）。
    /// 状态 → 界面这一段走的是 <see cref="ApplySyncTransportUi"/>，与真会话完全同一条路。
    /// </summary>
    internal void FillSyncPageForDev(string state = "idle")
    {
        bool isHost = true;
        string name = state;
        int colon = state.IndexOf(':');
        if (colon >= 0)
        {
            isHost = !state[..colon].Equals("member", StringComparison.OrdinalIgnoreCase);
            name = state[(colon + 1)..];
        }

        var transport = name.ToLowerInvariant() switch
        {
            "playing" => SyncTransport.Playing,
            "paused" => SyncTransport.Paused,
            "stopped" => SyncTransport.Stopped,
            _ => SyncTransport.Idle,
        };

        if (SyncConnectCard != null) SyncConnectCard.IsVisible = false;
        if (SyncRoomCard != null) SyncRoomCard.IsVisible = true;
        if (SyncIntroCard != null) SyncIntroCard.IsVisible = false;   // 与真会话一致：连接后收起
        if (TxtSyncStatus != null) TxtSyncStatus.Text = "已连接　3 人　2 人就绪";
        if (SyncInviteRow != null) SyncInviteRow.IsVisible = isHost;
        if (TxtSyncInviteShown != null)
            TxtSyncInviteShown.Text = "mkp://midikeyplayer-sync.example.workers.dev|小明的琴房|letmein";
        if (TxtSyncTrack != null)
            TxtSyncTrack.Text = "本机曲目指纹 3f2a91c4d8e05b76。请自己确认与其他人是同一份文件。";
        if (TxtSyncSpeed != null)
        {
            string stateText = transport switch
            {
                SyncTransport.Playing => "演奏中",
                SyncTransport.Paused => "已暂停",
                SyncTransport.Stopped => "已停止",
                _ => "等待开演",
            };
            TxtSyncSpeed.Text = $"{stateText}　速度 100%　（房主统一移调 -5）";
        }
        if (TxtSyncTranspose != null) TxtSyncTranspose.Text = "-5";
        if (SliderSyncTranspose != null) { SliderSyncTranspose.Value = -5; SliderSyncTranspose.IsEnabled = false; }
        if (ChkSyncReady != null) ChkSyncReady.IsChecked = true;
        if (CmbSyncCountdown != null) CmbSyncCountdown.SelectedIndex = 1;
        if (TxtSyncJoinHint != null) TxtSyncJoinHint.Text = "";
        if (BtnSyncUnifyTranspose != null) BtnSyncUnifyTranspose.IsVisible = isHost;
        if (BtnSyncClearTranspose != null) BtnSyncClearTranspose.IsVisible = isHost;

        ApplySyncTransportUi(new SyncUiState
        {
            IsHost = isHost,
            Transport = transport,
            HasTrack = true,
            Peers = 3,
            Ready = 2,
            NotReadyNames = "老王",
        });

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

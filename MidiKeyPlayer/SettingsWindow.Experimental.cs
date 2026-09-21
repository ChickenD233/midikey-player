using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MidiKeyPlayer.Engine;
using MidiKeyPlayer.Persist;

namespace MidiKeyPlayer;

/// <summary>
/// 设置窗口「实验功能」页：远程同演。
///
/// 这一页只做三件事，真正的同步逻辑全在 <see cref="SyncSession"/> 里：
/// 1. 把界面操作翻成对会话的调用（建房、加入、改声部、改移调、开演…）。
/// 2. 把会话推上来的状态翻成界面文字（成员表、声部占用、进度差）。
/// 3. 驱动漂移校正：每 2 秒看一次本机进度，按判断结果去调
///    <see cref="PlaybackEngine.TrimRate"/> 或跳转。
///
/// 线程约定：<see cref="SyncSession"/> 的事件在后台线程触发，这里一律用
/// <see cref="Dispatcher.UIThread"/> 收回界面线程再改控件。
/// </summary>
public partial class SettingsWindow
{
    private SyncSession? _sync;

    /// <summary>成员表与声部表的显示数据源。用 ObservableCollection 是为了成员进出时自动刷新。</summary>
    private readonly ObservableCollection<SyncPeerRow> _syncPeerRows = new();
    private readonly ObservableCollection<SyncVoiceRow> _syncVoiceRows = new();

    /// <summary>漂移校正计时器：每 2 秒判断一次。</summary>
    private DispatcherTimer? _syncDriftTimer;

    /// <summary>
    /// 创建之前记下的曲目指纹。主机开演时用它，成员加入时用它核对。
    /// 空串 = 还没记（本机没载入 MIDI）。
    /// </summary>
    private string _syncFingerprint = "";

    /// <summary>最近一次漂移判断的偏差（秒）。界面上显示用。</summary>
    private double _syncLastError;

    /// <summary>是否已经就绪。</summary>
    private bool _syncSelfReady;

    // ================= 初始化与收尾 =================

    /// <summary>窗口构造末尾调用：把两个列表接上、清空状态、恢复上次的名字。</summary>
    private void InitSyncPage()
    {
        if (SyncPeerList != null) SyncPeerList.ItemsSource = _syncPeerRows;
        if (SyncVoiceList != null) SyncVoiceList.ItemsSource = _syncVoiceRows;

        var cfg = _mainWindow?.SyncConfigForSettings;
        if (cfg != null)
        {
            if (TxtSyncName != null && cfg.SyncPeerName.Length > 0) TxtSyncName.Text = cfg.SyncPeerName;
            if (TxtSyncBroker != null && cfg.SyncBroker.Length > 0) TxtSyncBroker.Text = cfg.SyncBroker;
        }
        RefreshSyncUi();
    }

    /// <summary>关窗收尾：把会话与计时器收干净。不关的话窗口关了还在连代理。</summary>
    private void CloseSyncSession()
    {
        _syncDriftTimer?.Stop();
        _syncDriftTimer = null;
        if (_sync != null)
        {
            UnhookSyncEvents(_sync);
            _sync.Dispose();
            _sync = null;
        }
        // 退出远程模式时把主窗的声部掩码与移调锁清掉，否则主窗会一直只弹那几个声部。
        _mainWindow?.ClearSyncMaskForSettings();
    }

    /// <summary>把界面上的会话状态刷新一遍。任何状态变化都走这里。</summary>
    private void RefreshSyncUi()
    {
        bool connected = _sync != null && _sync.Connection is not SyncConnState.Offline;
        bool playing = _sync?.Transport == SyncTransport.Playing;

        if (SyncConnectCard != null) SyncConnectCard.IsVisible = !connected;
        if (SyncRoomCard != null) SyncRoomCard.IsVisible = connected;
        if (!connected) return;

        var session = _sync!;
        bool isHost = session.IsHost;

        if (TxtSyncStatus != null)
        {
            string conn = session.Connection switch
            {
                SyncConnState.Connecting => "正在连接代理…",
                SyncConnState.Reconnecting => "连接中断，正在重连…",
                SyncConnState.Online => "已连接",
                _ => "未连接",
            };
            TxtSyncStatus.Text = $"{conn}　{_syncPeerRows.Count} 人　延迟 {session.RttMs:F0} 毫秒"
                + $"　抖动 {session.JitterMs:F0} 毫秒";
        }

        if (SyncInviteRow != null) SyncInviteRow.IsVisible = isHost;
        if (TxtSyncInviteShown != null && isHost) TxtSyncInviteShown.Text = session.InviteText;

        // 曲目指纹核对：主机还没载入曲子就说清楚；载入了就比对双方的
        if (TxtSyncTrack != null)
        {
            string local = _syncFingerprint.Length == 0 ? "（本机还没载入 MIDI）" : _syncFingerprint;
            string host = session.Fingerprint.Length == 0 ? "（主机还没载入 MIDI）" : session.Fingerprint;
            if (_syncFingerprint.Length == 0)
                TxtSyncTrack.Text = $"曲目：{local}。先导入同一份 MIDI 文件。";
            else if (session.Fingerprint.Length == 0)
                TxtSyncTrack.Text = $"曲目：等主机载入。本机指纹 {local}。";
            else if (_syncFingerprint == session.Fingerprint)
                TxtSyncTrack.Text = $"曲目已核对一致（{local}），总时长 {session.DurationSec:F1} 秒。";
            else
                TxtSyncTrack.Text = $"曲目不一致！本机 {local}，主机 {host}。请确认两边是同一份文件。";
        }

        // 主机的按钮只有主机能按，成员的置灰
        if (BtnSyncAutoAssign != null) BtnSyncAutoAssign.IsEnabled = isHost;
        if (BtnSyncStart != null) BtnSyncStart.IsEnabled = isHost;
        if (BtnSyncPause != null) BtnSyncPause.IsEnabled = isHost && playing;
        if (BtnSyncResume != null) BtnSyncResume.IsEnabled = isHost && session.Transport == SyncTransport.Paused;
        if (BtnSyncSeek != null) BtnSyncSeek.IsEnabled = isHost;
        if (BtnSyncStop != null) BtnSyncStop.IsEnabled = isHost && session.Transport != SyncTransport.Idle;

        if (TxtSyncSpeed != null)
        {
            string state = session.Transport switch
            {
                SyncTransport.Playing => "演奏中",
                SyncTransport.Paused => "已暂停",
                SyncTransport.Armed => "倒计时中",
                SyncTransport.Stopped => "已停止",
                _ => "等待开演",
            };
            TxtSyncSpeed.Text = $"{state}　速度 {session.Speed * 100:F0}%（在演奏参数里改速度会同步给所有人）"
                + (playing ? $"　进度差 {_syncLastError * 1000:+0;-0;0} 毫秒" : "");
        }

        if (TxtSyncHint != null)
            TxtSyncHint.Text = isHost
                ? "你是主机。把邀请串发给朋友，等他们都进来、都点「我就绪了」，再点「开演」。"
                : "你是成员。等主机开演。轮到你的声部会自己开始弹，不用再点播放。";

        RebuildSyncPeerRows();
        RebuildSyncVoiceRows();
    }

    /// <summary>把成员表重建成显示行。每次都整表重建：人数少（个位数），不值得做增量。</summary>
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
                Name = p.Name + (p.Id == (_sync.IsHost ? selfId : "") && _sync.IsHost ? "（主机）" : ""),
                Voices = SyncVoicePlan.Describe(p.Voices),
                Transpose = p.Transpose == 0 ? "移调 0" : $"移调 {p.Transpose:+#;-#;0}",
                ReadyText = p.Ready ? "已就绪" : "未就绪",
                RttText = p.RttMs > 0 ? $"{p.RttMs:F0} 毫秒" : "—",
            });
        }
    }

    /// <summary>
    /// 重建声部勾选表。
    /// 别人占着的声部置灰并写出占用者名字；自己的可以自由勾。
    /// </summary>
    private void RebuildSyncVoiceRows()
    {
        if (_sync == null) return;
        var peers = _sync.SnapshotPeers();
        var mine = _sync.SelfPeer();
        var myVoices = new HashSet<int>(mine?.Voices ?? new List<int>());
        var taken = SyncVoicePlan.Conflicts(peers, _sync.PeerId);
        var names = _sync.VoiceNames;

        _syncVoiceRows.Clear();
        for (int v = 0; v < names.Count; v++)
        {
            bool isMine = myVoices.Contains(v);
            bool takenByOther = taken.TryGetValue(v, out string? ownerId);
            string ownerName = "";
            if (takenByOther)
            {
                var owner = peers.Find(p => p.Id == ownerId);
                ownerName = owner?.Name ?? "别人";
            }
            _syncVoiceRows.Add(new SyncVoiceRow
            {
                Index = v,
                Label = $"{v + 1}. {names[v]}",
                IsMine = isMine,
                CanToggle = !takenByOther,
                TakenBy = takenByOther ? $"被 {ownerName} 占用" : "",
                Tip = takenByOther
                    ? $"声部 {v + 1} 已经由 {ownerName} 负责。要换人得让主机改。"
                    : "勾上就是由你弹这个声部。录好的移调会一起锁定。",
            });
        }
    }

    // ================= 连接 =================

    /// <summary>建房：本机当主机。</summary>
    private void SyncHost_Click(object? sender, RoutedEventArgs e)
    {
        StartSyncSession(isHost: true, invite: "");
    }

    /// <summary>加入：粘贴邀请串。</summary>
    private void SyncJoin_Click(object? sender, RoutedEventArgs e)
    {
        string text = TxtSyncInvite?.Text ?? "";
        if (!SyncRoomInfo.TryParse(text, out _, out string error))
        {
            if (TxtSyncJoinHint != null) TxtSyncJoinHint.Text = error;
            return;
        }
        if (TxtSyncJoinHint != null) TxtSyncJoinHint.Text = "";
        StartSyncSession(isHost: false, invite: text);
    }

    /// <summary>
    /// 建立会话。主机用界面上填的代理地址，成员用邀请串里带的地址
    /// （邀请串优先：朋友发来的串里地址才是这个房间用的代理）。
    /// </summary>
    private void StartSyncSession(bool isHost, string invite)
    {
        CloseSyncSession();

        SyncRoomInfo? room;
        if (isHost)
        {
            var (host, port, path) = ParseBroker(TxtSyncBroker?.Text ?? "");
            room = SyncRoomInfo.Create(host, port, path);
        }
        else
        {
            if (!SyncRoomInfo.TryParse(invite, out room, out string error) || room == null)
            {
                if (TxtSyncJoinHint != null) TxtSyncJoinHint.Text = error;
                return;
            }
        }

        string name = (TxtSyncName?.Text ?? "").Trim();
        if (name.Length == 0) name = Environment.UserName;

        var cfg = _mainWindow?.SyncConfigForSettings;
        string peerId = cfg?.SyncPeerId ?? "";
        if (peerId.Length == 0)
        {
            peerId = SyncRoomInfo.NewPeerId();
            if (cfg != null) cfg.SyncPeerId = peerId;
        }

        RememberSyncSettings(name, isHost ? TxtSyncBroker?.Text ?? "" : room.Host + ":" + room.Port + room.Path);

        var session = new SyncSession(room, peerId, name, isHost);
        _sync = session;
        HookSyncEvents(session);

        // 开演前要核对曲目：把本机当前的指纹算出来。
        // 顺手把声轨按列表顺序全部勾进合奏：声部序号来自"勾选顺序"，
        // 各人自己勾就可能勾出不同顺序，同一个序号在不同机器上指向不同的轨。
        // 统一按文件里的轨顺序编号之后，序号就只由文件决定，全场一致。
        _mainWindow?.PrepareSyncTracksForSettings();
        _syncFingerprint = _mainWindow?.SyncTrackFingerprintForSettings() ?? "";

        // 远程模式：主窗的声部掩码由本会话管，先把现在的分配推过去
        PushMaskToMainWindow();

        session.Connect();
        StartSyncDriftTimer();
        RefreshSyncUi();
    }

    /// <summary>展开 / 收起「怎么看这个功能」那段说明。默认收起，省出竖向空间给按钮。</summary>
    private void SyncIntro_Click(object? sender, RoutedEventArgs e)
    {
        if (TxtSyncIntro == null) return;
        TxtSyncIntro.IsVisible = !TxtSyncIntro.IsVisible;
    }

    /// <summary>退出房间。</summary>
    private void SyncLeave_Click(object? sender, RoutedEventArgs e)
    {
        CloseSyncSession();
        RefreshSyncUi();
    }

    /// <summary>复制邀请串。走主窗那份剪贴板实现，与赞助页的复制群号同一条链路。</summary>
    private async void SyncCopyInvite_Click(object? sender, RoutedEventArgs e)
    {
        if (_sync == null) return;
        bool ok = _mainWindow != null && await _mainWindow.CopyTextForSettingsAsync(this, _sync.InviteText);
        if (TxtSyncCopied != null) TxtSyncCopied.Text = ok ? "已复制" : "复制失败，请手动选中那一行";
    }

    /// <summary>把「代理地址」输入框拆成主机 / 端口 / 路径。填不动就用默认值。</summary>
    private static (string? Host, int? Port, string? Path) ParseBroker(string text)
    {
        string s = text.Trim();
        if (s.Length == 0) return (null, null, null);
        foreach (string prefix in new[] { "wss://", "ws://" })
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) s = s[prefix.Length..];

        string path = SyncRoomInfo.DefaultBrokerPath;
        int slash = s.IndexOf('/');
        if (slash >= 0)
        {
            path = s[slash..];
            s = s[..slash];
        }
        int port = SyncRoomInfo.DefaultBrokerPort;
        string host = s;
        int colon = s.LastIndexOf(':');
        if (colon > 0 && int.TryParse(s[(colon + 1)..], out int p)) 
        {
            port = p;
            host = s[..colon];
        }
        return (host.Length == 0 ? null : host, port, path);
    }

    /// <summary>名字与代理地址记进设置文件，下次打开还在。</summary>
    private void RememberSyncSettings(string name, string broker)
    {
        var cfg = _mainWindow?.SyncConfigForSettings;
        if (cfg == null) return;
        cfg.SyncPeerName = name;
        cfg.SyncBroker = broker;
        cfg.SyncIntroSeen = true;
        _mainWindow?.SaveConfigForSettings();
    }

    // ================= 事件接线 =================

    private void HookSyncEvents(SyncSession session)
    {
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
        session.Closed += OnSyncClosed;
    }

    private void UnhookSyncEvents(SyncSession session)
    {
        session.ConnectionChanged -= OnSyncConnectionChanged;
        session.RoomChanged -= OnSyncRoomChanged;
        session.StartRequested -= OnSyncStartRequested;
        session.PauseRequested -= OnSyncPauseRequested;
        session.ResumeRequested -= OnSyncResumeRequested;
        session.SeekRequested -= OnSyncSeekRequested;
        session.SpeedRequested -= OnSyncSpeedRequested;
        session.StopRequested -= OnSyncStopRequested;
        session.Log -= OnSyncLog;
        session.Faulted -= OnSyncFaulted;
        session.Closed -= OnSyncClosed;
    }

    private void OnSyncClosed()
        => Dispatcher.UIThread.Post(() => { _syncDriftTimer?.Stop(); _mainWindow?.ClearSyncMaskForSettings(); });

    // 下面这些事件都在后台线程来，一律转回界面线程。

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

    private void SyncVoiceToggle_Click(object? sender, RoutedEventArgs e)
    {
        if (_sync == null) return;
        var wanted = new List<int>();
        foreach (var row in _syncVoiceRows) if (row.IsMine) wanted.Add(row.Index);
        string error = _sync.SetSelfVoices(wanted);
        if (error.Length > 0 && TxtSyncAssignHint != null) TxtSyncAssignHint.Text = error;
        else if (TxtSyncAssignHint != null) TxtSyncAssignHint.Text = "";
        PushMaskToMainWindow();
        RefreshSyncUi();
    }

    private void SyncAutoAssign_Click(object? sender, RoutedEventArgs e)
    {
        if (_sync == null) return;
        _sync.HostAutoAssign();
        PushMaskToMainWindow();
        RefreshSyncUi();
    }

    private void SyncTranspose_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (TxtSyncTranspose != null)
        {
            int t = (int)(SliderSyncTranspose?.Value ?? 0);
            TxtSyncTranspose.Text = t > 0 ? $"+{t}" : t.ToString();
        }
        if (_sync == null) return;
        _sync.SetSelfTranspose((int)(SliderSyncTranspose?.Value ?? 0));
        PushMaskToMainWindow();
    }

    private void SyncReady_Click(object? sender, RoutedEventArgs e)
    {
        if (_sync == null) return;
        _syncSelfReady = ChkSyncReady?.IsChecked == true;
        _sync.SetSelfReady(_syncSelfReady);
        RefreshSyncUi();
    }

    // ================= 演出控制 =================

    private void SyncStart_Click(object? sender, RoutedEventArgs e)
    {
        if (_sync == null || !_sync.IsHost) return;

        // 开演前三件事要说清楚：曲目一致、本机载入了、有人还没就绪。
        if (_sync.Fingerprint.Length == 0)
        {
            // 主机还没把曲目信息发出去：现在补
            var info = _mainWindow?.SyncTrackInfoForSettings();
            if (info == null)
            {
                if (TxtSyncHint != null) TxtSyncHint.Text = "本机还没载入 MIDI，无法开演。";
                return;
            }
            _sync.HostSetTrack(info.Value.Fingerprint, info.Value.DurationSec, info.Value.VoiceNames);
        }
        if (_sync.DurationSec <= 0)
        {
            if (TxtSyncHint != null) TxtSyncHint.Text = "曲目信息还不完整（总时长为 0），无法开演。";
            return;
        }
        if (!_sync.AllReady() && TxtSyncHint != null)
            TxtSyncHint.Text = "还有人不就绪，仍然开演。开演后中途进来的人会自动跟上进度。";

        double pos = _mainWindow?.SyncProgressSecondsForSettings() ?? 0;
        _sync.HostStart(pos, _sync.ComputeStartDelayMs());
        RefreshSyncUi();
    }

    private void SyncPause_Click(object? sender, RoutedEventArgs e)
    {
        _sync?.HostPause();
        RefreshSyncUi();
    }

    private void SyncResume_Click(object? sender, RoutedEventArgs e)
    {
        _sync?.HostResume(_sync.LastLocalPositionSec, _sync.ComputeControlDelayMs());
        RefreshSyncUi();
    }

    private void SyncSeek_Click(object? sender, RoutedEventArgs e)
    {
        if (_sync == null) return;
        _sync.HostSeek(_mainWindow?.SyncProgressSecondsForSettings() ?? 0);
        RefreshSyncUi();
    }

    private void SyncStop_Click(object? sender, RoutedEventArgs e)
    {
        _sync?.HostStop();
        RefreshSyncUi();
    }

    // ================= 漂移校正 =================

    /// <summary>每 2 秒判断一次漂移。这是"漂移自动校正"的驱动点。</summary>
    private void StartSyncDriftTimer()
    {
        _syncDriftTimer?.Stop();
        _syncDriftTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _syncDriftTimer.Tick += (_, _) => SyncDriftTick();
        _syncDriftTimer.Start();
    }

    private void SyncDriftTick()
    {
        var session = _sync;
        if (session == null) return;
        double local = _mainWindow?.SyncProgressSecondsForSettings() ?? 0;
        var result = session.TickDrift(local);
        _syncLastError = result.ErrorSec;

        switch (result.Action)
        {
            case SyncDrift.Action.None:
                _mainWindow?.SetSyncTrimForSettings(1.0);
                break;
            case SyncDrift.Action.Trim:
                // 只改内部分速率，界面上的速度数字不变
                _mainWindow?.SetSyncTrimForSettings(result.TrimRate);
                break;
            case SyncDrift.Action.Jump:
                _mainWindow?.SetSyncTrimForSettings(1.0);
                _mainWindow?.SeekSyncPlaybackForSettings(result.TargetSec);
                _mainWindow?.InsertSyncLogForSettings(
                    $"远程同演：进度差 {result.ErrorSec * 1000:F0} 毫秒，已跳到 {result.TargetSec:F2} 秒。");
                break;
        }
        session.ReportDrift(local);
        if (TxtSyncSpeed != null && session.Transport == SyncTransport.Playing)
            TxtSyncSpeed.Text = $"速度 {session.Speed * 100:F0}%（在演奏参数里改速度会同步给所有人）"
                + $"　进度差 {_syncLastError * 1000:+0;-0;0} 毫秒";
    }

    /// <summary>把本机当前的声部分配与移调推给主窗，主窗据此决定弹哪些音、弹什么调。</summary>
    private void PushMaskToMainWindow()
    {
        var session = _sync;
        if (session == null)
        {
            _mainWindow?.ClearSyncMaskForSettings();
            return;
        }
        var mine = session.SelfPeer();
        _mainWindow?.ApplySyncMaskForSettings(mine?.Voices ?? new List<int>(), mine?.Transpose ?? 0);
    }

    /// <summary>本机是否处在远程同演里（开演前也算）。主窗用它决定要不要按掩码过滤声部。</summary>
    internal bool SyncActive => _sync != null;

    /// <summary>【开发用】切到实验功能页后，把成员表填上假数据，供界面快照拍出有内容的一页。</summary>
    internal void FillSyncPageForDev()
    {
        if (SyncConnectCard != null) SyncConnectCard.IsVisible = false;
        if (SyncRoomCard != null) SyncRoomCard.IsVisible = true;
        if (TxtSyncStatus != null) TxtSyncStatus.Text = "已连接　3 人　延迟 438 毫秒　抖动 1 毫秒";
        if (SyncInviteRow != null) SyncInviteRow.IsVisible = true;
        if (TxtSyncInviteShown != null)
            TxtSyncInviteShown.Text = "mkp://broker.emqx.io:8084/mqtt|a7f39c2e1b4d6085|9f8e7d6c5b4a3928";
        if (TxtSyncTrack != null)
            TxtSyncTrack.Text = "曲目已核对一致（3f2a91c4d8e05b76），总时长 138.4 秒。";
        if (TxtSyncHint != null)
            TxtSyncHint.Text = "你是主机。把邀请串发给朋友，等他们都进来、都点「我就绪了」，再点「开演」。";
        if (TxtSyncSpeed != null)
            TxtSyncSpeed.Text = "速度 100%（在演奏参数里改速度会同步给所有人）　进度差 12 毫秒";
        if (TxtSyncAssignHint != null) TxtSyncAssignHint.Text = "声部共用 4 个，按勾选顺序编号。";
        if (TxtSyncTranspose != null) TxtSyncTranspose.Text = "-5";
        if (SliderSyncTranspose != null) SliderSyncTranspose.Value = -5;
        if (ChkSyncReady != null) ChkSyncReady.IsChecked = true;

        _syncPeerRows.Clear();
        _syncPeerRows.Add(new SyncPeerRow { Mark = "●", Name = "小明（主机）", Voices = "1、3", Transpose = "移调 0", ReadyText = "已就绪", RttText = "438 毫秒" });
        _syncPeerRows.Add(new SyncPeerRow { Mark = "", Name = "小红", Voices = "2、4", Transpose = "移调 -5", ReadyText = "已就绪", RttText = "512 毫秒" });
        _syncPeerRows.Add(new SyncPeerRow { Mark = "", Name = "老王", Voices = "（无）", Transpose = "移调 0", ReadyText = "未就绪", RttText = "—" });

        _syncVoiceRows.Clear();
        string[] names = { "主旋律", "和声", "贝斯", "鼓" };
        for (int v = 0; v < names.Length; v++)
        {
            bool mine = v == 0 || v == 2;
            bool takenByOther = v == 1;
            _syncVoiceRows.Add(new SyncVoiceRow
            {
                Index = v,
                Label = $"{v + 1}. {names[v]}",
                IsMine = mine,
                CanToggle = !takenByOther,
                TakenBy = takenByOther ? "被 小红 占用" : "",
                Tip = takenByOther ? "声部 2 已经由 小红 负责。要换人得让主机改。" : "勾上就是由你弹这个声部。录好的移调会一起锁定。",
            });
        }
    }
}

/// <summary>成员表的一行。</summary>
public sealed class SyncPeerRow
{
    public string Mark { get; init; } = "";
    public string Name { get; init; } = "";
    public string Voices { get; init; } = "";
    public string Transpose { get; init; } = "";
    public string ReadyText { get; init; } = "";
    public string RttText { get; init; } = "";
}

/// <summary>声部勾选表的一行。</summary>
public sealed class SyncVoiceRow
{
    public int Index { get; init; }
    public string Label { get; init; } = "";
    public bool IsMine { get; set; }
    public bool CanToggle { get; init; } = true;
    public string TakenBy { get; init; } = "";
    public string Tip { get; init; } = "";
}

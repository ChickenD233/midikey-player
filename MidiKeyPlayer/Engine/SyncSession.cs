using System.Text.Json;

namespace MidiKeyPlayer.Engine;

/// <summary>
/// 【实验功能·远程同演】一次同演会话。
///
/// 职责刻意压到最小，只有三件事：
/// 1. 连到中转站，收发消息（真正的连接在 <see cref="SyncRelay"/> 里）。
/// 2. 维护成员名单：谁在、弹什么声部、是否就绪。
/// 3. 算"什么时候开始弹"：用本机单调时钟的锚点，不比较两台机器的墙钟。
///
/// **不做的**（这些是旧方案里为了"精确同步"加的，按用户要求全删了）：
/// - 不测往返延迟。开演固定等 N 秒，延迟是常数，不需要测。
/// - 没有心跳、没有周期上报、没有房间快照。
/// - 不做漂移校正、不做微调、不自动跳转。
/// - 不比较两台机器的时钟。
///
/// 用量因此极小：一场 5 人 1 小时约 50 条消息。
/// </summary>
internal sealed class SyncSession : IDisposable
{
    // ================= 对外事件（后台线程触发） =================

    public event Action<SyncConnState>? ConnectionChanged;
    public event Action? RoomChanged;

    /// <summary>到点开演。参数 = 起始位置（秒）。</summary>
    public event Action<double>? StartRequested;
    public event Action? PauseRequested;
    public event Action? ResumeRequested;
    public event Action<double>? SeekRequested;
    public event Action<double>? SpeedRequested;
    public event Action? StopRequested;

    public event Action<string>? Log;
    public event Action<string>? Faulted;

    // ================= 身份 =================

    private readonly SyncRoomInfo _room;
    private readonly string _peerId;
    private readonly string _name;
    private readonly bool _isHost;

    /// <summary>成员名单。服务器推名单时整份替换。</summary>
    private List<SyncPeerInfo> _peers = new();

    /// <summary>自己的声部（显示文本，例如 "2、4"）。</summary>
    private string _selfVoice = "";

    /// <summary>自己的移调（半音）。</summary>
    private int _selfTranspose;

    /// <summary>房主统一规定的移调。null = 没有统一，各人自己管。</summary>
    private int? _unifiedTranspose;

    /// <summary>本轮的演奏状态。</summary>
    private SyncTransport _transport = SyncTransport.Idle;

    private double _speed = 1.0;
    private int _countdownSec = 3;

    private SyncRelay? _relay;
    private int _selfReadyCount;

    // ================= 时间 =================

    private SyncAnchor _anchor = SyncAnchor.Idle;
    private double _pausedPosition;

    internal SyncSession(SyncRoomInfo room, string peerId, string name, bool isHost)
    {
        _room = room;
        _peerId = peerId;
        _name = name;
        _isHost = isHost;
    }

    internal SyncRoomInfo Room => _room;
    internal string PeerId => _peerId;
    internal string PeerName => _name;
    internal bool IsHost => _isHost;
    internal SyncTransport Transport => _transport;
    internal double Speed => _speed;
    internal SyncConnState Connection => _relay?.Connection ?? SyncConnState.Offline;
    internal string InviteText => _room.ToInvite();

    /// <summary>自己的声部显示文本。</summary>
    internal string SelfVoice => _selfVoice;

    /// <summary>自己的移调。房主统一规定时，返回统一值。</summary>
    internal int SelfTranspose => _unifiedTranspose ?? _selfTranspose;

    /// <summary>房主统一规定的移调。null = 没统一。</summary>
    internal int? UnifiedTranspose => _unifiedTranspose;

    /// <summary>倒数秒数（房主可调，随开演命令发给全员）。</summary>
    internal int CountdownSec
    {
        get => _countdownSec;
        set => _countdownSec = Math.Clamp(value, 1, 30);
    }

    internal List<SyncPeerInfo> SnapshotPeers()
    {
        lock (_peers) return new List<SyncPeerInfo>(_peers);
    }

    internal SyncPeerInfo? SelfPeer()
    {
        lock (_peers) return _peers.Find(p => p.Id == _peerId);
    }

    /// <summary>当前应有的曲子位置（秒）。没开演就是 0。</summary>
    internal double TargetPositionSec => _anchor.PositionNow();

    /// <summary>从计划开演时刻到现在的剩余毫秒。0 = 已经在演。</summary>
    internal int LeadInMs
    {
        get
        {
            long left = _anchor.AtTickMs - SyncClock.NowMs;
            return left <= 0 ? 0 : (int)left;
        }
    }

    // ================= 连接 =================

    /// <summary>进房间。voice 是"我的声部"显示文本。</summary>
    internal void Connect(string voice)
    {
        _selfVoice = voice;
        var relay = new SyncRelay(_room, _peerId, _name, voice, _selfTranspose);
        _relay = relay;
        relay.ConnectionChanged += OnConnectionChanged;
        relay.MessageReceived += OnMessage;
        relay.Log += m => Log?.Invoke(m);
        relay.Faulted += m => Faulted?.Invoke(m);
        relay.Connect(voice, _selfTranspose);
    }

    private void OnConnectionChanged(SyncConnState state)
    {
        ConnectionChanged?.Invoke(state);
    }

    /// <summary>退出房间。</summary>
    public void Disconnect()
    {
        var relay = _relay;
        _relay = null;
        if (relay != null)
        {
            relay.Send(new SyncMessage { T = SyncKind.Leave });
            relay.Dispose();
        }
        _anchor = SyncAnchor.Idle;
        _transport = SyncTransport.Idle;
        ConnectionChanged?.Invoke(SyncConnState.Offline);
    }

    public void Dispose() => Disconnect();

    // ================= 收消息 =================

    private void OnMessage(SyncMessage msg)
    {
        switch (msg.T)
        {
            case SyncKind.Welcome:
                Log?.Invoke("远程同演：中转站确认了身份。");
                break;

            case SyncKind.Roster:
                if (msg.Peers != null)
                {
                    lock (_peers) _peers = msg.Peers;
                }
                RoomChanged?.Invoke();
                break;

            case SyncKind.Xpose:
                // 房主的统一移调。0 表示"没有统一"，各人回到自己的设置。
                // 用 0 而不是 null 是为了让服务器端少一个分支：那条消息永远是一个整数。
                _unifiedTranspose = (msg.Xpose ?? 0) == 0 ? null : msg.Xpose;
                Log?.Invoke(_unifiedTranspose == null
                    ? "远程同演：房主取消了统一移调，各人回到自己的设置。"
                    : $"远程同演：房主统一移调为 {_unifiedTranspose:+#;-#;0} 半音。");
                RoomChanged?.Invoke();
                break;

            case SyncKind.Start:
                ApplyStart(msg);
                break;

            case SyncKind.Pause:
                ApplyPause();
                break;

            case SyncKind.Stop:
                _anchor = SyncAnchor.Idle;
                _pausedPosition = 0;
                _transport = SyncTransport.Stopped;
                RoomChanged?.Invoke();
                StopRequested?.Invoke();
                break;
        }
    }

    /// <summary>
    /// 收到开演：算出"从计划时刻到本机现在还剩多久"，加到本机单调时刻上。
    ///
    /// 关键一步是减掉**这条消息在路上花掉的时间**（本机收到时刻 减 房主的发出时刻）。
    /// 不减的话，每个人会比房主晚整整一个传输时间才开演，而且各人延迟不同、各人晚的量也不同。
    /// 减掉之后，所有人的目标时刻都是"房主发出时刻 + DelayMs"，与各自的网络快慢无关。
    /// </summary>
    private void ApplyStart(SyncMessage msg)
    {
        int delayMs = msg.DelayMs ?? 3000;
        double position = msg.PositionSec ?? 0;
        long sentAt = msg.SentAt ?? 0;
        _speed = 1.0;

        int remaining = RemainingDelayCore(delayMs, sentAt, SyncClock.NowMs);
        _anchor = new SyncAnchor
        {
            PositionSec = position,
            AtTickMs = SyncClock.NowMs + Math.Max(0, remaining),
            Speed = _speed,
            Valid = true,
        };
        _transport = SyncTransport.Playing;
        RoomChanged?.Invoke();

        // 跳转（DelayMs = 0）要立刻动作，不能等下一次界面刷新：
        // 界面那个名单刷新计时器每 2 秒才跑一次，等它就可能拖到两秒才跳。
        if (delayMs <= 0) SeekRequested?.Invoke(position);
        else StartRequested?.Invoke(position);
    }

    /// <summary>
    /// 把 DelayMs 换算成本机还要等多久。
    ///
    /// DelayMs 的口径是"房主发出时刻 + DelayMs = 计划开演时刻"。
    /// 本机已经过了 (收到时刻 - 房主发出时刻) 这么久，所以剩下的等待要减掉这一段。
    /// 消息迟到超过 DelayMs 时返回 0：宁可立刻开始，也不要把开演推到更晚。
    /// </summary>
    internal static int RemainingDelayCore(int delayMs, long sentTickMs, long nowTickMs)
    {
        if (sentTickMs <= 0) return Math.Max(0, delayMs);   // 没带戳，退回旧口径
        long transit = nowTickMs - sentTickMs;
        if (transit < 0) transit = 0;                        // 对端时钟异常，当作没花时间
        return (int)Math.Max(0, delayMs - transit);
    }

    private void ApplyPause()
    {
        _pausedPosition = TargetPositionSec;
        _anchor = SyncAnchor.Idle;
        _transport = SyncTransport.Paused;
        RoomChanged?.Invoke();
        PauseRequested?.Invoke();
    }

    // ================= 发消息（房主操作） =================

    /// <summary>改自己的声部并报给全房间（名单只在真的变了时才发）。</summary>
    internal void SetSelfVoice(string voice)
    {
        if (voice == _selfVoice) return;
        _selfVoice = voice;
        _relay?.Send(new SyncMessage { T = SyncKind.Voice, Voice = voice });
    }

    /// <summary>改自己的移调。有统一移调时这条不生效（界面上也会禁用）。</summary>
    internal void SetSelfTranspose(int semitones)
    {
        int t = Math.Clamp(semitones, -24, 24);
        if (t == _selfTranspose) return;
        _selfTranspose = t;
        RoomChanged?.Invoke();
    }

    /// <summary>报自己的就绪态。</summary>
    internal void SetSelfReady(bool ready)
    {
        int want = ready ? 1 : 0;
        if (want == _selfReadyCount) return;
        _selfReadyCount = want;
        _relay?.Send(new SyncMessage { T = SyncKind.Ready, On = ready });
    }

    /// <summary>房主：统一规定移调。传 null 取消统一。</summary>
    internal void HostSetUnifiedTranspose(int? semitones)
    {
        if (!_isHost) return;
        _relay?.Send(new SyncMessage { T = SyncKind.Xpose, Xpose = semitones });
    }

    /// <summary>
    /// 房主：开演。
    ///
    /// 消息里带房主自己的单调时刻，收到方减掉传输时间。房主自己也等满 delayMs 再开始，
    /// 这样全场落到同一个时刻，而不是房主先弹、别人后弹。
    /// </summary>
    internal void HostStart(double positionSec)
    {
        if (!_isHost) return;
        int delayMs = _countdownSec * 1000;
        long sentAt = SyncClock.NowMs;

        _anchor = new SyncAnchor
        {
            PositionSec = positionSec,
            AtTickMs = sentAt + delayMs,
            Speed = _speed,
            Valid = true,
        };
        _transport = SyncTransport.Playing;

        _relay?.Send(new SyncMessage
        {
            T = SyncKind.Start,
            DelayMs = delayMs,
            PositionSec = positionSec,
            SentAt = sentAt,
        });
        RoomChanged?.Invoke();
        FireAfterDelay(delayMs, () => StartRequested?.Invoke(positionSec));
    }

    private void FireAfterDelay(int delayMs, Action action)
    {
        if (delayMs <= 0) { action(); return; }
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delayMs).ConfigureAwait(false); }
            catch { return; }
            action();
        });
    }

    internal void HostPause()
    {
        if (!_isHost) return;
        double pos = TargetPositionSec;
        _pausedPosition = pos;
        _anchor = SyncAnchor.Idle;
        _transport = SyncTransport.Paused;
        _relay?.Send(new SyncMessage { T = SyncKind.Pause });
        RoomChanged?.Invoke();
        PauseRequested?.Invoke();
    }

    internal void HostStop()
    {
        if (!_isHost) return;
        _anchor = SyncAnchor.Idle;
        _pausedPosition = 0;
        _transport = SyncTransport.Stopped;
        _relay?.Send(new SyncMessage { T = SyncKind.Stop });
        RoomChanged?.Invoke();
        StopRequested?.Invoke();
    }

    /// <summary>房主：从暂停处继续。与开演同一条时间线。</summary>
    internal void HostResume(double positionSec)
    {
        if (!_isHost) return;
        int delayMs = _countdownSec * 1000;
        _anchor = new SyncAnchor
        {
            PositionSec = positionSec,
            AtTickMs = SyncClock.NowMs + delayMs,
            Speed = _speed,
            Valid = true,
        };
        _transport = SyncTransport.Playing;
        _relay?.Send(new SyncMessage { T = SyncKind.Start, DelayMs = delayMs, PositionSec = positionSec });
        RoomChanged?.Invoke();
        FireAfterDelay(delayMs, () => ResumeRequested?.Invoke());
    }

    /// <summary>
    /// 房主：跳转。位置由房主定，因为各人进度条位置不一定一样。
    ///
    /// DelayMs 为 0 且带 0 延迟的开演消息：收到方立刻设锚点、立刻跳，不再等倒数。
    /// 拖进度条本来就是要立刻看到结果的，等 3 秒会让人以为没反应。
    /// </summary>
    internal void HostSeek(double positionSec)
    {
        if (!_isHost) return;
        _anchor = SyncAnchor.At(positionSec, _speed);
        _transport = SyncTransport.Playing;
        // 不填 SentAt：填了收到方会去算传输耗时，这里不需要，越简单越好。
        _relay?.Send(new SyncMessage { T = SyncKind.Start, DelayMs = 0, PositionSec = positionSec });
        RoomChanged?.Invoke();
        SeekRequested?.Invoke(positionSec);
    }

    /// <summary>房主：改速度。</summary>
    internal void HostSetSpeed(double speed)
    {
        if (!_isHost) return;
        _speed = Math.Clamp(speed, 0.1, 4.0);
        if (_transport == SyncTransport.Playing) _anchor = _anchor.WithSpeed(_speed);
        RoomChanged?.Invoke();
        SpeedRequested?.Invoke(_speed);
    }

    /// <summary>全员都点过就绪了吗。人少于两个时也算没就绪：一个人不叫合奏。</summary>
    internal bool AllReady()
    {
        lock (_peers)
        {
            if (_peers.Count < 2) return false;
            foreach (var p in _peers) if (!p.Ready) return false;
        }
        return true;
    }

    /// <summary>不参与演奏的成员（名单非空时）。界面显示用。</summary>
    internal string DescribeSelf()
    {
        string voice = _selfVoice.Length > 0 ? _selfVoice : "（未选）";
        return $"声部 {voice}　移调 {SelfTranspose:+#;-#;0}";
    }
}

/// <summary>房间的演奏状态。</summary>
internal enum SyncTransport
{
    Idle = 0,       // 还没开演
    Playing = 2,
    Paused = 3,
    Stopped = 4,
}

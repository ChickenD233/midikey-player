using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace MidiKeyPlayer.Engine;

/// <summary>
/// 【实验功能·远程同演】一次同演会话。
///
/// 这一层负责三件事，都不碰界面：
/// 1. 把 MQTT over WebSocket 连到公共代理，收发两个主题的消息。
/// 2. 维护房间状态：成员表、声部占用、曲目指纹、播放状态。
/// 3. 时间：用 <see cref="SyncClock"/> 的单调锚点算"该在哪儿"，按 <see cref="SyncDrift"/> 校正。
///
/// 线程约定：网络事件在后台线程触发，界面订阅方自己 marshal 回 UI 线程。
/// 主机的"房间快照"是权威；成员的本地状态只是镜像。
/// </summary>
internal sealed class SyncSession : IDisposable
{
    // ================= 对外事件（后台线程触发） =================

    /// <summary>连接状态变了。</summary>
    public event Action<SyncConnState>? ConnectionChanged;

    /// <summary>房间状态变了（成员进出、声部分配、指纹、播放状态）。</summary>
    public event Action? RoomChanged;

    /// <summary>收到"到点开演"。参数 = 起始位置（秒）。</summary>
    public event Action<double>? StartRequested;

    public event Action? PauseRequested;
    public event Action? ResumeRequested;

    /// <summary>要求跳转到某位置（秒）。</summary>
    public event Action<double>? SeekRequested;

    /// <summary>要求改速度（倍率）。</summary>
    public event Action<double>? SpeedRequested;

    public event Action? StopRequested;

    /// <summary>会话结束了（Dispose 或退出房间）。界面用它收掉计时器与掩码。</summary>
    public event Action? Closed;

    /// <summary>要写进日志的一行。</summary>
    public event Action<string>? Log;

    /// <summary>未处理的异常（网络断开等）。</summary>
    public event Action<string>? Faulted;

    // ================= 配置与身份 =================

    private readonly SyncRoomInfo _room;
    private readonly string _peerId;
    private readonly bool _isHost;
    private readonly string _name;
    private readonly uint _keepAliveSec;

    /// <summary>重连次数上限。到顶就停手，不无限打扰代理。</summary>
    private const int MaxReconnect = 10;

    /// <summary>
    /// 稳态播放时，快照位置与本机位置差多少才值得重设锚点（秒）。
    /// 比漂移校正的上限（120 毫秒）大：小于它的偏差交给漂移校正慢速修，
    /// 重设锚点只会把一个"消息在路上的时间"造成的台阶塞进播放位置。
    /// 真正的跳转远大于这个值，所以还能被认出来。
    /// </summary>
    private const double ReanchorThresholdSec = 0.35;

    // ================= 连接状态 =================

    private readonly object _gate = new();
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private int _packetId;
    private SyncConnState _conn = SyncConnState.Offline;
    private int _reconnectCount;
    private int _connectGeneration;

    // ================= 房间状态 =================

    private List<SyncPeer> _peers = new();
    private SyncHostState? _hostState;
    private string _fingerprint = "";
    private double _durationSec;
    private List<string> _voiceNames = new();
    private SyncTransport _transport = SyncTransport.Idle;
    private double _speed = 1.0;
    private int _lastStateSeq = -1;
    private readonly HashSet<string> _seenSeq = new();
    private long _lastHostStateTick;      // 最近一次收到主机快照的本机时刻
    private long _lastHostPacketTick;     // 最近一次收到主机任何 state 包的本机时刻

    // ================= 时间与漂移 =================

    private readonly SyncClock _clock = new();
    private SyncAnchor _anchor = SyncAnchor.Idle;
    private int _probeCounter;
    private long _lastProbeTick;
    private readonly Dictionary<int, long> _pendingProbes = new();

    /// <summary>主机用：所有成员上报的最大往返延迟，算开演余量用。</summary>
    private double _maxPeerRtt;

    // —— 自己的状态存在本地字段里，不靠成员表的镜像 ——
    //
    // 为什么不靠镜像：成员刚连上时，成员表里只有主机（自己的那一行要等主机的快照回来才有）。
    // 这时点"我就绪"，如果去镜像里找自己就会找不到，改动被静默丢掉，
    // 上报出去的永远是"未就绪"，主机那边就一直等不到全员就绪。同理适用于声部与移调。
    private bool _selfReady;
    private List<int> _selfVoices = new();
    private int _selfTranspose;

    // ================= 构造 =================

    internal SyncSession(SyncRoomInfo room, string peerId, string name, bool isHost, uint keepAliveSec = 60)
    {
        _room = room;
        _peerId = peerId;
        _name = name ?? "";
        _isHost = isHost;
        _keepAliveSec = keepAliveSec;
    }

    internal SyncRoomInfo Room => _room;
    internal string PeerId => _peerId;
    internal string PeerName => _name;
    internal bool IsHost => _isHost;
    internal string Fingerprint => _fingerprint;
    internal double DurationSec => _durationSec;
    internal IReadOnlyList<string> VoiceNames => _voiceNames;
    internal SyncTransport Transport => _transport;
    internal double Speed => _speed;
    internal double RttMs => _clock.RttMs;
    internal double JitterMs => _clock.JitterMs;
    internal SyncConnState Connection => _conn;

    /// <summary>当前应有的曲子位置（秒）。没开演就是 0。</summary>
    internal double TargetPositionSec => _anchor.PositionNow();

    /// <summary>这一轮的音频/按键是"提前量"之外还要等多久（毫秒）。0 = 已经在演。</summary>
    internal int LeadInMs
    {
        get
        {
            lock (_gate)
            {
                long left = _anchor.AtTickMs - SyncClock.NowMs;
                return left <= 0 ? 0 : (int)left;
            }
        }
    }

    /// <summary>【诊断用】本机锚点的原始状态：位置、速度、锚点时刻相对现在的偏移。</summary>
    internal string AnchorDebug
    {
        get
        {
            lock (_gate)
            {
                return $"pos={_anchor.PositionSec:F3} speed={_anchor.Speed:F3} "
                    + $"锚点距今={_anchor.AtTickMs - SyncClock.NowMs}ms 有效={_anchor.Valid} "
                    + $"_speed={_speed:F3} 状态={_transport}";
            }
        }
    }

    internal List<SyncPeer> SnapshotPeers()
    {
        lock (_gate) return new List<SyncPeer>(_peers);
    }

    internal SyncPeer? SelfPeer()
    {
        lock (_gate) return _peers.Find(p => p.Id == _peerId);
    }

    internal string InviteText => _room.ToInvite();

    // ================= 连接 =================

    /// <summary>开始连接。重复调用会先把旧的收掉。</summary>
    public void Connect()
    {
        if (_isHost)
        {
            // 主机自己先登记进成员表：房间快照里要有主机那一行，别人才能看到是谁在分配。
            lock (_gate)
            {
                if (_peers.Find(p => p.Id == _peerId) == null)
                    _peers.Add(new SyncPeer { Id = _peerId, Name = _name });
            }
        }
        _reconnectCount = 0;
        _ = ConnectLoopAsync();
    }

    private async Task ConnectLoopAsync()
    {
        while (_reconnectCount <= MaxReconnect)
        {
            int generation = ++_connectGeneration;
            try
            {
                await ConnectOnceAsync(generation).ConfigureAwait(false);
                // ConnectOnceAsync 只在连接结束后返回（正常断开或异常）。
                // 正常退出（用户点了断开）就不再重连。
                if (_conn == SyncConnState.Offline) return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"远程同演：连接中断，{ex.Message}");
            }

            if (_conn == SyncConnState.Offline) return;

            _reconnectCount++;
            if (_reconnectCount > MaxReconnect)
            {
                SetConn(SyncConnState.Offline);
                Faulted?.Invoke($"远程同演：重连 {MaxReconnect} 次都失败，已停止。可以手动重试。");
                return;
            }

            SetConn(SyncConnState.Reconnecting);
            int wait = Math.Min(30, 1 << Math.Min(_reconnectCount, 5));   // 2、4、8、16、30 秒
            Log?.Invoke($"远程同演：{wait} 秒后重连（第 {_reconnectCount} 次）。");
            try { await Task.Delay(wait * 1000).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>连一次，跑到断开为止。</summary>
    private async Task ConnectOnceAsync(int generation)
    {
        SetConn(SyncConnState.Connecting);
        var ws = new ClientWebSocket();
        // **必须声明 mqtt 子协议。** 代理按这个选协议，不声明就回 HTTP 400。
        // 报错是 "The server returned status code '400' when status code '101' was expected"，
        // 完全看不出是子协议的问题。实测：EMQX 不给子协议就拒；HiveMQ 两者都收。
        ws.Options.AddSubProtocol("mqtt");
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _ws = ws;
            _cts = cts;
        }

        try
        {
            await ws.ConnectAsync(new Uri(_room.WebSocketUrl), cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"远程同演：连不上代理 {_room.WebSocketUrl}。{ex.Message}");
            throw;
        }

        // 成员号每次都一样：代理那边 cleanSession=true，不依赖它保存会话；
        // 我们自己靠成员号认人，重连后还是同一个人。
        await SendRawAsync(MqttPacket.Connect(_peerId, keepAliveSec: (ushort)_keepAliveSec), cts.Token)
            .ConfigureAwait(false);

        var buffer = new List<byte>(8192);
        var chunk = new byte[8192];
        bool subscribed = false;

        while (!cts.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(chunk), cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (WebSocketException) { break; }

            if (result.MessageType == WebSocketMessageType.Close) break;
            if (result.Count > 0) buffer.AddRange(new ArraySegment<byte>(chunk, 0, result.Count));

            while (MqttPacket.TrySlice(buffer, out byte first, out byte[] body))
            {
                var type = (MqttPacketType)(first >> 4);
                switch (type)
                {
                    case MqttPacketType.ConnAck:
                    {
                        byte code = MqttPacket.ParseConnAck(body);
                        if (code != 0)
                        {
                            Log?.Invoke("远程同演：" + MqttConnectResultText.Of(code));
                            throw new InvalidOperationException(MqttConnectResultText.Of(code));
                        }
                        string topic1 = _room.ControlTopic;
                        string topic2 = _room.StateTopic;
                        await SendRawAsync(MqttPacket.Subscribe(topic1, NextPacketId()), cts.Token).ConfigureAwait(false);
                        await SendRawAsync(MqttPacket.Subscribe(topic2, NextPacketId()), cts.Token).ConfigureAwait(false);
                        break;
                    }

                    case MqttPacketType.SubAck:
                    {
                        byte rc = MqttPacket.ParseSubAck(body);
                        if (rc == 0x80) Log?.Invoke("远程同演：代理拒绝了订阅请求。");
                        if (!subscribed)
                        {
                            subscribed = true;
                            SetConn(SyncConnState.Online);
                            _reconnectCount = 0;
                            _clock.Reset();
                            _seenSeq.Clear();
                            _lastStateSeq = -1;
                            Log?.Invoke($"远程同演：已连上代理 {_room.Host}:{_room.Port}，房间 {_room.RoomCode[..8]}…");
                            if (_isHost)
                            {
                                BroadcastHostState();
                                StartKeepAlive(cts);
                                StartHostTicker(cts);
                            }
                            else
                            {
                                // 迟到者也要能立刻拿到快照：state 主题上的保留消息会推过来。
                                // 主动再发一次 join，让主机马上把成员表刷新。
                                SendControl(SyncMsgKind.Join, 0, 0, 1.0);
                                StartKeepAlive(cts);
                                StartMemberTicker(cts);
                            }
                        }
                        break;
                    }

                    case MqttPacketType.Publish:
                    {
                        MqttPacket.ParsePublish(first, body, out string topic, out byte[] payload, out int pid);
                        if (pid > 0) await SendRawAsync(MqttPacket.PubAck(pid), cts.Token).ConfigureAwait(false);
                        if (topic == _room.ControlTopic) OnControlPayload(payload);
                        else if (topic == _room.StateTopic) OnStatePayload(payload);
                        break;
                    }

                    case MqttPacketType.PubAck:
                    case MqttPacketType.PingResp:
                        break;   // 收到即可，不需要额外动作

                    default:
                        break;   // 其余报文一律忽略
                }
            }
        }

        if (generation == _connectGeneration) SetConn(SyncConnState.Reconnecting);
    }

    private void StartKeepAlive(CancellationTokenSource cts)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(10, _keepAliveSec / 2)), cts.Token)
                        .ConfigureAwait(false);
                    var ws = _ws;
                    if (ws == null || ws.State != WebSocketState.Open) break;
                    await SendRawAsync(MqttPacket.PingReq(), cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch { /* 保活失败就交给接收循环去发现断开 */ }
        }, cts.Token);
    }

    /// <summary>
    /// 主机心跳：每 5 秒发一份快照。
    ///
    /// retain 只在"状态真的变了"时为真：保留消息每次写入都会让代理记一份，
    /// 心跳也带 retain 会白白刷代理的存储，还可能让迟到者读到一份和保留位绑定的旧序号。
    /// 心跳带 retain=false，只用于让成员判断"主机还在"。
    /// </summary>
    private void StartHostTicker(CancellationTokenSource cts)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(5000, cts.Token).ConfigureAwait(false);
                    PublishHostState(retain: false);
                }
            }
            catch (OperationCanceledException) { }
        }, cts.Token);
    }

    /// <summary>成员心跳：定期测延迟、上报本机位置。</summary>
    private void StartMemberTicker(CancellationTokenSource cts)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    long now = SyncClock.NowMs;
                    // 每 5 秒测一次延迟。样本窗口够大，不需要更密。
                    if (now - _lastProbeTick >= 5000)
                    {
                        _lastProbeTick = now;
                        int id = ++_probeCounter;
                        lock (_gate) _pendingProbes[id] = now;
                        SendControl(SyncMsgKind.Probe, 0, 0, 1.0);
                        ReportState();
                    }
                    await Task.Delay(1000, cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        }, cts.Token);
    }

    /// <summary>主动断开，不再重连。</summary>
    public void Disconnect()
    {
        SetConn(SyncConnState.Offline);
        try { _cts?.Cancel(); } catch { }
        try
        {
            var ws = _ws;
            if (ws is { State: WebSocketState.Open })
                _ = ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch { }
        lock (_gate) { _ws = null; _cts = null; }
        _anchor = SyncAnchor.Idle;
        _transport = SyncTransport.Idle;
        Closed?.Invoke();
    }

    public void Dispose()
    {
        Disconnect();
        try { _ws?.Dispose(); } catch { }
    }

    private void SetConn(SyncConnState state)
    {
        if (_conn == state) return;
        _conn = state;
        ConnectionChanged?.Invoke(state);
    }

    private int NextPacketId()
    {
        lock (_gate)
        {
            _packetId = _packetId >= 65535 ? 1 : _packetId + 1;
            return _packetId;
        }
    }

    private async Task SendRawAsync(byte[] packet, CancellationToken token)
    {
        var ws = _ws;
        if (ws == null || ws.State != WebSocketState.Open) return;
        await ws.SendAsync(new ArraySegment<byte>(packet), WebSocketMessageType.Binary, true, token)
            .ConfigureAwait(false);
    }

    // ================= 发消息 =================

    /// <summary>发一条控制消息。只有主机能发播放类控制；成员只发 Probe 与 Join。</summary>
    internal void SendControl(SyncMsgKind kind, int delayMs, double positionSec, double speed)
    {
        var body = new SyncControlMessage
        {
            Kind = kind,
            Sender = _peerId,
            Name = _name,
            DelayMs = delayMs,
            HostTickMs = SyncClock.NowMs,   // 收到方用它算这条消息在路上花了多久
            PositionSec = positionSec,
            Speed = speed,
        };
        PublishSigned(_room.ControlTopic, body, retain: false);
    }

    /// <summary>成员上报：本机位置、延迟、就绪态。主机据此算余量、显示进度差。</summary>
    internal void ReportState(bool includeReady = true)
    {
        var body = new SyncStateMessage
        {
            Sender = _peerId,
            PositionSec = TargetPositionSec,
            RttMs = _clock.RttMs,
            Ready = includeReady && _selfReady,
        };
        PublishSigned(_room.StateTopic, body, retain: false);
    }

    /// <summary>主机把完整快照发到 state 主题。状态真变了才带保留标志，供迟到者订阅时立刻收到。</summary>
    internal void BroadcastHostState() => PublishHostState(retain: true);

    private void PublishHostState(bool retain)
    {
        if (!_isHost) return;
        SyncHostState snap;
        lock (_gate)
        {
            _hostState ??= new SyncHostState { HostId = _peerId };
            var s = _hostState;
            s.HostId = _peerId;
            s.Peers = new List<SyncPeer>();
            foreach (var p in _peers) s.Peers.Add(ClonePeer(p));
            s.Fingerprint = _fingerprint;
            s.DurationSec = _durationSec;
            s.VoiceNames = new List<string>(_voiceNames);
            s.Speed = _speed;
            s.PositionSec = _transport == SyncTransport.Playing ? TargetPositionSec : CurrentPositionLocked();
            s.Transport = _transport;
            s.HostUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            s.CountdownSec = _countdownSec;
            s.StateSeq = ++_stateSeq;
            snap = s;
        }
        PublishSigned(_room.StateTopic, new SyncStateMessage { Sender = _peerId, Host = snap }, retain);
    }

    private int _stateSeq;
    private int _countdownSec;
    private double _pausedPosition;

    private static SyncPeer ClonePeer(SyncPeer p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        Voices = new List<int>(p.Voices),
        Transpose = p.Transpose,
        Ready = p.Ready,
        RttMs = p.RttMs,
    };

    private double CurrentPositionLocked() => _transport == SyncTransport.Paused ? _pausedPosition : _anchor.PositionNow();

    /// <summary>封装信纸：按载荷的真实类型选源生成器元数据，再签名发出去。</summary>
    private void PublishSigned<T>(string topic, T body, bool retain) where T : class
    {
        try
        {
            // 必须走 SyncJsonContext（源生成），不能走反射：单文件裁剪版里反射序列化被禁用。
            JsonElement payload = body switch
            {
                SyncControlMessage c => JsonSerializer.SerializeToElement(c, SyncJsonContext.Default.SyncControlMessage),
                SyncStateMessage s => JsonSerializer.SerializeToElement(s, SyncJsonContext.Default.SyncStateMessage),
                _ => throw new NotSupportedException($"远程同演：没有为 {typeof(T).Name} 登记序列化元数据。"),
            };
            var envelope = new SyncEnvelope
            {
                Sender = _peerId,
                Seq = ++_outSeq,
                Payload = payload,
            };
            envelope.Sign(_room.SecretBytes);
            byte[] bytes = envelope.ToBytes();
            var ws = _ws;
            if (ws == null || ws.State != WebSocketState.Open)
            {
                Log?.Invoke("远程同演：还没连上，消息没发出去。");
                return;
            }
            // QoS 1：带包号，代理回 PUBACK。我们不重传：遥控消息 5 秒后自己会再发一份。
            byte[] packet = MqttPacket.Publish(topic, bytes, retain, NextPacketId());
            _ = SendRawAsync(packet, _cts?.Token ?? CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"远程同演：发消息失败。{ex.Message}");
        }
    }

    private int _outSeq;

    // ================= 收消息 =================

    private void OnControlPayload(byte[] raw)
    {
        if (!SyncEnvelope.VerifyMac(_room.SecretBytes, raw, out var env) || env == null)
        {
            Log?.Invoke("远程同演：收到一条验签不通过的消息，已丢弃。");
            return;
        }
        if (IsDuplicate(env)) return;

        SyncControlMessage? msg;
        try { msg = env.Payload.Deserialize(SyncJsonContext.Default.SyncControlMessage); }
        catch (JsonException) { return; }
        if (msg == null) return;
        if (msg.Sender == _peerId) return;                       // 自己发的，代理也会推回来

        // **Join 必须在成员表校验之前放行。**
        // 新人第一次说话时还不在成员表里，先查成员表就会把它自己的入场申请丢掉，
        // 结果是"双方都连上了、都能收到保留快照，但成员表永远只有主机一个人"。
        if (msg.Kind == SyncMsgKind.Join && _isHost)
        {
            OnPeerJoined(msg.Sender, msg.Name);
            return;
        }

        // 其余消息只认成员表里的人。验签只能证明"消息出自本房间"，不能证明"发的人在场"。
        if (!KnowsPeer(msg.Sender)) return;

        switch (msg.Kind)
        {
            // Join 已在上面单独处理（要赶在成员表校验之前），这里不再重复。

            case SyncMsgKind.Leave:
                if (_isHost) OnPeerLeft(msg.Sender);
                break;

            case SyncMsgKind.Probe:
                // 成员测延迟用的探针，主机原样回一个 Echo，带上自己的时刻。
                // 成员用"发出去到收到"的整段时间当往返延迟，不做双钟比较。
                SendControl(SyncMsgKind.Echo, 0, 0, 1.0);
                break;

            case SyncMsgKind.Echo:
                OnEcho();
                break;

            case SyncMsgKind.Arm:
                if (!_isHost)
                {
                    _transport = SyncTransport.Armed;
                    _countdownSec = msg.DelayMs / 1000;
                    RoomChanged?.Invoke();
                }
                break;

            case SyncMsgKind.Start:
                if (!_isHost) ApplyStart(msg);
                break;

            case SyncMsgKind.Pause:
                if (!_isHost) ApplyPause();
                break;

            case SyncMsgKind.Resume:
                if (!_isHost)
                {
                    int waitMs = RemainingDelayMs(msg);
                    _anchor = new SyncAnchor
                    {
                        PositionSec = msg.PositionSec,
                        AtTickMs = SyncClock.NowMs + Math.Max(0, waitMs),
                        Speed = _speed,
                        Valid = true,
                    };
                    _transport = SyncTransport.Playing;
                    RoomChanged?.Invoke();
                    ResumeRequested?.Invoke();
                }
                break;

            case SyncMsgKind.Seek:
                if (!_isHost)
                {
                    _anchor = _transport == SyncTransport.Playing
                        ? SyncAnchor.At(msg.PositionSec, _speed)
                        : SyncAnchor.Idle;
                    if (_transport != SyncTransport.Playing) _pausedPosition = msg.PositionSec;
                    RoomChanged?.Invoke();
                    SeekRequested?.Invoke(msg.PositionSec);
                }
                break;

            case SyncMsgKind.Rate:
                if (!_isHost)
                {
                    // 与主机同一个口径：算到统一切换时刻的冻结值，等到那一刻再切速度。
                    // 早切或晚切都会留下一个"往返延迟 × 新速度"的位置差。
                    int waitMs = RemainingDelayMs(msg);
                    lock (_gate) ScheduleSpeedChange(msg.Speed, waitMs);
                    RoomChanged?.Invoke();
                    FireAfterDelay(waitMs, () => SpeedRequested?.Invoke(msg.Speed));
                }
                break;

            case SyncMsgKind.Stop:
                if (!_isHost)
                {
                    _anchor = SyncAnchor.Idle;
                    _pausedPosition = 0;
                    _transport = SyncTransport.Stopped;
                    RoomChanged?.Invoke();
                    StopRequested?.Invoke();
                }
                break;
        }
    }

    /// <summary>
    /// 成员收到开演：算出"从计划时刻到本机现在还剩多久"，加到本机单调时刻上。
    ///
    /// 关键一步是减掉**这条消息在路上花掉的时间**（本机收到时刻 减 主机的发出时刻）。
    /// 不减的话，每个成员都会比主机晚一个消息传输时间才开演：实测公开代理下差 280 毫秒以上，
    /// 而且各人延迟不同 → 各人晚的量也不同 → 大家根本不在同一拍上。
    /// 减掉之后，所有人的目标时刻都是"主机发出时刻 + DelayMs"，与各自的网络延迟无关。
    /// </summary>
    private void ApplyStart(SyncControlMessage msg)
    {
        _speed = msg.Speed <= 0 ? 1.0 : msg.Speed;
        int remaining = RemainingDelayMs(msg);
        _anchor = new SyncAnchor
        {
            PositionSec = msg.PositionSec,
            AtTickMs = SyncClock.NowMs + Math.Max(0, remaining),
            Speed = _speed,
            Valid = true,
        };
        _transport = SyncTransport.Playing;
        RoomChanged?.Invoke();
        StartRequested?.Invoke(msg.PositionSec);
    }

    /// <summary>
    /// 把消息里的 DelayMs 换算成本机还要等多久。
    ///
    /// DelayMs 的口径是"主机发出时刻 + DelayMs = 计划开演时刻"。
    /// 本机已经过了 (收到时刻 - 主机发出时刻) 这么久，所以剩下的等待要减掉这一段。
    /// 消息迟到超过 DelayMs 时返回 0：宁可立刻开始，也不要把开演推到更晚。
    /// </summary>
    private int RemainingDelayMs(SyncControlMessage msg) => RemainingDelayCore(msg.DelayMs, msg.HostTickMs, SyncClock.NowMs);

    /// <summary>纯函数版本，供自检直接调用。</summary>
    internal static int RemainingDelayCore(int delayMs, long hostTickMs, long nowTickMs)
    {
        if (hostTickMs <= 0) return Math.Max(0, delayMs);   // 老版本或没带戳，退回旧口径
        long transit = nowTickMs - hostTickMs;
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

    private void OnEcho()
    {
        long now = SyncClock.NowMs;
        int id = _probeCounter;
        // 取最近一次还没结算的探针。代理不保证顺序，但 5 秒内只发一个，够用。
        long sent = 0;
        bool found = false;
        for (int i = id; i >= Math.Max(1, id - 3); i--)
        {
            if (_pendingProbes.TryGetValue(i, out sent)) { found = true; _pendingProbes.Remove(i); break; }
        }
        if (!found) return;
        _clock.AddSample(now - sent);
        _lastProbeTick = now;
        RoomChanged?.Invoke();
    }

    private void OnStatePayload(byte[] raw)
    {
        if (!SyncEnvelope.VerifyMac(_room.SecretBytes, raw, out var env) || env == null) return;
        if (IsDuplicate(env)) return;

        SyncStateMessage? msg;
        try { msg = env.Payload.Deserialize(SyncJsonContext.Default.SyncStateMessage); }
        catch (JsonException) { return; }
        if (msg == null) return;

        if (msg.Host != null)
        {
            ApplyHostState(msg.Host);
            return;
        }

        // 成员上报，只有主机关心
        if (!_isHost || msg.Sender == _peerId || !KnowsPeer(msg.Sender)) return;
        lock (_gate)
        {
            var p = _peers.Find(x => x.Id == msg.Sender);
            if (p == null) return;
            p.RttMs = msg.RttMs;
            p.Ready = msg.Ready;
        }
        if (msg.RttMs > _maxPeerRtt) _maxPeerRtt = msg.RttMs;
        RoomChanged?.Invoke();
    }

    private void ApplyHostState(SyncHostState host)
    {
        lock (_gate)
        {
            if (host.StateSeq <= _lastStateSeq && host.StateSeq != 0) return;
            _lastStateSeq = host.StateSeq;
            // 整份换掉，不要 Clear + Add：别的线程可能在读 _peers（界面刷新、开演判定），
            // Clear 与 Add 之间它们会看到一张空表，于是"成员数 0 人"这种假状态会漏到界面上。
            _peers = new List<SyncPeer>(host.Peers.Count);
            foreach (var p in host.Peers) _peers.Add(ClonePeer(p));
            _fingerprint = host.Fingerprint;
            _durationSec = host.DurationSec;
            _voiceNames = new List<string>(host.VoiceNames);
            _speed = host.Speed <= 0 ? 1.0 : host.Speed;
            SyncTransport previousTransport = _transport;
            _transport = host.Transport;
            _countdownSec = host.CountdownSec;
            _pausedPosition = host.PositionSec;
            _lastHostStateTick = SyncClock.NowMs;
            _lastHostPacketTick = SyncClock.NowMs;

            // 把自己那一行同步回本地字段，让"我现在的声部/移调/就绪"随时可读，
            // 不依赖调用方再去成员表里找自己。
            //
            // **就绪态不从这里回写。** 就绪是本机用户的意愿，主机只是汇总者。
            // 回写会造成死锁式循环：成员上报"我已就绪" → 主机还没处理这条上报时广播的快照里
            // 仍是 false → 成员把 false 抄回自己 → 下一次上报又是 false → 主机永远等不到全员就绪。
            // 实测就是这样卡住的。
            foreach (var p in _peers)
            {
                if (p.Id != _peerId) continue;
                _selfVoices = new List<int>(p.Voices);
                _selfTranspose = p.Transpose;
                p.Ready = _selfReady;   // 镜像里自己那一行以本机为准，别让旧快照盖掉
                break;
            }

            // 主机正在演：把快照里的位置换算成本机锚点。
            //
            // **只有"真的变了"才重设锚点。** 主机的快照每 5 秒无条件来一份，
            // 如果每份都拿来重设锚点，就等于每 5 秒把"这条消息在路上花掉的那段时间"
            // 当成一次后退塞进播放位置：实测公开代理下会让位置的推进速度掉到 0.87 倍。
            // 稳态播放只信本机锚点（这也正是参考项目 Bili-SyncPlay 的做法：
            // "位置从本机单调时钟锚点推算"），偏差交给漂移校正去慢速修。
            if (host.Transport == SyncTransport.Playing)
            {
                long age = Math.Clamp(SyncClock.NowMs - host.HostUnixMs, 0, 10_000);
                double hostPositionNow = host.PositionSec + age / 1000.0 * _speed;
                bool needReanchor = NeedReanchor(previousTransport, _anchor.Valid,
                    hostPositionNow, _anchor.PositionNow());
                if (needReanchor)
                {
                    _anchor = new SyncAnchor
                    {
                        PositionSec = hostPositionNow,
                        AtTickMs = SyncClock.NowMs,
                        Speed = _speed,
                        Valid = true,
                    };
                }
                else
                {
                    _anchor = _anchor.WithSpeed(_speed);
                }
            }
            else if (_transport != SyncTransport.Playing)
            {
                _anchor = SyncAnchor.Idle;
            }
        }
        RoomChanged?.Invoke();
    }

    private bool KnowsPeer(string id)
    {
        lock (_gate)
        {
            if (id == _peerId) return true;
            return _peers.Exists(p => p.Id == id);
        }
    }

    /// <summary>序号去重。QoS 1 是"至少一次"，同一条消息可能到两次。</summary>
    private bool IsDuplicate(SyncEnvelope env)
    {
        lock (_gate)
        {
            string key = env.Sender + "#" + env.Seq;
            // 窗口按条数限个上限，防止长期开着时集合无限增长
            if (_seenSeq.Count > 4096) _seenSeq.Clear();
            return !_seenSeq.Add(key);
        }
    }

    // ================= 主机侧的房间维护 =================

    private void OnPeerJoined(string peerId, string name)
    {
        lock (_gate)
        {
            var existing = _peers.Find(p => p.Id == peerId);
            if (existing == null)
                _peers.Add(new SyncPeer { Id = peerId, Name = string.IsNullOrWhiteSpace(name) ? peerId : name });
            else if (!string.IsNullOrWhiteSpace(name))
                existing.Name = name;   // 改了名字的人再发一次 join 就更新上了
        }
        BroadcastHostState();
        RoomChanged?.Invoke();
    }

    private void OnPeerLeft(string peerId)
    {
        lock (_gate) _peers.RemoveAll(p => p.Id == peerId);
        BroadcastHostState();
        RoomChanged?.Invoke();
    }

    /// <summary>主机：改某个成员的声部与移调。冲突时返回错误文本，改成功返回空串。</summary>
    internal string HostAssign(string peerId, List<int> voices, int transpose)
    {
        if (!_isHost) return "只有主机能分配声部。";
        int conflict = SyncVoicePlan.FirstConflict(voices, SnapshotPeers(), peerId);
        if (conflict >= 0)
        {
            var owner = SnapshotPeers().Find(p => p.Voices.Contains(conflict));
            return $"声部 {conflict + 1} 已经被 {owner?.Name ?? "别人"} 占用。";
        }
        lock (_gate)
        {
            var p = _peers.Find(x => x.Id == peerId);
            if (p == null) return "找不到这个成员。";
            p.Voices = new List<int>(voices);
            p.Transpose = Math.Clamp(transpose, -24, 24);
        }
        BroadcastHostState();
        RoomChanged?.Invoke();
        return "";
    }

    /// <summary>主机：一键分配。按在场人数轮流发牌。</summary>
    internal void HostAutoAssign()
    {
        if (!_isHost) return;
        List<string> ids;
        lock (_gate) ids = _peers.ConvertAll(p => p.Id);
        var plan = SyncVoicePlan.Allocate(_voiceNames.Count, ids.Count);
        lock (_gate)
        {
            for (int i = 0; i < ids.Count; i++)
            {
                var p = _peers.Find(x => x.Id == ids[i]);
                if (p != null) p.Voices = new List<int>(plan[i]);
            }
        }
        BroadcastHostState();
        RoomChanged?.Invoke();
    }

    /// <summary>主机：设定曲目指纹与声部名表。开演前必须一致。</summary>
    internal void HostSetTrack(string fingerprint, double durationSec, List<string> voiceNames)
    {
        lock (_gate)
        {
            _fingerprint = fingerprint;
            _durationSec = durationSec;
            _voiceNames = new List<string>(voiceNames);
        }
        BroadcastHostState();
        RoomChanged?.Invoke();
    }

    /// <summary>
    /// 主机：开演。delayMs 由 <see cref="ComputeStartDelayMs"/> 算出来。
    ///
    /// 主机自己也要等满 delayMs 再真正开始。以前主机把锚点设在"此刻"、立刻开演，
    /// 而成员要等 delayMs，结果是主机比所有人早一个 delayMs 开始 —— 实测差 219 毫秒。
    /// </summary>
    internal void HostStart(double positionSec, int delayMs)
    {
        if (!_isHost) return;
        lock (_gate)
        {
            _transport = SyncTransport.Playing;
            _anchor = new SyncAnchor
            {
                PositionSec = positionSec,
                AtTickMs = SyncClock.NowMs + Math.Max(0, delayMs),
                Speed = _speed,
                Valid = true,
            };
        }
        SendControl(SyncMsgKind.Start, delayMs, positionSec, _speed);
        BroadcastHostState();
        RoomChanged?.Invoke();
        FireAfterDelay(delayMs, () => StartRequested?.Invoke(positionSec));
    }

    /// <summary>
    /// 到点再触发事件。delayMs 为 0 时同步触发，避免多绕一次线程池。
    /// 主机与成员走同一条时间线：两边都是"DelayMs 之后开始"，只是一个在本地记锚点，一个等消息。
    /// </summary>
    private void FireAfterDelay(int delayMs, Action action)
    {
        if (delayMs <= 0)
        {
            action();
            return;
        }
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delayMs).ConfigureAwait(false); }
            catch { return; }
            action();
        });
    }

    /// <summary>
    /// 开演余量：取所有成员往返延迟的最大值的一半，再加 150 毫秒固定余量。
    /// 至少 800 毫秒，否则用户来不及切窗口。
    /// </summary>
    internal int ComputeStartDelayMs(int extraMs = 150)
    {
        double maxRtt = Math.Max(_maxPeerRtt, _clock.RttMs);
        int lead = (int)Math.Ceiling(maxRtt / 2.0) + extraMs;
        return Math.Clamp(lead, 800, 5000);
    }

    internal void HostPause()
    {
        if (!_isHost) return;
        double pos;
        lock (_gate)
        {
            pos = TargetPositionSec;
            _pausedPosition = pos;
            _anchor = SyncAnchor.Idle;
            _transport = SyncTransport.Paused;
        }
        SendControl(SyncMsgKind.Pause, 0, pos, _speed);
        BroadcastHostState();
        RoomChanged?.Invoke();
        PauseRequested?.Invoke();
    }

    internal void HostResume(double positionSec, int delayMs)
    {
        if (!_isHost) return;
        lock (_gate)
        {
            _anchor = new SyncAnchor
            {
                PositionSec = positionSec,
                AtTickMs = SyncClock.NowMs + Math.Max(0, delayMs),
                Speed = _speed,
                Valid = true,
            };
            _transport = SyncTransport.Playing;
        }
        SendControl(SyncMsgKind.Resume, delayMs, positionSec, _speed);
        BroadcastHostState();
        RoomChanged?.Invoke();
        // 与 HostStart 同一个道理：主机也要等满 delayMs，不能自己先跑。
        FireAfterDelay(delayMs, () => ResumeRequested?.Invoke());
    }

    internal void HostSeek(double positionSec)
    {
        if (!_isHost) return;
        lock (_gate)
        {
            if (_transport == SyncTransport.Playing) _anchor = _anchor.WithSpeed(_speed);
            _anchor = SyncAnchor.At(positionSec, _speed);
            if (_transport == SyncTransport.Paused) _pausedPosition = positionSec;
        }
        SendControl(SyncMsgKind.Seek, 0, positionSec, _speed);
        BroadcastHostState();
        RoomChanged?.Invoke();
        SeekRequested?.Invoke(positionSec);
    }

    /// <summary>
    /// 变速的统一切换时刻：位置先停在"旧速度算到切换时刻"的那个值，切换后再按新速度走。
    ///
    /// 为什么要停在那个值，而不是停在"排定这一刻的位置"：
    /// 排定这一刻各机不同（主机是发出时刻，成员是收到时刻，差半个往返）。
    /// 停在各机自己的当前位置，就会把这个差原样变成位置差。
    /// 停在"旧速度走到统一切换时刻"的值，则各机算出来的是同一个数 —— 位置差为 0。
    /// </summary>
    private void ScheduleSpeedChange(double target, int delayMs)
    {
        _speed = target;
        if (_transport != SyncTransport.Playing) return;
        double freezeAt = FreezeValueForSpeedChange(_anchor.PositionNow(), _anchor.Speed, delayMs);
        _anchor = new SyncAnchor
        {
            PositionSec = freezeAt,
            AtTickMs = SyncClock.NowMs + Math.Max(0, delayMs),
            Speed = target,
            Valid = true,
        };
    }

    /// <summary>纯函数版本，供自检直接调用：旧速度走到统一切换时刻的位置。</summary>
    internal static double FreezeValueForSpeedChange(double localPositionSec, double oldSpeed, int remainingDelayMs)
        => localPositionSec + Math.Max(0, remainingDelayMs) / 1000.0 * oldSpeed;

    /// <summary>
    /// 主机：改速度。**定在一个全场统一的未来时刻生效**，不在各自收到的那一刻生效。
    ///
    /// 为什么：变速命令在路上要花半个往返（实测公开代理下 200 到 400 毫秒）。
    /// 如果各自收到就立刻切，主机先切、成员后切，这段时间两边的速度不同，
    /// 于是留下一个"往返延迟 × 新速度"的固定位置差 —— 实测 374 毫秒。
    /// 这和开演是同一类问题，解法也一样：命令里带一个从发出时刻起算的延迟，
    /// 各人在本机等到同一个时刻再切，位置差就是 0，不需要事后再纠正。
    /// </summary>
    internal void HostSetSpeed(double speed)
    {
        if (!_isHost) return;
        double target = Math.Clamp(speed, 0.1, 4.0);
        int delayMs = ComputeControlDelayMs();
        lock (_gate) ScheduleSpeedChange(target, delayMs);
        SendControl(SyncMsgKind.Rate, delayMs, TargetPositionSec, target);
        BroadcastHostState();
        RoomChanged?.Invoke();
        FireAfterDelay(delayMs, () => SpeedRequested?.Invoke(target));
    }

    /// <summary>
    /// 一条控制命令从发出到全场统一的生效时刻，中间要留多久。
    /// 取最大往返延迟的一半再加固定余量，至少 300 毫秒。
    /// </summary>
    internal int ComputeControlDelayMs(int extraMs = 120)
    {
        double maxRtt = Math.Max(_maxPeerRtt, _clock.RttMs);
        int lead = (int)Math.Ceiling(maxRtt / 2.0) + extraMs;
        return Math.Clamp(lead, 300, 3000);
    }

    internal void HostStop()
    {
        if (!_isHost) return;
        lock (_gate)
        {
            _anchor = SyncAnchor.Idle;
            _pausedPosition = 0;
            _transport = SyncTransport.Stopped;
        }
        SendControl(SyncMsgKind.Stop, 0, 0, _speed);
        BroadcastHostState();
        RoomChanged?.Invoke();
        StopRequested?.Invoke();
    }

    /// <summary>
    /// 任何角色：改自己的就绪态。
    /// 本机字段先改，再尽量同步到成员表镜像。镜像里没有自己那一行时（刚连上还没收到主机的快照），
    /// 本地字段仍然是对的，上报出去的就是真话。
    /// </summary>
    internal void SetSelfReady(bool ready)
    {
        lock (_gate)
        {
            _selfReady = ready;
            var p = _peers.Find(x => x.Id == _peerId);
            if (p != null) p.Ready = ready;
        }
        if (_isHost) BroadcastHostState();
        else ReportState();
        RoomChanged?.Invoke();
    }

    /// <summary>任何角色：改自己的声部。主机直接改，成员先查冲突再记本地。</summary>
    internal string SetSelfVoices(List<int> voices)
    {
        if (_isHost) return HostAssign(_peerId, voices, _selfTranspose);
        int conflict = SyncVoicePlan.FirstConflict(voices, SnapshotPeers(), _peerId);
        if (conflict >= 0) return $"声部 {conflict + 1} 已经被别人占用。";
        lock (_gate)
        {
            _selfVoices = new List<int>(voices);
            var p = _peers.Find(x => x.Id == _peerId);
            if (p != null) p.Voices = new List<int>(voices);
        }
        RoomChanged?.Invoke();
        return "";
    }

    /// <summary>任何角色：改自己的移调。主机直接改并广播，成员记本地后等快照。</summary>
    internal void SetSelfTranspose(int semitones)
    {
        int t = Math.Clamp(semitones, -24, 24);
        if (_isHost)
        {
            HostAssign(_peerId, _selfVoices, t);
            return;
        }
        lock (_gate)
        {
            _selfTranspose = t;
            var p = _peers.Find(x => x.Id == _peerId);
            if (p != null) p.Transpose = t;
        }
        RoomChanged?.Invoke();
    }

    /// <summary>
    /// 稳态播放时要不要用快照里的位置重设本机锚点。
    ///
    /// 只有三种情况要重设：刚开始演（含迟到者入场）、本机还没有锚点、快照与本机差得太多（真跳转）。
    /// 其余时候不重设：主机的快照每 5 秒无条件来一份，每份都重设就等于每 5 秒把
    /// "这条消息在路上花掉的时间"当成一次后退塞进播放位置（实测公开代理下推进速度掉到 0.87 倍）。
    /// </summary>
    internal static bool NeedReanchor(SyncTransport previousTransport, bool anchorValid,
        double hostPositionNow, double localPositionNow)
        => previousTransport != SyncTransport.Playing
        || !anchorValid
        || Math.Abs(hostPositionNow - localPositionNow) > ReanchorThresholdSec;

    /// <summary>等所有人都就绪。超时返回 false。人数为 0 时返回 false（没人就没什么可开演的）。</summary>
    internal bool AllReady()
    {
        lock (_gate)
        {
            if (_peers.Count == 0) return false;
            foreach (var p in _peers) if (!p.Ready) return false;
        }
        return true;
    }

    // ================= 漂移校正 =================

    /// <summary>
    /// 每 2 秒调一次（由界面计时器驱动）。判断本机进度相对目标位置偏了多少，给出该做什么。
    ///
    /// 三档处理（阈值见 <see cref="SyncDrift"/>）：
    /// - 偏差小于 30 毫秒：不动。为几十毫秒反复微调反而听得出来。
    /// - 30 到 120 毫秒：返回要用的微调倍率，调用方把它写进
    ///   <see cref="PlaybackEngine.TrimRate"/>，界面上的速度数字不变。
    /// - 超过 120 毫秒：返回"该跳"，调用方把播放头挪到目标位置。
    ///
    /// 为什么不用快照强行对齐：那正是 19.2 节里第六个坑。稳态只信本机锚点。
    /// </summary>
    internal SyncDriftTickResult TickDrift(double localPositionSec)
    {
        double target;
        SyncTransport transport;
        lock (_gate)
        {
            transport = _transport;
            target = _anchor.PositionNow();
        }
        if (transport != SyncTransport.Playing)
            return new SyncDriftTickResult(SyncDrift.Action.None, 1.0, 0, 0);

        double error = target - localPositionSec;
        var action = SyncDrift.Decide(localPositionSec, target, out double trim);
        if (action == SyncDrift.Action.None) trim = 1.0;
        return new SyncDriftTickResult(action, trim, error, target);
    }

    /// <summary>把本机进度与状态上报给主机（主机用它显示进度差）。</summary>
    internal void ReportDrift(double localPositionSec)
    {
        _lastLocalPosition = localPositionSec;
        if (!_isHost) ReportState();
    }

    private double _lastLocalPosition;

    /// <summary>本机最近一次上报的进度（诊断与界面显示用）。</summary>
    internal double LastLocalPositionSec => _lastLocalPosition;
}

/// <summary>一次漂移判断的结果。</summary>
internal readonly record struct SyncDriftTickResult(
    SyncDrift.Action Action,
    double TrimRate,
    double ErrorSec,
    double TargetSec);

/// <summary>连接状态。</summary>
internal enum SyncConnState
{
    Offline = 0,
    Connecting = 1,
    Online = 2,
    Reconnecting = 3,
}

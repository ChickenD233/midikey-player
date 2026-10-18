using System.Text.Json;

namespace MidiKeyPlayer.Engine;

internal sealed class SyncSession : IDisposable
{

    public event Action<SyncConnState>? ConnectionChanged;
    public event Action? RoomChanged;

    public event Action<double>? StartRequested;
    public event Action? PauseRequested;
    public event Action? ResumeRequested;
    public event Action<double>? SeekRequested;
    public event Action<double>? SpeedRequested;
    public event Action? StopRequested;

    public event Action<string>? Log;
    public event Action<string>? Faulted;

    private readonly SyncRoomInfo _room;
    private readonly string _peerId;
    private readonly string _name;
    private readonly bool _isHost;

    private List<SyncPeerInfo> _peers = new();

    private string _selfVoice = "";

    private int _selfTranspose;

    private int? _unifiedTranspose;

    private SyncTransport _transport = SyncTransport.Idle;

    private double _speed = 1.0;
    private int _countdownSec = 3;

    private SyncRelay? _relay;
    private int _selfReadyCount;

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

    internal string SelfVoice => _selfVoice;

    internal int SelfTranspose => _unifiedTranspose ?? _selfTranspose;

    internal int? UnifiedTranspose => _unifiedTranspose;

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

    internal double TargetPositionSec => _anchor.PositionNow();

    internal int LeadInMs
    {
        get
        {
            long left = _anchor.AtTickMs - SyncClock.NowMs;
            return left <= 0 ? 0 : (int)left;
        }
    }

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

        if (delayMs <= 0) SeekRequested?.Invoke(position);
        else StartRequested?.Invoke(position);
    }

    internal static int RemainingDelayCore(int delayMs, long sentTickMs, long nowTickMs)
    {
        if (sentTickMs <= 0) return Math.Max(0, delayMs);   
        long transit = nowTickMs - sentTickMs;
        if (transit < 0) transit = 0;                        
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

    internal void SetSelfVoice(string voice)
    {
        if (voice == _selfVoice) return;
        _selfVoice = voice;
        _relay?.Send(new SyncMessage { T = SyncKind.Voice, Voice = voice });
    }

    internal void SetSelfTranspose(int semitones)
    {
        int t = Math.Clamp(semitones, -24, 24);
        if (t == _selfTranspose) return;
        _selfTranspose = t;
        RoomChanged?.Invoke();
    }

    internal void SetSelfReady(bool ready)
    {
        int want = ready ? 1 : 0;
        if (want == _selfReadyCount) return;
        _selfReadyCount = want;
        _relay?.Send(new SyncMessage { T = SyncKind.Ready, On = ready });
    }

    internal void HostSetUnifiedTranspose(int? semitones)
    {
        if (!_isHost) return;
        _relay?.Send(new SyncMessage { T = SyncKind.Xpose, Xpose = semitones });
    }

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

    internal void HostSeek(double positionSec)
    {
        if (!_isHost) return;
        _anchor = SyncAnchor.At(positionSec, _speed);
        _transport = SyncTransport.Playing;
        _relay?.Send(new SyncMessage { T = SyncKind.Start, DelayMs = 0, PositionSec = positionSec });
        RoomChanged?.Invoke();
        SeekRequested?.Invoke(positionSec);
    }

    internal void HostSetSpeed(double speed)
    {
        if (!_isHost) return;
        _speed = Math.Clamp(speed, 0.1, 4.0);
        if (_transport == SyncTransport.Playing) _anchor = _anchor.WithSpeed(_speed);
        RoomChanged?.Invoke();
        SpeedRequested?.Invoke(_speed);
    }

    internal bool AllReady()
    {
        lock (_peers)
        {
            if (_peers.Count < 2) return false;
            foreach (var p in _peers) if (!p.Ready) return false;
        }
        return true;
    }

    internal string DescribeSelf()
    {
        string voice = _selfVoice.Length > 0 ? _selfVoice : "（未选）";
        return $"声部 {voice}　移调 {SelfTranspose:+#;-#;0}";
    }
}

internal enum SyncTransport
{
    Idle = 0,       
    Playing = 2,
    Paused = 3,
    Stopped = 4,
}

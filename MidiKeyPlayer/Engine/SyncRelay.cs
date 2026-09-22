using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace MidiKeyPlayer.Engine;

/// <summary>
/// 【实验功能·远程同演】与中转站之间的连接。
///
/// 就是一根 WebSocket：说 JSON，收到什么就交给上层。没有心跳、没有轮询、
/// 没有周期上报 —— 只有人真的操作时才发消息，这是刻意的，为了把用量压到最低。
///
/// 与旧的 MQTT 方案比，这里省掉了：报文编解码、订阅握手、保活、
/// 发布/订阅语义、保留消息。中转站自己负责转发，不需要那套东西。
///
/// 线程约定：事件都在后台线程触发，界面订阅方自己 marshal 回 UI 线程。
/// </summary>
internal sealed class SyncRelay : IDisposable
{
    /// <summary>连接状态变了。</summary>
    public event Action<SyncConnState>? ConnectionChanged;

    /// <summary>收到一条消息。参数是解析好的消息对象。</summary>
    public event Action<SyncMessage>? MessageReceived;

    /// <summary>要写进日志的一行。</summary>
    public event Action<string>? Log;

    /// <summary>连接中断，重连次数用完了。</summary>
    public event Action<string>? Faulted;

    /// <summary>重连次数上限。到顶就停手，不无限打扰服务器。</summary>
    private const int MaxReconnect = 10;

    private readonly SyncRoomInfo _room;
    private readonly string _peerId;
    private readonly string _name;
    private readonly string _voice;
    private readonly int _xpose;

    private readonly object _gate = new();
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private SyncConnState _conn = SyncConnState.Offline;
    private int _reconnectCount;
    private int _generation;

    /// <summary>第一条消息（join）还没发出去时挂在这里，连上就发。</summary>
    private SyncMessage? _pendingJoin;

    internal SyncRelay(SyncRoomInfo room, string peerId, string name, string voice, int xpose)
    {
        _room = room;
        _peerId = peerId;
        _name = name;
        _voice = voice;
        _xpose = xpose;
    }

    internal SyncConnState Connection => _conn;
    internal bool IsOnline => _conn == SyncConnState.Online;

    /// <summary>
    /// 开始连接。第一条 join 消息带着房间名以外的全部身份信息（密码、名字、声部）。
    /// 房间名不进网络：它已经变成连接地址里的房间键了。
    /// </summary>
    public void Connect(string voice, int xpose)
    {
        _pendingJoin = new SyncMessage
        {
            T = SyncKind.Join,
            Id = _peerId,
            Name = _name,
            Pass = _room.Password,
            Voice = voice,
            Xpose = xpose,
        };
        _reconnectCount = 0;
        _ = ConnectLoopAsync();
    }

    /// <summary>发一条消息。没连上就丢掉并记一行日志。</summary>
    internal void Send(SyncMessage msg)
    {
        var ws = _ws;
        if (ws == null || ws.State != WebSocketState.Open)
        {
            Log?.Invoke("远程同演：还没连上，这条操作没发出去。");
            return;
        }
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(msg, SyncJsonContext.Default.SyncMessage);
            _ = ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true,
                _cts?.Token ?? CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"远程同演：发消息失败。{ex.Message}");
        }
    }

    private async Task ConnectLoopAsync()
    {
        while (_reconnectCount <= MaxReconnect)
        {
            int generation = ++_generation;
            try
            {
                await ConnectOnceAsync(generation).ConfigureAwait(false);
                if (_conn == SyncConnState.Offline) return;   // 用户主动断开
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
                Faulted?.Invoke($"远程同演：重连 {MaxReconnect} 次都失败，已停止。可以点「退出房间」再重进。");
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
            Log?.Invoke($"远程同演：连不上中转站 {_room.Host}。{ex.Message}");
            throw;
        }

        SetConn(SyncConnState.Online);
        _reconnectCount = 0;
        Log?.Invoke($"远程同演：已连上中转站 {_room.Host}，房间「{_room.RoomName}」。");

        // 连上就报身份。重连时也重发一次，服务器那边当同一个人重新登记。
        if (_pendingJoin != null) Send(_pendingJoin);

        var chunk = new byte[8192];
        var buffer = new List<byte>();

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

            // 一条消息可能分几帧到，收全了才解析
            if (!result.EndOfMessage) continue;

            if (buffer.Count > 0)
            {
                try
                {
                    var msg = JsonSerializer.Deserialize(
                        new ReadOnlySpan<byte>(buffer.ToArray()), SyncJsonContext.Default.SyncMessage);
                    if (msg != null) OnMessage(msg);
                }
                catch (JsonException ex)
                {
                    Log?.Invoke($"远程同演：收到读不懂的消息，已忽略。{ex.Message}");
                }
                buffer.Clear();
            }
        }

        if (generation == _generation) SetConn(SyncConnState.Reconnecting);
    }

    private void OnMessage(SyncMessage msg)
    {
        if (msg.T == SyncKind.Error)
        {
            // 服务器明确拒绝：密码不对、房间满了、发得太快。这些重连也没用，直接告诉用户。
            Log?.Invoke($"远程同演：房间拒绝了这次连接（{msg.Reason}）。");
            Faulted?.Invoke($"远程同演：{msg.Reason}。");
            return;
        }
        MessageReceived?.Invoke(msg);
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
}

/// <summary>连接状态。</summary>
internal enum SyncConnState
{
    Offline = 0,
    Connecting = 1,
    Online = 2,
    Reconnecting = 3,
}

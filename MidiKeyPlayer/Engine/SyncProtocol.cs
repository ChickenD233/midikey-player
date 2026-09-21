using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MidiKeyPlayer.Engine;

/// <summary>
/// 远程同演的消息序列化上下文。
///
/// **必须用源生成器，不能用反射。** 本程序发布时开裁剪（PublishTrimmed），
/// 裁剪后的单文件里 JsonSerializer 默认禁用基于反射的序列化，会直接抛
/// "Reflection-based serialization has been disabled for this application"。
/// 这里列出远程同演用到的全部类型，源生成器会为它们生成元数据。
/// 新增消息类型时，记得在这里补一行 <c>[JsonSerializable]</c>。
/// </summary>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(SyncEnvelope))]
[JsonSerializable(typeof(SyncControlMessage))]
[JsonSerializable(typeof(SyncStateMessage))]
[JsonSerializable(typeof(SyncHostState))]
[JsonSerializable(typeof(SyncPeer))]
[JsonSerializable(typeof(List<SyncPeer>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<int>))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class SyncJsonContext : JsonSerializerContext
{
}

/// <summary>一个成员的公开状态。所有字段都能被同房间的人看到。</summary>
internal sealed class SyncPeer
{
    /// <summary>成员号。本地生成，形如 "m3f2a1c9"。重连后不变，用来认人。</summary>
    public string Id { get; set; } = "";

    /// <summary>显示名。同名不禁止，只影响可读性。</summary>
    public string Name { get; set; } = "";

    /// <summary>这个人占用的声部序号（与左侧列表同口径）。</summary>
    public List<int> Voices { get; set; } = new();

    /// <summary>这个人锁定的移调，半音，范围见 AppConfig.MaxTransposeSemitones。</summary>
    public int Transpose { get; set; }

    /// <summary>是否已就绪。开演前主机要看这一列。</summary>
    public bool Ready { get; set; }

    /// <summary>到主机的往返延迟（毫秒）。主机用它算开演余量。</summary>
    public double RttMs { get; set; }
}

/// <summary>
/// 房间快照。state 主题上的保留消息就是这个对象，迟到者一订阅就拿到。
///
/// 只有主机写它。成员只读，另外用 <see cref="SyncStateMessage"/> 上报自己的进度差与就绪态。
/// </summary>
internal sealed class SyncHostState
{
    /// <summary>快照序号。主机每次改写加一。成员用它判断"是不是新状态"。</summary>
    public int StateSeq { get; set; }

    /// <summary>主机的成员号。谁都可能是主机，所以主机号必须写在状态里。</summary>
    public string HostId { get; set; } = "";

    /// <summary>成员表。</summary>
    public List<SyncPeer> Peers { get; set; } = new();

    /// <summary>曲目指纹（MIDI 文件字节的 SHA-256 前 16 位十六进制）。空 = 还没载入曲子。</summary>
    public string Fingerprint { get; set; } = "";

    /// <summary>曲目总时长（秒）。与指纹一起校验"两边是同一份文件"。</summary>
    public double DurationSec { get; set; }

    /// <summary>声部名表，按声部序号排。用于显示与校验。</summary>
    public List<string> VoiceNames { get; set; } = new();

    public double Speed { get; set; } = 1.0;
    public double PositionSec { get; set; }
    public SyncTransport Transport { get; set; } = SyncTransport.Idle;

    /// <summary>本快照写出的时刻（主机本机毫秒，DateTimeOffset.ToUnixTimeMilliseconds）。</summary>
    public long HostUnixMs { get; set; }

    /// <summary>倒计时剩余秒数。arm 之后到 start 之前有效。</summary>
    public int CountdownSec { get; set; }
}

/// <summary>房间的播放状态。与 <see cref="PlaybackEngine"/> 的运行态一一对应。</summary>
internal enum SyncTransport
{
    Idle = 0,       // 还没开演
    Armed = 1,      // 倒计时中，等全员就绪
    Playing = 2,
    Paused = 3,
    Stopped = 4,
}

/// <summary>一条控制消息。ctl 主题上的载荷就是这个对象。</summary>
internal sealed class SyncControlMessage
{
    public SyncMsgKind Kind { get; set; }

    /// <summary>发送者的成员号。</summary>
    public string Sender { get; set; } = "";

    /// <summary>发送者的显示名。只有 Join 会用到，别的时候留空。</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// 主机**发出**这条消息那一刻起，到**计划开演时刻**之间的毫秒数。
    ///
    /// 注意不是"收到之后再等这么久"：收到方要先减掉消息在路上花掉的时间。
    /// 不减的话，每个成员都会比主机晚整整一个消息传输时间才开演（实测公开代理下 280 毫秒以上）。
    /// </summary>
    public int DelayMs { get; set; }

    /// <summary>
    /// 主机发出这条消息时的**主机单调时刻**（<see cref="SyncClock.NowMs"/>）。
    ///
    /// 收到方拿"本机收到时刻"减它，就得到"这条消息在路上走了多久"。
    /// 跨机器的单调时钟绝对值不可比，但同一条消息的发出与收到之差是可测的，
    /// 所以这里只用差值，不用绝对值。这就是 DelayMs 能换算成本机等待时长的依据。
    /// </summary>
    public long HostTickMs { get; set; }

    /// <summary>消息里的曲子位置（秒）。</summary>
    public double PositionSec { get; set; }

    /// <summary>速度倍率（0.1 到 4.0）。</summary>
    public double Speed { get; set; } = 1.0;
}

/// <summary>
/// 一条状态消息（state 主题）。主机发完整快照，成员只发自己那几项。
/// 代理的保留消息会让最后一次写入留着，所以成员上报也写在这里，但主机每次都会覆盖回完整快照。
/// </summary>
internal sealed class SyncStateMessage
{
    public string Sender { get; set; } = "";

    /// <summary>非空 = 这是一份完整快照（主机发的）。空 = 这是成员上报。</summary>
    public SyncHostState? Host { get; set; }

    /// <summary>成员上报：本机位置（秒）。</summary>
    public double PositionSec { get; set; }

    /// <summary>成员上报：本机到主机的往返延迟（毫秒）。</summary>
    public double RttMs { get; set; }

    public bool Ready { get; set; }
}

/// <summary>控制消息的类型。</summary>
internal enum SyncMsgKind
{
    Join = 0,       // 有人进来
    Leave = 1,      // 有人退出
    Arm = 2,        // 主机：进入倒计时
    Start = 3,      // 主机：定时开演
    Pause = 4,      // 主机：暂停
    Resume = 5,     // 主机：继续
    Seek = 6,       // 主机：跳转
    Rate = 7,       // 主机：变速
    Stop = 8,       // 主机：本轮结束
    Probe = 9,      // 成员：测延迟
    Echo = 10,      // 主机：回延迟
}

/// <summary>
/// 带上鉴权的一层信封。ctl 与 state 两个主题的载荷都是它。
///
/// 代理不做鉴权，所以自己加：
/// - Mac：HMAC-SHA256(房间密钥, Payload 的 JSON 字节) 的前 16 字节，写成 32 位十六进制。
///   密钥在建房时生成 128 位随机值，随邀请串发给成员。代理看不到密钥就看不懂，也改不了。
/// - Seq：递增序号。QoS 1 是"至少一次"，同一条消息可能到两次，用序号去重。
/// </summary>
internal sealed class SyncEnvelope
{
    public string Sender { get; set; } = "";
    public int Seq { get; set; }
    public string Mac { get; set; } = "";

    /// <summary>真正的消息体。用 JsonElement 而不是泛型：两个主题的载荷类型不同，一个信封要装两种。</summary>
    public JsonElement Payload { get; set; }

    internal static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        TypeInfoResolver = SyncJsonContext.Default,
    };

    /// <summary>序列化信封，不带 Mac。Mac 要算在"去掉 Mac 之后"的字节上。</summary>
    private byte[] SerializeWithoutMac()
    {
        string? saved = Mac;
        Mac = "";
        try { return JsonSerializer.SerializeToUtf8Bytes(this, Options); }
        finally { Mac = saved ?? ""; }
    }

    /// <summary>算并写入 Mac。</summary>
    internal void Sign(byte[] key)
    {
        byte[] mac = HMACSHA256.HashData(key, SerializeWithoutMac());
        Mac = Convert.ToHexString(mac.AsSpan(0, 16)).ToLowerInvariant();
    }

    /// <summary>
    /// 校验 Mac。**必须在读 Payload 之前调用。**
    ///
    /// 校验的是"收到的那串原始字节"，而不是反序列化再序列化的结果：
    /// 后者会因为数字与转义写法变化算出不同的 Mac，把好消息误判成伪造。
    /// </summary>
    internal static bool VerifyMac(byte[] key, byte[] rawPayload, out SyncEnvelope? envelope)
    {
        envelope = null;
        try
        {
            envelope = JsonSerializer.Deserialize<SyncEnvelope>(rawPayload, Options);
        }
        catch (JsonException)
        {
            return false;
        }
        if (envelope == null || envelope.Mac.Length != 32) return false;

        // 信纸自己算：把 Mac 清成空串再序列化。**占位符必须与 SerializeWithoutMac 完全一致**
        // （空串，不是 32 个 0）：长度不同的占位符会把 JSON 变长，算出来的 Mac 必然对不上。
        string? saved = envelope.Mac;
        envelope.Mac = "";
        byte[] canonical;
        try { canonical = JsonSerializer.SerializeToUtf8Bytes(envelope, Options); }
        finally { envelope.Mac = saved; }

        byte[] expected = HMACSHA256.HashData(key, canonical);
        string expectHex = Convert.ToHexString(expected.AsSpan(0, 16)).ToLowerInvariant();
        bool ok = CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expectHex), Encoding.ASCII.GetBytes(envelope.Mac.ToLowerInvariant()));
        if (!ok) envelope = null;
        return ok;
    }

    /// <summary>把信封序列化成可以发的字节（先 <see cref="Sign"/>）。</summary>
    internal byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Options);
}

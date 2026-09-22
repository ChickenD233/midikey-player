using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MidiKeyPlayer.Engine;

/// <summary>
/// 【实验功能·远程同演】房间凭据：中转站地址、房间名、密码。
///
/// 房间名与密码都由用户自己填。房间名不进网络：本类把它算成一个房间键
/// （PBKDF2，10 万次迭代），键放进连接地址，服务器只看到键。
/// 服务器上的密码校验值也用这个键当盐，所以不存明文密码，也不存可反推的值。
///
/// 邀请串把三样打包成一行，用户只复制一次：mkp://中转站|房间名|密码。
/// </summary>
internal sealed class SyncRoomInfo
{
    /// <summary>
    /// 默认中转站。**发布前把这里改成你自己的 Worker 地址。**
    /// 用户界面上的「高级：中转站地址」留空时就用它。
    /// 没有默认值时用户必须自己填，那就失去了"粘一条邀请串就能进"的方便。
    /// </summary>
    internal const string DefaultRelayHost = "";

    /// <summary>邀请串的协议前缀，用来认出自家的串。</summary>
    internal const string Scheme = "mkp";

    /// <summary>房间键的迭代次数。必须与 Worker（cloudflare/src/index.js）里的数字一致。</summary>
    internal const int KeyIterations = 100_000;

    /// <summary>房间键的派生盐。必须与 Worker 里的写法一致。</summary>
    private const string KeySalt = "midikeyplayer:room:v1";

    /// <summary>中转站主机名，例如 midikeyplayer-sync.你的名字.workers.dev。</summary>
    public string Host { get; init; } = DefaultRelayHost;

    /// <summary>房间名。用户自定义，例如「小明的琴房」。</summary>
    public string RoomName { get; init; } = "";

    /// <summary>房间密码。用户自定义。</summary>
    public string Password { get; init; } = "";

    /// <summary>连接地址。房间键放在查询参数里。</summary>
    public string WebSocketUrl => $"wss://{Host}/room?k={RoomKey}";

    /// <summary>房间键：房间名经 PBKDF2 派生，64 位小写十六进制。</summary>
    public string RoomKey => DeriveRoomKey(RoomName);

    /// <summary>
    /// 房间名 → 房间键。加盐迭代，别人拿到服务器的库也难反推房间名。
    /// Worker 那边用同一个盐与同一个迭代次数，但它不校验这个值 —— 它只把它当密码的盐。
    /// </summary>
    internal static string DeriveRoomKey(string roomName)
    {
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(roomName),
            Encoding.UTF8.GetBytes(KeySalt),
            KeyIterations,
            HashAlgorithmName.SHA256,
            32);
        return Convert.ToHexString(key).ToLowerInvariant();
    }

    /// <summary>房间名是不是可用的：非空、不太长、没有控制字符。</summary>
    internal static bool IsValidRoomName(string name)
    {
        string s = name.Trim();
        if (s.Length == 0 || s.Length > 32) return false;
        foreach (char c in s) if (char.IsControl(c)) return false;
        return true;
    }

    /// <summary>密码是不是可用的。允许空密码（等于不设防），但太长要拒。</summary>
    internal static bool IsValidPassword(string password) => password.Length <= 128;

    /// <summary>成员号：128 位随机，形如 m3f2a1c9。重装程序后会变，那没关系。</summary>
    internal static string NewPeerId() => "m" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    /// <summary>邀请串，形如 mkp://中转站|房间名|密码。</summary>
    public string ToInvite() => $"{Scheme}://{Host}|{RoomName}|{Password}";

    /// <summary>解析邀请串。失败时 <paramref name="error"/> 是人话，可以直接显示。</summary>
    internal static bool TryParse(string? text, out SyncRoomInfo? room, out string error)
    {
        room = null;
        error = "";
        string s = (text ?? "").Trim();
        if (s.Length == 0)
        {
            error = "邀请串是空的。";
            return false;
        }

        const string prefix = Scheme + "://";
        if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) s = s[prefix.Length..];
        else if (s.Contains("://", StringComparison.Ordinal))
        {
            error = "邀请串的协议前缀不对，应该以 mkp:// 开头。";
            return false;
        }

        // 用 | 分段。房间名与密码都可能被聊天软件换行截断，所以先只按 | 切。
        string[] parts = s.Split('|');
        if (parts.Length != 3)
        {
            error = "邀请串应该有三段，用两条竖线分开：中转站|房间名|密码。";
            return false;
        }

        string host = NormalizeHost(parts[0]);
        string name = parts[1].Trim();
        string password = parts[2];

        if (host.Length == 0)
        {
            error = "邀请串里没有中转站地址。";
            return false;
        }
        if (!IsValidRoomName(name))
        {
            error = "邀请串里的房间名不合法（1 到 32 个字符）。";
            return false;
        }
        if (!IsValidPassword(password))
        {
            error = "邀请串里的密码太长。";
            return false;
        }

        room = new SyncRoomInfo { Host = host, RoomName = name, Password = password };
        return true;
    }

    /// <summary>
    /// 把用户填的中转站地址归一化：去掉协议前缀、路径与结尾斜杠。
    /// 用户可能粘 <c>https://x.workers.dev/</c> 或 <c>x.workers.dev</c>，两种都要认。
    /// </summary>
    internal static string NormalizeHost(string? text)
    {
        string s = (text ?? "").Trim();
        foreach (string prefix in new[] { "wss://", "ws://", "https://", "http://" })
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) s = s[prefix.Length..];
        int slash = s.IndexOf('/');
        if (slash >= 0) s = s[..slash];
        return s.Trim();
    }
}

// ================= 消息 =================

/// <summary>
/// 一条同演消息。上行下行共用一个形状，用 <see cref="T"/> 区分类型。
///
/// 只有八种类型，刻意压到最少：
/// join / leave / ready / voice / xpose / start / pause / stop
/// 再加上服务器发的 welcome / roster / error。
///
/// 没有心跳、没有轮询、没有周期上报：不用到的字段就空着。
/// </summary>
internal sealed class SyncMessage
{
    /// <summary>类型。用短名，省字节。</summary>
    [JsonPropertyName("t")] public string T { get; set; } = "";

    /// <summary>协议版本，只在 welcome 里用。</summary>
    [JsonPropertyName("v")] public int V { get; set; }

    /// <summary>发送者成员号。服务器填，客户端不填。</summary>
    [JsonPropertyName("from")] public string? From { get; set; }

    // —— join ——
    /// <summary>成员号。</summary>
    [JsonPropertyName("id")] public string? Id { get; set; }

    /// <summary>显示名。</summary>
    [JsonPropertyName("name")] public string? Name { get; set; }

    /// <summary>密码。只在 join 里发一次，之后不发。</summary>
    [JsonPropertyName("pass")] public string? Pass { get; set; }

    /// <summary>声部显示文本，例如 "2、4"。</summary>
    [JsonPropertyName("voice")] public string? Voice { get; set; }

    /// <summary>移调半音。</summary>
    [JsonPropertyName("xpose")] public int? Xpose { get; set; }

    // —— 演出控制 ——
    /// <summary>开演延迟（毫秒）。语义：房主发出时刻起，过这么久全场一起开始。</summary>
    [JsonPropertyName("delayMs")] public int? DelayMs { get; set; }

    /// <summary>
    /// 房主发出这条命令时的**本机单调时刻**。
    /// 收到方拿"本机收到时刻"减它，就得到"这条消息在路上走了多久"，再减掉它才是该等的时长。
    /// 跨机器的单调时钟绝对值不可比，但同一条消息的发出与收到之差是可测的，所以只用差值。
    /// </summary>
    [JsonPropertyName("sentAt")] public long? SentAt { get; set; }

    /// <summary>起始位置（秒）。</summary>
    [JsonPropertyName("positionSec")] public double? PositionSec { get; set; }

    /// <summary>就绪开关。</summary>
    [JsonPropertyName("on")] public bool? On { get; set; }

    // —— 服务器下行 ——
    /// <summary>成员名单。</summary>
    [JsonPropertyName("peers")] public List<SyncPeerInfo>? Peers { get; set; }

    /// <summary>拒绝原因（人话）。</summary>
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

/// <summary>名单里的一个人。</summary>
internal sealed class SyncPeerInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("voice")] public string Voice { get; set; } = "";
    [JsonPropertyName("xpose")] public int Xpose { get; set; }
    [JsonPropertyName("ready")] public bool Ready { get; set; }
}

/// <summary>消息类型名。用常量而不是枚举：上行下行共用一个字符串字段，省一层转换。</summary>
internal static class SyncKind
{
    internal const string Join = "join";
    internal const string Leave = "leave";
    internal const string Ready = "ready";
    internal const string Voice = "voice";
    internal const string Xpose = "xpose";
    internal const string Start = "start";
    internal const string Pause = "pause";
    internal const string Stop = "stop";
    internal const string Welcome = "welcome";
    internal const string Roster = "roster";
    internal const string Error = "error";
}

/// <summary>
/// 【实验功能·远程同演】消息的 JSON 序列化上下文。
///
/// **必须用源生成器，不能用反射。** 发布时开裁剪，裁剪后的单文件里
/// JsonSerializer 默认禁用基于反射的序列化，会直接抛
/// "Reflection-based serialization has been disabled for this application"。
/// 新增消息字段不用改这里；新增类型才要补一行 <c>[JsonSerializable]</c>。
/// </summary>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(SyncMessage))]
[JsonSerializable(typeof(SyncPeerInfo))]
[JsonSerializable(typeof(List<SyncPeerInfo>))]
internal sealed partial class SyncJsonContext : JsonSerializerContext
{
}

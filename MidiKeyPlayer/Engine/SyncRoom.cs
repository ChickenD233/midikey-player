using System.Security.Cryptography;

namespace MidiKeyPlayer.Engine;

/// <summary>
/// 【实验功能·远程同演】房间的身份：房间码、密钥、邀请串、主题名。
///
/// 设计要点：
/// - 房间码 128 位随机、密钥 128 位随机。都在本机生成，不经过代理。
/// - 邀请串把"代理地址、房间码、密钥"三样打包成一行，用户只复制一次。
///   服务器地址因此不需要内置到 exe 里，用户也不用分别填四样东西。
/// - 主题名由房间码派生。猜不到房间码就订阅不到，这层是公共代理上的第一道防护。
/// </summary>
internal sealed class SyncRoomInfo
{
    /// <summary>默认代理。EMQX 的公开代理是地理分布集群，国内延迟一般比 HiveMQ 低。</summary>
    internal const string DefaultBrokerHost = "broker.emqx.io";

    /// <summary>默认端口。8084 = 加密 WebSocket。</summary>
    internal const int DefaultBrokerPort = 8084;

    /// <summary>WebSocket 路径。两个公开代理都用 /mqtt。</summary>
    internal const string DefaultBrokerPath = "/mqtt";

    /// <summary>邀请串的协议前缀，用来认出自家的串。</summary>
    internal const string Scheme = "mkp";

    /// <summary>代理主机名或 IP。</summary>
    public string Host { get; init; } = DefaultBrokerHost;

    /// <summary>代理端口。</summary>
    public int Port { get; init; } = DefaultBrokerPort;

    /// <summary>WebSocket 路径。</summary>
    public string Path { get; init; } = DefaultBrokerPath;

    /// <summary>是否用加密 WebSocket（wss）。端口 8083 是明文，其余都当加密。</summary>
    public bool Secure => Port != 8083;

    /// <summary>128 位房间码，32 位小写十六进制。</summary>
    public string RoomCode { get; init; } = "";

    /// <summary>128 位房间密钥，32 位小写十六进制。用来算 HMAC。</summary>
    public string Secret { get; init; } = "";

    /// <summary>控制消息主题。QoS 1，不保留。</summary>
    public string ControlTopic => $"mkp/{RoomCode}/ctl";

    /// <summary>房间快照主题。QoS 1，保留。迟到者一订阅就拿到最后一份快照。</summary>
    public string StateTopic => $"mkp/{RoomCode}/state";

    /// <summary>WebSocket 地址，例如 wss://broker.emqx.io:8084/mqtt。</summary>
    public string WebSocketUrl => $"{(Secure ? "wss" : "ws")}://{Host}:{Port}{Path}";

    /// <summary>密钥的字节形式，算 HMAC 用。</summary>
    public byte[] SecretBytes => Convert.FromHexString(Secret);

    /// <summary>建一个新房间。房间码与密钥都取 128 位系统随机数。</summary>
    internal static SyncRoomInfo Create(string? host = null, int? port = null, string? path = null)
        => new()
        {
            Host = string.IsNullOrWhiteSpace(host) ? DefaultBrokerHost : host.Trim(),
            Port = port ?? DefaultBrokerPort,
            Path = NormalizePath(path),
            RoomCode = RandomHex(16),
            Secret = RandomHex(16),
        };

    /// <summary>生成 n 字节的密码学随机数，写成 2n 位小写十六进制。</summary>
    internal static string RandomHex(int bytes)
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    /// <summary>路径统一成以 / 开头、不以 / 结尾。空值用默认值。</summary>
    internal static string NormalizePath(string? path)
    {
        string p = string.IsNullOrWhiteSpace(path) ? DefaultBrokerPath : path.Trim();
        if (!p.StartsWith('/')) p = "/" + p;
        if (p.Length > 1 && p.EndsWith('/')) p = p.TrimEnd('/');
        return p;
    }

    /// <summary>成员号：128 位随机，形如 m3f2a1c9。重连后要沿用同一个，用来认人。</summary>
    internal static string NewPeerId() => "m" + RandomHex(8);

    /// <summary>邀请串，形如 mkp://host:port/path|房间码|密钥。</summary>
    public string ToInvite()
        => $"{Scheme}://{Host}:{Port}{Path}|{RoomCode}|{Secret}";

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

        // 允许用户漏掉前缀，直接粘 "host:port/mqtt|房间码|密钥"
        const string prefix = Scheme + "://";
        if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) s = s[prefix.Length..];
        else if (s.Contains("://", StringComparison.Ordinal))
        {
            error = "邀请串的协议前缀不对，应该以 mkp:// 开头。";
            return false;
        }

        // 用 | 分段。密钥可能被聊天软件换行截断，所以先只按 | 切。
        string[] parts = s.Split('|');
        if (parts.Length != 3)
        {
            error = "邀请串应该有三段，用两条竖线分开：代理地址|房间码|密钥。";
            return false;
        }

        string endpoint = parts[0].Trim();
        string code = parts[1].Trim();
        string secret = parts[2].Trim();
        if (secret.Length == 0)
        {
            error = "邀请串的密钥段是空的。";
            return false;
        }

        if (!IsHex(code, 32))
        {
            error = "房间码应该是 32 位十六进制。";
            return false;
        }
        if (!IsHex(secret, 32))
        {
            error = "密钥应该是 32 位十六进制。";
            return false;
        }

        string host;
        int port = DefaultBrokerPort;
        string path = DefaultBrokerPath;

        // host:port/path 拆开。注意 IPv6 会带方括号，这里不支持，直接报错让用户换写法。
        int slash = endpoint.IndexOf('/');
        string hostPort = slash >= 0 ? endpoint[..slash] : endpoint;
        if (slash >= 0) path = NormalizePath(endpoint[slash..]);
        if (hostPort.Contains(':'))
        {
            int colon = hostPort.LastIndexOf(':');
            string portText = hostPort[(colon + 1)..];
            if (!int.TryParse(portText, out port) || port < 1 || port > 65535)
            {
                error = "邀请串里的端口不是 1 到 65535 之间的数字。";
                return false;
            }
            host = hostPort[..colon];
        }
        else
        {
            host = hostPort;
        }

        if (host.Length == 0)
        {
            error = "邀请串里没有代理地址。";
            return false;
        }

        room = new SyncRoomInfo
        {
            Host = host,
            Port = port,
            Path = NormalizePath(path),
            RoomCode = code.ToLowerInvariant(),
            Secret = secret.ToLowerInvariant(),
        };
        return true;
    }

    private static bool IsHex(string s, int length)
    {
        if (s.Length != length) return false;
        foreach (char c in s)
        {
            bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!ok) return false;
        }
        return true;
    }
}

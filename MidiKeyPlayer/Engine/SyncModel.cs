using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MidiKeyPlayer.Engine;

internal sealed class SyncRoomInfo
{
    internal const string DefaultRelayHost = "midikeyplayer-sync.dl18710255985.workers.dev";

    internal const string Scheme = "mkp";

    internal const int KeyIterations = 100_000;

    private const string KeySalt = "midikeyplayer:room:v1";

    public string Host { get; init; } = DefaultRelayHost;

    public string RoomName { get; init; } = "";

    public string Password { get; init; } = "";

    public bool Secure => !IsLoopback(Host);

    private static bool IsLoopback(string host)
        => host.StartsWith("127.", StringComparison.Ordinal)
        || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.Equals("::1", StringComparison.Ordinal);

    public string WebSocketUrl => $"{(Secure ? "wss" : "ws")}://{Host}/room?k={RoomKey}";

    public string RoomKey => DeriveRoomKey(RoomName);

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

    internal static bool IsValidRoomName(string name)
    {
        string s = name.Trim();
        if (s.Length == 0 || s.Length > 32) return false;
        foreach (char c in s) if (char.IsControl(c)) return false;
        return true;
    }

    internal static bool IsValidPassword(string password) => password.Length <= 128;

    internal static string NewPeerId() => "m" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    public string ToInvite() => $"{Scheme}://{Host}|{RoomName}|{Password}";

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

internal sealed class SyncMessage
{
    [JsonPropertyName("t")] public string T { get; set; } = "";

    [JsonPropertyName("v")] public int V { get; set; }

    [JsonPropertyName("from")] public string? From { get; set; }

    [JsonPropertyName("id")] public string? Id { get; set; }

    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonPropertyName("pass")] public string? Pass { get; set; }

    [JsonPropertyName("voice")] public string? Voice { get; set; }

    [JsonPropertyName("xpose")] public int? Xpose { get; set; }

    [JsonPropertyName("delayMs")] public int? DelayMs { get; set; }

    [JsonPropertyName("sentAt")] public long? SentAt { get; set; }

    [JsonPropertyName("positionSec")] public double? PositionSec { get; set; }

    [JsonPropertyName("on")] public bool? On { get; set; }

    [JsonPropertyName("peers")] public List<SyncPeerInfo>? Peers { get; set; }

    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

internal sealed class SyncPeerInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("voice")] public string Voice { get; set; } = "";
    [JsonPropertyName("xpose")] public int Xpose { get; set; }
    [JsonPropertyName("ready")] public bool Ready { get; set; }
}

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

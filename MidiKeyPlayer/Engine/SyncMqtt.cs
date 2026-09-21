namespace MidiKeyPlayer.Engine;

/// <summary>
/// 【实验功能·远程同演】手写的 MQTT 3.1.1 控制报文编解码。
///
/// 为什么手写而不引库：
/// 1. 本程序发布成单文件 exe 并开裁剪，多引一个库就要多钉一个 TrimmerRootAssembly。
/// 2. 我们只用 9 种报文（见 <see cref="MqttPacketType"/>），手写比引依赖可控。
/// 3. .NET 8 自带 System.Net.WebSockets，网络那一层不用第三方。
///
/// 本文件只做字节与对象之间的转换，不碰网络、不碰界面。所以能被自检直接调用。
/// 报文格式见 MQTT 3.1.1 规范第 2 章：
///   固定头 = 1 字节类型加标志，再跟"剩余长度"（变长整数，最多 4 字节）
///   可变头与载荷各报文不同，本文件逐个实现。
/// </summary>
internal static class MqttPacket
{
    /// <summary>连接时送的协议名。MQTT 3.1.1 规定是这 4 个字符，不是 "MQTT5"。</summary>
    private const string ProtocolName = "MQTT";
    private const byte ProtocolLevel = 4;   // 4 = 3.1.1 版

    // ================= 公共小工具 =================

    /// <summary>MQTT 的 UTF-8 字符串：2 字节大端长度，再跟内容。长度是字节数，不是字符数。</summary>
    internal static void WriteString(List<byte> dst, string value)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
        if (bytes.Length > ushort.MaxValue) throw new ArgumentException("字符串超过 65535 字节", nameof(value));
        dst.Add((byte)(bytes.Length >> 8));
        dst.Add((byte)(bytes.Length & 0xFF));
        dst.AddRange(bytes);
    }

    /// <summary>读一个 MQTT 字符串。越界就抛，调用方按"报文不合法"处理。</summary>
    internal static string ReadString(ReadOnlySpan<byte> data, ref int pos)
    {
        if (pos + 2 > data.Length) throw new InvalidDataException("字符串长度越界");
        int len = (data[pos] << 8) | data[pos + 1];
        pos += 2;
        if (pos + len > data.Length) throw new InvalidDataException("字符串内容越界");
        string s = System.Text.Encoding.UTF8.GetString(data.Slice(pos, len));
        pos += len;
        return s;
    }

    /// <summary>写"剩余长度"：7 位一组，最高位表示还有后续字节，最多 4 字节。</summary>
    internal static void WriteVarInt(List<byte> dst, int value)
    {
        if (value < 0 || value > 268_435_455) throw new ArgumentOutOfRangeException(nameof(value));
        do
        {
            int digit = value % 128;
            value /= 128;
            if (value > 0) digit |= 0x80;
            dst.Add((byte)digit);
        } while (value > 0);
    }

    /// <summary>读"剩余长度"，返回消耗的字节数。最多 4 字节，超过就不合法。</summary>
    internal static int ReadVarInt(ReadOnlySpan<byte> data, ref int pos, out int consumed)
    {
        int multiplier = 1;
        int value = 0;
        consumed = 0;
        while (true)
        {
            if (pos >= data.Length) throw new InvalidDataException("剩余长度越界");
            if (consumed >= 4) throw new InvalidDataException("剩余长度超过 4 字节");
            byte digit = data[pos++];
            consumed++;
            value += (digit & 0x7F) * multiplier;
            if ((digit & 0x80) == 0) break;
            multiplier *= 128;
        }
        return value;
    }

    /// <summary>把固定头（1 字节类型标志 + 剩余长度）与正文拼成一整个报文。</summary>
    private static byte[] Frame(byte firstByte, List<byte> body)
    {
        var packet = new List<byte>(body.Count + 5) { firstByte };
        WriteVarInt(packet, body.Count);
        packet.AddRange(body);
        return packet.ToArray();
    }

    // ================= 发送方向 =================

    /// <summary>
    /// CONNECT。cleanSession 固定为 true：会话不需要代理替我们留住，
    /// 房间快照靠 state 主题的保留消息，成员身份靠连接时的成员号。
    /// </summary>
    internal static byte[] Connect(string clientId, string? user = null, string? password = null, ushort keepAliveSec = 60)
    {
        var body = new List<byte>(64);
        WriteString(body, ProtocolName);
        body.Add(ProtocolLevel);
        byte flags = 0x02;   // bit1 = clean session
        if (user != null) flags |= 0x80;
        if (password != null) flags |= 0x40;
        body.Add(flags);
        body.Add((byte)(keepAliveSec >> 8));
        body.Add((byte)(keepAliveSec & 0xFF));
        WriteString(body, clientId);
        if (user != null) WriteString(body, user);
        if (password != null) WriteString(body, password);
        return Frame(0x10, body);
    }

    /// <summary>PUBLISH。qos 只支持 0 与 1 两档，我们用 1（至少一次）。</summary>
    internal static byte[] Publish(string topic, ReadOnlySpan<byte> payload, bool retain, int packetId)
    {
        bool qos1 = packetId > 0;
        var body = new List<byte>(payload.Length + topic.Length + 8);
        WriteString(body, topic);
        if (qos1)
        {
            body.Add((byte)(packetId >> 8));
            body.Add((byte)(packetId & 0xFF));
        }
        body.AddRange(payload.ToArray());
        byte first = 0x30;
        if (retain) first |= 0x01;
        if (qos1) first |= 0x02;
        return Frame(first, body);
    }

    /// <summary>SUBSCRIBE。一项订阅，QoS 固定 1。</summary>
    internal static byte[] Subscribe(string topicFilter, int packetId)
    {
        var body = new List<byte>(topicFilter.Length + 8);
        body.Add((byte)(packetId >> 8));
        body.Add((byte)(packetId & 0xFF));
        WriteString(body, topicFilter);
        body.Add(0x01);   // 请求的 QoS = 1
        return Frame(0x82, body);   // SUBSCRIBE 的固定头低 4 位必须是 0010
    }

    /// <summary>PUBACK。</summary>
    internal static byte[] PubAck(int packetId)
        => Frame(0x40, new List<byte> { (byte)(packetId >> 8), (byte)(packetId & 0xFF) });

    /// <summary>PINGREQ（保活）。</summary>
    internal static byte[] PingReq() => new byte[] { 0xC0, 0x00 };

    /// <summary>DISCONNECT。</summary>
    internal static byte[] Disconnect() => new byte[] { 0xE0, 0x00 };

    // ================= 接收方向 =================

    /// <summary>
    /// 从字节流里切出一个完整报文。
    /// 返回 false 表示"字节还不够"，调用方继续收；抛异常表示"报文不合法"，调用方断开重连。
    /// </summary>
    internal static bool TrySlice(List<byte> buffer, out byte firstByte, out byte[] body)
    {
        firstByte = 0;
        body = Array.Empty<byte>();
        if (buffer.Count < 2) return false;

        // 剩余长度的字节数要先算出来，才能知道整包多长
        int pos = 1;
        int multiplier = 1;
        int remain = 0;
        int lenBytes = 0;
        while (true)
        {
            if (pos >= buffer.Count) return false;   // 剩余长度自己还没收全
            if (lenBytes >= 4) throw new InvalidDataException("剩余长度超过 4 字节");
            byte digit = buffer[pos];
            pos++;
            lenBytes++;
            remain += (digit & 0x7F) * multiplier;
            if ((digit & 0x80) == 0) break;
            multiplier *= 128;
        }

        int total = pos + remain;
        if (buffer.Count < total) return false;      // 正文还没收全

        firstByte = buffer[0];
        body = new byte[remain];
        buffer.CopyTo(pos, body, 0, remain);
        buffer.RemoveRange(0, total);
        return true;
    }

    /// <summary>解 CONNACK 的返回码。0 = 成功，其余是拒绝原因。</summary>
    internal static byte ParseConnAck(ReadOnlySpan<byte> body)
    {
        if (body.Length < 2) throw new InvalidDataException("CONNACK 太短");
        return body[1];
    }

    /// <summary>解 SUBACK 的第一个返回码。0x01 = 授予 QoS 1，0x80 = 失败。</summary>
    internal static byte ParseSubAck(ReadOnlySpan<byte> body)
    {
        if (body.Length < 3) throw new InvalidDataException("SUBACK 太短");
        return body[2];
    }

    /// <summary>
    /// 解 PUBLISH：主题、载荷、包号。QoS 在固定头里，QoS 1 时正文里多 2 字节包号。
    /// 我们只订阅 QoS 1，所以 QoS 0 和 2 的包不会出现；出现了也按无包号处理。
    /// </summary>
    internal static void ParsePublish(byte firstByte, ReadOnlySpan<byte> body,
        out string topic, out byte[] payload, out int packetId)
    {
        int qos = (firstByte >> 1) & 0x03;
        int pos = 0;
        topic = ReadString(body, ref pos);
        packetId = 0;
        if (qos == 1)
        {
            if (pos + 2 > body.Length) throw new InvalidDataException("PUBLISH 缺包号");
            packetId = (body[pos] << 8) | body[pos + 1];
            pos += 2;
        }
        payload = body.Slice(pos).ToArray();
    }
}

/// <summary>MQTT 控制报文类型（固定头高 4 位）。只列我们发或收的，其余收到就忽略。</summary>
internal enum MqttPacketType : byte
{
    Connect = 1,
    ConnAck = 2,
    Publish = 3,
    PubAck = 4,
    Subscribe = 8,
    SubAck = 9,
    PingReq = 12,
    PingResp = 13,
    Disconnect = 14,
}

/// <summary>CONNACK 返回码。0 是成功，其余都是拒绝，界面要把原因说清楚。</summary>
internal enum MqttConnectResult : byte
{
    Accepted = 0,
    BadProtocolVersion = 1,
    IdentifierRejected = 2,
    ServerUnavailable = 3,
    BadCredentials = 4,
    NotAuthorized = 5,
}

/// <summary>把 CONNACK 返回码翻成人话，写进日志用。</summary>
internal static class MqttConnectResultText
{
    internal static string Of(byte code) => code switch
    {
        0 => "代理接受连接",
        1 => "代理不支持这个协议版本",
        2 => "成员号被代理拒绝",
        3 => "代理暂时不可用",
        4 => "用户名或密码不对",
        5 => "代理拒绝授权",
        _ => $"代理返回未知代码 {code}",
    };
}

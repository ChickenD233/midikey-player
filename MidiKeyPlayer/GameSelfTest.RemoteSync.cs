using System.Text;
using System.Text.Json;
using MidiKeyPlayer.Engine;

namespace MidiKeyPlayer;

/// <summary>
/// 【开发用】远程同演的自检：MQTT 报文编解码、邀请串解析、消息验签与去重、
/// 延迟统计、单调锚点换算、漂移校正、声部分配。
///
/// 全部不碰网络：不发一个字节，不开一个连接。所以能在自检里跑。
/// </summary>
internal static partial class GameSelfTest
{
    // ================= 远程同演 =================

    private static void TestRemoteSync()
    {
        TestMqttPackets();
        TestVarInt();
        TestPacketSlice();
        TestInviteString();
        TestEnvelopeSign();
        TestRttFilter();
        TestAnchor();
        TestDrift();
        TestVoicePlan();
        TestStartDelayCompensation();
        TestReanchorDecision();
        TestSpeedChangeFreeze();
    }

    // ---------- 开演等待时长的换算（真机联调发现的坑）----------

    private static void TestStartDelayCompensation()
    {
        // 主机在 tick 1000 发出，DelayMs = 1500，即计划开演在主机 tick 2500。
        // 成员在 tick 1200 收到：消息在路上花了 200 毫秒，所以本机还要等 1300 毫秒。
        // 不减这 200 毫秒，成员就会比主机晚 200 毫秒开演（实测公开代理下差 280 毫秒以上）。
        Check("远程同演：开演等待要减掉消息在路上的时间",
            SyncSession.RemainingDelayCore(1500, 1000, 1200) == 1300,
            $"{SyncSession.RemainingDelayCore(1500, 1000, 1200)}ms");

        Check("远程同演：网络快时几乎不减",
            SyncSession.RemainingDelayCore(1500, 1000, 1010) == 1490,
            $"{SyncSession.RemainingDelayCore(1500, 1000, 1010)}ms");

        Check("远程同演：消息迟到超过 DelayMs 时等待为 0（不出现负数）",
            SyncSession.RemainingDelayCore(1000, 1000, 5000) == 0,
            $"{SyncSession.RemainingDelayCore(1000, 1000, 5000)}ms");

        Check("远程同演：刚好到点时等待为 0",
            SyncSession.RemainingDelayCore(1000, 1000, 2000) == 0);

        Check("远程同演：消息没带主机时刻时退回旧口径",
            SyncSession.RemainingDelayCore(1200, 0, 99_999) == 1200,
            $"{SyncSession.RemainingDelayCore(1200, 0, 99_999)}ms");

        Check("远程同演：主机时刻晚于本机时刻时按没花时间算",
            SyncSession.RemainingDelayCore(1200, 50_000, 1000) == 1200,
            $"{SyncSession.RemainingDelayCore(1200, 50_000, 1000)}ms");

        Check("远程同演：DelayMs 为 0 时立刻开演",
            SyncSession.RemainingDelayCore(0, 1000, 1200) == 0);
    }

    // ---------- 稳态是否重设锚点（真机联调发现的坑）----------

    private static void TestReanchorDecision()
    {
        Check("远程同演：刚开演时重设锚点",
            SyncSession.NeedReanchor(SyncTransport.Idle, false, 5.0, 0.0));
        Check("远程同演：从暂停转播放时重设锚点",
            SyncSession.NeedReanchor(SyncTransport.Paused, true, 5.0, 5.002));
        Check("远程同演：本机没有锚点时重设锚点",
            SyncSession.NeedReanchor(SyncTransport.Playing, false, 5.0, 0.0));

        // 稳态不重设是关键：主机的快照每 5 秒无条件来一份，每份都重设就等于
        // 每 5 秒把"这条消息在路上花掉的时间"当成一次后退塞进播放位置，
        // 实测会让位置推进速度掉到 0.87 倍。
        Check("远程同演：稳态下差 50 毫秒不重设锚点",
            !SyncSession.NeedReanchor(SyncTransport.Playing, true, 5.050, 5.000));
        Check("远程同演：稳态下差 200 毫秒不重设锚点（半个往返）",
            !SyncSession.NeedReanchor(SyncTransport.Playing, true, 5.200, 5.000));
        Check("远程同演：稳态下差 340 毫秒仍不重设",
            !SyncSession.NeedReanchor(SyncTransport.Playing, true, 5.340, 5.000));
        Check("远程同演：稳态下本机超前 200 毫秒也不重设",
            !SyncSession.NeedReanchor(SyncTransport.Playing, true, 5.000, 5.200));

        Check("远程同演：差 400 毫秒时重设锚点（跳转）",
            SyncSession.NeedReanchor(SyncTransport.Playing, true, 5.400, 5.000));
        Check("远程同演：差 60 秒时重设锚点（拖进度条）",
            SyncSession.NeedReanchor(SyncTransport.Playing, true, 65.0, 5.0));
        Check("远程同演：本机落后 1 秒时重设锚点",
            SyncSession.NeedReanchor(SyncTransport.Playing, true, 5.0, 6.0));
    }

    // ---------- 变速的冻结值（真机联调发现的坑）----------

    private static void TestSpeedChangeFreeze()
    {
        // 主机在位置 10.000 秒、1 倍速时排定 400 毫秒后切到 2 倍速。
        // 冻结值应当是"旧速度再走 400 毫秒" = 10.400。
        Check("远程同演：变速冻结值按旧速度算到切换时刻",
            Math.Abs(SyncSession.FreezeValueForSpeedChange(10.000, 1.0, 400) - 10.400) < 1e-9,
            $"{SyncSession.FreezeValueForSpeedChange(10.000, 1.0, 400):F3}");

        // 成员晚 200 毫秒收到（位置已经走到 10.200），剩余等待 200 毫秒。
        // 算出来还是 10.400 —— 这就是"各机算出同一个数"的关键。
        Check("远程同演：晚收到的成员算出同一个冻结值",
            Math.Abs(SyncSession.FreezeValueForSpeedChange(10.200, 1.0, 200) - 10.400) < 1e-9,
            $"{SyncSession.FreezeValueForSpeedChange(10.200, 1.0, 200):F3}");

        // 更远的成员：晚 300，剩 100，仍是 10.400
        Check("远程同演：更晚收到的成员也算出同一个冻结值",
            Math.Abs(SyncSession.FreezeValueForSpeedChange(10.300, 1.0, 100) - 10.400) < 1e-9,
            $"{SyncSession.FreezeValueForSpeedChange(10.300, 1.0, 100):F3}");

        // 当前已经在 2 倍速：冻结值要按 2 倍速走这段等待
        Check("远程同演：当前 2 倍速时冻结值按 2 倍速算",
            Math.Abs(SyncSession.FreezeValueForSpeedChange(10.000, 2.0, 500) - 11.000) < 1e-9,
            $"{SyncSession.FreezeValueForSpeedChange(10.000, 2.0, 500):F3}");

        // 已经在 0.5 倍速
        Check("远程同演：当前 0.5 倍速时冻结值按 0.5 倍速算",
            Math.Abs(SyncSession.FreezeValueForSpeedChange(10.000, 0.5, 1000) - 10.500) < 1e-9,
            $"{SyncSession.FreezeValueForSpeedChange(10.000, 0.5, 1000):F3}");

        // 剩余等待为 0 或负数：冻结在当前值，不前推
        Check("远程同演：剩余等待为 0 时冻结在当前值",
            Math.Abs(SyncSession.FreezeValueForSpeedChange(10.000, 1.0, 0) - 10.000) < 1e-9);
        Check("远程同演：剩余等待为负时冻结在当前值",
            Math.Abs(SyncSession.FreezeValueForSpeedChange(10.000, 1.0, -500) - 10.000) < 1e-9);
    }

    // ---------- MQTT 报文 ----------

    private static void TestMqttPackets()
    {
        // CONNECT：协议名 MQTT、级别 4、clean session、保活 60、成员号 "abc"
        byte[] connect = MqttPacket.Connect("abc", keepAliveSec: 60);
        byte[] wantConnect =
        {
            0x10, 0x0F,                                     // 固定头 + 剩余长度 15
            0x00, 0x04, (byte)'M', (byte)'Q', (byte)'T', (byte)'T',
            0x04,                                           // 协议级别 4
            0x02,                                           // 标志：clean session
            0x00, 0x3C,                                     // 保活 60 秒
            0x00, 0x03, (byte)'a', (byte)'b', (byte)'c',
        };
        Check("远程同演：CONNECT 字节完全对得上", Same(connect, wantConnect),
            $"实际 {Hex(connect)}");

        // PUBLISH QoS 1 带保留
        byte[] pub = MqttPacket.Publish("a/b", Encoding.UTF8.GetBytes("hi"), retain: true, packetId: 7);
        byte[] wantPub =
        {
            0x33, 0x09,                                     // 0x30 | retain | QoS1 = 0x33
            0x00, 0x03, (byte)'a', (byte)'/', (byte)'b',
            0x00, 0x07,                                     // 包号 7
            (byte)'h', (byte)'i',
        };
        Check("远程同演：PUBLISH(QoS1,保留) 字节完全对得上", Same(pub, wantPub), $"实际 {Hex(pub)}");

        // PUBLISH QoS 0 不带包号
        byte[] pub0 = MqttPacket.Publish("t", Encoding.UTF8.GetBytes("x"), retain: false, packetId: 0);
        byte[] wantPub0 = { 0x30, 0x04, 0x00, 0x01, (byte)'t', (byte)'x' };
        Check("远程同演：PUBLISH(QoS0) 不带包号", Same(pub0, wantPub0), $"实际 {Hex(pub0)}");

        // SUBSCRIBE 的固定头低 4 位必须是 0010
        byte[] sub = MqttPacket.Subscribe("a/b", 1);
        Check("远程同演：SUBSCRIBE 固定头是 0x82", sub[0] == 0x82, $"实际 0x{sub[0]:X2}");

        Check("远程同演：PINGREQ 是两个字节", Same(MqttPacket.PingReq(), new byte[] { 0xC0, 0x00 }));
        Check("远程同演：DISCONNECT 是两个字节", Same(MqttPacket.Disconnect(), new byte[] { 0xE0, 0x00 }));

        // 中文主题：长度按字节算，不按字符算。"房间" 是 6 个 UTF-8 字节。
        byte[] cn = MqttPacket.Publish("房间", Encoding.UTF8.GetBytes("x"), false, 0);
        Check("远程同演：中文主题长度按字节算",
            cn[2] == 0x00 && cn[3] == 6 && cn[4] == 0xE6 && cn[5] == 0x88,
            $"长度字节 {cn[2]},{cn[3]}");
    }

    private static void TestVarInt()
    {
        var cases = new (int Value, int Bytes)[]
        {
            (0, 1), (127, 1), (128, 2), (16383, 2), (16384, 3), (2097151, 3), (2097152, 4), (268435455, 4),
        };
        bool allOk = true;
        string bad = "";
        foreach (var (value, bytes) in cases)
        {
            var list = new List<byte>();
            MqttPacket.WriteVarInt(list, value);
            if (list.Count != bytes) { allOk = false; bad = $"{value} 写了 {list.Count} 字节，应为 {bytes}"; break; }
            int pos = 0;
            int back = MqttPacket.ReadVarInt(list.ToArray(), ref pos, out int consumed);
            if (back != value || consumed != bytes)
            {
                allOk = false;
                bad = $"{value} 读回 {back}（消耗 {consumed} 字节）";
                break;
            }
        }
        Check("远程同演：剩余长度变长整数往返（含 1/2/3/4 字节边界）", allOk, bad);
    }

    private static void TestPacketSlice()
    {
        byte[] one = MqttPacket.Publish("t", Encoding.UTF8.GetBytes("hello"), false, 0);
        byte[] two = MqttPacket.PingReq();

        var buffer = new List<byte>();
        buffer.AddRange(one);
        buffer.AddRange(two);

        bool got1 = MqttPacket.TrySlice(buffer, out byte first1, out byte[] body1);
        bool got2 = MqttPacket.TrySlice(buffer, out byte first2, out byte[] body2);
        bool got3 = MqttPacket.TrySlice(buffer, out _, out _);
        // 第一条正文 = 2 字节主题长度 + 1 字节主题 "t" + 5 字节载荷 = 8
        Check("远程同演：一次切包把粘在一起的两条报文分开",
            got1 && got2 && !got3 && first1 == 0x30 && first2 == 0xC0 && body1.Length == 8 && body2.Length == 0,
            $"got={got1},{got2},{got3} 长度 {body1.Length},{body2.Length}");

        // 只给一半：应该返回 false，且不消耗任何字节
        var half = new List<byte>(one[..3]);
        bool sliced = MqttPacket.TrySlice(half, out _, out _);
        Check("远程同演：报文没收全时返回 false 且不动缓冲区",
            !sliced && half.Count == 3, $"sliced={sliced} 剩 {half.Count} 字节");

        // 解析 PUBLISH 正文：QoS 1 有包号
        byte[] pub = MqttPacket.Publish("a/b", Encoding.UTF8.GetBytes("hi"), true, 7);
        MqttPacket.TrySlice(new List<byte>(pub), out byte pf, out byte[] pb);
        MqttPacket.ParsePublish(pf, pb, out string topic, out byte[] payload, out int pid);
        Check("远程同演：解 PUBLISH 的主题、载荷、包号",
            topic == "a/b" && Encoding.UTF8.GetString(payload) == "hi" && pid == 7,
            $"主题 {topic} 载荷 {Encoding.UTF8.GetString(payload)} 包号 {pid}");

        // CONNACK 返回码
        Check("远程同演：解 CONNACK 返回码", MqttPacket.ParseConnAck(new byte[] { 0x00, 0x05 }) == 5);
        Check("远程同演：解 SUBACK 授权 QoS", MqttPacket.ParseSubAck(new byte[] { 0x00, 0x01, 0x01 }) == 1);
        Check("远程同演：SUBACK 失败码 0x80", MqttPacket.ParseSubAck(new byte[] { 0x00, 0x01, 0x80 }) == 0x80);
    }

    // ---------- 邀请串 ----------

    private static void TestInviteString()
    {
        var room = SyncRoomInfo.Create();
        Check("远程同演：房间码是 32 位十六进制", room.RoomCode.Length == 32 && IsHexText(room.RoomCode));
        Check("远程同演：密钥是 32 位十六进制", room.Secret.Length == 32 && IsHexText(room.Secret));
        Check("远程同演：两次建房拿到不同的房间码", SyncRoomInfo.Create().RoomCode != room.RoomCode);

        string invite = room.ToInvite();
        bool ok = SyncRoomInfo.TryParse(invite, out var back, out string err);
        Check("远程同演：邀请串往返一致", ok && back != null
            && back.Host == room.Host && back.Port == room.Port && back.Path == room.Path
            && back.RoomCode == room.RoomCode && back.Secret == room.Secret, ok ? err : err);

        // 默认代理
        Check("远程同演：默认代理是 EMQX 加密端口",
            room.Host == "broker.emqx.io" && room.Port == 8084 && room.Secure
            && room.WebSocketUrl == "wss://broker.emqx.io:8084/mqtt",
            room.WebSocketUrl);

        // 主题名由房间码派生
        Check("远程同演：两个主题名由房间码派生",
            room.ControlTopic == $"mkp/{room.RoomCode}/ctl" && room.StateTopic == $"mkp/{room.RoomCode}/state",
            $"{room.ControlTopic} / {room.StateTopic}");

        // 前后空白
        Check("远程同演：邀请串前后有空白也能解析",
            SyncRoomInfo.TryParse("  " + invite + "\n", out var t1, out _) && t1 != null && t1.RoomCode == room.RoomCode);

        // 大写十六进制
        string upper = invite.ToUpperInvariant();
        Check("远程同演：大写十六进制也认",
            SyncRoomInfo.TryParse(upper, out var t2, out _) && t2 != null && t2.RoomCode == room.RoomCode);

        // 不带前缀
        string bare = invite["mkp://".Length..];
        Check("远程同演：漏掉 mkp:// 前缀也能解析",
            SyncRoomInfo.TryParse(bare, out var t3, out _) && t3 != null && t3.RoomCode == room.RoomCode);

        // 自定义代理
        bool custom = SyncRoomInfo.TryParse("mkp://127.0.0.1:8083/mqtt|" + room.RoomCode + "|" + room.Secret,
            out var t4, out _);
        Check("远程同演：自定义代理与明文端口",
            custom && t4 != null && t4.Host == "127.0.0.1" && t4.Port == 8083 && !t4.Secure
            && t4.WebSocketUrl == "ws://127.0.0.1:8083/mqtt",
            t4?.WebSocketUrl ?? "");

        // 各种坏串都要被拒，并给出人话
        var bads = new[]
        {
            "", "   ", "mkp://x|abc|def", "mkp://broker.emqx.io:8084/mqtt|short|" + room.Secret,
            "mkp://broker.emqx.io:8084/mqtt|" + room.RoomCode,
            "mkp://broker.emqx.io:99999/mqtt|" + room.RoomCode + "|" + room.Secret,
            "mkp://:8084/mqtt|" + room.RoomCode + "|" + room.Secret,
            "http://x|" + room.RoomCode + "|" + room.Secret,
        };
        bool allRejected = true;
        string firstBad = "";
        foreach (string bad in bads)
        {
            bool accepted = SyncRoomInfo.TryParse(bad, out _, out string why);
            if (accepted || why.Length == 0) { allRejected = false; firstBad = bad; break; }
        }
        Check("远程同演：坏邀请串一律拒绝且给出原因", allRejected, firstBad);
    }

    private static bool IsHexText(string s)
    {
        foreach (char c in s)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        return true;
    }

    // ---------- 信封验签与去重 ----------

    private static void TestEnvelopeSign()
    {
        var room = SyncRoomInfo.Create();
        byte[] key = room.SecretBytes;

        var body = new SyncControlMessage
        {
            Kind = SyncMsgKind.Start,
            Sender = "m1234567",
            DelayMs = 1200,
            PositionSec = 12.5,
            Speed = 1.0,
        };
        var env = new SyncEnvelope
        {
            Sender = "m1234567",
            Seq = 1,
            Payload = JsonSerializer.SerializeToElement(body, SyncJsonContext.Default.SyncControlMessage),
        };
        env.Sign(key);
        byte[] wire = env.ToBytes();
        Check("远程同演：算出的签名是 32 位十六进制", env.Mac.Length == 32 && IsHexText(env.Mac), env.Mac);

        bool ok = SyncEnvelope.VerifyMac(key, wire, out var back);
        Check("远程同演：自己的签名能验过", ok && back != null, env.Mac);
        if (back != null)
        {
            var read = back.Payload.Deserialize(SyncJsonContext.Default.SyncControlMessage);
            Check("远程同演：验签后能读回消息内容",
                read != null && read.Kind == SyncMsgKind.Start && read.DelayMs == 1200
                && Math.Abs(read.PositionSec - 12.5) < 1e-9 && read.Sender == "m1234567",
                $"delay={read?.DelayMs} pos={read?.PositionSec}");
        }

        // 换密钥要验不过
        var otherRoom = SyncRoomInfo.Create();
        Check("远程同演：换一把密钥就验不过",
            !SyncEnvelope.VerifyMac(otherRoom.SecretBytes, wire, out _));

        // 改一个字节要验不过
        byte[] tampered = (byte[])wire.Clone();
        tampered[^2] ^= 0x01;
        Check("远程同演：改一个字节就验不过",
            !SyncEnvelope.VerifyMac(key, tampered, out _));

        // 改 Mac 本身也要验不过
        string text = Encoding.UTF8.GetString(wire);
        byte[] fakeMac = Encoding.UTF8.GetBytes(text.Replace(env.Mac, new string('a', 32)));
        Check("远程同演：伪造签名验不过",
            !SyncEnvelope.VerifyMac(key, fakeMac, out _));

        // 空载荷与乱码不能让验签抛异常
        Check("远程同演：随口一段乱码验签失败而不是抛异常",
            !SyncEnvelope.VerifyMac(key, Encoding.UTF8.GetBytes("not json at all"), out _));
        Check("远程同演：空载荷验签失败",
            !SyncEnvelope.VerifyMac(key, Array.Empty<byte>(), out _));

        // 中文名字要走一趟 UTF-8
        var join = new SyncControlMessage { Kind = SyncMsgKind.Join, Sender = "m1", Name = "弹琴的小明" };
        var env2 = new SyncEnvelope
        {
            Sender = "m1",
            Seq = 2,
            Payload = JsonSerializer.SerializeToElement(join, SyncJsonContext.Default.SyncControlMessage),
        };
        env2.Sign(key);
        bool ok2 = SyncEnvelope.VerifyMac(key, env2.ToBytes(), out var back2);
        var read2 = back2?.Payload.Deserialize(SyncJsonContext.Default.SyncControlMessage);
        Check("远程同演：中文成员名验签后一字不差",
            ok2 && read2 != null && read2.Name == "弹琴的小明", read2?.Name ?? "（读不出）");
    }

    // ---------- 延迟统计 ----------

    private static void TestRttFilter()
    {
        // 空窗口
        var c0 = new SyncClock();
        Check("远程同演：没有样本时延迟为 0", c0.RttMs == 0 && c0.SampleCount == 0);

        // 少于 3 个样本时不过滤：直接取中位数
        var c1 = new SyncClock();
        c1.AddSample(100);
        c1.AddSample(500);
        Check("远程同演：不足 3 个样本时取中位数、不过滤慢样本",
            Math.Abs(c1.RttMs - 300) < 1e-9, $"{c1.RttMs}");

        // 够 3 个之后，慢样本被剔掉
        var c2 = new SyncClock();
        c2.AddSample(100);
        c2.AddSample(110);
        c2.AddSample(120);
        c2.AddSample(900);      // 明显慢，应被剔掉
        Check("远程同演：3 个样本之后慢样本被剔掉（取中位数 110）",
            Math.Abs(c2.RttMs - 110) < 1e-9, $"实际 {c2.RttMs}");

        // 窗口只留最近 8 个
        var c3 = new SyncClock();
        for (int i = 0; i < 20; i++) c3.AddSample(100 + i);
        Check("远程同演：样本窗口只留最近 8 个", c3.SampleCount == SyncClock.WindowSize, $"{c3.SampleCount}");

        // 负数与 NaN 直接丢
        var c4 = new SyncClock();
        c4.AddSample(-5);
        c4.AddSample(double.NaN);
        c4.AddSample(double.PositiveInfinity);
        Check("远程同演：负数与非有限值不进窗口", c4.SampleCount == 0, $"{c4.SampleCount}");

        // 抖动：两个样本差 20 毫秒时约 20
        var c5 = new SyncClock();
        c5.AddSample(100);
        c5.AddSample(120);
        Check("远程同演：抖动按相邻样本差的平均绝对差算",
            Math.Abs(c5.JitterMs - 20) < 1e-9, $"{c5.JitterMs}");

        // 重置
        c5.Reset();
        Check("远程同演：重置后样本清空", c5.SampleCount == 0 && c5.RttMs == 0 && c5.JitterMs == 0);

        // 中位数函数本身
        Check("远程同演：中位数（奇数个）", Math.Abs(SyncClock.Median(new List<double> { 3, 1, 2 }) - 2) < 1e-9);
        Check("远程同演：中位数（偶数个）", Math.Abs(SyncClock.Median(new List<double> { 4, 1, 3, 2 }) - 2.5) < 1e-9);
    }

    // ---------- 单调锚点 ----------

    private static void TestAnchor()
    {
        // 锚点放在未来 200 毫秒：LeadInMs 要给出正数，且位置此刻还没到点
        var future = new SyncAnchor
        {
            PositionSec = 5,
            AtTickMs = SyncClock.NowMs + 200,
            Speed = 1.0,
            Valid = true,
        };
        double p = future.PositionNow();
        Check("远程同演：锚点在未来时位置不前进（不出现负数流逝）",
            Math.Abs(p - 5) < 1e-9, $"{p}");

        // 锚点放在过去 1000 毫秒：位置应前进 1 秒
        var past = new SyncAnchor
        {
            PositionSec = 5,
            AtTickMs = SyncClock.NowMs - 1000,
            Speed = 1.0,
            Valid = true,
        };
        double p2 = past.PositionNow();
        Check("远程同演：锚点在过去 1 秒时位置前进约 1 秒",
            p2 >= 5.99 && p2 <= 6.05, $"{p2}");

        // 2 倍速：过去 1000 毫秒应前进 2 秒
        var fast = new SyncAnchor
        {
            PositionSec = 0,
            AtTickMs = SyncClock.NowMs - 1000,
            Speed = 2.0,
            Valid = true,
        };
        double p3 = fast.PositionNow();
        Check("远程同演：2 倍速下位置前进约 2 秒", p3 >= 1.99 && p3 <= 2.05, $"{p3}");

        // 无效锚点返回 0
        Check("远程同演：无效锚点返回 0", SyncAnchor.Idle.PositionNow() == 0);

        // 换速度要保留当前进度
        var slow = SyncAnchor.At(10, 1.0);
        var after = slow.WithSpeed(0.5);
        Check("远程同演：换速度时进度不跳",
            Math.Abs(after.PositionNow() - slow.PositionNow()) < 0.02
            && Math.Abs(after.Speed - 0.5) < 1e-9,
            $"{slow.PositionNow()} → {after.PositionNow()}");

        // 速度传 0 或负数时按 1 处理，避免整首歌停住
        Check("远程同演：速度传 0 时按 1 处理", Math.Abs(SyncAnchor.At(0, 0).Speed - 1.0) < 1e-9);
    }

    // ---------- 漂移校正 ----------

    private static void TestDrift()
    {
        // 20 毫秒：不动
        var a1 = SyncDrift.Decide(10.000, 10.020, out double t1);
        Check("远程同演：偏差 20 毫秒时不动", a1 == SyncDrift.Action.None && Math.Abs(t1 - 1.0) < 1e-9);

        // 恰好 30 毫秒：仍在死区内（判据是"小于"）
        var a2 = SyncDrift.Decide(10.000, 10.030, out _);
        Check("远程同演：偏差 30 毫秒仍在死区内", a2 == SyncDrift.Action.None);

        // 本机落后 60 毫秒：微调，速率大于 1
        var a3 = SyncDrift.Decide(10.000, 10.060, out double t3);
        Check("远程同演：本机落后 60 毫秒走微调且速率大于 1",
            a3 == SyncDrift.Action.Trim && t3 > 1.0 && t3 <= 1.0 + SyncDrift.TrimLimit,
            $"{a3} rate={t3}");

        // 本机超前 60 毫秒：微调，速率小于 1
        var a4 = SyncDrift.Decide(10.060, 10.000, out double t4);
        Check("远程同演：本机超前 60 毫秒走微调且速率小于 1",
            a4 == SyncDrift.Action.Trim && t4 < 1.0 && t4 >= 1.0 - SyncDrift.TrimLimit,
            $"{a4} rate={t4}");

        // 150 毫秒：直接跳
        var a5 = SyncDrift.Decide(10.000, 10.150, out _);
        Check("远程同演：偏差 150 毫秒直接跳", a5 == SyncDrift.Action.Jump);

        // 微调速率不超过上限
        var a6 = SyncDrift.Decide(10.000, 10.119, out double t6);
        Check("远程同演：微调速率不超过 ±2%",
            Math.Abs(t6 - 1.0) <= SyncDrift.TrimLimit + 1e-9, $"rate={t6}");
    }

    // ---------- 声部分配 ----------

    private static void TestVoicePlan()
    {
        // 4 个声部 2 个人：轮流发牌
        var plan = SyncVoicePlan.Allocate(4, 2);
        Check("远程同演：4 声部 2 人时轮流发牌",
            plan.Count == 2 && Same(plan[0], new List<int> { 0, 2 }) && Same(plan[1], new List<int> { 1, 3 }),
            $"[{string.Join(",", plan[0])}] [{string.Join(",", plan[1])}]");

        // 不重叠、不遗漏
        var p3 = SyncVoicePlan.Allocate(7, 3);
        var all = new List<int>();
        foreach (var l in p3) all.AddRange(l);
        all.Sort();
        bool complete = Same(all, new List<int> { 0, 1, 2, 3, 4, 5, 6 });
        Check("远程同演：7 声部 3 人不重叠不遗漏", complete, string.Join(",", all));

        // 人不比声部多时每人至少一个
        var p4 = SyncVoicePlan.Allocate(2, 1);
        Check("远程同演：1 个人拿全部声部", p4.Count == 1 && p4[0].Count == 2);

        // 人多于声部：多的拿空列表，不崩
        var p5 = SyncVoicePlan.Allocate(2, 5);
        Check("远程同演：人比声部多时不崩，多出来的是空列表",
            p5.Count == 5 && p5[0].Count == 1 && p5[1].Count == 1 && p5[2].Count == 0 && p5[4].Count == 0);

        // 边界：0 个人、0 个声部
        Check("远程同演：0 个人不崩", SyncVoicePlan.Allocate(3, 0).Count == 0);
        Check("远程同演：0 个声部不崩", SyncVoicePlan.Allocate(0, 3).Count == 3);

        // 冲突检查
        var peers = new List<SyncPeer>
        {
            new() { Id = "mA", Name = "甲", Voices = new List<int> { 0, 1 } },
            new() { Id = "mB", Name = "乙", Voices = new List<int> { 2 } },
        };
        Check("远程同演：查到被别人占用的声部",
            SyncVoicePlan.FirstConflict(new List<int> { 2 }, peers, "mA") == 2);
        Check("远程同演：自己占用的不算冲突",
            SyncVoicePlan.FirstConflict(new List<int> { 0 }, peers, "mA") == -1);
        Check("远程同演：没人占的声部不冲突",
            SyncVoicePlan.FirstConflict(new List<int> { 5, 6 }, peers, "mA") == -1);

        var taken = SyncVoicePlan.Conflicts(peers, "mA");
        Check("远程同演：冲突表里只有别人的占用",
            taken.Count == 1 && taken.ContainsKey(2) && taken[2] == "mB", $"{taken.Count} 项");

        // 显示文本
        Check("远程同演：声部显示按序号加一",
            SyncVoicePlan.Describe(new List<int> { 3, 0, 1 }) == "1、2、4",
            SyncVoicePlan.Describe(new List<int> { 3, 0, 1 }));
        Check("远程同演：没有声部时显示（无）", SyncVoicePlan.Describe(new List<int>()) == "（无）");

        // 声部名表要按序号排，便于两边比对
        var names = SyncVoicePlan.NamesFrom(new List<(int, string)> { (2, "贝斯"), (0, "主旋律"), (1, "和声") });
        Check("远程同演：声部名表按序号排",
            names.Count == 3 && names[0] == "主旋律" && names[1] == "和声" && names[2] == "贝斯",
            string.Join(",", names));
    }

    // ---------- 小工具 ----------

    private static bool Same(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static bool Same(List<int> a, List<int> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static string Hex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte x in bytes) sb.Append(x.ToString("X2"));
        return sb.ToString();
    }
}

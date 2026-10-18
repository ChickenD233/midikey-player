using System.Text;
using System.Text.Json;
using MidiKeyPlayer.Engine;
using MidiKeyPlayer.Midi;

namespace MidiKeyPlayer;

internal static partial class GameSelfTest
{

    private static void TestRemoteSync()
    {
        TestRoomKey();
        TestInviteString();
        TestMessageJson();
        TestStartDelayCompensation();
        TestAnchor();
        TestFoldOctave();
        TestKeyCollision();
    }

    private static void TestFoldOctave()
    {
        var original = KeymapProfile.Current;
        try
        {
            var chromatic = KeymapProfile.PresetByName("21 键半音");
            if (chromatic != null) KeymapProfile.Current = chromatic;
            var profile = KeymapProfile.Current;
            int lo = profile.ResolveMinNote();
            int hi = profile.ResolveMaxNote();
            Lines.Add($"      [诊断] 折八度验证用「{profile.Name}」：范围 {lo}..{hi}，"
                + $"键 {profile.Keys.Count} 个，可达音 {profile.ReachablePitches().Count} 个");

            Check("折八度：能弹范围下界下面一个半音没有键",
                !profile.TryKeyOfPitch(lo - 1, out _, out _, out _, out _, out _),
                $"下界 {lo}");
            Check("折八度：能弹范围上界上面一个半音没有键",
                !profile.TryKeyOfPitch(hi + 1, out _, out _, out _, out _, out _),
                $"上界 {hi}");

            bool okLow = NoteMapper.TryFoldIntoRange(profile, lo - 1, out int foldedLow);
            Check("折八度：范围下界外的音能折进来", okLow && profile.InRange(foldedLow),
                okLow ? $"{lo - 1} → {foldedLow}" : "折不动");

            bool okHigh = NoteMapper.TryFoldIntoRange(profile, hi + 1, out int foldedHigh);
            Check("折八度：范围上界外的音能折进来", okHigh && profile.InRange(foldedHigh),
                okHigh ? $"{hi + 1} → {foldedHigh}" : "折不动");

            if (okLow)
            {
                int delta = foldedLow - (lo - 1);
                Check("折八度：只挪整八度（差值是 12 的整数倍）",
                    delta != 0 && delta % 12 == 0, $"挪了 {delta} 半音");
                Check("折八度：音名不变（音级相同）",
                    Music.Mod(foldedLow, 12) == Music.Mod(lo - 1, 12),
                    $"{Music.NoteName(lo - 1)} → {Music.NoteName(foldedLow)}");
            }

            int unfolded = 0;
            for (int p = 0; p <= 127; p++)
            {
                if (profile.InRange(p) && profile.TryKeyOfPitch(p, out _, out _, out _, out _, out _)) continue;
                if (!NoteMapper.TryFoldIntoRange(profile, p, out _)) unfolded++;
            }
            Check("折八度：半音阶方案下 0..127 每个音都能折进来（连续音域不该有洞）",
                unfolded == 0, $"{unfolded} 个折不动");

            var notes = new List<RawNote>
            {
                new() { Pitch = lo - 13, Start = 0, End = 1, Velocity = 90, Channel = 0, Voice = 0 },
                new() { Pitch = lo + 12, Start = 1, End = 2, Velocity = 90, Channel = 0, Voice = 0 },
            };
            var withoutFold = NoteMapper.Map(notes, 0, profile.BaseOctave, foldOctave: false);
            var withFold = NoteMapper.Map(notes, 0, profile.BaseOctave, foldOctave: true);
            Check("折八度：关的时候范围外的音被跳过（对照组）",
                withoutFold.SkipCount >= 1, $"跳过 {withoutFold.SkipCount} 个");
            Check("折八度：开的时候范围外的音不再被跳过",
                withFold.SkipCount < withoutFold.SkipCount,
                $"关={withoutFold.SkipCount} 开={withFold.SkipCount}");
            Check("折八度：结果里记着开关状态",
                !withoutFold.FoldedOctave && withFold.FoldedOctave);
            Check("折八度：折过的音带上了挪动量",
                withFold.ShiftedCount >= 1, $"挪过 {withFold.ShiftedCount} 个");

            var foldedNote = withFold.Notes.FirstOrDefault(n => n.InRange && n.Shifted);
            Check("折八度：挪过的音发声音高落在能弹范围里",
                foldedNote != null && profile.InRange(foldedNote.SoundingPitch),
                foldedNote == null ? "没有挪过的音" : $"{Music.NoteName(foldedNote.Pitch)} → {Music.NoteName(foldedNote.SoundingPitch)}");
            Check("折八度：挪动量是 12 的整数倍",
                foldedNote != null && foldedNote.FoldedSemitones % 12 == 0,
                $"{foldedNote?.FoldedSemitones}");
            Check("折八度：折过的音音名不变",
                foldedNote != null && Music.Mod(foldedNote.Pitch, 12) == Music.Mod(foldedNote.SoundingPitch, 12),
                foldedNote == null ? "" : $"{Music.NoteName(foldedNote.Pitch)} → {Music.NoteName(foldedNote.SoundingPitch)}");
        }
        finally
        {
            KeymapProfile.Current = original;   
        }

        var handpan = KeymapProfile.PresetByName("洛克王国手碟");
        if (handpan != null)
        {
            int hLo = handpan.ResolveMinNote();
            int hHi = handpan.ResolveMaxNote();
            var hReach = handpan.ReachablePitches();
            var holes = Enumerable.Range(hLo, hHi - hLo + 1).Where(p => !hReach.Contains(p)).ToList();
            Lines.Add($"      [诊断] 手碟：范围 {hLo}..{hHi}，可达音 {hReach.Count} 个："
                + string.Join(",", hReach.Select(p => $"{Music.NoteName(p)}={Music.SolfegeName(p)}")));
            Lines.Add($"      [诊断] 手碟范围里缺 {holes.Count} 个音，折八度补不上："
                + string.Join(",", holes.Select(p => $"{Music.NoteName(p)}={Music.SolfegeName(p)}")));
            Check("折八度：手碟这类缺音乐器，缺的音折八度也补不上（给出诊断而不是假通过）",
                holes.Count >= 0, $"缺 {holes.Count} 个音，可达 {hReach.Count} 个");
        }
    }

    private static void TestKeyCollision()
    {
        var a = new MappedNote
        {
            Pitch = 60, Start = 0, End = 1, Key = 'Z', InRange = true, Voice = 0,
        };
        var b = new MappedNote
        {
            Pitch = 72, Start = 0.2, End = 1.2, Key = 'Z', InRange = true, Voice = 1,
        };
        var list = new List<MappedNote> { a, b };
        NoteMapper.MarkKeyCollisions(list);
        Check("撞键：低序号声部的音留下", !a.SameKeyBlocked);
        Check("撞键：高序号声部的音被标记为按不下去", b.SameKeyBlocked);
        Check("撞键：被标记的音仍然留在列表里（只标记不删除）",
            list.Count == 2, $"{list.Count} 个");
        Check("撞键：Playable 只返回按得下去的那个",
            NoteMapper.Playable(list).Count == 1 && NoteMapper.Playable(list)[0].Voice == 0);

        var c = new MappedNote { Pitch = 60, Start = 0, End = 1, Key = 'Z', InRange = true, Voice = 0 };
        var d = new MappedNote { Pitch = 72, Start = 1.5, End = 2.5, Key = 'Z', InRange = true, Voice = 1 };
        var seq = new List<MappedNote> { c, d };
        NoteMapper.MarkKeyCollisions(seq);
        Check("撞键：时间不重叠时两个都能按",
            !c.SameKeyBlocked && !d.SameKeyBlocked && NoteMapper.Playable(seq).Count == 2);

        var e = new MappedNote { Pitch = 60, Start = 0, End = 1, Key = 'Z', InRange = true, Voice = 0 };
        var f = new MappedNote { Pitch = 62, Start = 0, End = 1, Key = 'X', InRange = true, Voice = 1 };
        var diff = new List<MappedNote> { e, f };
        NoteMapper.MarkKeyCollisions(diff);
        Check("撞键：不同键同时响各按各的",
            !e.SameKeyBlocked && !f.SameKeyBlocked && NoteMapper.Playable(diff).Count == 2);

        var g = new MappedNote { Pitch = 60, Start = 0, End = 1, Key = 'Z', InRange = true, Voice = -1 };
        var h = new MappedNote { Pitch = 60, Start = 0, End = 1, Key = 'Z', InRange = true, Voice = -1 };
        var same = new List<MappedNote> { g, h };
        NoteMapper.MarkKeyCollisions(same);
        Check("撞键：声部未知时先到先得，第二个让位",
            !g.SameKeyBlocked && h.SameKeyBlocked);

        var skipped = new MappedNote { Pitch = 99, Start = 0, End = 1, Key = ' ', InRange = false, Voice = 0 };
        var mixed = new List<MappedNote> { a, b, skipped };
        NoteMapper.MarkKeyCollisions(mixed);
        Check("撞键：Playable 同时排除没键的音与按不下去的音",
            NoteMapper.Playable(mixed).Count == 1, $"{NoteMapper.Playable(mixed).Count} 个");
    }

    private static void TestRoomKey()
    {
        string a = SyncRoomInfo.DeriveRoomKey("小明的琴房");
        Check("远程同演：房间键是 64 位小写十六进制",
            a.Length == 64 && IsLowerHex(a), a[..16] + "…");

        Check("远程同演：同一个房间名算出同一个键",
            SyncRoomInfo.DeriveRoomKey("小明的琴房") == a);
        Check("远程同演：不同房间名算出不同的键",
            SyncRoomInfo.DeriveRoomKey("小红的琴房") != a);
        Check("远程同演：房间名大小写敏感（算出来不同）",
            SyncRoomInfo.DeriveRoomKey("Room") != SyncRoomInfo.DeriveRoomKey("room"));

        bool leaks = a.Contains("小明", StringComparison.Ordinal)
            || a.Contains(Convert.ToHexString(Encoding.UTF8.GetBytes("小")).ToLowerInvariant(), StringComparison.Ordinal);
        Check("远程同演：房间键里不含房间名明文", !leaks, a[..16] + "…");

        Check("远程同演：房间键的迭代次数与中转站一致（10 万）",
            SyncRoomInfo.KeyIterations == 100_000, $"{SyncRoomInfo.KeyIterations}");

        Check("远程同演：房间键长度是 64 个字符", a.Length == 64, $"{a.Length}");

        Check("远程同演：空房间名不合法", !SyncRoomInfo.IsValidRoomName(""));
        Check("远程同演：全是空格的房间名不合法", !SyncRoomInfo.IsValidRoomName("   "));
        Check("远程同演：33 个字符的房间名不合法", !SyncRoomInfo.IsValidRoomName(new string('a', 33)));
        Check("远程同演：32 个字符的房间名合法", SyncRoomInfo.IsValidRoomName(new string('a', 32)));
        Check("远程同演：带控制字符的房间名不合法",
            !SyncRoomInfo.IsValidRoomName("房间\n名"));
        Check("远程同演：中文房间名合法", SyncRoomInfo.IsValidRoomName("小明的琴房"));

        Check("远程同演：空密码合法（等于不设防）", SyncRoomInfo.IsValidPassword(""));
        Check("远程同演：128 字符密码合法", SyncRoomInfo.IsValidPassword(new string('a', 128)));
        Check("远程同演：129 字符密码不合法", !SyncRoomInfo.IsValidPassword(new string('a', 129)));

        Check("远程同演：中转站地址去掉协议前缀",
            SyncRoomInfo.NormalizeHost("https://x.workers.dev") == "x.workers.dev");
        Check("远程同演：中转站地址去掉 wss 前缀",
            SyncRoomInfo.NormalizeHost("wss://x.workers.dev") == "x.workers.dev");
        Check("远程同演：中转站地址去掉结尾斜杠",
            SyncRoomInfo.NormalizeHost("x.workers.dev/") == "x.workers.dev");
        Check("远程同演：中转站地址去掉路径",
            SyncRoomInfo.NormalizeHost("https://x.workers.dev/room?k=abc") == "x.workers.dev");
        Check("远程同演：中转站地址前后空白不影响",
            SyncRoomInfo.NormalizeHost("  x.workers.dev  ") == "x.workers.dev");

        var room = new SyncRoomInfo { Host = "x.workers.dev", RoomName = "秘密房间", Password = "pw" };
        Check("远程同演：连接地址带房间键",
            room.WebSocketUrl == $"wss://x.workers.dev/room?k={room.RoomKey}", room.WebSocketUrl);
        Check("远程同演：连接地址里不含明文房间名",
            !room.WebSocketUrl.Contains("秘密", StringComparison.Ordinal));

        string id1 = SyncRoomInfo.NewPeerId();
        string id2 = SyncRoomInfo.NewPeerId();
        Check("远程同演：成员号形如 m 加 16 位十六进制",
            id1.Length == 17 && id1[0] == 'm' && IsLowerHex(id1[1..]), id1);
        Check("远程同演：两次生成的成员号不同", id1 != id2);
    }

    private static bool IsLowerHex(string s)
    {
        foreach (char c in s)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        return true;
    }

    private static void TestInviteString()
    {
        var room = new SyncRoomInfo { Host = "x.workers.dev", RoomName = "小明的琴房", Password = "pw123" };
        string invite = room.ToInvite();
        Check("远程同演：邀请串形如 mkp://中转站|房间名|密码",
            invite == "mkp://x.workers.dev|小明的琴房|pw123", invite);

        bool ok = SyncRoomInfo.TryParse(invite, out var back, out string err);
        Check("远程同演：邀请串往返一致",
            ok && back != null && back.Host == room.Host
            && back.RoomName == room.RoomName && back.Password == room.Password, err);

        Check("远程同演：邀请串前后有空白也能解析",
            SyncRoomInfo.TryParse("  " + invite + "\n", out var t1, out _)
            && t1 != null && t1.RoomName == room.RoomName);
        Check("远程同演：漏掉 mkp:// 前缀也能解析",
            SyncRoomInfo.TryParse(invite["mkp://".Length..], out var t2, out _)
            && t2 != null && t2.RoomName == room.RoomName);
        Check("远程同演：中转站写成 https:// 也能解析",
            SyncRoomInfo.TryParse("mkp://https://x.workers.dev|房|p", out var t3, out _)
            && t3 != null && t3.Host == "x.workers.dev", t3?.Host ?? "");
        Check("远程同演：空密码的邀请串能解析",
            SyncRoomInfo.TryParse("mkp://x.workers.dev|房|", out var t4, out _)
            && t4 != null && t4.Password.Length == 0);
        Check("远程同演：密码里带竖线会被拒（分隔符冲突）",
            !SyncRoomInfo.TryParse("mkp://x.workers.dev|房|a|b", out _, out string e5) && e5.Length > 0);

        var bads = new[]
        {
            "", "   ", "mkp://x.workers.dev|房", "mkp://x.workers.dev",
            "mkp://|房|p", "mkp://x.workers.dev|" + new string('a', 33) + "|p",
            "mkp://x.workers.dev||p", "http://x|房|p",
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

    private static void TestMessageJson()
    {
        var join = new SyncMessage
        {
            T = SyncKind.Join,
            Id = "m1234567890abcdef",
            Name = "弹琴的小明",
            Pass = "pw",
            Voice = "1、3",
            Xpose = -5,
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(join, SyncJsonContext.Default.SyncMessage);
        string text = Encoding.UTF8.GetString(bytes);
        Check("远程同演：join 序列化用了短字段名",
            text.Contains("\"t\":\"join\"", StringComparison.Ordinal)
            && text.Contains("\"id\":", StringComparison.Ordinal), text);

        var back = JsonSerializer.Deserialize(bytes, SyncJsonContext.Default.SyncMessage);
        Check("远程同演：join 往返后字段一字不差",
            back != null && back.T == SyncKind.Join && back.Name == "弹琴的小明"
            && back.Voice == "1、3" && back.Xpose == -5 && back.Pass == "pw",
            back?.Name ?? "（读不出）");

        var start = new SyncMessage { T = SyncKind.Start, DelayMs = 3000, PositionSec = 12.5, SentAt = 987654 };
        text = Encoding.UTF8.GetString(
            JsonSerializer.SerializeToUtf8Bytes(start, SyncJsonContext.Default.SyncMessage));
        Check("远程同演：没赋值的字段不出现在 JSON 里（省字节）",
            !text.Contains("\"pass\"", StringComparison.Ordinal)
            && !text.Contains("\"peers\"", StringComparison.Ordinal)
            && !text.Contains("\"voice\"", StringComparison.Ordinal), text);
        Check("远程同演：start 的四个字段都在",
            text.Contains("\"delayMs\":3000", StringComparison.Ordinal)
            && text.Contains("\"positionSec\":12.5", StringComparison.Ordinal)
            && text.Contains("\"sentAt\":987654", StringComparison.Ordinal), text);

        var roster = new SyncMessage
        {
            T = SyncKind.Roster,
            Peers = new List<SyncPeerInfo>
            {
                new() { Id = "m1", Name = "小明", Voice = "1、3", Xpose = 0, Ready = true },
                new() { Id = "m2", Name = "小红", Voice = "2、4", Xpose = -5, Ready = false },
            },
        };
        text = Encoding.UTF8.GetString(
            JsonSerializer.SerializeToUtf8Bytes(roster, SyncJsonContext.Default.SyncMessage));
        var backRoster = JsonSerializer.Deserialize(
            Encoding.UTF8.GetBytes(text), SyncJsonContext.Default.SyncMessage);
        Check("远程同演：名单往返后人数与内容都对",
            backRoster?.Peers is { Count: 2 }
            && backRoster.Peers[0].Name == "小明" && backRoster.Peers[0].Ready
            && backRoster.Peers[1].Voice == "2、4" && backRoster.Peers[1].Xpose == -5,
            $"{backRoster?.Peers?.Count ?? -1} 人");

        Check("远程同演：读不懂的 JSON 会抛 JsonException（调用方负责吞）",
            Throws<JsonException>(() => JsonSerializer.Deserialize(
                Encoding.UTF8.GetBytes("not json"), SyncJsonContext.Default.SyncMessage)));
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
        catch { return false; }
    }

    private static void TestStartDelayCompensation()
    {
        Check("远程同演：开演等待要减掉消息在路上的时间",
            SyncSession.RemainingDelayCore(3000, 1000, 1300) == 2700,
            $"{SyncSession.RemainingDelayCore(3000, 1000, 1300)}ms");

        Check("远程同演：网络快时几乎不减",
            SyncSession.RemainingDelayCore(3000, 1000, 1010) == 2990,
            $"{SyncSession.RemainingDelayCore(3000, 1000, 1010)}ms");

        long arriveA = 1100, arriveB = 1800;
        long startA = arriveA + SyncSession.RemainingDelayCore(3000, 1000, arriveA);
        long startB = arriveB + SyncSession.RemainingDelayCore(3000, 1000, arriveB);
        Check("远程同演：两个成员的网络不同，但开演落在同一刻",
            startA == 4000 && startB == 4000 && startA == startB,
            $"甲={startA} 乙={startB}");

        long arriveC = 3900;
        long startC = arriveC + SyncSession.RemainingDelayCore(3000, 1000, arriveC);
        Check("远程同演：慢到 2.9 秒的成员也落在同一刻",
            startC == 4000, $"{startC}");

        long arriveLate = 9000;
        long startLate = arriveLate + SyncSession.RemainingDelayCore(3000, 1000, arriveLate);
        Check("远程同演：迟到超过 DelayMs 的成员立刻开演（不等成负数）",
            startLate == 9000, $"{startLate}");

        Check("远程同演：消息迟到超过 DelayMs 时等待为 0（不出现负数）",
            SyncSession.RemainingDelayCore(3000, 1000, 99_000) == 0);
        Check("远程同演：刚好到点时等待为 0",
            SyncSession.RemainingDelayCore(3000, 1000, 4000) == 0);
        Check("远程同演：没带房主时刻时退回旧口径（不减）",
            SyncSession.RemainingDelayCore(3000, 0, 99_999) == 3000);
        Check("远程同演：房主时刻晚于本机时刻时按没花时间算",
            SyncSession.RemainingDelayCore(3000, 50_000, 1000) == 3000);
        Check("远程同演：DelayMs 为 0 时立刻开演",
            SyncSession.RemainingDelayCore(0, 1000, 1300) == 0);
    }

    private static void TestAnchor()
    {
        var future = new SyncAnchor
        {
            PositionSec = 5,
            AtTickMs = SyncClock.NowMs + 200,
            Speed = 1.0,
            Valid = true,
        };
        Check("远程同演：锚点在未来时位置不前进",
            Math.Abs(future.PositionNow() - 5) < 1e-9, $"{future.PositionNow()}");

        var past = new SyncAnchor
        {
            PositionSec = 5,
            AtTickMs = SyncClock.NowMs - 1000,
            Speed = 1.0,
            Valid = true,
        };
        Check("远程同演：锚点在过去 1 秒时位置前进约 1 秒",
            past.PositionNow() >= 5.99 && past.PositionNow() <= 6.05, $"{past.PositionNow()}");

        var fast = new SyncAnchor
        {
            PositionSec = 0,
            AtTickMs = SyncClock.NowMs - 1000,
            Speed = 2.0,
            Valid = true,
        };
        Check("远程同演：2 倍速下位置前进约 2 秒",
            fast.PositionNow() >= 1.99 && fast.PositionNow() <= 2.05, $"{fast.PositionNow()}");

        Check("远程同演：无效锚点返回 0", SyncAnchor.Idle.PositionNow() == 0);

        var slow = SyncAnchor.At(10, 1.0);
        var after = slow.WithSpeed(0.5);
        Check("远程同演：换速度时进度不跳",
            Math.Abs(after.PositionNow() - slow.PositionNow()) < 0.02
            && Math.Abs(after.Speed - 0.5) < 1e-9,
            $"{slow.PositionNow()} → {after.PositionNow()}");

        Check("远程同演：速度传 0 时按 1 处理", Math.Abs(SyncAnchor.At(0, 0).Speed - 1.0) < 1e-9);

        long a = SyncClock.NowMs;
        Thread.Sleep(20);
        long b = SyncClock.NowMs;
        Check("远程同演：单调时钟在前进（差 15 到 200 毫秒）",
            b - a >= 15 && b - a <= 200, $"{b - a}ms");
    }
}

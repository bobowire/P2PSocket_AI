// M6-02 KcpTunnelTransport 受控介质单测（设计 11 §11.1、M6-02 完成判定）：
// 帧型×conv 路由穷举、unknown-conv 拒绝、conv 上限降级、Adopt 后所有权与 Dispose 关 socket、
// 裸路径白名单闭包（迟到 STUN 形似帧不再杀隧道——顺带修复项红证）、裸队列 DropOldest 守恒、
// 帧自分界跨 KCP 段重组。介质=真实 loopback socket 对（丢包/乱序注入在 Core 属性单测覆盖）。
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using P2P.Client.Tunnel;
using P2P.Core.Protocol;
using P2P.Core.Tunnel;
using Xunit;

namespace P2P.Client.Tests;

public sealed class KcpTunnelTransportTests : IAsyncLifetime
{
    private Socket _sockA = null!, _sockB = null!, _sockC = null!;
    private IPEndPoint _epA = null!, _epB = null!;
    private KcpTunnelTransport _a = null!, _b = null!;

    private static uint _counter;

    /// <summary>构造假 PTP 帧（承载层不解密——16B 明文头+载荷即可路由）。</summary>
    private static byte[] Frame(byte type, uint channelId, int payloadLen = 4)
    {
        var f = new byte[16 + payloadLen];
        f[0] = 1;
        f[1] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(2), channelId);
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(6), ++_counter);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(14), (ushort)payloadLen);
        for (var i = 16; i < f.Length; i++)
            f[i] = (byte)(i * 31 + type);
        return f;
    }

    private (KcpTunnelTransport A, KcpTunnelTransport B) CreatePair(KcpTunnelTransportOptions? opts = null)
    {
        _sockA = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _sockB = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _sockA.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _sockB.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _epA = (IPEndPoint)_sockA.LocalEndPoint!;
        _epB = (IPEndPoint)_sockB.LocalEndPoint!;
        _a = KcpTunnelTransport.Adopt(_sockA, _epB, opts);
        _b = KcpTunnelTransport.Adopt(_sockB, _epA, opts);
        return (_a, _b);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_a is not null) await _a.DisposeAsync();
        if (_b is not null) await _b.DisposeAsync();
        _sockC?.Dispose();
    }

    private static async Task<byte[]> RecvAsync(KcpTunnelTransport t, int timeoutMs = 4000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var frame = await t.ReceiveAsync(cts.Token);
        Assert.NotNull(frame);
        return frame!;
    }

    private static async Task<List<byte[]>> RecvCountAsync(KcpTunnelTransport t, int count, int timeoutMs = 4000)
    {
        var frames = new List<byte[]>();
        using var cts = new CancellationTokenSource(timeoutMs);
        while (frames.Count < count)
        {
            var f = await t.ReceiveAsync(cts.Token);
            Assert.NotNull(f);
            frames.Add(f!);
        }
        return frames;
    }

    private static async Task AssertSameBytes(byte[] expected, byte[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        Assert.True(expected.AsSpan().SequenceEqual(actual), "帧字节不一致");
        await Task.CompletedTask;
    }

    // ── 帧型 × conv 路由穷举（设计 11 §4.1 路由表）──────────────────

    [Fact]
    public async Task 全帧型路由_往返完整_channel帧走channel_conv()
    {
        var (a, b) = CreatePair();
        a.EnsureChannel(42);
        b.EnsureChannel(42); // 接收侧 conv 就位（真实流=OPEN 处理后）

        var conv0Frames = new[]
        {
            Frame(PtpFrameType.Open, 42),      // OPEN 走 conv 0（路由表）
            Frame(PtpFrameType.Keepalive, 0),
            Frame(PtpFrameType.Ping, 0),
            Frame(PtpFrameType.Pong, 0),
            Frame(PtpFrameType.Rekey, 0),
            Frame(PtpFrameType.RekeyAck, 0),
        };
        var chFrames = new[]
        {
            Frame(PtpFrameType.OpenResult, 42),
            Frame(PtpFrameType.Window, 42),
            Frame(PtpFrameType.Data, 42, 1384), // 1400B 满帧→跨 2 个 KCP 段
            Frame(PtpFrameType.Close, 42),
        };
        foreach (var f in conv0Frames.Concat(chFrames))
            await a.SendAsync(f);

        var got = await RecvCountAsync(b, conv0Frames.Length + chFrames.Length);
        // 逐帧字节一致（conv 内有序：conv0 帧序、ch42 帧序各自保持）
        foreach (var f in conv0Frames)
            Assert.Contains(got, g => g.AsSpan().SequenceEqual(f));
        foreach (var f in chFrames)
            Assert.Contains(got, g => g.AsSpan().SequenceEqual(f));
        Assert.True(b.ConvExists(0) && b.ConvExists(42));
        Assert.True(b.PeerSegmentsReceived(42) > 0, "channel 帧须走 channel conv（非 conv 0 转发）");
    }

    [Fact]
    public async Task 帧自分界_跨KCP段重组_顺序保持()
    {
        var (a, b) = CreatePair();
        a.EnsureChannel(7);
        b.EnsureChannel(7);
        // 满帧 1400B（2 段）+ 满帧 + 微帧：流式自分界不粘不裂
        var f1 = Frame(PtpFrameType.Data, 7, 1384);
        var f2 = Frame(PtpFrameType.Data, 7, 1384);
        var f3 = Frame(PtpFrameType.Data, 7, 1);
        await a.SendAsync(f1);
        await a.SendAsync(f2);
        await a.SendAsync(f3);
        var got = await RecvCountAsync(b, 3);
        await AssertSameBytes(f1, got[0]);
        await AssertSameBytes(f2, got[1]);
        await AssertSameBytes(f3, got[2]);
    }

    [Fact]
    public async Task 多channel并发_各conv独立有序()
    {
        var (a, b) = CreatePair();
        a.EnsureChannel(0x1111);
        a.EnsureChannel(0x2222);
        b.EnsureChannel(0x1111);
        b.EnsureChannel(0x2222);
        const int n = 20;
        var sent = new List<(uint ch, byte[] frame)>();
        for (var i = 0; i < n; i++)
        {
            var ch = i % 2 == 0 ? 0x1111u : 0x2222u;
            var f = Frame(PtpFrameType.Data, ch, 100);
            await a.SendAsync(f);
            sent.Add((ch, f));
        }
        var got = await RecvCountAsync(b, n);
        // 各 channel 内序=发序（channel 间无全局序——按 channel 分组校验）
        foreach (var chGroup in sent.GroupBy(s => s.ch))
        {
            var expectedSeq = chGroup.Select(g => g.frame).ToList();
            var actualSeq = got.Where(g => BinaryPrimitives.ReadUInt32LittleEndian(g.AsSpan(2)) == chGroup.Key)
                .ToList();
            Assert.Equal(expectedSeq.Count, actualSeq.Count);
            for (var i = 0; i < expectedSeq.Count; i++)
                await AssertSameBytes(expectedSeq[i], actualSeq[i]);
        }
    }

    // ── unknown-conv 防线（§4.3 无 lazy accept）─────────────────────

    [Fact]
    public async Task unknown_conv包_丢弃计数_会话存活()
    {
        var (a, b) = CreatePair();
        a.EnsureChannel(42);
        b.EnsureChannel(42);

        // 伪造 conv=999 的 KCP 包直发 B（合法 KCP 形态、未经授权的 conv）
        _sockC = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _sockC.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var foreign = new List<byte[]>();
        var foreignPeer = new P2P.Core.Transport.Kcp.KcpPeer(999,
            new ForeignSink(_sockC, _epB, foreign), new P2P.Core.Transport.Kcp.KcpPeerOptions());
        foreignPeer.Update();
        foreignPeer.Send(new byte[100]);
        foreignPeer.Flush();
        Assert.NotEmpty(foreign); // 伪造包已上线
        await UntilAsync(() => b.UnknownConvDropped >= 1, 2000);
        Assert.True(b.UnknownConvDropped >= 1, "unknown conv 须丢弃+计数");

        // 会话存活：KCP 帧照常送达
        var f = Frame(PtpFrameType.Data, 42, 100);
        await a.SendAsync(f);
        var got = await RecvAsync(b);
        await AssertSameBytes(f, got);
    }

    private sealed class ForeignSink(Socket socket, IPEndPoint to, List<byte[]> sent) : P2P.Core.Transport.Kcp.IKcpDatagramSink
    {
        public void Send(ReadOnlySpan<byte> datagram)
        {
            var buf = new byte[1 + datagram.Length];
            buf[0] = 0x4B;
            datagram.CopyTo(buf.AsSpan(1));
            sent.Add(buf);
            socket.SendTo(buf, SocketFlags.None, to);
        }
    }

    private static async Task UntilAsync(Func<bool> done, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        while (!done())
        {
            try { await Task.Delay(10, cts.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    // ── conv 上限降级（决议⑤：超限全帧降级 conv 0）─────────────────

    [Fact]
    public async Task conv上限_超限channel降级conv0_正确性保持()
    {
        var (a, b) = CreatePair(new KcpTunnelTransportOptions { ConvCap = 1 });
        a.EnsureChannel(7);
        b.EnsureChannel(7);
        a.EnsureChannel(8); // 超上限 → 降级
        b.EnsureChannel(8);
        Assert.Equal(1, a.ConvCount);

        var f = Frame(PtpFrameType.Data, 8, 100); // 降级 channel 的帧
        await a.SendAsync(f);
        var got = await RecvAsync(b);
        await AssertSameBytes(f, got); // 仍可靠有序到达（走 conv 0）
        Assert.True(a.DegradedFrameCount >= 1, "降级帧须可观测");
    }

    [Fact]
    public async Task 超限channel销毁_降级项清理_conv0守卫永存()
    {
        // 审查修复红绿：原 _degraded 无清理路径（超限 id 从不入 _convs，TryReap 够不着——channel
        // 高频开关的长会话下单调增长）；行为口径：在降级集合内→DegradedFrameCount 递增，
        // RemoveChannel 清理后再发同 channel 帧→不再计数（未登记路由直达 conv 0 仍可靠到达）。
        var (a, b) = CreatePair(new KcpTunnelTransportOptions { ConvCap = 1 });
        a.EnsureChannel(7);
        a.EnsureChannel(8); // 超上限 → 入 _degraded
        b.EnsureChannel(7);
        b.EnsureChannel(8);

        var f = Frame(PtpFrameType.Data, 8, 64);
        await a.SendAsync(f);
        await RecvAsync(b);
        var counted = a.DegradedFrameCount;
        Assert.True(counted >= 1, "降级集合内的帧须计数");

        a.RemoveChannel(8); // 销毁 → _degraded 随之清理
        a.RemoveChannel(0); // conv 0 永存守卫：幂等、不置 Draining（原无守卫时更新泵 10ms 内收尸）
        await Task.Delay(60); // 放行更新泵一 tick（守卫缺失的红态在此窗口内显形）
        Assert.True(a.ConvExists(0), "conv 0 不得被 RemoveChannel 销毁");

        await a.SendAsync(f);
        var got = await RecvAsync(b);
        await AssertSameBytes(f, got); // 清理后仍走 conv 0 可靠到达
        Assert.Equal(counted, a.DegradedFrameCount); // 不再按降级计数=集合已清（旧代码此处恒红）
    }

    // ── 裸路径白名单闭包（§5.1：迟到 STUN 形似帧不杀隧道——修复项）──

    [Fact]
    public async Task 裸旁路_UDP_DGRAM与FRAG_丢包容忍路径()
    {
        var (a, b) = CreatePair();
        a.EnsureChannel(9);
        b.EnsureChannel(9);
        var dgram = Frame(PtpFrameType.UdpDgram, 9, 200);
        var frag = Frame(PtpFrameType.Frag, 9, 100);
        await a.SendAsync(dgram);
        await a.SendAsync(frag);
        var got = await RecvCountAsync(b, 2);
        Assert.Contains(got, g => g.AsSpan().SequenceEqual(dgram));
        Assert.Contains(got, g => g.AsSpan().SequenceEqual(frag));
    }

    [Fact]
    public async Task 迟到STUN形似帧_白名单外丢弃_隧道存活()
    {
        var (a, b) = CreatePair();
        a.EnsureChannel(42);
        b.EnsureChannel(42);

        _sockC = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _sockC.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        // 迟到 STUN 重传应答形态：首字节 0x01（恰与 PTP ver 同值）+ type 0x01（形似 OPEN）+ 垃圾
        var stunLike = new byte[64];
        stunLike[0] = 0x01;
        stunLike[1] = 0x01;
        for (var i = 2; i < stunLike.Length; i++)
            stunLike[i] = (byte)(i * 13);
        await _sockC.SendToAsync(stunLike, SocketFlags.None, _epB);
        // 纯垃圾（非魔数非 PTP ver）
        var garbage = new byte[40];
        garbage[0] = 0xAB;
        await _sockC.SendToAsync(garbage, SocketFlags.None, _epB);
        await UntilAsync(() => b.NonWhitelistDropped >= 2, 2000);
        Assert.True(b.NonWhitelistDropped >= 2, $"白名单外丢弃计数不足（{b.NonWhitelistDropped}）");

        // 隧道存活：KCP 帧照常（现状裸承载此形似帧会进接收循环→整隧道误杀——修复红证）
        var f = Frame(PtpFrameType.Data, 42, 100);
        await a.SendAsync(f);
        var got = await RecvAsync(b);
        await AssertSameBytes(f, got);
    }

    [Fact]
    public async Task 裸队列有界_守恒丢弃_不阻塞泵()
    {
        var (a, b) = CreatePair(new KcpTunnelTransportOptions { RawQueueCap = 4 });
        a.EnsureChannel(9);
        b.EnsureChannel(9);
        const int total = 300;
        for (var i = 0; i < total; i++)
            await a.SendAsync(Frame(PtpFrameType.UdpDgram, 9, 200));
        // 守恒：送达数 + 丢弃数 = 发送数（DropOldest 语义内丢失，泵不死）
        var received = 0;
        try
        {
            while (true)
            {
                using var cts = new CancellationTokenSource(200);
                received++;
                _ = await b.ReceiveAsync(cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        received--; // 最后一次超时未达
        Assert.Equal(total, received + (int)b.RawQueueDropped);

        // 泵存活：KCP 路径照常
        var f = Frame(PtpFrameType.Data, 9, 100);
        await a.SendAsync(f);
        var got = await RecvAsync(b);
        await AssertSameBytes(f, got);
    }

    // ── Adopt 所有权 / 帧上限 ───────────────────────────────────────

    [Fact]
    public async Task Dispose_关闭socket_二次幂等()
    {
        var (a, _) = CreatePair();
        await a.DisposeAsync();
        Assert.ThrowsAny<ObjectDisposedException>(() =>
            _sockA.SendTo(new byte[1], SocketFlags.None, new IPEndPoint(IPAddress.Loopback, 9)));
        await a.DisposeAsync(); // 幂等
    }

    [Fact]
    public async Task 超限帧_拒绝_非法帧头_拒绝()
    {
        var (a, b) = CreatePair();
        await Assert.ThrowsAsync<ProtocolException>(() =>
            a.SendAsync(Frame(PtpFrameType.Data, 1, KcpTunnelTransport.MaxUdpFrame - 16 + 1)).AsTask());
        var short_ = new byte[10];
        await Assert.ThrowsAsync<ProtocolException>(() => a.SendAsync(short_).AsTask());
        var badVer = Frame(PtpFrameType.Data, 1, 4);
        badVer[0] = 2;
        await Assert.ThrowsAsync<ProtocolException>(() => a.SendAsync(badVer).AsTask());
    }
}

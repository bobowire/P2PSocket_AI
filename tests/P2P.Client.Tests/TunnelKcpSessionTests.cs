// M6-03 会话级 KCP 承载测试（设计 11 §11.2）：KCP 承载上全帧型往返（OPEN/OPEN_OK/OPEN_FAIL/
// DATA/WINDOW/CLOSE/REKEY/REKEY_ACK/PING/KEEPALIVE/UDP_DGRAM/FRAG）+ 三缝 conv 生命周期
//（Ensure 于 OPEN 前后、Remove 于 OPEN_FAIL/CLOSE 后排空）。
// 建链=生产流 rehearsal：PtpHandshake 外驱动握手（裸 UDP socket 对）→ Adopt →
// TunnelSession.FromEstablishedKeys（M2-16 缝，即 M6-05 Puncher 将走的路径）。
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using P2P.Client.Tunnel;
using P2P.Core.Crypto;
using P2P.Core.Tunnel;
using Xunit;

namespace P2P.Client.Tests;

public sealed class TunnelKcpSessionTests
{
    private static readonly TimeSpan Idle = TimeSpan.FromHours(1);

    private sealed record Event(uint ChannelId, string Kind, object? Payload);

    private sealed class RecordingHandler : ITunnelChannelHandler
    {
        public ConcurrentQueue<Event> Events { get; } = [];
        /// <summary>OnData 即回报等额 WINDOW（模拟真实引擎消费侧授信——不开启则 64KiB 信用
        /// 耗尽后对端 SendDataAsync 挂起属设计语义）。</summary>
        public bool AutoGrant { get; set; }
        public void OnOpen(TunnelSession s, uint ch, OpenPayload p) => Events.Enqueue(new(ch, "open", p));
        public void OnOpenResult(TunnelSession s, uint ch, OpenResultPayload r) => Events.Enqueue(new(ch, "openresult", r));
        public void OnData(TunnelSession s, uint ch, ReadOnlyMemory<byte> d)
        {
            Events.Enqueue(new(ch, "data", d.ToArray()));
            if (AutoGrant)
                _ = s.SendWindowCreditAsync(ch, d.Length);
        }
        public void OnUdpDgram(TunnelSession s, uint ch, ReadOnlyMemory<byte> d) => Events.Enqueue(new(ch, "udp_dgram", d.ToArray()));
        public void OnClose(TunnelSession s, uint ch) => Events.Enqueue(new(ch, "close", null));
    }

    private sealed record KcpPair(
        TunnelSession A, TunnelSession B,
        KcpTunnelTransport Ta, KcpTunnelTransport Tb,
        RecordingHandler Ha, RecordingHandler Hb,
        Socket SockA, Socket SockB);

    /// <summary>外驱动握手 + Adopt + FromEstablishedKeys（M6-05 生产流 rehearsal）。</summary>
    private static async Task<KcpPair> EstablishAsync(bool withRekeyKeys = true)
    {
        var staticA = EcKeyPair.Generate();
        var staticB = EcKeyPair.Generate();
        var sockA = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var sockB = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sockA.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        sockB.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var epA = (IPEndPoint)sockA.LocalEndPoint!;
        var epB = (IPEndPoint)sockB.LocalEndPoint!;
        var sessionId = Guid.NewGuid();
        var buf = new byte[65_535];
        var any = new IPEndPoint(IPAddress.Any, 0);

        using var initiator = PtpHandshake.StartInitiator(sessionId, staticA, staticB.ExportPublicKey());
        await sockA.SendToAsync(initiator.THello1Wire, SocketFlags.None, epB);

        byte[] t1;
        while (true)
        {
            var r = await sockB.ReceiveFromAsync(buf, SocketFlags.None, any);
            t1 = buf.AsMemory(0, r.ReceivedBytes).ToArray();
            if (PtpFrameCodec.ParseHeader(t1).Type == PtpFrameType.THello1) break;
        }
        using var responder = PtpHandshake.AcceptTHello1(t1, staticB, staticA.ExportPublicKey());
        await sockB.SendToAsync(responder.THello2Wire, SocketFlags.None, epA);

        byte[] t2;
        while (true)
        {
            var r = await sockA.ReceiveFromAsync(buf, SocketFlags.None, any);
            t2 = buf.AsMemory(0, r.ReceivedBytes).ToArray();
            if (PtpFrameCodec.ParseHeader(t2).Type == PtpFrameType.THello2) break;
        }
        var (tConfirm, keysA) = initiator.HandleTHello2(t2);
        await sockA.SendToAsync(tConfirm, SocketFlags.None, epB);

        byte[] tc;
        while (true)
        {
            var r = await sockB.ReceiveFromAsync(buf, SocketFlags.None, any);
            tc = buf.AsMemory(0, r.ReceivedBytes).ToArray();
            if (PtpFrameCodec.ParseHeader(tc).Type == PtpFrameType.TConfirm) break;
        }
        var keysB = responder.VerifyTConfirm(tc);

        // 承载切换：Adopt（socket 所有权移交；握手期裸帧收发到此为止）
        var ta = KcpTunnelTransport.Adopt(sockA, epB);
        var tb = KcpTunnelTransport.Adopt(sockB, epA);
        var ha = new RecordingHandler();
        var hb = new RecordingHandler();
        var opts = new TunnelSessionOptions { KeepaliveInterval = Idle };
        var peerA = Guid.NewGuid();
        var peerB = Guid.NewGuid();
        var a = TunnelSession.FromEstablishedKeys(sessionId, peerB, true, keysA, ta, ha, opts,
            staticKey: withRekeyKeys ? staticA : null,
            peerStaticPub: withRekeyKeys ? staticB.ExportPublicKey() : null);
        var b = TunnelSession.FromEstablishedKeys(sessionId, peerA, false, keysB, tb, hb, opts,
            staticKey: withRekeyKeys ? staticB : null,
            peerStaticPub: withRekeyKeys ? staticA.ExportPublicKey() : null);
        return new KcpPair(a, b, ta, tb, ha, hb, sockA, sockB);
    }

    private static async Task UntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = Environment.TickCount64 + (long)(timeout ?? TimeSpan.FromSeconds(8)).TotalMilliseconds;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException($"等待超时：{what}");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task KCP承载_全帧型往返_信用_UDP_DGRAM_conv生命周期()
    {
        var p = await EstablishAsync();
        try
        {
            // conv 0 生而存在（双方）；channel conv 随 OPEN 建立
            Assert.True(p.Ta.ConvExists(0) && p.Tb.ConvExists(0));

            var ch = p.A.AllocateChannelId();
            await p.A.SendOpenAsync(ch, new OpenPayload("tcp", "self", 8080));
            await UntilAsync(() => p.Hb.Events.Any(e => e.Kind == "open"), "B 收 OPEN");
            await UntilAsync(() => p.Tb.ConvExists(ch), "B 侧 conv 就位（三缝②：OnOpen 前登记）");
            Assert.True(p.Ta.ConvExists(ch), "A 侧 conv 就位（三缝①：SendOpen 前置）");

            await p.B.SendOpenResultAsync(ch, new OpenResultPayload(true, null));
            await UntilAsync(() => p.Ha.Events.Any(e => e.Kind == "openresult"), "A 收 OPEN_OK");

            // DATA 双向（信用闸内）+ WINDOW 回报
            await p.A.SendDataAsync(ch, "hello-a"u8.ToArray());
            await p.B.SendDataAsync(ch, "hello-b"u8.ToArray());
            await UntilAsync(() => p.Hb.Events.Any(e => e.Kind == "data") && p.Ha.Events.Any(e => e.Kind == "data"),
                "DATA 双向到达");
            await p.B.SendWindowCreditAsync(ch, 7);

            // UDP_DGRAM（裸旁路）+ FRAG（>1368 触发分片）
            var dgramCh = p.A.AllocateChannelId();
            await p.A.SendOpenAsync(dgramCh, new OpenPayload("udp", "self", 5353));
            await UntilAsync(() => p.Hb.Events.Any(e => e.Kind == "open" && e.ChannelId == dgramCh), "UDP channel OPEN");
            await p.A.SendUdpDgramAsync(dgramCh, new byte[3000]); // FRAG 序列
            await UntilAsync(() => p.Hb.Events.Any(e => e.Kind == "udp_dgram" && e.ChannelId == dgramCh),
                "FRAG 重组交付");

            // CLOSE → 对端 conv 排空销毁（三缝④）
            await p.A.SendCloseAsync(ch);
            await UntilAsync(() => p.Hb.Events.Any(e => e.Kind == "close"), "B 收 CLOSE");
            await UntilAsync(() => !p.Tb.ConvExists(ch), "B 侧 conv 排空销空");

            // PING RTT（conv 0）
            var rtt = await p.A.PingAsync();
            Assert.True(rtt >= TimeSpan.Zero);

            // OPEN_FAIL → 发起侧 conv 即毁（三缝③：OpenResult !Ok）
            var ch2 = p.A.AllocateChannelId();
            await p.A.SendOpenAsync(ch2, new OpenPayload("tcp", "1.2.3.4", 9999));
            await UntilAsync(() => p.Hb.Events.Any(e => e.Kind == "open" && e.ChannelId == ch2), "B 收第二个 OPEN");
            await p.B.SendOpenResultAsync(ch2, new OpenResultPayload(false, "refused"));
            await UntilAsync(() => !p.Ta.ConvExists(ch2), "A 侧 OPEN_FAIL conv 即毁");

            Assert.False(p.A.IsClosed);
            Assert.False(p.B.IsClosed);
        }
        finally
        {
            await p.A.DisposeAsync();
            await p.B.DisposeAsync();
        }
    }

    [Fact]
    public async Task KCP承载_REKEY轮换_代际推进()
    {
        var p = await EstablishAsync(withRekeyKeys: true);
        try
        {
            var genBefore = p.B.RekeyGeneration;
            var result = await p.A.TriggerRekeyAsync();
            Assert.Equal(RekeyOutcome.Ok, result.Outcome);
            await UntilAsync(() => p.B.RekeyGeneration > genBefore, "B 侧代际推进");

            // 轮换后帧照常（新代密钥下 OPEN/OPEN_RESULT 往返）
            var ch = p.A.AllocateChannelId();
            await p.A.SendOpenAsync(ch, new OpenPayload("tcp", "self", 8080));
            await UntilAsync(() => p.Hb.Events.Any(e => e.Kind == "open"), "REKEY 后 OPEN 到达");
            await p.B.SendOpenResultAsync(ch, new OpenResultPayload(true, null));
            await UntilAsync(() => p.Ha.Events.Any(e => e.Kind == "openresult"), "REKEY 后 OPEN_RESULT 到达");
        }
        finally
        {
            await p.A.DisposeAsync();
            await p.B.DisposeAsync();
        }
    }

    [Fact]
    public async Task KCP承载_大流量信用窗推进_零缺段零重复_控制帧可达()
    {
        // 单 channel 连续 200×1KiB DATA（跨多 KCP 段，信用耗尽自然挂起等 WINDOW——KCP 保证回报必达）
        var p = await EstablishAsync(withRekeyKeys: false);
        p.Hb.AutoGrant = true; // 消费侧等额授信（200×1KiB 超 64KiB 信用，须回报推进）
        try
        {
            var ch = p.A.AllocateChannelId();
            await p.A.SendOpenAsync(ch, new OpenPayload("tcp", "self", 8080));
            await UntilAsync(() => p.Hb.Events.Any(e => e.Kind == "open"), "OPEN");
            await p.B.SendOpenResultAsync(ch, new OpenResultPayload(true, null));
            await UntilAsync(() => p.Ha.Events.Any(e => e.Kind == "openresult"), "OPEN_OK");

            const int n = 200;
            for (var i = 0; i < n; i++)
            {
                var payload = new byte[1024];
                payload[0] = (byte)i;
                await p.A.SendDataAsync(ch, payload);
            }
            await UntilAsync(() => p.Hb.Events.Count(e => e.Kind == "data") >= n, $"{n} 帧 DATA 全达",
                TimeSpan.FromSeconds(15));
            var dataEvents = p.Hb.Events.Where(e => e.Kind == "data").Select(e => (byte[])e.Payload!).ToList();
            Assert.Equal(n, dataEvents.Count);
            for (var i = 0; i < n; i++)
                Assert.Equal((byte)i, dataEvents[i][0]); // 严格有序（零缺段零重复）

            var rtt = await p.A.PingAsync(); // 满载后 conv 0 控制帧仍可达
            Assert.True(rtt >= TimeSpan.Zero);
        }
        finally
        {
            await p.A.DisposeAsync();
            await p.B.DisposeAsync();
        }
    }
}

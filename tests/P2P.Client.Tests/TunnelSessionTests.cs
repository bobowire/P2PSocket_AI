using System.Collections.Concurrent;
using System.Threading.Channels;
using P2P.Client.Tunnel;
using P2P.Core.Crypto;
using P2P.Core.Tunnel;
using Xunit;

namespace P2P.Client.Tests;

/// <summary>
/// M1-25 TunnelHost/TunnelSession 单测（内存管道，任务清单：帧往返、counter 重放拒绝、断链事件、会话表）。
/// </summary>
public sealed class TunnelSessionTests
{
    private static readonly TimeSpan Idle = TimeSpan.FromHours(1); // 关闭心跳防干扰

    private static (EcKeyPair A, EcKeyPair B) StaticKeys()
    {
        var a = EcKeyPair.Generate();
        var b = EcKeyPair.Generate();
        return (a, b);
    }

    /// <summary>双侧建立会话（内存管道对 + 真实 PTP 握手）。peerA/peerB 控制对端设备号（会话表测试用）。</summary>
    private static async Task<(TunnelSession A, TunnelSession B, MemoryTransport Ta, MemoryTransport Tb, RecordingHandler Ha, RecordingHandler Hb)>
        EstablishAsync(TunnelSessionOptions? options = null, Guid? peerA = null, Guid? peerB = null)
    {
        var (staticA, staticB) = StaticKeys();
        var (ta, tb) = MemoryTransport.CreatePair();
        var ha = new RecordingHandler();
        var hb = new RecordingHandler();
        var sessionId = Guid.NewGuid();
        peerA ??= Guid.NewGuid();
        peerB ??= Guid.NewGuid();
        options ??= new TunnelSessionOptions { KeepaliveInterval = Idle };

        var connectTask = TunnelSession.ConnectAsync(sessionId, peerB.Value, staticA,
            staticB.ExportPublicKey(), ta, ha, options);
        var t1 = await tb.ReceiveAsync() ?? throw new IOException("无 THello1");
        var acceptTask = TunnelSession.AcceptAsync(peerA.Value, t1, staticB,
            staticA.ExportPublicKey(), tb, hb, options);
        var sessionA = await connectTask;
        var sessionB = await acceptTask;
        return (sessionA, sessionB, ta, tb, ha, hb);
    }

    private static async Task UntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = Environment.TickCount64 + (long)(timeout ?? TimeSpan.FromSeconds(5)).TotalMilliseconds;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException($"等待超时：{what}");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task 握手建立_帧往返_OPEN结果_DATA_Ping()
    {
        var (a, b, _, _, ha, hb) = await EstablishAsync();
        await using (a)
        await using (b)
        {
            Assert.Equal(a.SessionId, b.SessionId);
            Assert.True(a.IsInitiator);
            Assert.False(b.IsInitiator);

            // OPEN（A 访问侧 → B 目标侧）
            var ch = a.AllocateChannelId();
            await a.SendOpenAsync(ch, new OpenPayload("tcp", "self", 8080));
            await UntilAsync(() => hb.Events.Any(e => e.Kind == "open"), "B 收到 OPEN");
            var open = hb.Events.Single(e => e.Kind == "open");
            Assert.Equal(ch, open.ChannelId);
            Assert.Equal("tcp", ((OpenPayload)open.Payload!).TargetProto);
            Assert.Equal("self", ((OpenPayload)open.Payload!).TargetAddr);
            Assert.Equal((ushort)8080, ((OpenPayload)open.Payload!).TargetPort);

            // OPEN_OK（B → A）
            await b.SendOpenResultAsync(ch, new OpenResultPayload(true, null));
            await UntilAsync(() => ha.Events.Any(e => e.Kind == "openresult"), "A 收到 OPEN_RESULT");
            Assert.True(((OpenResultPayload)ha.Events.Single(e => e.Kind == "openresult").Payload!).Ok);

            // DATA 双向
            await a.SendDataAsync(ch, "hello-from-a"u8.ToArray());
            await b.SendDataAsync(ch, "hello-from-b"u8.ToArray());
            await UntilAsync(() => hb.Events.Any(e => e.Kind == "data") && ha.Events.Any(e => e.Kind == "data"), "DATA 双向到达");
            Assert.Equal("hello-from-a"u8.ToArray(), (byte[])hb.Events.Single(e => e.Kind == "data").Payload!);
            Assert.Equal("hello-from-b"u8.ToArray(), (byte[])ha.Events.Single(e => e.Kind == "data").Payload!);

            // CLOSE
            await a.SendCloseAsync(ch);
            await UntilAsync(() => hb.Events.Any(e => e.Kind == "close"), "B 收到 CLOSE");
            Assert.Equal(ch, hb.Events.Single(e => e.Kind == "close").ChannelId);

            // PING/PONG RTT（诊断，02 §4.2 0x06）
            var rtt = await a.PingAsync();
            Assert.True(rtt >= TimeSpan.Zero);

            // 超长 DATA 拒绝（02 §4.3 上限 16KiB）
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => a.SendDataAsync(ch, new byte[PtpFrameCodec.MaxDataPayload + 1]).AsTask());
        }
    }

    [Fact]
    public async Task 同帧重放_丢弃且不重复分发()
    {
        var (a, b, ta, tb, _, hb) = await EstablishAsync();
        await using (a)
        await using (b)
        {
            var ch = a.AllocateChannelId();
            await a.SendDataAsync(ch, "x"u8.ToArray());
            await UntilAsync(() => hb.Events.Any(e => e.Kind == "data"), "首次送达");

            var wire = ta.Sent.Last(f => PtpFrameCodec.ParseHeader(f).Type == PtpFrameType.Data);
            tb.Inject(wire); // 重放同一帧（SEC-12）
            tb.Inject(wire);
            await UntilAsync(() => b.ReplayDropped >= 2, "重放计数");

            await Task.Delay(100);
            Assert.Single(hb.Events, e => e.Kind == "data"); // 仅首次分发
            Assert.False(b.IsClosed); // 重放丢弃不断链（告警语义）
        }
    }

    [Fact]
    public async Task 对端静默_心跳三次未响应_断链事件()
    {
        var (staticA, staticB) = StaticKeys();
        var (ta, tb) = MemoryTransport.CreatePair();
        var fast = new TunnelSessionOptions
        {
            KeepaliveInterval = TimeSpan.FromMilliseconds(50),
            HandshakeTimeout = TimeSpan.FromSeconds(5),
        };

        // B 侧仅完成握手后静默（不回 KEEPALIVE）
        var connectTask = TunnelSession.ConnectAsync(Guid.NewGuid(), Guid.NewGuid(), staticA,
            staticB.ExportPublicKey(), ta, NullChannelHandler.Instance, fast);
        var t1 = await tb.ReceiveAsync() ?? throw new IOException();
        using var responder = PtpHandshake.AcceptTHello1(t1, staticB, staticA.ExportPublicKey());
        await tb.SendAsync(responder.THello2Wire);
        var confirm = await tb.ReceiveAsync() ?? throw new IOException();
        responder.VerifyTConfirm(confirm);

        var a = await connectTask;
        var reasons = new ConcurrentQueue<string>();
        a.Disconnected += (_, reason) => reasons.Enqueue(reason);
        await UntilAsync(() => reasons.Count > 0, "keepalive 断链", TimeSpan.FromSeconds(5));
        Assert.Contains("keepalive_timeout", reasons.Single());
        Assert.True(a.IsClosed);
        Assert.Equal((ulong)3, a.SentFrames); // 恰发 3 次心跳（THello1/TConfirm 为明文握手帧不计）
    }

    [Fact]
    public async Task 双侧互发心跳_持续存活不断链()
    {
        // 双侧都是真实会话（KEEPALIVE 双向独立 20s 周期，02 §4.5——收到对端帧即存活证明）
        var fast = new TunnelSessionOptions
        {
            KeepaliveInterval = TimeSpan.FromMilliseconds(50),
            HandshakeTimeout = TimeSpan.FromSeconds(5),
        };
        var (a, b, _, _, _, _) = await EstablishAsync(fast);
        var aDown = false;
        var bDown = false;
        a.Disconnected += (_, _) => aDown = true;
        b.Disconnected += (_, _) => bDown = true;
        await using (a)
        await using (b)
        {
            await Task.Delay(400); // > 6 个心跳周期（3 次未响应阈值 150ms）
            Assert.False(aDown);
            Assert.False(bDown);
            Assert.False(a.IsClosed);
            Assert.False(b.IsClosed);
        }
    }

    [Fact]
    public async Task 会话表_取用_替换_移除_断链摘表()
    {
        var host = new TunnelHost();
        var disconnected = new ConcurrentQueue<(Guid Peer, string Reason)>();
        host.SessionDisconnected += (peer, reason) => disconnected.Enqueue((peer, reason));
        var peerB = Guid.NewGuid();
        await using (host)
        {
            // 取用：Attach 后 Get 命中，未知设备 null
            var (a, _, _, _, _, _) = await EstablishAsync(peerB: peerB);
            host.Attach(a);
            Assert.Same(a, host.Get(peerB));
            Assert.Null(host.Get(Guid.NewGuid()));

            // 替换：同设备对新会话 → 旧会话销毁（replaced）+ 表内只剩新会话（02 §4.5 重建=新 sessionId）
            var (a2, _, _, _, _, _) = await EstablishAsync(peerB: peerB);
            host.Attach(a2);
            Assert.True(a.IsClosed);
            Assert.Same(a2, host.Get(peerB));
            Assert.Contains(disconnected, e => e.Peer == peerB && e.Reason.Contains("replaced"));

            // 主动移除（解绑/停机）
            await host.RemoveAsync(peerB, "unit");
            Assert.Null(host.Get(peerB));
            Assert.True(a2.IsClosed);

            // 断链摘表 + 事件转发（KEEPALIVE 断链/传输故障由引擎订阅）
            var (a3, _, _, _, _, _) = await EstablishAsync(peerB: peerB);
            host.Attach(a3);
            a3.Close("unit");
            await UntilAsync(() => host.Get(peerB) is null, "断链摘表");
            Assert.Contains(disconnected, e => e.Peer == peerB && e.Reason.Contains("local_close"));
        }
    }

    // —— 桩与工具 ——————————————————————————————————————————————

    private sealed record Event(uint ChannelId, string Kind, object? Payload);

    private sealed class RecordingHandler : ITunnelChannelHandler
    {
        public ConcurrentQueue<Event> Events { get; } = [];
        public void OnOpen(uint channelId, OpenPayload open) => Events.Enqueue(new(channelId, "open", open));
        public void OnOpenResult(uint channelId, OpenResultPayload result) => Events.Enqueue(new(channelId, "openresult", result));
        public void OnData(uint channelId, ReadOnlyMemory<byte> data) => Events.Enqueue(new(channelId, "data", data.ToArray()));
        public void OnClose(uint channelId) => Events.Enqueue(new(channelId, "close", null));
    }
}

/// <summary>内存传输对（单测）：双向无界队列；Sent 记录本端发出全部帧；Inject 模拟对端注入。</summary>
internal sealed class MemoryTransport(Channel<byte[]> inbox, Channel<byte[]> outbox) : ITunnelTransport
{
    public List<byte[]> Sent { get; } = [];

    public static (MemoryTransport A, MemoryTransport B) CreatePair()
    {
        var ab = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        var ba = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        return (new MemoryTransport(ba, ab), new MemoryTransport(ab, ba));
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
    {
        var copy = frame.ToArray();
        lock (Sent) Sent.Add(copy);
        return outbox.Writer.WriteAsync(copy, ct);
    }

    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken ct = default)
    {
        try { return await inbox.Reader.ReadAsync(ct); }
        catch (ChannelClosedException) { return null; }
    }

    /// <summary>测试注：直接向本端收件箱写帧（重放模拟）。</summary>
    public void Inject(byte[] frame) => inbox.Writer.TryWrite(frame);

    public ValueTask DisposeAsync()
    {
        inbox.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

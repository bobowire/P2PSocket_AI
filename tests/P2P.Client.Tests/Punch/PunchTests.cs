using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using P2P.Client.Control;
using P2P.Client.Punch;
using P2P.Client.Tunnel;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using P2P.Core.Stun;
using P2P.Core.Tunnel;
using P2P.Core.Utils;
using Xunit;

namespace P2P.Client.Tests;

// ── M1-26 单测（任务清单：串行顺序、超时失败事件、成功事件带端点、被动侧收到邀请即探测上报并发包）──

/// <summary>StunProber：回环假 STUN 应答器（真实 StunCodec 组包/解包）。</summary>
public sealed class StunProberTests : IDisposable
{
    private readonly UdpClient _server = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public StunProberTests()
    {
        var ep = (IPEndPoint)_server.Client.LocalEndPoint!;
        ServerEndpoint = ep;
        _loop = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var r = await _server.ReceiveAsync(_cts.Token);
                    var tid = r.Buffer.AsSpan(8, StunCodec.TransactionIdLen).ToArray(); // 头 type2+len2+magic4 → tid@8
                    // 先投噪声包（打洞窗口混入语义），再投真实响应
                    await _server.SendAsync(new byte[32], 32, r.RemoteEndPoint);
                    var resp = StunCodec.BuildBindingResponse(tid,
                        r.RemoteEndPoint.Address, (ushort)r.RemoteEndPoint.Port);
                    await _server.SendAsync(resp, resp.Length, r.RemoteEndPoint);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    public IPEndPoint ServerEndpoint { get; }

    [Fact]
    public async Task 探测返回本端映射端点_跳过无关包()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var mapped = await StunProber.ProbeAsync(socket, ServerEndpoint,
            Guid.NewGuid(), RandomGenerator.Bytes(32), new ClockSync(), perTryTimeout: TimeSpan.FromSeconds(2));
        var local = (IPEndPoint)socket.LocalEndPoint!;
        Assert.Equal(local, mapped); // 回环：映射端点=本端
    }

    [Fact]
    public async Task 无应答_重试耗尽抛IOException()
    {
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); // 占位（不响应）
        _cts.Cancel();
        try { await _loop; } catch { }
        _server.Dispose(); // 换成静默：直接关闭应答器

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        await Assert.ThrowsAsync<IOException>(() => StunProber.ProbeAsync(socket, ServerEndpoint,
            Guid.NewGuid(), RandomGenerator.Bytes(32), new ClockSync(),
            retries: 2, perTryTimeout: TimeSpan.FromMilliseconds(100)));
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _server.Dispose();
        _cts.Dispose();
    }
}

/// <summary>UdpPunchTransport：回环帧往返、超长拒绝、关闭语义。</summary>
public sealed class UdpPunchTransportTests
{
    private static (Socket A, Socket B) SocketPair()
    {
        var a = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        a.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var b = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        b.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return (a, b);
    }

    [Fact]
    public async Task 帧往返()
    {
        var (sa, sb) = SocketPair();
        var bEp = (IPEndPoint)sb.LocalEndPoint!;
        await using var ta = new UdpPunchTransport(sa, bEp);
        await using var tb = new UdpPunchTransport(sb, (IPEndPoint)sa.LocalEndPoint!);
        var frame = PtpFrameCodec.BuildHandshake(PtpFrameType.THello1, new byte[97]);
        await ta.SendAsync(frame);
        var got = await tb.ReceiveAsync();
        Assert.Equal(frame, got);
    }

    [Fact]
    public async Task 超长帧拒绝()
    {
        var (sa, sb) = SocketPair();
        await using var ta = new UdpPunchTransport(sa, (IPEndPoint)sb.LocalEndPoint!);
        sb.Dispose();
        await Assert.ThrowsAsync<ProtocolException>(
            () => ta.SendAsync(new byte[UdpPunchTransport.MaxUdpFrame + 1]).AsTask());
    }

    [Fact]
    public async Task 承载关闭_接收返回null()
    {
        var (sa, sb) = SocketPair();
        var tb = new UdpPunchTransport(sb, (IPEndPoint)sa.LocalEndPoint!);
        await using (tb)
        {
            sa.Dispose();
            await tb.DisposeAsync();
            Assert.Null(await tb.ReceiveAsync());
        }
    }
}

/// <summary>Puncher：访问方/被邀请方两段式全链路（真实 UDP 回环 + 假控制面缝）。</summary>
public sealed class PuncherTests
{
    /// <summary>控制面假缝：记录探测/上报/请求，供测试编排（模拟服务端延后 Ack）。</summary>
    private sealed class FakeSeams
    {
        public TaskCompletionSource<EndpointPair> Probed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<EndpointPair> Reported { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<(Guid SessionId, EndpointPair Endpoints)> Reports { get; } = [];
        public Func<Guid, Guid?, string, EndpointPair, CancellationToken, Task<PunchRequestAck>>? RequestHandler;
        public Exception? RequestError;

        public Task<IPEndPoint> ProbeAsync(Socket socket, CancellationToken ct)
        {
            // socket 绑 0.0.0.0：模拟 STUN 返回真实可达地址（回环同端口）
            var ep = (IPEndPoint)socket.LocalEndPoint!;
            var mapped = new IPEndPoint(IPAddress.Loopback, ep.Port);
            Probed.TrySetResult(new EndpointPair(new P2P.Core.Protocol.Endpoint(
                mapped.Address.ToString(), (ushort)mapped.Port), null));
            return Task.FromResult(mapped);
        }

        public Task ReportAsync(Guid sessionId, EndpointPair endpoints, CancellationToken ct)
        {
            Reports.Enqueue((sessionId, endpoints));
            Reported.TrySetResult(endpoints);
            return Task.CompletedTask;
        }

        public Task<PunchRequestAck> SendRequestAsync(Guid target, Guid? trigger, string proto,
            EndpointPair requesterEndpoints, CancellationToken ct)
        {
            if (RequestError is not null) return Task.FromException<PunchRequestAck>(RequestError);
            if (RequestHandler is not null) return RequestHandler(target, trigger, proto, requesterEndpoints, ct);
            return Task.FromResult(new PunchRequestAck(0, 0, MsgType.PunchRequest, Guid.NewGuid(),
                new PeerInfo(target, "?", "000000", []),
                new EndpointPair(new P2P.Core.Protocol.Endpoint("127.0.0.1", 1), null), 3, false));
        }
    }

    private static (Puncher Puncher, FakeSeams Seams) Make(EcKeyPair staticKey) =>
        Make(staticKey, out _);

    private static (Puncher Puncher, FakeSeams Seams) Make(EcKeyPair staticKey, out FakeSeams seams)
    {
        seams = new FakeSeams();
        return (new Puncher(seams.SendRequestAsync, seams.ReportAsync, seams.ProbeAsync, staticKey), seams);
    }

    [Fact]
    public async Task 两段式全链路_双方握手成功_可互通()
    {
        using var staticA = EcKeyPair.Generate();
        using var staticB = EcKeyPair.Generate();
        var deviceA = Guid.NewGuid();
        var deviceB = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var triggerMapping = Guid.NewGuid();

        var (puncherA, seamsA) = Make(staticA);
        var (puncherB, seamsB) = Make(staticB);
        using (puncherA)
        using (puncherB)
        {
            EndpointPair? requestedByA = null;
            // A 的 0x70 假缝：模拟服务端——等 B 的 0x76 端点上报后回延后 Ack（02 §5.1④）
            seamsA.RequestHandler = async (target, trigger, proto, endpoints, ct) =>
            {
                var bReported = await seamsB.Reported.Task;
                requestedByA = endpoints;
                return new PunchRequestAck(0, 0, MsgType.PunchRequest, sessionId,
                    new PeerInfo(deviceB, "B", "b00000", staticB.ExportPublicKey()),
                    new EndpointPair(bReported.Udp, null), 3, false);
            };

            // A 出队打洞：0x70 由假缝接管（假缝内先等 B 上报 → B 必须已启动应答）
            var outcomeATask = puncherA.InitiateAsync(deviceB, triggerMapping, "udp");

            // B 收到邀请（A 端点需 A 已探测——invite 构造等待 A 探测结果）
            var aEp = await seamsA.Probed.Task;
            var invite = new PunchInvite(0, 0, MsgType.PunchInvite, sessionId,
                new PeerInfo(deviceA, "A", "a00000", staticA.ExportPublicKey()),
                new EndpointPair(aEp.Udp, null), 3, false);
            var outcomeB = await puncherB.RespondAsync(invite);
            var outcomeA = await outcomeATask;

            // 双方成功、会话配对、端点交叉一致
            Assert.True(outcomeA.Ok, outcomeA.FailReason);
            Assert.True(outcomeB.Ok, outcomeB.FailReason);
            Assert.Equal(sessionId, outcomeA.SessionId);
            Assert.Equal(sessionId, outcomeB.SessionId);
            Assert.Equal(deviceB, outcomeA.Session!.PeerDeviceId);
            Assert.Equal(deviceA, outcomeB.Session!.PeerDeviceId);
            Assert.Equal(outcomeA.LocalEndpoint, outcomeB.PeerEndpoint);
            Assert.Equal(outcomeB.LocalEndpoint, outcomeA.PeerEndpoint);

            // A 的 0x70 第一段上送端点=其探测端点（OQ-18）
            Assert.NotNull(requestedByA);
            Assert.Equal(aEp.Udp!.Host, requestedByA!.Udp!.Host);
            // B 的 0x76 上报：sessionId 正确、端点=B 探测端点
            var (reportedSession, reportedPair) = seamsB.Reports.Single();
            Assert.Equal(sessionId, reportedSession);
            Assert.Equal(outcomeB.LocalEndpoint!.Port, reportedPair.Udp!.Port);

            // 会话互通（PING/PONG 内嵌于 TunnelSession）
            var rtt = await outcomeA.Session!.PingAsync();
            Assert.True(rtt >= TimeSpan.Zero);
            await outcomeA.Session.DisposeAsync();
            await outcomeB.Session!.DisposeAsync();
        }
    }

    [Fact]
    public async Task 被邀请方_邀请缺端点_失败且不上报()
    {
        using var staticKey = EcKeyPair.Generate();
        var (puncher, seams) = Make(staticKey);
        using (puncher)
        {
            var invite = new PunchInvite(0, 0, MsgType.PunchInvite, Guid.NewGuid(),
                new PeerInfo(Guid.NewGuid(), "A", "a00000", staticKey.ExportPublicKey()),
                new EndpointPair(null, null), 3, false);
            var outcome = await puncher.RespondAsync(invite);
            Assert.False(outcome.Ok);
            Assert.Contains("未携带发起方", outcome.FailReason);
            Assert.Empty(seams.Reports); // 未进入 0x76 上报
        }
    }

    [Fact]
    public async Task 探测失败_被动侧不上报_访问侧立即失败()
    {
        using var staticKey = EcKeyPair.Generate();
        var seams = new FakeSeams();
        Task<IPEndPoint> BrokenProbe(Socket socket, CancellationToken ct)
            => Task.FromException<IPEndPoint>(new IOException("stun down"));
        var puncher = new Puncher(seams.SendRequestAsync, seams.ReportAsync, BrokenProbe, staticKey);
        using (puncher)
        {
            var invite = new PunchInvite(0, 0, MsgType.PunchInvite, Guid.NewGuid(),
                new PeerInfo(Guid.NewGuid(), "A", "a00000", staticKey.ExportPublicKey()),
                new EndpointPair(new P2P.Core.Protocol.Endpoint("127.0.0.1", 1), null), 3, false);
            var outcome = await puncher.RespondAsync(invite);
            Assert.False(outcome.Ok);
            Assert.Contains("stun_failed", outcome.FailReason);
            Assert.Empty(seams.Reports);

            var initiate = await puncher.InitiateAsync(Guid.NewGuid(), null, "udp");
            Assert.False(initiate.Ok);
            Assert.Contains("stun_failed", initiate.FailReason);
        }
    }

    [Fact]
    public async Task 访问方_服务端4005_即时失败()
    {
        using var staticKey = EcKeyPair.Generate();
        var seams = new FakeSeams
        {
            RequestError = new ControlErrorException(4005, "target_offline"),
        };
        var puncher = new Puncher(seams.SendRequestAsync, seams.ReportAsync, seams.ProbeAsync, staticKey);
        using (puncher)
        {
            var outcome = await puncher.InitiateAsync(Guid.NewGuid(), null, "udp");
            Assert.False(outcome.Ok);
            Assert.Contains("server_4005", outcome.FailReason); // OQ-18：B 离线立即 failed 不等超时
        }
    }
}

/// <summary>PunchScheduler：串行 FIFO、同对合并、完成事件（成功带端点/失败带原因）。</summary>
public sealed class PunchSchedulerTests
{
    private sealed class FakePuncher : IPuncher
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        public ConcurrentQueue<(Guid Peer, long StartedAt, long EndedAt)> Runs { get; } = [];
        public ConcurrentQueue<Guid> CompletedOrder { get; } = [];
        public ConcurrentQueue<string> Protos { get; } = [];
        public Func<Guid, Task<PunchOutcome>>? Behavior { get; set; }
        public int MaxConcurrency { get; private set; }
        private int _concurrency;

        public async Task<PunchOutcome> InitiateAsync(Guid targetDeviceId, Guid? triggerMappingId, string proto,
            CancellationToken ct = default)
        {
            Protos.Enqueue(proto);
            var entered = Interlocked.Increment(ref _concurrency);
            MaxConcurrency = Math.Max(MaxConcurrency, entered);
            await _gate.WaitAsync(ct);
            try
            {
                Runs.Enqueue((targetDeviceId, Environment.TickCount64, 0));
                var outcome = Behavior is not null
                    ? await Behavior(targetDeviceId)
                    : await Task.Run(async () =>
                    {
                        await Task.Delay(80, ct);
                        return PunchOutcome.Success(Guid.NewGuid(), targetDeviceId, null,
                            new IPEndPoint(IPAddress.Loopback, 1), new IPEndPoint(IPAddress.Loopback, 2));
                    });
                CompletedOrder.Enqueue(targetDeviceId);
                return outcome;
            }
            finally
            {
                _gate.Release();
                Interlocked.Decrement(ref _concurrency);
            }
        }

        public Task<PunchOutcome> RespondAsync(PunchInvite invite, CancellationToken ct = default)
            => throw new NotSupportedException("调度器测试不覆盖被动侧");
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
    public async Task 并发入队_串行执行_FIFO顺序()
    {
        var fake = new FakePuncher();
        await using (var scheduler = new PunchScheduler(fake))
        {
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            var c = Guid.NewGuid();
            scheduler.Enqueue(a);
            scheduler.Enqueue(b);
            scheduler.Enqueue(c);
            Assert.Equal(3, scheduler.QueueDepth);

            await UntilAsync(() => fake.CompletedOrder.Count == 3, "三目标完成");
            Assert.Equal(1, fake.MaxConcurrency); // 不并发执行（OQ-11 串行）
            Assert.Equal([a, b, c], fake.CompletedOrder); // FIFO
            Assert.Equal(0, scheduler.QueueDepth);
            Assert.Null(scheduler.CurrentPeer);
        }
    }

    [Fact]
    public async Task 同对合并_队列与进行中均去重()
    {
        var fake = new FakePuncher();
        await using (var scheduler = new PunchScheduler(fake))
        {
            var target = Guid.NewGuid();
            Assert.True(scheduler.Enqueue(target));
            Assert.False(scheduler.Enqueue(target)); // 已在队列 → 合并
            await UntilAsync(() => scheduler.CurrentPeer == target, "开始打洞");
            Assert.False(scheduler.Enqueue(target)); // 进行中 → 合并
            await UntilAsync(() => fake.CompletedOrder.Count == 1, "完成");
            Assert.Single(fake.Runs); // 仅执行一次
        }
    }

    [Fact]
    public async Task 完成事件_成功带端点_失败带原因()
    {
        var okPeer = Guid.NewGuid();
        var failPeer = Guid.NewGuid();
        var fake = new FakePuncher
        {
            Behavior = peer => Task.FromResult(peer == okPeer
                ? PunchOutcome.Success(Guid.NewGuid(), peer, null,
                    new IPEndPoint(IPAddress.Loopback, 1111), new IPEndPoint(IPAddress.Loopback, 2222))
                : PunchOutcome.Failure(peer, "punch_timeout")),
        };
        await using (var scheduler = new PunchScheduler(fake))
        {
            var outcomes = new ConcurrentQueue<PunchOutcome>();
            scheduler.PunchCompleted += o => outcomes.Enqueue(o);
            scheduler.Enqueue(okPeer);
            scheduler.Enqueue(failPeer);
            await UntilAsync(() => outcomes.Count == 2, "两结果");

            var ok = outcomes.Single(o => o.Ok);
            Assert.Equal(okPeer, ok.PeerDeviceId);
            Assert.Equal(1111, ok.LocalEndpoint!.Port); // 成功事件带端点（任务清单）
            Assert.Equal(2222, ok.PeerEndpoint!.Port);
            var failed = outcomes.Single(o => !o.Ok);
            Assert.Equal(failPeer, failed.PeerDeviceId);
            Assert.Equal("punch_timeout", failed.FailReason); // 超时失败事件
        }
    }

    [Fact]
    public async Task 入队携带proto_出队透传至打洞器()
    {
        var fake = new FakePuncher();
        await using (var scheduler = new PunchScheduler(fake))
        {
            var target = Guid.NewGuid();
            scheduler.Enqueue(target, null, "tcp"); // M2-16：映射 proto 随队列出队透传（TCP 打洞路由）
            await UntilAsync(() => fake.CompletedOrder.Count == 1, "完成");
            Assert.Equal(["tcp"], fake.Protos); // 出队即映射的 proto，非缺省 udp
        }
    }
}

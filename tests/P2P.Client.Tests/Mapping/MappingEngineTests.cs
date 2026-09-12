using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using P2P.Client.Mapping;
using P2P.Client.Punch;
using P2P.Client.Tunnel;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace P2P.Client.Tests;

/// <summary>M1-27 MappingEngine 单测（任务清单：accept→OPEN→DATA 往返、OPEN_FAIL 关闭、
/// 背压暂停读、隧道存活时 enable 直达 direct；虚拟 socket=回环 TCP + 内存隧道对（真实 PTP 握手））。</summary>
public sealed class MappingEngineTests
{
    public MappingEngineTests(ITestOutputHelper output) => _output = output;
    private readonly ITestOutputHelper _output;

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
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

    /// <summary>回环 TCP 服务（echo=true 回写；false 只计收——背压用例防回程流量干扰）。</summary>
    private sealed class LocalServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly List<Task> _sessions = [];
        private readonly bool _echo;
        private long _received;
        private int _connections;

        public LocalServer(bool echo)
        {
            _echo = echo;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = (ushort)((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                        Interlocked.Increment(ref _connections);
                        _sessions.Add(Task.Run(async () =>
                        {
                            using var s = client;
                            // 大读缓冲：单次读完成吸收多个分块，消费吞吐稳超内存隧道生产速率，
                            // 避免 16MiB 突发打满引擎入站有界队列（191 条）触发设计内的"满则断 channel"
                            var buf = new byte[256 * 1024];
                            var stream = s.GetStream();
                            try
                            {
                                int n;
                                while ((n = await stream.ReadAsync(buf, _cts.Token)) > 0)
                                {
                                    Interlocked.Add(ref _received, n);
                                    if (_echo) await stream.WriteAsync(buf.AsMemory(0, n), _cts.Token);
                                }
                            }
                            catch { /* 客户端断开/停机 */ }
                        }));
                    }
                }
                catch { /* 停机 */ }
            });
        }

        public ushort Port { get; }
        public long Received => Volatile.Read(ref _received);
        public int Connections => Volatile.Read(ref _connections);

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            foreach (var t in _sessions.ToArray()) { try { await t; } catch { } }
            _cts.Dispose();
        }
    }

    private sealed class FakePuncher : IPuncher
    {
        public Func<Guid, Task<PunchOutcome>>? Behavior { get; set; }
        public ConcurrentQueue<Guid> Initiated { get; } = [];

        public Task<PunchOutcome> InitiateAsync(Guid targetDeviceId, Guid? triggerMappingId, CancellationToken ct = default)
        {
            Initiated.Enqueue(targetDeviceId);
            return Behavior is not null
                ? Behavior(targetDeviceId)
                : Task.FromResult(PunchOutcome.Failure(targetDeviceId, "no_behavior"));
        }

        public Task<PunchOutcome> RespondAsync(PunchInvite invite, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>闸门传输：包装 A 侧传输——Block 后挂起全部 A→B 发送（模拟隧道对端消费无限慢）。</summary>
    private sealed class GatedTransport(ITunnelTransport inner) : ITunnelTransport
    {
        private TaskCompletionSource _tcs = NewCompleted();

        private static TaskCompletionSource NewCompleted()
        {
            var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            t.TrySetResult();
            return t;
        }

        public void Block() => _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Unblock() => _tcs.TrySetResult();

        public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
        {
            var gate = _tcs.Task;
            if (!gate.IsCompleted) await gate.WaitAsync(ct);
            await inner.SendAsync(frame, ct);
        }

        public ValueTask<byte[]?> ReceiveAsync(CancellationToken ct = default) => inner.ReceiveAsync(ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>测试拓扑：A/B 两侧各 TunnelHost + PunchScheduler(FakePuncher) + MappingEngine（回环虚拟 IP）。</summary>
    private sealed class Topology : IAsyncDisposable
    {
        public TunnelHost HostA = new();
        public TunnelHost HostB = new();
        public PunchScheduler SchedulerA;
        public PunchScheduler SchedulerB;
        public MappingEngine EngineA;
        public MappingEngine EngineB;
        public FakePuncher PuncherA = new();
        public FakePuncher PuncherB = new();
        public Guid PeerA = Guid.NewGuid();
        public Guid PeerB = Guid.NewGuid();
        private readonly List<TunnelSession> _sessions = [];

        public Topology(MappingEngineOptions? options = null)
        {
            SchedulerA = new PunchScheduler(PuncherA);
            SchedulerB = new PunchScheduler(PuncherB);
            EngineA = new MappingEngine(HostA, SchedulerA, IPAddress.Loopback, options);
            EngineB = new MappingEngine(HostB, SchedulerB, IPAddress.Loopback, options);
        }

        /// <summary>再建一条 A↔B 隧道（内存传输对 + 真实 PTP 握手，handler=两侧引擎）。
        /// attach=false 时不挂 TunnelHost 表——供打洞产物用例：挂表只走引擎 OnPunchCompleted 单一路径。
        /// wrapA 包装 A 侧传输（GatedTransport 闸门注入）。</summary>
        public async Task<TunnelSession> EstablishTunnelAsync(bool attach = true,
            Func<ITunnelTransport, ITunnelTransport>? wrapA = null)
        {
            var staticA = EcKeyPair.Generate();
            var staticB = EcKeyPair.Generate();
            var (taRaw, tb) = MemoryTransport.CreatePair();
            var ta = wrapA is not null ? wrapA(taRaw) : taRaw;
            var idle = new TunnelSessionOptions { KeepaliveInterval = TimeSpan.FromHours(1) };
            var connectTask = TunnelSession.ConnectAsync(Guid.NewGuid(), PeerB, staticA,
                staticB.ExportPublicKey(), ta, EngineA, idle);
            var t1 = await tb.ReceiveAsync() ?? throw new IOException("无 THello1");
            var acceptTask = TunnelSession.AcceptAsync(PeerA, t1, staticB,
                staticA.ExportPublicKey(), tb, EngineB, idle);
            var sessionA = await connectTask;
            var sessionB = await acceptTask;
            if (attach)
            {
                HostA.Attach(sessionA);
                HostB.Attach(sessionB);
            }
            _sessions.Add(sessionA);
            _sessions.Add(sessionB);
            return sessionA;
        }

        public async ValueTask DisposeAsync()
        {
            await EngineA.DisposeAsync();
            await EngineB.DisposeAsync();
            await SchedulerA.DisposeAsync();
            await SchedulerB.DisposeAsync();
            await HostA.DisposeAsync();   // 摘表并销毁已挂会话
            await HostB.DisposeAsync();
            foreach (var s in _sessions) { try { await s.DisposeAsync(); } catch { } } // 未挂表会话兜底（重复释放无害）
        }
    }

    [Fact]
    public async Task 隧道存活时enable直达direct_accept_OPEN_DATA往返()
    {
        await using var target = new LocalServer(echo: true);
        await using var topo = new Topology();
        await topo.EstablishTunnelAsync();

        var localPort = (ushort)FreePort();
        var mappingId = Guid.NewGuid();
        await topo.EngineA.EnableAsync(new MappingConfig(mappingId, "m1", localPort, "tcp",
            "self", target.Port, topo.PeerB));

        // 02 §4.5 复用：设备对隧道存活 → 直接 direct，不排队打洞
        var snapshot = topo.EngineA.Snapshots.Single();
        Assert.Equal(MappingState.Direct, snapshot.State);
        Assert.Equal("tunnel_reused", snapshot.Detail);
        Assert.Empty(topo.PuncherA.Initiated);

        // 本地应用连接 → accept → OPEN → OPEN_OK → 双向 splice → echo 回程
        using var app = new TcpClient();
        await app.ConnectAsync(IPAddress.Loopback, localPort);
        var stream = app.GetStream();
        var payload = new byte[4096];
        Random.Shared.NextBytes(payload);
        await stream.WriteAsync(payload);
        var echoBack = new byte[payload.Length];
        var read = 0;
        while (read < payload.Length)
        {
            var n = await stream.ReadAsync(echoBack.AsMemory(read));
            if (n == 0) throw new IOException($"echo 提前 EOF：{read}/{payload.Length}");
            read += n;
        }
        Assert.Equal(payload, echoBack);
        Assert.Equal(1, target.Connections);

        await topo.EngineA.DisableAsync(mappingId);
    }

    [Fact]
    public async Task OPEN_FAIL关闭本地连接()
    {
        var deadPort = (ushort)FreePort(); // 无监听（占用后释放——大概率保持关闭）
        await using var topo = new Topology();
        await topo.EstablishTunnelAsync();

        var localPort = (ushort)FreePort();
        var mappingId = Guid.NewGuid();
        await topo.EngineA.EnableAsync(new MappingConfig(mappingId, "m1", localPort, "tcp",
            "self", deadPort, topo.PeerB));
        Assert.Equal(MappingState.Direct, topo.EngineA.Snapshots.Single().State);

        using var app = new TcpClient();
        await app.ConnectAsync(IPAddress.Loopback, localPort);
        var stream = app.GetStream();
        var closed = false;
        try
        {
            var n = await stream.ReadAsync(new byte[16]).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            closed = n == 0; // OPEN_FAIL → 本地关闭 → EOF
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            closed = true; // 重置式关闭同样表示已断
        }
        Assert.True(closed);
        await topo.EngineA.DisableAsync(mappingId);
    }

    [Fact]
    public async Task 出站背压_闸住隧道暂停读_解除后恢复()
    {
        await using var target = new LocalServer(echo: false); // 只计收不回写，隔离回程流量
        // 内存隧道无网络节流：接收循环速率 2~3 倍于本地 socket 写出速率，默认 256KiB 入站队列
        // 必然积满触发设计内"满则断 channel"（05 §2.3；生产由真实链路速率自然节流不触发）。
        // 放大 backlog 至 32MiB 吸收整个 16MiB 突发——本用例只测"闸住→暂停读→解除→恢复"语义。
        await using var topo = new Topology(new MappingEngineOptions { BacklogBytes = 32 * 1024 * 1024 });

        var localPort = (ushort)FreePort();
        var mappingId = Guid.NewGuid();
        GatedTransport? gated = null;
        var sessionA = await topo.EstablishTunnelAsync(wrapA: t => gated = new GatedTransport(t));
        // 诊断接线（定位断链/断 channel 责任方）
        topo.EngineA.Log += m => _output.WriteLine($"[EngineA] {m}");
        topo.EngineB.Log += m => _output.WriteLine($"[EngineB] {m}");
        sessionA.Log += m => _output.WriteLine($"[SessionA] {m}");
        sessionA.Disconnected += (_, r) => _output.WriteLine($"[SessionA] Disconnected: {r}");
        topo.HostA.SessionDisconnected += (p, r) => _output.WriteLine($"[HostA] peer={p} disconnected: {r}");
        topo.HostB.SessionDisconnected += (p, r) => _output.WriteLine($"[HostB] peer={p} disconnected: {r}");
        await topo.EngineA.EnableAsync(new MappingConfig(mappingId, "m1", localPort, "tcp",
            "self", target.Port, topo.PeerB));

        using var app = new TcpClient();
        app.SendBufferSize = 64 * 1024; // 有界发送缓冲（防窗口自适应吞掉"写未完成"断言）
        await app.ConnectAsync(IPAddress.Loopback, localPort);
        var stream = app.GetStream();

        // OPEN_OK 佐证：目标侧已接受连接 → 闸住 A→B 全部发送
        await UntilAsync(() => target.Connections == 1, "目标侧接受连接（OPEN 已达）");
        gated!.Block();

        // 本地应用持续写 16MiB：引擎暂停读本地 socket（发送被闸 + 256KiB 配额有界）→ 写入受阻
        var total = 16 * 1024 * 1024;
        var writing = Task.Run(async () =>
        {
            var chunk = new byte[16 * 1024];
            for (var i = 0; i < total / chunk.Length; i++)
                await stream.WriteAsync(chunk);
        });

        await Task.Delay(500);
        Assert.False(writing.IsCompleted, "闸住隧道后应用写应受阻（背压暂停读本地 socket）");
        Assert.True(target.Received < total, $"闸住期间目标侧不应收满：{target.Received}/{total}");

        gated.Unblock();
        await writing.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(writing.IsCompletedSuccessfully);
        await UntilAsync(() => target.Received >= total, "解除后目标侧收满", TimeSpan.FromSeconds(30));

        await topo.EngineA.DisableAsync(mappingId);
    }

    [Fact]
    public async Task 打洞Success带null会话_按失败兜底落failed()
    {
        await using var topo = new Topology();
        var localPort = (ushort)FreePort();
        var mappingId = Guid.NewGuid();
        var states = new ConcurrentQueue<MappingState>();
        topo.EngineA.StateChanged += e => states.Enqueue(e.State);

        // Success 但 session=null（无产物会话）→ OnPunchCompleted 要求 Session 非空才 Direct → Failed 兜底
        var ep = new IPEndPoint(IPAddress.Loopback, 1);
        topo.PuncherA.Behavior = _ => Task.FromResult(PunchOutcome.Success(
            Guid.NewGuid(), topo.PeerB, null, ep, ep));

        await topo.EngineA.EnableAsync(new MappingConfig(mappingId, "m1", localPort, "tcp",
            "self", 80, topo.PeerB));
        await UntilAsync(() => topo.PuncherA.Initiated.Count == 1, "打洞出队执行");

        // 瞬态经事件轨迹断言（轮询快照在满载并发下可能错过瞬变——
        // Behavior 同步完成时快照已越过 Punching，轨迹保序仍可证明经过）
        await UntilAsync(() => states.Contains(MappingState.Failed), "null 会话按失败→failed");
        Assert.Contains(MappingState.Punching, states);
        Assert.Equal(MappingState.Failed, topo.EngineA.Snapshots.Single().State);
        Assert.Null(topo.HostA.Get(topo.PeerB)); // 无产物不挂表
    }

    [Fact]
    public async Task 打洞产物真实会话_挂表direct_断链回punching重排后failed()
    {
        await using var topo = new Topology();
        var localPort = (ushort)FreePort();
        var mappingId = Guid.NewGuid();
        var states = new ConcurrentQueue<MappingState>();
        topo.EngineA.StateChanged += e => states.Enqueue(e.State);

        // 第 1 次打洞产出真实会话（attach:false——挂表只走引擎 OnPunchCompleted 单一路径）；
        // 第 2 次（断链重建）失败——M1 无中继回退 → failed
        TunnelSession? punched = null;
        var calls = 0;
        topo.PuncherA.Behavior = async _ =>
        {
            var n = Interlocked.Increment(ref calls);
            if (n == 1)
            {
                punched = await topo.EstablishTunnelAsync(attach: false);
                var ep = new IPEndPoint(IPAddress.Loopback, 1);
                return PunchOutcome.Success(punched.SessionId, topo.PeerB, punched, ep, ep);
            }
            return PunchOutcome.Failure(topo.PeerB, "punch_timeout");
        };

        await topo.EngineA.EnableAsync(new MappingConfig(mappingId, "m1", localPort, "tcp",
            "self", 80, topo.PeerB));
        await UntilAsync(() => states.Contains(MappingState.Direct), "打洞成功→direct");
        Assert.NotNull(topo.HostA.Get(topo.PeerB)); // 会话已由引擎挂表

        // 断链 → 该设备对映射回 punching 并重新排队（02 §4.5 重建=新 sessionId）。
        // 瞬态经事件轨迹断言：满载并发下 punching 可能瞬逝（调度器立即二次出队失败）
        punched!.Close("unit-test");
        await UntilAsync(() => topo.PuncherA.Initiated.Count == 2, "断链重排（第 2 次打洞）");
        await UntilAsync(() => states.Count == 4, "状态轨迹完整");
        Assert.Equal(new[] { MappingState.Punching, MappingState.Direct, MappingState.Punching, MappingState.Failed }, states);
    }

    // ── M1-32：失败手动重试（04 §2.5 /retry 的引擎语义）──────────────

    [Fact]
    public async Task UpdateVirtualIp_新监听绑新地址_与回环同端口占位并存()
    {
        // 向导路径缺陷回归 + A-4 端口隔离引擎级语义（01 §3.2 监听绑虚拟 IP）：引擎以 Loopback
        // 构造（未注册冷启动初值），UpdateVirtualIp 后新启映射监听绑新地址，不占回环同端口；
        // 打洞失败（failed）监听保留——127.0.0.4 上 accept 后因无隧道被关闭，占位 echo 不受影响。
        await using var placeholder = new LocalServer(echo: true);
        var port = placeholder.Port;
        await using var topo = new Topology();
        topo.PuncherA.Behavior = _ => Task.FromResult(PunchOutcome.Failure(topo.PeerB, "punch_timeout"));
        topo.EngineA.UpdateVirtualIp(IPAddress.Parse("127.0.0.4"));

        var mappingId = Guid.NewGuid();
        await topo.EngineA.EnableAsync(new MappingConfig(mappingId, "m1", port, "tcp",
            "self", 80, topo.PeerB));
        await UntilAsync(() => topo.EngineA.Snapshots.Single().State == MappingState.Failed, "打洞失败→failed（监听保留）");

        // 映射监听在 127.0.0.4:port：连接可建立（握手完成）→ 无隧道被引擎关闭
        using var viaMapping = new TcpClient();
        await viaMapping.ConnectAsync(IPAddress.Parse("127.0.0.4"), port);
        var mappingClosed = false;
        try
        {
            var n = await viaMapping.GetStream().ReadAsync(new byte[8]).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            mappingClosed = n == 0; // EOF
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { mappingClosed = true; } // 重置式关闭
        Assert.True(mappingClosed, "映射监听应在 127.0.0.4：accept 后无隧道即关闭");

        // 占位不受影响：127.0.0.1:port 仍是本地服务 echo（A-4 并存互不干扰）
        using var viaLocal = new TcpClient();
        await viaLocal.ConnectAsync(IPAddress.Loopback, port);
        var probe = new byte[] { 1, 2, 3 };
        await viaLocal.GetStream().WriteAsync(probe);
        var back = new byte[probe.Length];
        var read = 0;
        while (read < probe.Length)
        {
            var n = await viaLocal.GetStream().ReadAsync(back.AsMemory(read));
            if (n == 0) throw new IOException("占位 echo 提前 EOF");
            read += n;
        }
        Assert.Equal(probe, back);
        Assert.Equal(1, placeholder.Connections);
    }

    [Fact]
    public async Task 失败重试_failed重排打洞_成功后direct_未知id幂等()
    {
        await using var topo = new Topology();
        var localPort = (ushort)FreePort();
        var mappingId = Guid.NewGuid();
        var states = new ConcurrentQueue<MappingState>();
        topo.EngineA.StateChanged += e => states.Enqueue(e.State);

        // 第 1 次打洞失败；重试后第 2 次产出真实会话 → direct
        var calls = 0;
        topo.PuncherA.Behavior = async _ =>
        {
            var n = Interlocked.Increment(ref calls);
            if (n == 1)
                return PunchOutcome.Failure(topo.PeerB, "punch_timeout");
            var session = await topo.EstablishTunnelAsync(attach: false);
            var ep = new IPEndPoint(IPAddress.Loopback, 1);
            return PunchOutcome.Success(session.SessionId, topo.PeerB, session, ep, ep);
        };

        await topo.EngineA.EnableAsync(new MappingConfig(mappingId, "m1", localPort, "tcp",
            "self", 80, topo.PeerB));
        await UntilAsync(() => states.Contains(MappingState.Failed), "打洞失败→failed");
        Assert.Single(topo.EngineA.Snapshots, s => s.Config.MappingId == mappingId && s.State == MappingState.Failed);

        // 手动重试：failed→punching 重排（监听保持，不重建 Runtime）
        await topo.EngineA.RetryAsync(mappingId);
        await UntilAsync(() => topo.PuncherA.Initiated.Count == 2, "重试重排（第 2 次打洞）");
        await UntilAsync(() => states.Contains(MappingState.Direct), "重试成功→direct");
        Assert.Equal(new[] { MappingState.Punching, MappingState.Failed, MappingState.Punching, MappingState.Direct }, states);

        // 未知/未启用 id：幂等空操作（不抛、不打洞）
        await topo.EngineA.RetryAsync(Guid.NewGuid());
        Assert.Equal(2, topo.PuncherA.Initiated.Count);

        // direct 态重试：无意义空操作（轨迹不再变化）
        await topo.EngineA.RetryAsync(mappingId);
        Assert.Equal(2, topo.PuncherA.Initiated.Count);
    }
}

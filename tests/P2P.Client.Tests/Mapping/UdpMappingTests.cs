using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using P2P.Client.Mapping;
using P2P.Client.Punch;
using P2P.Client.Tunnel;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using Xunit;

namespace P2P.Client.Tests;

/// <summary>M2-20 UDP 映射引擎单测（05 §2.4/FR-C-303/TD-15）：双向数据报往返（含 &gt;1368B
/// FRAG 分片重组）、双向空闲回收（同端点再发包走新 channel）、并发 channel 上限、
/// OPEN_FAIL 段外目标（映射 failed+端点静默丢弃）、停用释放监听可重绑。
/// 拓扑=回环 UDP + 内存隧道对（真实 PTP 握手，Topology 副本与 MappingEngineTests 同构）。</summary>
public sealed class UdpMappingTests
{
    private static int FreePort()
    {
        // 测试专有端口带（24000-28000，避开 Windows 临时端口段 49152+）：FreePort 释放→引擎真实 bind
        // 之间存在竞选窗口，并行测试类的 :0 抓取（OS 临时段）可能抢走该口——跨类串扰曾致监听位失守、
        // 应用客户端连入对方目标。专有带内 :0 分配器不踏足，仅剩带内随机对撞（~1/7000，探测重试兜底）。
        while (true)
        {
            var port = Random.Shared.Next(24000, 28000);
            var l = new TcpListener(IPAddress.Loopback, port);
            try { l.Start(); }
            catch (SocketException) { continue; }
            l.Stop();
            return port;
        }
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

    /// <summary>回环 UDP echo 服务（收什么回什么；记录每包来源端点——服务侧 channel 源端口观察点）。</summary>
    private sealed class UdpEchoServer : IDisposable
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _cts = new();
        private long _received;

        public UdpEchoServer()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        var r = await _udp.ReceiveAsync(_cts.Token);
                        Interlocked.Add(ref _received, r.Buffer.Length);
                        Sources.Enqueue(r.RemoteEndPoint);
                        await _udp.SendAsync(r.Buffer, r.RemoteEndPoint);
                    }
                }
                catch { /* 停机 */ }
            });
        }

        public ushort Port => (ushort)((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        public long Received => Volatile.Read(ref _received);
        public ConcurrentQueue<IPEndPoint> Sources { get; } = [];

        public void Dispose()
        {
            _cts.Cancel();
            _udp.Dispose();
            _cts.Dispose();
        }
    }

    /// <summary>发数据报到映射端口并等回显：首包存在 OPEN 异步处理竞态（UDP_DGRAM 先于
    /// OPEN_OK 到达目标侧被丢，UDP 语义容忍），超时未回则重发模拟应用重传；
    /// 收尾排空滞留的重复回包，避免污染后续 Receive。</summary>
    private static async Task<byte[]> SendAndWaitEchoAsync(UdpClient app, byte[] payload, ushort port,
        int attempts = 5, int waitMs = 600)
    {
        var target = new IPEndPoint(IPAddress.Loopback, port);
        for (var i = 0; ; i++)
        {
            await app.SendAsync(payload, target);
            using var cts = new CancellationTokenSource(waitMs);
            try
            {
                var r = await app.ReceiveAsync(cts.Token);
                var any = new IPEndPoint(IPAddress.Any, 0);
                while (app.Available > 0) _ = app.Receive(ref any); // 排空重试滞留
                return r.Buffer;
            }
            catch (OperationCanceledException) when (i < attempts - 1) { /* 竞态丢包：重发 */ }
        }
    }

    /// <summary>测试拓扑（MappingEngineTests.Topology 副本）：A/B 两侧各 TunnelHost +
    /// PunchScheduler(FakePuncher) + MappingEngine（回环虚拟 IP）。</summary>
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

        public Topology(MappingEngineOptions? options = null,
            Func<IReadOnlyCollection<string>>? cidrsB = null)
        {
            SchedulerA = new PunchScheduler(PuncherA);
            SchedulerB = new PunchScheduler(PuncherB);
            EngineA = new MappingEngine(HostA, SchedulerA, IPAddress.Loopback, options);
            EngineB = new MappingEngine(HostB, SchedulerB, IPAddress.Loopback, options, cidrsB);
        }

        /// <summary>再建一条 A↔B 隧道（内存传输对 + 真实 PTP 握手，handler=两侧引擎）。</summary>
        public async Task<TunnelSession> EstablishTunnelAsync()
        {
            var staticA = EcKeyPair.Generate();
            var staticB = EcKeyPair.Generate();
            var (ta, tb) = MemoryTransport.CreatePair();
            var idle = new TunnelSessionOptions { KeepaliveInterval = TimeSpan.FromHours(1) };
            var connectTask = TunnelSession.ConnectAsync(Guid.NewGuid(), PeerB, staticA,
                staticB.ExportPublicKey(), ta, EngineA, idle);
            var t1 = await tb.ReceiveAsync() ?? throw new IOException("无 THello1");
            var acceptTask = TunnelSession.AcceptAsync(PeerA, t1, staticB,
                staticA.ExportPublicKey(), tb, EngineB, idle);
            var sessionA = await connectTask;
            var sessionB = await acceptTask;
            HostA.Attach(sessionA);
            HostB.Attach(sessionB);
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
            await HostA.DisposeAsync();
            await HostB.DisposeAsync();
            foreach (var s in _sessions) { try { await s.DisposeAsync(); } catch { } }
        }
    }

    private sealed class FakePuncher : IPuncher
    {
        public ConcurrentQueue<Guid> Initiated { get; } = [];

        public Task<PunchOutcome> InitiateAsync(Guid targetDeviceId, Guid? triggerMappingId, string proto,
            CancellationToken ct = default)
        {
            Initiated.Enqueue(targetDeviceId);
            return Task.FromResult(PunchOutcome.Failure(targetDeviceId, "no_behavior"));
        }

        public Task<PunchOutcome> RespondAsync(PunchInvite invite, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private MappingState StateOf(Topology topo, Guid mappingId)
        => topo.EngineA.Snapshots.Single(s => s.Config.MappingId == mappingId).State;

    [Fact]
    public async Task UDP映射_双向数据报往返_超限报文分片重组()
    {
        using var echo = new UdpEchoServer();
        var port = (ushort)FreePort();
        var mappingId = Guid.NewGuid();
        await using var topo = new Topology();
        await topo.EstablishTunnelAsync();
        await topo.EngineA.EnableAsync(new MappingConfig(mappingId, "m-udp", port, "udp", "self", echo.Port, topo.PeerB));
        await UntilAsync(() => StateOf(topo, mappingId) == MappingState.Direct, "隧道复用翻 direct");

        using var app = new UdpClient();
        // 小包：单帧 UDP_DGRAM
        var payload = new byte[200];
        new Random(42).NextBytes(payload);
        Assert.Equal(payload, await SendAndWaitEchoAsync(app, payload, port));

        // 4000B > 1368（单帧明文上限）→ 上行 3 片 FRAG（1352+1352+1296）+ 下行重组同口径
        var big = new byte[4000];
        new Random(43).NextBytes(big);
        Assert.Equal(big, await SendAndWaitEchoAsync(app, big, port));
        Assert.True(echo.Received >= 4200, $"echo 实收 {echo.Received}");

        var traffic = topo.EngineA.TrafficSnapshots().Single(t => t.MappingId == mappingId);
        Assert.True(traffic.BytesUp >= 4200);
        Assert.True(traffic.BytesDown >= 4200);
    }

    [Fact]
    public async Task UDP通道_双向空闲回收_同端点再发包走新通道()
    {
        using var echo = new UdpEchoServer();
        var port = (ushort)FreePort();
        var mappingId = Guid.NewGuid();
        await using var topo = new Topology(new MappingEngineOptions
        {
            UdpIdleTimeout = TimeSpan.FromMilliseconds(400),
            UdpSweepInterval = TimeSpan.FromMilliseconds(100),
        });
        await topo.EstablishTunnelAsync();
        await topo.EngineA.EnableAsync(new MappingConfig(mappingId, "m-udp-idle", port, "udp", "self", echo.Port, topo.PeerB));
        await UntilAsync(() => StateOf(topo, mappingId) == MappingState.Direct, "隧道复用翻 direct");

        using var app = new UdpClient();
        var payload = new byte[64];
        new Random(7).NextBytes(payload);
        Assert.Equal(payload, await SendAndWaitEchoAsync(app, payload, port));
        var firstSourcePort = echo.Sources.Last().Port; // 服务侧 channel 随机源端口

        // 双向无流量超 400ms → 空闲回收（CLOSE+双端表项释放；含 echo 回程后的 LastActive 起点）
        await Task.Delay(1200);

        // 同端点再发包：端点表已摘除 → 建新 channel（服务侧新随机源端口）→ 仍通
        Assert.Equal(payload, await SendAndWaitEchoAsync(app, payload, port));
        var secondSourcePort = echo.Sources.Last().Port;
        Assert.NotEqual(firstSourcePort, secondSourcePort);
    }

    [Fact]
    public async Task UDP通道_并发上限_超限端点静默丢弃()
    {
        using var echo = new UdpEchoServer();
        var port = (ushort)FreePort();
        var mappingId = Guid.NewGuid();
        await using var topo = new Topology(new MappingEngineOptions { UdpMaxChannels = 2 });
        await topo.EstablishTunnelAsync();
        await topo.EngineA.EnableAsync(new MappingConfig(mappingId, "m-udp-cap", port, "udp", "self", echo.Port, topo.PeerB));
        await UntilAsync(() => StateOf(topo, mappingId) == MappingState.Direct, "隧道复用翻 direct");

        var payload = new byte[32];
        // 前两个端点：限额内建 channel，正常回显
        using var c1 = new UdpClient();
        using var c2 = new UdpClient();
        Assert.Equal(payload, await SendAndWaitEchoAsync(c1, payload, port));
        Assert.Equal(payload, await SendAndWaitEchoAsync(c2, payload, port));

        // 第三端点：超 UdpMaxChannels=2 → 建 channel 拒绝 → 静默丢弃（无 OPEN、无回显）
        using var c3 = new UdpClient();
        var target = new IPEndPoint(IPAddress.Loopback, port);
        for (var i = 0; i < 3; i++) // 多发几次排除首包竞态
        {
            await c3.SendAsync(payload, target);
            await Task.Delay(100);
        }
        using var timeout = new CancellationTokenSource(800);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c3.ReceiveAsync(timeout.Token).AsTask());

        // 目标侧只见过 2 个 channel 源端口；映射态不受影响（丢弃非失败）
        Assert.Equal(2, echo.Sources.Select(s => s.Port).Distinct().Count());
        Assert.Equal(MappingState.Direct, StateOf(topo, mappingId));
    }

    [Fact]
    public async Task UDP映射_OPEN_FAIL段外目标_映射failed且端点静默丢弃()
    {
        var port = (ushort)FreePort();
        var mappingId = Guid.NewGuid();
        // cidrsB 缺省 null → 目标侧 enabled 段空集：非 self 一律 fail closed（SEC-52 第二道）
        await using var topo = new Topology();
        await topo.EstablishTunnelAsync();
        await topo.EngineA.EnableAsync(new MappingConfig(mappingId, "m-udp-l3", port, "udp", "10.9.9.9", 53, topo.PeerB));
        await UntilAsync(() => StateOf(topo, mappingId) == MappingState.Direct, "隧道复用翻 direct");

        using var app = new UdpClient();
        var payload = new byte[48];
        new Random(9).NextBytes(payload);
        // 端点首包 → OPEN{udp, 10.9.9.9:53} → 目标侧 l3_not_permitted → OPEN_FAIL
        await app.SendAsync(payload, new IPEndPoint(IPAddress.Loopback, port));
        await UntilAsync(() => StateOf(topo, mappingId) == MappingState.Failed, "OPEN_FAIL 翻 failed");
        var detail = topo.EngineA.Snapshots.Single(s => s.Config.MappingId == mappingId).Detail;
        Assert.Contains("l3_not_permitted", detail);

        // OPEN_FAIL 后该端点后续包静默丢弃（表项保留不重试 OPEN，监听保持）
        await app.SendAsync(payload, new IPEndPoint(IPAddress.Loopback, port));
        using var timeout = new CancellationTokenSource(800);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => app.ReceiveAsync(timeout.Token).AsTask());
        Assert.Equal(MappingState.Failed, StateOf(topo, mappingId));
    }

    [Fact]
    public async Task UDP映射_停用释放监听_同端口可重绑再通()
    {
        using var echo = new UdpEchoServer();
        var port = (ushort)FreePort();
        var mappingId = Guid.NewGuid();
        await using var topo = new Topology();
        await topo.EstablishTunnelAsync();
        await topo.EngineA.EnableAsync(new MappingConfig(mappingId, "m-udp-life", port, "udp", "self", echo.Port, topo.PeerB));
        await UntilAsync(() => StateOf(topo, mappingId) == MappingState.Direct, "隧道复用翻 direct");

        using var app = new UdpClient();
        var payload = new byte[96];
        new Random(11).NextBytes(payload);
        Assert.Equal(payload, await SendAndWaitEchoAsync(app, payload, port));

        // 停用：监听/channel 全释放（映射摘表）
        await topo.EngineA.DisableAsync(mappingId);
        Assert.DoesNotContain(topo.EngineA.Snapshots, s => s.Config.MappingId == mappingId);

        // 同端口重绑成功（UDP 监听已释放；隧道仍存活 → tunnel_reused direct）+ 收发再通
        await topo.EngineA.EnableAsync(new MappingConfig(mappingId, "m-udp-life", port, "udp", "self", echo.Port, topo.PeerB));
        await UntilAsync(() => StateOf(topo, mappingId) == MappingState.Direct, "重绑后复用翻 direct");
        Assert.Equal(payload, await SendAndWaitEchoAsync(app, payload, port));
    }
}

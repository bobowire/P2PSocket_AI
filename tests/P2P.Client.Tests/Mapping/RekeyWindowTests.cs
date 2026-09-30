using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using P2P.Client.Mapping;
using P2P.Client.Punch;
using P2P.Client.Tunnel;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using P2P.Core.Tunnel;
using Xunit;

namespace P2P.Client.Tests;

/// <summary>M2-21 单测（SEC-14/FR-C-502、02 §4.4、05 §2.3）：REKEY TTL 定时轮换跨存量映射数据
/// 连续（序号校验、双端代际观察）；WINDOW 信用背压——会话级确定验证"耗尽挂起/回报恢复"，
/// 引擎级以全双工 1MiB echo 往返证明双向信用闭环（任一方向回报缺失即永久卡死）。</summary>
public sealed class RekeyWindowTests
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

    /// <summary>回环 TCP echo 服务（背压用例共用；大读缓冲单次吸收多个分块）。</summary>
    private sealed class EchoServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        public EchoServer()
        {
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
                        _ = Task.Run(async () =>
                        {
                            using var s = client;
                            var buf = new byte[256 * 1024];
                            var stream = s.GetStream();
                            try
                            {
                                int n;
                                while ((n = await stream.ReadAsync(buf, _cts.Token)) > 0)
                                    await stream.WriteAsync(buf.AsMemory(0, n), _cts.Token);
                            }
                            catch { /* 客户端断开/停机 */ }
                        });
                    }
                }
                catch { /* 停机 */ }
            });
        }

        public ushort Port { get; }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }

    /// <summary>测试拓扑（MappingEngineTests.Topology 副本）：EstablishTunnelAsync 可注入会话选项
    /// （RekeyInterval 测试缝）并暴露两侧会话（代际观察）。</summary>
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
        public TunnelSession? SessionA;
        public TunnelSession? SessionB;
        private readonly List<TunnelSession> _sessions = [];

        public Topology(MappingEngineOptions? options = null,
            Func<IReadOnlyCollection<string>>? cidrsB = null)
        {
            SchedulerA = new PunchScheduler(PuncherA);
            SchedulerB = new PunchScheduler(PuncherB);
            EngineA = new MappingEngine(HostA, SchedulerA, IPAddress.Loopback, options);
            EngineB = new MappingEngine(HostB, SchedulerB, IPAddress.Loopback, options, cidrsB);
        }

        public async Task<TunnelSession> EstablishTunnelAsync(TunnelSessionOptions? sessionOptions = null)
        {
            var staticA = EcKeyPair.Generate();
            var staticB = EcKeyPair.Generate();
            var (ta, tb) = MemoryTransport.CreatePair();
            var idle = sessionOptions ?? new TunnelSessionOptions { KeepaliveInterval = TimeSpan.FromHours(1) };
            var connectTask = TunnelSession.ConnectAsync(Guid.NewGuid(), PeerB, staticA,
                staticB.ExportPublicKey(), ta, EngineA, idle);
            var t1 = await tb.ReceiveAsync() ?? throw new IOException("无 THello1");
            var acceptTask = TunnelSession.AcceptAsync(PeerA, t1, staticB,
                staticA.ExportPublicKey(), tb, EngineB, idle);
            var sessionA = await connectTask;
            var sessionB = await acceptTask;
            HostA.Attach(sessionA);
            HostB.Attach(sessionB);
            SessionA = sessionA;
            SessionB = sessionB;
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

    [Fact]
    public async Task REKEY轮换_跨代际存量映射数据连续_序号校验不丢不乱()
    {
        using var echo = new EchoServer();
        var port = (ushort)FreePort();
        var mappingId = Guid.NewGuid();
        await using var topo = new Topology();
        await topo.EstablishTunnelAsync(new TunnelSessionOptions
        {
            KeepaliveInterval = TimeSpan.FromHours(1),
            RekeyInterval = TimeSpan.FromMilliseconds(250),
        });
        await topo.EngineA.EnableAsync(new MappingConfig(mappingId, "m-rekey", port, "tcp", "self", echo.Port, topo.PeerB));
        await UntilAsync(() => topo.EngineA.Snapshots.Single(s => s.Config.MappingId == mappingId).State
            == MappingState.Direct, "隧道复用翻 direct");

        // 持续序号载荷往返（每轮 4KiB，序号 u64 前缀），跨 ≥2 次密钥轮换（先排水后切换，NET-75）：
        // 任一轮换期在途帧用旧钥封——接收侧密钥环排水窗内可解，数据不断流
        using var app = new TcpClient();
        await app.ConnectAsync(IPAddress.Loopback, port);
        var stream = app.GetStream();
        var send = new byte[4096];
        var recv = new byte[4096];
        var rounds = 0;
        while ((topo.SessionA!.RekeyGeneration < 2 || topo.SessionB!.RekeyGeneration < 2) && rounds < 400)
        {
            BitConverter.GetBytes((long)rounds).CopyTo(send, 0);
            await stream.WriteAsync(send);
            var read = 0;
            while (read < recv.Length)
                read += await stream.ReadAsync(recv.AsMemory(read));
            Assert.Equal(send, recv); // 每轮往返字节级一致（序号校验：丢/乱即不匹配）
            rounds++;
            await Task.Delay(2); // 高频交织数据流与轮换（ACK→切换竞态窗的回归压力）
        }
        Assert.True(topo.SessionA!.RekeyGeneration >= 2, $"发起方代际 {topo.SessionA.RekeyGeneration} < 2");
        Assert.True(topo.SessionB!.RekeyGeneration >= 2, $"响应方代际 {topo.SessionB.RekeyGeneration} < 2");
        Assert.False(topo.SessionA!.IsClosed, "轮换后发起方会话应存活");
        Assert.False(topo.SessionB!.IsClosed, "轮换后响应方会话应存活");
    }

    [Fact]
    public async Task WINDOW信用_耗尽挂起_回报恢复()
    {
        // 会话级确定性验证（引擎级"不消费闸门"在 Windows 回环不可行：内核吸纳 MiB 级、
        // SO_RCVBUF 缩小不生效——停滞幅度不可控）。假 handler 只记账不消化=本地应用不消费：
        // 47 块（47×1368=64296 ≤ 64KiB）后信用耗尽，第 48 块 SendDataAsync 挂起
        // ——调用方 splice 循环阻塞于此=暂停读本地 socket（05 §2.3）；回报到达 → 挂起完成。
        var staticA = EcKeyPair.Generate();
        var staticB = EcKeyPair.Generate();
        var (ta, tb) = MemoryTransport.CreatePair();
        var opts = new TunnelSessionOptions { KeepaliveInterval = TimeSpan.FromHours(1) };
        var peerA = Guid.NewGuid();
        var sinkB = new SinkHandler();
        var connectTask = TunnelSession.ConnectAsync(Guid.NewGuid(), peerA, staticA,
            staticB.ExportPublicKey(), ta, new SinkHandler(), opts);
        var t1 = await tb.ReceiveAsync() ?? throw new IOException("无 THello1");
        var acceptTask = TunnelSession.AcceptAsync(peerA, t1, staticB,
            staticA.ExportPublicKey(), tb, sinkB, opts);
        await using var a = await connectTask;
        await using var b = await acceptTask;

        const int chunk = 1368;                       // 引擎 splice 块大小（ChunkSize 同源）
        const int window = 64 * 1024;                 // CreditWindow 默认信用
        var fullChunks = window / chunk;              // 47：恰好耗尽初始信用
        var buf = new byte[chunk];
        new Random(7).NextBytes(buf);
        for (var i = 0; i < fullChunks; i++)
            await a.SendDataAsync(7, buf);            // 初始信用内全数直通
        await UntilAsync(() => sinkB.ReceivedBytes == fullChunks * chunk, "初始信用 47 块全量到达");

        var pending = a.SendDataAsync(7, buf).AsTask(); // 余 1240 < 1368：挂起
        await Task.Delay(300);
        Assert.False(pending.IsCompleted);            // 无回报即不流动（背压语义）
        Assert.Equal(fullChunks * chunk, sinkB.ReceivedBytes); // B 侧无新增（回报前对端不可能多发）

        await b.SendWindowCreditAsync(7, chunk);      // B 本地消费 1 块 → WINDOW 回报
        await pending.WaitAsync(TimeSpan.FromSeconds(2)); // 信用恢复：挂起完成
        await UntilAsync(() => sinkB.ReceivedBytes == (fullChunks + 1) * chunk, "回报后第 48 块到达");
    }

    [Fact]
    public async Task WINDOW信用_引擎全链路_消费回报全量送达()
    {
        // 引擎级全链路（05 §2.3）：全双工 1MiB echo 往返——两个方向均远超 64KiB 信用窗，
        // 任一方向 WINDOW 回报缺失即永久卡死在信用窗；全量字节一致 = 双向信用闭环成立
        // （echo 即时消费，无内核吸纳依赖，判定确定性不受回环缓冲影响）。
        using var echo = new EchoServer();
        var port = (ushort)FreePort();
        var mappingId = Guid.NewGuid();
        await using var topo = new Topology();
        await topo.EstablishTunnelAsync();
        await topo.EngineA.EnableAsync(new MappingConfig(mappingId, "m-window", port, "tcp", "self", echo.Port, topo.PeerB));
        await UntilAsync(() => topo.EngineA.Snapshots.Single(s => s.Config.MappingId == mappingId).State
            == MappingState.Direct, "隧道复用翻 direct");

        var payload = new byte[1024 * 1024];
        new Random(5).NextBytes(payload);
        using var app = new TcpClient();
        await app.ConnectAsync(IPAddress.Loopback, port);
        var stream = app.GetStream();
        var writeTask = Task.Run(async () => await stream.WriteAsync(payload)); // A→B 方向 1MiB
        var recv = new byte[payload.Length];
        var read = 0;
        while (read < recv.Length) // B→A 方向 1MiB（echo 回程）——双向均须信用回报才可通过
            read += await stream.ReadAsync(recv.AsMemory(read));
        await writeTask.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(payload, recv); // 全量字节一致（往返校验：丢/乱/卡即不匹配）
        Assert.False(topo.SessionA!.IsClosed, "会话应存活");
        Assert.False(topo.SessionB!.IsClosed, "会话应存活");
    }

    /// <summary>会话级桩：OnData 仅记账（模拟本地应用不消费），其余回调空操作。</summary>
    private sealed class SinkHandler : ITunnelChannelHandler
    {
        private long _bytes;
        public long ReceivedBytes => Volatile.Read(ref _bytes);

        public void OnData(TunnelSession session, uint channelId, ReadOnlyMemory<byte> data)
            => Interlocked.Add(ref _bytes, data.Length);
        public void OnOpen(TunnelSession session, uint channelId, OpenPayload open) { }
        public void OnOpenResult(TunnelSession session, uint channelId, OpenResultPayload result) { }
        public void OnUdpDgram(TunnelSession session, uint channelId, ReadOnlyMemory<byte> datagram) { }
        public void OnClose(TunnelSession session, uint channelId) { }
    }
}

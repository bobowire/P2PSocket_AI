using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using P2P.Client.Punch;
using P2P.Client.Tunnel;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using P2P.Core.Stun;
using P2P.Core.Tunnel;
using Xunit;

namespace P2P.Client.Tests;

// ── M2-16 单测（任务清单完成判定：N 条并发目标端口偏移正确、第 1 条沿用 L、胜出取消其余 N−1、
//    N_A≠N_B 必 miss（对称性验证）、N 取 0x70 Ack 回填值；TCP 承载 u16 前缀分帧）──

/// <summary>端口预测纯逻辑（02 §5.2⑥ 扇形：偏移 [0..N−1]，每偏移一条 connect）。</summary>
public sealed class TcpPunchPlanTests
{
    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 2)] // 默认
    [InlineData(4, 3)]
    [InlineData(5, 4)] // 上限
    public void 偏移扇_零起连续_含对端端点原样(int n, int topOffset)
    {
        Assert.Equal(Enumerable.Range(0, n), TcpPunchPlan.Offsets(n));
        var targets = TcpPunchPlan.TargetEndpoints(new IPEndPoint(IPAddress.Loopback, 40000), n);
        Assert.Equal(n, targets.Length);
        Assert.Equal(40000, targets[0].Port);            // 偏移 0=对端端点原样（§5.4 直连路径并入扇形）
        Assert.Equal(40000 + topOffset, targets[^1].Port);
        Assert.All(targets, t => Assert.Equal(IPAddress.Loopback, t.Address));
    }

    [Fact]
    public void 扇形子集性_小N扇被大N扇覆盖_异N混布仍可交叉()
    {
        // 扇形消解旧单点口径的「不对称 N 必 miss」：小 N 的扇是大 N 扇的子集——
        // 混布（两端配置不同 N）时小扇落位仍被大扇覆盖，交叉命中不依赖 N 相等
        var small = TcpPunchPlan.Offsets(3);
        var big = TcpPunchPlan.Offsets(5);
        Assert.All(small, o => Assert.Contains(o, big));
    }
}

/// <summary>TCP 承载分帧（02 §6.2 u16 长度前缀）：往返/有序、关闭/截断/非法前缀、发送侧越界。</summary>
public sealed class TcpFrameTransportTests
{
    private static (Socket A, Socket B) ConnectedPair()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(2);
        var a = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        a.Connect(listener.LocalEndpoint!);
        var b = listener.AcceptSocket();
        listener.Stop();
        return (a, b);
    }

    [Fact]
    public async Task 帧往返_多帧有序()
    {
        var (sa, sb) = ConnectedPair();
        await using var ta = new TcpFrameTransport(sa);
        await using var tb = new TcpFrameTransport(sb);
        var frames = new[]
        {
            PtpFrameCodec.BuildHandshake(PtpFrameType.THello1, new byte[50]),
            PtpFrameCodec.BuildHandshake(PtpFrameType.THello2, new byte[120]),
            PtpFrameCodec.BuildHandshake(PtpFrameType.TConfirm, new byte[32]),
        };
        foreach (var f in frames) await ta.SendAsync(f);
        foreach (var expected in frames)
            Assert.Equal(expected, await tb.ReceiveAsync());
        Assert.NotNull(ta.RemoteEndPoint); // 端点快照（打洞结果上报用）
        Assert.Equal(((IPEndPoint)sa.RemoteEndPoint!).Port, ta.RemoteEndPoint!.Port);
    }

    [Fact]
    public async Task 对端关闭_返回null()
    {
        var (sa, sb) = ConnectedPair();
        await using var ta = new TcpFrameTransport(sa);
        sb.Dispose();
        Assert.Null(await ta.ReceiveAsync());
    }

    [Fact]
    public async Task 中途截断_返回null()
    {
        var (sa, sb) = ConnectedPair();
        await using var ta = new TcpFrameTransport(sa);
        var prefix = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(prefix, 100);
        await sb.SendAsync(prefix, SocketFlags.None);
        await sb.SendAsync(new byte[40], SocketFlags.None); // 声明 100 只到 40
        sb.Dispose();
        Assert.Null(await ta.ReceiveAsync());
    }

    [Fact]
    public async Task 非法前缀_过短与超限_返回null()
    {
        var (sa, sb) = ConnectedPair();
        await using var ta = new TcpFrameTransport(sa);
        var prefix = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(prefix, PtpHeader.WireLen - 1); // 过短（< 帧头 16B）
        await sb.SendAsync(prefix, SocketFlags.None);
        Assert.Null(await ta.ReceiveAsync());
        BinaryPrimitives.WriteUInt16LittleEndian(prefix, TcpFrameTransport.MaxFrame + 1); // 超帧上限
        await sb.SendAsync(prefix, SocketFlags.None);
        Assert.Null(await ta.ReceiveAsync());
    }

    [Fact]
    public async Task 发送侧越界与过短_拒绝()
    {
        var (sa, _) = ConnectedPair();
        await using var ta = new TcpFrameTransport(sa);
        await Assert.ThrowsAsync<ProtocolException>(() =>
            ta.SendAsync(new byte[TcpFrameTransport.MaxFrame + 1]).AsTask());
        await Assert.ThrowsAsync<ProtocolException>(() => ta.SendAsync(new byte[10]).AsTask());
    }
}

/// <summary>TcpPunchFleet：listen(L)+N 并发 connect、第 1 条沿用 L、入池等待首帧、胜出释放其余关闭。</summary>
public sealed class TcpPunchFleetTests
{
    /// <summary>测试专有端口带内取 port..port+span-1 连续空闲段（24000-28000，避开 Windows
    /// 临时端口段 49152+）。曾用 :0 探测——临时端口顺序分配下，并行测试类的 :0 监听常与本类
    /// 探测结果相邻取号，"死端口"假设被打洞 connect 击中对方 echo 服务（FailedConnects 永不到
    /// 位 + 对方 Connections 计数被污染，本地冷启与 CI 同型翻车实录）。带内 :0 分配器不踏足，
    /// 仅剩带内随机对撞（~1/7000，探测重试兜底）。</summary>
    private static int FreePort(int span = 1)
    {
        while (true)
        {
            var basePort = Random.Shared.Next(24000, 28000 - span);
            List<TcpListener>? taken = null;
            try
            {
                for (var p = basePort; p < basePort + span; p++)
                {
                    var l = new TcpListener(IPAddress.Loopback, p);
                    l.Start();
                    (taken ??= []).Add(l);
                }
                return basePort;
            }
            catch (SocketException) { /* 带内对撞：换段重探 */ }
            finally
            {
                if (taken is not null)
                    foreach (var l in taken) l.Stop();
            }
        }
    }

    /// <summary>取一个与 keepClear 各端口（含 ±1 邻位）均不同的空闲端口——端口预测类测试的
    /// connect 目标/监听位须与已知端口保持邻位距离（带内随机对撞由探测重试兜底）。</summary>
    private static int FreePortApart(params int[] keepClear)
    {
        while (true)
        {
            var p = FreePort();
            if (!keepClear.Any(k => Math.Abs(k - p) <= 1)) return p;
        }
    }

    private static byte[] Hello1Wire(int payload = 50) =>
        PtpFrameCodec.BuildHandshake(PtpFrameType.THello1, new byte[payload]);

    private static async Task WriteFramedAsync(Socket s, byte[] frame)
    {
        var wire = new byte[2 + frame.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(wire, (ushort)frame.Length);
        frame.CopyTo(wire, 2);
        await s.SendAsync(wire, SocketFlags.None);
    }

    private static async Task<List<Socket>> AcceptAsync(TcpListener listener, int count, string what)
    {
        var accepted = new List<Socket>();
        var deadline = Environment.TickCount64 + 5000;
        while (accepted.Count < count)
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException($"等待超时：{what}");
            while (listener.Pending() && accepted.Count < count)
                accepted.Add(listener.AcceptSocket());
            await Task.Delay(10);
        }
        return accepted;
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException($"等待超时：{what}");
            await Task.Delay(10);
        }
    }

    /// <summary>轮询等待对端观察到 EOF（连接被关）。</summary>
    private static async Task AssertEofAsync(Socket s)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline)
        {
            if (s.Poll(0, SelectMode.SelectRead) && s.Available == 0) return;
            await Task.Delay(10);
        }
        throw new Xunit.Sdk.XunitException("未见 EOF：连接未关闭");
    }

    [Fact]
    public async Task 并发connect_第1条沿用端口L_全部打向统一目标端口()
    {
        var l = FreePort();
        var targetPort = FreePortApart(l);
        var listener = new TcpListener(IPAddress.Loopback, targetPort);
        listener.Start(8);

        await using var fleet = TcpPunchFleet.Create(IPAddress.Loopback, l,
            [new IPEndPoint(IPAddress.Loopback, targetPort)], null, 3, PtpFrameType.THello1);
        var accepted = await AcceptAsync(listener, 3, "对端接受 3 条 connect");

        Assert.Equal(l, fleet.FirstConnectLocalPort);            // 第 1 条沿用 L（02 §5.2③b）
        Assert.Contains(accepted, s => ((IPEndPoint)s.RemoteEndPoint!).Port == l); // 对端看到源端口 L
        Assert.Equal(0, fleet.FailedConnects);
        await UntilAsync(() => fleet.LiveConnections == 3, "三条连接全部入池"); // 入池异步于对端 accept
        foreach (var s in accepted) s.Dispose();
    }

    [Fact]
    public async Task listen收连接入池_等待首帧命中()
    {
        var l = FreePort();
        var closed = FreePortApart(l); // connect 目标无人监听：全部拒连，仅剩 listen 路径
        await using var fleet = TcpPunchFleet.Create(IPAddress.Loopback, l,
            [new IPEndPoint(IPAddress.Loopback, closed)], null, 2, PtpFrameType.THello1);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, l); // 外部连入端口 L → listen accept 入池
        var wire = Hello1Wire();
        await WriteFramedAsync(client.Client, wire);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var (transport, frame) = await fleet.WaitFrameAsync(cts.Token);
        Assert.Equal(wire, frame); // 首帧命中 awaitedType
        Assert.Equal(((IPEndPoint)client.Client.LocalEndPoint!).Port, transport.RemoteEndPoint!.Port);
        Assert.Single(fleet.Connections);
    }

    [Fact]
    public async Task 胜出释放_其余连接关闭_胜者可用()
    {
        var l = FreePort();
        var targetPort = FreePortApart(l);
        var listener = new TcpListener(IPAddress.Loopback, targetPort);
        listener.Start(8);

        await using var fleet = TcpPunchFleet.Create(IPAddress.Loopback, l,
            [new IPEndPoint(IPAddress.Loopback, targetPort)], null, 3, PtpFrameType.THello1);
        var accepted = await AcceptAsync(listener, 3, "对端接受 3 条 connect");

        // 对端在第 2 条连接上送 THello1 → 该连接胜出
        await WriteFramedAsync(accepted[1], Hello1Wire());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var (winner, _) = await fleet.WaitFrameAsync(cts.Token);
        fleet.Release(winner); // 移出池：销毁时不关
        await fleet.DisposeAsync();

        await winner.SendAsync(Hello1Wire()); // 胜者仍可用（会话承载）
        await AssertEofAsync(accepted[0]);    // 其余 N−1 关闭（02 §5.2④）
        await AssertEofAsync(accepted[2]);
        foreach (var s in accepted) s.Dispose();
    }

    [Fact]
    public async Task 不对称N_目标端口无人监听_等待超时必miss()
    {
        // B 实际落位（N_B=3）：Qbase+2 有监听；A 以 N_A=2 瞄准 Qbase+1——预测基准错位。
        // 三口连续一并探测（qbase=死、qbase+1=死目标、qbase+2=监听位），保证"死端口"前提成立。
        var qbase = FreePort(span: 3);
        var realPort = qbase + 2;
        var listener = new TcpListener(IPAddress.Loopback, realPort);
        listener.Start(4);

        await using var fleet = TcpPunchFleet.Create(IPAddress.Loopback, FreePortApart(qbase, realPort),
            [new IPEndPoint(IPAddress.Loopback, qbase + 1)], null, 2, PtpFrameType.THello1);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fleet.WaitFrameAsync(cts.Token));
        await UntilAsync(() => fleet.FailedConnects >= 2, "两条 connect 均被拒（无监听端口）——波次补射下计数只增不减");
        listener.Stop();
    }

    // ── §5.2⑤ 齐射同步+波次补射（2026-10-24）：开火闸延迟首发、首波 RST 后对端晚开门由后续波命中 ──

    [Fact]
    public async Task 开火闸_闸前不发射_闸开即发首波()
    {
        var l = FreePort();
        var targetPort = FreePortApart(l);
        var listener = new TcpListener(IPAddress.Loopback, targetPort);
        listener.Start(8);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var fleet = TcpPunchFleet.Create(IPAddress.Loopback, l,
            [new IPEndPoint(IPAddress.Loopback, targetPort)], null, 3, PtpFrameType.THello1,
            volleyGate: _ => gate.Task);
        await Task.Delay(300); // 闸未开：零发射、零连接（listen 口已就绪但 connect 未出发）
        Assert.Equal(0, fleet.Waves);
        Assert.Equal(0, fleet.LiveConnections);

        gate.SetResult();
        var accepted = await AcceptAsync(listener, 3, "闸开首波 3 条 connect");
        Assert.Equal(1, fleet.Waves);
        Assert.Equal(l, fleet.FirstConnectLocalPort); // 首波首槽仍沿用端口 L（02 §5.2③b）
        foreach (var s in accepted) s.Dispose();
    }

    [Fact]
    public async Task 波次补射_首波RST后对端晚开门_后续波命中()
    {
        // §5.2③b′：对端监听口晚于首波就绪（B 早打 A 空门竞态的回环投影）——首波 connect 全 RST
        // 阵亡，后续波以全新 socket（全新 SYN 预算）在门开时段补射命中
        var l = FreePort();
        var targetPort = FreePortApart(l);
        await using var fleet = TcpPunchFleet.Create(IPAddress.Loopback, l,
            [new IPEndPoint(IPAddress.Loopback, targetPort)], null, 2, PtpFrameType.THello1,
            waveInterval: TimeSpan.FromMilliseconds(150));
        await UntilAsync(() => fleet.FailedConnects >= 2, "首波 2 条 connect 均被拒（门未开）");

        var listener = new TcpListener(IPAddress.Loopback, targetPort);
        listener.Start(4); // 对端门晚就绪
        var accepted = await AcceptAsync(listener, 1, "后续波补射连入晚开的门");
        await WriteFramedAsync(accepted[0], Hello1Wire());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var (_, frame) = await fleet.WaitFrameAsync(cts.Token);
        Assert.Equal(Hello1Wire(), frame);
        Assert.True(fleet.Waves >= 2, $"后续波已补射（waves={fleet.Waves}）");
        listener.Stop();
    }

    // ── §5.4 直连探测（2026-10-21）：directTargets 入池扇出/拒连重试/DirectRefused 分账/accept 侧扇出 ──

    [Fact]
    public async Task 直连探测_命中入池_onConnection扇出_不污FailedConnects()
    {
        var l = FreePort();
        var deadAim = FreePortApart(l);   // 预测目标死端口：N 条 connect 全拒
        var directPort = FreePortApart(l, deadAim); // 直连目标：真实监听口
        var listener = new TcpListener(IPAddress.Loopback, directPort);
        listener.Start(4);
        var fanned = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fanWire = Hello1Wire(); // 扇出内容=THello1（生产口径）
        var wire2 = PtpFrameCodec.BuildHandshake(PtpFrameType.THello2, new byte[50]);

        await using var fleet = TcpPunchFleet.Create(IPAddress.Loopback, l,
            [new IPEndPoint(IPAddress.Loopback, deadAim)],
            [new IPEndPoint(IPAddress.Loopback, directPort)], 2, PtpFrameType.THello2,
            onConnection: async t =>
            {
                fanned.TrySetResult(t.RemoteEndPoint!); // connect 建立即扇出（§5.4）
                await t.SendAsync(fanWire);
            });
        var accepted = await AcceptAsync(listener, 1, "直连探测连入监听口");

        var fanEp = await fanned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(directPort, fanEp.Port); // 扇出的正是直连连接（对端=真实监听口）
        await using var peerSide = new TcpFrameTransport(accepted[0]);
        Assert.Equal(fanWire, await peerSide.ReceiveAsync()); // 扇出内容即达对端
        await peerSide.SendAsync(wire2); // 对端回 THello2 → 直连连接胜出
        var got = await fleet.WaitFrameAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        Assert.Equal(wire2, got.Frame);
        await UntilAsync(() => fleet.FailedConnects >= 2, "预测 connect 2 条均被拒（死端口）——波次补射下计数只增不减");
        Assert.Equal(0, fleet.DirectRefused);  // 直连一次命中：无拒连计数
    }

    [Fact]
    public async Task 直连探测_首探拒连_监听晚就绪重试仍命中()
    {
        // §5.4 竞态覆盖：对端 fleet/listener 晚于本端直连探测就绪 → 首 SYN 遇 RST → 250ms 步进重试
        var l = FreePort();
        var deadAim = FreePortApart(l);
        var directPort = FreePortApart(l, deadAim);

        await using var fleet = TcpPunchFleet.Create(IPAddress.Loopback, l,
            [new IPEndPoint(IPAddress.Loopback, deadAim)],
            [new IPEndPoint(IPAddress.Loopback, directPort)], 1, PtpFrameType.THello1);
        await UntilAsync(() => fleet.DirectRefused >= 1, "首探测拒连（RST）计数"); // 此刻监听口尚无人听

        var listener = new TcpListener(IPAddress.Loopback, directPort);
        listener.Start(4);
        var accepted = await AcceptAsync(listener, 1, "重试后直连探测连入");
        await WriteFramedAsync(accepted[0], Hello1Wire());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var (_, frame) = await fleet.WaitFrameAsync(cts.Token);
        Assert.Equal(Hello1Wire(), frame);
        await UntilAsync(() => fleet.FailedConnects >= 1, "预测 connect 1 条被拒（分账）——波次补射下计数只增不减");
        Assert.True(fleet.DirectRefused >= 1); // 拒连计数保留（重试史）——不污预测分账
        listener.Stop();
    }

    [Fact]
    public async Task listen收连接_accept侧亦即时扇出onConnection()
    {
        // §5.4：onConnection 移入 AddConnection 统一触发——accept 路径连入也即时发首帧
        // （对端公网/同网段可达时 B 连入即握手，不依赖周期补发窗）
        var l = FreePort();
        var closed = FreePortApart(l);
        var wire = PtpFrameCodec.BuildHandshake(PtpFrameType.THello2, new byte[50]);

        await using var fleet = TcpPunchFleet.Create(IPAddress.Loopback, l,
            [new IPEndPoint(IPAddress.Loopback, closed)], null, 2, PtpFrameType.THello1,
            onConnection: t => t.SendAsync(wire));

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, l); // 外部连入端口 L → accept 入池即扇出
        await using var peerSide = new TcpFrameTransport(client.Client);
        var got = await peerSide.ReceiveAsync();
        Assert.Equal(wire, got); // 连入侧无需等待即收到首帧（旧实现仅 connect 路径触发）
    }
}

/// <summary>Puncher TCP 路径：两段式全链路（N=1 回环 simultaneous open）、N 取 Ack 回填值、超时/探测失败。</summary>
public sealed class PuncherTcpTests
{
    /// <summary>TCP 控制面假缝：探测=回环恒等 NAT（MappedPortShift 模拟端口平移）、0x70/0x76 可编排。</summary>
    private sealed class FakeTcpSeams
    {
        public TaskCompletionSource<EndpointPair> Probed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<EndpointPair> Reported { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<(Guid SessionId, EndpointPair Endpoints)> Reports { get; } = [];
        public ConcurrentQueue<Endpoint[]?> ReportedCandidates { get; } = []; // §5.4 B 侧 0x76 候选
        public Func<Guid, Guid?, string, EndpointPair, Endpoint[]?, uint?, CancellationToken, Task<PunchRequestAck>>? RequestHandler;

        /// <summary>0x78 起跑假缝（§5.2⑥）：null=未装（晚探测流程跳过）。</summary>
        public Func<Guid, Guid?, string, CancellationToken, Task<PunchStartGo>>? StartHandler;

        /// <summary>旧 0x70 流程被触达标记（降级路径断言用）。</summary>
        public bool LegacyRequested;

        /// <summary>探测上报的映射端口 = 本地端口 + 平移量（0=恒等；模拟 SymmetricSequential 下一分配）。</summary>
        public int MappedPortShift;

        /// <summary>默认 Ack 的对端公钥（StartInitiator 校验 65B 未压缩 P-256）。</summary>
        private readonly byte[] _peerPub = EcKeyPair.Generate().ExportPublicKey();

        public string? SeenProto { get; private set; }
        public EndpointPair? SeenRequesterEndpoints { get; private set; }
        public Endpoint[]? SeenCandidates { get; private set; } // §5.4 A 侧 0x70 候选

        public Task<IPEndPoint> TcpProbeAsync(Socket socket, CancellationToken ct)
        {
            var ep = new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)socket.LocalEndPoint!).Port); // 恒等 NAT：地址侧归一回环（LocalEndPoint 是 0.0.0.0——连 0.0.0.0 黑洞）
            var mapped = new IPEndPoint(IPAddress.Loopback, ep.Port + MappedPortShift);
            socket.Dispose(); // M2-04 契约：探测事务即关，端口 L 由调用方记录复用
            Probed.TrySetResult(new EndpointPair(null,
                new P2P.Core.Protocol.Endpoint(mapped.Address.ToString(), (ushort)mapped.Port)));
            return Task.FromResult(mapped);
        }

        public Task ReportAsync(Guid sessionId, EndpointPair endpoints, Endpoint[]? localCandidates, uint? bearerCaps,
            CancellationToken ct)
        {
            Reports.Enqueue((sessionId, endpoints));
            ReportedCandidates.Enqueue(localCandidates);
            Reported.TrySetResult(endpoints);
            return Task.CompletedTask;
        }

        public Task<PunchRequestAck> SendRequestAsync(Guid target, Guid? trigger, string proto,
            EndpointPair requesterEndpoints, Endpoint[]? localCandidates, uint? bearerCaps, CancellationToken ct)
        {
            SeenProto = proto;
            SeenRequesterEndpoints = requesterEndpoints;
            SeenCandidates = localCandidates;
            LegacyRequested = true;
            if (RequestHandler is not null)
                return RequestHandler(target, trigger, proto, requesterEndpoints, localCandidates, bearerCaps, ct);
            return Task.FromResult(new PunchRequestAck(0, 0, MsgType.PunchRequest, Guid.NewGuid(),
                new PeerInfo(target, "?", "000000", _peerPub),
                new EndpointPair(null, new P2P.Core.Protocol.Endpoint("127.0.0.1", (ushort)ClosedPort())), 3, false));
        }

        /// <summary>带内探测空闲口（24000-28000）：ack 端点随后的 TCP connect 须真的拒连，
        /// :0 临时段顺序分配易与并行类监听相邻相撞（跨类串扰家族，同 FreePort 迁带缘由）。</summary>
        private static int ClosedPort()
        {
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
    }

    private static Task<IPEndPoint> UdpProbeUnused(Socket socket, CancellationToken ct)
        => Task.FromResult(new IPEndPoint(IPAddress.Loopback, 1)); // TCP 链路不触及 UDP 探测

    private static (Puncher Puncher, FakeTcpSeams Seams) Make(EcKeyPair staticKey,
        int mappedPortShift = 0, PunchOptions? options = null,
        Func<IPAddress?, IReadOnlyList<IPAddress>>? localIps = null,
        Puncher.PunchFireWaiter? punchFireWaiter = null,
        Puncher.PunchStartSender? punchStartSender = null,
        Puncher.StunTcpExchangeDelegate? tcpExchange = null,
        Action<string>? log = null)
    {
        var seams = new FakeTcpSeams { MappedPortShift = mappedPortShift };
        if (punchStartSender is not null) seams.StartHandler = punchStartSender.Invoke;
        return (new Puncher(
            seams.SendRequestAsync, seams.ReportAsync, UdpProbeUnused, staticKey,
            options: options, tcpProbe: seams.TcpProbeAsync, localIpEnumerator: localIps,
            log: log,
            punchFireWaiter: punchFireWaiter,
            punchStartSender: seams.StartHandler is null ? null : (t, tr, p, ct) => seams.StartHandler(t, tr, p, ct),
            tcpExchange: tcpExchange), seams);
    }

    /// <summary>回环枚举缝（§5.4 用例）：候选=127.0.0.1×监听口——回环 fabric 即「同网段/公网可达」。</summary>
    private static readonly Func<IPAddress?, IReadOnlyList<IPAddress>> LoopbackIps =
        static _ => [IPAddress.Loopback];

    private static Task<IPEndPoint> BrokenTcpProbe(Socket socket, CancellationToken ct)
        => Task.FromException<IPEndPoint>(new IOException("stun-tcp down"));

    [Fact]
    public async Task TCP两段式全链路_N1_双方握手成功_可互通()
    {
        using var staticA = EcKeyPair.Generate();
        using var staticB = EcKeyPair.Generate();
        var deviceA = Guid.NewGuid();
        var deviceB = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var (puncherA, seamsA) = Make(staticA);
        var (puncherB, seamsB) = Make(staticB);
        using (puncherA)
        using (puncherB)
        {
            // A 的 0x70 假缝=模拟服务端：等 B 的 0x76 端点上报后回延后 Ack（N=1 → 目标=对端映射端口+0）
            seamsA.RequestHandler = async (target, trigger, proto, endpoints, candidates, bearerCaps, ct) =>
            {
                var bReported = await seamsB.Reported.Task;
                return new PunchRequestAck(0, 0, MsgType.PunchRequest, sessionId,
                    new PeerInfo(deviceB, "B", "b00000", staticB.ExportPublicKey()),
                    new EndpointPair(null, bReported.Tcp), PunchCount: 1, RelayAllowed: false);
            };

            var outcomeATask = puncherA.InitiateAsync(deviceB, null, "tcp");

            // B 收邀请（A 端点须 A 已探测）：tcp 槽位在 → TCP 打洞路径
            var aEp = await seamsA.Probed.Task;
            var invite = new PunchInvite(0, 0, MsgType.PunchInvite, sessionId,
                new PeerInfo(deviceA, "A", "a00000", staticA.ExportPublicKey()),
                new EndpointPair(null, aEp.Tcp), PunchCount: 1, false);
            var outcomeB = await puncherB.RespondAsync(invite);
            var outcomeA = await outcomeATask;

            // 双方成功、会话配对、胜出连接四元组交叉一致
            Assert.True(outcomeA.Ok, outcomeA.FailReason);
            Assert.True(outcomeB.Ok, outcomeB.FailReason);
            Assert.Equal(sessionId, outcomeA.SessionId);
            Assert.Equal(sessionId, outcomeB.SessionId);
            Assert.Equal(deviceB, outcomeA.Session!.PeerDeviceId);
            Assert.Equal(deviceA, outcomeB.Session!.PeerDeviceId);
            Assert.Equal(outcomeA.LocalEndpoint!.Port, outcomeB.PeerEndpoint!.Port);
            Assert.Equal(outcomeB.LocalEndpoint!.Port, outcomeA.PeerEndpoint!.Port);

            // 第一段上送：proto=tcp、端点在 tcp 槽位（udp 空）
            Assert.Equal("tcp", seamsA.SeenProto);
            Assert.NotNull(seamsA.SeenRequesterEndpoints!.Tcp);
            Assert.Null(seamsA.SeenRequesterEndpoints.Udp);

            // B 的 0x76：sessionId 正确、tcp 端点（udp 空）
            var (reportedSession, reportedPair) = seamsB.Reports.Single();
            Assert.Equal(sessionId, reportedSession);
            Assert.NotNull(reportedPair.Tcp);
            Assert.Null(reportedPair.Udp);
            Assert.Equal(outcomeB.LocalEndpoint!.Port, reportedPair.Tcp!.Port);

            // 会话互通（PING/PONG 内嵌于 TunnelSession）
            var rtt = await outcomeA.Session!.PingAsync();
            Assert.True(rtt >= TimeSpan.Zero);
            await outcomeA.Session.DisposeAsync();
            await outcomeB.Session!.DisposeAsync();
        }
    }

    // ── §5.2⑤ 齐射锚点（2026-10-24）：B 侧开火闸走 0x77 锚点倒计时 / 超时兜底立即开火 ──

    [Fact]
    public async Task B侧锚点闸_收到PunchFire倒计时开火_全链路成交()
    {
        // A 以 Ack 到达为锚、B 以 0x77（缝注入即时 true）为锚，各倒数同一 Δ（PunchPolicy.
        // TcpVolleyCountdown）到点齐射——回环下两波重合即成交；锚点缝以 invite sessionId 调用
        using var staticA = EcKeyPair.Generate();
        using var staticB = EcKeyPair.Generate();
        var deviceA = Guid.NewGuid();
        var deviceB = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        Guid? seenFireSession = null;

        var options = new PunchOptions { TcpWaveInterval = TimeSpan.FromMilliseconds(150) };
        var (puncherA, seamsA) = Make(staticA, options: options);
        var (puncherB, seamsB) = Make(staticB, options: options,
            punchFireWaiter: (sid, _, _) => { seenFireSession = sid; return Task.FromResult(true); });
        using (puncherA)
        using (puncherB)
        {
            seamsA.RequestHandler = async (target, trigger, proto, endpoints, candidates, bearerCaps, ct) =>
            {
                var bReported = await seamsB.Reported.Task;
                return new PunchRequestAck(0, 0, MsgType.PunchRequest, sessionId,
                    new PeerInfo(deviceB, "B", "b00000", staticB.ExportPublicKey()),
                    new EndpointPair(null, bReported.Tcp), PunchCount: 1, RelayAllowed: false);
            };

            var outcomeATask = puncherA.InitiateAsync(deviceB, null, "tcp");
            var aEp = await seamsA.Probed.Task;
            var invite = new PunchInvite(0, 0, MsgType.PunchInvite, sessionId,
                new PeerInfo(deviceA, "A", "a00000", staticA.ExportPublicKey()),
                new EndpointPair(null, aEp.Tcp), PunchCount: 1, false);
            var outcomeB = await puncherB.RespondAsync(invite);
            var outcomeA = await outcomeATask;

            Assert.True(outcomeA.Ok, outcomeA.FailReason);
            Assert.True(outcomeB.Ok, outcomeB.FailReason);
            Assert.Equal(sessionId, seenFireSession); // 锚点等待以 invite 会话为键
            await outcomeA.Session!.DisposeAsync();
            await outcomeB.Session!.DisposeAsync();
        }
    }

    [Fact]
    public async Task B侧锚点闸_锚点超时兜底立即开火_全链路仍成交()
    {
        // 混布降级：旧服务端不推 0x77 → 等待器超时返 false → B 立即开火（旧行为）；
        // 与 A 倒计时的残余时差由波次补射覆盖（TcpWaveInterval=150ms）
        using var staticA = EcKeyPair.Generate();
        using var staticB = EcKeyPair.Generate();
        var deviceA = Guid.NewGuid();
        var deviceB = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var options = new PunchOptions
        {
            TcpWaveInterval = TimeSpan.FromMilliseconds(150),
            PunchFireFallbackTimeout = TimeSpan.FromMilliseconds(100),
        };
        var (puncherA, seamsA) = Make(staticA, options: options);
        var (puncherB, seamsB) = Make(staticB, options: options,
            punchFireWaiter: async (_, timeout, ct) => { await Task.Delay(timeout, ct); return false; });
        using (puncherA)
        using (puncherB)
        {
            seamsA.RequestHandler = async (target, trigger, proto, endpoints, candidates, bearerCaps, ct) =>
            {
                var bReported = await seamsB.Reported.Task;
                return new PunchRequestAck(0, 0, MsgType.PunchRequest, sessionId,
                    new PeerInfo(deviceB, "B", "b00000", staticB.ExportPublicKey()),
                    new EndpointPair(null, bReported.Tcp), PunchCount: 1, RelayAllowed: false);
            };

            var outcomeATask = puncherA.InitiateAsync(deviceB, null, "tcp");
            var aEp = await seamsA.Probed.Task;
            var invite = new PunchInvite(0, 0, MsgType.PunchInvite, sessionId,
                new PeerInfo(deviceA, "A", "a00000", staticA.ExportPublicKey()),
                new EndpointPair(null, aEp.Tcp), PunchCount: 1, false);
            var outcomeB = await puncherB.RespondAsync(invite);
            var outcomeA = await outcomeATask;

            Assert.True(outcomeA.Ok, outcomeA.FailReason);
            Assert.True(outcomeB.Ok, outcomeB.FailReason);
            await outcomeA.Session!.DisposeAsync();
            await outcomeB.Session!.DisposeAsync();
        }
    }

    [Fact]
    public async Task 目标端口取Ack回填N_N2经平移NAT命中()
    {
        using var staticA = EcKeyPair.Generate();
        using var staticB = EcKeyPair.Generate();
        var deviceA = Guid.NewGuid();
        var deviceB = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var (puncherA, seamsA) = Make(staticA);                 // A 恒等 NAT
        var (puncherB, seamsB) = Make(staticB, mappedPortShift: -1); // B 的映射=L−1（下一分配≈L）
        using (puncherA)
        using (puncherB)
        {
            // Ack 回填 N=2：A 须瞄 B 映射端口+1=B 实际 listen 端口才可能命中——
            // 若 A 未用回填 N（如本地缺省 3）则瞄 +2 → 必 miss → 超时失败
            seamsA.RequestHandler = async (target, trigger, proto, endpoints, candidates, bearerCaps, ct) =>
            {
                var bReported = await seamsB.Reported.Task;
                return new PunchRequestAck(0, 0, MsgType.PunchRequest, sessionId,
                    new PeerInfo(deviceB, "B", "b00000", staticB.ExportPublicKey()),
                    new EndpointPair(null, bReported.Tcp), PunchCount: 2, RelayAllowed: false);
            };

            var outcomeATask = puncherA.InitiateAsync(deviceB, null, "tcp");
            var aEp = await seamsA.Probed.Task;
            var invite = new PunchInvite(0, 0, MsgType.PunchInvite, sessionId,
                new PeerInfo(deviceA, "A", "a00000", staticA.ExportPublicKey()),
                new EndpointPair(null, aEp.Tcp), PunchCount: 2, false);
            var outcomeB = await puncherB.RespondAsync(invite);
            var outcomeA = await outcomeATask;

            Assert.True(outcomeA.Ok, outcomeA.FailReason); // 命中即证明 A 以 Ack 回填 N=2 计算 +1 偏移
            Assert.True(outcomeB.Ok, outcomeB.FailReason);
            await outcomeA.Session!.DisposeAsync();
            await outcomeB.Session!.DisposeAsync();
        }
    }

    [Fact]
    public async Task 目标端口无监听_超时失败()
    {
        using var staticKey = EcKeyPair.Generate();
        var (puncher, seams) = Make(staticKey,
            options: new PunchOptions { PunchTimeout = TimeSpan.FromMilliseconds(1200) });
        using (puncher)
        {
            // 默认假缝回 Ack：对端 tcp 端点=已释放端口，N=3 → +2 偏移处无人监听
            var outcome = await puncher.InitiateAsync(Guid.NewGuid(), null, "tcp");
            Assert.False(outcome.Ok);
            Assert.Equal("punch_timeout", outcome.FailReason);
            Assert.Null(seams.SeenRequesterEndpoints!.Udp); // 端点只在 tcp 槽位
        }
    }

    [Fact]
    public async Task 探测失败_双侧即败_不上报()
    {
        using var staticKey = EcKeyPair.Generate();
        var seams = new FakeTcpSeams();
        var puncher = new Puncher(seams.SendRequestAsync, seams.ReportAsync, UdpProbeUnused, staticKey,
            tcpProbe: BrokenTcpProbe);
        using (puncher)
        {
            var invite = new PunchInvite(0, 0, MsgType.PunchInvite, Guid.NewGuid(),
                new PeerInfo(Guid.NewGuid(), "A", "a00000", staticKey.ExportPublicKey()),
                new EndpointPair(null, new P2P.Core.Protocol.Endpoint("127.0.0.1", 40000)), 3, false);
            var outcome = await puncher.RespondAsync(invite);
            Assert.False(outcome.Ok);
            Assert.Contains("stun_failed", outcome.FailReason);
            Assert.Empty(seams.Reports); // 探测失败不进 0x76

            var initiate = await puncher.InitiateAsync(Guid.NewGuid(), null, "tcp");
            Assert.False(initiate.Ok);
            Assert.Contains("stun_failed", initiate.FailReason);
        }
    }

    [Fact]
    public async Task 未知proto_直接拒绝()
    {
        using var staticKey = EcKeyPair.Generate();
        var (puncher, _) = Make(staticKey);
        using (puncher)
        {
            var outcome = await puncher.InitiateAsync(Guid.NewGuid(), null, "sctp");
            Assert.False(outcome.Ok);
            Assert.Contains("proto_not_supported", outcome.FailReason);
        }
    }

    // ── §5.2⑥ 晚探测起跑（2026-10-25）：0x78→交换→扇形 全链路 / 旧服务端降级 ──

    /// <summary>交换假替身（镜像 TcpStunStub 交换语义）：恒等 NAT（mapped=本地端口），
    /// 双端注册到齐同刻互回；socket 保持打开（不关——映射钉活语义）。</summary>
    private sealed class LateExchangeHub
    {
        private readonly object _gate = new();
        private readonly Dictionary<Guid, List<(IPEndPoint Ep, IPEndPoint[] Cands,
            TaskCompletionSource<StunTcpProber.PunchExchange> Tcs)>> _sessions = new();

        public Task<StunTcpProber.PunchExchange> RegisterAsync(Socket socket, Guid sessionId,
            IReadOnlyList<IPEndPoint>? candidates, CancellationToken ct)
        {
            var ep = new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)socket.LocalEndPoint!).Port); // 恒等 NAT：地址归一回环（LocalEndPoint=0.0.0.0 连它会黑洞）
            var cands = candidates?.ToArray() ?? [];
            var tcs = new TaskCompletionSource<StunTcpProber.PunchExchange>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                var list = _sessions.TryGetValue(sessionId, out var e) ? e : _sessions[sessionId] = [];
                list.Add((ep, cands, tcs));
                if (list.Count >= 2)
                {
                    list[0].Tcs.TrySetResult(new StunTcpProber.PunchExchange(list[0].Ep, list[1].Ep, list[1].Cands));
                    list[1].Tcs.TrySetResult(new StunTcpProber.PunchExchange(list[1].Ep, list[0].Ep, list[0].Cands));
                    _sessions.Remove(sessionId);
                }
            }
            return tcs.Task;
        }
    }

    [Fact]
    public async Task 晚探测起跑_交换锚点扇形齐射_全链路成交()
    {
        // 0x78 起跑（双推 Go）→ 双端并行 STUN 交换（连接保活、恒等映射）→ 应答即锚点扇形齐射
        //（N=3，偏移 0=对端 listen 口）→ THello 握手成交；旧 0x70 流程全程不被触达
        using var staticA = EcKeyPair.Generate();
        using var staticB = EcKeyPair.Generate();
        var deviceA = Guid.NewGuid();
        var deviceB = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var hub = new LateExchangeHub();
        var options = new PunchOptions { TcpWaveInterval = TimeSpan.FromMilliseconds(150) };
        var logsA = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var logsB = new System.Collections.Concurrent.ConcurrentQueue<string>();

        var (puncherA, seamsA) = Make(staticA, options: options,
            punchStartSender: (_, _, _, _) => Task.FromResult(new PunchStartGo(0, 0, MsgType.PunchStart,
                sessionId, new PeerInfo(deviceB, "B", "b00000", staticB.ExportPublicKey()), 3, false)),
            tcpExchange: hub.RegisterAsync, log: m => logsA.Enqueue(m));
        var (puncherB, seamsB) = Make(staticB, options: options,
            tcpExchange: hub.RegisterAsync, log: m => logsB.Enqueue(m));
        using (puncherA)
        using (puncherB)
        {
            var goB = new PunchStartGo(0, 0, MsgType.PunchStart, sessionId,
                new PeerInfo(deviceA, "A", "a00000", staticA.ExportPublicKey()), 3, false);
            var outcomeBTask = puncherB.RespondGoAsync(goB);
            var outcomeA = await puncherA.InitiateAsync(deviceB, null, "tcp");
            var outcomeB = await outcomeBTask;

            Assert.True(outcomeA.Ok, $"{outcomeA.FailReason}；A日志={string.Join(" | ", logsA)}");
            Assert.True(outcomeB.Ok, $"{outcomeB.FailReason}；B日志={string.Join(" | ", logsB)}");
            Assert.Equal(sessionId, outcomeA.SessionId);
            Assert.Equal(sessionId, outcomeB.SessionId);
            Assert.False(seamsA.LegacyRequested); // 0x70 全程未被触达（晚探测独立成交）
            Assert.Empty(seamsB.Reports);         // 0x76 亦不发生
            await outcomeA.Session!.DisposeAsync();
            await outcomeB.Session!.DisposeAsync();
        }
    }

    [Fact]
    public async Task 旧服务端静默丢弃0x78_超时自动降级旧070流程成交()
    {
        // 旧服务端对未知 msgType 静默丢弃（02 §7）→ PunchStartReplyTimeout 超时 → 编排器降级
        // 旧流程全窗重打（混布自由升级序）；断言两条路径先后都被触达且成交
        using var staticA = EcKeyPair.Generate();
        using var staticB = EcKeyPair.Generate();
        var deviceA = Guid.NewGuid();
        var deviceB = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var options = new PunchOptions
        {
            PunchStartReplyTimeout = TimeSpan.FromMilliseconds(200),
            TcpWaveInterval = TimeSpan.FromMilliseconds(150),
        };
        var (puncherA, seamsA) = Make(staticA, options: options,
            punchStartSender: async (_, _, _, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct); // 旧服务端：永不应答（静默丢弃）
                return null!;
            },
            tcpExchange: new LateExchangeHub().RegisterAsync);
        var (puncherB, seamsB) = Make(staticB, options: options);
        using (puncherA)
        using (puncherB)
        {
            seamsA.RequestHandler = async (target, trigger, proto, endpoints, candidates, bearerCaps, ct) =>
            {
                var bReported = await seamsB.Reported.Task; // 等价服务端延后 Ack（B 0x76 就绪）
                return new PunchRequestAck(0, 0, MsgType.PunchRequest, sessionId,
                    new PeerInfo(deviceB, "B", "b00000", staticB.ExportPublicKey()),
                    new EndpointPair(null, bReported.Tcp), PunchCount: 1, RelayAllowed: false);
            };

            var outcomeATask = puncherA.InitiateAsync(deviceB, null, "tcp");
            var aEp = await seamsA.Probed.Task;
            var invite = new PunchInvite(0, 0, MsgType.PunchInvite, sessionId,
                new PeerInfo(deviceA, "A", "a00000", staticA.ExportPublicKey()),
                new EndpointPair(null, aEp.Tcp), PunchCount: 1, false);
            var outcomeB = await puncherB.RespondAsync(invite);
            var outcomeA = await outcomeATask;

            Assert.True(outcomeA.Ok, outcomeA.FailReason); // 降级路径成交
            Assert.True(outcomeB.Ok, outcomeB.FailReason);
            Assert.True(seamsA.LegacyRequested);           // 0x70 确实被触达（降级发生）
            await outcomeA.Session!.DisposeAsync();
            await outcomeB.Session!.DisposeAsync();
        }
    }

    // ── §5.4 直连优先（2026-10-21）：恒等映射（单侧公网样）与候选直达（映射不可达样）──

    [Fact]
    public async Task 对端公网样_N3_恒等映射_直连偏移0命中()
    {
        // §5.4 红绿证据：双侧恒等映射（公网 IP：映射口=监听口）+N=3——旧实现双方瞄映射口+2 →
        // 双侧必 miss 超时；新实现直连探测瞄映射端点原样（偏移 0=真实监听口）→ 即刻握手。
        // 恒等映射下 B 候选=映射端点，去重后 directTargets=[偏移 0]；B 侧收连接即应答（公网端样）。
        // 对端无候选（旧端混布）→ 直连探测整体跳过、退化为纯映射路径（BuildDirectTargets 门控）。
        using var staticA = EcKeyPair.Generate();
        using var staticB = EcKeyPair.Generate();
        var deviceA = Guid.NewGuid();
        var deviceB = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var (puncherA, seamsA) = Make(staticA, localIps: LoopbackIps);
        var (puncherB, seamsB) = Make(staticB, localIps: LoopbackIps);
        using (puncherA)
        using (puncherB)
        {
            seamsA.RequestHandler = async (target, trigger, proto, endpoints, candidates, bearerCaps, ct) =>
            {
                var bReported = await seamsB.Reported.Task;
                return new PunchRequestAck(0, 0, MsgType.PunchRequest, sessionId,
                    new PeerInfo(deviceB, "B", "b00000", staticB.ExportPublicKey()),
                    new EndpointPair(null, bReported.Tcp), PunchCount: 3, RelayAllowed: false,
                    PeerLocalCandidates: seamsB.ReportedCandidates.Single()); // B 候选透传给 A
            };

            var outcomeATask = puncherA.InitiateAsync(deviceB, null, "tcp");
            var aEp = await seamsA.Probed.Task;
            var invite = new PunchInvite(0, 0, MsgType.PunchInvite, sessionId,
                new PeerInfo(deviceA, "A", "a00000", staticA.ExportPublicKey()),
                new EndpointPair(null, aEp.Tcp), PunchCount: 3, false);
            var outcomeB = await puncherB.RespondAsync(invite);
            var outcomeA = await outcomeATask;

            Assert.True(outcomeA.Ok, outcomeA.FailReason); // 旧实现此处必 punch_timeout（+2 双 miss）
            Assert.True(outcomeB.Ok, outcomeB.FailReason);
            Assert.Equal(sessionId, outcomeA.SessionId);
            Assert.Equal(sessionId, outcomeB.SessionId);
            // §5.4 候选上报链路：0x70 携带本机候选（注入枚举=回环 → 127.0.0.1×端口 L）
            Assert.NotNull(seamsA.SeenCandidates);
            Assert.Contains(seamsA.SeenCandidates!, c => c.Host == "127.0.0.1" && c.Port == aEp.Tcp!.Port);
            Assert.NotNull(seamsB.ReportedCandidates.Single()); // B 的 0x76 亦携带
            await outcomeA.Session!.DisposeAsync();
            await outcomeB.Session!.DisposeAsync();
        }
    }

    [Fact]
    public async Task 对端映射平移_预测与偏移0皆死_候选直连命中()
    {
        // §5.4 同网段/发夹样：B 映射端口=L+5（A 瞄映射口 +2 与偏移 0 均死——公网 NAT 平移/
        // 发夹不可达）；唯一活路=A 的直连探测命中 B 随 0x76 携带的候选 127.0.0.1:L（真实监听口）。
        using var staticA = EcKeyPair.Generate();
        using var staticB = EcKeyPair.Generate();
        var deviceA = Guid.NewGuid();
        var deviceB = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var (puncherA, seamsA) = Make(staticA, localIps: LoopbackIps);                 // A 恒等映射
        var (puncherB, seamsB) = Make(staticB, mappedPortShift: 5, localIps: LoopbackIps); // B 映射=L+5
        using (puncherA)
        using (puncherB)
        {
            seamsA.RequestHandler = async (target, trigger, proto, endpoints, candidates, bearerCaps, ct) =>
            {
                var bReported = await seamsB.Reported.Task;
                return new PunchRequestAck(0, 0, MsgType.PunchRequest, sessionId,
                    new PeerInfo(deviceB, "B", "b00000", staticB.ExportPublicKey()),
                    new EndpointPair(null, bReported.Tcp), PunchCount: 3, RelayAllowed: false,
                    PeerLocalCandidates: seamsB.ReportedCandidates.Single()); // B 候选透传给 A
            };

            var outcomeATask = puncherA.InitiateAsync(deviceB, null, "tcp");
            var aEp = await seamsA.Probed.Task;
            var invite = new PunchInvite(0, 0, MsgType.PunchInvite, sessionId,
                new PeerInfo(deviceA, "A", "a00000", staticA.ExportPublicKey()),
                new EndpointPair(null, aEp.Tcp), PunchCount: 3, false); // 不带候选：B 跳过直连探测（门控），靠 accept 侧应答 A 的候选连接
            var outcomeB = await puncherB.RespondAsync(invite);
            var outcomeA = await outcomeATask;

            Assert.True(outcomeA.Ok, outcomeA.FailReason); // A 侧唯一可达路径=候选直连（红绿证据）
            Assert.True(outcomeB.Ok, outcomeB.FailReason);
            // B 的 0x76 候选=本机监听口 L（注入枚举=回环）
            var bCandidates = seamsB.ReportedCandidates.Single()!;
            Assert.Contains(bCandidates, c => c.Host == "127.0.0.1");
            await outcomeA.Session!.DisposeAsync();
            await outcomeB.Session!.DisposeAsync();
        }
    }
}

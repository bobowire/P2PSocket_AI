// M6-06 弱网红绿矩阵（设计 11 §11.3/§12 决议⑥，立项级证据）：
// 载体=真实 UdpNatSimulator（随机 STUN 端口——不经 :3478，免跨进程撞车）+ 真实 Puncher 双角色
// 握手穿 NAT（caps 协商→KCP 承载）+ 真实 TunnelSession 8 并发 channel 持续传输（信用窗推进）。
// 矩阵：净网建链 → 注扰动（丢包 1/5/20% × 乱序×重复×100ms 抖动 + 突发连片丢档）→
// KCP 组零缺段/零 credit 挂起/会话存活；裸 UDP 对照组同条件复现 M2-38 家族故障（红证）；
// 黑洞档：KCP 无限重传不伪造活性——KEEPALIVE 3×miss 断链照常，净网恢复后重建承载可用。
// （世界级 ClientRuntime 场景映射面 proto=tcp 走 TCP 打洞不经 UDP NAT——KCP 弱网证据由本
// 会话级矩阵承载；世界级 DisableKcpBearer 布线验证见 ScenarioIntegrationTests.M6_05_A3形态。）
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using P2P.Client.Punch;
using P2P.Client.Tunnel;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using P2P.Core.Tunnel;
using P2P.IntegrationTests.NatSimulator;
using Xunit;
using Xunit.Abstractions;

namespace P2P.IntegrationTests;

[Collection("heavy-runtime")] // 保守与既有世界测试互斥串行（虽用随机端口）
public sealed class KcpWeakNetworkMatrixTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private StubStunUpstream _upstream = null!;
    private UdpNatSimulator _sim = null!;

    private static readonly IPAddress IpA = IPAddress.Parse("127.0.0.4");
    private static readonly IPAddress IpB = IPAddress.Parse("127.0.0.5");

    public KcpWeakNetworkMatrixTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        _upstream = new StubStunUpstream();
        await _upstream.StartAsync();
        _sim = new UdpNatSimulator(new UdpNatOptions { Upstream = _upstream.Endpoint, StunPort = 0 });
        await _sim.StartAsync();
        _sim.RegisterClient(IpA, UdpNatMode.FullCone, "A");
        _sim.RegisterClient(IpB, UdpNatMode.FullCone, "B");
    }

    public async Task DisposeAsync()
    {
        await _sim.DisposeAsync();
        await _upstream.DisposeAsync();
    }

    // ── 建链基建 ─────────────────────────────────────────────────────

    /// <summary>极简 RFC5389 Binding 事务（经模拟器：建立公网映射+XOR-MAPPED 改写返回公网身份）。</summary>
    private static async Task<IPEndPoint> ProbeAsync(Socket socket, IPEndPoint stun, CancellationToken ct)
    {
        var req = new byte[20];
        req[1] = 1; // Binding Request
        BinaryPrimitives.WriteUInt32BigEndian(req.AsSpan(4), 0x2112A442);
        RandomGenerator.Bytes(12).CopyTo(req.AsSpan(8));
        await socket.SendToAsync(req, SocketFlags.None, stun, ct);
        var buf = new byte[256];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var r = await socket.ReceiveFromAsync(buf, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token);
        var off = 20;
        while (off + 4 <= r.ReceivedBytes)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(off));
            var len = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(off + 2));
            if (type == 0x0020 && len == 8)
            {
                var port = (BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(off + 6)) ^ 0x2112) & 0xFFFF;
                Span<byte> addr = stackalloc byte[4];
                for (var i = 0; i < 4; i++)
                    addr[i] = (byte)(buf[off + 8 + i] ^ (byte)(0x2112A442 >> (24 - i * 8)));
                return new IPEndPoint(new IPAddress(addr), port);
            }
            off += 4 + len + (len % 4 == 0 ? 0 : 4 - len % 4);
        }
        throw new IOException("STUN 应答无 XOR-MAPPED-ADDRESS");
    }

    /// <summary>B 侧 handler：OPEN 即回 OK；DATA 逐帧收集并等额授信（信用窗推进=消费侧语义）。</summary>
    private sealed class ConsumerHandler : ITunnelChannelHandler
    {
        public ConcurrentDictionary<uint, List<byte>> Streams { get; } = [];
        public ConcurrentDictionary<uint, TaskCompletionSource> Opened { get; } = [];

        public void OnOpen(TunnelSession s, uint ch, OpenPayload p)
        {
            Opened.GetOrAdd(ch, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult();
            _ = s.SendOpenResultAsync(ch, new OpenResultPayload(true, null));
        }

        public void OnOpenResult(TunnelSession s, uint ch, OpenResultPayload r) { }
        public void OnData(TunnelSession s, uint ch, ReadOnlyMemory<byte> d)
        {
            var list = Streams.GetOrAdd(ch, _ => []);
            lock (list) list.AddRange(d.ToArray());
            _ = s.SendWindowCreditAsync(ch, d.Length);
        }
        public void OnUdpDgram(TunnelSession s, uint ch, ReadOnlyMemory<byte> d) { }
        public void OnClose(TunnelSession s, uint ch) { }
    }

    /// <summary>穿 NAT 双端建链（Puncher 真实握手：0x70/0x76 假缝=测试本体充当服务端透传 caps；
    /// 探测/打洞/承载全部经模拟器公网映射）。kcp=false 即裸 UDP 对照组建链。</summary>
    private async Task<(TunnelSession A, TunnelSession B, ConsumerHandler HandlerB)> EstablishAsync(bool kcp, int keepaliveSec = 20)
    {
        var staticA = EcKeyPair.Generate();
        var staticB = EcKeyPair.Generate();
        var deviceB = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var caps = kcp ? BearerCaps.Kcp : (uint?)null;
        var handlerB = new ConsumerHandler();
        // 端点经两段式假缝流转（PuncherKcpBearerTests 同款编排；探测由 Puncher 在其**自身**打洞
        // socket 上进行——BindAddress=注册内网 IP，模拟器按源 IP 识别客户端 NAT 并建公网映射）
        var bReported = new TaskCompletionSource<EndpointPair>(TaskCreationOptions.RunContinuationsAsynchronously);
        var aSentEp = new TaskCompletionSource<EndpointPair>(TaskCreationOptions.RunContinuationsAsynchronously);

        var puncherA = new Puncher(
            (target, trigger, proto, endpoints, candidates, sentCaps, ct) =>
            {
                aSentEp.TrySetResult(endpoints!);
                return Task.Run(async () => new PunchRequestAck(0, 0, MsgType.PunchRequest, sessionId,
                    new PeerInfo(deviceB, "B", "b00000", staticB.ExportPublicKey()),
                    await bReported.Task, 3, false, PeerBearerCaps: caps));
            },
            (_, _, _, _, _) => Task.CompletedTask,
            (socket, ct) => ProbeAsync(socket, _sim.StunEndpoint, ct),
            staticA, options: new PunchOptions { KeepaliveSec = keepaliveSec, BindAddress = IpA },
            localBearerCaps: kcp ? () => BearerCaps.Kcp : null);
        var puncherB = new Puncher(
            (_, _, _, _, _, _, _) => Task.FromResult(new PunchRequestAck(0, 0, MsgType.PunchRequest, Guid.NewGuid(),
                new PeerInfo(Guid.NewGuid(), "?", "000000", []),
                new EndpointPair(new Endpoint("127.0.0.1", 1), null), 3, false)),
            (sid, endpoints, candidates, sentCaps, ct) =>
            {
                bReported.TrySetResult(endpoints);
                return Task.CompletedTask;
            },
            (socket, ct) => ProbeAsync(socket, _sim.StunEndpoint, ct),
            staticB, handlerB, new PunchOptions { KeepaliveSec = keepaliveSec, BindAddress = IpB },
            localBearerCaps: kcp ? () => BearerCaps.Kcp : null);

        var outcomeATask = Task.Run(() => puncherA.InitiateAsync(deviceB, null, "udp"));
        // 服务端（测试本体）：A 的公网端点随 0x70 到手 → 0x71 下发 B（caps 透传）
        var invite = new PunchInvite(0, 0, MsgType.PunchInvite, sessionId,
            new PeerInfo(Guid.NewGuid(), "A", "a00000", staticA.ExportPublicKey()),
            await aSentEp.Task, 3, false, PeerBearerCaps: caps);
        var outcomeB = await puncherB.RespondAsync(invite);
        var outcomeA = await outcomeATask;
        Assert.True(outcomeA.Ok, "A 打洞失败：" + outcomeA.FailReason);
        Assert.True(outcomeB.Ok, "B 打洞失败：" + outcomeB.FailReason);
        Assert.Equal(kcp, outcomeA.Session!.UsesKcpBearer); // 承载协商生效实证（穿 NAT）
        Assert.Equal(kcp, outcomeB.Session!.UsesKcpBearer);
        _output.WriteLine($"[wn] 建链 kcp={kcp} a={await aSentEp.Task} b={(await bReported.Task).Udp}");
        return (outcomeA.Session, outcomeB.Session, handlerB);
    }

    /// <summary>对齐到突发连片丢窗内（相位确定性：保证传输起点即被整窗丢覆盖——回环过快时
    /// 264 包可在一个净网窗内跑完致零丢包，红绿证据成运气）。</summary>
    private static async Task AlignToBurstLossWindowAsync(int windowMs)
    {
        var period = 2L * windowMs;
        while (true)
        {
            var phase = Environment.TickCount64 % period;
            if (phase >= windowMs + 20 && phase < 2 * windowMs - 60) return; // lossy 窗中段（进出留裕量）
            await Task.Delay(10);
        }
    }

    /// <summary>单 channel 传输：OPEN→（等 OPEN 就位）→N×1KiB 确定性载荷→等长收齐→字节比对。</summary>
    private async Task<bool> TransferChannelAsync(TunnelSession a, ConsumerHandler handlerB,
        uint ch, int frames, int timeoutSec)
    {
        try
        {
            await a.SendOpenAsync(ch, new OpenPayload("tcp", "self", 80));
            var opened = handlerB.Opened.GetOrAdd(ch,
                _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var payload = new byte[1024];
            for (var f = 0; f < frames; f++)
            {
                for (var i = 0; i < payload.Length; i++)
                    payload[i] = (byte)(ch + f * 7 + i);
                await a.SendDataAsync(ch, payload).AsTask().WaitAsync(TimeSpan.FromSeconds(timeoutSec));
            }
            var stream = handlerB.Streams.GetOrAdd(ch, _ => []);
            var deadline = Environment.TickCount64 + timeoutSec * 1000L;
            while (true)
            {
                lock (stream)
                    if (stream.Count >= frames * 1024) break;
                if (Environment.TickCount64 > deadline) return false; // 欠收（credit 挂起/缺段）
                await Task.Delay(20);
            }
            byte[] got;
            lock (stream) got = [.. stream];
            var expected = new byte[frames * 1024];
            for (var f = 0; f < frames; f++)
                for (var i = 0; i < 1024; i++)
                    expected[f * 1024 + i] = (byte)(ch + f * 7 + i);
            return got.AsSpan().SequenceEqual(expected); // 字节精确=零缺段零重复
        }
        catch (Exception e)
        {
            _output.WriteLine($"[wn] channel {ch} 异常：{e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    /// <summary>8 并发 channel 持续传输（各自独立 conv——channel 间队头隔离观察面）。</summary>
    private async Task<bool[]> TransferAllAsync(TunnelSession a, ConsumerHandler handlerB,
        int framesPerChannel, int timeoutSec)
    {
        var channels = Enumerable.Range(0, 8).Select(_ => a.AllocateChannelId()).ToArray();
        return await Task.WhenAll(channels.Select(ch => TransferChannelAsync(a, handlerB, ch,
            framesPerChannel, timeoutSec)));
    }

    // ── 红绿矩阵（设计 11 §12 决议⑥修订版：矩阵全格+突发档；对照组红证）────────

    [Theory]
    [InlineData(0.01)]  // 1%
    [InlineData(0.05)]  // 5%
    [InlineData(0.20)]  // 20%
    public async Task KCP组_丢包下_8并发channel_零缺段零挂起_会话存活(double loss)
    {
        var (a, bSession, handlerB) = await EstablishAsync(kcp: true);
        try
        {
            _sim.Perturbation = new UdpPerturbation { LossRate = loss, Seed = 1000 + (int)(loss * 1000) };
            var results = await TransferAllAsync(a, handlerB, framesPerChannel: 32, timeoutSec: 90);
            Assert.True(_sim.PerturbDropped > 0, "扰动须实际生效（否则绿灯空洞）");
            Assert.True(results.All(r => r),
                $"KCP 组 {loss:P0} 丢包：须零缺段/零 credit 挂起（丢 {_sim.PerturbDropped} 包，" +
                $"失败 channel {results.Count(r => !r)}/8）");
            Assert.False(a.IsClosed); // 会话存活
        }
        finally
        {
            await a.DisposeAsync();
            await bSession.DisposeAsync();
        }
    }

    [Fact]
    public async Task KCP组_乱序重复抖动叠加_零缺段零挂起()
    {
        var (a, bSession, handlerB) = await EstablishAsync(kcp: true);
        try
        {
            _sim.Perturbation = new UdpPerturbation
            {
                LossRate = 0.05, ReorderDepth = 3, DuplicateRate = 0.10, JitterMs = 100, Seed = 77,
            };
            var results = await TransferAllAsync(a, handlerB, framesPerChannel: 32, timeoutSec: 90);
            Assert.True(_sim.PerturbDropped > 0, "扰动须实际生效");
            Assert.True(results.All(r => r), "KCP 组 乱序×重复×抖动：须零缺段/零挂起");
            Assert.False(a.IsClosed);
        }
        finally
        {
            await a.DisposeAsync();
            await bSession.DisposeAsync();
        }
    }

    [Fact]
    public async Task KCP组_突发连片丢档_重传恢复_零缺段()
    {
        // 决议⑥ 突发档：200ms 窗连片丢交替（WiFi 漫游/NAT 重绑定形态——公网主流丢包形态）
        var (a, bSession, handlerB) = await EstablishAsync(kcp: true);
        try
        {
            _sim.Perturbation = new UdpPerturbation { BurstLossWindowMs = 200, Seed = 88 };
            await AlignToBurstLossWindowAsync(200);
            var results = await TransferAllAsync(a, handlerB, framesPerChannel: 32, timeoutSec: 150);
            Assert.True(_sim.PerturbDropped > 0, "扰动须实际生效");
            Assert.True(results.All(r => r), "KCP 组 突发连片丢：重传恢复须零缺段/零挂起");
            Assert.False(a.IsClosed);
        }
        finally
        {
            await a.DisposeAsync();
            await bSession.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(20)]  // 20% 均匀丢
    [InlineData(-1)]   // 突发连片丢档
    public async Task 裸UDP对照组_同条件复现M2_38家族故障(int lossPct)
    {
        // 红证（设计 11 §11.3）：对照组承载=裸数据报——丢 DATA 帧字节流缺段/丢 WINDOW 信用挂起
        // → 欠收或字节漂移（M2-38 家族）。KCP 组同条件全绿即红绿互证。
        var (a, bSession, handlerB) = await EstablishAsync(kcp: false);
        try
        {
            _sim.Perturbation = lossPct < 0
                ? new UdpPerturbation { BurstLossWindowMs = 200, Seed = 88 }
                : new UdpPerturbation { LossRate = lossPct / 100.0, Seed = 2000 + lossPct };
            if (lossPct < 0) await AlignToBurstLossWindowAsync(200);
            var results = await TransferAllAsync(a, handlerB, framesPerChannel: 32, timeoutSec: 40);
            _output.WriteLine($"[wn] B: recv={bSession.ReceivedFrames} sent={a.SentFrames} replayDrop={bSession.ReplayDropped} " +
                $"closedA={a.IsClosed} closedB={bSession.IsClosed} simDrop={_sim.PerturbDropped} loopFaults={_sim.LoopFaults}");
            Assert.True(_sim.PerturbDropped > 0, $"扰动须实际生效（否则红证空洞；LoopFaults={_sim.LoopFaults}）");
            Assert.Contains(false, results); // 至少一 channel 欠收/漂移（丢包不可恢复）
            Assert.False(results.All(r => r), "裸 UDP 对照组同条件须复现 M2-38 家族故障");
        }
        finally
        {
            await a.DisposeAsync();
            await bSession.DisposeAsync();
        }
    }

    [Fact]
    public async Task 黑洞档_KCP重传不伪造活性_心跳断链照常_净网恢复重建()
    {
        // 设计 11 §2.3 裁剪③/§11.6：dead_link 不实现=重传到会话消亡为止；活性判定归 KEEPALIVE
        // 3×miss——全丢黑洞下 KCP 再怎么重传也无对端响应，断链照常触发。
        var (a, bSession, handlerB) = await EstablishAsync(kcp: true, keepaliveSec: 2);
        try
        {
            _sim.Perturbation = new UdpPerturbation { LossRate = 1.0 };
            var disconnected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            a.Disconnected += (_, reason) => disconnected.TrySetResult(reason);
            var reason = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(20));
            _output.WriteLine($"[wn] 黑洞断链 reason={reason}（重传不伪造活性）");
            Assert.True(a.IsClosed);
        }
        finally
        {
            await a.DisposeAsync();
            await bSession.DisposeAsync();
        }

        // 净网恢复：承载随会话消亡，重建走全新打洞+协商（§7.3 断链重建路径）
        _sim.Perturbation = null;
        var (a2, b2, _) = await EstablishAsync(kcp: true);
        await a2.DisposeAsync();
        await b2.DisposeAsync();
    }

}

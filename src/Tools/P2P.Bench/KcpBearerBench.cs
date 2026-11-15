// M6-07 KCP 承载基准（设计 11 §12 决议⑥，NET-77/TD-29；09 §3 增补节）：
// ⑧ 四组对照（0% loss）：udp-raw（裸 UDP 承载，caps off）/ kcp-nc1（KCP 默认参数）/
//    kcp-nc0（KCP 拥塞窗开对照）/ tcp-ref（TCP 承载参照——TcpFrameTransport u16 分帧，
//    与中继 TCP JOIN 同口径）。承载介质=UdpNatSimulator FC×FC（STUN 端口随机——每 cell
//    独立实例免 :3478 跨进程撞车）；tcp-ref=回环 TCP 对。
//    测量口径=会话级 DATA 流（同信用窗 64KiB/同 chunk 1368B=MappingEngine 口径/同帧逻辑），
//    仅承载不同——性能闸（吞吐比/PING 增量）的直接测量面。tcp 映射复用 udp 打洞会话时
//    DATA 走 KCP 可靠层=真实生产路径（02 §4.5 设备对级隧道复用），口径成立。
// ⑨ 调参矩阵：Nodelay{1,0} × Resend{0,2} × Interval{10,20,40ms} × nc{1,0} = 24 cell
//    → 推荐参数（回填设计 11 §5.4）。
// ⑩ 丢包-吞吐曲线：KCP nc=1 @ loss{0,1,5,10,20,30}%（净网建链后注扰动，M6-06 同口径；
//    完整性恒断言；裸 UDP 在 loss 下无完整性语义——曲线仅 KCP，报告注记）。
// 性能闸（决议⑥）：0% loss KCP 吞吐 ≥ 裸 UDP 70% 且 PING RTT 增量 ≤5ms；不达线回调参
//    矩阵→移植核对层。TD-24 L1 标注：本机回环≠真实网络。
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using P2P.Client.Punch;
using P2P.Client.Tunnel;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using P2P.Core.Transport.Kcp;
using P2P.Core.Tunnel;
using P2P.IntegrationTests.NatSimulator;

namespace P2P.Bench;

internal static class KcpBearerBench
{
    private static readonly IPAddress IpA = IPAddress.Parse("127.0.0.4");
    private static readonly IPAddress IpB = IPAddress.Parse("127.0.0.5");
    private const int DataChunk = 1368;         // MappingEngine.ChunkSize 口径（02 §4.3）
    private const int BigTotal = 64 << 20;      // 1ch 大帧档 64MB
    private const int SmallChunk = 100;         // 小帧档（M2-37 小帧形态——逐帧开销观察面）
    private const int SmallTotal = 8 << 20;     // 8MB
    private const int MultiChannel = 8;         // 8 channel 聚合档（conv 隔离扩展性）
    private const int MultiPer = 8 << 20;       // 每 channel 8MB（合计 64MB）
    private const int PingSamples = 100;

    // ── ⑧ 四组对照 ─────────────────────────────────────────────────

    public static async Task RunFourGroupsAsync(Csv four, Csv setup, Csv ping)
    {
        Console.WriteLine("  [kcp] udp-raw（裸承载基线）…");
        await using (var sim = await BenchSim.CreateAsync())
        {
            var (a, b, h) = await EstablishUdpAsync(sim.Sim, kcp: false, peerOpts: null, setup);
            await MeasureGroupAsync(four, ping, a, b, h, "udp-raw");
            await a.DisposeAsync();
            await b.DisposeAsync();
        }

        Console.WriteLine("  [kcp] kcp-nc1（默认参数）…");
        await using (var sim = await BenchSim.CreateAsync())
        {
            var (a, b, h) = await EstablishUdpAsync(sim.Sim, kcp: true, peerOpts: null, setup);
            await MeasureGroupAsync(four, ping, a, b, h, "kcp-nc1");
            await a.DisposeAsync();
            await b.DisposeAsync();
        }

        Console.WriteLine("  [kcp] kcp-nc0（拥塞窗开对照）…");
        await using (var sim = await BenchSim.CreateAsync())
        {
            var (a, b, h) = await EstablishUdpAsync(sim.Sim, kcp: true,
                peerOpts: new KcpPeerOptions { NoCongestionWindow = false }, setup);
            await MeasureGroupAsync(four, ping, a, b, h, "kcp-nc0");
            await a.DisposeAsync();
            await b.DisposeAsync();
        }

        Console.WriteLine("  [kcp] tcp-ref（TCP 承载参照）…");
        {
            var (a, b, h) = await EstablishTcpAsync(setup);
            await MeasureGroupAsync(four, ping, a, b, h, "tcp-ref");
            await a.DisposeAsync();
            await b.DisposeAsync();
        }
    }

    /// <summary>每 cell 独立基准世界：NAT 模拟器 + 其 STUN 上游替身（弱网矩阵同款装配——
    /// 缺上游则探测转发到无人监听端点，双端超时且 B 不上报，A 侧假缝永久等待）。</summary>
    private sealed class BenchSim : IAsyncDisposable
    {
        public readonly StubStunUpstream Upstream;
        public readonly UdpNatSimulator Sim;

        private BenchSim(StubStunUpstream upstream, UdpNatSimulator sim)
        {
            Upstream = upstream;
            Sim = sim;
        }

        public static async Task<BenchSim> CreateAsync()
        {
            var upstream = new StubStunUpstream();
            await upstream.StartAsync();
            var sim = new UdpNatSimulator(new UdpNatOptions { Upstream = upstream.Endpoint, StunPort = 0 });
            await sim.StartAsync();
            sim.RegisterClient(IpA, UdpNatMode.FullCone, "A");
            sim.RegisterClient(IpB, UdpNatMode.FullCone, "B");
            return new BenchSim(upstream, sim);
        }

        public async ValueTask DisposeAsync()
        {
            await Sim.DisposeAsync();
            await Upstream.DisposeAsync();
        }
    }

    private static async Task MeasureGroupAsync(Csv four, Csv ping, TunnelSession a, TunnelSession b,
        ConsumerHandler h, string group)
    {
        // 吞吐三档（完整性全比对）
        await TierAsync(four, a, h, group, "1ch-big", DataChunk, 1, BigTotal);
        await TierAsync(four, a, h, group, "1ch-small", SmallChunk, 1, SmallTotal);
        await TierAsync(four, a, h, group, "8ch-agg", DataChunk, MultiChannel, (long)MultiChannel * MultiPer);

        // PING RTT（Stopwatch µs——PONG 载荷时间戳 ms 精度回环失真，RttBench 同款教训）
        for (var i = 1; i <= PingSamples; i++)
        {
            var sw = Stopwatch.StartNew();
            await a.PingAsync();
            sw.Stop();
            ping.Row(group, i, Math.Round(sw.Elapsed.TotalMilliseconds, 3));
        }
        _ = b; // b 会话由调用方 Dispose（对称持有）
    }

    /// <summary>一档吞吐：streams 并发 channel 各推 totalBytes/streams（字节精确完整性比对）。</summary>
    private static async Task TierAsync(Csv four, TunnelSession a, ConsumerHandler h,
        string group, string tier, int chunk, int streams, long totalBytes)
    {
        var channels = new uint[streams];
        for (var i = 0; i < streams; i++)
        {
            channels[i] = a.AllocateChannelId();
            h.Opened.GetOrAdd(channels[i],   // 预置 TCS（B 的 OnOpen 只 TrySetResult——竞态安全）
                _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            await a.SendOpenAsync(channels[i], new OpenPayload("tcp", "self", 80));
        }
        // 等 B 侧 OPEN 全部就位（OPEN→conv 就绪；不等 OPEN_OK——数据流不依赖）
        foreach (var ch in channels)
            await h.Opened[ch].Task.WaitAsync(TimeSpan.FromSeconds(10));

        var per = totalBytes / streams;
        var strict = group != "udp-raw"; // 裸承载无可靠性：丢包语义内，实收记账
        var proc = Process.GetCurrentProcess();
        var cpu0 = proc.TotalProcessorTime;
        var w0 = Stopwatch.GetTimestamp();
        var delivered = await Task.WhenAll(channels.Select(ch => PumpAsync(a, h, ch, chunk, per, strict)));
        var elapsedMs = (long)((Stopwatch.GetTimestamp() - w0) * 1000.0 / Stopwatch.Frequency);
        var wallMs = Math.Max(1, elapsedMs);
        var cpuPct = (proc.TotalProcessorTime - cpu0).TotalMilliseconds / wallMs
                     / Environment.ProcessorCount * 100;

        if (strict)
            foreach (var ch in channels) // 完整性（基准自身的静默正确性闸）：逐 channel 字节精确
                VerifyIntegrity(h, ch, per, chunk);

        var deliveredBytes = delivered.Sum();
        var mbps = totalBytes * 8.0 / 1024 / 1024 / (elapsedMs / 1000.0); // 发送口径（裸承载对照=实收另列）
        four.Row(group, tier, chunk, streams, totalBytes, elapsedMs, Math.Round(mbps, 1),
            Math.Round(cpuPct, 1), deliveredBytes);
        Console.WriteLine($"    {group}/{tier} streams={streams} chunk={chunk} → {elapsedMs}ms {mbps:F1}Mbps " +
                          $"cpu={cpuPct:F0}% delivered={deliveredBytes * 100.0 / totalBytes:F1}%");
    }

    private static async Task<long> PumpAsync(TunnelSession a, ConsumerHandler h, uint ch, int chunk,
        long totalBytes, bool strict)
    {
        var payload = new byte[chunk];
        for (var i = 0; i < chunk; i++)
            payload[i] = (byte)(ch * 31 + i * 7);
        var sent = 0L;
        while (sent < totalBytes)
        {
            var take = (int)Math.Min(chunk, totalBytes - sent);
            await a.SendDataAsync(ch, payload.AsMemory(0, take));
            sent += take;
        }
        // 收齐等待：strict（KCP/TCP——欠收=缺陷即抛）；udp-raw=裸承载无可靠性，回环满速小帧
        // 下内核接收缓冲溢出丢包（M2-38 家族机理）——记实收不炸（发送口径 Mbps+实收对照）
        var stream = h.Streams.GetOrAdd(ch, _ => []);
        var deadline = Environment.TickCount64 + (strict ? 120_000 : 3_000);
        while (true)
        {
            long got;
            lock (stream) got = stream.Count;
            if (got >= totalBytes) return got;
            if (Environment.TickCount64 > deadline)
            {
                if (strict)
                    throw new TimeoutException($"channel {ch} 欠收 {got}/{totalBytes}");
                return got; // 裸承载：实收即账（丢包进对照）
            }
            await Task.Delay(5);
        }
    }

    private static void VerifyIntegrity(ConsumerHandler h, uint ch, long totalBytes, int chunk)
    {
        var stream = h.Streams.GetOrAdd(ch, _ => []);
        List<byte> got;
        lock (stream) got = [.. stream];
        if (got.Count != totalBytes)
            throw new InvalidOperationException($"完整性失败 ch={ch}：{got.Count}/{totalBytes}（缺段/挂起）");
        var payload = new byte[chunk];
        for (var i = 0; i < chunk; i++)
            payload[i] = (byte)(ch * 31 + i * 7);
        for (var off = 0; off < got.Count; off += chunk)
        {
            var take = Math.Min(chunk, got.Count - off);
            for (var i = 0; i < take; i++)
                if (got[off + i] != payload[i])
                    throw new InvalidOperationException($"完整性失败 ch={ch} @{off + i}");
        }
        h.Streams.TryRemove(ch, out _); // 下一档复用同一会话——清场
        h.Opened.TryRemove(ch, out _);
    }

    // ── ⑨ 调参矩阵（24 cell）＋ ⑩ 丢包曲线 ─────────────────────────

    public static async Task RunTuningMatrixAsync(Csv matrix)
    {
        const int cellBytes = 8 << 20;
        foreach (var nodelay in new[] { true, false })
        foreach (var resend in new[] { 0, 2 })
        foreach (var interval in new[] { 10, 20, 40 })
        foreach (var nc in new[] { true, false })
        {
            await using var sim = await BenchSim.CreateAsync();
            var (a, b, h) = await EstablishUdpAsync(sim.Sim, kcp: true,
                peerOpts: new KcpPeerOptions
                {
                    NoDelay = nodelay, Resend = resend, IntervalMs = interval, NoCongestionWindow = nc,
                }, setup: null);
            var cell = $"nd={(nodelay ? 1 : 0)},rs={resend},iv={interval},nc={(nc ? 1 : 0)}";
            var ch = a.AllocateChannelId();
            h.Opened.GetOrAdd(ch,
                _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            await a.SendOpenAsync(ch, new OpenPayload("tcp", "self", 80));
            await h.Opened[ch].Task.WaitAsync(TimeSpan.FromSeconds(10));
            var sw = Stopwatch.StartNew();
            await PumpAsync(a, h, ch, DataChunk, cellBytes, strict: true);
            var elapsed = sw.ElapsedMilliseconds;
            VerifyIntegrity(h, ch, cellBytes, DataChunk);
            var mbps = cellBytes * 8.0 / 1024 / 1024 / (elapsed / 1000.0);
            matrix.Row(nodelay ? 1 : 0, resend, interval, nc ? 1 : 0, cellBytes, elapsed,
                Math.Round(mbps, 1));
            Console.WriteLine($"    {cell} → {elapsed}ms {mbps:F1}Mbps");
            await a.DisposeAsync();
            await b.DisposeAsync();
        }
    }

    public static async Task RunLossCurveAsync(Csv loss)
    {
        const int cellBytes = 8 << 20;
        foreach (var pct in new[] { 0, 1, 5, 10, 20, 30 })
        {
            await using var sim = await BenchSim.CreateAsync();
            var (a, b, h) = await EstablishUdpAsync(sim.Sim, kcp: true, peerOpts: null, setup: null);
            if (pct > 0)
                sim.Sim.Perturbation = new UdpPerturbation { LossRate = pct / 100.0, Seed = 3000 + pct };
            var ch = a.AllocateChannelId();
            h.Opened.GetOrAdd(ch,
                _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            await a.SendOpenAsync(ch, new OpenPayload("tcp", "self", 80));
            await h.Opened[ch].Task.WaitAsync(TimeSpan.FromSeconds(30));
            var sw = Stopwatch.StartNew();
            await PumpAsync(a, h, ch, DataChunk, cellBytes, strict: true);
            var elapsed = sw.ElapsedMilliseconds;
            VerifyIntegrity(h, ch, cellBytes, DataChunk); // 任意丢包档完整性恒成立（零缺段）
            var mbps = cellBytes * 8.0 / 1024 / 1024 / (elapsed / 1000.0);
            loss.Row(pct, cellBytes, elapsed, Math.Round(mbps, 1), sim.Sim.PerturbDropped);
            Console.WriteLine($"    loss={pct}% → {elapsed}ms {mbps:F1}Mbps（sim 丢 {sim.Sim.PerturbDropped} 包）");
            await a.DisposeAsync();
            await b.DisposeAsync();
        }
    }

    // ── 建链基建（弱网矩阵同款：Puncher 穿 NAT，0x70/0x76 假缝=测试本体充当服务端）──

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
            _ = s.SendWindowCreditAsync(ch, d.Length); // 消费侧等额授信（信用窗推进）
        }
        public void OnUdpDgram(TunnelSession s, uint ch, ReadOnlyMemory<byte> d) { }
        public void OnClose(TunnelSession s, uint ch) { }
    }

    /// <summary>极简 RFC5388 Binding（经模拟器建立公网映射；XOR-MAPPED-ADDRESS 解析）。</summary>
    private static async Task<IPEndPoint> ProbeAsync(Socket socket, IPEndPoint stun, CancellationToken ct)
    {
        Console.WriteLine($"    [probe] → {stun} (local={socket.LocalEndPoint})");
        var req = new byte[20];
        req[1] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(req.AsSpan(4), 0x2112A442);
        RandomGenerator.Bytes(12).CopyTo(req.AsSpan(8));
        await socket.SendToAsync(req, SocketFlags.None, stun, ct);
        var buf = new byte[256];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var r = await socket.ReceiveFromAsync(buf, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token);
        Console.WriteLine($"    [probe] ← {r.ReceivedBytes}B from {r.RemoteEndPoint}");
        var off = 20;
        while (off + 4 <= r.ReceivedBytes)
        {
            var type = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(off));
            var len = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(off + 2));
            if (type == 0x0020 && len == 8)
            {
                var port = (System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(off + 6)) ^ 0x2112) & 0xFFFF;
                Span<byte> addr = stackalloc byte[4];
                for (var i = 0; i < 4; i++)
                    addr[i] = (byte)(buf[off + 8 + i] ^ (byte)(0x2112A442 >> (24 - i * 8)));
                return new IPEndPoint(new IPAddress(addr), port);
            }
            off += 4 + len + (len % 4 == 0 ? 0 : 4 - len % 4);
        }
        throw new IOException("STUN 应答无 XOR-MAPPED-ADDRESS");
    }

    private static async Task<(TunnelSession A, TunnelSession B, ConsumerHandler H)> EstablishUdpAsync(
        UdpNatSimulator sim, bool kcp, KcpPeerOptions? peerOpts, Csv? setup)
    {
        var staticA = EcKeyPair.Generate();
        var staticB = EcKeyPair.Generate();
        var deviceB = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var caps = kcp ? BearerCaps.Kcp : (uint?)null;
        var handlerB = new ConsumerHandler();
        var bReported = new TaskCompletionSource<EndpointPair>(TaskCreationOptions.RunContinuationsAsynchronously);
        var aSentEp = new TaskCompletionSource<EndpointPair>(TaskCreationOptions.RunContinuationsAsynchronously);

        var punchOptions = new PunchOptions
        {
            KeepaliveSec = 20,
            BindAddress = IpA,
            KcpBearerOptions = peerOpts is null ? null : new KcpTunnelTransportOptions { Peer = peerOpts },
        };
        var puncherA = new Puncher(
            (_, _, _, endpoints, _, _, _) =>
            {
                aSentEp.TrySetResult(endpoints!); // A 的 0x70 端点就绪（invite 下发依据）
                return Task.Run(async () => new PunchRequestAck(0, 0, MsgType.PunchRequest,
                    sessionId, new PeerInfo(deviceB, "B", "b00000", staticB.ExportPublicKey()),
                    await bReported.Task.WaitAsync(TimeSpan.FromSeconds(15)), 3, false,
                    PeerBearerCaps: caps));
            },
            (_, _, _, _, _) => Task.CompletedTask,
            (socket, ct) => ProbeAsync(socket, sim.StunEndpoint, ct),
            staticA, options: punchOptions, localIpEnumerator: _ => [],
            localBearerCaps: kcp ? () => BearerCaps.Kcp : null);
        var puncherB = new Puncher(
            (_, _, _, _, _, _, _) => Task.FromResult(new PunchRequestAck(0, 0, MsgType.PunchRequest,
                Guid.NewGuid(), new PeerInfo(Guid.NewGuid(), "?", "000000", []),
                new EndpointPair(new Endpoint("127.0.0.1", 1), null), 3, false)),
            (_, endpoints, _, _, _) =>
            {
                Console.WriteLine("    [est] B 0x76 上报就绪");
                bReported.TrySetResult(endpoints!);
                return Task.CompletedTask;
            },
            (socket, ct) => ProbeAsync(socket, sim.StunEndpoint, ct),
            staticB, handlerB,
            new PunchOptions { KeepaliveSec = 20, BindAddress = IpB },
            localIpEnumerator: _ => [],
            localBearerCaps: kcp ? () => BearerCaps.Kcp : null);

        var sw = Stopwatch.StartNew();
        Console.WriteLine($"    [est] begin kcp={kcp}");
        var outcomeATask = Task.Run(() => puncherA.InitiateAsync(deviceB, null, "udp"));
        EndpointPair aEp;
        try { aEp = await aSentEp.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (TimeoutException)
        {
            var o = await outcomeATask; // A 必已终局（成功路径必先设 aSentEp）
            throw new IOException($"A 未进入 0x70（A 终局 ok={o.Ok} fail={o.FailReason}）");
        }
        var invite = new PunchInvite(0, 0, MsgType.PunchInvite, sessionId,
            new PeerInfo(Guid.NewGuid(), "A", "a00000", staticA.ExportPublicKey()),
            aEp, 3, false, PeerBearerCaps: caps);
        Console.WriteLine("    [est] A 0x70 就绪，invite 已下发 B");
        var outcomeB = await puncherB.RespondAsync(invite);
        Console.WriteLine($"    [est] B done ok={outcomeB.Ok} {outcomeB.FailReason}");
        var outcomeA = await outcomeATask;
        Console.WriteLine($"    [est] A done ok={outcomeA.Ok} {outcomeA.FailReason}");
        var establishMs = sw.ElapsedMilliseconds;
        if (!outcomeA.Ok) throw new IOException("A 打洞失败：" + outcomeA.FailReason);
        if (!outcomeB.Ok) throw new IOException("B 打洞失败：" + outcomeB.FailReason);
        if (outcomeA.Session!.UsesKcpBearer != kcp)
            throw new IOException("承载协商与预期不符");
        setup?.Row(kcp && peerOpts is null ? "kcp-nc1" : kcp ? "kcp-cell" : "udp-raw", 0, establishMs);
        return (outcomeA.Session, outcomeB.Session!, handlerB);
    }

    /// <summary>TCP 参照组：回环 TCP 对 + TcpFrameTransport（中继 TCP 同 u16 分帧口径）+ 内联握手。</summary>
    private static async Task<(TunnelSession A, TunnelSession B, ConsumerHandler H)> EstablishTcpAsync(Csv? setup)
    {
        var staticA = EcKeyPair.Generate();
        var staticB = EcKeyPair.Generate();
        var handlerB = new ConsumerHandler();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var sockA = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await sockA.ConnectAsync(listener.LocalEndpoint);
        var sockB = await listener.AcceptSocketAsync();
        listener.Stop();

        var ta = new TcpFrameTransport(sockA);
        var tb = new TcpFrameTransport(sockB);
        var opts = new TunnelSessionOptions { KeepaliveInterval = TimeSpan.FromSeconds(20) };
        var sw = Stopwatch.StartNew();
        var sessionId = Guid.NewGuid();
        var connectTask = TunnelSession.ConnectAsync(sessionId, Guid.NewGuid(), staticA,
            staticB.ExportPublicKey(), ta, NullChannelHandler.Instance, opts);
        var t1 = await tb.ReceiveAsync() ?? throw new IOException("无 THello1");
        var b = await TunnelSession.AcceptAsync(Guid.NewGuid(), t1, staticB,
            staticA.ExportPublicKey(), tb, handlerB, opts);
        var a = await connectTask;
        setup?.Row("tcp-ref", 0, sw.ElapsedMilliseconds);
        return (a, b, handlerB);
    }
}

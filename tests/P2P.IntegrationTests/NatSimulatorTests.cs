using System.Net;
using System.Net.Sockets;
using System.Text;
using P2P.Core.Crypto;
using P2P.Core.Stun;
using P2P.IntegrationTests.NatSimulator;
using Xunit;

namespace P2P.IntegrationTests;

/// <summary>
/// M1-34 NatSimulator UDP 四模式（任务清单完成判定：集成测可按模式参数化拉起；各模式映射行为符合定义）。
/// 拓扑（单宿主机回环别名，09 §2.2）：模拟公网 127.0.0.1｜上游 STUN 替身 127.0.0.2（恒回垃圾映射地址
/// 证明改写）｜C1=127.0.0.4（参数化模式，各用例首行注册）｜C2=127.0.0.5（FullCone）｜C3=127.0.0.6
/// （FullCone）｜外部主机 R=127.0.0.9（未注册）。
/// </summary>
public sealed class NatSimulatorTests : IAsyncLifetime
{
    private readonly StubStunUpstream _upstream = new();
    private UdpNatSimulator _sim = null!;
    private UdpClient _c1 = null!;
    private UdpClient _c2 = null!;
    private UdpClient _c3 = null!;
    private UdpClient _r = null!;

    public async Task InitializeAsync()
    {
        await _upstream.StartAsync();
        _sim = new UdpNatSimulator(new UdpNatOptions { Upstream = _upstream.Endpoint });
        await _sim.StartAsync();
        _c1 = Bind(IPAddress.Parse("127.0.0.4"));
        _c2 = Bind(IPAddress.Parse("127.0.0.5"));
        _c3 = Bind(IPAddress.Parse("127.0.0.6"));
        _r = Bind(IPAddress.Parse("127.0.0.9"));
        _sim.RegisterClient(LocalEp(_c2), UdpNatMode.FullCone, "C2");
        _sim.RegisterClient(LocalEp(_c3), UdpNatMode.FullCone, "C3");
    }

    public async Task DisposeAsync()
    {
        await _sim.DisposeAsync();
        await _upstream.DisposeAsync();
        _c1.Dispose();
        _c2.Dispose();
        _c3.Dispose();
        _r.Dispose();
    }

    [Theory]
    [InlineData(UdpNatMode.FullCone)]
    [InlineData(UdpNatMode.RestrictedCone)]
    [InlineData(UdpNatMode.PortRestricted)]
    [InlineData(UdpNatMode.SymmetricRandom)]
    public async Task 映射分配与STUN改写_模式语义符合定义(UdpNatMode mode)
    {
        _sim.RegisterClient(LocalEp(_c1), mode, "C1");

        // STUN 探测 → 改写后的公网身份：上游替身恒回 203.0.113.7:9999，客户端读到分配端口即改写生效（TD-17）
        var p1 = await StunProbeAsync(_sim, _c1);
        Assert.Equal(IPAddress.Loopback, p1.Address);
        Assert.NotEqual(StubStunUpstream.GarbageAddress, p1.Address);
        Assert.NotEqual(StubStunUpstream.GarbagePort, p1.Port);
        Assert.InRange(p1.Port, 20000, 20999); // 模拟公网端口段（09 §2.2）

        var map1 = Assert.Single(_sim.Mappings, m => m.Client == "C1");
        Assert.Equal(p1, map1.Public);
        Assert.Equal(_sim.StunEndpoint, map1.Destination);
        Assert.True(_upstream.Requests >= 1); // 代理链路确实到达上游

        // C2（FullCone）探测 → 其固定公网身份 P2
        var p2 = await StunProbeAsync(_sim, _c2);

        // C1 → 第二目的地 P2：cone 复用同一映射（同源端口），symmetric 按目的地新映射（随机端口）
        Send(_c1, p2, "hi");
        var hi = await MustReceiveAsync(_c2, "C1 出站经 NAT 投递（源地址改写为公网身份）");
        Assert.Equal("hi", Encoding.UTF8.GetString(hi.Buffer));
        Assert.Equal(IPAddress.Loopback, hi.RemoteEndPoint.Address);

        var c1Maps = _sim.Mappings.Where(m => m.Client == "C1").ToList();
        if (mode == UdpNatMode.SymmetricRandom)
        {
            Assert.Equal(2, c1Maps.Count);
            var second = Assert.Single(c1Maps, m => !m.Public.Equals(p1));
            Assert.Equal(p2, second.Destination);
            Assert.NotEqual(p1.Port, second.Public.Port); // 随机分配 → 不可预测 → 打洞必失败（A-6 前提）
            Assert.Equal(second.Public.Port, hi.RemoteEndPoint.Port); // 投递源 = 第二映射端口
        }
        else
        {
            var only = Assert.Single(c1Maps); // 固定映射：换目的地不新增
            Assert.Equal(p1, only.Public);
            Assert.Equal(p1.Port, hi.RemoteEndPoint.Port); // 同源端口（cone 语义）
        }
    }

    [Theory]
    [InlineData(UdpNatMode.FullCone)]
    [InlineData(UdpNatMode.RestrictedCone)]
    [InlineData(UdpNatMode.PortRestricted)]
    [InlineData(UdpNatMode.SymmetricRandom)]
    public async Task 入站过滤矩阵_按模式定义放行或丢弃(UdpNatMode mode)
    {
        _sim.RegisterClient(LocalEp(_c1), mode, "C1");

        // 前置：C1/C2 各经 STUN 建映射（P1/P2），且 C1 已出站联系 P2（contacted）
        var p1 = await StunProbeAsync(_sim, _c1);
        var p2 = await StunProbeAsync(_sim, _c2);
        Send(_c1, p2, "hi");
        await MustReceiveAsync(_c2, "contacted 前置出站");

        // ① 外部主机 R → P1：仅 FullCone 放行（R 的 IP 从未出站联系过）
        Send(_r, p1, "R1");
        if (mode == UdpNatMode.FullCone)
            Assert.Equal("R1", Encoding.UTF8.GetString((await MustReceiveAsync(_c1, "FullCone 任意外部可入")).Buffer));
        else
        {
            await WaitFilteredAsync(p1, 1);
            await AssertSilentAsync(_c1);
        }

        // ② C3（与 contacted 同 IP、不同端口）→ P1：FullCone/RestrictedCone 放行，PortRestricted/Symmetric 丢弃
        Send(_c3, p1, "C3");
        if (mode is UdpNatMode.FullCone or UdpNatMode.RestrictedCone)
        {
            var got = await MustReceiveAsync(_c1, "IP 级放行");
            Assert.Equal("C3", Encoding.UTF8.GetString(got.Buffer));
            Assert.Equal(IPAddress.Loopback, got.RemoteEndPoint.Address); // 源已改写为 C3 公网身份
        }
        else
        {
            await WaitFilteredAsync(p1, 2);
            await AssertSilentAsync(_c1);
        }

        // ③ C2 → P1（其公网身份恰为 contacted 的 P2）：PortRestricted 也放行；Symmetric 丢弃（STUN 映射对外不可达）
        Send(_c2, p1, "C2");
        if (mode == UdpNatMode.SymmetricRandom)
        {
            await WaitFilteredAsync(p1, 3);
            await AssertSilentAsync(_c1);
        }
        else
        {
            var got = await MustReceiveAsync(_c1, "endpoint 级放行");
            Assert.Equal("C2", Encoding.UTF8.GetString(got.Buffer));
            Assert.Equal(p2, got.RemoteEndPoint); // 源 = C2 公网身份 P2（端口级精确命中）
        }

        // ④ Symmetric 精确目的地可入：C2 → C1 的第二映射（该映射目的地即 P2）
        if (mode == UdpNatMode.SymmetricRandom)
        {
            var m2 = Assert.Single(_sim.Mappings, m => m.Client == "C1" && m.Destination.Equals(p2));
            Send(_c2, m2.Public, "ok");
            var got = await MustReceiveAsync(_c1, "symmetric 仅放行映射目的地本身");
            Assert.Equal("ok", Encoding.UTF8.GetString(got.Buffer));
            Assert.Equal(p2, got.RemoteEndPoint);
        }
    }

    // ── 工具 ───────────────────────────────────────────────────────────

    private static UdpClient Bind(IPAddress address) => new(new IPEndPoint(address, 0));

    private static IPEndPoint LocalEp(UdpClient c) => (IPEndPoint)c.Client.LocalEndPoint!;

    private static void Send(UdpClient c, IPEndPoint to, string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        c.Send(bytes, bytes.Length, to);
    }

    /// <summary>STUN 探测（真实 StunCodec 组包/解包，DEVICE-AUTH 字段由上游替身忽略）。</summary>
    private static async Task<IPEndPoint> StunProbeAsync(UdpNatSimulator sim, UdpClient client)
    {
        var tid = StunCodec.NewTransactionId();
        var request = StunCodec.BuildBindingRequest(tid, Guid.NewGuid(), RandomGenerator.Bytes(32),
            (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), RandomGenerator.Bytes(16));
        client.Send(request, request.Length, sim.StunEndpoint);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (true)
        {
            var r = await client.ReceiveAsync(cts.Token);
            if (StunCodec.TryParseBindingResponse(r.Buffer, out var resp) &&
                resp!.TransactionId.AsSpan().SequenceEqual(tid))
                return resp.Mapped;
        }
    }

    private static async Task<UdpReceiveResult?> ReceiveOrNullAsync(UdpClient c, int ms = 3000)
    {
        using var cts = new CancellationTokenSource(ms);
        try { return await c.ReceiveAsync(cts.Token); }
        catch (OperationCanceledException) { return null; }
    }

    private static async Task<UdpReceiveResult> MustReceiveAsync(UdpClient c, string because)
    {
        var r = await ReceiveOrNullAsync(c);
        Assert.True(r.HasValue, $"超时未收到报文：{because}");
        return r!.Value;
    }

    private static async Task AssertSilentAsync(UdpClient c, int ms = 150)
    {
        var r = await ReceiveOrNullAsync(c, ms);
        Assert.True(r is null, r is null
            ? "不应收到报文"
            : $"不应收到报文，却收到 {Encoding.UTF8.GetString(r.Value.Buffer)} 来自 {r.Value.RemoteEndPoint}");
    }

    /// <summary>轮询直至 target 公网端点的过滤记录达到 expected 条（负向断言即时化，不靠静默超时）。</summary>
    private async Task WaitFilteredAsync(IPEndPoint target, int expected)
    {
        var deadline = Environment.TickCount64 + 3000;
        while (true)
        {
            var n = _sim.Filtered.Count(f => f.Target.Equals(target));
            if (n >= expected) return;
            if (Environment.TickCount64 > deadline)
                Assert.Fail($"入站过滤记录未达预期：期望 ≥{expected}，实际 {n}（target={target}）");
            await Task.Delay(20);
        }
    }
}

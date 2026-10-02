// M3-15 stun-test RFC5780 子集判型集成测（05 §7.2、09 §2.2 双地址世界）：
// 真实 StunService（alt 配置→辅监听+两属性）+ UdpNatSimulator 双地址代理（改写 RFC5780 属性、
// 回程按接收方模式矩阵过滤）→ StunTester 全链判型。
//   场景① FullCone 双地址：eim+eif（TCP 顺带直连真实服务覆盖依赖测试 false 分支）；
//   场景② SymmetricRandom 双地址：adm_or_apdm+adf_or_apdf（change 回包被过滤，负向断言即时化）；
//   场景③ TCP SymmetricSequential：分配规律 sequential=true（TcpNatSimulator 导演端口，TD-17）；
//   场景④ 无 alt 单地址：downgraded+两维 null+注记（05 §7.2 单公网 IP 降级口径）；
//   场景⑤ 未注册：StunTestException 1002（04 §5 码表）。
// 全运行时重组件专用集合（与 ScenarioIntegrationTests 共用）：真实服务+模拟器监听相互串行，
// 避免与轻量类并行时叠加满载偶发（沿 M1-31 加固惯例）。
using System.Net;
using Microsoft.EntityFrameworkCore;
using P2P.Client.Diagnostics;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using P2P.Core.Utils;
using P2P.IntegrationTests.NatSimulator;
using P2P.Server.Data;
using P2P.Server.Services;
using Xunit;

namespace P2P.IntegrationTests;

[Collection("heavy-runtime")]
public sealed class M3_15_StunTestTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    private readonly List<StunService> _stuns = [];
    private readonly List<UdpNatSimulator> _sims = [];
    private readonly List<TcpNatSimulator> _tcpSims = [];
    private StubFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new StubFactory(CreateDb);
        using var init = _factory.CreateDbContext();
        DbInitializer.Initialize(init);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _stuns) await s.DisposeAsync();
        foreach (var s in _sims) await s.DisposeAsync();
        foreach (var s in _tcpSims) await s.DisposeAsync();
        await Task.Delay(200); // 服务收尾与夹具销毁竞态宽限（ScenarioIntegrationTests 同法）
        try { _db.Dispose(); }
        catch (InvalidOperationException) { }
    }

    private AppDbContext CreateDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_db.DataSource).Options);

    // ── 世界构建 ───────────────────────────────────────────────────────

    /// <summary>种已注册设备行（stun-test 认证查表的数据前提；凭据留在测试内存，不落客户端态）。</summary>
    private async Task<(Guid DeviceId, byte[] Secret)> SeedDeviceAsync()
    {
        var deviceId = Guid.NewGuid();
        var secret = RandomGenerator.Bytes(32);
        using var kp = EcKeyPair.Generate();
        await using var db = _factory.CreateDbContext();
        db.Devices.Add(new Device
        {
            Id = deviceId,
            DeviceName = $"st-{Guid.NewGuid():N}"[..12],
            Os = "windows",
            ClientVersion = "0.1.0",
            MacCode = $"SC-{Guid.NewGuid():N}"[..16],
            RemoteCode = $"ST-{Guid.NewGuid():N}"[..10],
            VirtualIp = "127.0.0.20",
            StaticPubKey = kp.ExportPublicKey(),
            DeviceSecret = secret,
            LastSeenAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return (deviceId, secret);
    }

    /// <summary>拉起真实 StunService（alt 非空=双地址世界：辅监听+RFC5780 两属性；端口全随机）。</summary>
    private async Task<StunService> StartStunAsync(IPEndPoint? alt)
    {
        var stun = new StunService(_factory, requireAuth: true, alt: alt);
        await stun.StartAsync(0, 0);
        _stuns.Add(stun);
        return stun;
    }

    /// <summary>双地址 NAT 世界：主/辅上游=真实服务两监听，公网侧以回环别名 127.0.0.1/127.0.0.2
    /// 构造异 IP（EIF/ADF 才可细分，05 §7.2），客户端 NAT 注册在 127.0.0.4。</summary>
    private async Task<UdpNatSimulator> StartDualAddressSimAsync(StunService stun, UdpNatMode mode)
    {
        var sim = new UdpNatSimulator(new UdpNatOptions
        {
            Upstream = new IPEndPoint(IPAddress.Loopback, stun.Port),
            AltUpstream = new IPEndPoint(IPAddress.Loopback, stun.AltEndpoint!.Port),
            PublicAddress = IPAddress.Loopback,
            AltPublicAddress = IPAddress.Parse("127.0.0.2"),
            StunPort = 0,
            AltStunPort = 0,
        });
        sim.RegisterClient(IPAddress.Parse("127.0.0.4"), mode, "A");
        await sim.StartAsync();
        _sims.Add(sim);
        return sim;
    }

    /// <summary>判型器直构（不经 ClientRuntime/LocalWebApi——STUN 面独立成链；
    /// bindAddress=127.0.0.4 为模拟器识别客户端 NAT 的探测源）。</summary>
    private static StunTester NewTester(
        Func<IPEndPoint?> udp, Func<IPEndPoint?> tcp, (Guid DeviceId, byte[] Secret) device) =>
        new(udp, tcp, () => new StunCredentials(device.DeviceId, device.Secret, new ClockSync()),
            IPAddress.Parse("127.0.0.4"));

    // ── 场景 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task 双地址FullCone_判型eim加eif_TCP依赖不敏感()
    {
        var device = await SeedDeviceAsync();
        var stun = await StartStunAsync(new IPEndPoint(IPAddress.Loopback, 0));
        var sim = await StartDualAddressSimAsync(stun, UdpNatMode.FullCone);

        // TCP 直连真实服务（不经 NAT 模拟器）：响应带 OTHER-ADDRESS → 同端口重绑比对分支可走，
        // 直连下两映射恒同 → portDependent=false（false 分支集成覆盖，TD-17 边界注记）
        var view = await NewTester(() => sim.StunEndpoint, () => new IPEndPoint(IPAddress.Loopback, stun.TcpPort),
            device).RunAsync();

        Assert.Equal("eim", view.UdpMapping);
        Assert.Equal("eif", view.UdpFiltering);
        Assert.False(view.Downgraded);
        Assert.StartsWith("127.0.0.1:", view.PublicEndpoint); // 映射身份=模拟器公网 socket（主地址）
        Assert.DoesNotContain(view.Notes, n => n.Contains("TCP 探测失败")); // 判定项丢失即回归信号
        Assert.NotNull(view.TcpPortDependent);
        Assert.False(view.TcpPortDependent.Value);
        Assert.Single(sim.Mappings); // 锥形：同一内部端点全程一条映射（探主/change/探辅同 socket）
    }

    [Fact]
    public async Task 双地址SymmetricRandom_判型adm加adf_change回包被过滤()
    {
        var device = await SeedDeviceAsync();
        var stun = await StartStunAsync(new IPEndPoint(IPAddress.Loopback, 0));
        var sim = await StartDualAddressSimAsync(stun, UdpNatMode.SymmetricRandom);

        var view = await NewTester(() => sim.StunEndpoint, () => null, device).RunAsync();

        Assert.Equal("adm_or_apdm", view.UdpMapping); // 探辅=新目的地新映射（随机端口≠主映射）
        Assert.Equal("adf_or_apdf", view.UdpFiltering); // change 回包源≠映射目的地 → NAT 丢弃 → 超时即判据
        Assert.False(view.Downgraded);
        Assert.Equal(2, sim.Mappings.Count); // 主/辅各一条（change 复用主映射同目的地）
        Assert.Contains(sim.Filtered, f => f.Sender == "stun-alt"); // 负向断言即时化：辅源回包确被过滤
    }

    [Fact]
    public async Task TCPSymmetricSequential_分配规律sequential()
    {
        var device = await SeedDeviceAsync();
        var tcpSim = new TcpNatSimulator(new TcpNatOptions { StunPort = 0, PortBase = 20500 });
        tcpSim.RegisterClient(IPAddress.Parse("127.0.0.4"), TcpNatMode.SymmetricSequential, "A");
        await tcpSim.StartAsync();
        _tcpSims.Add(tcpSim);

        var view = await NewTester(() => null, () => tcpSim.StunEndpoint, device).RunAsync();

        Assert.True(view.TcpSequential); // 导演端口 +1 递增：三连接 deltas 全 1（05 §3.2 端口预测适用）
        Assert.Null(view.UdpMapping);
        Assert.Contains(view.Notes, n => n.Contains("UDP STUN 端点不可用"));
    }

    [Fact]
    public async Task 无alt单地址_降级注记两维不可判()
    {
        var device = await SeedDeviceAsync();
        var stun = await StartStunAsync(alt: null);

        var view = await NewTester(() => new IPEndPoint(IPAddress.Loopback, stun.Port), () => null, device)
            .RunAsync();

        Assert.True(view.Downgraded);
        Assert.Null(view.UdpMapping);
        Assert.Null(view.UdpFiltering);
        Assert.StartsWith("127.0.0.4:", view.PublicEndpoint); // 直连：映射=本地端点原样
        Assert.Contains(view.Notes, n => n.Contains("stun_alt_addr 未配置"));
    }

    [Fact]
    public async Task 未注册_拒绝码1002()
    {
        var tester = new StunTester(() => new IPEndPoint(IPAddress.Loopback, 3478), () => null,
            credentialsLookup: () => null);
        var ex = await Assert.ThrowsAsync<StunTestException>(() => tester.RunAsync());
        Assert.Equal((int)ErrorCode.NotFound, ex.Code);
    }
}

using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using P2P.Client.Control;
using P2P.Client.Tunnel;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using P2P.Server.Data;
using P2P.Server.Services;
using Xunit;

namespace P2P.IntegrationTests;

/// <summary>
/// M2-17 RelayClient 对 in-proc 服务端集成测试（02 §6.1，TD-11）：0x74 分配（A 侧请求应答 /
/// B 侧 Grant 推送）→ RELAY_JOIN → 双向密文帧往返（转发字节逐字节相等——客户端侧佐证零解密）、
/// UDP/TCP 两承载变体、分配错误路径（1001）、JOIN 确认超时、90s 空闲自关闭。
/// </summary>
public sealed class RelayClientIntegrationTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    private readonly DeviceRegistry _registry = new();
    private readonly List<ControlClient> _clients = [];
    private readonly List<RelayTransport> _transports = [];
    private SignalingCoordinator _signaling = null!;
    private RelayService _relay = null!;
    private ControlServer _server = null!;
    private StubFactory _factory = null!;
    private int _port;
    private IPEndPoint _relayUdp = null!;

    public async Task InitializeAsync()
    {
        _factory = new StubFactory(CreateDb);
        using (var init = _factory.CreateDbContext())
            DbInitializer.Initialize(init);
        var audit = new AuditLogger(_factory);
        _signaling = new SignalingCoordinator(_factory, _registry, new Authorizer(_factory), audit);
        _relay = new RelayService(_factory, _registry, _signaling.ResolveRelayPeers);
        var router = new ControlMessageRouter(
            new RegistrationService(_factory, _registry, audit),
            new UserService(_factory, audit),
            new GroupService(_factory, _registry, audit),
            _signaling,
            new MappingService(_factory, audit),
            _relay,
            new StatsService(_factory, audit),
            audit);
        _server = new ControlServer(_factory, _registry, router.DispatchAsync);
        await _server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
        _port = _server.LocalEndPoint!.Port;
        await _relay.StartAsync(0, 0); // 双端口系统分配（Grant 端点=127.0.0.1:实际端口）
        _relayUdp = new IPEndPoint(IPAddress.Loopback, _relay.UdpEndpoint!.Port);
    }

    public async Task DisposeAsync()
    {
        foreach (var t in _transports) await t.DisposeAsync();
        foreach (var c in _clients) await c.DisposeAsync();
        await _server.DisposeAsync();
        await _relay.DisposeAsync();
        await _signaling.DisposeAsync();
        await Task.Delay(200); // Sqlite 拆除竞态宽限（同族测试已知瞬态）
        try { _db.Dispose(); }
        catch (InvalidOperationException) { }
    }

    private AppDbContext CreateDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_db.DataSource).Options);

    // ── 世界构建 ───────────────────────────────────────────────────────

    /// <summary>直接写库种已注册设备（凭据已知，绕过注册流；同 ControlClientIntegrationTests）。</summary>
    private async Task<(ControlClient Client, Guid DeviceId)> NewDeviceAsync(string name)
    {
        var secret = RandomGenerator.Bytes(32);
        var device = new Device
        {
            Id = Guid.NewGuid(),
            DeviceName = name,
            Os = "windows",
            ClientVersion = "0.1.0",
            MacCode = $"RL-{Guid.NewGuid():N}"[..16],
            RemoteCode = string.Concat(RandomGenerator.Bytes(6).Select(b => (char)('0' + b % 10))), // 唯一索引：随机 6 位
            VirtualIp = "100.64.0.2",
            StaticPubKey = new byte[65],
            DeviceSecret = secret,
            LastSeenAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
        };
        await using var db = _factory.CreateDbContext();
        db.Devices.Add(device);
        // 注册流程会自动入默认分组（RegistrationService），直写库须补——否则打洞 L2 不可见（l2_not_visible）
        db.GroupMembers.Add(new GroupMember
        {
            Id = Guid.NewGuid(),
            GroupId = db.Groups.Single(g => g.IsDefault).Id,
            DeviceId = device.Id,
            Approved = true,
            JoinedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var client = new ControlClient(new[] { $"127.0.0.1:{_port}" },
            new ControlClientOptions { HeartbeatInterval = TimeSpan.FromHours(1) },
            deviceId: device.Id, deviceSecret: secret);
        _clients.Add(client);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await client.WaitReadyAsync(cts.Token);
        return (client, device.Id);
    }

    /// <summary>打洞两段式（0x70→0x71→0x76→Ack）：B 侧 invite 经 ServerPush 到达（端点为替身公网值，
    /// 打洞本身不在本测试范围——只需会话完成入中继台账）。</summary>
    private static async Task<Guid> PunchAsync(ControlClient a, ControlClient b, Guid bId)
    {
        var inviteTcs = new TaskCompletionSource<PunchInvite>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPush(IPcpMessage m)
        {
            if (m is PunchInvite pi) inviteTcs.TrySetResult(pi);
        }
        b.ServerPush += OnPush;
        try
        {
            var ackTask = a.SendRequestAsync<PunchRequestAck>(new PunchRequest(
                a.NextSeq(), a.TimestampMs(), MsgType.PunchRequest, bId, null, "udp",
                new EndpointPair(new P2P.Core.Protocol.Endpoint("203.0.113.10", 50000), null), null));
            var invite = await inviteTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await b.SendAsync(new PunchEndpoint(b.NextSeq(), b.TimestampMs(), MsgType.PunchEndpoint,
                invite.SessionId, new EndpointPair(new P2P.Core.Protocol.Endpoint("198.51.100.20", 50001), null)));
            var ack = await ackTask;
            return ack.SessionId;
        }
        finally { b.ServerPush -= OnPush; }
    }

    /// <summary>B 侧等 Grant 推送（02 §6.1② 向双方下发）。</summary>
    private static async Task<RelayGrant> AwaitGrantAsync(ControlClient side)
    {
        var tcs = new TaskCompletionSource<RelayGrant>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPush(IPcpMessage m)
        {
            if (m is RelayGrant g) tcs.TrySetResult(g);
        }
        side.ServerPush += OnPush;
        try { return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { side.ServerPush -= OnPush; }
    }

    /// <summary>PTP 密文帧替身：随机字节（客户端侧零解密佐证——转发字节不变即可）。</summary>
    private static byte[] PtpCiphertext(int len) => RandomGenerator.Bytes(len);

    // ── UDP 承载全链路 ────────────────────────────────────────────────

    [Theory]
    [InlineData(RelayCarrier.Udp)]
    [InlineData(RelayCarrier.Tcp)]
    public async Task 中继全链路_分配加入_双向密文帧往返(RelayCarrier carrier)
    {
        var (a, _) = await NewDeviceAsync("rly-a");
        var (b, bId) = await NewDeviceAsync("rly-b");
        var punchId = await PunchAsync(a, b, bId);

        // A 侧 0x74：请求-应答（族类型配对 0x74）
        var grantBTask = AwaitGrantAsync(b);
        var grantA = await RelayClient.AllocateAsync(a, punchId);
        var grantB = await grantBTask;
        Assert.NotEqual(0UL, grantA.RelaySessionId);
        Assert.Equal(grantA.RelaySessionId, grantB.RelaySessionId); // 同一 relaySessionId
        var ep = carrier == RelayCarrier.Udp ? grantA.RelayEndpoints.Udp! : grantA.RelayEndpoints.Tcp!;
        Assert.Equal("127.0.0.1", ep.Host); // 端点 host=控制连接 LocalEndPoint 派生（M2-07）
        Assert.Equal(carrier == RelayCarrier.Udp ? _relay.UdpEndpoint!.Port : _relay.TcpEndpoint!.Port,
            ep.Port); // 端口=relay 实际双端口

        // 双端 RELAY_JOIN → 数据面通道
        var ta = await RelayClient.JoinAsync(grantA, carrier);
        var tb = await RelayClient.JoinAsync(grantB, carrier);
        _transports.Add(ta);
        _transports.Add(tb);

        // A → B：发送侧附加 8B / 接收侧即服务端剥离后的裸帧——密文逐字节相等
        var aToB = PtpCiphertext(96);
        await ta.SendAsync(aToB);
        Assert.Equal(aToB, await tb.ReceiveAsync());

        // B → A（双向）
        var bToA = PtpCiphertext(72);
        await tb.SendAsync(bToA);
        Assert.Equal(bToA, await ta.ReceiveAsync());

        // KEEPALIVE 语义：PTP 帧照常过通道（TunnelSession 20s 周期天然刷新，NET-72）
        var keep = PtpCiphertext(32);
        await ta.SendAsync(keep);
        Assert.Equal(keep, await tb.ReceiveAsync());
    }

    // ── 0x74 错误路径（客户端侧）──────────────────────────────────────

    /// <summary>M2-38 承载顺序：TCP 优先（公网实测 UDP 承载并发大流量丢 DATA/WINDOW/OPEN 帧无重传）。
    /// 双端点均健康时 JOIN 应落 TCP；UDP 为 7020 被封场景兜底（顺序反转，02 §6.2）。</summary>
    [Fact]
    public async Task 承载选择_双端点健康_优先TCP()
    {
        var (a, _) = await NewDeviceAsync("ord-a");
        var (b, bId) = await NewDeviceAsync("ord-b");
        var punchId = await PunchAsync(a, b, bId);
        var grantBTask = AwaitGrantAsync(b);
        var grantA = await RelayClient.AllocateAsync(a, punchId);
        var grantB = await grantBTask;

        var ta = await RelayClient.JoinWithCarrierFallbackAsync(grantA);
        _transports.Add(ta);
        var tb = await RelayClient.JoinWithCarrierFallbackAsync(grantB);
        _transports.Add(tb);

        Assert.Equal(_relay.TcpEndpoint!.Port, ta.RemoteEndPoint.Port); // JOIN 落 TCP 承载（M2-38）
        Assert.Equal(_relay.TcpEndpoint!.Port, tb.RemoteEndPoint.Port);
        var probe = PtpCiphertext(64);
        await ta.SendAsync(probe);
        Assert.Equal(probe, await tb.ReceiveAsync()); // TCP 承载上转发照常
    }

    [Fact]
    public async Task 分配_未知打洞会话_收1001()
    {
        var (a, _) = await NewDeviceAsync("unk-a");
        var ex = await Assert.ThrowsAsync<ControlErrorException>(
            () => RelayClient.AllocateAsync(a, Guid.NewGuid()));
        Assert.Equal(1001, ex.Code);
        Assert.Equal("session_unknown", ex.HttpLikeMsg);
    }

    // ── JOIN 确认超时（服务器对未知 sid 静默丢弃，M2-07 行为）──────────

    [Fact]
    public async Task 加入_会话不存在_确认超时失败()
    {
        var grant = new RelayGrant(1, 0, MsgType.RelayAllocate, 0xDEADBEEF01,
            new EndpointPair(new Endpoint("127.0.0.1", (ushort)_relayUdp.Port), null));
        var ex = await Assert.ThrowsAsync<IOException>(
            () => RelayClient.JoinAsync(grant, RelayCarrier.Udp, joinTimeout: TimeSpan.FromMilliseconds(800)));
        Assert.Contains("未确认", ex.Message);
    }

    // ── 90s 空闲自关闭（02 §6.2 服务端回收口径对齐）───────────────────

    [Fact]
    public async Task 空闲90秒_通道自关闭()
    {
        var (a, _) = await NewDeviceAsync("idle-a");
        var (b, bId) = await NewDeviceAsync("idle-b");
        var punchId = await PunchAsync(a, b, bId);
        var grantBTask = AwaitGrantAsync(b);
        var grantA = await RelayClient.AllocateAsync(a, punchId);
        var grantB = await grantBTask;

        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var ta = await RelayClient.JoinAsync(grantA, RelayCarrier.Udp, time: time);
        _transports.Add(ta);
        var tb = await RelayClient.JoinAsync(grantB, RelayCarrier.Udp, time: time);
        _transports.Add(tb);

        var frame = PtpCiphertext(48);
        await ta.SendAsync(frame); // 业务流量刷新空闲时钟
        Assert.Equal(frame, await tb.ReceiveAsync());

        time.Advance(TimeSpan.FromSeconds(106)); // 跨 15s 检查点：idle 106s > 90s
        // 防挂起护栏：watchdog 由 Advance 触发但续体在线程池——若未及关闭，5s 后 OCE 传播即失败
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Null(await ta.ReceiveAsync(guard.Token)); // 自关闭（承载关闭契约）
        Assert.Null(await tb.ReceiveAsync(guard.Token));
    }
}

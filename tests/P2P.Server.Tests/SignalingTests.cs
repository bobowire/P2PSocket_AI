using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using P2P.Server.Data;
using P2P.Server.Services;
using Xunit;

namespace P2P.Server.Tests;

/// <summary>
/// 两段式打洞信令测试（02 §5.1、OQ-18/TD-19；完成判定：授权通过全链路、L2 拒 4001+审计、
/// 同目标并发排队、B 端点上报前 Ack 不下发、B 离线 4005、超时 5001）。
/// </summary>
public sealed class SignalingTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DeviceRegistry _registry = new();
    private readonly List<TestPcpClient> _clients = [];
    private ControlServer _server = null!;
    private SignalingCoordinator _signaling = null!;
    private FakeTimeProvider _time = null!;

    public Task InitializeAsync()
    {
        _connection.Open();
        var factory = new StubFactory(CreateDb);
        using var init = factory.CreateDbContext();
        DbInitializer.Initialize(init);
        _time = new FakeTimeProvider(DateTimeOffset.UtcNow);

        var audit = new AuditLogger(factory, _time);
        _signaling = new SignalingCoordinator(factory, _registry, new Authorizer(factory), audit, _time);
        var router = new ControlMessageRouter(
            new RegistrationService(factory, _registry, audit, _time),
            new UserService(factory, audit, _time),
            new GroupService(factory, _registry, _time),
            _signaling,
            new MappingService(factory, audit),
            audit);
        _server = new ControlServer(factory, _registry, router.DispatchAsync, time: _time);
        return _server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
    }

    public async Task DisposeAsync()
    {
        foreach (var c in _clients) await c.DisposeAsync();
        await _server.DisposeAsync();
        await _signaling.DisposeAsync();
        _connection.Dispose();
    }

    private AppDbContext CreateDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);

    /// <summary>注册设备（默认组成员 → 双方可见，02 §2.4）。</summary>
    private async Task<(TestPcpClient Client, Guid DeviceId, string MacCode)> RegisterAsync(string name)
    {
        var client = await TestPcpClient.ConnectAsync(_server.LocalEndPoint!);
        _clients.Add(client);
        await client.HelloAsync(deviceId: null);
        var key = EcKeyPair.Generate();
        var mac = $"P2P-SIG{Guid.NewGuid():N}"[..14];
        await client.SendAsync(new Register(client.NextSeq(), client.Now(), MsgType.Register,
            mac, name, "windows", "0.1.0", key.ExportPublicKey(), null, null, null), sign: false);
        var ack = await client.ReceiveAsync<RegisterAck>() ?? throw new IOException("注册无应答");
        client.EstablishWithSecret(Ecies.Decrypt(key, ack.DeviceSecretBox));
        return (client, ack.DeviceId, mac);
    }

    private static EndpointPair UdpOnly(string host, ushort port) => new(new Endpoint(host, port), null);

    /// <summary>断言指定毫秒内无任何消息到达（Ack 延后/未启动语义）。</summary>
    private static async Task AssertSilentAsync(TestPcpClient client, int ms = 300)
    {
        try { await client.ReceiveAsync<HeartbeatAck>(ms); }
        catch (TimeoutException) { return; }
        throw new Xunit.Sdk.XunitException($"预期 {ms}ms 内无消息，却收到了");
    }

    // ── 完成判定①：授权通过全链路（0x70 → 0x71 → 0x76 → 0x70 Ack）────

    [Fact]
    public async Task Punch_Authorized_FullTwoPhaseFlow()
    {
        var (a, aId, _) = await RegisterAsync("initiator");
        var (b, bId, _) = await RegisterAsync("target");
        var aEndpoints = UdpOnly("203.0.113.10", 50000);

        await a.SendAsync(new PunchRequest(a.NextSeq(), a.Now(), MsgType.PunchRequest,
            bId, null, "udp", aEndpoints));

        // B 收 0x71：A 信息 + A 端点 + N + relayAllowed=false
        var invite = await b.ReceiveAsync<PunchInvite>() ?? throw new IOException("B 未收到 PunchInvite");
        Assert.Equal(aId, invite.Peer.DeviceId);
        Assert.Equal("initiator", invite.Peer.DeviceName);
        Assert.NotEmpty(invite.Peer.StaticPubKey);
        Assert.Equal(aEndpoints.Udp!.Host, invite.PeerEndpoints.Udp!.Host);
        Assert.Equal(aEndpoints.Udp.Port, invite.PeerEndpoints.Udp.Port);
        Assert.Equal(SignalingCoordinator.DefaultPunchCount, invite.PunchCount);
        Assert.False(invite.RelayAllowed);

        // B 回 0x76（即时 STUN 所得端点）
        var bEndpoints = UdpOnly("198.51.100.20", 50001);
        await b.SendAsync(new PunchEndpoint(b.NextSeq(), b.Now(), MsgType.PunchEndpoint,
            invite.SessionId, bEndpoints));

        // A 收延后 Ack：B 信息 + B 端点
        var ack = await a.ReceiveAsync<PunchRequestAck>() ?? throw new IOException("A 未收到延后 Ack");
        Assert.Equal(invite.SessionId, ack.SessionId);
        Assert.Equal(bId, ack.Peer.DeviceId);
        Assert.Equal("target", ack.Peer.DeviceName);
        Assert.Equal(bEndpoints.Udp!.Host, ack.PeerEndpoints.Udp!.Host);
        Assert.Equal(bEndpoints.Udp.Port, ack.PeerEndpoints.Udp.Port);
        Assert.False(ack.RelayAllowed);
    }

    // ── 完成判定②：L2 拒 4001 + punch_deny 审计 ──────────────────────

    [Fact]
    public async Task Punch_TargetNotVisible_Rejected4001AndAudited()
    {
        var (a, aId, _) = await RegisterAsync("isolated-a");
        var (b, bId, _) = await RegisterAsync("isolated-b");

        // B 移出默认组且不同账号 → 不可见（入组接口属 M2，直接写库构造）
        await using (var db = CreateDb())
        {
            var defaultGroupId = await db.Groups.AsNoTracking().Where(g => g.IsDefault).Select(g => g.Id).SingleAsync();
            var membership = await db.GroupMembers.SingleAsync(m => m.GroupId == defaultGroupId && m.DeviceId == bId);
            db.GroupMembers.Remove(membership);
            await db.SaveChangesAsync();
        }

        await a.SendAsync(new PunchRequest(a.NextSeq(), a.Now(), MsgType.PunchRequest,
            bId, null, "udp", null));
        var error = await a.ReceiveAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.TargetNotAuthorized, error!.Code);

        await using var db2 = CreateDb();
        Assert.True(await db2.AuditLogs.AsNoTracking()
            .AnyAsync(x => x.Event == "punch_deny" && x.DeviceId == aId));
    }

    // ── 完成判定③：同目标并发申请排队（per-device 单会话）────────────

    [Fact]
    public async Task Punch_ConcurrentRequestsToSameTarget_QueuedInOrder()
    {
        var (a1, _, _) = await RegisterAsync("queue-a1");
        var (a2, _, _) = await RegisterAsync("queue-a2");
        var (b, bId, _) = await RegisterAsync("queue-b");

        // 第一发：B 空闲 → 立即收到邀请
        await a1.SendAsync(new PunchRequest(a1.NextSeq(), a1.Now(), MsgType.PunchRequest,
            bId, null, "udp", UdpOnly("203.0.113.1", 40001)));
        var invite1 = await b.ReceiveAsync<PunchInvite>() ?? throw new IOException("第一次未收到邀请");

        // 第二发：B 忙 → 排队，不下发邀请、A2 无任何应答
        await a2.SendAsync(new PunchRequest(a2.NextSeq(), a2.Now(), MsgType.PunchRequest,
            bId, null, "udp", UdpOnly("203.0.113.2", 40002)));
        await AssertSilentAsync(a2);
        await AssertSilentAsync(b);

        // B 回 0x76 结束第一会话 → A1 得 Ack；排队项推进 → B 收第二次邀请
        await b.SendAsync(new PunchEndpoint(b.NextSeq(), b.Now(), MsgType.PunchEndpoint,
            invite1.SessionId, UdpOnly("198.51.100.1", 41001)));
        var ack1 = await a1.ReceiveAsync<PunchRequestAck>();
        Assert.Equal(invite1.SessionId, ack1!.SessionId);

        var invite2 = await b.ReceiveAsync<PunchInvite>() ?? throw new IOException("排队项未推进");
        Assert.NotEqual(invite1.SessionId, invite2.SessionId);
        Assert.Equal("queue-a2", invite2.Peer.DeviceName);

        await b.SendAsync(new PunchEndpoint(b.NextSeq(), b.Now(), MsgType.PunchEndpoint,
            invite2.SessionId, UdpOnly("198.51.100.2", 41002)));
        var ack2 = await a2.ReceiveAsync<PunchRequestAck>();
        Assert.Equal(invite2.SessionId, ack2!.SessionId);
    }

    // ── 完成判定④：B 端点上报前 A 无 Ack（延后语义）──────────────────

    [Fact]
    public async Task Punch_AckWithheld_UntilTargetEndpointReported()
    {
        var (a, _, _) = await RegisterAsync("wait-a");
        var (b, bId, _) = await RegisterAsync("wait-b");

        await a.SendAsync(new PunchRequest(a.NextSeq(), a.Now(), MsgType.PunchRequest,
            bId, null, "udp", UdpOnly("203.0.113.3", 40003)));
        _ = await b.ReceiveAsync<PunchInvite>(); // B 收到邀请但不上报

        await AssertSilentAsync(a); // 0x76 未达 → Ack 不下发
    }

    // ── 完成判定⑤：B 离线 → 4005 ─────────────────────────────────────

    [Fact]
    public async Task Punch_TargetOffline_Gets4005()
    {
        var (a, _, _) = await RegisterAsync("live-a");
        var (b, bId, _) = await RegisterAsync("gone-b");
        await b.DisposeAsync(); // TCP 关闭 → 服务端 EOF → CloseAsync 注销
        _clients.Remove(b);

        // 轮询等服务端完成注销（读循环收尾是异步的）
        var deadline = Environment.TickCount + 5000;
        while (_registry.IsOnline(bId) && Environment.TickCount < deadline)
            await Task.Delay(20);
        Assert.False(_registry.IsOnline(bId));

        await a.SendAsync(new PunchRequest(a.NextSeq(), a.Now(), MsgType.PunchRequest,
            bId, null, "udp", null));
        var error = await a.ReceiveAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.TargetOffline, error!.Code);
    }

    // ── 完成判定⑥：0x76 超 10s 未达 → 5001 ───────────────────────────

    [Fact]
    public async Task Punch_EndpointNotReportedInTime_TimesOutWith5001()
    {
        var (a, _, _) = await RegisterAsync("slow-a");
        var (b, bId, _) = await RegisterAsync("silent-b");

        await a.SendAsync(new PunchRequest(a.NextSeq(), a.Now(), MsgType.PunchRequest,
            bId, null, "udp", UdpOnly("203.0.113.4", 40004)));
        _ = await b.ReceiveAsync<PunchInvite>(); // B 收到邀请但永不回应

        _time.Advance(TimeSpan.FromSeconds(11)); // 超过 10s 会话超时
        var error = await a.ReceiveAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.PunchFailed, error!.Code);
    }
}

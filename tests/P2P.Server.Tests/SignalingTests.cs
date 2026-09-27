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
/// M2-19 增 0x73 PunchRetry 回切协调（台账命中双端通知/活中继反查兜底/非发起方 1001/
/// 未知会话 1001/目标离线 4005）。
/// </summary>
public sealed class SignalingTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DeviceRegistry _registry = new();
    private readonly List<TestPcpClient> _clients = [];
    private ControlServer _server = null!;
    private SignalingCoordinator _signaling = null!;
    private RelayService _relay = null!;
    private FakeTimeProvider _time = null!;

    public Task InitializeAsync()
    {
        _connection.Open();
        var factory = new StubFactory(CreateDb);
        using var init = factory.CreateDbContext();
        DbInitializer.Initialize(init);
        _time = new FakeTimeProvider(DateTimeOffset.UtcNow);

        var audit = new AuditLogger(factory, _time);
        // 活中继反查缝（M2-19 回切）：闭包捕获字段延迟解环——_relay 构造依赖 _signaling 台账委托
        _signaling = new SignalingCoordinator(factory, _registry, new Authorizer(factory), audit, _time,
            activeRelayLookup: id => _relay?.ResolveActiveRelay(id));
        _relay = new RelayService(factory, _registry, _signaling.ResolveRelayPeers,
            new RelayServiceOptions { IdleTimeout = TimeSpan.FromHours(1) }, // 台账过期用例推进假时钟 121s：中继条目须存活
            time: _time);
        var router = new ControlMessageRouter(
            new RegistrationService(factory, _registry, audit, _time),
            new UserService(factory, audit, _time),
            new GroupService(factory, _registry, audit, _time),
            _signaling,
            new MappingService(factory, audit),
            _relay,
            new StatsService(factory, audit),
            audit);
        _server = new ControlServer(factory, _registry, router.DispatchAsync, time: _time);
        return _server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
    }

    public async Task DisposeAsync()
    {
        foreach (var c in _clients) await c.DisposeAsync();
        await _server.DisposeAsync();
        await _relay.DisposeAsync();
        await _signaling.DisposeAsync();
        _connection.Dispose();
    }

    private AppDbContext CreateDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);

    /// <summary>注册设备（默认组成员 → 双方可见，02 §2.4）。时间戳对齐假时钟（M2-19 回切用例
    /// Advance 121s 后 0x73 仍须落在 TsWindow 内——真实时钟会偏出 ±30s 被拒 5005）。</summary>
    private async Task<(TestPcpClient Client, Guid DeviceId, string MacCode)> RegisterAsync(string name)
    {
        var client = await TestPcpClient.ConnectAsync(_server.LocalEndPoint!);
        client.Time = _time;
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
            bId, null, "udp", aEndpoints, null));

        // B 收 0x71：A 信息 + A 端点 + N + relayAllowed=false
        var invite = await b.ReceiveAsync<PunchInvite>() ?? throw new IOException("B 未收到 PunchInvite");
        Assert.Equal(aId, invite.Peer.DeviceId);
        Assert.Equal("initiator", invite.Peer.DeviceName);
        Assert.NotEmpty(invite.Peer.StaticPubKey);
        Assert.Equal(aEndpoints.Udp!.Host, invite.PeerEndpoints.Udp!.Host);
        Assert.Equal(aEndpoints.Udp.Port, invite.PeerEndpoints.Udp.Port);
        Assert.Equal(PunchPolicy.DefaultConcurrency, invite.PunchCount); // 未携带 → 缺省 3（OQ-19）
        Assert.True(invite.RelayAllowed); // 种子 relay_enabled=1 → 真实合成（M2-07）

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
        Assert.True(ack.RelayAllowed);
    }

    // ── M2-07：relay_enabled=0 → relayAllowed=false 全链路 ─────────────

    [Fact]
    public async Task Punch_RelayDisabled_FlagFalseInInviteAndAck()
    {
        await using (var db = CreateDb())
        {
            db.ServerConfig.Single(c => c.Key == "relay_enabled").Value = "0";
            await db.SaveChangesAsync();
        }

        var (a, _, _) = await RegisterAsync("relay-off-a");
        var (b, bId, _) = await RegisterAsync("relay-off-b");

        await a.SendAsync(new PunchRequest(a.NextSeq(), a.Now(), MsgType.PunchRequest,
            bId, null, "udp", null, null));
        var invite = await b.ReceiveAsync<PunchInvite>() ?? throw new IOException("B 未收到 PunchInvite");
        Assert.False(invite.RelayAllowed);

        await b.SendAsync(new PunchEndpoint(b.NextSeq(), b.Now(), MsgType.PunchEndpoint,
            invite.SessionId, UdpOnly("198.51.100.30", 50002)));
        var ack = await a.ReceiveAsync<PunchRequestAck>() ?? throw new IOException("A 未收到延后 Ack");
        Assert.False(ack.RelayAllowed);
    }

    // ── M2-16 OQ-19/TD-20：punchConcurrency 取请求值校验回填（0x71/0x70 Ack 同值）──

    [Theory]
    [InlineData(5, 5)]   // 合法上限
    [InlineData(1, 1)]   // 合法下限
    [InlineData(0, 3)]   // 越界 → 缺省（容忍，PunchPolicy.Normalize）
    [InlineData(6, 3)]
    [InlineData(255, 3)]
    public async Task Punch_ConcurrencyNormalized_BackfilledInInviteAndAck(byte requested, byte expected)
    {
        var (a, _, _) = await RegisterAsync("con-a");
        var (b, bId, _) = await RegisterAsync("con-b");

        await a.SendAsync(new PunchRequest(a.NextSeq(), a.Now(), MsgType.PunchRequest,
            bId, null, "tcp", UdpOnly("203.0.113.10", 50000), requested));
        var invite = await b.ReceiveAsync<PunchInvite>() ?? throw new IOException("B 未收到 PunchInvite");
        Assert.Equal(expected, invite.PunchCount);

        await b.SendAsync(new PunchEndpoint(b.NextSeq(), b.Now(), MsgType.PunchEndpoint,
            invite.SessionId, UdpOnly("198.51.100.20", 50001)));
        var ack = await a.ReceiveAsync<PunchRequestAck>() ?? throw new IOException("A 未收到延后 Ack");
        Assert.Equal(expected, ack.PunchCount); // 双方该次打洞执行同一 N（02 §5.2② 对称性硬约束）
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
            bId, null, "udp", null, null));
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
            bId, null, "udp", UdpOnly("203.0.113.1", 40001), null));
        var invite1 = await b.ReceiveAsync<PunchInvite>() ?? throw new IOException("第一次未收到邀请");

        // 第二发：B 忙 → 排队，不下发邀请、A2 无任何应答
        await a2.SendAsync(new PunchRequest(a2.NextSeq(), a2.Now(), MsgType.PunchRequest,
            bId, null, "udp", UdpOnly("203.0.113.2", 40002), null));
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
            bId, null, "udp", UdpOnly("203.0.113.3", 40003), null));
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
            bId, null, "udp", null, null));
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
            bId, null, "udp", UdpOnly("203.0.113.4", 40004), null));
        _ = await b.ReceiveAsync<PunchInvite>(); // B 收到邀请但永不回应

        _time.Advance(TimeSpan.FromSeconds(11)); // 超过 10s 会话超时
        var error = await a.ReceiveAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.PunchFailed, error!.Code);
    }

    // ── M2-19：0x73 PunchRetry 回切协调（02 §6.2/OQ-7）────────────────

    /// <summary>两段式打到台账在册（供 0x73 用例取 sessionId；A 收延后 Ack）。</summary>
    private async Task<Guid> PunchToLedgerAsync(TestPcpClient a, TestPcpClient b, Guid bId)
    {
        await a.SendAsync(new PunchRequest(a.NextSeq(), a.Now(), MsgType.PunchRequest,
            bId, null, "udp", UdpOnly("203.0.113.10", 50000), null));
        var invite = await b.ReceiveAsync<PunchInvite>() ?? throw new IOException("B 未收到 PunchInvite");
        await b.SendAsync(new PunchEndpoint(b.NextSeq(), b.Now(), MsgType.PunchEndpoint,
            invite.SessionId, UdpOnly("198.51.100.20", 50001)));
        var ack = await a.ReceiveAsync<PunchRequestAck>() ?? throw new IOException("A 未收到延后 Ack");
        return ack.SessionId;
    }

    [Fact]
    public async Task Retry_LedgerHit_BothEndsNotifiedWithOriginalSessionId()
    {
        var (a, _, _) = await RegisterAsync("retry-a");
        var (b, bId, _) = await RegisterAsync("retry-b");
        var sessionId = await PunchToLedgerAsync(a, b, bId);

        await a.SendAsync(new PunchRetry(a.NextSeq(), a.Now(), MsgType.PunchRetry, sessionId));

        // A 侧同族 0x73 应答（受理回执）+ B 侧推送（重打预备通知）：载荷均为原 sessionId（02 §6.2）
        var ackA = await a.ReceiveAsync<PunchRetry>() ?? throw new IOException("A 未收到回切应答");
        Assert.Equal(sessionId, ackA.SessionId);
        var pushB = await b.ReceiveAsync<PunchRetry>() ?? throw new IOException("B 未收到重打通知");
        Assert.Equal(sessionId, pushB.SessionId);
    }

    [Fact]
    public async Task Retry_LedgerExpired_ActiveRelayFallback_StillResolved()
    {
        var (a, _, _) = await RegisterAsync("relay-lookup-a");
        var (b, bId, _) = await RegisterAsync("relay-lookup-b");
        var sessionId = await PunchToLedgerAsync(a, b, bId);

        // 中继会话建立（0x74：分配即记录 PunchSessionId，M2-19 反查依据）
        await a.SendAsync(new RelayAllocate(a.NextSeq(), a.Now(), MsgType.RelayAllocate, sessionId));
        _ = await a.ReceiveAsync<RelayGrant>();
        _ = await b.ReceiveAsync<RelayGrant>();

        _time.Advance(TimeSpan.FromSeconds(121)); // 台账（120s）过期；中继条目存活（IdleTimeout=1h）

        await a.SendAsync(new PunchRetry(a.NextSeq(), a.Now(), MsgType.PunchRetry, sessionId));
        var ackA = await a.ReceiveAsync<PunchRetry>() ?? throw new IOException("活中继反查未生效");
        Assert.Equal(sessionId, ackA.SessionId);
        _ = await b.ReceiveAsync<PunchRetry>();
    }

    [Fact]
    public async Task Retry_NonInitiator_Rejected1001()
    {
        var (a, _, _) = await RegisterAsync("retry-ni-a");
        var (b, bId, _) = await RegisterAsync("retry-ni-b");
        var sessionId = await PunchToLedgerAsync(a, b, bId);

        // 被邀请方（非发起方）不可触发回切协调：1001（防双端同时重打，02 §6.2）
        await b.SendAsync(new PunchRetry(b.NextSeq(), b.Now(), MsgType.PunchRetry, sessionId));
        var error = await b.ReceiveAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.BadRequest, error!.Code);
    }

    [Fact]
    public async Task Retry_UnknownSession_Rejected1001()
    {
        var (a, _, _) = await RegisterAsync("retry-unk-a");

        await a.SendAsync(new PunchRetry(a.NextSeq(), a.Now(), MsgType.PunchRetry, Guid.NewGuid()));
        var error = await a.ReceiveAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.BadRequest, error!.Code);
    }

    [Fact]
    public async Task Retry_TargetOffline_Gets4005()
    {
        var (a, _, _) = await RegisterAsync("retry-off-a");
        var (b, bId, _) = await RegisterAsync("retry-off-b");
        var sessionId = await PunchToLedgerAsync(a, b, bId);
        await b.DisposeAsync();
        _clients.Remove(b);

        var deadline = Environment.TickCount + 5000;
        while (_registry.IsOnline(bId) && Environment.TickCount < deadline)
            await Task.Delay(20);

        await a.SendAsync(new PunchRetry(a.NextSeq(), a.Now(), MsgType.PunchRetry, sessionId));
        var error = await a.ReceiveAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.TargetOffline, error!.Code);
    }
}

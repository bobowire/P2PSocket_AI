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
/// 打洞与状态统计闭环（M2-08，FR-S-503/FR-C-404、03 §2.6/§2.9、OQ-17）：
/// 0x72 三种结果（direct=Ok+端点 / relay=Ok 无端点 / failed=!Ok）落库字段齐全且取台账上下文
/// （proto/N/时长），sessionId 去重、未知会话/非发起方 drop+审计；0x62 本人映射 → 审计流水；
/// 0x64 覆盖式 upsert 幂等（重复/乱序不叠加，最新到达=客户端当前累计）。
/// 三上报均无 Ack（fire-and-forget）：以 Heartbeat 往返作同步栅——连接内逐帧串行处理，
/// Ack 到达即此前全部消息已处理完（避免与共享连接上的服务端写并发：SQLite 单连接不允许）。
/// </summary>
public sealed class StatsTests : IAsyncLifetime
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
        _signaling = new SignalingCoordinator(factory, _registry, new Authorizer(factory), audit, _time,
            activeRelayLookup: id => _relay?.ResolveActiveRelay(id));
        _relay = new RelayService(factory, _registry, _signaling.ResolveRelayPeers,
            new RelayServiceOptions { IdleTimeout = TimeSpan.FromHours(1) }, time: _time);
        var router = new ControlMessageRouter(
            new RegistrationService(factory, _registry, audit, _time),
            new UserService(factory, audit, _time),
            new GroupService(factory, _registry, audit, time: _time),
            _signaling,
            new MappingService(factory, audit),
            _relay,
            new StatsService(factory, audit, _time),
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

    /// <summary>注册设备（默认组成员 → 双方可见）；时间戳对齐假时钟（TsWindow）。</summary>
    private async Task<(TestPcpClient Client, Guid DeviceId)> RegisterAsync(string name)
    {
        var client = await TestPcpClient.ConnectAsync(_server.LocalEndPoint!);
        client.Time = _time;
        _clients.Add(client);
        await client.HelloAsync(deviceId: null);
        var key = EcKeyPair.Generate();
        var mac = $"P2P-STA{Guid.NewGuid():N}"[..14];
        await client.SendAsync(new Register(client.NextSeq(), client.Now(), MsgType.Register,
            mac, name, "windows", "0.1.0", key.ExportPublicKey(), null, null, null), sign: false);
        var ack = await client.ReceiveAsync<RegisterAck>() ?? throw new IOException("注册无应答");
        client.EstablishWithSecret(Ecies.Decrypt(key, ack.DeviceSecretBox));
        return (client, ack.DeviceId);
    }

    /// <summary>同步栅：Heartbeat 往返——Ack 到达时该连接此前发送的全部消息已处理完毕。</summary>
    private static async Task SyncAsync(TestPcpClient client)
    {
        await client.SendAsync(new Heartbeat(client.NextSeq(), client.Now(), MsgType.Heartbeat));
        Assert.NotNull(await client.ReceiveAsync<HeartbeatAck>());
    }

    /// <summary>跑完两段式信令（0x70→0x71→0x76→延后 Ack）返回 sessionId——会话落台账供 0x72 解析。</summary>
    private async Task<Guid> PunchFlowAsync(TestPcpClient a, TestPcpClient b, Guid bId,
        string proto = "udp", byte? n = null)
    {
        await a.SendAsync(new PunchRequest(a.NextSeq(), a.Now(), MsgType.PunchRequest,
            bId, null, proto, UdpOnly("203.0.113.10", 50000), n));
        var invite = await b.ReceiveAsync<PunchInvite>() ?? throw new IOException("B 未收到 PunchInvite");
        await b.SendAsync(new PunchEndpoint(b.NextSeq(), b.Now(), MsgType.PunchEndpoint,
            invite.SessionId, UdpOnly("198.51.100.20", 50001)));
        var ack = await a.ReceiveAsync<PunchRequestAck>() ?? throw new IOException("A 未收到延后 Ack");
        return ack.SessionId;
    }

    /// <summary>0x60 建映射（A→B，tcp）返回 mappingId。</summary>
    private async Task<Guid> CreateMappingAsync(TestPcpClient a, Guid bId, ushort localPort = 18080)
    {
        string remoteCode;
        await using (var db = CreateDb())
            remoteCode = db.Devices.AsNoTracking().Single(d => d.Id == bId).RemoteCode;

        await a.SendAsync(new MappingUpsert(a.NextSeq(), a.Now(), MsgType.MappingUpsert,
            null, "m1", localPort, "tcp", remoteCode, "self", 8080, true));
        var ack = await a.ReceiveAsync<MappingUpsertAck>() ?? throw new IOException("映射无应答");
        return ack.MappingId;
    }

    private static EndpointPair UdpOnly(string host, ushort port) => new(new Endpoint(host, port), null);

    // ── 完成判定①：0x72 三种结果落库字段齐全（结果/proto/N/时长/双方）────────

    [Fact]
    public async Task PunchResult_Direct_FullLedgerContextLogged()
    {
        var (a, aId) = await RegisterAsync("stats-a");
        var (b, bId) = await RegisterAsync("stats-b");
        var sid = await PunchFlowAsync(a, b, bId, proto: "udp", n: null); // 未携带 → 缺省 3（OQ-19）
        _time.Advance(TimeSpan.FromSeconds(2)); // 打洞耗时入 duration_ms

        await a.SendAsync(new PunchResult(a.NextSeq(), a.Now(), MsgType.PunchResult,
            sid, true, new Endpoint("203.0.113.10", 50000), null));
        await SyncAsync(a);

        await using var db = CreateDb();
        var row = await db.PunchStats.AsNoTracking().SingleAsync(p => p.SessionId == sid);
        Assert.Equal(aId, row.InitiatorId);
        Assert.Equal(bId, row.TargetId);
        Assert.Equal("udp", row.Proto);
        Assert.Equal(3, row.Concurrency);
        Assert.Equal("direct", row.Result);
        Assert.Null(row.Reason);
        Assert.InRange(row.DurationMs, 2000, int.MaxValue); // 台账 CreatedAt→上报到达
        Assert.Equal(_time.GetLocalNow().UtcDateTime, row.Ts, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task PunchResult_Relay_OkWithoutEndpointReasonKept()
    {
        var (a, _) = await RegisterAsync("relay-a");
        var (b, bId) = await RegisterAsync("relay-b");
        var sid = await PunchFlowAsync(a, b, bId, proto: "tcp", n: 5);

        await a.SendAsync(new PunchResult(a.NextSeq(), a.Now(), MsgType.PunchResult,
            sid, true, null, "listener_refused"));
        await SyncAsync(a);

        await using var db = CreateDb();
        var row = await db.PunchStats.AsNoTracking().SingleAsync(p => p.SessionId == sid);
        Assert.Equal("relay", row.Result);
        Assert.Equal("tcp", row.Proto);
        Assert.Equal(5, row.Concurrency);
        Assert.Equal("listener_refused", row.Reason); // 回退原因随行保留
    }

    [Fact]
    public async Task PunchResult_Failed_OkFalse()
    {
        var (a, _) = await RegisterAsync("fail-a");
        var (b, bId) = await RegisterAsync("fail-b");
        var sid = await PunchFlowAsync(a, b, bId);

        await a.SendAsync(new PunchResult(a.NextSeq(), a.Now(), MsgType.PunchResult,
            sid, false, null, "invite_timeout"));
        await SyncAsync(a);

        await using var db = CreateDb();
        var row = await db.PunchStats.AsNoTracking().SingleAsync(p => p.SessionId == sid);
        Assert.Equal("failed", row.Result);
        Assert.Equal("invite_timeout", row.Reason);
    }

    // ── 去重与非法上报 ─────────────────────────────────────────────────────

    [Fact]
    public async Task PunchResult_Duplicate_SecondIgnored()
    {
        var (a, _) = await RegisterAsync("dup-a");
        var (b, bId) = await RegisterAsync("dup-b");
        var sid = await PunchFlowAsync(a, b, bId);
        await a.SendAsync(new PunchResult(a.NextSeq(), a.Now(), MsgType.PunchResult, sid, true, null, null));
        await a.SendAsync(new PunchResult(a.NextSeq(), a.Now(), MsgType.PunchResult, sid, false, null, "late"));
        await SyncAsync(a);

        await using var db = CreateDb();
        var row = Assert.Single(await db.PunchStats.AsNoTracking().Where(p => p.SessionId == sid).ToListAsync());
        Assert.Equal("relay", row.Result); // 首份结果即定论
    }

    [Fact]
    public async Task PunchResult_UnknownSession_DroppedWithAudit()
    {
        var (a, _) = await RegisterAsync("unknown-a");
        await a.SendAsync(new PunchResult(a.NextSeq(), a.Now(), MsgType.PunchResult,
            Guid.NewGuid(), false, null, "whatever"));
        await SyncAsync(a);

        await using var db = CreateDb();
        Assert.False(await db.PunchStats.AsNoTracking().AnyAsync()); // 无台账上下文不落行
        var audit = await db.AuditLogs.AsNoTracking()
            .SingleAsync(x => x.Event == "punch_result_unknown");
        Assert.NotNull(audit.Detail);
    }

    [Fact]
    public async Task PunchResult_FromTarget_Dropped()
    {
        var (a, _) = await RegisterAsync("tgt-a");
        var (b, bId) = await RegisterAsync("tgt-b");
        var sid = await PunchFlowAsync(a, b, bId);

        await b.SendAsync(new PunchResult(b.NextSeq(), b.Now(), MsgType.PunchResult,
            sid, true, new Endpoint("198.51.100.20", 50001), null));
        await SyncAsync(b);

        await using var db = CreateDb();
        Assert.False(await db.PunchStats.AsNoTracking().AnyAsync()); // 仅发起方可报
    }

    // ── 完成判定③：0x64 覆盖式 upsert 幂等（重复/乱序）────────────────────

    [Fact]
    public async Task StatsReport_RepeatIdempotent_LastReportWins()
    {
        var (a, _) = await RegisterAsync("report-a");
        var (b, bId) = await RegisterAsync("report-b");
        var mappingId = await CreateMappingAsync(a, bId);

        async Task SendAsync(ulong up, ulong down, ulong relay) =>
            await a.SendAsync(new StatsReport(a.NextSeq(), a.Now(), MsgType.StatsReport,
                [new StatsEntry(mappingId, up, down, relay)]));

        await SendAsync(100, 200, 50);
        await SendAsync(100, 200, 50);   // 重发：不叠加
        await SendAsync(300, 400, 60);
        await SendAsync(150, 250, 55);   // 乱序迟到旧值：最新到达=客户端当前累计
        await SyncAsync(a);

        await using var db = CreateDb();
        Assert.Single(await db.MappingStats.AsNoTracking().ToListAsync()); // 单行不重复
        var row = await db.MappingStats.AsNoTracking().SingleAsync(s => s.MappingId == mappingId);
        Assert.Equal(150, row.BytesUp);
        Assert.Equal(250, row.BytesDown);
        Assert.Equal(55, row.RelayBytes);
    }

    [Fact]
    public async Task StatsReport_ForeignOrUnknownEntriesSkipped()
    {
        var (a, _) = await RegisterAsync("foreign-a");
        var (_, bId) = await RegisterAsync("foreign-b");
        var (other, _) = await RegisterAsync("foreign-c");
        var mappingId = await CreateMappingAsync(a, bId);

        // 同报告：未知 mappingId + 他人视角的合法 mappingId → 逐项跳过，不落任何行
        await a.SendAsync(new StatsReport(a.NextSeq(), a.Now(), MsgType.StatsReport,
            [new StatsEntry(Guid.NewGuid(), 1, 1, 1)]));
        await other.SendAsync(new StatsReport(other.NextSeq(), other.Now(), MsgType.StatsReport,
            [new StatsEntry(mappingId, 999, 999, 999)]));
        await SyncAsync(a);
        await SyncAsync(other);

        await using (var db = CreateDb())
            Assert.False(await db.MappingStats.AsNoTracking().AnyAsync());

        // 本人上报正常落行（对照）
        await a.SendAsync(new StatsReport(a.NextSeq(), a.Now(), MsgType.StatsReport,
            [new StatsEntry(mappingId, 10, 20, 5)]));
        await SyncAsync(a);
        await using (var db = CreateDb())
            Assert.True(await db.MappingStats.AsNoTracking().AnyAsync(s => s.MappingId == mappingId));
    }

    // ── 0x62 映射状态 → 审计流水（客户端状态机为真相源，服务端留观测轨迹）────

    [Fact]
    public async Task MappingStatus_OwnedMappingAudited_ForeignIgnored()
    {
        var (a, aId) = await RegisterAsync("status-a");
        var (b, bId) = await RegisterAsync("status-b");
        var mappingId = await CreateMappingAsync(a, bId);

        await a.SendAsync(new MappingStatus(a.NextSeq(), a.Now(), MsgType.MappingStatus,
            mappingId, "relay", "fallback:symmetric_nat"));
        await SyncAsync(a);
        await b.SendAsync(new MappingStatus(b.NextSeq(), b.Now(), MsgType.MappingStatus,
            mappingId, "direct", null)); // 他人映射：忽略
        await SyncAsync(b);

        await using var db = CreateDb();
        var row = Assert.Single(await db.AuditLogs.AsNoTracking()
            .Where(x => x.Event == "mapping_status").ToListAsync());
        Assert.Equal(aId, row.DeviceId);
        Assert.Contains("\"relay\"", row.Detail);
        Assert.Contains("fallback:symmetric_nat", row.Detail);
    }
}

/// <summary>
/// 保留清理（M2-08，OQ-17、03 §2.7/§2.9）：只删过期行、punch_retention_days 键生效、
/// 启动轮 + 周期轮两路径。文件库 + 每上下文独立连接（WAL）：后台轮与断言查询可安全并发。
/// </summary>
public sealed class RetentionCleanerTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"p2p-ret-{Guid.NewGuid():N}.db");
    private IDbContextFactory<AppDbContext> _factory = null!;
    private FakeTimeProvider _time = null!;

    public Task InitializeAsync()
    {
        _factory = ServerDatabase.CreateFactory(_dbPath);
        using var init = _factory.CreateDbContext();
        DbInitializer.Initialize(init);
        _time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { File.Delete(_dbPath); } catch (IOException) { /* 临时文件残留无害 */ }
        return Task.CompletedTask;
    }

    private PunchStat Row(int ageDays, string result = "direct") => new()
    {
        Ts = _time.GetLocalNow().UtcDateTime.AddDays(ageDays),
        SessionId = Guid.NewGuid(),
        InitiatorId = Guid.NewGuid(),
        TargetId = Guid.NewGuid(),
        Proto = "udp",
        Concurrency = 3,
        Result = result,
        DurationMs = 1,
    };

    [Fact]
    public async Task Retention_ExpiredRowsDeleted_FreshKept_ConfigHonored()
    {
        await using var cleaner = new RetentionCleaner(_factory, _time);
        await cleaner.StartupRound; // 启动轮确定性完成（此时库中无旧行）

        // punch_stats FK → devices（Restrict）：真实设备行
        Guid devId;
        using (var db = _factory.CreateDbContext())
        {
            devId = Guid.NewGuid();
            db.Devices.Add(new Device
            {
                Id = devId, MacCode = $"P2P-RET{Guid.NewGuid():N}"[..14], DeviceName = "ret-dev",
                Os = "windows", ClientVersion = "0.1.0", StaticPubKey = [],
                RemoteCode = "R00000", CreatedAt = _time.GetLocalNow().UtcDateTime,
            });
            await db.SaveChangesAsync();
        }

        using (var db = _factory.CreateDbContext())
        {
            foreach (var r in new[] { Row(-100), Row(-40), Row(-20) })
            {
                r.InitiatorId = devId;
                r.TargetId = devId;
                db.PunchStats.Add(r);
            }
            db.AuditLogs.Add(new AuditLog { Ts = _time.GetLocalNow().UtcDateTime.AddDays(-100), Event = "old_audit", DeviceId = devId });
            db.AuditLogs.Add(new AuditLog { Ts = _time.GetLocalNow().UtcDateTime.AddDays(-5), Event = "new_audit", DeviceId = devId });
            db.ServerConfig.Single(c => c.Key == "punch_retention_days").Value = "30"; // 键生效验证
            db.SaveChanges();
        }

        await cleaner.RunOnceAsync();

        using (var db = _factory.CreateDbContext())
        {
            var row = Assert.Single(db.PunchStats.AsNoTracking().ToList()); // -100d（>90d 默认窗）与 -40d（>30d 配置窗）删，-20d 留
            Assert.Equal(-20, (row.Ts - _time.GetLocalNow().UtcDateTime).TotalDays, 1);
            var audits = db.AuditLogs.AsNoTracking().Select(x => x.Event).ToList();
            Assert.DoesNotContain("old_audit", audits); // audit_retention_days=90：-100d 删
            Assert.Contains("new_audit", audits);       // -5d 留
        }
    }

    [Fact]
    public async Task Retention_PeriodicRound_FiresAfterInterval()
    {
        await using var cleaner = new RetentionCleaner(_factory, _time,
            interval: TimeSpan.FromMilliseconds(200)); // 周期轮路径（生产 24h）
        await cleaner.StartupRound;

        using (var db = _factory.CreateDbContext())
        {
            var devId = Guid.NewGuid();
            db.Devices.Add(new Device
            {
                Id = devId, MacCode = $"P2P-RET{Guid.NewGuid():N}"[..14], DeviceName = "loop-dev",
                Os = "windows", ClientVersion = "0.1.0", StaticPubKey = [],
                RemoteCode = "R00001", CreatedAt = _time.GetLocalNow().UtcDateTime,
            });
            var old = Row(-100);
            old.InitiatorId = devId;
            old.TargetId = devId;
            db.PunchStats.Add(old); // FK → devices
            db.SaveChanges();
        }

        _time.Advance(TimeSpan.FromMilliseconds(300)); // PeriodicTimer 走假时钟：推进过首个周期

        for (var i = 0; i < 50; i++) // 周期轮异步落删：轮询至多 ~2.5s（文件库 WAL 并发安全）
        {
            using var db = _factory.CreateDbContext();
            if (!db.PunchStats.AsNoTracking().Any())
                return;
            await Task.Delay(50);
        }
        Assert.Fail("周期轮未清理过期行");
    }
}

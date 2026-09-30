using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using P2P.Server.Data;
using P2P.Server.Services;
using Xunit;

namespace P2P.Server.Tests;

/// <summary>
/// M2-11 内网段白名单与 L3 授权（05 §2.5、FR-C-701/702、SEC-52/53）。
/// 覆盖：0x63 三分支（新建规范化/更新/移除）+ passive 允许 + 归属校验；L3 双路径
/// （0x60 映射 upsert、0x70 打洞 TriggerMappingId 现值校验 + 伪造 1002）；
/// 移除联动 0x75(lan_segment_removed) 仅携受影响映射。夹具不装 pusher（无 0x41 交错）。
/// </summary>
public sealed class LanSegmentTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    private readonly DeviceRegistry _registry = new();
    private readonly List<TestPcpClient> _clients = [];
    private ControlServer _server = null!;
    private SignalingCoordinator _signaling = null!;
    private RelayService _relay = null!;

    public Task InitializeAsync()
    {
        var factory = new StubFactory(CreateDb);
        using var init = factory.CreateDbContext();
        DbInitializer.Initialize(init);

        var audit = new AuditLogger(factory);
        _signaling = new SignalingCoordinator(factory, _registry, new Authorizer(factory), audit);
        _relay = new RelayService(factory, _registry, _signaling.ResolveRelayPeers);
        var router = new ControlMessageRouter(
            new RegistrationService(factory, _registry, audit),
            new UserService(factory, audit),
            new GroupService(factory, _registry, audit),
            _signaling,
            new MappingService(factory, audit),
            _relay,
            new StatsService(factory, audit),
            audit,
            new LanSegmentService(factory, _registry, audit)); // M2-11 0x63
        _server = new ControlServer(factory, _registry, router.DispatchAsync);
        return _server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
    }

    public async Task DisposeAsync()
    {
        await _relay.DisposeAsync();
        foreach (var c in _clients) await c.DisposeAsync();
        await _server.DisposeAsync();
        await _signaling.DisposeAsync();
        _db.Dispose();
    }

    private AppDbContext CreateDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_db.DataSource).Options);

    private async Task<(TestPcpClient Client, Guid DeviceId)> ConnectLoggedInAsync(string deviceName)
    {
        var (client, deviceId) = await ConnectRegisteredAsync(deviceName);
        await client.SendAsync(new UserLogin(client.NextSeq(), client.Now(), MsgType.UserLogin,
            DbInitializer.AdminUsername, DbInitializer.AdminUsername));
        var login = await client.ReceiveAsync<UserLoginAck>();
        Assert.True(login!.Ok);
        return (client, deviceId);
    }

    private async Task<(TestPcpClient Client, Guid DeviceId)> ConnectRegisteredAsync(string deviceName)
    {
        var client = await TestPcpClient.ConnectAsync(_server.LocalEndPoint!);
        _clients.Add(client);
        await client.HelloAsync(deviceId: null);
        var key = EcKeyPair.Generate();
        await client.SendAsync(new Register(client.NextSeq(), client.Now(), MsgType.Register,
            $"P2P-{Guid.NewGuid():N}"[..16], deviceName, "windows", "0.1.0",
            key.ExportPublicKey(), null, null, null), sign: false);
        var ack = await client.ReceiveAsync<RegisterAck>() ?? throw new IOException("注册无应答");
        client.EstablishWithSecret(Ecies.Decrypt(key, ack.DeviceSecretBox));
        return (client, ack.DeviceId);
    }

    // ── 0x63 白名单 CRUD ───────────────────────────────────────────────

    private async Task<Guid> CreateSegmentAsync(TestPcpClient client, string cidr)
    {
        await client.SendAsync(new LanSegmentsUpsert(client.NextSeq(), client.Now(),
            MsgType.LanSegmentsUpsert, null, cidr, true));
        var ack = await client.ReceiveAsync<LanSegmentsUpsertAck>() ?? throw new IOException("0x63 无应答");
        return ack.SegmentId;
    }

    private async Task UpdateSegmentAsync(TestPcpClient client, Guid segmentId, string cidr)
    {
        await client.SendAsync(new LanSegmentsUpsert(client.NextSeq(), client.Now(),
            MsgType.LanSegmentsUpsert, segmentId, cidr, true));
        _ = await client.ReceiveAsync<LanSegmentsUpsertAck>() ?? throw new IOException("0x63 无应答");
    }

    private async Task RemoveSegmentAsync(TestPcpClient client, Guid segmentId)
    {
        await client.SendAsync(new LanSegmentsUpsert(client.NextSeq(), client.Now(),
            MsgType.LanSegmentsUpsert, segmentId, "", false)); // Cidr 移除分支不消费
        _ = await client.ReceiveAsync<LanSegmentsUpsertAck>() ?? throw new IOException("0x63 无应答");
    }

    private async Task<Guid> CreateMappingAsync(TestPcpClient client, string remoteCode, string targetAddr,
        ushort localPort, ushort targetPort = 80)
    {
        await client.SendAsync(new MappingUpsert(client.NextSeq(), client.Now(), MsgType.MappingUpsert,
            null, "m-l3", localPort, "tcp", remoteCode, targetAddr, targetPort, true));
        var ack = await client.ReceiveAsync<MappingUpsertAck>() ?? throw new IOException("0x60 无应答");
        return ack.MappingId;
    }

    private async Task<string> GetRemoteCodeAsync(Guid deviceId)
    {
        await using var db = CreateDb();
        return await db.Devices.AsNoTracking().Where(d => d.Id == deviceId)
            .Select(d => d.RemoteCode).SingleAsync();
    }

    private async Task<Guid> CreateSegmentDirectAsync(Guid deviceId, string cidr)
    {
        var id = Guid.NewGuid();
        await using var db = CreateDb();
        db.LanSegments.Add(new LanSegment
        {
            Id = id, DeviceId = deviceId, Cidr = cidr, Enabled = true, CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    // ── NormalizeCidr 纯函数（M2-11：入库统一规范形态）─────────────────

    [Theory]
    [InlineData("192.168.1.0/24", "192.168.1.0/24")]   // 显式前缀原样
    [InlineData("192.168.1.50", "192.168.1.50/32")]    // 裸 IPv4 补 /32
    [InlineData("fe80::1", "fe80::1/128")]             // 裸 IPv6 补 /128
    [InlineData("::", "::/128")]                       // .NET 语义：裸 IP=主机地址（全开放须显式 ::/0）
    [InlineData("not-a-cidr", null)]                   // 主机名/非法串
    [InlineData("300.1.1.1", null)]                    // 越界八位组
    [InlineData("192.168.1.0/33", null)]               // 前缀越界
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void CIDR规范化理论(string input, string? expected)
        => Assert.Equal(expected, LanSegmentService.NormalizeCidr(input));

    // ── 0x63 协议路径 ──────────────────────────────────────────────────

    [Fact]
    public async Task 新建_裸IP补32入库()
    {
        var (b, bId) = await ConnectRegisteredAsync("seg-b");
        var segmentId = await CreateSegmentAsync(b, "192.168.1.50");
        Assert.NotEqual(Guid.Empty, segmentId);

        await using var db = CreateDb();
        var row = await db.LanSegments.AsNoTracking().SingleAsync(s => s.DeviceId == bId);
        Assert.Equal("192.168.1.50/32", row.Cidr); // 规范形态落库
        Assert.True(row.Enabled);
    }

    [Fact]
    public async Task 非法CIDR_拒绝1001()
    {
        var (b, _) = await ConnectRegisteredAsync("seg-b");
        await b.SendAsync(new LanSegmentsUpsert(b.NextSeq(), b.Now(),
            MsgType.LanSegmentsUpsert, null, "not-a-cidr", true));
        var err = await b.ReceiveAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.BadRequest, err!.Code);
        Assert.Equal("bad_cidr", err.HttpLikeMsg);

        // SegmentId=null + Enabled=false：无对应操作语义
        await b.SendAsync(new LanSegmentsUpsert(b.NextSeq(), b.Now(),
            MsgType.LanSegmentsUpsert, null, "192.168.1.0/24", false));
        var err2 = await b.ReceiveAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.BadRequest, err2!.Code);
        Assert.Equal("bad_segment_fields", err2.HttpLikeMsg);
    }

    [Fact]
    public async Task passive登出后_063仍允许()
    {
        var (b, _) = await ConnectLoggedInAsync("seg-b");
        await b.SendAsync(new UserLogout(b.NextSeq(), b.Now(), MsgType.UserLogout));
        _ = await b.ReceiveAsync<UserLogoutAck>(); // 能力降级 Passive（连接保持）

        var segmentId = await CreateSegmentAsync(b, "192.168.1.0/24"); // 本机管理类不拦
        Assert.NotEqual(Guid.Empty, segmentId);
    }

    [Fact]
    public async Task 操作他人段_1002()
    {
        var (a, _) = await ConnectLoggedInAsync("seg-a");
        var (b, bId) = await ConnectRegisteredAsync("seg-b");
        var segmentId = await CreateSegmentDirectAsync(bId, "192.168.1.0/24");

        // a 更新 b 的段 → 归属校验拒绝（查询限定 DeviceId=本人 → segment_not_found）
        await a.SendAsync(new LanSegmentsUpsert(a.NextSeq(), a.Now(),
            MsgType.LanSegmentsUpsert, segmentId, "10.0.0.0/8", true));
        var err = await a.ReceiveAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.NotFound, err!.Code);
        Assert.Equal("segment_not_found", err.HttpLikeMsg);

        await a.SendAsync(new LanSegmentsUpsert(a.NextSeq(), a.Now(),
            MsgType.LanSegmentsUpsert, segmentId, "", false));
        var err2 = await a.ReceiveAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.NotFound, err2!.Code);

        await using var db = CreateDb();
        Assert.True(await db.LanSegments.AnyAsync(s => s.Id == segmentId)); // 原行未动
    }

    // ── L3 路径一：0x60 映射 upsert ────────────────────────────────────

    [Fact]
    public async Task 映射upsert_L3_段内ack_段外4002_self放行()
    {
        var (a, aId) = await ConnectLoggedInAsync("seg-a");
        var (b, bId) = await ConnectRegisteredAsync("seg-b");
        await CreateSegmentAsync(b, "192.168.1.0/24");
        var remoteCode = await GetRemoteCodeAsync(bId);

        // 段内 → Ack
        _ = await CreateMappingAsync(a, remoteCode, "192.168.1.100", 20001);
        // self → L3 不适用（05 §2.5 恒放行）
        _ = await CreateMappingAsync(a, remoteCode, "self", 20002);
        // 段外 → 4002（先审计后回错）
        await a.SendAsync(new MappingUpsert(a.NextSeq(), a.Now(), MsgType.MappingUpsert,
            null, "m-out", 20003, "tcp", remoteCode, "10.0.0.5", 80, true));
        var err = await a.ReceiveAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.TargetAddrNotAllowed, err!.Code);
        Assert.Equal("l3_segment_not_covered", err.HttpLikeMsg);

        await using var db = CreateDb();
        Assert.True(await db.WaitAuditAsync(x =>
            x.Event == "mapping_deny" && x.DeviceId == aId && x.Detail!.Contains("l3_segment_not_covered")));
    }

    // ── L3 路径二：0x70 打洞（TriggerMappingId 现值校验）──────────────

    [Fact]
    public async Task 打洞_段内映射_放行至邀请_段外映射_4002_伪造_1002()
    {
        var (a, aId) = await ConnectLoggedInAsync("seg-a");
        var (b, bId) = await ConnectRegisteredAsync("seg-b");
        var segmentId = await CreateSegmentAsync(b, "192.168.1.0/24");
        var remoteCode = await GetRemoteCodeAsync(bId);
        var mappingId = await CreateMappingAsync(a, remoteCode, "192.168.1.100", 20010);

        // 段内：verdict 通过 → b 收 0x71 PunchInvite（Ack 延后至端点就绪，不在此断言）
        await a.SendAsync(new PunchRequest(a.NextSeq(), a.Now(), MsgType.PunchRequest,
            bId, mappingId, "tcp", null, null));
        _ = await b.ReceiveAsync<PunchInvite>();

        // 段收窄（更新 Cidr）→ 映射现值落在段外 → 0x70 拒 4002
        await UpdateSegmentAsync(b, segmentId, "10.0.0.0/8");
        await a.SendAsync(new PunchRequest(a.NextSeq(), a.Now(), MsgType.PunchRequest,
            bId, mappingId, "tcp", null, null));
        var err = await a.ReceiveAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.TargetAddrNotAllowed, err!.Code);
        Assert.Equal("l3_segment_not_covered", err.HttpLikeMsg);

        // 伪造：不存在的 TriggerMappingId → 1002（防绕过 0x60 校验点）
        await a.SendAsync(new PunchRequest(a.NextSeq(), a.Now(), MsgType.PunchRequest,
            bId, Guid.NewGuid(), "tcp", null, null));
        var err2 = await a.ReceiveAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.NotFound, err2!.Code);
        Assert.Equal("mapping_not_found", err2.HttpLikeMsg);

        await using var db = CreateDb();
        Assert.True(await db.WaitAuditAsync(x =>
            x.Event == "punch_deny" && x.DeviceId == aId && x.Detail!.Contains("l3_segment_not_covered")));
    }

    // ── 移除联动 0x75 ──────────────────────────────────────────────────

    [Fact]
    public async Task 移除段_075仅携受影响映射_段行删除()
    {
        var (a, _) = await ConnectLoggedInAsync("seg-a");
        var (b, bId) = await ConnectRegisteredAsync("seg-b");
        var seg1 = await CreateSegmentAsync(b, "192.168.1.0/24");
        var seg2 = await CreateSegmentAsync(b, "10.0.0.0/8");
        var remoteCode = await GetRemoteCodeAsync(bId);

        var m1 = await CreateMappingAsync(a, remoteCode, "192.168.1.100", 20020); // ∈ seg1
        var m2 = await CreateMappingAsync(a, remoteCode, "10.0.0.5", 20021);      // ∈ seg2

        // b 移除 seg1 → b 收 Ack；0x75 随后达映射 owner a（携 [m1]；m2 由 seg2 覆盖不受影响）——
        // M2-33 修正：按映射持有方路由（0x14/0x52/0x56 同口径），段属设备 b 不持有这些映射
        await b.SendAsync(new LanSegmentsUpsert(b.NextSeq(), b.Now(),
            MsgType.LanSegmentsUpsert, seg1, "", false));
        _ = await b.ReceiveAsync<LanSegmentsUpsertAck>();
        var push = await a.ReceiveAsync<Invalidation>();
        Assert.Equal(InvalidationReason.LanSegmentRemoved, push!.Reason);
        Assert.Equal(new[] { m1 }, push.AffectedMappingIds); // 单元素数组（M2-10 教训：集合表达式推断不可靠）
        Assert.Null(push.NewCapability);

        await using var db = CreateDb();
        Assert.False(await db.LanSegments.AnyAsync(s => s.Id == seg1)); // 段已删
        Assert.True(await db.LanSegments.AnyAsync(s => s.Id == seg2));
        Assert.True(await db.WaitAuditAsync(x => x.Event == "lan_segment_remove"));
    }

    [Fact]
    public async Task 移除段_无受影响映射_不推075()
    {
        var (b, _) = await ConnectRegisteredAsync("seg-b");
        var seg = await CreateSegmentAsync(b, "172.16.0.0/12"); // 无映射引用

        await b.SendAsync(new LanSegmentsUpsert(b.NextSeq(), b.Now(),
            MsgType.LanSegmentsUpsert, seg, "", false));
        _ = await b.ReceiveAsync<LanSegmentsUpsertAck>();
        await Assert.ThrowsAsync<TimeoutException>(() => b.ReceiveAsync<Invalidation>(800));
    }
}

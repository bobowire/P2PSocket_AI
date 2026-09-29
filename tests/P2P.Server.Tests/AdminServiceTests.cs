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
/// M2-13 禁用与解绑机制（FR-S-105/204/103、05 §5、04 §5 4004）：
/// 设备禁用=踢线+引用方 0x75(device_disabled)+读侧拒绝（Hello 断连/同 MAC 覆盖式恢复 2003）；
/// 用户禁用=名下在线设备降级 passive+0x75(user_disabled,newCapability) 不踢线（FR-S-204），
/// enable 后重新登录恢复 normal；解绑=删行+同 MAC 重注册全新身份（身份重签，FR-S-103 收口）；
/// 心跳兜底=跨进程 CLI 直写库（绕过 AdminService）由 PresenceMonitor 在节流窗口内收口。
/// </summary>
public sealed class AdminServiceTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    private readonly DeviceRegistry _registry = new();
    private readonly List<TestPcpClient> _clients = [];
    private ControlServer _server = null!;
    private SignalingCoordinator _signaling = null!;
    private RelayService _relay = null!;
    private PresenceMonitor _monitor = null!;
    private AdminService _admin = null!;

    public Task InitializeAsync()
    {
        var factory = new StubFactory(CreateDb);
        using var init = factory.CreateDbContext();
        DbInitializer.Initialize(init);

        var audit = new AuditLogger(factory);
        _signaling = new SignalingCoordinator(factory, _registry, new Authorizer(factory), audit);
        _relay = new RelayService(factory, _registry, _signaling.ResolveRelayPeers);
        var invalidation = new InvalidationPusher(factory, _registry); // 0x75（M2-12）
        var pusher = new DeviceListPusher(factory, _registry);         // 0x41（帧序交错源）
        var router = new ControlMessageRouter(
            new RegistrationService(factory, _registry, audit, invalidation: invalidation, listPusher: pusher),
            new UserService(factory, audit, invalidation: invalidation),
            new GroupService(factory, _registry, audit, pusher, invalidation),
            _signaling,
            new MappingService(factory, audit),
            _relay,
            new StatsService(factory, audit),
            audit,
            new LanSegmentService(factory, _registry, audit));
        _admin = new AdminService(factory, _registry, audit, invalidation);
        // 心跳兜底（真时钟小间隔：跨进程写库窗口压至 ~200ms，覆盖 CLI 场景）
        _monitor = new PresenceMonitor(_registry, factory,
            period: TimeSpan.FromMilliseconds(100), invalidation: invalidation,
            adminCheckInterval: TimeSpan.FromMilliseconds(100));
        _server = new ControlServer(factory, _registry, router.DispatchAsync);
        return _server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
    }

    public async Task DisposeAsync()
    {
        await _relay.DisposeAsync();
        foreach (var c in _clients) await c.DisposeAsync();
        await _server.DisposeAsync();
        await _signaling.DisposeAsync();
        await _monitor.DisposeAsync();
        _db.Dispose();
    }

    private AppDbContext CreateDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_db.DataSource).Options);

    private async Task<(TestPcpClient Client, Guid DeviceId, string MacCode, byte[] Secret)> RegisterAsync(string name)
    {
        var client = await TestPcpClient.ConnectAsync(_server.LocalEndPoint!);
        _clients.Add(client);
        await client.HelloAsync(deviceId: null);
        var key = EcKeyPair.Generate();
        var mac = $"P2P-ADM{Guid.NewGuid():N}"[..16];
        await client.SendAsync(new Register(client.NextSeq(), client.Now(), MsgType.Register,
            mac, name, "windows", "0.1.0", key.ExportPublicKey(), null, null, null), sign: false);
        var ack = await client.ReceiveSkippingPushesAsync<RegisterAck>() ?? throw new IOException("注册无应答");
        var secret = Ecies.Decrypt(key, ack.DeviceSecretBox);
        client.EstablishWithSecret(secret);
        await DrainPushesAsync(client); // 沉降：注册收尾（默认组入组/audit/0x41）落定后再还出客户端
        return (client, ack.DeviceId, mac, secret);
    }

    /// <summary>注册独立用户并登录（用户禁用目标：名下在线设备）。</summary>
    private async Task LoginNewUserAsync(TestPcpClient client, string username)
    {
        await client.SendAsync(new UserRegister(client.NextSeq(), client.Now(), MsgType.UserRegister,
            username, $"{username}-password-1"));
        _ = await client.ReceiveSkippingPushesAsync<UserRegisterAck>();
        await client.SendAsync(new UserLogin(client.NextSeq(), client.Now(), MsgType.UserLogin,
            username, $"{username}-password-1"));
        _ = await client.ReceiveSkippingPushesAsync<UserLoginAck>();
        await DrainPushesAsync(client); // 沉降：登录收尾落定后再发起后续跨客户端请求
    }

    private async Task<Guid> CreateMappingAsync(TestPcpClient client, string remoteCode, ushort localPort)
    {
        await client.SendAsync(new MappingUpsert(client.NextSeq(), client.Now(), MsgType.MappingUpsert,
            null, "m-adm", localPort, "tcp", remoteCode, "self", 80, true));
        var ack = await client.ReceiveSkippingPushesAsync<MappingUpsertAck>() ?? throw new IOException("0x60 无应答");
        return ack.MappingId;
    }

    private async Task<string> GetRemoteCodeAsync(Guid deviceId)
    {
        await using var db = CreateDb();
        return await db.Devices.AsNoTracking().Where(d => d.Id == deviceId)
            .Select(d => d.RemoteCode).SingleAsync();
    }

    /// <summary>排干在途 0x41（连接期上线/成员变更/下线推送）；兼作沉降点（夹具竞态纪律）。</summary>
    private async Task DrainPushesAsync(TestPcpClient client)
    {
        for (var i = 0; i < 50; i++)
        {
            var frame = await client.ReceiveDescribeAsync(400);
            if (frame is null) return; // 静默期：排干完成
            if (frame.StartsWith("0x41 ")) continue;
            Assert.Fail($"排干撞非 0x41 帧：{frame}；余帧: {string.Join(" | ", await client.DumpFramesAsync(1500))}");
        }
        Assert.Fail("0x41 排干超上限（异常推送风暴？）");
    }

    // ── 设备禁用：踢线 + 0x75 + 读侧拒绝（FR-S-105）────────────────────

    [Fact]
    public async Task DisableDevice_KicksSends075BlocksHelloAndRecover()
    {
        var (a, _, _, _) = await RegisterAsync("adm-a");
        var (b, bId, bMac, _) = await RegisterAsync("adm-b");
        var mid = await CreateMappingAsync(a, await GetRemoteCodeAsync(bId), 18180);

        Assert.True(await _admin.DisableDeviceAsync(bMac));

        // 踢线：b 连接关闭（先推 0x75 后断连接）
        Assert.True(await b.WaitClosedAsync(3000));
        // 引用方 a：0x75(device_disabled, [mid])——先于 b 下线 0x41（处理器顺序确定）
        var inv = await a.ReceiveAsync<Invalidation>();
        Assert.NotNull(inv);
        Assert.Equal(InvalidationReason.DeviceDisabled, inv!.Reason);
        Assert.Equal(new[] { mid }, inv.AffectedMappingIds);
        Assert.Null(inv.NewCapability);
        await DrainPushesAsync(a); // b 下线 0x41 收尾

        // 读侧拒绝①：Hello(带 deviceId) 直接断连（不落 NeedRegister——禁用不可借道重注册绕回）
        var c1 = await TestPcpClient.ConnectAsync(_server.LocalEndPoint!);
        _clients.Add(c1);
        await Assert.ThrowsAsync<IOException>(() => c1.HelloAsync(bId));

        // 读侧拒绝②：同 MAC 覆盖式恢复 → 2003 forbidden
        var c2 = await TestPcpClient.ConnectAsync(_server.LocalEndPoint!);
        _clients.Add(c2);
        var hello = await c2.HelloAsync(deviceId: null);
        Assert.Equal(HelloStatus.NeedRegister, hello.Status);
        var key = EcKeyPair.Generate();
        await c2.SendAsync(new Register(c2.NextSeq(), c2.Now(), MsgType.Register,
            bMac, "adm-b2", "windows", "0.1.0", key.ExportPublicKey(), null, null, null), sign: false);
        var err = await c2.ReceiveAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.Forbidden, err!.Code);
        Assert.True(await c2.WaitClosedAsync(3000));

        // 幂等：重复禁用视作成功
        Assert.True(await _admin.DisableDeviceAsync(bMac));

        await using var db = CreateDb();
        var row = await db.Devices.AsNoTracking().SingleAsync(d => d.Id == bId);
        Assert.True(row.Disabled);
        Assert.True(await db.WaitAuditAsync(x => x.Event == "device_disable" && x.DeviceId == bId));
    }

    [Fact]
    public async Task EnableDevice_RestoresHelloPath()
    {
        var (b, bId, bMac, _) = await RegisterAsync("adm-en");
        Assert.True(await _admin.DisableDeviceAsync(bMac));
        Assert.True(await b.WaitClosedAsync(3000));
        Assert.True(await _admin.EnableDeviceAsync(bMac));

        var c = await TestPcpClient.ConnectAsync(_server.LocalEndPoint!);
        _clients.Add(c);
        var ack = await c.HelloAsync(bId);
        Assert.Equal(HelloStatus.Ok, ack.Status); // 读侧放行：正常走 AwaitingProof
    }

    // ── 用户禁用：降级 passive + 0x75(user_disabled) 不踢线（FR-S-204）────

    [Fact]
    public async Task DisableUser_DowngradesToPassiveThenEnableRestoresLogin()
    {
        var (a, aId, _, aSecret) = await RegisterAsync("adm-u-a");
        var (b, bId, _, _) = await RegisterAsync("adm-u-b");
        await LoginNewUserAsync(a, "adm-user1");
        var mid = await CreateMappingAsync(a, await GetRemoteCodeAsync(bId), 18181);

        Assert.True(await _admin.DisableUserAsync("adm-user1"));

        // 0x75 先到（降级提示独立于映射集合；连接不关闭）
        var inv = await a.ReceiveAsync<Invalidation>();
        Assert.NotNull(inv);
        Assert.Equal(InvalidationReason.UserDisabled, inv!.Reason);
        Assert.Equal(new[] { mid }, inv.AffectedMappingIds);
        Assert.Equal(CapabilityMode.Passive, inv.NewCapability);

        // 不踢线（哑节点继续被动同步）：心跳仍通
        await a.SendAsync(new Heartbeat(a.NextSeq(), a.Now(), MsgType.Heartbeat));
        Assert.NotNull(await a.ReceiveSkippingPushesAsync<HeartbeatAck>());

        // 主动类 → 2002 + 审计（SEC-51）
        await a.SendAsync(new DeviceListRequest(a.NextSeq(), a.Now(), MsgType.DeviceList, 0, 100));
        var err = await a.ReceiveAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.ForbiddenPassive, err!.Code);

        // enable 后恢复 normal（02 §2.5：0x21 属主动类清单，passive 期同连接登录被 2002 拦——
        // 恢复路径=重连+登录；新会话能力初始 normal，再登录绑定即全功能）
        Assert.True(await _admin.EnableUserAsync("adm-user1"));
        var c = await TestPcpClient.ConnectAsync(_server.LocalEndPoint!);
        _clients.Add(c);
        var helloAck = await c.HelloAsync(aId);
        Assert.Equal(HelloStatus.Ok, helloAck.Status);
        await c.ProofAsync(aSecret);
        await c.SendAsync(new UserLogin(c.NextSeq(), c.Now(), MsgType.UserLogin,
            "adm-user1", "adm-user1-password-1"));
        var login = await c.ReceiveSkippingPushesAsync<UserLoginAck>();
        Assert.True(login!.Ok);
        Assert.Equal(CapabilityMode.Normal, login.Mode);
        await c.SendAsync(new DeviceListRequest(c.NextSeq(), c.Now(), MsgType.DeviceList, 0, 100));
        Assert.NotNull(await c.ReceiveSkippingPushesAsync<DeviceListResponse>());
        await DrainPushesAsync(c);

        await using var db = CreateDb();
        Assert.True(await db.WaitAuditAsync(x => x.Event == "user_disable"));
        Assert.True(await db.WaitAuditAsync(x => x.Event == "user_enable"));
    }

    // ── 解绑：删行 + 同 MAC 重注册全新身份（FR-S-103 收口）──────────────

    [Fact]
    public async Task UnbindDevice_DeletesRowsAndAllowsFreshRegister()
    {
        var (b, bId, bMac, _) = await RegisterAsync("adm-ub");
        var oldCode = await GetRemoteCodeAsync(bId);

        Assert.True(await _admin.UnbindDeviceAsync(bMac));
        Assert.True(await b.WaitClosedAsync(3000)); // 在线也解（4004 挡自助路径后的管理员出口）

        await using (var db = CreateDb())
        {
            Assert.False(await db.Devices.AsNoTracking().AnyAsync(d => d.MacCode == bMac));
            Assert.True(await db.WaitAuditAsync(x => x.Event == "unbind_admin"));
        }

        // 同 MAC 重注册=全新身份：新 deviceId/新远程码/新凭据（身份重签）
        var c = await TestPcpClient.ConnectAsync(_server.LocalEndPoint!);
        _clients.Add(c);
        await c.HelloAsync(deviceId: null);
        var key = EcKeyPair.Generate();
        await c.SendAsync(new Register(c.NextSeq(), c.Now(), MsgType.Register,
            bMac, "adm-ub2", "windows", "0.1.0", key.ExportPublicKey(), null, null, null), sign: false);
        var ack = await c.ReceiveAsync<RegisterAck>() ?? throw new IOException("重注册无应答");
        Assert.NotEqual(bId, ack.DeviceId);
        Assert.NotEqual(oldCode, ack.RemoteCode);
        c.EstablishWithSecret(Ecies.Decrypt(key, ack.DeviceSecretBox));

        // 目标不存在 → false
        Assert.False(await _admin.UnbindDeviceAsync("P2P-NOSUCH0000"));
    }

    // ── 心跳兜底：跨进程 CLI 直写库（绕过 AdminService）由监视器收口 ──────

    [Fact]
    public async Task HeartbeatFallback_DirectDbDisablePickedUpByMonitor()
    {
        var (a, _, _, _) = await RegisterAsync("adm-fb-a");
        var (b, bId, _, _) = await RegisterAsync("adm-fb-b");
        var mid = await CreateMappingAsync(a, await GetRemoteCodeAsync(bId), 18182);

        // 模拟 CLI 独立进程：只写库（无内存注册表触达）
        await using (var db = CreateDb())
            await db.Devices.Where(d => d.Id == bId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Disabled, true));

        // 兜底窗口（period 100ms × 节流 100ms）：踢线 + 引用方 0x75 补推
        Assert.True(await b.WaitClosedAsync(5000));
        var inv = await a.ReceiveAsync<Invalidation>();
        Assert.NotNull(inv);
        Assert.Equal(InvalidationReason.DeviceDisabled, inv!.Reason);
        Assert.Equal(new[] { mid }, inv.AffectedMappingIds);
    }
}

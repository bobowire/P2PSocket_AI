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
/// 用户与能力模式测试（02 §2.4/§2.5；完成判定：登录→能力切换、passive 发 0x40/0x70 → 2002 + 审计行）。
/// </summary>
public sealed class UserTests : IAsyncLifetime
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
            audit);
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

    private async Task<TestPcpClient> ConnectRegisteredAsync()
    {
        var client = await TestPcpClient.ConnectAsync(_server.LocalEndPoint!);
        _clients.Add(client);
        await client.HelloAsync(deviceId: null);
        var key = EcKeyPair.Generate();
        await client.SendAsync(new Register(client.NextSeq(), client.Now(), MsgType.Register,
            $"P2P-USER{Guid.NewGuid():N}"[..14], "host-u", "windows", "0.1.0",
            key.ExportPublicKey(), null, null, null), sign: false);
        var ack = await client.ReceiveAsync<RegisterAck>() ?? throw new IOException("注册无应答");
        client.EstablishWithSecret(Ecies.Decrypt(key, ack.DeviceSecretBox));
        return client;
    }

    // ── 0x21 登录（FR-S-106 owner 绑定 + 能力切换）────────────────────

    [Fact]
    public async Task Login_AdminCredentials_BindsOwnerAndReturnsNormal()
    {
        var client = await ConnectRegisteredAsync();

        await client.SendAsync(new UserLogin(client.NextSeq(), client.Now(), MsgType.UserLogin,
            DbInitializer.AdminUsername, DbInitializer.AdminUsername));
        var ack = await client.ReceiveAsync<UserLoginAck>();
        Assert.NotNull(ack);
        Assert.True(ack!.Ok);
        Assert.Equal(CapabilityMode.Normal, ack.Mode);
        Assert.NotEmpty(ack.Token);

        // 设备 owner 绑定落库 + 审计 login
        await using var db = CreateDb();
        var device = await db.Devices.AsNoTracking()
            .SingleAsync(d => d.MacCode.StartsWith("P2P-USER"));
        Assert.NotNull(device.OwnerUserId);
        var admin = await db.Users.AsNoTracking().SingleAsync(u => u.Username == DbInitializer.AdminUsername);
        Assert.Equal(admin.Id, device.OwnerUserId);
        Assert.True(await db.WaitAuditAsync(a => a.Event == "login" && a.DeviceId == device.Id));
    }

    [Fact]
    public async Task Login_WrongPassword_Rejected()
    {
        var client = await ConnectRegisteredAsync();
        await client.SendAsync(new UserLogin(client.NextSeq(), client.Now(), MsgType.UserLogin,
            DbInitializer.AdminUsername, "wrong-password"));
        var ack = await client.ReceiveAsync<UserLoginAck>();
        Assert.NotNull(ack);
        Assert.False(ack!.Ok);
    }

    // ── 0x22 登出 → passive → 主动类 2002（02 §2.5，SEC-51）──────────

    [Fact]
    public async Task Logout_ThenActiveMessages_RejectedWith2002AndAudited()
    {
        var client = await ConnectRegisteredAsync();
        await client.SendAsync(new UserLogout(client.NextSeq(), client.Now(), MsgType.UserLogout));
        var logoutAck = await client.ReceiveAsync<UserLogoutAck>();
        Assert.NotNull(logoutAck);
        Assert.True(logoutAck!.Ok);

        // 0x40 设备列表 → 2002 + 审计
        await client.SendAsync(new DeviceListRequest(client.NextSeq(), client.Now(), MsgType.DeviceList, 0, 100));
        var error = await client.ReceiveAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.ForbiddenPassive, error!.Code);

        // 0x70 打洞发起 → 2002 + 审计
        await client.SendAsync(new PunchRequest(client.NextSeq(), client.Now(), MsgType.PunchRequest,
            Guid.NewGuid(), null, "udp", null, null));
        var error2 = await client.ReceiveAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.ForbiddenPassive, error2!.Code);

        await using var db = CreateDb();
        // 错误帧先于审计落库（路由器序）：轮询等待第二行 commit，避免读早于写
        Assert.Equal(2, await db.WaitAuditCountAsync(a => a.Event == "passive_deny", 2));

        // 心跳（被动类 0x30）不受影响
        await client.SendAsync(new Heartbeat(client.NextSeq(), client.Now(), MsgType.Heartbeat));
        var hb = await client.ReceiveAsync<HeartbeatAck>();
        Assert.NotNull(hb);
    }

    // ── 0x13 改名（本机管理类，passive 允许）──────────────────────────

    [Fact]
    public async Task DeviceRename_PassiveMode_Allowed()
    {
        var client = await ConnectRegisteredAsync();
        await client.SendAsync(new UserLogout(client.NextSeq(), client.Now(), MsgType.UserLogout)); // 切 passive
        _ = await client.ReceiveAsync<UserLogoutAck>();

        await client.SendAsync(new DeviceUpdate(client.NextSeq(), client.Now(), MsgType.DeviceUpdate, "新设备名"));
        var ack = await client.ReceiveAsync<DeviceUpdateAck>();
        Assert.NotNull(ack);
        Assert.True(ack!.Ok); // 02 §2.5：0x13 本机管理类不拦

        await using var db = CreateDb();
        var device = await db.Devices.AsNoTracking().SingleAsync(d => d.MacCode.StartsWith("P2P-USER"));
        Assert.Equal("新设备名", device.DeviceName);
    }

    // ── 0x20 用户注册（registration_open 开关，FR-S-201）──────────────

    [Fact]
    public async Task UserRegister_OpenSwitch_CreatesAndCanLogin()
    {
        var client = await ConnectRegisteredAsync();
        await client.SendAsync(new UserRegister(client.NextSeq(), client.Now(), MsgType.UserRegister,
            "alice", "alice-password"));
        var ack = await client.ReceiveAsync<UserRegisterAck>();
        Assert.NotNull(ack);
        Assert.True(ack!.Ok);

        // 新用户可登录
        await client.SendAsync(new UserLogin(client.NextSeq(), client.Now(), MsgType.UserLogin,
            "alice", "alice-password"));
        var login = await client.ReceiveAsync<UserLoginAck>();
        Assert.True(login!.Ok);

        // 用户名占用 → Ok=false
        var client2 = await ConnectRegisteredAsync();
        await client2.SendAsync(new UserRegister(client2.NextSeq(), client2.Now(), MsgType.UserRegister,
            "alice", "another-pass"));
        var dup = await client2.ReceiveAsync<UserRegisterAck>();
        Assert.False(dup!.Ok);
    }

    [Fact]
    public async Task UserRegister_ClosedSwitch_Gets2004()
    {
        await using (var db = CreateDb())
        {
            var entry = await db.ServerConfig.SingleAsync(c => c.Key == "registration_open");
            entry.Value = "0";
            await db.SaveChangesAsync();
        }

        var client = await ConnectRegisteredAsync();
        await client.SendAsync(new UserRegister(client.NextSeq(), client.Now(), MsgType.UserRegister,
            "bob", "bob-password-1"));
        var error = await client.ReceiveAsync<ErrorMessage>();
        Assert.NotNull(error);
        Assert.Equal(ErrorCode.RegistrationClosed, error!.Code);
    }
}

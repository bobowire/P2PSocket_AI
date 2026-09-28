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
/// 注册族集成测试（02 §2.4 0x10/0x12；完成判定：恢复保留 deviceId/远程码/分组、在线 4004、离线判定 30s）。
/// </summary>
public sealed class RegistrationTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DeviceRegistry _registry = new();
    private readonly List<TestPcpClient> _clients = [];
    private ControlServer _server = null!;
    private RegistrationService _registration = null!;
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
        _registration = new RegistrationService(factory, _registry, audit, _time);
        _signaling = new SignalingCoordinator(factory, _registry, new Authorizer(factory), audit, _time);
        _relay = new RelayService(factory, _registry, _signaling.ResolveRelayPeers, time: _time);
        var router = new ControlMessageRouter(_registration, new UserService(factory, audit, _time),
            new GroupService(factory, _registry, audit, time: _time), _signaling,
            new MappingService(factory, audit), _relay, new StatsService(factory, audit), audit);
        _server = new ControlServer(factory, _registry, router.DispatchAsync, time: _time);
        return _server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
    }

    public async Task DisposeAsync()
    {
        await _relay.DisposeAsync();
        foreach (var c in _clients) await c.DisposeAsync();
        await _server.DisposeAsync();
        await _signaling.DisposeAsync();
        _connection.Dispose();
    }

    private AppDbContext CreateDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);

    private async Task<TestPcpClient> ConnectAsync()
    {
        var client = await TestPcpClient.ConnectAsync(_server.LocalEndPoint!);
        _clients.Add(client);
        return client;
    }

    /// <summary>走完整注册链路（未签名 Register → RegisterAck），解 ECIES 转签名会话。</summary>
    private async Task<(TestPcpClient Client, EcKeyPair Key, RegisterAck Ack, byte[] Secret)> RegisterAsync(string macCode)
    {
        var client = await ConnectAsync();
        var helloAck = await client.HelloAsync(deviceId: null);
        Assert.Equal(HelloStatus.NeedRegister, helloAck.Status);

        var key = EcKeyPair.Generate();
        await client.SendAsync(new Register(client.NextSeq(), client.Now(), MsgType.Register,
            macCode, "host-x", "windows", "0.1.0", key.ExportPublicKey(), null, null, null), sign: false);
        var ack = await client.ReceiveAsync<RegisterAck>() ?? throw new IOException("注册无应答");

        var secret = Ecies.Decrypt(key, ack.DeviceSecretBox);
        client.EstablishWithSecret(secret); // RegisterAck 未签名；自此双向签名（02 §2.2）
        return (client, key, ack, secret);
    }

    private static async Task WaitForAsync(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.True(condition(), $"等待超时：{what}");
    }

    // ── 0x10 新建注册 ──────────────────────────────────────────────────

    [Fact]
    public async Task Register_NewDevice_CreatesWithDefaults()
    {
        var (client, _, ack, secret) = await RegisterAsync("P2P-AABBCCDDEE01");

        Assert.NotEqual(Guid.Empty, ack.DeviceId);
        Assert.Equal(32, secret.Length);                       // ECIES 可解出 32B deviceSecret
        Assert.Matches("^[0-9abc]{6}$", ack.RemoteCode);       // 6 位 0-9+abc（D18/OQ-8）
        Assert.Equal(RegistrationService.VirtualIp, ack.VirtualIp); // 固定 .2（OQ-13）
        var groupName = ack.Groups.Single(g => g.GroupName == DbInitializer.DefaultGroupName);
        Assert.NotEqual(Guid.Empty, groupName.GroupId);

        // 会话已建立：签名心跳有应答；registry 在线
        Assert.True(_registry.IsOnline(ack.DeviceId));
        await client.SendAsync(new Heartbeat(client.NextSeq(), client.Now(), MsgType.Heartbeat));
        var hbAck = await client.ReceiveAsync<HeartbeatAck>();
        Assert.NotNull(hbAck);

        // 落库 + 审计
        await using var db = CreateDb();
        var device = await db.Devices.AsNoTracking().SingleAsync(d => d.Id == ack.DeviceId);
        Assert.Equal("host-x", device.DeviceName);
        Assert.Equal("P2P-AABBCCDDEE01", device.MacCode);
        Assert.True(await db.GroupMembers.AnyAsync(m => m.DeviceId == ack.DeviceId));
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Event == "register" && a.DeviceId == ack.DeviceId));
    }

    // ── 0x10 覆盖式恢复（OQ-14）───────────────────────────────────────

    [Fact]
    public async Task Register_MacCodeOfflineHit_RecoversIdentity()
    {
        var (first, _, firstAck, firstSecret) = await RegisterAsync("P2P-RECOVER0001");
        var deviceId = firstAck.DeviceId;
        var mappingId = Guid.NewGuid();

        // 存量映射 + 额外分组归属（恢复后不失效）
        await using (var db = CreateDb())
        {
            db.Mappings.Add(new Mapping
            {
                Id = mappingId,
                OwnerDeviceId = deviceId,
                Name = "存量",
                LocalPort = 8080,
                Proto = "tcp",
                TargetDeviceId = deviceId,
                TargetAddr = "self",
                TargetPort = 80,
                Enabled = true,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        await first.DisposeAsync(); // 原设备离线
        await WaitForAsync(() => !_registry.IsOnline(deviceId), "原会话注销");

        var (recovered, _, ack, newSecret) = await RegisterAsync("P2P-RECOVER0001");

        Assert.Equal(deviceId, ack.DeviceId);                  // 保留 deviceId
        Assert.Equal(firstAck.RemoteCode, ack.RemoteCode);     // 保留远程码
        Assert.NotEqual(firstSecret, newSecret);               // deviceSecret 重签
        Assert.Equal(RegistrationService.VirtualIp, ack.VirtualIp);
        Assert.Contains(ack.Groups, g => g.GroupName == DbInitializer.DefaultGroupName); // 分组保留

        await using var db2 = CreateDb();
        Assert.True(await db2.Mappings.AnyAsync(m => m.Id == mappingId), "存量映射不失效");
        Assert.True(await db2.AuditLogs.AnyAsync(a => a.Event == "register_recover" && a.DeviceId == deviceId));
        _ = recovered;
    }

    [Fact]
    public async Task Register_MacCodeOnlineHit_Gets4004()
    {
        var (online, _, ack, _) = await RegisterAsync("P2P-ONLINE00001");
        Assert.True(_registry.IsOnline(ack.DeviceId)); // 原设备在线

        // 另一连接同 macCode 抢注 → 4004 + 断连（防伪造 MAC，OQ-14）
        var rival = await ConnectAsync();
        await rival.HelloAsync(deviceId: null);
        var key = EcKeyPair.Generate();
        await rival.SendAsync(new Register(rival.NextSeq(), rival.Now(), MsgType.Register,
            "P2P-ONLINE00001", "rival", "windows", "0.1.0", key.ExportPublicKey(), null, null, null), sign: false);
        var error = await rival.ReceiveAsync<ErrorMessage>();
        Assert.NotNull(error);
        Assert.Equal(ErrorCode.DeviceActive, error!.Code);
        Assert.True(await rival.WaitClosedAsync(), "4004 后应断连");

        Assert.True(_registry.IsOnline(ack.DeviceId), "原设备不受影响");
        _ = online;
    }

    // ── 0x12 解绑 ─────────────────────────────────────────────────────

    [Fact]
    public async Task Unbind_Established_DeletesDeviceAndCleansUp()
    {
        var (client, _, ack, _) = await RegisterAsync("P2P-UNBIND00001");
        Assert.True(_registry.IsOnline(ack.DeviceId));

        await client.SendAsync(new UnbindMe(client.NextSeq(), client.Now(), MsgType.UnbindMe));
        Assert.True(await client.WaitClosedAsync(), "解绑后应断连");

        await WaitForAsync(() => !_registry.IsOnline(ack.DeviceId), "会话注销");
        await using var db = CreateDb();
        Assert.False(await db.Devices.AnyAsync(d => d.Id == ack.DeviceId));
        Assert.False(await db.GroupMembers.AnyAsync(m => m.DeviceId == ack.DeviceId));
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Event == "unbind" && a.DeviceId == ack.DeviceId));
    }

    // ── 心跳超时离线判定（FR-S-104，30s 可配）─────────────────────────

    [Fact]
    public async Task PresenceMonitor_NoHeartbeatFor30s_MarksOfflineAndPersists()
    {
        var factory = new StubFactory(CreateDb);
        await using var monitor = new PresenceMonitor(_registry, factory,
            timeout: TimeSpan.FromSeconds(30), period: TimeSpan.FromSeconds(1), time: _time);

        var (client, _, ack, _) = await RegisterAsync("P2P-PRESENCE001");
        var deviceId = ack.DeviceId;
        await client.SendAsync(new Heartbeat(client.NextSeq(), client.Now(), MsgType.Heartbeat));
        var hb = await client.ReceiveAsync<HeartbeatAck>();
        Assert.NotNull(hb); // 心跳刷新 LastSeen

        _time.Advance(TimeSpan.FromSeconds(31)); // 无心跳 31s > 30s
        Assert.True(await client.WaitClosedAsync(), "超时应断连");
        await WaitForAsync(() => !_registry.IsOnline(deviceId), "registry 移除");

        await using var db = CreateDb();
        var device = await db.Devices.AsNoTracking().SingleAsync(d => d.Id == deviceId);
        Assert.NotNull(device.LastSeenAt); // last_seen_at 落库（05 §5）
    }

    [Fact]
    public async Task PresenceMonitor_RegularHeartbeat_StaysOnline()
    {
        var factory = new StubFactory(CreateDb);
        await using var monitor = new PresenceMonitor(_registry, factory,
            timeout: TimeSpan.FromSeconds(30), period: TimeSpan.FromSeconds(1), time: _time);

        var (client, _, ack, _) = await RegisterAsync("P2P-KEEPALIVE01");
        // 每 10s 心跳：累计 30s 不触发离线（判定按"最近心跳距今"）
        for (var i = 0; i < 3; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(10));
            await client.SendAsync(new Heartbeat(client.NextSeq(), client.Now(), MsgType.Heartbeat));
            var hb = await client.ReceiveAsync<HeartbeatAck>();
            Assert.NotNull(hb);
        }
        Assert.True(_registry.IsOnline(ack.DeviceId), "持续心跳应保持在线");
    }
}

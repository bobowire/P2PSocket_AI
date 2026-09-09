using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using P2P.Client.Control;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using P2P.Server.Data;
using P2P.Server.Services;
using Xunit;

namespace P2P.IntegrationTests;

/// <summary>in-proc EF 工厂桩（同 P2P.Server.Tests 模式；共享已打开的 Sqlite 内存连接）。</summary>
internal sealed class StubFactory(Func<AppDbContext> create) : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext() => create();
    public Task<AppDbContext> CreateDbContextAsync(CancellationToken ct = default)
        => Task.FromResult(create());
}

/// <summary>
/// M1-23 ControlClient 对 in-proc 服务端集成测试（02 §2.3、OQ-12）：
/// 已注册设备握手成功、断服重连恢复、时钟偏移注入后 5005 重校准收敛、
/// 未注册停在 NeedRegister、serverAddrs[] 顺序尝试（FR-C-604）、
/// 能力模式维护与 passive 本地拦截（02 §2.5）。
/// </summary>
public sealed class ControlClientIntegrationTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DeviceRegistry _registry = new();
    private readonly List<SignalingCoordinator> _signalings = [];
    private readonly List<ControlServer> _servers = [];
    private StubFactory _factory = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        _connection.Open();
        _factory = new StubFactory(CreateDb);
        using (var init = _factory.CreateDbContext())
            DbInitializer.Initialize(init);
        var server = await StartServerAsync(0);
        _port = server.LocalEndPoint!.Port;
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _servers) await s.DisposeAsync();
        foreach (var s in _signalings) await s.DisposeAsync();
        _connection.Dispose();
    }

    private AppDbContext CreateDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);

    /// <summary>同端口可重启（ControlServer SO_REUSEADDR），支撑断服重连场景。</summary>
    private async Task<ControlServer> StartServerAsync(int port)
    {
        var audit = new AuditLogger(_factory);
        var signaling = new SignalingCoordinator(_factory, _registry, new Authorizer(_factory), audit);
        var router = new ControlMessageRouter(
            new RegistrationService(_factory, _registry, audit),
            new UserService(_factory, audit),
            new GroupService(_factory, _registry),
            signaling,
            new MappingService(_factory, audit),
            audit);
        var server = new ControlServer(_factory, _registry, router.DispatchAsync);
        await server.StartAsync(new IPEndPoint(IPAddress.Loopback, port));
        _signalings.Add(signaling);
        _servers.Add(server);
        return server;
    }

    /// <summary>直接写库种一台已注册设备（凭据已知），绕过注册流。</summary>
    private async Task<(Guid DeviceId, byte[] DeviceSecret)> SeedDeviceAsync()
    {
        var secret = RandomGenerator.Bytes(32);
        var device = new Device
        {
            Id = Guid.NewGuid(),
            DeviceName = "it-device",
            Os = "windows",
            ClientVersion = "0.1.0",
            MacCode = $"IT-{Guid.NewGuid():N}"[..16],
            RemoteCode = FakeRemoteCode(),
            VirtualIp = "100.64.0.2",
            StaticPubKey = new byte[65],
            DeviceSecret = secret,
            LastSeenAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
        };
        await using var db = _factory.CreateDbContext();
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return (device.Id, secret);
    }

    /// <summary>6 位 0-9+abc（OQ-8 字符集）；仅保证唯一，格式合法性属服务端生成器测试。</summary>
    private static string FakeRemoteCode()
    {
        const string alphabet = "0123456789abc";
        return new string(RandomGenerator.Bytes(6).Select(b => alphabet[b % alphabet.Length]).ToArray());
    }

    private ControlClient NewClient(Guid? deviceId, byte[]? deviceSecret = null) => new(
        new[] { $"127.0.0.1:{_port}" },
        new ControlClientOptions { HeartbeatInterval = TimeSpan.FromHours(1) }, // 测试不跑心跳周期
        deviceId: deviceId,
        deviceSecret: deviceSecret);

    private static async Task ReadyAsync(ControlClient client)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await client.WaitReadyAsync(cts.Token);
    }

    [Fact]
    public async Task 已注册设备_握手成功_请求往返()
    {
        var (deviceId, secret) = await SeedDeviceAsync();
        await using var client = NewClient(deviceId, secret);
        await ReadyAsync(client);

        Assert.Equal(ControlClientState.Established, client.State);
        Assert.True(client.Clock.Calibrated); // HelloAck RTT/2 校准（OQ-12）

        var ack = await client.SendRequestAsync<DeviceUpdateAck>(new DeviceUpdate(
            client.NextSeq(), client.TimestampMs(), MsgType.DeviceUpdate, "it-renamed"));
        Assert.True(ack.Ok);

        await using var db = _factory.CreateDbContext();
        var name = await db.Devices.AsNoTracking()
            .Where(d => d.Id == deviceId).Select(d => d.DeviceName).SingleAsync();
        Assert.Equal("it-renamed", name);
    }

    [Fact]
    public async Task 未注册设备_握手停在NeedRegister()
    {
        await using var client = NewClient(deviceId: null);
        await ReadyAsync(client);
        Assert.Equal(ControlClientState.NeedRegister, client.State); // M1-24 向导入口
    }

    [Fact]
    public async Task 断服后_指数退避重连恢复()
    {
        var (deviceId, secret) = await SeedDeviceAsync();
        await using var client = NewClient(deviceId, secret);
        await ReadyAsync(client);
        Assert.Equal(ControlClientState.Established, client.State);

        await _servers[0].DisposeAsync();  // 杀服务端（连接断）
        await StartServerAsync(_port);     // 同端口重启

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        // 先等客户端检测断线（离开 Established），再等退避重连恢复（避免捕获断线前的旧状态）
        while (client.State == ControlClientState.Established)
            await Task.Delay(50, cts.Token);
        while (client.State != ControlClientState.Established)
            await Task.Delay(100, cts.Token);

        var ack = await client.SendRequestAsync<DeviceUpdateAck>(new DeviceUpdate(
            client.NextSeq(), client.TimestampMs(), MsgType.DeviceUpdate, "after-reconnect"));
        Assert.True(ack.Ok); // 重连后业务恢复
    }

    [Fact]
    public async Task 时钟偏移注入_5005重校准_重试成功收敛()
    {
        var (deviceId, secret) = await SeedDeviceAsync();
        await using var client = NewClient(deviceId, secret);
        await ReadyAsync(client);
        Assert.True(Math.Abs(client.Clock.OffsetMs) < 60_000); // 握手后基本对齐

        // 注入 +5min 偏移（模拟本机时钟漂移超 ±120s 窗口，02 §2.3）
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        client.Clock.Calibrate((ulong)(now + 300_000), now, now);
        Assert.Equal(300_000, client.Clock.OffsetMs);

        // 出站 ts 超窗 → 服务端 5005 附 serverTs → 客户端重校准并换新 seq/ts 重试一次
        var ack = await client.SendRequestAsync<DeviceUpdateAck>(new DeviceUpdate(
            client.NextSeq(), client.TimestampMs(), MsgType.DeviceUpdate, "skew-fixed"));
        Assert.True(ack.Ok);

        await using var db = _factory.CreateDbContext();
        var name = await db.Devices.AsNoTracking()
            .Where(d => d.Id == deviceId).Select(d => d.DeviceName).SingleAsync();
        Assert.Equal("skew-fixed", name);          // 重试真正生效（非首达）
        Assert.True(Math.Abs(client.Clock.OffsetMs) < 5_000,
            $"重校准后 offset 未收敛：{client.Clock.OffsetMs}ms");
    }

    [Fact]
    public async Task 首地址不可达_顺序尝试次地址()
    {
        var (deviceId, secret) = await SeedDeviceAsync();
        await using var client = new ControlClient(
            new[] { "127.0.0.1:1", $"127.0.0.1:{_port}" }, // 端口 1 无监听 → 拒连 → 次 址（FR-C-604）
            new ControlClientOptions { HeartbeatInterval = TimeSpan.FromHours(1) },
            deviceId: deviceId,
            deviceSecret: secret);
        await ReadyAsync(client);
        Assert.Equal(ControlClientState.Established, client.State);
    }

    [Fact]
    public async Task 登录登出_能力模式维护_passive本地拦截()
    {
        var (deviceId, secret) = await SeedDeviceAsync();
        await using var client = NewClient(deviceId, secret);
        await ReadyAsync(client);
        Assert.Equal(CapabilityMode.Normal, client.Capability); // 新会话初始 Normal（02 §2.5 镜像）

        var login = await client.SendRequestAsync<UserLoginAck>(new UserLogin(
            client.NextSeq(), client.TimestampMs(), MsgType.UserLogin,
            DbInitializer.AdminUsername, DbInitializer.AdminUsername));
        Assert.True(login.Ok);
        Assert.Equal(CapabilityMode.Normal, client.Capability);

        var logout = await client.SendRequestAsync<UserLogoutAck>(new UserLogout(
            client.NextSeq(), client.TimestampMs(), MsgType.UserLogout));
        Assert.True(logout.Ok);
        Assert.Equal(CapabilityMode.Passive, client.Capability); // 登出降级（FR-C-603）

        // passive 下主动类本地拒绝（02 §2.5；省一次往返，服务端同样会拒）
        await Assert.ThrowsAsync<PassiveModeException>(() => client.SendRequestAsync<DeviceListResponse>(
            new DeviceListRequest(client.NextSeq(), client.TimestampMs(), MsgType.DeviceList, 0, 20)));
    }
}

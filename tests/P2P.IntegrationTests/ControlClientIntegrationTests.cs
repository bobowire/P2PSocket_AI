using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using P2P.Client.Control;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using P2P.Core.Utils;
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
    private readonly TestDatabase _db = new();
    private readonly DeviceRegistry _registry = new();
    private readonly List<SignalingCoordinator> _signalings = [];
    private readonly List<RelayService> _relays = [];
    private readonly List<ControlServer> _servers = [];
    private StubFactory _factory = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        _factory = new StubFactory(CreateDb);
        using (var init = _factory.CreateDbContext())
            DbInitializer.Initialize(init);
        var server = await StartServerAsync(0);
        _port = server.LocalEndPoint!.Port;
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _servers) await s.DisposeAsync();
        foreach (var r in _relays) await r.DisposeAsync();
        foreach (var s in _signalings) await s.DisposeAsync();
        _db.Dispose();
    }

    private AppDbContext CreateDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_db.DataSource).Options);

    /// <summary>同端口可重启（ControlServer SO_REUSEADDR），支撑断服重连场景。</summary>
    private async Task<ControlServer> StartServerAsync(int port)
    {
        var audit = new AuditLogger(_factory);
        var signaling = new SignalingCoordinator(_factory, _registry, new Authorizer(_factory), audit);
        var relay = new RelayService(_factory, _registry, signaling.ResolveRelayPeers);
        var router = new ControlMessageRouter(
            new RegistrationService(_factory, _registry, audit),
            new UserService(_factory, audit),
            new GroupService(_factory, _registry, audit),
            signaling,
            new MappingService(_factory, audit),
            relay,
            new StatsService(_factory, audit),
            audit);
        var server = new ControlServer(_factory, _registry, router.DispatchAsync);
        await server.StartAsync(new IPEndPoint(IPAddress.Loopback, port));
        _signalings.Add(signaling);
        _relays.Add(relay);
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
            DbInitializer.AdminUsername, "", client.BoxLoginSecret(DbInitializer.AdminUsername)));
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

    [Fact]
    public async Task 并发工厂发送与心跳_seq保持递增不断连()
    {
        // M4-05 基准（8 映射并发）暴露的 P0 回归防护：消息 seq 在调用方构造时刻固化、写线在
        // 发送门内——多线程并发（打洞成功后 0x72/0x76/0x64 与心跳交错）分配序≠写线序 →
        // 服务端 seq_regression 断连（02 §2.4）。工厂重载把组包收进门内（分配序≡写线序）；
        // 本用例 100ms 心跳 + 8 线程单向风暴 + 并发请求，全程无断连且末次请求往返成功。
        var (deviceId, secret) = await SeedDeviceAsync();
        await using var client = new ControlClient(
            new[] { $"127.0.0.1:{_port}" },
            new ControlClientOptions { HeartbeatInterval = TimeSpan.FromMilliseconds(100) },
            deviceId: deviceId,
            deviceSecret: secret);
        await ReadyAsync(client);

        var unknownId = Guid.NewGuid(); // StatsService 对非本人映射静默跳过（M2-08）——纯发送面压力
        var flood = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 50; i++)
                await client.SendAsync(seq => new StatsReport(
                    seq, client.TimestampMs(), MsgType.StatsReport,
                    [new StatsEntry(unknownId, 1, 1, 1)]));
        }));
        var requests = Enumerable.Range(0, 5).Select(_ => client.SendRequestAsync<DeviceListResponse>(
            seq => new DeviceListRequest(seq, client.TimestampMs(), MsgType.DeviceList, 0, 20)));
        await Task.WhenAll(flood.Concat(requests)); // 断连会让 pending 以"连接已断开"失败 → 此处即抛

        Assert.Equal(ControlClientState.Established, client.State); // 无 seq_regression 断连
        var ack = await client.SendRequestAsync<DeviceUpdateAck>(seq => new DeviceUpdate(
            seq, client.TimestampMs(), MsgType.DeviceUpdate, "after-storm"));
        Assert.True(ack.Ok); // 风暴后连接仍可用
    }

    [Fact]
    public async Task 服务端并发推送_入队重盖seq_客户端不断连()
    {
        // 对称方向回归（M4-05 P0 服务端面）：session.SendAsync 入 Channel FIFO 串行，但调用方
        // 构造时刻分配的 seq 与入队序在多线程推送（读循环 Ack 与信令/列表推送并发）下交错 →
        // 客户端按 seq 回退断连。修复=入队前重盖（分配与入队同点）；本用例多线程并发 PushAsync
        // 0x41，客户端全量收到且连接存活。
        var (deviceId, secret) = await SeedDeviceAsync();
        await using var client = NewClient(deviceId, secret);
        var received = 0;
        client.ServerPush += _ => Interlocked.Increment(ref received);
        await ReadyAsync(client);

        var session = _registry.TryGet(deviceId);
        Assert.NotNull(session);

        var pushes = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 50; i++)
                await session.PushAsync(new DeviceListUpdate(session.NextSeq(),
                    session.ServerTimestamp(), MsgType.DeviceListUpdate));
        }));
        await Task.WhenAll(pushes);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (Volatile.Read(ref received) < 400 && !cts.IsCancellationRequested)
            await Task.Delay(20, cts.Token);
        Assert.Equal(400, Volatile.Read(ref received)); // 全量送达（断连=读循环退出即凑不齐）

        Assert.Equal(ControlClientState.Established, client.State); // 客户端 seq 校验未触发断连
        var ack = await client.SendRequestAsync<DeviceUpdateAck>(seq => new DeviceUpdate(
            seq, client.TimestampMs(), MsgType.DeviceUpdate, "after-push-storm"));
        Assert.True(ack.Ok);
    }

    /// <summary>2026-11-12 断网不恢复回归（7×24 稳定性修复）：TCP 连上但对端永不应答
    ///（黑洞路由/挂起中间盒形态）。旧缺陷两族：①HelloAck 读取无超时 → 永久滞留 Handshaking，
    /// 重连循环死等（本测试红态：失败日志恒 0 条）；②SYN 黑洞下 ConnectAsync 超时 OCE 被
    /// RunLoop 旧过滤条件放行 → 穿透杀死整个重连循环（生产日志 13:26:40 后 [control] 再无输出，
    /// 只能重启进程）。修复后：连接+握手全程有界，失败即退避重试，循环持续存活。</summary>
    [Fact]
    public async Task 握手黑洞_超时有界失败_重连循环持续存活()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(16);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var held = new List<Socket>(); // 持住已接受 socket：句柄不被 GC 回收（回收会 RST 对端，失败原因变成连接重置而非握手超时，断言失真）
        var hang = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    var sock = await listener.AcceptSocketAsync(); // 接受后挂起：不读不写不断（黑洞形态）
                    lock (held) held.Add(sock);
                }
                catch (Exception e) when (e is SocketException or ObjectDisposedException) { return; }
            }
        });
        try
        {
            var logs = new ConcurrentQueue<string>();
            await using var client = new ControlClient(new[] { $"127.0.0.1:{port}" },
                new ControlClientOptions
                {
                    ConnectTimeout = TimeSpan.FromMilliseconds(300),
                    RequestTimeout = TimeSpan.FromMilliseconds(300),
                    HeartbeatInterval = TimeSpan.FromHours(1), // 测试不跑心跳周期
                },
                backoff: new BackoffPolicy(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(300)));
            client.Log += logs.Enqueue;

            // 红态（旧代码）：首次连接 TCP 即成、Hello 后死等 HelloAck，失败日志恒 0
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (logs.Count(l => l.Contains("失败")) < 3 && !cts.IsCancellationRequested)
                await Task.Delay(50, cts.Token);
            var failures = logs.Where(l => l.Contains("失败")).ToList();
            Assert.True(failures.Count >= 3,
                $"重连循环未存活（失败仅 {failures.Count} 条）：{string.Join(" | ", logs)}");
            Assert.All(failures, l => Assert.Contains("超时", l)); // 失败原因=握手超时（有界）
        }
        finally
        {
            listener.Stop();
            await hang;
            lock (held) foreach (var s in held) s.Dispose();
        }
    }
}

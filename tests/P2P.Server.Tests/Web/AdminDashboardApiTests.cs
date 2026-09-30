using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using P2P.Server.Data;
using P2P.Server.Services;
using P2P.Server.Web;
using Xunit;

namespace P2P.Server.Tests.Web;

/// <summary>
/// M3-06 仪表盘 API（FR-S-810、04 §3.2）：六指标组对种子数据精确断言——在线数（registry 连接级
/// 真相源，离线即减）/分组数/映射 enabled 计数+TD-22 状态分布（0x62 流水最新一条、无流水 unknown、
/// disabled 不入分布）/中继零会话快照与自启动时刻/STUN 到达吞吐+丢弃三分桶/打洞 24h 成功率（窗内外
/// 行分界）+按小时桶时序（24 桶升序、桶合计=总数、当前桶命中、排除小时不入桶）。
/// 全链夹具同 AdminGroupsApiTests + 真实 RelayService/StunService（Guard 可不经 StartAsync 驱动）。
/// </summary>
public sealed class AdminDashboardApiTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    private readonly DeviceRegistry _registry = new();
    private readonly List<TestPcpClient> _clients = [];
    private ControlServer _server = null!;
    private SignalingCoordinator _signaling = null!;
    private RelayService _relay = null!;
    private StunService _stun = null!;
    private ServerWebHostService _web = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        var factory = new StubFactory(CreateDb);
        using (var init = factory.CreateDbContext())
            DbInitializer.Initialize(init);

        var audit = new AuditLogger(factory);
        _signaling = new SignalingCoordinator(factory, _registry, new Authorizer(factory), audit);
        _relay = new RelayService(factory, _registry, _signaling.ResolveRelayPeers);
        var invalidation = new InvalidationPusher(factory, _registry);
        var pusher = new DeviceListPusher(factory, _registry);
        var groupService = new GroupService(factory, _registry, audit, pusher, invalidation);
        var router = new ControlMessageRouter(
            new RegistrationService(factory, _registry, audit, invalidation: invalidation, listPusher: pusher),
            new UserService(factory, audit, invalidation: invalidation),
            groupService,
            _signaling,
            new MappingService(factory, audit),
            _relay,
            new StatsService(factory, audit),
            audit,
            new LanSegmentService(factory, _registry, audit));
        var admin = new AdminService(factory, _registry, audit, invalidation, listPusher: pusher);
        _stun = new StunService(factory); // Guard 可驱动即可（未监听端口）
        _server = new ControlServer(factory, _registry, router.DispatchAsync);
        await _server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));

        var options = new ServerOptions { Listen = { Web = Random.Shared.Next(21000, 24000) } };
        _web = new ServerWebHostService(options, TimeProvider.System, new AdminSessionStore(TimeProvider.System),
            factory, audit, admin, _registry, groupService, _relay, _stun)
        {
            WebRootOverride = Path.Combine(Path.GetTempPath(), $"p2p-no-webroot-{Guid.NewGuid():N}"),
        };
        await _web.StartAsync(CancellationToken.None);

        _http = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{options.Listen.Web}/"),
        };
        Assert.Equal(HttpStatusCode.OK,
            (await _http.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin" })).StatusCode);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _web.StopAsync(CancellationToken.None);
        await _stun.DisposeAsync();
        await _relay.DisposeAsync();
        foreach (var c in _clients) await c.DisposeAsync();
        await _server.DisposeAsync();
        await _signaling.DisposeAsync();
        _db.Dispose();
    }

    private AppDbContext CreateDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_db.DataSource).Options);

    /// <summary>注册设备并登录（返回在线客户端与设备 id）。</summary>
    private async Task<(TestPcpClient Client, Guid DeviceId)> RegisterLoggedInAsync(string name, string username)
    {
        var client = await TestPcpClient.ConnectAsync(_server.LocalEndPoint!);
        _clients.Add(client);
        await client.HelloAsync(deviceId: null);
        var key = EcKeyPair.Generate();
        await client.SendAsync(new Register(client.NextSeq(), client.Now(), MsgType.Register,
            $"P2P-G06{Guid.NewGuid():N}"[..16], name, "windows", "0.1.0", key.ExportPublicKey(), null, null, null),
            sign: false);
        var ack = await client.ReceiveSkippingPushesAsync<RegisterAck>() ?? throw new IOException("注册无应答");
        client.EstablishWithSecret(Ecies.Decrypt(key, ack.DeviceSecretBox));
        await client.SendAsync(new UserRegister(client.NextSeq(), client.Now(), MsgType.UserRegister,
            username, $"{username}-pass-1"));
        _ = await client.ReceiveSkippingPushesAsync<UserRegisterAck>();
        await client.SendAsync(new UserLogin(client.NextSeq(), client.Now(), MsgType.UserLogin,
            username, $"{username}-pass-1"));
        var login = await client.ReceiveSkippingPushesAsync<UserLoginAck>() ?? throw new IOException("登录无应答");
        Assert.True(login.Ok);
        return (client, ack.DeviceId);
    }

    private async Task<JsonElement> GetAsync(string url)
        => JsonSerializer.Deserialize<JsonElement>(
            await (await _http.GetAsync(url)).Content.ReadAsStreamAsync());

    /// <summary>轮询等待条件成立（registry 离线事件异步到达）。</summary>
    private static async Task WaitAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        Assert.Fail($"等待超时：{what}");
    }

    private static DateTime TruncateHour(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task 六指标组_种子数据精确聚合()
    {
        var now = DateTime.UtcNow;
        var (owner, ownerId) = await RegisterLoggedInAsync("d06-a", "alice");
        var (_, peerId) = await RegisterLoggedInAsync("d06-b", "bob");
        var (ghost, ghostId) = await RegisterLoggedInAsync("d06-c", "carol");
        await ghost.DisposeAsync();
        _clients.Remove(ghost);
        await WaitAsync(() => !_registry.IsOnline(ghostId), "离线设备 registry 下线");

        await owner.SendAsync(new GroupCreate(owner.NextSeq(), owner.Now(), MsgType.GroupCreate,
            "d06-g", JoinPolicy.Free));
        _ = await owner.ReceiveSkippingPushesAsync<GroupCreateAck>();

        // 映射种子：4 行=3 enabled + 1 disabled；0x62 流水（detail 与 StatsService 同构 PascalCase）
        var m1 = Guid.NewGuid(); // 两行流水：failed→direct，最新一条生效
        var m2 = Guid.NewGuid(); // 单行 relay
        var m3 = Guid.NewGuid(); // 无流水 → unknown
        var m4 = Guid.NewGuid(); // disabled：有行不入分布
        await using (var db = CreateDb())
        {
            db.Mappings.AddRange(
                new Mapping { Id = m1, OwnerDeviceId = ownerId, Name = "d06-1", LocalPort = 32001,
                    Proto = "tcp", TargetDeviceId = peerId, TargetAddr = "self", TargetPort = 80,
                    Enabled = true, CreatedAt = now },
                new Mapping { Id = m2, OwnerDeviceId = ownerId, Name = "d06-2", LocalPort = 32002,
                    Proto = "tcp", TargetDeviceId = peerId, TargetAddr = "self", TargetPort = 81,
                    Enabled = true, CreatedAt = now },
                new Mapping { Id = m3, OwnerDeviceId = ownerId, Name = "d06-3", LocalPort = 32003,
                    Proto = "udp", TargetDeviceId = peerId, TargetAddr = "self", TargetPort = 82,
                    Enabled = true, CreatedAt = now },
                new Mapping { Id = m4, OwnerDeviceId = ownerId, Name = "d06-4", LocalPort = 32004,
                    Proto = "tcp", TargetDeviceId = peerId, TargetAddr = "self", TargetPort = 83,
                    Enabled = false, CreatedAt = now });
            db.AuditLogs.AddRange(
                new AuditLog { Ts = now, Event = "mapping_status", DeviceId = ownerId,
                    Detail = JsonSerializer.Serialize(new { MappingId = m1, State = "failed", Detail = (string?)null }) },
                new AuditLog { Ts = now, Event = "mapping_status", DeviceId = ownerId,
                    Detail = JsonSerializer.Serialize(new { MappingId = m1, State = "direct", Detail = (string?)null }) },
                new AuditLog { Ts = now, Event = "mapping_status", DeviceId = ownerId,
                    Detail = JsonSerializer.Serialize(new { MappingId = m2, State = "relay", Detail = (string?)null }) },
                new AuditLog { Ts = now, Event = "mapping_status", DeviceId = ownerId,
                    Detail = JsonSerializer.Serialize(new { MappingId = m4, State = "failed", Detail = (string?)null }) });
            // punch_stats（FK=真设备）：25h 前 1 direct（窗外排除）+ 窗内 2 direct/1 relay/1 failed
            db.PunchStats.AddRange(
                new PunchStat { Ts = now.AddHours(-25), SessionId = Guid.NewGuid(),
                    InitiatorId = ownerId, TargetId = peerId, Proto = "udp", Concurrency = 1,
                    Result = "direct", DurationMs = 100 },
                new PunchStat { Ts = now.AddMinutes(-10), SessionId = Guid.NewGuid(),
                    InitiatorId = ownerId, TargetId = peerId, Proto = "udp", Concurrency = 1,
                    Result = "direct", DurationMs = 110 },
                new PunchStat { Ts = now.AddHours(-3), SessionId = Guid.NewGuid(),
                    InitiatorId = ownerId, TargetId = peerId, Proto = "tcp", Concurrency = 3,
                    Result = "direct", DurationMs = 120 },
                new PunchStat { Ts = now.AddMinutes(-90), SessionId = Guid.NewGuid(),
                    InitiatorId = peerId, TargetId = ownerId, Proto = "udp", Concurrency = 1,
                    Result = "relay", DurationMs = 130 },
                new PunchStat { Ts = now.AddHours(-3).AddMinutes(-5), SessionId = Guid.NewGuid(),
                    InitiatorId = ownerId, TargetId = peerId, Proto = "udp", Concurrency = 1,
                    Result = "failed", Reason = "stun_failed", DurationMs = 140 });
            await db.SaveChangesAsync();
        }

        // STUN 闸驱动：5 到达放行 + 2 auth 丢弃（不经网络——Guard 直调）
        for (var i = 0; i < 5; i++) _stun.Guard.AdmitArrival();
        _stun.Guard.CountAuth();
        _stun.Guard.CountAuth();

        var data = (await GetAsync("/api/dashboard")).GetProperty("data");

        // ① 在线设备数：连接级真相源（第三台已断开）
        Assert.Equal(2, data.GetProperty("onlineDevices").GetInt32());

        // ② 分组数：默认 + 自建
        Assert.Equal(2, data.GetProperty("groups").GetInt32());

        // ③ 活跃映射：enabled 计数 + TD-22 状态分布（disabled 不入分布；无流水 unknown）
        var mappings = data.GetProperty("mappings");
        Assert.Equal(4, mappings.GetProperty("total").GetInt32());
        Assert.Equal(3, mappings.GetProperty("enabled").GetInt32());
        var byStatus = mappings.GetProperty("byStatus");
        Assert.Equal(1, byStatus.GetProperty("direct").GetInt32()); // m1 取最新一条
        Assert.Equal(1, byStatus.GetProperty("relay").GetInt32());
        Assert.Equal(1, byStatus.GetProperty("unknown").GetInt32()); // m3 无流水
        Assert.Equal(0, byStatus.GetProperty("failed").GetInt32());
        Assert.Equal(0, byStatus.GetProperty("invalid").GetInt32());

        // ④ 中继：零会话快照 + 自启动时刻与时长
        var relay = data.GetProperty("relay");
        Assert.Equal(0, relay.GetProperty("sessions").GetInt32());
        Assert.Equal(0, relay.GetProperty("bytesForwarded").GetInt64());
        Assert.True(relay.GetProperty("startedAt").GetDateTime() <= DateTime.UtcNow);
        Assert.True(relay.GetProperty("uptimeSec").GetDouble() >= 0);

        // ⑤ STUN：到达吞吐 + 丢弃三分桶
        var stun = data.GetProperty("stun");
        Assert.Equal(5, stun.GetProperty("admitted").GetInt64());
        Assert.True(stun.GetProperty("qps").GetDouble() > 0);
        var dropped = stun.GetProperty("dropped");
        Assert.Equal(0, dropped.GetProperty("rate").GetInt64());
        Assert.Equal(2, dropped.GetProperty("auth").GetInt64());
        Assert.Equal(0, dropped.GetProperty("circuit").GetInt64());

        // ⑥ 打洞 24h：窗内外分界 + 成功率 + 时序桶
        var punch = data.GetProperty("punch");
        Assert.Equal(4, punch.GetProperty("total24h").GetInt32());
        Assert.Equal(2, punch.GetProperty("direct24h").GetInt32());
        Assert.Equal(1, punch.GetProperty("relay24h").GetInt32());
        Assert.Equal(1, punch.GetProperty("failed24h").GetInt32());
        Assert.Equal(0.75, punch.GetProperty("successRate24h").GetDouble(), 5);

        var hourly = punch.GetProperty("hourly").EnumerateArray().ToList();
        Assert.Equal(24, hourly.Count); // 固定 24 桶
        var starts = hourly.Select(b => b.GetProperty("hourStart").GetInt64()).ToList();
        Assert.Equal(starts.Order(), starts); // 时间升序（最旧→最新）
        Assert.Equal(4, hourly.Sum(b => b.GetProperty("total").GetInt32())); // 桶合计=窗内总数
        Assert.Equal(3, hourly.Sum(b => b.GetProperty("success").GetInt32())); // direct+relay
        // now-10min 行所在桶（锚定种子小时——与断言时刻的跨小时边界无关，桶必在 24 桶集内）
        var seedBucket = hourly.Single(b => b.GetProperty("hourStart").GetInt64()
            == new DateTimeOffset(TruncateHour(now.AddMinutes(-10))).ToUnixTimeMilliseconds());
        Assert.Equal(1, seedBucket.GetProperty("total").GetInt32());
        Assert.Equal(1, seedBucket.GetProperty("success").GetInt32());
        var excludedHour = new DateTimeOffset(TruncateHour(now.AddHours(-25))).ToUnixTimeMilliseconds();
        Assert.DoesNotContain(excludedHour, starts); // 窗外小时不入桶集
    }

    [Fact]
    public async Task 空库_成功率null_各零值快照()
    {
        var data = (await GetAsync("/api/dashboard")).GetProperty("data");

        Assert.Equal(0, data.GetProperty("onlineDevices").GetInt32());
        Assert.Equal(1, data.GetProperty("groups").GetInt32()); // DbInitializer 默认组
        Assert.Equal(0, data.GetProperty("mappings").GetProperty("total").GetInt32());
        Assert.Equal(0, data.GetProperty("punch").GetProperty("total24h").GetInt32());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("punch").GetProperty("successRate24h").ValueKind);
        Assert.Equal(24, data.GetProperty("punch").GetProperty("hourly").GetArrayLength()); // 空库也全桶
        Assert.Equal(0, data.GetProperty("stun").GetProperty("dropped").GetProperty("auth").GetInt64());
    }
}

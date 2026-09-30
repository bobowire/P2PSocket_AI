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
/// M3-08 映射数据 API（FR-S-823、04 §3.2、TD-22）：列表投影与过滤（种子 0x62 流水末次覆盖/
/// 无流水 unknown/disabled 仍投影）+ 0x62 端到端（真控制连接签名上报→/api/mappings 投影跟随，
/// 无 Ack fire-and-forget→轮询收敛）。全链夹具同 AdminUsersApiTests（路由全家桶+Web 宿主）。
/// </summary>
public sealed class AdminMappingsApiTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    private readonly DeviceRegistry _registry = new();
    private readonly List<TestPcpClient> _clients = [];
    private ControlServer _server = null!;
    private SignalingCoordinator _signaling = null!;
    private ServerWebHostService _web = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        var factory = new StubFactory(CreateDb);
        using (var init = factory.CreateDbContext())
            DbInitializer.Initialize(init);

        var audit = new AuditLogger(factory);
        _signaling = new SignalingCoordinator(factory, _registry, new Authorizer(factory), audit);
        var relay = new RelayService(factory, _registry, _signaling.ResolveRelayPeers);
        var invalidation = new InvalidationPusher(factory, _registry);
        var pusher = new DeviceListPusher(factory, _registry);
        var groupService = new GroupService(factory, _registry, audit, pusher, invalidation);
        var router = new ControlMessageRouter(
            new RegistrationService(factory, _registry, audit, invalidation: invalidation, listPusher: pusher),
            new UserService(factory, audit, invalidation: invalidation),
            groupService,
            _signaling,
            new MappingService(factory, audit),
            relay,
            new StatsService(factory, audit),
            audit,
            new LanSegmentService(factory, _registry, audit));
        var admin = new AdminService(factory, _registry, audit, invalidation, listPusher: pusher);
        _server = new ControlServer(factory, _registry, router.DispatchAsync);
        await _server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));

        var options = new ServerOptions { Listen = { Web = Random.Shared.Next(21000, 24000) } };
        _web = new ServerWebHostService(options, TimeProvider.System, new AdminSessionStore(TimeProvider.System),
            factory, audit, admin, _registry, groupService)
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
        foreach (var c in _clients) await c.DisposeAsync();
        await _server.DisposeAsync();
        await _signaling.DisposeAsync();
        _db.Dispose();
    }

    private AppDbContext CreateDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_db.DataSource).Options);

    private async Task<JsonElement> GetAsync(string url)
        => JsonSerializer.Deserialize<JsonElement>(
            await (await _http.GetAsync(url)).Content.ReadAsStreamAsync());

    /// <summary>直写库种设备（列表投影测试无需控制连接；remoteCode/macCode 须随机避 UNIQUE）。</summary>
    private static Device SeedDevice(string name) => new()
    {
        Id = Guid.NewGuid(),
        DeviceName = name,
        Os = "windows",
        MacCode = $"P2P-M08{Guid.NewGuid():N}"[..14],
        RemoteCode = Random.Shared.Next(100000, 1000000).ToString(), // 6 位数字（OQ-8 字符集子集）
        CreatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task 映射列表_投影流量与过滤()
    {
        Guid aId, bId, cId, m1, m2, m3;
        await using (var db = CreateDb())
        {
            var a = SeedDevice("m08-a");
            var b = SeedDevice("m08-b");
            var c = SeedDevice("m08-c");
            db.Devices.AddRange(a, b, c);
            m1 = Guid.NewGuid();
            m2 = Guid.NewGuid();
            m3 = Guid.NewGuid();
            db.Mappings.AddRange(
                new Mapping { Id = m1, OwnerDeviceId = a.Id, TargetDeviceId = b.Id, Name = "web", LocalPort = 8080, Proto = "tcp", TargetAddr = "self", TargetPort = 80, Enabled = true, CreatedAt = DateTime.UtcNow.AddMinutes(-3) },
                new Mapping { Id = m2, OwnerDeviceId = a.Id, TargetDeviceId = c.Id, Name = "dns", LocalPort = 5353, Proto = "udp", TargetAddr = "self", TargetPort = 53, Enabled = false, CreatedAt = DateTime.UtcNow.AddMinutes(-2) },
                new Mapping { Id = m3, OwnerDeviceId = b.Id, TargetDeviceId = a.Id, Name = "ssh", LocalPort = 2222, Proto = "tcp", TargetAddr = "self", TargetPort = 22, Enabled = true, CreatedAt = DateTime.UtcNow.AddMinutes(-1) });
            db.MappingStats.Add(new MappingStat { MappingId = m1, BytesUp = 1000, BytesDown = 2000, RelayBytes = 500, UpdatedAt = DateTime.UtcNow });
            // 0x62 流水：m1 两行（relay 先、direct 后=末次覆盖）；m2 一行 failed；m3 无流水
            db.AuditLogs.AddRange(
                new AuditLog { Ts = DateTime.UtcNow.AddMinutes(-2), Event = "mapping_status", DeviceId = a.Id, Detail = $$"""{"MappingId":"{{m1}}","State":"relay","Detail":null}""" },
                new AuditLog { Ts = DateTime.UtcNow.AddMinutes(-1), Event = "mapping_status", DeviceId = a.Id, Detail = $$"""{"MappingId":"{{m1}}","State":"direct","Detail":null}""" },
                new AuditLog { Ts = DateTime.UtcNow.AddMinutes(-1), Event = "mapping_status", DeviceId = a.Id, Detail = $$"""{"MappingId":"{{m2}}","State":"failed","Detail":null}""" });
            await db.SaveChangesAsync();
            aId = a.Id; bId = b.Id; cId = c.Id;
        }

        var data = (await GetAsync("/api/mappings")).GetProperty("data");
        var items = data.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, data.GetProperty("total").GetInt32()); // 全量（含 disabled）

        var s1 = items.Single(i => i.GetProperty("id").GetGuid() == m1);
        Assert.Equal("web", s1.GetProperty("name").GetString());
        Assert.Equal(8080, s1.GetProperty("localPort").GetInt32());
        Assert.Equal("tcp", s1.GetProperty("proto").GetString());
        Assert.Equal("self", s1.GetProperty("targetAddr").GetString());
        Assert.Equal(80, s1.GetProperty("targetPort").GetInt32());
        Assert.True(s1.GetProperty("enabled").GetBoolean());
        Assert.Equal("m08-a", s1.GetProperty("ownerDeviceName").GetString()); // 归属设备名
        Assert.Equal(aId, s1.GetProperty("ownerDeviceId").GetGuid());
        Assert.False(string.IsNullOrEmpty(s1.GetProperty("ownerRemoteCode").GetString())); // 远程码
        Assert.Equal("m08-b", s1.GetProperty("targetDeviceName").GetString());
        Assert.Equal(1000, s1.GetProperty("bytes").GetProperty("up").GetInt64()); // mapping_stats join
        Assert.Equal(2000, s1.GetProperty("bytes").GetProperty("down").GetInt64());
        Assert.Equal(500, s1.GetProperty("bytes").GetProperty("relay").GetInt64());
        Assert.Equal("direct", s1.GetProperty("status").GetString()); // 末次覆盖（relay→direct）

        var s2 = items.Single(i => i.GetProperty("id").GetGuid() == m2);
        Assert.False(s2.GetProperty("enabled").GetBoolean());
        Assert.Equal("failed", s2.GetProperty("status").GetString()); // disabled 仍投影最后已知态
        Assert.Equal(0, s2.GetProperty("bytes").GetProperty("up").GetInt64()); // 无 stats 行=零值

        Assert.Equal("unknown", items.Single(i => i.GetProperty("id").GetGuid() == m3)
            .GetProperty("status").GetString()); // 无流水=unknown

        // 过滤：按归属设备 / 按投影态
        var byDevice = (await GetAsync($"/api/mappings?deviceId={aId}")).GetProperty("data");
        Assert.Equal(2, byDevice.GetProperty("total").GetInt32());
        Assert.All(byDevice.GetProperty("items").EnumerateArray(),
            i => Assert.Equal(aId, i.GetProperty("ownerDeviceId").GetGuid()));

        Assert.Equal(m1, (await GetAsync("/api/mappings?status=direct")).GetProperty("data")
            .GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid());
        Assert.Equal(m3, (await GetAsync("/api/mappings?status=unknown")).GetProperty("data")
            .GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task 状态上报_投影跟随()
    {
        // 真控制连接（0x62 仅受理本人映射——归属校验在 StatsService）
        var a = await TestPcpClient.ConnectAsync(_server.LocalEndPoint!);
        _clients.Add(a);
        await a.HelloAsync(deviceId: null);
        var key = EcKeyPair.Generate();
        await a.SendAsync(new Register(a.NextSeq(), a.Now(), MsgType.Register,
            $"P2P-S08{Guid.NewGuid():N}"[..14], "m08-live-a", "windows", "0.1.0", key.ExportPublicKey(), null, null, null),
            sign: false);
        var ack = await a.ReceiveSkippingPushesAsync<RegisterAck>() ?? throw new IOException("注册无应答");
        a.EstablishWithSecret(Ecies.Decrypt(key, ack.DeviceSecretBox));

        var b = SeedDevice("m08-live-b");
        Guid mappingId;
        await using (var db = CreateDb())
        {
            db.Devices.Add(b);
            mappingId = Guid.NewGuid();
            db.Mappings.Add(new Mapping { Id = mappingId, OwnerDeviceId = ack.DeviceId, TargetDeviceId = b.Id,
                Name = "live", LocalPort = 9000, Proto = "tcp", TargetAddr = "self", TargetPort = 90, Enabled = true, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        // 0x62 fire-and-forget 无 Ack → 轮询投影收敛
        await a.SendAsync(new MappingStatus(a.NextSeq(), a.Now(), MsgType.MappingStatus, mappingId, "direct", null));
        await WaitStatusAsync(mappingId, "direct");
        await a.SendAsync(new MappingStatus(a.NextSeq(), a.Now(), MsgType.MappingStatus, mappingId, "failed", "stalled"));
        await WaitStatusAsync(mappingId, "failed"); // 状态迁移后投影跟随
    }

    private async Task WaitStatusAsync(Guid mappingId, string expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            var data = (await GetAsync("/api/mappings")).GetProperty("data");
            var item = data.GetProperty("items").EnumerateArray()
                .SingleOrDefault(i => i.GetProperty("id").GetGuid() == mappingId);
            if (item.ValueKind != JsonValueKind.Undefined
                && item.GetProperty("status").GetString() == expected)
                return;
            await Task.Delay(50);
        }
        Assert.Fail($"等待投影为 {expected} 超时（mappingId={mappingId}）");
    }
}

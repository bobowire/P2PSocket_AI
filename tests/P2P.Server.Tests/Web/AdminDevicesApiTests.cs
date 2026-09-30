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
/// M3-04 设备管理 API（FR-S-821/103/903、04 §3.2）：列表与库+registry 一致（在线态/归属/分组/远程码）、
/// 禁用=在线设备即时断连+引用方 0x75(device_disabled)、解绑=行清理全集+同 MAC 重注册全新身份、
/// 重置码=旧码 4003+引用方 0x75(remote_code_reset)+新码可用。全链夹具=AdminUsersApiTests 同款
/// （ControlServer+路由全家桶+AdminService 带失效链与 0x41 推送）+共享同 registry/AdminService。
/// </summary>
public sealed class AdminDevicesApiTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    private readonly DeviceRegistry _registry = new();
    private readonly List<TestPcpClient> _clients = [];
    private ControlServer _server = null!;
    private SignalingCoordinator _signaling = null!;
    private RelayService _relay = null!;
    private AdminService _admin = null!;
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
        _admin = new AdminService(factory, _registry, audit, invalidation, listPusher: pusher); // Web 载体直调实例
        _server = new ControlServer(factory, _registry, router.DispatchAsync);
        await _server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));

        var options = new ServerOptions { Listen = { Web = Random.Shared.Next(21000, 24000) } };
        _web = new ServerWebHostService(options, TimeProvider.System, new AdminSessionStore(TimeProvider.System),
            factory, audit, _admin, _registry, groupService)
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
        await _relay.DisposeAsync();
        foreach (var c in _clients) await c.DisposeAsync();
        await _server.DisposeAsync();
        await _signaling.DisposeAsync();
        _db.Dispose();
    }

    private AppDbContext CreateDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_db.DataSource).Options);

    /// <summary>注册设备（可指定 MAC/复用账号登录）——返回在线客户端、设备 id、用户 id、MAC。</summary>
    private async Task<(TestPcpClient Client, Guid DeviceId, Guid? UserId, string Mac)> RegisterDeviceAsync(
        string name, string? username = null, string? mac = null)
    {
        var client = await TestPcpClient.ConnectAsync(_server.LocalEndPoint!);
        _clients.Add(client);
        await client.HelloAsync(deviceId: null);
        var key = EcKeyPair.Generate();
        mac ??= $"P2P-D04{Guid.NewGuid():N}"[..16];
        await client.SendAsync(new Register(client.NextSeq(), client.Now(), MsgType.Register,
            mac, name, "windows", "0.1.0", key.ExportPublicKey(), null, null, null), sign: false);
        var ack = await client.ReceiveSkippingPushesAsync<RegisterAck>() ?? throw new IOException("注册无应答");
        client.EstablishWithSecret(Ecies.Decrypt(key, ack.DeviceSecretBox));

        Guid? userId = null;
        if (username is not null)
        {
            await client.SendAsync(new UserRegister(client.NextSeq(), client.Now(), MsgType.UserRegister,
                username, $"{username}-pass-1"));
            _ = await client.ReceiveSkippingPushesAsync<UserRegisterAck>();
            await client.SendAsync(new UserLogin(client.NextSeq(), client.Now(), MsgType.UserLogin,
                username, $"{username}-pass-1"));
            var login = await client.ReceiveSkippingPushesAsync<UserLoginAck>() ?? throw new IOException("登录无应答");
            Assert.True(login.Ok);
            await using var db = CreateDb();
            userId = db.Users.AsNoTracking().Single(u => u.Username == username).Id;
        }
        return (client, ack.DeviceId, userId, mac);
    }

    /// <summary>0x60 建映射（目标按远程码解析——同账号可见性前提）。</summary>
    private static async Task<Guid> CreateMappingAsync(TestPcpClient client, string remoteCode, int localPort)
    {
        await client.SendAsync(new MappingUpsert(client.NextSeq(), client.Now(), MsgType.MappingUpsert,
            null, "m-d04", (ushort)localPort, "tcp", remoteCode, "self", 80, true));
        var ack = await client.ReceiveSkippingPushesAsync<MappingUpsertAck>() ?? throw new IOException("0x60 无应答");
        return ack.MappingId;
    }

    private async Task<string> GetRemoteCodeAsync(Guid deviceId)
    {
        await using var db = CreateDb();
        return (await db.Devices.AsNoTracking().SingleAsync(d => d.Id == deviceId)).RemoteCode;
    }

    private async Task<JsonElement> GetAsync(string url)
        => JsonSerializer.Deserialize<JsonElement>(
            await (await _http.GetAsync(url)).Content.ReadAsStreamAsync());

    private async Task<int> AuditCountAsync(string @event)
    {
        await using var db = CreateDb();
        return await db.AuditLogs.AsNoTracking().CountAsync(a => a.Event == @event);
    }

    /// <summary>轮询等待条件成立（踢线/离线事件异步到达）。</summary>
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

    // ── 列表一致性与库+registry 一致 ──────────────────────────────────────

    [Fact]
    public async Task 列表与库和registry一致_在线态归属分组()
    {
        var (dev1, id1, _, _) = await RegisterDeviceAsync("d04-alice-1", "alice");
        var (_, id2, _, _) = await RegisterDeviceAsync("d04-alice-2", "alice");
        var (dev3, id3, _, _) = await RegisterDeviceAsync("d04-orphan");

        // alice 第一台建组（创建者即首成员，M2-09）→ groups 字段非空对照
        await dev1.SendAsync(new GroupCreate(dev1.NextSeq(), dev1.Now(), MsgType.GroupCreate,
            "d04-g", JoinPolicy.Free));
        _ = await dev1.ReceiveSkippingPushesAsync<GroupCreateAck>();

        // 未登录设备断开后 registry 离线（在线态=连接级真相源）
        await dev3.DisposeAsync();
        _clients.Remove(dev3);
        await WaitAsync(() => !_registry.IsOnline(id3), "离线设备 registry 下线");

        var data = (await GetAsync("/api/devices")).GetProperty("data");
        var items = data.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, items.Count);

        var row1 = items.Single(d => Guid.Parse(d.GetProperty("deviceId").GetString()!) == id1);
        Assert.Equal("d04-alice-1", row1.GetProperty("deviceName").GetString());
        Assert.Equal(await GetRemoteCodeAsync(id1), row1.GetProperty("remoteCode").GetString());
        Assert.StartsWith("100.64.", row1.GetProperty("virtualIp").GetString());
        Assert.Equal("alice", row1.GetProperty("ownerUsername").GetString());
        // 注册即入默认分组（03 §6）+ 自建组首成员（M2-09）
        Assert.Equal("d04-g,默认分组", string.Join(",", row1.GetProperty("groups").EnumerateArray()
            .Select(g => g.GetString()).Order()));
        Assert.True(row1.GetProperty("online").GetBoolean());
        Assert.False(row1.GetProperty("disabled").GetBoolean());

        var row2 = items.Single(d => Guid.Parse(d.GetProperty("deviceId").GetString()!) == id2);
        Assert.Equal("alice", row2.GetProperty("ownerUsername").GetString());
        Assert.Equal("默认分组", string.Join(",", row2.GetProperty("groups").EnumerateArray()
            .Select(g => g.GetString()).Order()));
        Assert.True(row2.GetProperty("online").GetBoolean());

        var row3 = items.Single(d => Guid.Parse(d.GetProperty("deviceId").GetString()!) == id3);
        Assert.Null(row3.GetProperty("ownerUsername").GetString()); // 未归属（注册即入组但未登录）
        Assert.Equal("默认分组", string.Join(",", row3.GetProperty("groups").EnumerateArray()
            .Select(g => g.GetString()).Order()));
        Assert.False(row3.GetProperty("online").GetBoolean()); // registry 离线如实呈现
    }

    // ── 禁用：即时踢线 + 引用方 0x75 ─────────────────────────────────────

    [Fact]
    public async Task 禁用_在线设备断连_引用方0x75_启用复位()
    {
        var (_, id1, _, _) = await RegisterDeviceAsync("d04-b1", "alice");
        var (dev2, id2, _, _) = await RegisterDeviceAsync("d04-b2", "alice");
        _ = await CreateMappingAsync(dev2, await GetRemoteCodeAsync(id1), 31001); // 引用 id1

        Assert.Equal(HttpStatusCode.OK,
            (await _http.PostAsync($"/api/devices/{id1}/disable", null)).StatusCode);

        // 引用方 0x75(device_disabled)
        var inv = await dev2.ReceiveSkippingPushesAsync<Invalidation>();
        Assert.NotNull(inv);
        Assert.Equal(InvalidationReason.DeviceDisabled, inv.Reason);

        // 即时踢线（进程内直调，无 30s 兜底等待）
        await WaitAsync(() => !_registry.IsOnline(id1), "被禁设备踢线下线");
        await using (var db = CreateDb())
            Assert.True((await db.Devices.AsNoTracking().SingleAsync(d => d.Id == id1)).Disabled);
        Assert.Equal(1, await AuditCountAsync("device_disable"));

        // 启用复位（恢复靠设备重连）
        Assert.Equal(HttpStatusCode.OK,
            (await _http.PostAsync($"/api/devices/{id1}/enable", null)).StatusCode);
        await using var db2 = CreateDb();
        Assert.False((await db2.Devices.AsNoTracking().SingleAsync(d => d.Id == id1)).Disabled);
        Assert.Equal(1, await AuditCountAsync("device_enable"));
    }

    // ── 解绑：行清理全集 + 同 MAC 重注册全新身份 ─────────────────────────

    [Fact]
    public async Task 解绑_行清理_同MAC重注册新身份()
    {
        var (dev1, id1, _, mac) = await RegisterDeviceAsync("d04-c1", "carol");
        var oldCode = await GetRemoteCodeAsync(id1);
        await dev1.SendAsync(new GroupCreate(dev1.NextSeq(), dev1.Now(), MsgType.GroupCreate,
            "d04-cg", JoinPolicy.Free));
        _ = await dev1.ReceiveSkippingPushesAsync<GroupCreateAck>();

        Assert.Equal(HttpStatusCode.OK,
            (await _http.PostAsync($"/api/devices/{id1}/unbind", null)).StatusCode);
        await WaitAsync(() => !_registry.IsOnline(id1), "解绑踢线下线");

        await using (var db = CreateDb())
        {
            Assert.Equal(0, await db.Devices.AsNoTracking().CountAsync(d => d.MacCode == mac));
            Assert.Equal(0, await db.GroupMembers.AsNoTracking().CountAsync(m => m.DeviceId == id1));
            Assert.Equal(1, await db.Groups.AsNoTracking().CountAsync(g => g.Name == "d04-cg"
                && !db.GroupMembers.Any(m => m.GroupId == g.Id))); // 组行保留（属用户），成员行清理
        }
        Assert.Equal(1, await AuditCountAsync("unbind_admin"));

        // 同 MAC 重注册=全新身份（新 deviceId/新远程码）
        var (dev2, id2, _, _) = await RegisterDeviceAsync("d04-c1r", "carol", mac);
        Assert.NotEqual(id1, id2);
        Assert.NotEqual(oldCode, await GetRemoteCodeAsync(id2));
    }

    // ── 远程码重置：旧码 4003 + 引用方 0x75 + 新码可用 ────────────────────

    [Fact]
    public async Task 重置码_引用方0x75_旧码4003_新码可用()
    {
        var (_, id1, _, _) = await RegisterDeviceAsync("d04-r1", "alice");
        var (dev2, _, _, _) = await RegisterDeviceAsync("d04-r2", "alice");
        var oldCode = await GetRemoteCodeAsync(id1);
        _ = await CreateMappingAsync(dev2, oldCode, 31002); // 引用旧码

        var resp = await _http.PostAsync($"/api/devices/{id1}/reset-remote-code", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStreamAsync());
        var newCode = body.GetProperty("data").GetProperty("remoteCode").GetString();
        Assert.NotNull(newCode);
        Assert.NotEqual(oldCode, newCode);
        Assert.Equal(newCode, await GetRemoteCodeAsync(id1));
        Assert.Equal(1, await AuditCountAsync("remote_code_reset"));

        // 引用方 0x75(remote_code_reset)
        var inv = await dev2.ReceiveSkippingPushesAsync<Invalidation>();
        Assert.NotNull(inv);
        Assert.Equal(InvalidationReason.RemoteCodeReset, inv.Reason);

        // 旧码 0x60 → 4003；新码 → 可用
        await dev2.SendAsync(new MappingUpsert(dev2.NextSeq(), dev2.Now(), MsgType.MappingUpsert,
            null, "m-d04-old", 31003, "tcp", oldCode, "self", 80, true));
        var err = await dev2.ReceiveSkippingPushesAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.RemoteCodeInvalid, err!.Code);
        var mappingId = await CreateMappingAsync(dev2, newCode!, 31004);
        Assert.NotEqual(Guid.Empty, mappingId);
    }
}

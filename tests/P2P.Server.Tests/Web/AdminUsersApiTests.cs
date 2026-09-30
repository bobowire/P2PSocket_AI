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
/// M3-03 用户管理 API（FR-S-820/204、04 §3.2）：列表分页与字段、禁用=进程内直调即时降级
/// passive+0x75（区别 CLI 30s 兜底）、enable 放行、密码重置=临时密码一次性返回（旧密码失效）、
/// 内置管理员守卫、未知 id 404。全链夹具=AdminServiceTests 同款（ControlServer+路由全家桶+
/// AdminService 带失效链）+ ServerWebHost 共享同 registry/AdminService。
/// </summary>
public sealed class AdminUsersApiTests : IAsyncLifetime
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
    private Guid _adminUserId;

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
        _admin = new AdminService(factory, _registry, audit, invalidation); // Web 载体直调实例
        _server = new ControlServer(factory, _registry, router.DispatchAsync);
        await _server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));

        var options = new ServerOptions { Listen = { Web = Random.Shared.Next(21000, 24000) } };
        _web = new ServerWebHostService(options, TimeProvider.System, new AdminSessionStore(TimeProvider.System),
            factory, audit, _admin)
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
        await using (var db = factory.CreateDbContext())
            _adminUserId = db.Users.Single(u => u.IsAdmin).Id;
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

    /// <summary>注册设备并登录指定用户（返回在线客户端、设备 id、用户 id）。</summary>
    private async Task<(TestPcpClient Client, Guid DeviceId, Guid UserId)> RegisterLoggedInAsync(string name, string username)
    {
        var client = await TestPcpClient.ConnectAsync(_server.LocalEndPoint!);
        _clients.Add(client);
        await client.HelloAsync(deviceId: null);
        var key = EcKeyPair.Generate();
        var mac = $"P2P-USR{Guid.NewGuid():N}"[..16];
        await client.SendAsync(new Register(client.NextSeq(), client.Now(), MsgType.Register,
            mac, name, "windows", "0.1.0", key.ExportPublicKey(), null, null, null), sign: false);
        var ack = await client.ReceiveSkippingPushesAsync<RegisterAck>() ?? throw new IOException("注册无应答");
        client.EstablishWithSecret(Ecies.Decrypt(key, ack.DeviceSecretBox));

        await client.SendAsync(new UserRegister(client.NextSeq(), client.Now(), MsgType.UserRegister,
            username, $"{username}-pass-1"));
        _ = await client.ReceiveSkippingPushesAsync<UserRegisterAck>();
        await client.SendAsync(new UserLogin(client.NextSeq(), client.Now(), MsgType.UserLogin,
            username, $"{username}-pass-1"));
        var login = await client.ReceiveSkippingPushesAsync<UserLoginAck>() ?? throw new IOException("登录无应答");
        Assert.True(login.Ok);
        await using var db = CreateDb();
        return (client, ack.DeviceId, db.Users.Single(u => u.Username == username).Id);
    }

    private async Task<JsonElement> GetAsync(string url)
        => JsonSerializer.Deserialize<JsonElement>(
            await (await _http.GetAsync(url)).Content.ReadAsStreamAsync());

    private async Task<int> AuditCountAsync(string @event)
    {
        await using var db = CreateDb();
        return await db.AuditLogs.AsNoTracking().CountAsync(a => a.Event == @event);
    }

    // ── 列表与分页 ───────────────────────────────────────────────────────

    [Fact]
    public async Task 列表字段与分页()
    {
        var (_, _, userId) = await RegisterLoggedInAsync("u1-dev", "alice");

        var body = await GetAsync("/api/users");
        Assert.Equal(0, body.GetProperty("code").GetInt32());
        var data = body.GetProperty("data");
        Assert.Equal(2, data.GetProperty("total").GetInt32());

        var alice = data.GetProperty("items").EnumerateArray()
            .Single(u => u.GetProperty("username").GetString() == "alice");
        Assert.Equal(userId, Guid.Parse(alice.GetProperty("id").GetString()!));
        Assert.False(alice.GetProperty("disabled").GetBoolean());
        Assert.False(alice.GetProperty("isAdmin").GetBoolean());
        Assert.Equal(1, alice.GetProperty("deviceCount").GetInt32());
        Assert.True(alice.GetProperty("createdAt").GetDateTime() > DateTime.UtcNow.AddDays(-1));

        var adminRow = data.GetProperty("items").EnumerateArray()
            .Single(u => u.GetProperty("username").GetString() == "admin");
        Assert.True(adminRow.GetProperty("isAdmin").GetBoolean());

        // 分页：pageSize=1 两页收齐
        var p1 = (await GetAsync("/api/users?page=1&pageSize=1")).GetProperty("data");
        var p2 = (await GetAsync("/api/users?page=2&pageSize=1")).GetProperty("data");
        Assert.Equal(1, p1.GetProperty("items").GetArrayLength());
        Assert.Equal(1, p2.GetProperty("items").GetArrayLength());
        var names = new[] { p1, p2 }.SelectMany(p => p.GetProperty("items").EnumerateArray()
            .Select(u => u.GetProperty("username").GetString()));
        Assert.Equal("admin,alice", string.Join(",", names.Order()));
    }

    // ── 禁用/启用（即时生效面）──────────────────────────────────────────

    [Fact]
    public async Task 禁用_即时降级passive与0x75_区别CLI30s兜底()
    {
        var (client, deviceId, userId) = await RegisterLoggedInAsync("u2-dev", "bob");
        Assert.Equal(HttpStatusCode.OK,
            (await _http.PostAsync($"/api/users/{userId}/disable", null)).StatusCode);

        // 进程内直调：registry 会话即时 passive（无 30s 兜底等待）
        var session = _registry.TryGet(deviceId);
        Assert.NotNull(session);
        Assert.Equal(CapabilityMode.Passive, session!.Capability);

        // 0x75(user_disabled, newCapability=passive) 到达在线设备
        var inv = await client.ReceiveSkippingPushesAsync<Invalidation>();
        Assert.NotNull(inv);
        Assert.Equal(InvalidationReason.UserDisabled, inv.Reason);
        Assert.Equal(1, await AuditCountAsync("user_disable"));

        // 读侧：禁用期间新会话登录拒绝
        var c2 = await TestPcpClient.ConnectAsync(_server.LocalEndPoint!);
        _clients.Add(c2);
        await c2.HelloAsync(deviceId: null);
        var key = EcKeyPair.Generate();
        await c2.SendAsync(new Register(c2.NextSeq(), c2.Now(), MsgType.Register,
            $"P2P-U2B{Guid.NewGuid():N}"[..16], "bob-2", "windows", "0.1.0", key.ExportPublicKey(), null, null, null),
            sign: false);
        var ack2 = await c2.ReceiveSkippingPushesAsync<RegisterAck>();
        c2.EstablishWithSecret(Ecies.Decrypt(key, ack2!.DeviceSecretBox));
        await c2.SendAsync(new UserLogin(c2.NextSeq(), c2.Now(), MsgType.UserLogin, "bob", "bob-pass-1"));
        var denied = await c2.ReceiveSkippingPushesAsync<UserLoginAck>();
        Assert.False(denied!.Ok);

        // enable 放行：重新登录成功
        Assert.Equal(HttpStatusCode.OK,
            (await _http.PostAsync($"/api/users/{userId}/enable", null)).StatusCode);
        await c2.SendAsync(new UserLogin(c2.NextSeq(), c2.Now(), MsgType.UserLogin, "bob", "bob-pass-1"));
        var ok = await c2.ReceiveSkippingPushesAsync<UserLoginAck>();
        Assert.True(ok!.Ok);
        Assert.Equal(1, await AuditCountAsync("user_enable"));
    }

    // ── 密码重置 ────────────────────────────────────────────────────────

    [Fact]
    public async Task 密码重置_临时密码一次返回_旧密码失效()
    {
        var (_, _, userId) = await RegisterLoggedInAsync("u3-dev", "carol");

        var resp = await _http.PutAsync($"/api/users/{userId}/password-reset", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStreamAsync());
        var temp = body.GetProperty("data").GetProperty("tempPassword").GetString();
        Assert.NotNull(temp);
        Assert.Equal(10, temp!.Length);

        Assert.Equal(1, await AuditCountAsync("user_password_reset"));

        // 旧密码失效、临时密码可登（新会话读侧验证）
        var c = await TestPcpClient.ConnectAsync(_server.LocalEndPoint!);
        _clients.Add(c);
        await c.HelloAsync(deviceId: null);
        var key = EcKeyPair.Generate();
        await c.SendAsync(new Register(c.NextSeq(), c.Now(), MsgType.Register,
            $"P2P-U3B{Guid.NewGuid():N}"[..16], "carol-2", "windows", "0.1.0", key.ExportPublicKey(), null, null, null),
            sign: false);
        var ack = await c.ReceiveSkippingPushesAsync<RegisterAck>();
        c.EstablishWithSecret(Ecies.Decrypt(key, ack!.DeviceSecretBox));
        await c.SendAsync(new UserLogin(c.NextSeq(), c.Now(), MsgType.UserLogin, "carol", "carol-pass-1"));
        Assert.False((await c.ReceiveSkippingPushesAsync<UserLoginAck>())!.Ok);
        await c.SendAsync(new UserLogin(c.NextSeq(), c.Now(), MsgType.UserLogin, "carol", temp));
        Assert.True((await c.ReceiveSkippingPushesAsync<UserLoginAck>())!.Ok);
    }

    // ── 守卫与 404 ───────────────────────────────────────────────────────

    [Fact]
    public async Task 内置管理员守卫与未知id()
    {
        var disableAdmin = await _http.PostAsync($"/api/users/{_adminUserId}/disable", null);
        Assert.Equal(HttpStatusCode.BadRequest, disableAdmin.StatusCode);
        Assert.Equal(1003, JsonSerializer.Deserialize<JsonElement>(
            await disableAdmin.Content.ReadAsStreamAsync()).GetProperty("code").GetInt32());

        var resetAdmin = await _http.PutAsync($"/api/users/{_adminUserId}/password-reset", null);
        Assert.Equal(HttpStatusCode.BadRequest, resetAdmin.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound,
            (await _http.PostAsync($"/api/users/{Guid.NewGuid()}/disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _http.PostAsync("/api/users/not-a-guid/disable", null)).StatusCode); // 参数错误 1001
    }
}

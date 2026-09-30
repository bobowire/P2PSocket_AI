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
/// M3-05 分组与审批 API（FR-S-822/305、04 §3.2）：总览聚合（成员数/策略/owner/是否默认/待审数）、
/// 默认分组策略双写（键+行——新申请走新策略）、跨分组待审批单投影、Web 审批与 0x53 客户端侧
/// 审批同一申请单不双入组（GroupService.DecideJoinRequestAsync 共享核）。全链夹具同
/// AdminDevicesApiTests（ControlServer+路由全家桶+共享 GroupService/AdminService）。
/// </summary>
public sealed class AdminGroupsApiTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    private readonly DeviceRegistry _registry = new();
    private readonly List<TestPcpClient> _clients = [];
    private ControlServer _server = null!;
    private SignalingCoordinator _signaling = null!;
    private RelayService _relay = null!;
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
            $"P2P-G05{Guid.NewGuid():N}"[..16], name, "windows", "0.1.0", key.ExportPublicKey(), null, null, null),
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

    /// <summary>建 approval 组并生成邀请码（返回 groupId 与码）。</summary>
    private static async Task<(Guid GroupId, string Code)> CreateApprovalGroupAsync(TestPcpClient owner, string name)
    {
        await owner.SendAsync(new GroupCreate(owner.NextSeq(), owner.Now(), MsgType.GroupCreate,
            name, JoinPolicy.Approval));
        var group = await owner.ReceiveSkippingPushesAsync<GroupCreateAck>() ?? throw new IOException("建组无应答");
        await owner.SendAsync(new GroupInviteGen(owner.NextSeq(), owner.Now(), MsgType.GroupInviteGen,
            group.GroupId, Revoke: false));
        var gen = await owner.ReceiveSkippingPushesAsync<GroupInviteGenAck>() ?? throw new IOException("生码无应答");
        Assert.True(gen!.Ok);
        return (group.GroupId, gen.InviteCode!);
    }

    private async Task<JsonElement> GetAsync(string url)
        => JsonSerializer.Deserialize<JsonElement>(
            await (await _http.GetAsync(url)).Content.ReadAsStreamAsync());

    private async Task<int> AuditCountAsync(string @event)
    {
        await using var db = CreateDb();
        return await db.AuditLogs.AsNoTracking().CountAsync(a => a.Event == @event);
    }

    // ── 总览聚合 ────────────────────────────────────────────────────────

    [Fact]
    public async Task 总览聚合_字段与计数正确()
    {
        var (owner, _) = await RegisterLoggedInAsync("g05-owner", "alice");
        var (joiner, _) = await RegisterLoggedInAsync("g05-joiner", "bob");
        var (groupId, code) = await CreateApprovalGroupAsync(owner, "g05-审");

        // bob 凭码申请（approval → pending）
        await joiner.SendAsync(new GroupJoin(joiner.NextSeq(), joiner.Now(), MsgType.GroupJoin, code));
        _ = await joiner.ReceiveSkippingPushesAsync<ErrorMessage>();

        var items = (await GetAsync("/api/groups")).GetProperty("data").GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);

        var def = items.Single(g => g.GetProperty("isDefault").GetBoolean());
        Assert.Equal("默认分组", def.GetProperty("name").GetString());
        Assert.Equal("admin", def.GetProperty("ownerUsername").GetString());
        Assert.Equal("free", def.GetProperty("joinPolicy").GetString());
        Assert.Equal(2, def.GetProperty("memberCount").GetInt32()); // owner+joiner 注册即入
        Assert.Equal(0, def.GetProperty("pendingCount").GetInt32());

        var grp = items.Single(g => g.GetProperty("groupId").GetGuid() == groupId);
        Assert.Equal("g05-审", grp.GetProperty("name").GetString());
        Assert.Equal("approval", grp.GetProperty("joinPolicy").GetString());
        Assert.Equal("alice", grp.GetProperty("ownerUsername").GetString());
        Assert.Equal(1, grp.GetProperty("memberCount").GetInt32()); // 仅创建者
        Assert.Equal(1, grp.GetProperty("pendingCount").GetInt32());
        Assert.True(grp.GetProperty("createdAt").GetDateTime() > DateTime.UtcNow.AddDays(-1));
    }

    // ── 默认分组策略 ────────────────────────────────────────────────────

    [Fact]
    public async Task 默认策略双写_修改后新申请走新策略()
    {
        // 校验：非法值 400
        var bad = await _http.PutAsJsonAsync("/api/groups/default", new { policy = "whatever" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // 默认组无邀请码（owner=admin 无控制会话）——测试侧直写库铺路
        await using (var db = CreateDb())
        {
            var def = await db.Groups.SingleAsync(g => g.IsDefault);
            def.InviteCode = "d05cod";
            await db.SaveChangesAsync();
        }

        // 切 approval：键+行双写
        Assert.Equal(HttpStatusCode.OK,
            (await _http.PutAsJsonAsync("/api/groups/default", new { policy = "approval" })).StatusCode);
        await using (var db = CreateDb())
        {
            Assert.Equal("approval", (await db.ServerConfig.AsNoTracking()
                .SingleAsync(c => c.Key == "default_join_policy")).Value);
            Assert.Equal("approval", (await db.Groups.AsNoTracking()
                .SingleAsync(g => g.IsDefault)).JoinPolicy);
        }
        Assert.Equal(1, await AuditCountAsync("default_join_policy_change"));

        // 新申请走新策略：注册即已入默认组（03 §6），须先 0x52 退组再凭码申请 → 3002 待审批
        Guid defId;
        await using (var db0 = CreateDb())
            defId = db0.Groups.AsNoTracking().Single(g => g.IsDefault).Id;
        var (joiner, _) = await RegisterLoggedInAsync("g05-d1", "carol");
        await joiner.SendAsync(new GroupLeave(joiner.NextSeq(), joiner.Now(), MsgType.GroupLeave, defId));
        Assert.True((await joiner.ReceiveSkippingPushesAsync<GroupLeaveAck>())!.Ok);
        await joiner.SendAsync(new GroupJoin(joiner.NextSeq(), joiner.Now(), MsgType.GroupJoin, "d05cod"));
        var wait = await joiner.ReceiveSkippingPushesAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.GroupNeedApproval, wait!.Code);
        await using var db2 = CreateDb();
        Assert.Equal(1, await db2.JoinRequests.CountAsync(r => r.Status == "pending" && r.GroupId == defId));

        // 切回 free：清残留 pending 直接入组（0x51 free 分支）
        Assert.Equal(HttpStatusCode.OK,
            (await _http.PutAsJsonAsync("/api/groups/default", new { policy = "free" })).StatusCode);
        var (joiner2, _) = await RegisterLoggedInAsync("g05-d2", "dave");
        await joiner2.SendAsync(new GroupLeave(joiner2.NextSeq(), joiner2.Now(), MsgType.GroupLeave, defId));
        Assert.True((await joiner2.ReceiveSkippingPushesAsync<GroupLeaveAck>())!.Ok);
        await joiner2.SendAsync(new GroupJoin(joiner2.NextSeq(), joiner2.Now(), MsgType.GroupJoin, "d05cod"));
        var ack = await joiner2.ReceiveSkippingPushesAsync<GroupJoinAck>();
        Assert.NotNull(ack);
    }

    // ── 审批队列与 Web/0x53 同一申请单 ──────────────────────────────────

    [Fact]
    public async Task 审批队列_Web审批推送0x41_与0x53同单不双入组()
    {
        var (owner, _) = await RegisterLoggedInAsync("g05-o2", "alice");
        var (joiner, joinerId) = await RegisterLoggedInAsync("g05-j2", "bob");
        var (_, code) = await CreateApprovalGroupAsync(owner, "g05-审2");
        await joiner.SendAsync(new GroupJoin(joiner.NextSeq(), joiner.Now(), MsgType.GroupJoin, code));
        _ = await joiner.ReceiveSkippingPushesAsync<ErrorMessage>(); // 3002

        // 队列投影：组名/申请人/owner
        var items = (await GetAsync("/api/group-requests?status=pending"))
            .GetProperty("data").GetProperty("items").EnumerateArray().ToList();
        var row = Assert.Single(items);
        var requestId = row.GetProperty("requestId").GetGuid();
        Assert.Equal("g05-审2", row.GetProperty("groupName").GetString());
        Assert.Equal("g05-j2", row.GetProperty("deviceName").GetString());
        Assert.Equal("alice", row.GetProperty("ownerUsername").GetString());
        Assert.Equal(joinerId, row.GetProperty("deviceId").GetGuid());
        Assert.True(row.GetProperty("createdAt").GetDateTime() > DateTime.UtcNow.AddDays(-1));

        // 0x53 所有者 List 看到同一单（同单跨端可见）
        await owner.SendAsync(new JoinRequests(owner.NextSeq(), owner.Now(), MsgType.JoinRequests,
            JoinRequestAction.List, GroupIdByCode(code), null));
        var queue = await owner.ReceiveSkippingPushesAsync<JoinRequestsResponse>();
        Assert.Equal(requestId, Assert.Single(queue!.Items).RequestId);

        // Web 审批：入组 + 双方 0x41
        Assert.Equal(HttpStatusCode.OK,
            (await _http.PostAsync($"/api/group-requests/{requestId}/approve", null)).StatusCode);
        await using (var db = CreateDb())
        {
            var g2a = await db.Groups.AsNoTracking().SingleAsync(g => g.Name == "g05-审2");
            Assert.Equal(1, await db.GroupMembers.CountAsync(m => m.DeviceId == joinerId && m.GroupId == g2a.Id));
        }
        Assert.Equal(1, await AuditCountAsync("group_join_approve"));
        // 0x41 是推送帧本身：严格读（Skipping 助手按定义跳过推送帧——对 0x41 类型会永久跳过）
        var push1 = await owner.ReceiveAsync<DeviceListUpdate>();
        var push2 = await joiner.ReceiveAsync<DeviceListUpdate>();
        Assert.NotNull(push1);
        Assert.NotNull(push2);

        // 同一单 0x53 再批：Ok=false 诚实应答；成员行不翻倍（不双入组）
        await owner.SendAsync(new JoinRequests(owner.NextSeq(), owner.Now(),
            MsgType.JoinRequests, JoinRequestAction.Approve, null, requestId));
        var stale = await owner.ReceiveSkippingPushesAsync<JoinRequestsAck>();
        Assert.False(stale!.Ok);
        // Web 再批：{ok:false}
        var again = await _http.PostAsync($"/api/group-requests/{requestId}/approve", null);
        var againBody = JsonSerializer.Deserialize<JsonElement>(await again.Content.ReadAsStreamAsync());
        Assert.Equal("已处理", againBody.GetProperty("message").GetString());
        await using var db3 = CreateDb();
        var g2 = await db3.Groups.AsNoTracking().SingleAsync(g => g.Name == "g05-审2");
        Assert.Equal(1, await db3.GroupMembers.CountAsync(m => m.DeviceId == joinerId
            && m.GroupId == g2.Id)); // 另一行是默认组（注册即入）

        // 拒绝路径：不入组、无 0x41
        var (joiner3, joiner3Id) = await RegisterLoggedInAsync("g05-j3", "carol");
        await joiner3.SendAsync(new GroupJoin(joiner3.NextSeq(), joiner3.Now(), MsgType.GroupJoin, code));
        _ = await joiner3.ReceiveSkippingPushesAsync<ErrorMessage>();
        var items2 = (await GetAsync("/api/group-requests")).GetProperty("data").GetProperty("items");
        var rid2 = items2.EnumerateArray().Single(r => r.GetProperty("deviceId").GetGuid() == joiner3Id)
            .GetProperty("requestId").GetGuid();
        Assert.Equal(HttpStatusCode.OK,
            (await _http.PostAsync($"/api/group-requests/{rid2}/reject", null)).StatusCode);
        await using var db4 = CreateDb();
        var g2b = await db4.Groups.AsNoTracking().SingleAsync(g => g.Name == "g05-审2");
        Assert.False(await db4.GroupMembers.AnyAsync(m => m.DeviceId == joiner3Id && m.GroupId == g2b.Id));
        Assert.Equal(1, await AuditCountAsync("group_join_reject"));
    }

    // ── 守卫与 404 ──────────────────────────────────────────────────────

    [Fact]
    public async Task 守卫与未知id()
    {
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _http.GetAsync("/api/group-requests?status=all")).StatusCode); // 仅支持 pending
        Assert.Equal(HttpStatusCode.NotFound,
            (await _http.PostAsync($"/api/group-requests/{Guid.NewGuid()}/approve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _http.PostAsync("/api/group-requests/not-a-guid/reject", null)).StatusCode);
    }

    /// <summary>按邀请码反查 groupId（测试侧小口径：申请单无需提前知道组 id）。</summary>
    private Guid GroupIdByCode(string code)
    {
        using var db = CreateDb();
        return db.Groups.AsNoTracking().Single(g => g.InviteCode == code).Id;
    }
}

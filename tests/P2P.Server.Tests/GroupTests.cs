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
/// 分组与设备列表测试（02 §2.4 0x50~0x57/0x40）。
/// M1-16：分页边界（空/恰满/越界 offset）、0x50/0x55/0x56 权限与生命周期、解散后可见性回收。
/// M2-09：凭码入组（free/approval 双策略）、审批队列、邀请码生成/撤销/唯一性、退组/移出可见性回收。
/// M2-27：0x42 已加入分组列表（isOwner/memberCount/policy，本地 /api/groups 数据源）。
/// </summary>
public sealed class GroupTests : IAsyncLifetime
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

    /// <summary>注册一台设备并以 admin 登录（owner=内置管理员），返回设备号。</summary>
    private async Task<(TestPcpClient Client, Guid DeviceId)> ConnectLoggedInAsync(string deviceName)
    {
        var client = await TestPcpClient.ConnectAsync(_server.LocalEndPoint!);
        _clients.Add(client);
        await client.HelloAsync(deviceId: null);
        var key = EcKeyPair.Generate();
        await client.SendAsync(new Register(client.NextSeq(), client.Now(), MsgType.Register,
            $"P2P-{Guid.NewGuid():N}"[..16], deviceName, "windows", "0.1.0",
            key.ExportPublicKey(), null, null, null), sign: false);
        var ack = await client.ReceiveAsync<RegisterAck>() ?? throw new IOException("注册无应答");
        client.EstablishWithSecret(Ecies.Decrypt(key, ack.DeviceSecretBox));

        await client.SendAsync(new UserLogin(client.NextSeq(), client.Now(), MsgType.UserLogin,
            DbInitializer.AdminUsername, DbInitializer.AdminUsername));
        var login = await client.ReceiveAsync<UserLoginAck>();
        Assert.True(login!.Ok);
        return (client, ack.DeviceId);
    }

    /// <summary>仅注册不登录。</summary>
    private async Task<(TestPcpClient Client, Guid DeviceId)> ConnectRegisteredAsync(string deviceName)
    {
        var client = await TestPcpClient.ConnectAsync(_server.LocalEndPoint!);
        _clients.Add(client);
        await client.HelloAsync(deviceId: null);
        var key = EcKeyPair.Generate();
        await client.SendAsync(new Register(client.NextSeq(), client.Now(), MsgType.Register,
            $"P2P-{Guid.NewGuid():N}"[..16], deviceName, "windows", "0.1.0",
            key.ExportPublicKey(), null, null, null), sign: false);
        var ack = await client.ReceiveAsync<RegisterAck>() ?? throw new IOException("注册无应答");
        client.EstablishWithSecret(Ecies.Decrypt(key, ack.DeviceSecretBox));
        return (client, ack.DeviceId);
    }

    private Task<DeviceListResponse> ListAsync(TestPcpClient client, uint offset, uint limit)
        => SendListAsync(client, offset, limit);

    private async Task<DeviceListResponse> SendListAsync(TestPcpClient client, uint offset, uint limit)
    {
        await client.SendAsync(new DeviceListRequest(client.NextSeq(), client.Now(), MsgType.DeviceList, offset, limit));
        return await client.ReceiveAsync<DeviceListResponse>() ?? throw new IOException("列表无应答");
    }

    // ── 0x40 分页边界（OQ-16）─────────────────────────────────────────

    [Fact]
    public async Task DeviceList_PaginationBoundaries()
    {
        // 5 台设备（含查询者）全部在默认组 → 可见总数 5
        var (viewer, _) = await ConnectLoggedInAsync("viewer");
        for (var i = 1; i <= 4; i++)
            await ConnectRegisteredAsync($"dev-{i:d2}");

        var full = await ListAsync(viewer, 0, 100);                 // 一次拉全
        Assert.Equal(5u, full.Total);
        Assert.False(full.HasMore);
        Assert.Equal(5, full.Items.Length);

        var partial = await ListAsync(viewer, 0, 3);                // 部分：hasMore=true
        Assert.Equal(5u, partial.Total);
        Assert.Equal(3, partial.Items.Length);
        Assert.True(partial.HasMore);

        var tail = await ListAsync(viewer, 3, 3);                   // 恰满剩余：3..5 共 2 条
        Assert.Equal(2, tail.Items.Length);
        Assert.False(tail.HasMore);

        var exact = await ListAsync(viewer, 0, 5);                  // 恰好等于总数
        Assert.Equal(5, exact.Items.Length);
        Assert.False(exact.HasMore);

        var beyond = await ListAsync(viewer, 5, 100);               // 越界 offset：空页但 Total 不变
        Assert.Equal(5u, beyond.Total);
        Assert.Empty(beyond.Items);
        Assert.False(beyond.HasMore);
    }

    [Fact]
    public async Task DeviceList_ItemFields_CarryGroupsAndPresence()
    {
        var (viewer, viewerId) = await ConnectLoggedInAsync("viewer");
        var (peer, peerId) = await ConnectRegisteredAsync("peer");

        var list = await ListAsync(viewer, 0, 100);
        Assert.Equal(2u, list.Total);

        var self = list.Items.Single(i => i.DeviceId == viewerId);
        Assert.True(self.Online);                                   // 查询者自身在线（registry）
        Assert.Contains(DbInitializer.DefaultGroupName, self.Groups);
        Assert.Equal(viewerId, self.DeviceId);

        var peerItem = list.Items.Single(i => i.DeviceId == peerId);
        Assert.True(peerItem.Online);
        _ = peer;
    }

    // ── 0x50/0x55/0x56 权限与生命周期 ─────────────────────────────────

    [Fact]
    public async Task GroupCreate_RequiresLogin()
    {
        var (anon, _) = await ConnectRegisteredAsync("anon");
        await anon.SendAsync(new GroupCreate(anon.NextSeq(), anon.Now(), MsgType.GroupCreate,
            "新组", JoinPolicy.Free));
        var error = await anon.ReceiveAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.Unauthorized, error!.Code);
    }

    [Fact]
    public async Task GroupLifecycle_CreateUpdateDissolve()
    {
        var (owner, ownerId) = await ConnectLoggedInAsync("owner");
        await owner.SendAsync(new GroupCreate(owner.NextSeq(), owner.Now(), MsgType.GroupCreate,
            "项目组", JoinPolicy.Free));
        var created = await owner.ReceiveAsync<GroupCreateAck>();
        Assert.NotEqual(Guid.Empty, created!.GroupId);

        // 编辑：改名 + 改策略
        await owner.SendAsync(new GroupUpdate(owner.NextSeq(), owner.Now(), MsgType.GroupUpdate,
            created.GroupId, "项目组v2", JoinPolicy.Approval));
        var updated = await owner.ReceiveAsync<GroupUpdateAck>();
        Assert.True(updated!.Ok);
        await using (var db = CreateDb())
        {
            var group = await db.Groups.AsNoTracking().SingleAsync(g => g.Id == created.GroupId);
            Assert.Equal("项目组v2", group.Name);
            Assert.Equal("approval", group.JoinPolicy);
        }

        // 解散默认分组 → 1003
        Guid defaultGroupId;
        await using (var db = CreateDb())
            defaultGroupId = await db.Groups.AsNoTracking().Where(g => g.IsDefault).Select(g => g.Id).SingleAsync();
        await owner.SendAsync(new GroupDissolve(owner.NextSeq(), owner.Now(), MsgType.GroupDissolve, defaultGroupId));
        var defaultErr = await owner.ReceiveAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.Conflict, defaultErr!.Code);

        // 解散自定义组 → Ok + 成员/申请联动清理（创建者成员由建组隐式写入，M2-09）
        await owner.SendAsync(new GroupDissolve(owner.NextSeq(), owner.Now(), MsgType.GroupDissolve, created.GroupId));
        var dissolved = await owner.ReceiveAsync<GroupDissolveAck>();
        Assert.True(dissolved!.Ok);
        await using var db2 = CreateDb();
        Assert.False(await db2.Groups.AnyAsync(g => g.Id == created.GroupId));
        Assert.False(await db2.GroupMembers.AnyAsync(m => m.GroupId == created.GroupId));
    }

    [Fact]
    public async Task GroupUpdate_ByNonOwner_Forbidden()
    {
        var (owner, _) = await ConnectLoggedInAsync("owner-a");
        await owner.SendAsync(new GroupCreate(owner.NextSeq(), owner.Now(), MsgType.GroupCreate, "A 的组", JoinPolicy.Free));
        var created = await owner.ReceiveAsync<GroupCreateAck>();

        // 第二个 admin 账号设备（同内置 admin——组 owner 即 admin，故改用独立用户构造非所有者）
        var (intruder, _) = await ConnectRegisteredAsync("intruder");
        await intruder.SendAsync(new UserRegister(intruder.NextSeq(), intruder.Now(), MsgType.UserRegister,
            "eve", "eve-password-1"));
        _ = await intruder.ReceiveAsync<UserRegisterAck>();
        await intruder.SendAsync(new UserLogin(intruder.NextSeq(), intruder.Now(), MsgType.UserLogin,
            "eve", "eve-password-1"));
        _ = await intruder.ReceiveAsync<UserLoginAck>();

        await intruder.SendAsync(new GroupUpdate(intruder.NextSeq(), intruder.Now(), MsgType.GroupUpdate,
            created!.GroupId, "劫持", null));
        var error = await intruder.ReceiveAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.Forbidden, error!.Code);
    }

    // ── M2-09 辅助：独立账号（跨账号共享入组，05 §1）──────────────────

    /// <summary>已注册设备注册独立用户并登录（默认 username 同密码）。</summary>
    private async Task LoginNewUserAsync(TestPcpClient client, string username)
    {
        await client.SendAsync(new UserRegister(client.NextSeq(), client.Now(), MsgType.UserRegister,
            username, $"{username}-password-1"));
        _ = await client.ReceiveAsync<UserRegisterAck>();
        await client.SendAsync(new UserLogin(client.NextSeq(), client.Now(), MsgType.UserLogin,
            username, $"{username}-password-1"));
        _ = await client.ReceiveAsync<UserLoginAck>();
    }

    /// <summary>所有者生成邀请码（0x54），返回码值。</summary>
    private static async Task<string> GenInviteAsync(TestPcpClient owner, Guid groupId)
    {
        await owner.SendAsync(new GroupInviteGen(owner.NextSeq(), owner.Now(), MsgType.GroupInviteGen,
            groupId, Revoke: false));
        var ack = await owner.ReceiveAsync<GroupInviteGenAck>();
        Assert.True(ack!.Ok);
        return ack.InviteCode!;
    }

    /// <summary>B 移出默认组，使指定组成为唯一可见性来源（M1-16 基线同款构造）。</summary>
    private async Task RemoveFromDefaultGroupAsync(Guid deviceId)
    {
        await using var db = CreateDb();
        var defaultGroupId = await db.Groups.AsNoTracking().Where(g => g.IsDefault).Select(g => g.Id).SingleAsync();
        var row = await db.GroupMembers.SingleAsync(m => m.GroupId == defaultGroupId && m.DeviceId == deviceId);
        db.GroupMembers.Remove(row);
        await db.SaveChangesAsync();
    }

    // ── M2-09 0x51/0x54 凭码入组（free/approval 双策略）────────────────

    [Fact]
    public async Task GroupJoin_FreePolicy_CrossAccount_JoinsAndVisible()
    {
        // 跨账号：组所有者=admin，入组设备属独立用户 eve（成员是设备维度，05 §1）
        var (owner, ownerId) = await ConnectLoggedInAsync("owner");
        var (joiner, joinerId) = await ConnectRegisteredAsync("joiner");
        await LoginNewUserAsync(joiner, "eve");

        await owner.SendAsync(new GroupCreate(owner.NextSeq(), owner.Now(), MsgType.GroupCreate,
            "共享组", JoinPolicy.Free));
        var group = await owner.ReceiveAsync<GroupCreateAck>();
        var code = await GenInviteAsync(owner, group!.GroupId);

        // 凭码入组：free 即入
        await joiner.SendAsync(new GroupJoin(joiner.NextSeq(), joiner.Now(), MsgType.GroupJoin, code));
        var joined = await joiner.ReceiveAsync<GroupJoinAck>();
        Assert.Equal(group.GroupId, joined!.GroupId);

        await using (var db = CreateDb())
        {
            var member = await db.GroupMembers.AsNoTracking()
                .SingleAsync(m => m.GroupId == group.GroupId && m.DeviceId == joinerId);
            Assert.True(member.Approved); // free 即入=已批准成员
            Assert.True(await db.WaitAuditAsync(a => a.Event == "group_join"
                && a.DeviceId == joinerId && a.UserId != null));
        }

        // 可见性互认（与 L2 同口径）
        var ownerList = await ListAsync(owner, 0, 100);
        Assert.Contains(ownerList.Items, i => i.DeviceId == joinerId);
        var joinerList = await ListAsync(joiner, 0, 100);
        Assert.Contains(joinerList.Items, i => i.DeviceId == ownerId);

        // 已是成员：幂等 Ack（重复凭码不报错不重复入组）
        await joiner.SendAsync(new GroupJoin(joiner.NextSeq(), joiner.Now(), MsgType.GroupJoin, code));
        var again = await joiner.ReceiveAsync<GroupJoinAck>();
        Assert.Equal(group.GroupId, again!.GroupId);
        await using var db2 = CreateDb();
        Assert.Equal(1, await db2.GroupMembers.CountAsync(m =>
            m.GroupId == group.GroupId && m.DeviceId == joinerId));
    }

    [Fact]
    public async Task GroupJoin_ApprovalPolicy_PendingListApprove()
    {
        var (owner, _) = await ConnectLoggedInAsync("owner");
        var (joiner, joinerId) = await ConnectRegisteredAsync("joiner");
        await LoginNewUserAsync(joiner, "eve");

        await owner.SendAsync(new GroupCreate(owner.NextSeq(), owner.Now(), MsgType.GroupCreate,
            "审批组", JoinPolicy.Approval));
        var group = await owner.ReceiveAsync<GroupCreateAck>();
        var code = await GenInviteAsync(owner, group!.GroupId);

        // approval：建 pending 申请单 → 3002 待审批
        await joiner.SendAsync(new GroupJoin(joiner.NextSeq(), joiner.Now(), MsgType.GroupJoin, code));
        var pending = await joiner.ReceiveAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.GroupNeedApproval, pending!.Code);

        // 重复申请去重：再入组仍 3002，申请单不重复
        await joiner.SendAsync(new GroupJoin(joiner.NextSeq(), joiner.Now(), MsgType.GroupJoin, code));
        _ = await joiner.ReceiveAsync<ErrorMessage>();
        Guid requestId;
        await using (var db = CreateDb())
            requestId = await db.JoinRequests.AsNoTracking()
                .Where(r => r.GroupId == group.GroupId && r.DeviceId == joinerId)
                .Select(r => r.Id).SingleAsync(); // 唯一 pending 行
        await using (var db = CreateDb())
            Assert.Equal(1, await db.JoinRequests.CountAsync(r =>
                r.GroupId == group.GroupId && r.DeviceId == joinerId && r.Status == "pending"));

        // 所有者 List：队列项含设备名与申请时间
        await owner.SendAsync(new JoinRequests(owner.NextSeq(), owner.Now(), MsgType.JoinRequests,
            JoinRequestAction.List, group.GroupId, null));
        var queue = await owner.ReceiveAsync<JoinRequestsResponse>();
        var item = Assert.Single(queue!.Items);
        Assert.Equal(requestId, item.RequestId);
        Assert.Equal(joinerId, item.DeviceId);
        Assert.Equal("joiner", item.DeviceName);
        Assert.True(item.CreatedAtMs > 0);

        // Approve：批准即入组
        await owner.SendAsync(new JoinRequests(owner.NextSeq(), owner.Now(), MsgType.JoinRequests,
            JoinRequestAction.Approve, null, requestId));
        var approved = await owner.ReceiveAsync<JoinRequestsAck>();
        Assert.True(approved!.Ok);
        await using var db3 = CreateDb();
        Assert.True(await db3.GroupMembers.AnyAsync(m =>
            m.GroupId == group.GroupId && m.DeviceId == joinerId && m.Approved));
        Assert.Equal("approved", await db3.JoinRequests.Where(r => r.Id == requestId)
            .Select(r => r.Status).SingleAsync());
        Assert.True(await db3.WaitAuditAsync(a => a.Event == "group_join_approve"));

        // 已处理的申请再 Approve：Ok=false（不重复入组）
        await owner.SendAsync(new JoinRequests(owner.NextSeq(), owner.Now(), MsgType.JoinRequests,
            JoinRequestAction.Approve, null, requestId));
        var stale = await owner.ReceiveAsync<JoinRequestsAck>();
        Assert.False(stale!.Ok);
    }

    [Fact]
    public async Task GroupJoin_ApprovalPolicy_Reject_NoMembership()
    {
        var (owner, _) = await ConnectLoggedInAsync("owner");
        var (joiner, joinerId) = await ConnectRegisteredAsync("joiner");
        await LoginNewUserAsync(joiner, "eve");

        await owner.SendAsync(new GroupCreate(owner.NextSeq(), owner.Now(), MsgType.GroupCreate,
            "审批组R", JoinPolicy.Approval));
        var group = await owner.ReceiveAsync<GroupCreateAck>();
        var code = await GenInviteAsync(owner, group!.GroupId);

        await joiner.SendAsync(new GroupJoin(joiner.NextSeq(), joiner.Now(), MsgType.GroupJoin, code));
        _ = await joiner.ReceiveAsync<ErrorMessage>();
        Guid requestId;
        await using (var db = CreateDb())
            requestId = (await db.JoinRequests.AsNoTracking().SingleAsync(r =>
                r.GroupId == group!.GroupId && r.DeviceId == joinerId)).Id;

        await owner.SendAsync(new JoinRequests(owner.NextSeq(), owner.Now(), MsgType.JoinRequests,
            JoinRequestAction.Reject, null, requestId));
        var rejected = await owner.ReceiveAsync<JoinRequestsAck>();
        Assert.True(rejected!.Ok);
        await using var db2 = CreateDb();
        Assert.False(await db2.GroupMembers.AnyAsync(m => m.GroupId == group.GroupId && m.DeviceId == joinerId));
        Assert.Equal("rejected", await db2.JoinRequests.Where(r => r.Id == requestId)
            .Select(r => r.Status).SingleAsync());
        Assert.True(await db2.WaitAuditAsync(a => a.Event == "group_join_reject"));
    }

    [Fact]
    public async Task GroupJoin_RequiresLogin()
    {
        var (owner, _) = await ConnectLoggedInAsync("owner");
        var (anon, _) = await ConnectRegisteredAsync("anon"); // 未登录
        await owner.SendAsync(new GroupCreate(owner.NextSeq(), owner.Now(), MsgType.GroupCreate,
            "码组", JoinPolicy.Free));
        var group = await owner.ReceiveAsync<GroupCreateAck>();
        var code = await GenInviteAsync(owner, group!.GroupId);

        await anon.SendAsync(new GroupJoin(anon.NextSeq(), anon.Now(), MsgType.GroupJoin, code));
        var error = await anon.ReceiveAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.Unauthorized, error!.Code);
    }

    // ── M2-09 0x54 邀请码撤销/无效 → 3001；唯一性 ─────────────────────

    [Fact]
    public async Task GroupJoin_RevokedOrInvalidCode_3001()
    {
        var (owner, _) = await ConnectLoggedInAsync("owner");
        var (joiner, _) = await ConnectRegisteredAsync("joiner");
        await LoginNewUserAsync(joiner, "eve");

        await owner.SendAsync(new GroupCreate(owner.NextSeq(), owner.Now(), MsgType.GroupCreate,
            "撤销组", JoinPolicy.Free));
        var group = await owner.ReceiveAsync<GroupCreateAck>();
        var code = await GenInviteAsync(owner, group!.GroupId);

        // 撤销：Ack Ok 且不携带码
        await owner.SendAsync(new GroupInviteGen(owner.NextSeq(), owner.Now(), MsgType.GroupInviteGen,
            group.GroupId, Revoke: true));
        var revoked = await owner.ReceiveAsync<GroupInviteGenAck>();
        Assert.True(revoked!.Ok);
        Assert.Null(revoked.InviteCode);

        // 撤销后凭原码 → 3001（码失效）
        await joiner.SendAsync(new GroupJoin(joiner.NextSeq(), joiner.Now(), MsgType.GroupJoin, code));
        var revokedErr = await joiner.ReceiveAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.GroupNotFound, revokedErr!.Code);

        // 乱码 → 3001
        await joiner.SendAsync(new GroupJoin(joiner.NextSeq(), joiner.Now(), MsgType.GroupJoin, "zzzzzz"));
        var invalid = await joiner.ReceiveAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.GroupNotFound, invalid!.Code);

        await using var db = CreateDb();
        Assert.True(await db.WaitAuditCountAsync(a => a.Event == "group_join_deny", 2) >= 2);
    }

    [Fact]
    public async Task InviteCode_UniqueAcrossGroups_OverwritePerGroup()
    {
        var (owner, _) = await ConnectLoggedInAsync("owner");
        await owner.SendAsync(new GroupCreate(owner.NextSeq(), owner.Now(), MsgType.GroupCreate,
            "组一", JoinPolicy.Free));
        var g1 = await owner.ReceiveAsync<GroupCreateAck>();
        await owner.SendAsync(new GroupCreate(owner.NextSeq(), owner.Now(), MsgType.GroupCreate,
            "组二", JoinPolicy.Free));
        var g2 = await owner.ReceiveAsync<GroupCreateAck>();

        var code1 = await GenInviteAsync(owner, g1!.GroupId);
        var code2 = await GenInviteAsync(owner, g2!.GroupId);
        Assert.NotEqual(code1, code2); // 全局唯一（UNIQUE 约束 + 生成器互异）

        foreach (var c in new[] { code1, code2 })
        {
            Assert.Equal(GroupService.InviteCodeLength, c.Length); // 6 位
            Assert.All(c, ch => Assert.True(GroupService.InviteCharset.Contains(ch))); // 去混淆字符集
        }

        // 同组再生成=覆盖式：旧码失效、新码可用
        var code1b = await GenInviteAsync(owner, g1.GroupId);
        Assert.NotEqual(code1, code1b);
        var (joiner, _) = await ConnectRegisteredAsync("joiner");
        await LoginNewUserAsync(joiner, "eve");
        await joiner.SendAsync(new GroupJoin(joiner.NextSeq(), joiner.Now(), MsgType.GroupJoin, code1));
        var staleErr = await joiner.ReceiveAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.GroupNotFound, staleErr!.Code);
        await joiner.SendAsync(new GroupJoin(joiner.NextSeq(), joiner.Now(), MsgType.GroupJoin, code1b));
        var joined = await joiner.ReceiveAsync<GroupJoinAck>();
        Assert.Equal(g1.GroupId, joined!.GroupId);
    }

    [Fact]
    public void InviteCode_Generator_FormatAndBulkDistinct()
    {
        // 纯单测：生成器格式（6 位/去混淆字符集）与批量互异
        var seen = new HashSet<string>();
        for (var i = 0; i < 200; i++)
        {
            var code = GroupService.GenerateInviteCode();
            Assert.Equal(GroupService.InviteCodeLength, code.Length);
            Assert.All(code, ch => Assert.True("23456789abcdefghjkmnpqrstuvwxyz".Contains(ch)));
            Assert.True(seen.Add(code), $"撞码：{code}");
        }
    }

    // ── M2-09 0x53 权限：仅所有者 ─────────────────────────────────────

    [Fact]
    public async Task JoinRequests_ByNonOwner_Forbidden()
    {
        var (owner, _) = await ConnectLoggedInAsync("owner-a");
        await owner.SendAsync(new GroupCreate(owner.NextSeq(), owner.Now(), MsgType.GroupCreate,
            "审批组N", JoinPolicy.Approval));
        var group = await owner.ReceiveAsync<GroupCreateAck>();

        var (intruder, _) = await ConnectRegisteredAsync("intruder");
        await LoginNewUserAsync(intruder, "mallory");

        await intruder.SendAsync(new JoinRequests(intruder.NextSeq(), intruder.Now(), MsgType.JoinRequests,
            JoinRequestAction.List, group!.GroupId, null));
        var error = await intruder.ReceiveAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.Forbidden, error!.Code);
    }

    // ── M2-09 0x52/0x57 退组/移出后可见性回收（完成判定）──────────────

    [Fact]
    public async Task GroupLeave_RevokesVisibility()
    {
        var (owner, ownerId) = await ConnectLoggedInAsync("owner");
        var (member, memberId) = await ConnectRegisteredAsync("member");
        await LoginNewUserAsync(member, "eve");
        await RemoveFromDefaultGroupAsync(memberId); // 指定组为唯一可见性来源

        await owner.SendAsync(new GroupCreate(owner.NextSeq(), owner.Now(), MsgType.GroupCreate,
            "退组测试", JoinPolicy.Free));
        var group = await owner.ReceiveAsync<GroupCreateAck>();
        var code = await GenInviteAsync(owner, group!.GroupId);
        await member.SendAsync(new GroupJoin(member.NextSeq(), member.Now(), MsgType.GroupJoin, code));
        _ = await member.ReceiveAsync<GroupJoinAck>();

        var before = await ListAsync(owner, 0, 100);
        Assert.Contains(before.Items, i => i.DeviceId == memberId);

        // 自退：Ok=true → 可见性回收
        await member.SendAsync(new GroupLeave(member.NextSeq(), member.Now(), MsgType.GroupLeave,
            group.GroupId));
        var left = await member.ReceiveAsync<GroupLeaveAck>();
        Assert.True(left!.Ok);
        var after = await ListAsync(owner, 0, 100);
        Assert.DoesNotContain(after.Items, i => i.DeviceId == memberId);
        Assert.Contains(after.Items, i => i.DeviceId == ownerId);
        await using var db = CreateDb();
        Assert.True(await db.WaitAuditAsync(a => a.Event == "group_leave" && a.DeviceId == memberId));

        // 再退（非成员）：Ok=false 诚实应答
        await member.SendAsync(new GroupLeave(member.NextSeq(), member.Now(), MsgType.GroupLeave,
            group.GroupId));
        var again = await member.ReceiveAsync<GroupLeaveAck>();
        Assert.False(again!.Ok);
    }

    [Fact]
    public async Task GroupRemoveMember_RevokesVisibility()
    {
        var (owner, ownerId) = await ConnectLoggedInAsync("owner");
        var (member, memberId) = await ConnectRegisteredAsync("member");
        await LoginNewUserAsync(member, "eve");
        await RemoveFromDefaultGroupAsync(memberId);

        await owner.SendAsync(new GroupCreate(owner.NextSeq(), owner.Now(), MsgType.GroupCreate,
            "移出测试", JoinPolicy.Free));
        var group = await owner.ReceiveAsync<GroupCreateAck>();
        var code = await GenInviteAsync(owner, group!.GroupId);
        await member.SendAsync(new GroupJoin(member.NextSeq(), member.Now(), MsgType.GroupJoin, code));
        _ = await member.ReceiveAsync<GroupJoinAck>();

        // 所有者移出：Ok=true → 可见性回收 + 审计
        await owner.SendAsync(new GroupRemoveMember(owner.NextSeq(), owner.Now(), MsgType.GroupRemoveMember,
            group.GroupId, memberId));
        var removed = await owner.ReceiveAsync<GroupRemoveMemberAck>();
        Assert.True(removed!.Ok);
        var after = await ListAsync(owner, 0, 100);
        Assert.DoesNotContain(after.Items, i => i.DeviceId == memberId);
        Assert.Contains(after.Items, i => i.DeviceId == ownerId);
        await using var db = CreateDb();
        Assert.True(await db.WaitAuditAsync(a => a.Event == "group_member_remove"
            && a.DeviceId == ownerId));

        // 再移（已非成员）：Ok=false
        await owner.SendAsync(new GroupRemoveMember(owner.NextSeq(), owner.Now(), MsgType.GroupRemoveMember,
            group.GroupId, memberId));
        var again = await owner.ReceiveAsync<GroupRemoveMemberAck>();
        Assert.False(again!.Ok);

        _ = member;
    }

    // ── 解散后可见性回收（完成判定）──────────────────────────────────

    [Fact]
    public async Task DissolveGroup_RevokesVisibility()
    {
        var (a, aId) = await ConnectRegisteredAsync("island-a");
        var (b, bId) = await ConnectRegisteredAsync("island-b");

        // B 移出默认组：仅存共同组 G 作为可见性来源（入组接口属 M2，直接写库构造）
        Guid defaultGroupId;
        await using (var db = CreateDb())
        {
            defaultGroupId = await db.Groups.AsNoTracking().Where(g => g.IsDefault).Select(g => g.Id).SingleAsync();
            var bDefault = await db.GroupMembers.SingleAsync(m => m.GroupId == defaultGroupId && m.DeviceId == bId);
            db.GroupMembers.Remove(bDefault);
            await db.SaveChangesAsync();
        }

        // A 登录建组 G（A 即首成员，M2-09），B 直接写库加入
        await a.SendAsync(new UserLogin(a.NextSeq(), a.Now(), MsgType.UserLogin,
            DbInitializer.AdminUsername, DbInitializer.AdminUsername));
        _ = await a.ReceiveAsync<UserLoginAck>();
        await a.SendAsync(new GroupCreate(a.NextSeq(), a.Now(), MsgType.GroupCreate, "临时组", JoinPolicy.Free));
        var created = await a.ReceiveAsync<GroupCreateAck>();
        await using (var db = CreateDb())
        {
            db.GroupMembers.Add(new GroupMember
            {
                Id = Guid.NewGuid(), GroupId = created!.GroupId, DeviceId = bId,
                Approved = true, JoinedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // G 存续：A 可见 B
        var before = await ListAsync(a, 0, 100);
        Assert.Contains(before.Items, i => i.DeviceId == bId);

        // 解散 G：可见性回收
        await a.SendAsync(new GroupDissolve(a.NextSeq(), a.Now(), MsgType.GroupDissolve, created.GroupId));
        var ack = await a.ReceiveAsync<GroupDissolveAck>();
        Assert.True(ack!.Ok);

        var after = await ListAsync(a, 0, 100);
        Assert.DoesNotContain(after.Items, i => i.DeviceId == bId);
        Assert.Contains(after.Items, i => i.DeviceId == aId); // 自身仍可见
    }

    // ── M2-27 0x42 已加入分组列表（本地 /api/groups 数据源）────────────

    private static async Task<GroupListResponse> GroupListAsync(TestPcpClient client)
    {
        await client.SendAsync(new GroupListRequest(client.NextSeq(), client.Now(), MsgType.GroupList));
        return await client.ReceiveAsync<GroupListResponse>() ?? throw new IOException("分组列表无应答");
    }

    [Fact]
    public async Task GroupList_MembershipOwnerFlagAndCounts()
    {
        // 跨账号拓扑：admin 持有自有组+开放组；eve 凭码加入开放组（成员是设备维度）
        var (owner, ownerId) = await ConnectLoggedInAsync("owner");
        var (joiner, joinerId) = await ConnectRegisteredAsync("joiner");
        await LoginNewUserAsync(joiner, "eve");

        await owner.SendAsync(new GroupCreate(owner.NextSeq(), owner.Now(), MsgType.GroupCreate,
            "自有组", JoinPolicy.Approval));
        var owned = await owner.ReceiveAsync<GroupCreateAck>();
        await owner.SendAsync(new GroupCreate(owner.NextSeq(), owner.Now(), MsgType.GroupCreate,
            "开放组", JoinPolicy.Free));
        var open = await owner.ReceiveAsync<GroupCreateAck>();
        var code = await GenInviteAsync(owner, open!.GroupId);
        await joiner.SendAsync(new GroupJoin(joiner.NextSeq(), joiner.Now(), MsgType.GroupJoin, code));
        Assert.Equal(open.GroupId, (await joiner.ReceiveAsync<GroupJoinAck>())!.GroupId);

        // owner 侧：两新组 IsOwner=true、成员数 2（创建即首成员+joiner）、approval 策略回传
        var ownerList = await GroupListAsync(owner);
        var ownItem = ownerList.Items.Single(i => i.GroupId == owned!.GroupId);
        Assert.True(ownItem.IsOwner);
        Assert.Equal(JoinPolicy.Approval, ownItem.Policy);
        Assert.Equal(1u, ownItem.MemberCount);
        var openItem = ownerList.Items.Single(i => i.GroupId == open.GroupId);
        Assert.True(openItem.IsOwner);
        Assert.Equal(2u, openItem.MemberCount);
        Assert.Contains(ownerList.Items, i => i.GroupName == DbInitializer.DefaultGroupName);

        // joiner 侧（独立账号 eve）：开放组 IsOwner=false；未加入的自有组不在列
        var joinerList = await GroupListAsync(joiner);
        var joined = joinerList.Items.Single(i => i.GroupId == open.GroupId);
        Assert.False(joined.IsOwner);
        Assert.Equal(JoinPolicy.Free, joined.Policy);
        Assert.Equal(2u, joined.MemberCount);
        Assert.DoesNotContain(joinerList.Items, i => i.GroupId == owned!.GroupId);

        _ = ownerId;
        _ = joinerId;
    }

    [Fact]
    public async Task GroupList_UnloggedStillListed_AndActiveClassGated()
    {
        // 未登录设备仍可列（0x40 同口径：成员资格是设备维度）
        var (anon, _) = await ConnectRegisteredAsync("anon");
        var list = await GroupListAsync(anon);
        Assert.Contains(list.Items, i => i.GroupName == DbInitializer.DefaultGroupName);

        // 主动类闸：0x42 与 0x40 同列（02 §2.5，passive 拒 2002）
        Assert.True(ControlMessageRouter.IsActiveClass(MsgType.GroupList));
    }
}

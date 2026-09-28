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
/// M2-10 0x41 DeviceListUpdate 推送（02 §2.4、FR-S-403、TD-16）。
/// 独立夹具（pusher 全程装配）：0x41 为异步提示帧，会扰乱 GroupTests 等严格接收断言，
/// 故触发链专项在此验证——在线/离线（registry 事件）、入组/批准/退组/移出/解散（GroupService 触发点）、
/// 无关方（不同账号且无共同分组）不收、哑节点（0x22 登出降级 Passive）不下发。
/// </summary>
public sealed class DeviceListPushTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DeviceRegistry _registry = new();
    private readonly List<TestPcpClient> _clients = [];
    private ControlServer _server = null!;
    private SignalingCoordinator _signaling = null!;
    private RelayService _relay = null!;

    public Task InitializeAsync()
    {
        _connection.Open();
        var factory = new StubFactory(CreateDb);
        using var init = factory.CreateDbContext();
        DbInitializer.Initialize(init);

        var audit = new AuditLogger(factory);
        _signaling = new SignalingCoordinator(factory, _registry, new Authorizer(factory), audit);
        _relay = new RelayService(factory, _registry, _signaling.ResolveRelayPeers);
        var pusher = new DeviceListPusher(factory, _registry); // 订阅 registry 在线事件（M2-10）
        var router = new ControlMessageRouter(
            new RegistrationService(factory, _registry, audit),
            new UserService(factory, audit),
            new GroupService(factory, _registry, audit, pusher),
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
        _connection.Dispose();
    }

    private AppDbContext CreateDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);

    private async Task<(TestPcpClient Client, Guid DeviceId)> ConnectLoggedInAsync(string deviceName)
    {
        var (client, deviceId) = await ConnectRegisteredAsync(deviceName);
        await client.SendAsync(new UserLogin(client.NextSeq(), client.Now(), MsgType.UserLogin,
            DbInitializer.AdminUsername, DbInitializer.AdminUsername));
        var login = await client.ReceiveSkippingPushesAsync<UserLoginAck>();
        Assert.True(login!.Ok);
        return (client, deviceId);
    }

    private async Task<(TestPcpClient Client, Guid DeviceId)> ConnectRegisteredAsync(string deviceName)
    {
        var client = await TestPcpClient.ConnectAsync(_server.LocalEndPoint!);
        _clients.Add(client);
        await client.HelloAsync(deviceId: null);
        var key = EcKeyPair.Generate();
        await client.SendAsync(new Register(client.NextSeq(), client.Now(), MsgType.Register,
            $"P2P-{Guid.NewGuid():N}"[..16], deviceName, "windows", "0.1.0",
            key.ExportPublicKey(), null, null, null), sign: false);
        var ack = await client.ReceiveSkippingPushesAsync<RegisterAck>() ?? throw new IOException("注册无应答");
        client.EstablishWithSecret(Ecies.Decrypt(key, ack.DeviceSecretBox));
        return (client, ack.DeviceId);
    }

    /// <summary>已注册设备注册独立用户并登录（跨账号无关方构造）。</summary>
    private async Task LoginNewUserAsync(TestPcpClient client, string username)
    {
        await client.SendAsync(new UserRegister(client.NextSeq(), client.Now(), MsgType.UserRegister,
            username, $"{username}-password-1"));
        _ = await client.ReceiveSkippingPushesAsync<UserRegisterAck>();
        await client.SendAsync(new UserLogin(client.NextSeq(), client.Now(), MsgType.UserLogin,
            username, $"{username}-password-1"));
        _ = await client.ReceiveSkippingPushesAsync<UserLoginAck>();
    }

    /// <summary>所有者建组（free）并生成邀请码，返回 (groupId, code)。</summary>
    private async Task<(Guid GroupId, string Code)> CreateFreeGroupWithCodeAsync(TestPcpClient owner, string name)
    {
        await owner.SendAsync(new GroupCreate(owner.NextSeq(), owner.Now(), MsgType.GroupCreate,
            name, JoinPolicy.Free));
        var group = await owner.ReceiveSkippingPushesAsync<GroupCreateAck>();
        Assert.NotEqual(Guid.Empty, group!.GroupId);
        await owner.SendAsync(new GroupInviteGen(owner.NextSeq(), owner.Now(), MsgType.GroupInviteGen,
            group.GroupId, Revoke: false));
        var gen = await owner.ReceiveSkippingPushesAsync<GroupInviteGenAck>();
        Assert.True(gen!.Ok);
        return (group.GroupId, gen.InviteCode!);
    }

    /// <summary>移出默认组，使指定关系成为唯一可见性来源（M1-16 基线同款构造）。</summary>
    private async Task RemoveFromDefaultGroupAsync(Guid deviceId)
    {
        await using var db = CreateDb();
        var defaultGroupId = await db.Groups.AsNoTracking().Where(g => g.IsDefault).Select(g => g.Id).SingleAsync();
        var row = await db.GroupMembers.SingleAsync(m => m.GroupId == defaultGroupId && m.DeviceId == deviceId);
        db.GroupMembers.Remove(row);
        await db.SaveChangesAsync();
    }

    /// <summary>排干在途 0x41（前置连接的上线推送）——读尽直至短窗超时；上限防异常风暴挂死。</summary>
    private async Task DrainPushesAsync(TestPcpClient client)
    {
        for (var i = 0; i < 50; i++)
        {
            try { _ = await client.ReceiveAsync<DeviceListUpdate>(400); }
            catch (TimeoutException) { return; }
        }
        Assert.Fail("0x41 排干超上限（异常推送风暴？）");
    }

    /// <summary>短窗内不应收到 0x41（无关方/哑节点断言；其余帧到达即解码失败显式暴露）。</summary>
    private static async Task AssertNoPushAsync(TestPcpClient client)
        => await Assert.ThrowsAsync<TimeoutException>(() => client.ReceiveAsync<DeviceListUpdate>(800));

    // ── 在线/离线（registry 事件订阅）─────────────────────────────────

    [Fact]
    public async Task Presence_OnlineOffline_PeerReceives041_OutsiderDoesNot()
    {
        var (peer, _) = await ConnectLoggedInAsync("push-peer");
        var (outsider, outsiderId) = await ConnectRegisteredAsync("push-out");
        await RemoveFromDefaultGroupAsync(outsiderId); // 不同账号且无共同分组 → 与世隔绝
        await DrainPushesAsync(peer);                  // 清 outsider 上线时默认组互见的在途推送

        // B 上线 → 能看见它的在线设备（默认组共同成员 peer）收 0x41；outsider 不收
        var (b, _) = await ConnectRegisteredAsync("push-b");
        _ = await peer.ReceiveAsync<DeviceListUpdate>();
        await AssertNoPushAsync(outsider);

        // B 离线（连接关闭）→ peer 再收一帧；outsider 仍不收
        await b.DisposeAsync();
        _ = await peer.ReceiveAsync<DeviceListUpdate>();
        await AssertNoPushAsync(outsider);
    }

    // ── 成员变更（GroupService 触发点）────────────────────────────────

    [Fact]
    public async Task GroupJoinAndLeave_Push041_ToMembersNotOutsider()
    {
        var (owner, _) = await ConnectLoggedInAsync("push-owner");
        var (joiner, joinerId) = await ConnectRegisteredAsync("push-joiner");
        await LoginNewUserAsync(joiner, "eve");
        await RemoveFromDefaultGroupAsync(joinerId);
        var (outsider, outsiderId) = await ConnectRegisteredAsync("push-out");
        await LoginNewUserAsync(outsider, "mallory");
        await RemoveFromDefaultGroupAsync(outsiderId);
        await DrainPushesAsync(owner);

        var (groupId, code) = await CreateFreeGroupWithCodeAsync(owner, "推送组");

        // 凭码入组（free 即入）→ 组员（owner+joiner，双方列表均变）各收 0x41；outsider 不收
        await joiner.SendAsync(new GroupJoin(joiner.NextSeq(), joiner.Now(), MsgType.GroupJoin, code));
        _ = await joiner.ReceiveSkippingPushesAsync<GroupJoinAck>();
        _ = await owner.ReceiveAsync<DeviceListUpdate>();
        _ = await joiner.ReceiveAsync<DeviceListUpdate>();
        await AssertNoPushAsync(outsider);

        // 自退 → 剩余成员（owner）收 0x41；outsider 仍不收
        await joiner.SendAsync(new GroupLeave(joiner.NextSeq(), joiner.Now(), MsgType.GroupLeave, groupId));
        var left = await joiner.ReceiveSkippingPushesAsync<GroupLeaveAck>();
        Assert.True(left!.Ok);
        _ = await owner.ReceiveAsync<DeviceListUpdate>();
        await AssertNoPushAsync(outsider);
    }

    [Fact]
    public async Task GroupApproval_Push041_OnlyOnApprove()
    {
        var (owner, _) = await ConnectLoggedInAsync("push-owner");
        var (joiner, joinerId) = await ConnectRegisteredAsync("push-joiner");
        await LoginNewUserAsync(joiner, "eve");
        await DrainPushesAsync(owner);

        await owner.SendAsync(new GroupCreate(owner.NextSeq(), owner.Now(), MsgType.GroupCreate,
            "审批推送组", JoinPolicy.Approval));
        var group = await owner.ReceiveSkippingPushesAsync<GroupCreateAck>();
        await owner.SendAsync(new GroupInviteGen(owner.NextSeq(), owner.Now(), MsgType.GroupInviteGen,
            group!.GroupId, Revoke: false));
        var gen = await owner.ReceiveSkippingPushesAsync<GroupInviteGenAck>();
        Assert.True(gen!.Ok);

        // approval：建 pending 申请单 → 3002（未入组，无成员变更 → 不推送）
        await joiner.SendAsync(new GroupJoin(joiner.NextSeq(), joiner.Now(), MsgType.GroupJoin, gen.InviteCode!));
        var pending = await joiner.ReceiveSkippingPushesAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.GroupNeedApproval, pending!.Code);
        await AssertNoPushAsync(owner);

        Guid requestId;
        await using (var db = CreateDb())
            requestId = await db.JoinRequests.AsNoTracking()
                .Where(r => r.GroupId == group.GroupId && r.DeviceId == joinerId)
                .Select(r => r.Id).SingleAsync();

        // 批准即入组 → 组员收 0x41
        await owner.SendAsync(new JoinRequests(owner.NextSeq(), owner.Now(), MsgType.JoinRequests,
            JoinRequestAction.Approve, null, requestId));
        var approved = await owner.ReceiveSkippingPushesAsync<JoinRequestsAck>();
        Assert.True(approved!.Ok);
        _ = await owner.ReceiveAsync<DeviceListUpdate>();
    }

    [Fact]
    public async Task GroupRemoveAndDissolve_Push041()
    {
        var (owner, _) = await ConnectLoggedInAsync("push-owner");
        var (member, memberId) = await ConnectRegisteredAsync("push-member");
        await LoginNewUserAsync(member, "eve");
        await DrainPushesAsync(owner);

        // 0x57 移出：剩余成员（owner）收 0x41
        var (g1, code1) = await CreateFreeGroupWithCodeAsync(owner, "移除组");
        await member.SendAsync(new GroupJoin(member.NextSeq(), member.Now(), MsgType.GroupJoin, code1));
        _ = await member.ReceiveSkippingPushesAsync<GroupJoinAck>();
        await DrainPushesAsync(owner);
        await DrainPushesAsync(member);
        await owner.SendAsync(new GroupRemoveMember(owner.NextSeq(), owner.Now(), MsgType.GroupRemoveMember,
            g1, memberId));
        var removed = await owner.ReceiveSkippingPushesAsync<GroupRemoveMemberAck>();
        Assert.True(removed!.Ok);
        _ = await owner.ReceiveAsync<DeviceListUpdate>();

        // 0x56 解散：成员清单先捕获 → 双方收 0x41
        var (g2, code2) = await CreateFreeGroupWithCodeAsync(owner, "解散组");
        await member.SendAsync(new GroupJoin(member.NextSeq(), member.Now(), MsgType.GroupJoin, code2));
        _ = await member.ReceiveSkippingPushesAsync<GroupJoinAck>();
        await DrainPushesAsync(owner);
        await DrainPushesAsync(member);
        await owner.SendAsync(new GroupDissolve(owner.NextSeq(), owner.Now(), MsgType.GroupDissolve, g2));
        var dissolved = await owner.ReceiveSkippingPushesAsync<GroupDissolveAck>();
        Assert.True(dissolved!.Ok);
        _ = await owner.ReceiveAsync<DeviceListUpdate>();
        _ = await member.ReceiveAsync<DeviceListUpdate>();
    }

    // ── 哑节点不下发（02 §2.4）────────────────────────────────────────

    [Fact]
    public async Task PassiveNode_LogoutDowngrade_NotPushed()
    {
        var (active, _) = await ConnectLoggedInAsync("push-active");
        var (passive, _) = await ConnectLoggedInAsync("push-passive"); // 同 admin 账号 = 互为 peers
        await passive.SendAsync(new UserLogout(passive.NextSeq(), passive.Now(), MsgType.UserLogout));
        var logout = await passive.ReceiveSkippingPushesAsync<UserLogoutAck>();
        Assert.True(logout!.Ok); // 0x22 成功 → 会话降级 Passive（连接保持在线）
        await DrainPushesAsync(active);

        // B 上线：默认组共同成员=active+passive，哑节点跳过 → 仅 active 收 0x41
        var (b, _) = await ConnectRegisteredAsync("push-b");
        _ = await active.ReceiveAsync<DeviceListUpdate>();
        await AssertNoPushAsync(passive);
    }

    // ── 收件人解析（internal static 纯查询单测）───────────────────────

    [Fact]
    public async Task ResolveAccountPeers_SameAccountUnionCommonGroups_ExcludesSelf()
    {
        // d1(u1)：同账号 d2 + 共同组 g1{d3}、g2{d2,d5} → {d2,d3,d5}；d4(u2 无共同组) 不可见
        // d5(未绑定)：仅共同组 g2{d1,d2} → {d1,d2}
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var (d1, d2, d3, d4, d5) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var g1 = Guid.NewGuid();
        var g2 = Guid.NewGuid();
        await using (var db = CreateDb())
        {
            foreach (var (id, admin) in new[] { (u1, true), (u2, false) })
                db.Users.Add(new User
                {
                    Id = id, Username = $"u-{id:N}"[..12], PasswordHash = "x",
                    IsAdmin = admin, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                });
            foreach (var (id, owner) in new[] { (d1, u1), (d2, u1), (d3, u2), (d4, u2), (d5, (Guid?)null) })
                db.Devices.Add(new Device
                {
                    Id = id, OwnerUserId = owner, DeviceName = $"dev-{id:N}"[..12],
                    Os = "windows", ClientVersion = "0.1.0", MacCode = $"MC-{id:N}"[..12],
                    RemoteCode = $"RC-{id:N}"[..12], VirtualIp = "100.64.0.2",
                    StaticPubKey = [], DeviceSecret = [], CreatedAt = DateTime.UtcNow,
                });
            db.Groups.Add(new Group { Id = g1, Name = "g1", OwnerUserId = u1, CreatedAt = DateTime.UtcNow });
            db.Groups.Add(new Group { Id = g2, Name = "g2", OwnerUserId = u1, CreatedAt = DateTime.UtcNow });
            foreach (var (g, d) in new[] { (g1, d1), (g1, d3), (g2, d1), (g2, d2), (g2, d5) })
                db.GroupMembers.Add(new GroupMember
                    { Id = Guid.NewGuid(), GroupId = g, DeviceId = d, Approved = true, JoinedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        await using (var db = CreateDb())
        {
            var peersOfD1 = await DeviceListPusher.ResolveAccountPeersAsync(db, d1);
            Assert.Equal(new[] { d2, d3, d5 }.Order().ToArray(), peersOfD1.Order().ToArray());
            var peersOfD5 = await DeviceListPusher.ResolveAccountPeersAsync(db, d5);
            Assert.Equal(new[] { d1, d2 }.Order().ToArray(), peersOfD5.Order().ToArray());
        }
    }
}

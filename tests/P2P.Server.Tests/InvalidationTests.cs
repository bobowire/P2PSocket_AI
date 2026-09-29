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
/// M2-12 0x14 远程码重置与 0x75 Invalidation 推送全集（02 §2.4、PRD 05 §4/§5、FR-S-903、FR-C-702）。
/// 覆盖：登出→本人映射 075(logged_out)；0x14→新码/旧码 4003/引用方 075(remote_code_reset)/
/// 相关方 0x41/passive 允许；退组/移出/解散→切断边映射 075（**残余可见性复核**：
/// 另共同组仍在不失效、同账号对保留）。推送帧序=同连接 Ack→0x41→0x75（处理器顺序确定）。
/// </summary>
public sealed class InvalidationTests : IAsyncLifetime
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
        var invalidation = new InvalidationPusher(factory, _registry); // M2-12 0x75
        var pusher = new DeviceListPusher(factory, _registry);         // M2-10 0x41（帧序交错源）
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
        await DrainPushesAsync(client); // 沉降：注册收尾（默认组入组/audit/0x41）落定后再还出客户端
        return (client, ack.DeviceId);
    }

    /// <summary>已注册设备注册独立用户并登录（跨账号：与 admin 无共同可见性来源时构造切断场景）。</summary>
    private async Task LoginNewUserAsync(TestPcpClient client, string username)
    {
        await client.SendAsync(new UserRegister(client.NextSeq(), client.Now(), MsgType.UserRegister,
            username, $"{username}-password-1"));
        _ = await client.ReceiveSkippingPushesAsync<UserRegisterAck>();
        await client.SendAsync(new UserLogin(client.NextSeq(), client.Now(), MsgType.UserLogin,
            username, $"{username}-password-1"));
        _ = await client.ReceiveSkippingPushesAsync<UserLoginAck>();
        await DrainPushesAsync(client); // 沉降：登录收尾（audit/0x41）落定后再发起后续跨客户端请求
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

    private async Task<Guid> CreateMappingAsync(TestPcpClient client, string remoteCode, ushort localPort)
    {
        await client.SendAsync(new MappingUpsert(client.NextSeq(), client.Now(), MsgType.MappingUpsert,
            null, "m-inv", localPort, "tcp", remoteCode, "self", 80, true));
        var ack = await client.ReceiveSkippingPushesAsync<MappingUpsertAck>() ?? throw new IOException("0x60 无应答");
        return ack.MappingId;
    }

    private async Task<string> GetRemoteCodeAsync(Guid deviceId)
    {
        await using var db = CreateDb();
        return await db.Devices.AsNoTracking().Where(d => d.Id == deviceId)
            .Select(d => d.RemoteCode).SingleAsync();
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

    /// <summary>排干在途 0x41（连接期上线/成员变更推送）；兼作沉降点——等待服务端 Ack 后收尾
    /// （audit/0x41 推送）落定，避免下一条跨客户端请求与其并发碰共享测试连接（夹具竞态）。</summary>
    private async Task DrainPushesAsync(TestPcpClient client)
    {
        for (var i = 0; i < 50; i++)
        {
            var frame = await client.ReceiveDescribeAsync(400);
            if (frame is null) return; // 静默期：排干完成
            if (frame.StartsWith("0x41 ")) continue;
            Assert.Fail($"排干撞非 0x41 帧：{frame}；余帧: {string.Join(" | ", await client.DumpFramesAsync(1500))}");
        }
        Assert.Fail("0x41 排干超上限（异常推送风暴？）");
    }

    /// <summary>短窗内不应收到 0x75（残余可见性保留断言）。</summary>
    private static async Task AssertNoInvalidationAsync(TestPcpClient client)
        => await Assert.ThrowsAsync<TimeoutException>(() => client.ReceiveAsync<Invalidation>(800));

    // ── logged_out ← 0x22 ──────────────────────────────────────────────

    [Fact]
    public async Task 登出_本人enabled映射全部075()
    {
        var (a, _) = await ConnectLoggedInAsync("inv-a");
        var (b, bId) = await ConnectRegisteredAsync("inv-b");
        await DrainPushesAsync(a);
        var remoteCode = await GetRemoteCodeAsync(bId);

        var m1 = await CreateMappingAsync(a, remoteCode, 30001);
        var m2 = await CreateMappingAsync(a, remoteCode, 30002);

        await a.SendAsync(new UserLogout(a.NextSeq(), a.Now(), MsgType.UserLogout));
        _ = await a.ReceiveSkippingPushesAsync<UserLogoutAck>();
        var push = await a.ReceiveAsync<Invalidation>(); // 登出不触发 0x41，严格读即失效帧
        Assert.Equal(InvalidationReason.LoggedOut, push!.Reason);
        Assert.Equal(new[] { m1, m2 }.Order().ToArray(), push.AffectedMappingIds.Order().ToArray());
        Assert.Null(push.NewCapability);
    }

    // ── remote_code_reset ← 0x14 ───────────────────────────────────────

    [Fact]
    public async Task 远程码重置_引用方075_旧码4003_新码可用_相关方041()
    {
        var (a, _) = await ConnectLoggedInAsync("inv-a");
        var (b, bId) = await ConnectRegisteredAsync("inv-b");
        await DrainPushesAsync(a);
        var oldCode = await GetRemoteCodeAsync(bId);
        var m1 = await CreateMappingAsync(a, oldCode, 30010);

        await b.SendAsync(new RemoteCodeReset(b.NextSeq(), b.Now(), MsgType.RemoteCodeReset));
        var ack = await b.ReceiveSkippingPushesAsync<RemoteCodeResetAck>();
        Assert.True(ack!.Ok);
        Assert.NotNull(ack.NewRemoteCode);
        Assert.NotEqual(oldCode, ack.NewRemoteCode);

        // a（引用方）：0x75 先于 0x41（处理器顺序），两者都到
        var push = await a.ReceiveAsync<Invalidation>();
        Assert.Equal(InvalidationReason.RemoteCodeReset, push!.Reason);
        Assert.Equal(new[] { m1 }, push.AffectedMappingIds);
        _ = await a.ReceiveAsync<DeviceListUpdate>();

        // 旧码 → 4003（唯一索引换值即不可解析）；新码 → 正常建映射
        await a.SendAsync(new MappingUpsert(a.NextSeq(), a.Now(), MsgType.MappingUpsert,
            null, "m-old", 30011, "tcp", oldCode, "self", 80, true));
        var err = await a.ReceiveSkippingPushesAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.RemoteCodeInvalid, err!.Code);
        _ = await CreateMappingAsync(a, ack.NewRemoteCode!, 30012);

        await using var db = CreateDb();
        Assert.Equal(ack.NewRemoteCode, await db.Devices.Where(d => d.Id == bId).Select(d => d.RemoteCode).SingleAsync());
        var row = await db.AuditLogs.AsNoTracking().SingleAsync(x => x.Event == "remote_code_reset");
        Assert.True(row.Detail is null || !row.Detail.Contains(ack.NewRemoteCode!)); // AI-17：码值不入审计
    }

    [Fact]
    public async Task 登出降级passive后_014仍允许()
    {
        var (b, _) = await ConnectLoggedInAsync("inv-b");
        await b.SendAsync(new UserLogout(b.NextSeq(), b.Now(), MsgType.UserLogout));
        _ = await b.ReceiveSkippingPushesAsync<UserLogoutAck>(); // 无映射 → 无 0x75

        await b.SendAsync(new RemoteCodeReset(b.NextSeq(), b.Now(), MsgType.RemoteCodeReset));
        var ack = await b.ReceiveSkippingPushesAsync<RemoteCodeResetAck>();
        Assert.True(ack!.Ok); // 本机管理类：passive 不拦（02 §2.5）
    }

    // ── group_left ← 0x52 / group_dissolved ← 0x56/0x57 ───────────────

    [Fact]
    public async Task 退组_另共同组仍在_映射不失效()
    {
        var (a, _) = await ConnectLoggedInAsync("inv-a");
        var (b, bId) = await ConnectRegisteredAsync("inv-b");
        await LoginNewUserAsync(b, "inv-c-user"); // 跨账号：保留断言只锚定默认组（同账号保留另测）
        var (groupId, code) = await CreateFreeGroupWithCodeAsync(a, "共存组");
        await b.SendAsync(new GroupJoin(b.NextSeq(), b.Now(), MsgType.GroupJoin, code));
        _ = await b.ReceiveSkippingPushesAsync<GroupJoinAck>();
        await DrainPushesAsync(a); // 入组 0x41

        // a↔b 双共同组（默认 + 共存组）：m 引用 b
        var remoteCode = await GetRemoteCodeAsync(bId);
        var m = await CreateMappingAsync(a, remoteCode, 30020);

        // b 退共存组 → 默认组仍在 → 可见性保留 → 无 0x75
        await b.SendAsync(new GroupLeave(b.NextSeq(), b.Now(), MsgType.GroupLeave, groupId));
        var left = await b.ReceiveSkippingPushesAsync<GroupLeaveAck>();
        Assert.True(left!.Ok);
        _ = await a.ReceiveAsync<DeviceListUpdate>(); // 0x41 照常
        await AssertNoInvalidationAsync(a);
    }

    [Fact]
    public async Task 退组_唯一可见性来源切断_双方各自075()
    {
        var (a, aId) = await ConnectLoggedInAsync("inv-a");
        var (b, bId) = await ConnectRegisteredAsync("inv-b");
        await LoginNewUserAsync(b, "inv-b-user");
        await RemoveFromDefaultGroupAsync(bId); // b 与 a 的唯一可见性来源=新建组
        await DrainPushesAsync(a); // 沉降：b 登录收尾后再由 a 发起建组
        var (groupId, code) = await CreateFreeGroupWithCodeAsync(a, "切断组");
        await b.SendAsync(new GroupJoin(b.NextSeq(), b.Now(), MsgType.GroupJoin, code));
        _ = await b.ReceiveSkippingPushesAsync<GroupJoinAck>();
        await DrainPushesAsync(a);

        var rcB = await GetRemoteCodeAsync(bId);
        var rcA = await GetRemoteCodeAsync(aId);
        var mAb = await CreateMappingAsync(a, rcB, 30030);
        var mBa = await CreateMappingAsync(b, rcA, 30031);

        await b.SendAsync(new GroupLeave(b.NextSeq(), b.Now(), MsgType.GroupLeave, groupId));
        Assert.True((await b.ReceiveSkippingPushesAsync<GroupLeaveAck>())!.Ok);

        // a：0x41（组成员即 a 自己的列表变更）→ 0x75([mAb])
        _ = await a.ReceiveAsync<DeviceListUpdate>();
        var pushA = await a.ReceiveAsync<Invalidation>();
        Assert.Equal(InvalidationReason.GroupLeft, pushA!.Reason);
        Assert.Equal(new[] { mAb }, pushA.AffectedMappingIds);
        // b：Ack → 0x75([mBa])（自身是离开者，非组成员收件人）
        var pushB = await b.ReceiveAsync<Invalidation>();
        Assert.Equal(InvalidationReason.GroupLeft, pushB!.Reason);
        Assert.Equal(new[] { mBa }, pushB.AffectedMappingIds);
    }

    [Fact]
    public async Task 移出成员_075_group_dissolved口径()
    {
        var (a, _) = await ConnectLoggedInAsync("inv-a");
        var (b, bId) = await ConnectRegisteredAsync("inv-b");
        await LoginNewUserAsync(b, "inv-r-user");
        await RemoveFromDefaultGroupAsync(bId);
        await DrainPushesAsync(a); // 沉降：b 登录收尾后再由 a 发起建组
        var (groupId, code) = await CreateFreeGroupWithCodeAsync(a, "移出组");
        await b.SendAsync(new GroupJoin(b.NextSeq(), b.Now(), MsgType.GroupJoin, code));
        _ = await b.ReceiveSkippingPushesAsync<GroupJoinAck>();
        await DrainPushesAsync(a);

        var mAb = await CreateMappingAsync(a, await GetRemoteCodeAsync(bId), 30040);

        await a.SendAsync(new GroupRemoveMember(a.NextSeq(), a.Now(), MsgType.GroupRemoveMember,
            groupId, bId));
        Assert.True((await a.ReceiveSkippingPushesAsync<GroupRemoveMemberAck>())!.Ok);

        _ = await a.ReceiveAsync<DeviceListUpdate>();
        var pushA = await a.ReceiveAsync<Invalidation>();
        Assert.Equal(InvalidationReason.GroupDissolved, pushA!.Reason); // 0x57 枚举口径（组关系终止）
        Assert.Equal(new[] { mAb }, pushA.AffectedMappingIds);
    }

    [Fact]
    public async Task 解散_组内切断_同账号对保留()
    {
        var (a1, _) = await ConnectLoggedInAsync("inv-a1");
        var (a2, a2Id) = await ConnectLoggedInAsync("inv-a2"); // 同 admin 账号：账号可见性对照
        var (b, bId) = await ConnectRegisteredAsync("inv-b");
        await LoginNewUserAsync(b, "inv-d-user");
        await RemoveFromDefaultGroupAsync(bId);
        await DrainPushesAsync(a1); // 沉降：b 登录收尾后再由 a1 发起建组
        var (groupId, code) = await CreateFreeGroupWithCodeAsync(a1, "解散组");
        foreach (var (member, name) in new[] { (a2, "a2"), (b, "b") })
        {
            await member.SendAsync(new GroupJoin(member.NextSeq(), member.Now(), MsgType.GroupJoin, code));
            for (var i = 0; ; i++)
            {
                var frame = await member.ReceiveDescribeAsync(500);
                if (frame is null) Assert.Fail($"{name} join 无应答");
                if (frame.StartsWith("0x41 ")) continue;
                if (frame.StartsWith("0x51 ")) break;
                Assert.Fail($"{name} join 期撞帧：{frame}；余帧: {string.Join(" | ", await member.DumpFramesAsync(1500))}");
            }
            await DrainPushesAsync(member); // 沉降：Ack 先于 audit/推送落定，紧接下一条 join 会与在途收尾并发碰共享连接
        }
        await DrainPushesAsync(a1);
        await DrainPushesAsync(a2);

        var m1b = await CreateMappingAsync(a1, await GetRemoteCodeAsync(bId), 30050);
        var m2b = await CreateMappingAsync(a2, await GetRemoteCodeAsync(bId), 30051);
        var m12 = await CreateMappingAsync(a1, await GetRemoteCodeAsync(a2Id), 30052); // 同账号对

        await a1.SendAsync(new GroupDissolve(a1.NextSeq(), a1.Now(), MsgType.GroupDissolve, groupId));
        Assert.True((await a1.ReceiveSkippingPushesAsync<GroupDissolveAck>())!.Ok);

        // a1：0x41 → 0x75 仅 [m1b]（m12 同账号保留）
        _ = await a1.ReceiveAsync<DeviceListUpdate>();
        var push1 = await a1.ReceiveAsync<Invalidation>();
        Assert.Equal(InvalidationReason.GroupDissolved, push1!.Reason);
        Assert.Equal(new[] { m1b }, push1.AffectedMappingIds);

        // a2：0x41 → 0x75 [m2b]
        _ = await a2.ReceiveAsync<DeviceListUpdate>();
        var push2 = await a2.ReceiveAsync<Invalidation>();
        Assert.Equal(InvalidationReason.GroupDissolved, push2!.Reason);
        Assert.Equal(new[] { m2b }, push2.AffectedMappingIds);

        await using var db = CreateDb();
        Assert.False(await db.Groups.AnyAsync(g => g.Id == groupId));
    }
}

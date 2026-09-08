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
/// 分组与设备列表测试（02 §2.4 0x50/0x55/0x56/0x40；完成判定：分页边界（空/恰满/越界 offset）、解散后可见性回收）。
/// 入组 0x51 属 M2：非默认组成员关系以直接写库构造。
/// </summary>
public sealed class GroupTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DeviceRegistry _registry = new();
    private readonly List<TestPcpClient> _clients = [];
    private ControlServer _server = null!;

    public Task InitializeAsync()
    {
        _connection.Open();
        var factory = new StubFactory(CreateDb);
        using var init = factory.CreateDbContext();
        DbInitializer.Initialize(init);

        var audit = new AuditLogger(factory);
        var router = new ControlMessageRouter(
            new RegistrationService(factory, _registry, audit),
            new UserService(factory, audit),
            new GroupService(factory, _registry),
            audit);
        _server = new ControlServer(factory, _registry, router.DispatchAsync);
        return _server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
    }

    public async Task DisposeAsync()
    {
        foreach (var c in _clients) await c.DisposeAsync();
        await _server.DisposeAsync();
        _connection.Dispose();
    }

    private AppDbContext CreateDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);

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

        // 解散自定义组 → Ok + 成员/申请联动清理
        await using (var db = CreateDb())
        {
            db.GroupMembers.Add(new GroupMember
            {
                Id = Guid.NewGuid(), GroupId = created.GroupId, DeviceId = ownerId,
                Approved = true, JoinedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
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

        // A 登录建组 G，A、B 加入
        await a.SendAsync(new UserLogin(a.NextSeq(), a.Now(), MsgType.UserLogin,
            DbInitializer.AdminUsername, DbInitializer.AdminUsername));
        _ = await a.ReceiveAsync<UserLoginAck>();
        await a.SendAsync(new GroupCreate(a.NextSeq(), a.Now(), MsgType.GroupCreate, "临时组", JoinPolicy.Free));
        var created = await a.ReceiveAsync<GroupCreateAck>();
        await using (var db = CreateDb())
        {
            db.GroupMembers.AddRange(
                new GroupMember { Id = Guid.NewGuid(), GroupId = created!.GroupId, DeviceId = aId, Approved = true, JoinedAt = DateTime.UtcNow },
                new GroupMember { Id = Guid.NewGuid(), GroupId = created.GroupId, DeviceId = bId, Approved = true, JoinedAt = DateTime.UtcNow });
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
}

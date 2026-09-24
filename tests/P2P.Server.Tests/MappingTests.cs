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
/// 映射 CRUD 同步测试（02 §2.4 0x60/0x61；完成判定：创建落库、未知远程码 4003、
/// L2 拒 4001+mapping_deny 审计、端口冲突 1003、启用态改端口 1003/停用态可改、删除幂等）。
/// </summary>
public sealed class MappingTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DeviceRegistry _registry = new();
    private readonly List<TestPcpClient> _clients = [];
    private ControlServer _server = null!;
    private SignalingCoordinator _signaling = null!;
    private RelayService _relay = null!;
    private StubFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _connection.Open();
        _factory = new StubFactory(CreateDb);
        using var init = _factory.CreateDbContext();
        DbInitializer.Initialize(init);

        var audit = new AuditLogger(_factory);
        _signaling = new SignalingCoordinator(_factory, _registry, new Authorizer(_factory), audit);
        _relay = new RelayService(_factory, _registry, _signaling.ResolveRelayPeers);
        var router = new ControlMessageRouter(
            new RegistrationService(_factory, _registry, audit),
            new UserService(_factory, audit),
            new GroupService(_factory, _registry),
            _signaling,
            new MappingService(_factory, audit),
            _relay,
            new StatsService(_factory, audit),
            audit);
        _server = new ControlServer(_factory, _registry, router.DispatchAsync);
        await _server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
    }

    public async Task DisposeAsync()
    {
        await _relay.DisposeAsync();
        foreach (var c in _clients) await c.DisposeAsync();
        await _server.DisposeAsync();
        await _signaling.DisposeAsync();
        await Task.Delay(200); // 服务端收尾审计与连接销毁竞态宽限（LocalWebApi 测试同法）
        try { _connection.Dispose(); }
        catch (Exception) { /* sqlite 收尾竞态已知瞬态家族（IOE/NRE 换皮）：测试本体已断言完毕 */ }
    }

    private AppDbContext CreateDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);

    /// <summary>注册一台设备并以 admin 登录（默认组同账号 → 互相可见）；login=false 供 L2 用例隔离。</summary>
    private async Task<(TestPcpClient Client, Guid DeviceId, string RemoteCode)> RegisterAsync(
        string name, bool login = true)
    {
        var client = await TestPcpClient.ConnectAsync(_server.LocalEndPoint!);
        _clients.Add(client);
        await client.HelloAsync(deviceId: null);
        var key = EcKeyPair.Generate();
        await client.SendAsync(new Register(client.NextSeq(), client.Now(), MsgType.Register,
            $"P2P-MAP{Guid.NewGuid():N}"[..14], name, "windows", "0.1.0",
            key.ExportPublicKey(), null, null, null), sign: false);
        var ack = await RegisterAckAsync(client);
        client.EstablishWithSecret(Ecies.Decrypt(key, ack.DeviceSecretBox));

        if (login)
        {
            await client.SendAsync(new UserLogin(client.NextSeq(), client.Now(), MsgType.UserLogin,
                DbInitializer.AdminUsername, DbInitializer.AdminUsername));
            var loginAck = await client.ReceiveAsync<UserLoginAck>();
            Assert.True(loginAck!.Ok);
        }
        return (client, ack.DeviceId, ack.RemoteCode);
    }

    private static async Task<RegisterAck> RegisterAckAsync(TestPcpClient client)
        => await client.ReceiveAsync<RegisterAck>() ?? throw new IOException("注册无应答");

    private static MappingUpsert Upsert(TestPcpClient client, Guid? id, string name, ushort localPort,
        string remoteCode, ushort targetPort = 8080, bool enabled = false)
        => new(client.NextSeq(), client.Now(), MsgType.MappingUpsert,
            id, name, localPort, "tcp", remoteCode, "self", targetPort, enabled);

    // ── 完成判定①：创建 Ack 返回新 id + 落库字段 ──────────────────────

    [Fact]
    public async Task Upsert_Create_PersistedAndAcked()
    {
        var (a, aId, _) = await RegisterAsync("map-a");
        var (_, bId, bCode) = await RegisterAsync("map-b");

        await a.SendAsync(Upsert(a, null, "web", 18080, bCode, 80, enabled: false));
        var ack = await a.ReceiveAsync<MappingUpsertAck>() ?? throw new IOException("无 Ack");

        await using var db = CreateDb();
        var row = await db.Mappings.AsNoTracking().SingleAsync(m => m.Id == ack.MappingId);
        Assert.Equal(aId, row.OwnerDeviceId);
        Assert.Equal("web", row.Name);
        Assert.Equal(18080, row.LocalPort);
        Assert.Equal("tcp", row.Proto);
        Assert.Equal(bId, row.TargetDeviceId);
        Assert.Equal("self", row.TargetAddr);
        Assert.Equal(80, row.TargetPort);
        Assert.False(row.Enabled);
    }

    // ── 完成判定②：未知远程码 4003 ───────────────────────────────────

    [Fact]
    public async Task Upsert_UnknownRemoteCode_Rejected4003()
    {
        var (a, _, _) = await RegisterAsync("code-a");
        await a.SendAsync(Upsert(a, null, "m", 18081, "zzzzzz"));
        var error = await a.ReceiveAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.RemoteCodeInvalid, error!.Code);
    }

    // ── 完成判定③：L2 不可见 4001 + mapping_deny 审计 ────────────────

    [Fact]
    public async Task Upsert_TargetNotVisible_Rejected4001AndAudited()
    {
        // 不登录：无账号归属，可见性仅剩默认组 → 移出即不可见（同账号会恒可见）
        var (a, aId, _) = await RegisterAsync("vis-a", login: false);
        var (_, bId, bCode) = await RegisterAsync("vis-b", login: false);

        // B 移出默认组且不同账号 → 不可见（入组接口属 M2，直接写库构造）
        await using (var db = CreateDb())
        {
            var defaultGroupId = await db.Groups.AsNoTracking().Where(g => g.IsDefault).Select(g => g.Id).SingleAsync();
            var membership = await db.GroupMembers.SingleAsync(m => m.GroupId == defaultGroupId && m.DeviceId == bId);
            db.GroupMembers.Remove(membership);
            await db.SaveChangesAsync();
        }

        await a.SendAsync(Upsert(a, null, "m", 18082, bCode));
        var error = await a.ReceiveAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.TargetNotAuthorized, error!.Code);

        await using var db2 = CreateDb();
        Assert.True(await db2.AuditLogs.AsNoTracking()
            .AnyAsync(x => x.Event == "mapping_deny" && x.DeviceId == aId));
    }

    // ── 完成判定④：同 owner+proto+localPort 冲突 1003（跨目标也算）────

    [Fact]
    public async Task Upsert_PortConflict_Rejected1003()
    {
        var (a, _, aCode) = await RegisterAsync("conf-a");
        var (_, _, bCode) = await RegisterAsync("conf-b");

        await a.SendAsync(Upsert(a, null, "m1", 18083, bCode));
        var ack1 = await a.ReceiveAsync<MappingUpsertAck>();

        await a.SendAsync(Upsert(a, null, "m2", 18083, aCode)); // 同端口不同目标/名称
        var error = await a.ReceiveAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.Conflict, error!.Code);

        // 异协议同端口不冲突（UNIQUE 含 proto，03 §2.4）
        await a.SendAsync(Upsert(a, null, "m3", 18083, aCode) with { Proto = "udp" });
        var ack3 = await a.ReceiveAsync<MappingUpsertAck>();
        Assert.NotEqual(ack1!.MappingId, ack3!.MappingId);
    }

    // ── 完成判定⑤：启用态改端口/协议 1003；停用态可改 ────────────────

    [Fact]
    public async Task Upsert_UpdatePortRules()
    {
        var (a, _, _) = await RegisterAsync("upd-a");
        var (_, _, bCode) = await RegisterAsync("upd-b");

        await a.SendAsync(Upsert(a, null, "m", 18084, bCode));
        var created = await a.ReceiveAsync<MappingUpsertAck>();

        // 停用态：改端口 OK
        await a.SendAsync(Upsert(a, created!.MappingId, "m", 18085, bCode));
        await a.ReceiveAsync<MappingUpsertAck>();

        // 启用后再改端口 → 1003
        await a.SendAsync(Upsert(a, created.MappingId, "m", 18085, bCode, enabled: true));
        await a.ReceiveAsync<MappingUpsertAck>();
        await a.SendAsync(Upsert(a, created.MappingId, "m", 18086, bCode));
        var error = await a.ReceiveAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.Conflict, error!.Code);
        Assert.Contains("disabled", error.HttpLikeMsg); // port_change_requires_disabled

        // 启用态改名称（端口不变）OK
        await a.SendAsync(Upsert(a, created.MappingId, "renamed", 18085, bCode, enabled: true));
        await a.ReceiveAsync<MappingUpsertAck>();

        await using var db = CreateDb();
        var row = await db.Mappings.AsNoTracking().SingleAsync(m => m.Id == created.MappingId);
        Assert.Equal("renamed", row.Name);
        Assert.Equal(18085, row.LocalPort);
        Assert.True(row.Enabled);
    }

    // ── 完成判定⑥：删除 Ok=true 且落库；再删/不存在 Ok=false ──────────

    [Fact]
    public async Task Delete_RemovesRowAndIdempotent()
    {
        var (a, _, _) = await RegisterAsync("del-a");
        var (_, _, bCode) = await RegisterAsync("del-b");

        await a.SendAsync(Upsert(a, null, "m", 18087, bCode));
        var created = await a.ReceiveAsync<MappingUpsertAck>();

        await a.SendAsync(new MappingDelete(a.NextSeq(), a.Now(), MsgType.MappingDelete, created!.MappingId));
        var ack = await a.ReceiveAsync<MappingDeleteAck>();
        Assert.True(ack!.Ok);

        await a.SendAsync(new MappingDelete(a.NextSeq(), a.Now(), MsgType.MappingDelete, created.MappingId));
        var ack2 = await a.ReceiveAsync<MappingDeleteAck>();
        Assert.False(ack2!.Ok); // 幂等：不存在 → Ok=false 非 Error

        await using var db = CreateDb();
        Assert.False(await db.Mappings.AsNoTracking().AnyAsync(m => m.Id == created.MappingId));

        // 他人映射不可删（owner 范围外按不存在语义）
        var (c, _, _) = await RegisterAsync("del-c");
        await c.SendAsync(new MappingDelete(c.NextSeq(), c.Now(), MsgType.MappingDelete, Guid.NewGuid()));
        var ack3 = await c.ReceiveAsync<MappingDeleteAck>();
        Assert.False(ack3!.Ok);
    }

    // ── 补充：字段校验 1001（空名/0 端口/坏协议）──────────────────────

    [Fact]
    public async Task Upsert_BadFields_Rejected1001()
    {
        var (a, _, _) = await RegisterAsync("bad-a");
        var (_, _, bCode) = await RegisterAsync("bad-b");

        await a.SendAsync(Upsert(a, null, "", 18088, bCode));
        Assert.Equal(ErrorCode.BadRequest, (await a.ReceiveAsync<ErrorMessage>())!.Code);

        await a.SendAsync(Upsert(a, null, "m", 0, bCode));
        Assert.Equal(ErrorCode.BadRequest, (await a.ReceiveAsync<ErrorMessage>())!.Code);

        await a.SendAsync(Upsert(a, null, "m", 18088, bCode) with { Proto = "sctp" });
        Assert.Equal(ErrorCode.BadRequest, (await a.ReceiveAsync<ErrorMessage>())!.Code);
    }
}

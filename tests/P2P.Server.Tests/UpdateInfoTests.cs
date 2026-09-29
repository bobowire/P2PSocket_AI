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
/// M2-14 UpdateInfo 稳定接口（FR-S-804/OQ-5、02 §7 拒绝策略、02 §2.3）：
/// 版本不符 Hello → 5004 → 0x03 仍可达且响应完整（字段来自 server_config + 宿主编译协议版本）
/// → 随后断开；非 0x03 消息立即断开；正常版本已建立会话 0x03 响应与配置一致。
/// </summary>
public sealed class UpdateInfoTests : IAsyncLifetime
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

    private async Task<TestPcpClient> ConnectAsync()
    {
        var client = await TestPcpClient.ConnectAsync(_server.LocalEndPoint!);
        _clients.Add(client);
        return client;
    }

    // ── 版本不符路径：5004 → 0x03 唯一受理 → 断开（02 §7）──────────────

    [Fact]
    public async Task VersionReject_UpdateInfoStillAnswered_ThenClosed()
    {
        var client = await ConnectAsync();
        var ack = await client.HelloAsync(deviceId: null, version: (ushort)(ProtocolVersion.Current + 1));
        Assert.Equal(HelloStatus.VersionNotSupported, ack.Status);

        // 0x03 无签名（会话未建立、版本协商失败窗口）
        await client.SendAsync(new UpdateInfoRequest(client.NextSeq(), client.Now(), MsgType.UpdateInfo),
            sign: false);
        var resp = await client.ReceiveAsync<UpdateInfoResponse>() ?? throw new IOException("0x03 无应答");
        Assert.Equal("0.1.0", resp.LatestVersion);         // Seed 默认（宿主版本对齐）
        Assert.Equal(1, resp.MinProtocol);
        Assert.Equal(ProtocolVersion.Current, resp.MaxProtocol); // 宿主编译协议版本注入
        Assert.InRange(resp.MinProtocol, 1, resp.MaxProtocol);
        Assert.NotNull(resp.UpgradeUrl);
        Assert.NotNull(resp.Notes);

        // 应答后随即断开（拒绝策略终点）
        Assert.True(await client.WaitClosedAsync(3000));
    }

    [Fact]
    public async Task VersionReject_OtherMessage_ClosedImmediately()
    {
        var client = await ConnectAsync();
        var ack = await client.HelloAsync(deviceId: null, version: (ushort)(ProtocolVersion.Current + 1));
        Assert.Equal(HelloStatus.VersionNotSupported, ack.Status);

        // 非 0x03 消息（重复 Hello）：受理窗口外，直接断开
        await client.SendAsync(new Hello(client.NextSeq(), client.Now(), MsgType.Hello,
            (ushort)(ProtocolVersion.Current + 1), null, RandomGenerator.Bytes(16)), sign: false);
        Assert.True(await client.WaitClosedAsync(3000));
    }

    // ── 正常版本：已建立会话 0x03 响应与配置一致 ────────────────────────

    [Fact]
    public async Task UpdateInfo_EstablishedSession_MatchesConfig()
    {
        await using (var db = CreateDb())
        {
            db.ServerConfig.Single(c => c.Key == "update_latest_version").Value = "2.3.4";
            db.ServerConfig.Single(c => c.Key == "update_min_protocol").Value = "1";
            db.ServerConfig.Single(c => c.Key == "update_url").Value = "https://example.com/dl";
            db.ServerConfig.Single(c => c.Key == "update_notes").Value = "维护公告：周三 02:00";
            await db.SaveChangesAsync();
        }

        var client = await ConnectAsync();
        await client.HelloAsync(deviceId: null);
        var key = EcKeyPair.Generate();
        await client.SendAsync(new Register(client.NextSeq(), client.Now(), MsgType.Register,
            $"P2P-UPD{Guid.NewGuid():N}"[..16], "host-upd", "windows", "0.1.0",
            key.ExportPublicKey(), null, null, null), sign: false);
        var reg = await client.ReceiveAsync<RegisterAck>() ?? throw new IOException("注册无应答");
        client.EstablishWithSecret(Ecies.Decrypt(key, reg.DeviceSecretBox));

        // 已建立会话（signed）：0x03 正常查询升级信息
        await client.SendAsync(new UpdateInfoRequest(client.NextSeq(), client.Now(), MsgType.UpdateInfo));
        var resp = await client.ReceiveAsync<UpdateInfoResponse>() ?? throw new IOException("0x03 无应答");
        Assert.Equal("2.3.4", resp.LatestVersion);
        Assert.Equal(1, resp.MinProtocol);
        Assert.Equal(ProtocolVersion.Current, resp.MaxProtocol);
        Assert.Equal("https://example.com/dl", resp.UpgradeUrl);
        Assert.Equal("维护公告：周三 02:00", resp.Notes);

        // 连接保持（正常会话查询升级信息不断线）
        await client.SendAsync(new Heartbeat(client.NextSeq(), client.Now(), MsgType.Heartbeat));
        Assert.NotNull(await client.ReceiveAsync<HeartbeatAck>());
    }

    [Fact]
    public async Task UpdateInfo_MinProtocol_ClampedIntoRange()
    {
        // 越界配置（0 / 超 Current）防御：clamp 进 [1, Current]
        await using (var db = CreateDb())
        {
            db.ServerConfig.Single(c => c.Key == "update_min_protocol").Value = "0";
            await db.SaveChangesAsync();
        }
        var client = await ConnectAsync();
        await client.HelloAsync(deviceId: null, version: (ushort)(ProtocolVersion.Current + 1));
        await client.SendAsync(new UpdateInfoRequest(client.NextSeq(), client.Now(), MsgType.UpdateInfo),
            sign: false);
        var resp = await client.ReceiveAsync<UpdateInfoResponse>() ?? throw new IOException("0x03 无应答");
        Assert.Equal(1, resp.MinProtocol);
    }
}

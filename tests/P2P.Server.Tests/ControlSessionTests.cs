using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using MessagePack;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using P2P.Core.Utils;
using P2P.Server.Data;
using P2P.Server.Services;
using Xunit;

namespace P2P.Server.Tests;

/// <summary>
/// 控制会话层集成测试（09 §21：HMAC 错误/seq 重复/超窗断连计数；05 §5 会话状态机）。
/// 真实 TCP 回环 + 最小客户端桩，走完整 Hello/Proof 握手。
/// </summary>
public sealed class ControlSessionTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    private readonly DeviceRegistry _registry = new();
    private readonly ConcurrentQueue<IPcpMessage> _dispatched = new();
    private readonly List<TestPcpClient> _clients = [];
    private ControlServer _server = null!;

    public Task InitializeAsync()
    {
        var factory = new StubFactory(() => new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_db.DataSource).Options));
        using (var init = factory.CreateDbContext())
            DbInitializer.Initialize(init);
        _server = new ControlServer(factory, _registry, Dispatch);
        return _server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
    }

    public async Task DisposeAsync()
    {
        foreach (var c in _clients) await c.DisposeAsync();
        await _server.DisposeAsync();
        _db.Dispose();
    }

    private Task Dispatch(ControlSession session, IPcpMessage message)
    {
        _dispatched.Enqueue(message);
        return Task.CompletedTask;
    }

    private async Task<TestPcpClient> ConnectAsync()
    {
        var client = await TestPcpClient.ConnectAsync(_server.LocalEndPoint!);
        _clients.Add(client);
        return client;
    }

    /// <summary>预置一台已注册设备，返回 (deviceId, deviceSecret)。</summary>
    private (Guid DeviceId, byte[] Secret) SeedDevice()
    {
        var id = Guid.NewGuid();
        var secret = RandomGenerator.Bytes(32);
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_db.DataSource).Options);
        db.Devices.Add(new Device
        {
            Id = id,
            DeviceName = "测试机",
            Os = "windows",
            ClientVersion = "0.1.0",
            MacCode = $"MAC{Guid.NewGuid():N}"[..12],
            RemoteCode = "123456",
            VirtualIp = "100.64.0.2",
            StaticPubKey = new byte[65],
            DeviceSecret = secret,
            CreatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return (id, secret);
    }

    /// <summary>建立已认证会话：Hello → Proof → Established。</summary>
    private async Task<TestPcpClient> ConnectEstablishedAsync(Guid deviceId, byte[] secret)
    {
        var client = await ConnectAsync();
        var helloAck = await client.HelloAsync(deviceId);
        Assert.Equal(HelloStatus.Ok, helloAck.Status);
        var proofAck = await client.ProofAsync(secret);
        Assert.True(proofAck.Ok);
        return client;
    }

    private static async Task WaitForAsync(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.True(condition(), $"等待超时：{what}");
    }

    // ── 握手与状态机（05 §5）────────────────────────────────────────────

    [Fact]
    public async Task Hello_UnknownDevice_GetsNeedRegister()
    {
        var client = await ConnectAsync();
        var ack = await client.HelloAsync(deviceId: null);
        Assert.Equal(HelloStatus.NeedRegister, ack.Status);
        Assert.Equal(ProtocolVersion.Current, ack.ProtocolVersion);
        Assert.NotEmpty(ack.NonceS);
    }

    [Fact]
    public async Task Register_FromNeedRegisterSession_DispatchedUnsigned()
    {
        var client = await ConnectAsync();
        await client.HelloAsync(deviceId: null);

        // 未注册设备首个 Register 例外不签名（02 §2.2）
        await client.SendAsync(new Register(client.NextSeq(), client.Now(), MsgType.Register,
            "MAC:AA", "host-a", "windows", "0.1.0", new byte[65], null, null, null), sign: false);

        await WaitForAsync(() => _dispatched.OfType<Register>().Any(), "Register 到达分发器");
    }

    [Fact]
    public async Task Hello_KnownDevice_ProofEstablished_RegistryOnline()
    {
        var (deviceId, secret) = SeedDevice();
        var client = await ConnectEstablishedAsync(deviceId, secret);
        Assert.True(_registry.IsOnline(deviceId));

        // 断开后离线（CloseAsync → Unregister）
        await client.DisposeAsync();
        await WaitForAsync(() => !_registry.IsOnline(deviceId), "会话注销");
    }

    [Fact]
    public async Task Proof_WrongHmac_RejectedAndDisconnected()
    {
        var (deviceId, secret) = SeedDevice();
        var client = await ConnectAsync();
        await client.HelloAsync(deviceId);
        var ack = await client.ProofAsync(secret, valid: false);
        Assert.False(ack.Ok);
        Assert.True(await client.WaitClosedAsync(), "身份证明失败应断连");
        Assert.False(_registry.IsOnline(deviceId));
    }

    [Fact]
    public async Task Hello_ProtocolVersionMismatch_Rejected()
    {
        var client = await ConnectAsync();
        var ack = await client.HelloAsync(deviceId: null, version: 99);
        Assert.Equal(HelloStatus.VersionNotSupported, ack.Status);
        Assert.True(await client.WaitClosedAsync(), "版本不匹配应断连");
    }

    [Fact]
    public async Task UnexpectedMessage_BeforeHello_BadRequestAndDisconnect()
    {
        var client = await ConnectAsync();
        await client.SendAsync(new Heartbeat(client.NextSeq(), client.Now(), MsgType.Heartbeat), sign: false);
        var error = await client.ReceiveAsync<ErrorMessage>();
        Assert.NotNull(error);
        Assert.Equal(ErrorCode.BadRequest, error!.Code);
        Assert.True(await client.WaitClosedAsync(), "握手前意外消息应断连");
    }

    // ── Established 后逐帧校验（09 §21）─────────────────────────────────

    [Fact]
    public async Task Heartbeat_GetsInlineAck_WithServerTs()
    {
        var (deviceId, secret) = SeedDevice();
        var client = await ConnectEstablishedAsync(deviceId, secret);

        var before = client.Now();
        await client.SendAsync(new Heartbeat(client.NextSeq(), client.Now(), MsgType.Heartbeat));
        var ack = await client.ReceiveAsync<HeartbeatAck>();
        Assert.NotNull(ack);
        var after = client.Now();
        Assert.InRange((long)ack!.ServerTs, (long)before - 1000, (long)after + 1000);
    }

    [Fact]
    public async Task TimestampBeyondWindow_Gets5005_ConnectionStaysAlive()
    {
        var (deviceId, secret) = SeedDevice();
        var client = await ConnectEstablishedAsync(deviceId, secret);

        // ts 落后 200s（窗口 ±120s，OQ-12）
        var staleTs = client.Now() - 200_000;
        await client.SendAsync(new DeviceUpdate(client.NextSeq(), staleTs, MsgType.DeviceUpdate, "新名字"));

        var error = await client.ReceiveAsync<ErrorMessage>();
        Assert.NotNull(error);
        Assert.Equal(ErrorCode.TimeSkew, error!.Code);
        Assert.Contains("time_skew:", error.HttpLikeMsg); // 附 serverTs 供重算 offset

        // 5005 只拒该消息不断连：随后心跳仍通
        await client.SendAsync(new Heartbeat(client.NextSeq(), client.Now(), MsgType.Heartbeat));
        var ack = await client.ReceiveAsync<HeartbeatAck>();
        Assert.NotNull(ack);

        // 超窗消息不得进入分发器
        Assert.DoesNotContain(_dispatched, m => m is DeviceUpdate);
    }

    [Fact]
    public async Task TamperedHmac_DisconnectsAndCounts()
    {
        var (deviceId, secret) = SeedDevice();
        var client = await ConnectEstablishedAsync(deviceId, secret);
        var before = _server.HmacFailureCount;

        // 以错误密钥签名 → HMAC 校验失败 → 断连 + 计数（SEC-22）
        await client.SendAsync(new DeviceUpdate(client.NextSeq(), client.Now(), MsgType.DeviceUpdate, "x"),
            sign: true, keyOverride: RandomGenerator.Bytes(32));

        Assert.True(await client.WaitClosedAsync(), "HMAC 失败应断连");
        await WaitForAsync(() => _server.HmacFailureCount == before + 1, "断连计数递增");
        Assert.False(_registry.IsOnline(deviceId));
    }

    [Fact]
    public async Task SeqRegression_DisconnectsAndCounts()
    {
        var (deviceId, secret) = SeedDevice();
        var client = await ConnectEstablishedAsync(deviceId, secret);

        var seq = client.NextSeq();
        await client.SendAsync(new DeviceUpdate(seq, client.Now(), MsgType.DeviceUpdate, "一次"));
        await WaitForAsync(() => _dispatched.OfType<DeviceUpdate>().Any(), "首条消息分发");

        var before = _server.HmacFailureCount;
        await client.SendAsync(new DeviceUpdate(seq, client.Now(), MsgType.DeviceUpdate, "seq 回退"));

        Assert.True(await client.WaitClosedAsync(), "seq 回退应断连");
        await WaitForAsync(() => _server.HmacFailureCount == before + 1, "断连计数递增");
        Assert.Single(_dispatched.OfType<DeviceUpdate>());
    }

    [Fact]
    public async Task UnknownMsgType_Tolerated_SilentlyDropped()
    {
        var (deviceId, secret) = SeedDevice();
        var client = await ConnectEstablishedAsync(deviceId, secret);

        // 手工构造未登记 msgType 0x6F 的签名帧（绕过 PcpCodec 类型校验）
        var msgpack = MessagePackSerializer.Serialize(new PcpHeader(client.NextSeq(), client.Now(), 0x6F));
        var mac = Mac.HmacSha256(client.ConnMacKey, msgpack);
        await client.SendRawAsync([.. msgpack, .. mac]);

        // 容忍丢弃：连接仍活、后续心跳有应答、不进分发器（02 §7）
        await client.SendAsync(new Heartbeat(client.NextSeq(), client.Now(), MsgType.Heartbeat));
        var ack = await client.ReceiveAsync<HeartbeatAck>();
        Assert.NotNull(ack);
        Assert.DoesNotContain(_dispatched, m => m.MsgType == 0x6F);
    }

    // ── 单会话在线（DeviceRegistry 踢线）────────────────────────────────

    [Fact]
    public async Task SameDeviceSecondSession_KicksFirst()
    {
        var (deviceId, secret) = SeedDevice();
        var first = await ConnectEstablishedAsync(deviceId, secret);

        var second = await ConnectEstablishedAsync(deviceId, secret);
        Assert.True(_registry.IsOnline(deviceId));

        Assert.True(await first.WaitClosedAsync(), "旧会话应被踢线");
        Assert.True(_registry.IsOnline(deviceId), "新会话仍在线");
    }
}

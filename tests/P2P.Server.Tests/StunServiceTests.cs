using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using P2P.Core.Crypto;
using P2P.Core.Stun;
using P2P.Server.Data;
using P2P.Server.Services;
using Xunit;

namespace P2P.Server.Tests;

/// <summary>
/// STUN 服务测试（02 §3、05 §7；完成判定：认证通过回正确映射地址、未注册 deviceId 静默、错误 HMAC 静默）。
/// 另覆盖 ts 窗口 / nonce 去重与 5min 过期 / stun_auth=0；
/// M2-06 增 TCP Binding 短事务与四道闸（TD-18：①单 IP UDP 突发/TCP 并发 ③设备 QPS ④全局熔断 + reason 计数）。
/// </summary>
public sealed class StunServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly byte[] _secret = RandomGenerator.Bytes(32);
    private readonly Guid _deviceId = Guid.NewGuid();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private StunService _service = null!;
    private int _port;
    private int _tcpPort;

    public Task InitializeAsync()
    {
        _connection.Open();
        var factory = new StubFactory(CreateDb);
        using (var init = factory.CreateDbContext())
            DbInitializer.Initialize(init);
        using (var db = CreateDb())
        {
            db.Devices.Add(new Device
            {
                Id = _deviceId,
                MacCode = "STUN-HOST-01",
                RemoteCode = "0000a1",
                DeviceName = "stun-dev",
                VirtualIp = "100.64.0.2",
                StaticPubKey = new byte[65],
                DeviceSecret = _secret,
                CreatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }

        _service = new StunService(factory, requireAuth: true, _time);
        _service.StartAsync(0, 0);
        _port = _service.Port;
        _tcpPort = _service.TcpPort;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _service.DisposeAsync();
        _connection.Dispose();
    }

    private AppDbContext CreateDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);

    private static Task<UdpReceiveResult?> ReceiveOrNullAsync(UdpClient client, int timeoutMs = 500)
    {
        var tcs = new TaskCompletionSource<UdpReceiveResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cts = new CancellationTokenSource(timeoutMs); // 生命周期随接收任务（方法返回后仍需存活）
        _ = ReceiveAsync();
        return tcs.Task;

        async Task ReceiveAsync()
        {
            using (cts)
            {
                try { tcs.SetResult(await client.ReceiveAsync(cts.Token)); }
                catch (OperationCanceledException) { tcs.SetResult(null); }
                catch (Exception ex) { tcs.SetException(ex); }
            }
        }
    }

    private async Task<UdpReceiveResult?> RoundTripAsync(UdpClient client, byte[] wire)
    {
        if (client.Client.RemoteEndPoint is null)
            client.Connect(new IPEndPoint(IPAddress.Loopback, _port)); // 使 LocalEndPoint 解析出源地址
        await client.SendAsync(wire);
        return await ReceiveOrNullAsync(client);
    }

    private byte[] Build(byte[] secret, ulong? tsOverride = null, byte[]? nonceOverride = null, Guid? deviceOverride = null)
        => StunCodec.BuildBindingRequest(StunCodec.NewTransactionId(),
            deviceOverride ?? _deviceId, secret, tsOverride ?? (ulong)_time.GetLocalNow().ToUnixTimeMilliseconds(),
            nonceOverride ?? RandomGenerator.Bytes(16));

    // ── 完成判定①：认证通过回正确映射地址 ─────────────────────────────

    [Fact]
    public async Task AuthenticatedBinding_ReturnsXorMappedAddress()
    {
        using var client = new UdpClient(AddressFamily.InterNetwork);
        var result = await RoundTripAsync(client, Build(_secret));

        Assert.NotNull(result);
        Assert.True(StunCodec.TryParseBindingResponse(result!.Value.Buffer, out var response));
        var local = (IPEndPoint)client.Client.LocalEndPoint!;
        Assert.Equal(local.Address, response!.Mapped.Address); // 观察到的源地址
        Assert.Equal(local.Port, response.Mapped.Port);        // 观察到的源端口
    }

    // ── 完成判定②：未注册 deviceId 静默丢弃 ──────────────────────────

    [Fact]
    public async Task UnregisteredDevice_SilentlyDropped()
    {
        using var client = new UdpClient(AddressFamily.InterNetwork);
        var result = await RoundTripAsync(client, Build(_secret, deviceOverride: Guid.NewGuid()));
        Assert.Null(result);
    }

    // ── 完成判定③：错误 HMAC 静默丢弃 ─────────────────────────────────

    [Fact]
    public async Task WrongSecret_SilentlyDropped()
    {
        using var client = new UdpClient(AddressFamily.InterNetwork);
        var result = await RoundTripAsync(client, Build(RandomGenerator.Bytes(32)));
        Assert.Null(result);
    }

    [Fact]
    public async Task TimestampOutOfWindow_SilentlyDropped()
    {
        using var client = new UdpClient(AddressFamily.InterNetwork);
        var stale = (ulong)(_time.GetLocalNow().ToUnixTimeMilliseconds() - 121_000); // 超出 ±120s
        Assert.Null(await RoundTripAsync(client, Build(_secret, tsOverride: stale)));
    }

    [Fact]
    public async Task NonceReplay_SilentlyDroppedUntilTtlExpires()
    {
        using var client = new UdpClient(AddressFamily.InterNetwork);
        var nonce = RandomGenerator.Bytes(16);

        // 首次：正常回应
        Assert.NotNull(await RoundTripAsync(client, Build(_secret, nonceOverride: nonce)));
        // 同 nonce 重放（新事务）：静默
        Assert.Null(await RoundTripAsync(client, Build(_secret, nonceOverride: nonce)));

        // 5min 去重窗过期（含清扫周期）后：同 nonce 可再用
        _time.Advance(TimeSpan.FromMinutes(6));
        Assert.NotNull(await RoundTripAsync(client, Build(_secret, nonceOverride: nonce)));
    }

    [Fact]
    public async Task AuthDisabled_TestMode_RespondsWithoutValidation()
    {
        await RecreateAsync(requireAuth: false); // 换 stun_auth=0 实例

        using var client = new UdpClient(AddressFamily.InterNetwork);
        var result = await RoundTripAsync(client, Build(_secret, deviceOverride: Guid.NewGuid()));
        Assert.NotNull(result);
    }

    // ── M2-06：TCP Binding 短事务（FR-S-602、02 §3.3） ────────────────

    [Fact]
    public async Task TcpBinding_Authenticated_ReturnsXorMappedAddressThenCloses()
    {
        using var cts = new CancellationTokenSource(2000);
        using var client = new TcpClient(AddressFamily.InterNetwork); // 显式 IPv4：本地端点与映射地址族一致
        await client.ConnectAsync(IPAddress.Loopback, _tcpPort, cts.Token);
        var local = (IPEndPoint)client.Client.LocalEndPoint!;
        await using var stream = client.GetStream();

        await StunTcpFraming.WriteAsync(stream, Build(_secret), cts.Token);
        var response = await StunTcpFraming.TryReadAsync(stream, cts.Token);

        Assert.NotNull(response);
        Assert.True(StunCodec.TryParseBindingResponse(response!, out var parsed));
        Assert.Equal(local.Address, parsed!.Mapped.Address); // XOR-MAPPED = 连接源（TCP 同 UDP 语义）
        Assert.Equal(local.Port, parsed.Mapped.Port);

        var tail = new byte[1]; // 短事务：回包即关（02 §3.3）——后续读得 EOF
        Assert.Equal(0, await stream.ReadAsync(tail, cts.Token));
    }

    [Fact]
    public async Task TcpBinding_UnregisteredDevice_ClosedWithoutResponse_AuthCounted()
    {
        var before = _service.Guard.Snapshot().Auth;
        var (response, _) = await TcpExchangeAsync(_tcpPort, Build(_secret, deviceOverride: Guid.NewGuid()));

        Assert.Null(response); // 闸②：未注册直接断连，无回应（无回显放大）
        Assert.True(_service.Guard.Snapshot().Auth > before);
    }

    [Fact]
    public async Task TcpGarbage_MsgLenOverLimit_ConnectionClosed()
    {
        using var cts = new CancellationTokenSource(2000);
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, _tcpPort, cts.Token);
        await using var stream = client.GetStream();
        var wire = new byte[StunCodec.HeaderLen];
        BinaryPrimitives.WriteUInt16BigEndian(wire.AsSpan(2, 2), (ushort)(StunTcpFraming.MaxAttrLen + 1));
        await stream.WriteAsync(wire, cts.Token); // 裸写：绕过 WriteAsync 写侧一致性校验（发送方违约场景）

        byte[]? response = null;
        try { response = await StunTcpFraming.TryReadAsync(stream, cts.Token); }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }

        Assert.Null(response); // 协议错断连（AI-19）
        Assert.Equal(new StunDropStats(0, 0, 0), _service.Guard.Snapshot()); // 闸外路径不计丢弃
    }

    [Fact]
    public async Task TcpPerIpConcurrency_FifthConnectionFromSameIpDropped()
    {
        var holders = new List<TcpClient>();
        try
        {
            for (var i = 0; i < 4; i++)
            {
                var holder = new TcpClient();
                await holder.ConnectAsync(IPAddress.Loopback, _tcpPort);
                holders.Add(holder); // 连而不发：占用并发额度（3s 事务超时窗口内）
            }
            await Task.Delay(200); // 服务端 accept 记账就位

            var (response, _) = await TcpExchangeAsync(_tcpPort, Build(_secret));
            Assert.Null(response); // 闸① TCP：第 5 条即连即关
            Assert.Equal(1, _service.Guard.Snapshot().Rate);
        }
        finally
        {
            foreach (var holder in holders) holder.Dispose();
        }
    }

    // ── M2-06：四道闸 UDP 路径（阈值注入 + reason 计数，TD-18、05 §7.1） ──

    [Fact]
    public async Task UdpBurst_BeyondPerIpBurst_DroppedAndCounted()
    {
        await RecreateAsync(requireAuth: true, new StunGuardOptions { PerDeviceQps = 1000 }); // 抬闸③孤测闸①

        using var client = new UdpClient(AddressFamily.InterNetwork);
        client.Connect(new IPEndPoint(IPAddress.Loopback, _port));
        for (var i = 0; i < 105; i++)
            await client.SendAsync(Build(_secret)); // FakeTime 冻结 → 无补币，突发容量 100 耗尽
        Assert.Equal(100, await DrainResponsesAsync(client));
        Assert.Equal(5, _service.Guard.Snapshot().Rate);

        _time.Advance(TimeSpan.FromSeconds(1)); // 补币 50 枚 → 恢复放行
        Assert.NotNull(await RoundTripAsync(client, Build(_secret)));
    }

    [Fact]
    public async Task PerDeviceQps_BeyondThreshold_DroppedThenRecovers()
    {
        await RecreateAsync(requireAuth: true, new StunGuardOptions { PerIpPps = 1000 }); // 抬闸①孤测闸③

        using var client = new UdpClient(AddressFamily.InterNetwork);
        client.Connect(new IPEndPoint(IPAddress.Loopback, _port));
        for (var i = 0; i < 12; i++)
            await client.SendAsync(Build(_secret));
        Assert.Equal(10, await DrainResponsesAsync(client)); // QPS=10 放行前十
        Assert.Equal(2, _service.Guard.Snapshot().Rate);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.NotNull(await RoundTripAsync(client, Build(_secret)));
    }

    [Fact]
    public async Task CircuitBreak_GlobalThreshold_OpensDropsRecovers()
    {
        await RecreateAsync(requireAuth: true,
            new StunGuardOptions { PerIpPps = 1000, PerDeviceQps = 1000, CircuitPps = 5 }); // 孤测闸④

        using var client = new UdpClient(AddressFamily.InterNetwork);
        client.Connect(new IPEndPoint(IPAddress.Loopback, _port));
        for (var i = 0; i < 5; i++)
            await client.SendAsync(Build(_secret));
        Assert.Equal(5, await DrainResponsesAsync(client)); // 窗内 ≤ 阈值全放行

        Assert.Null(await RoundTripAsync(client, Build(_secret))); // 第 6 条：超阈触发熔断即拒
        Assert.Null(await RoundTripAsync(client, Build(_secret))); // 熔断期内：一律拒
        Assert.Equal(2, _service.Guard.Snapshot().Circuit);

        _time.Advance(TimeSpan.FromSeconds(6)); // 熔断 5s 过期 → 恢复
        Assert.NotNull(await RoundTripAsync(client, Build(_secret)));
    }

    // ── 辅助 ─────────────────────────────────────────────────────────

    /// <summary>重建服务实例（换 auth 开关/闸参数；FakeTime 与库共享）。</summary>
    private async Task RecreateAsync(bool requireAuth, StunGuardOptions? guard = null)
    {
        await _service.DisposeAsync();
        _service = new StunService(new StubFactory(CreateDb), requireAuth, _time, guard);
        _ = _service.StartAsync(0, 0);
        _port = _service.Port;
        _tcpPort = _service.TcpPort;
    }

    /// <summary>TCP 单事务往返（null=无响应：干净关闭/重置/超时统一）。</summary>
    private static async Task<(byte[]? Response, IPEndPoint? Local)> TcpExchangeAsync(
        int port, byte[] wire, int timeoutMs = 2000)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            var local = (IPEndPoint)client.Client.LocalEndPoint!;
            await using var stream = client.GetStream();
            await StunTcpFraming.WriteAsync(stream, wire, cts.Token);
            return (await StunTcpFraming.TryReadAsync(stream, cts.Token), local);
        }
        catch (Exception ex) when (ex is IOException or SocketException
            or ObjectDisposedException or OperationCanceledException)
        {
            return (null, null);
        }
    }

    /// <summary>排空已收响应（每次等 200ms，超时即止）。</summary>
    private static async Task<int> DrainResponsesAsync(UdpClient client)
    {
        var count = 0;
        while (await ReceiveOrNullAsync(client, 200) is not null) count++;
        return count;
    }
}

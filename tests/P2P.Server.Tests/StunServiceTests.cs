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
/// 另覆盖 ts 窗口 / nonce 去重与 5min 过期 / stun_auth=0。
/// </summary>
public sealed class StunServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly byte[] _secret = RandomGenerator.Bytes(32);
    private readonly Guid _deviceId = Guid.NewGuid();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private StunService _service = null!;
    private int _port;

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
        _service.StartAsync(0);
        _port = _service.Port;
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
        await _service.DisposeAsync(); // 换 stun_auth=0 实例
        var factory = new StubFactory(CreateDb);
        _service = new StunService(factory, requireAuth: false, _time);
        _ = _service.StartAsync(0);
        _port = _service.Port;

        using var client = new UdpClient(AddressFamily.InterNetwork);
        var result = await RoundTripAsync(client, Build(_secret, deviceOverride: Guid.NewGuid()));
        Assert.NotNull(result);
    }
}

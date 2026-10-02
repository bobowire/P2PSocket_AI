// M3-16 ping-device 单测（04 §2.6）：RTT 统计/丢失容忍/错误通道（1001/4003/1002）——
// DeviceTunnel 抽象使编排器纯单测，不依赖 TunnelSession/ControlClient。
using System.IO;
using P2P.Client.Diagnostics;
using P2P.Core.Protocol;
using Xunit;

namespace P2P.Client.Tests;

public class DevicePingerTests
{
    private static DeviceListItem Dev(Guid id, string remoteCode, string name = "peer") =>
        new(id, name, remoteCode, "100.64.0.2", true, [], []);

    private static DevicePinger Make(
        IReadOnlyList<DeviceListItem> devices,
        Func<Guid, DeviceTunnel?>? tunnelLookup = null) => new(
        _ => Task.FromResult(devices),
        tunnelLookup ?? (_ => new DeviceTunnel(false,
            _ => Task.FromResult(TimeSpan.FromMilliseconds(15)))));

    [Fact]
    public async Task RunAsync_AllReceived_StatsAndViaRelay()
    {
        var id = Guid.NewGuid();
        var delays = new Queue<long>([11, 13, 12, 14]);
        var pinger = new DevicePinger(
            _ => Task.FromResult<IReadOnlyList<DeviceListItem>>([Dev(id, "483921")]),
            _ => new DeviceTunnel(true, _ => Task.FromResult(TimeSpan.FromMilliseconds(delays.Dequeue()))));

        var view = await pinger.RunAsync("483921");

        Assert.Equal("peer", view.TargetDevice);
        Assert.Equal("483921", view.TargetRemoteCode);
        Assert.Equal(4, view.Sent);
        Assert.Equal(4, view.Received);
        Assert.Equal(11, view.MinMs);
        Assert.Equal(14, view.MaxMs);
        Assert.Equal(12.5, view.AvgMs);
        Assert.True(view.ViaRelay);
        Assert.True(view.DurationMs >= 0);
    }

    [Fact]
    public async Task RunAsync_PartialLoss_OnlyReceivedCounted()
    {
        var id = Guid.NewGuid();
        var calls = 0;
        var pinger = new DevicePinger(
            _ => Task.FromResult<IReadOnlyList<DeviceListItem>>([Dev(id, "483921")]),
            _ => new DeviceTunnel(false, async ct =>
            {
                if (++calls % 2 == 0) throw new IOException("PONG 超时");
                return TimeSpan.FromMilliseconds(20);
            }));

        var view = await pinger.RunAsync("483921");

        Assert.Equal(4, view.Sent);
        Assert.Equal(2, view.Received);
        Assert.Equal(20, view.MinMs);
        Assert.Equal(20, view.MaxMs);
        Assert.False(view.ViaRelay);
    }

    [Fact]
    public async Task RunAsync_AllTimeout_StatsNull()
    {
        var id = Guid.NewGuid();
        var pinger = new DevicePinger(
            _ => Task.FromResult<IReadOnlyList<DeviceListItem>>([Dev(id, "483921")]),
            _ => new DeviceTunnel(false, _ => throw new IOException("PONG 超时")));

        var view = await pinger.RunAsync("483921");

        Assert.Equal(4, view.Sent);
        Assert.Equal(0, view.Received);
        Assert.Null(view.MinMs);
        Assert.Null(view.AvgMs);
        Assert.Null(view.MaxMs);
    }

    [Fact]
    public async Task RunAsync_UnknownRemoteCode_4003()
    {
        var pinger = Make([Dev(Guid.NewGuid(), "111111")]);

        var ex = await Assert.ThrowsAsync<DiagnosticsException>(() => pinger.RunAsync("999999"));

        Assert.Equal((int)ErrorCode.RemoteCodeInvalid, ex.Code);
    }

    [Fact]
    public async Task RunAsync_NoTunnel_1002WithHint()
    {
        var id = Guid.NewGuid();
        var pinger = Make([Dev(id, "483921", "办公室 NAS")], _ => null);

        var ex = await Assert.ThrowsAsync<DiagnosticsException>(() => pinger.RunAsync("483921"));

        Assert.Equal((int)ErrorCode.NotFound, ex.Code);
        Assert.Contains("须先启用一条到该设备的映射", ex.Message);
        Assert.Contains("办公室 NAS", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RunAsync_BlankCode_1001(string? code)
    {
        var pinger = Make([Dev(Guid.NewGuid(), "483921")]);

        var ex = await Assert.ThrowsAsync<DiagnosticsException>(() => pinger.RunAsync(code));

        Assert.Equal((int)ErrorCode.BadRequest, ex.Code);
    }

    [Fact]
    public async Task RunAsync_CodeMatchCaseInsensitiveAndTrimmed()
    {
        var id = Guid.NewGuid();
        var pinger = new DevicePinger(
            _ => Task.FromResult<IReadOnlyList<DeviceListItem>>([Dev(id, "48abc1")]),
            _ => new DeviceTunnel(false, _ => Task.FromResult(TimeSpan.FromMilliseconds(5))));

        var view = await pinger.RunAsync(" 48ABC1 ");

        Assert.Equal("48abc1", view.TargetRemoteCode); // 大小写不敏感+首尾空白容忍命中同一设备
        Assert.Equal(4, view.Received);
    }

    [Fact]
    public async Task RunAsync_OuterCancellation_Propagates()
    {
        var id = Guid.NewGuid();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var pinger = new DevicePinger(
            _ => Task.FromResult<IReadOnlyList<DeviceListItem>>([Dev(id, "483921")]),
            _ => new DeviceTunnel(false, _ => Task.FromResult(TimeSpan.FromMilliseconds(5))));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pinger.RunAsync("483921", cts.Token));
    }
}

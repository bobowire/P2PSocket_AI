using P2P.Core.Utils;
using Xunit;

namespace P2P.Core.Tests;

/// <summary>时钟对齐（02 §2.3 OQ-12）：offset = serverTs + RTT/2 − t(收到响应)。</summary>
public sealed class ClockSyncTests
{
    [Fact]
    public void NotCalibrated_Initially()
    {
        var sync = new ClockSync();
        Assert.False(sync.Calibrated);
        Assert.Equal(0, sync.OffsetMs);
    }

    [Fact]
    public void Calibrate_SymmetricDelay_ExactOffset()
    {
        // 对称半程 50ms：服务器在 ts=50 发出（其时钟=本地+0），客户端本地 100 收到
        var sync = new ClockSync();
        sync.Calibrate(serverTsMs: 50, sentAtLocalMs: 0, receivedAtLocalMs: 100);
        Assert.True(sync.Calibrated);
        Assert.Equal(0, sync.OffsetMs);
    }

    [Fact]
    public void Calibrate_ServerAhead_ComputesPositiveOffset()
    {
        // 服务器时钟快 1000ms：本地 0 发、本地 100 收（RTT=100，半程 50），服务器在自身 1050 发出
        var sync = new ClockSync();
        sync.Calibrate(serverTsMs: 1050, sentAtLocalMs: 0, receivedAtLocalMs: 100);
        Assert.Equal(1000, sync.OffsetMs);
    }

    [Fact]
    public void Calibrate_AsymmetricDelay_ErrorBoundedByHalfProcessingTime()
    {
        // 真实 offset=1000；去程 40ms、服务端处理 20ms、回程 40ms（RTT=100）
        // 服务器在本地 60（=自身 1060）时刻发出响应 → 估计 offset = 1060 + 50 − 100 = 1010
        var sync = new ClockSync();
        sync.Calibrate(serverTsMs: 1060, sentAtLocalMs: 0, receivedAtLocalMs: 100);
        Assert.InRange(Math.Abs(sync.OffsetMs - 1000), 0, 10); // 误差 ≤ 处理时延/2（02 §2.3）
    }

    [Fact]
    public void Calibrate_NegativeRtt_ClampedToZero()
    {
        // 本地时钟回拨导致 recv < sent：RTT 钳 0，不产生荒谬 offset
        var sync = new ClockSync();
        sync.Calibrate(serverTsMs: 1000, sentAtLocalMs: 100, receivedAtLocalMs: 50);
        Assert.Equal(950, sync.OffsetMs);
    }

    [Fact]
    public void ToRemoteMs_AppliesOffset()
    {
        var sync = new ClockSync();
        sync.Calibrate(serverTsMs: 1050, sentAtLocalMs: 0, receivedAtLocalMs: 100); // offset=1000
        Assert.Equal(6000uL, sync.ToRemoteMs(5000));
    }

    [Fact]
    public void NowRemoteMs_UsesTimeProvider()
    {
        var sync = new ClockSync();
        sync.Calibrate(serverTsMs: 1050, sentAtLocalMs: 0, receivedAtLocalMs: 100); // offset=1000
        var tp = new FixedTimeProvider(123_456L);
        Assert.Equal(124_456uL, sync.NowRemoteMs(tp));
    }

    private sealed class FixedTimeProvider(long unixMs) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
            => DateTimeOffset.FromUnixTimeMilliseconds(unixMs); // ToUnixTimeMilliseconds 不受时区影响
    }
}

using System.Net;
using Microsoft.Extensions.Time.Testing;
using P2P.Server.Services;
using Xunit;

namespace P2P.Server.Tests;

/// <summary>
/// 四道闸纯逻辑（TD-18、05 §7.1）：令牌桶突发容量/按速补币/封顶/多 IP 独立、
/// TCP 并发进出与重复释放、设备 QPS 桶、全局熔断开合与秒窗重置、计数分桶互不串扰。
/// </summary>
public sealed class StunGuardTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    [Fact]
    public void UdpTokenBucket_BurstCapacityThenRefill()
    {
        var guard = new StunGuard(new StunGuardOptions(), _time); // 默认 50pps/突发 100
        var ip = IPAddress.Loopback;
        for (var i = 0; i < 100; i++) Assert.True(guard.TryAcquireUdp(ip)); // 初始满桶
        Assert.False(guard.TryAcquireUdp(ip));
        Assert.Equal(1, guard.Snapshot().Rate);

        _time.Advance(TimeSpan.FromSeconds(1)); // 按速补 50 枚
        for (var i = 0; i < 50; i++) Assert.True(guard.TryAcquireUdp(ip));
        Assert.False(guard.TryAcquireUdp(ip));
        Assert.Equal(2, guard.Snapshot().Rate);
    }

    [Fact]
    public void UdpTokenBucket_LongIdle_RefillCappedAtBurst()
    {
        var guard = new StunGuard(new StunGuardOptions(), _time);
        var ip = IPAddress.Parse("127.0.0.7");
        for (var i = 0; i < 100; i++) Assert.True(guard.TryAcquireUdp(ip));
        _time.Advance(TimeSpan.FromSeconds(60));
        for (var i = 0; i < 100; i++) Assert.True(guard.TryAcquireUdp(ip)); // 封顶 100 不越
        Assert.False(guard.TryAcquireUdp(ip));
    }

    [Fact]
    public void UdpTokenBucket_DistinctIpIndependent()
    {
        var guard = new StunGuard(new StunGuardOptions { PerIpPps = 1, PerIpBurst = 1 }, _time);
        Assert.True(guard.TryAcquireUdp(IPAddress.Parse("127.0.0.1")));
        Assert.False(guard.TryAcquireUdp(IPAddress.Parse("127.0.0.1")));
        Assert.True(guard.TryAcquireUdp(IPAddress.Parse("127.0.0.2"))); // 各 IP 独立配额
    }

    [Fact]
    public void TcpConcurrency_CapReleaseRerelease()
    {
        var guard = new StunGuard(new StunGuardOptions(), _time); // 默认 4
        var ip = IPAddress.Loopback;
        for (var i = 0; i < 4; i++) Assert.True(guard.TryAcquireTcp(ip));
        Assert.False(guard.TryAcquireTcp(ip)); // 第 5 条拒
        Assert.Equal(1, guard.Snapshot().Rate);

        guard.ReleaseTcp(ip);
        Assert.True(guard.TryAcquireTcp(ip)); // 释放后额度回收
        guard.ReleaseTcp(ip);
        guard.ReleaseTcp(ip);
        guard.ReleaseTcp(ip); // 多次释放安全（计数随连接终结）
        Assert.True(guard.TryAcquireTcp(ip));
    }

    [Fact]
    public void DeviceQps_TokenBucketAtQps()
    {
        var guard = new StunGuard(new StunGuardOptions(), _time); // 默认 10
        var deviceId = Guid.NewGuid();
        for (var i = 0; i < 10; i++) Assert.True(guard.TryAcquireDevice(deviceId));
        Assert.False(guard.TryAcquireDevice(deviceId));
        Assert.Equal(1, guard.Snapshot().Rate);

        _time.Advance(TimeSpan.FromMilliseconds(100)); // 按速补 1 枚（10/s）
        Assert.True(guard.TryAcquireDevice(deviceId));
        Assert.False(guard.TryAcquireDevice(deviceId));
    }

    [Fact]
    public void Circuit_OpensAtThreshold_DropsDuringBreak_RecoversAfter()
    {
        var guard = new StunGuard(
            new StunGuardOptions { CircuitPps = 3, CircuitBreak = TimeSpan.FromSeconds(5) }, _time);
        for (var i = 0; i < 3; i++) Assert.True(guard.AdmitArrival()); // 窗内 ≤ 阈值放行
        Assert.False(guard.AdmitArrival()); // 第 4 条触发熔断
        Assert.False(guard.AdmitArrival()); // 熔断期内一律拒
        Assert.Equal(2, guard.Snapshot().Circuit);

        _time.Advance(TimeSpan.FromSeconds(5)); // 到期即恢复（半开=新秒窗重评）
        Assert.True(guard.AdmitArrival());
    }

    [Fact]
    public void Circuit_NewSecondWindow_ResetsCount()
    {
        var guard = new StunGuard(new StunGuardOptions { CircuitPps = 3 }, _time);
        for (var i = 0; i < 3; i++) Assert.True(guard.AdmitArrival());
        _time.Advance(TimeSpan.FromSeconds(1)); // 跨秒窗：计数不累计
        for (var i = 0; i < 3; i++) Assert.True(guard.AdmitArrival());
        Assert.Equal(0, guard.Snapshot().Circuit);
    }

    [Fact]
    public void CountAuth_OnlyAuthBucket()
    {
        var guard = new StunGuard(new StunGuardOptions(), _time);
        guard.CountAuth();
        guard.CountAuth();
        guard.CountAuth();
        Assert.Equal(new StunDropStats(0, 3, 0), guard.Snapshot());
    }
}

// M7-02 PunchModePlanner 单测（设计 12 §10.1）：优先模式/交替序列/Begin 重置/并发安全。
// 返回值按执行层口径断言（设计 12 §2.1 映射：配置 kcp → 执行 udp）。
using P2P.Client.Punch;
using Xunit;

namespace P2P.Client.Tests.Punch;

public sealed class PunchModePlannerTests
{
    [Fact]
    public void Begin_返回优先模式_执行层口径()
    {
        var planner = new PunchModePlanner(_ => "kcp");
        Assert.Equal("udp", planner.Begin(Guid.NewGuid())); // 配置 kcp → 执行 udp（§2.1 映射）
        var tcp = new PunchModePlanner(_ => "tcp");
        Assert.Equal("tcp", tcp.Begin(Guid.NewGuid()));
    }

    [Fact]
    public void NextRetry_tcp优先_以tcp为起点交替_执行层tcp_udp_tcp()
    {
        var peer = Guid.NewGuid();
        var planner = new PunchModePlanner(_ => "tcp");
        var seq = Enumerable.Range(0, 5).Select(_ => planner.NextRetry(peer)).ToArray();
        Assert.Equal(["tcp", "udp", "tcp", "udp", "tcp"], seq); // 决策②：以优先模式为起点
    }

    [Fact]
    public void NextRetry_kcp优先_以kcp为起点交替_执行层udp_tcp_udp()
    {
        var peer = Guid.NewGuid();
        var planner = new PunchModePlanner(_ => "kcp");
        var seq = Enumerable.Range(0, 5).Select(_ => planner.NextRetry(peer)).ToArray();
        Assert.Equal(["udp", "tcp", "udp", "tcp", "udp"], seq); // 对称规则（§2.2）
    }

    [Fact]
    public void Begin后计数重置_重试重新自优先模式交替()
    {
        var peer = Guid.NewGuid();
        var planner = new PunchModePlanner(_ => "tcp");
        Assert.Equal("tcp", planner.NextRetry(peer));
        Assert.Equal("udp", planner.NextRetry(peer));
        Assert.Equal("tcp", planner.Begin(peer)); // 新序列触发（手动重试等，§5.4）：重置计数
        Assert.Equal("tcp", planner.NextRetry(peer)); // 重新自优先模式开始交替
        Assert.Equal("udp", planner.NextRetry(peer));
    }

    [Fact]
    public async Task 并发NextRetry_无跳号无异常()
    {
        var peer = Guid.NewGuid();
        var planner = new PunchModePlanner(_ => "tcp");
        var results = new System.Collections.Concurrent.ConcurrentBag<string>();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 50; i++)
            {
                results.Add(planner.NextRetry(peer));
                await Task.Yield();
            }
        })));
        Assert.Equal(200, results.Count); // 无异常无丢失
        // 无跳号：计数严格连续 → 前缀恰有 N 个 tcp 与 N 个 udp（第 n 次调用 n%2==0→tcp）。
        // 200 次调用若计数无跳号则恰 100/100；跳号（如并发递增丢失）必失衡。
        Assert.Equal(100, results.Count(r => r == "tcp"));
        Assert.Equal(100, results.Count(r => r == "udp"));
        // 设备对隔离：另一对独立计数不受并发影响
        var other = Guid.NewGuid();
        Assert.Equal("tcp", planner.NextRetry(other));
        Assert.Equal("udp", planner.NextRetry(other));
    }
}

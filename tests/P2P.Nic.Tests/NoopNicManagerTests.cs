// M8-01 NoopNicManager 单测（设计 13 §10.1/§4.2）：恒成功幂等、恒健康、Remove/RemoveLeftover
// no-op 不抛——无卡端口映射模式的产品级语义（模板=集成测试 StubNicManager，设计 13 §4.2）。
using System.Net;
using P2P.Nic;
using Xunit;

namespace P2P.Nic.Tests;

public sealed class NoopNicManagerTests
{
    private static readonly IPAddress Vip = IPAddress.Parse("100.64.0.2");

    [Fact]
    public async Task Ensure_恒成功返回loopback句柄_幂等()
    {
        var manager = new NoopNicManager();
        var first = await manager.EnsureAsync(Vip);
        var second = await manager.EnsureAsync(Vip);
        Assert.Equal(NoopNicManager.LoopbackAdapterName, first.AdapterName);
        Assert.Equal(Vip, first.VirtualIp);
        Assert.Equal(first, second); // 幂等：重复 Ensure 同值安全（FR-C-201 语义等价）
    }

    [Fact]
    public void CheckHealth_恒Healthy()
    {
        var manager = new NoopNicManager();
        var health = manager.CheckHealth(Vip);
        Assert.Equal(NicHealthState.Healthy, health.State);
        Assert.Equal(Vip, health.BoundIp);
    }

    [Fact]
    public async Task Remove与RemoveLeftover_noop不抛_无实际移除()
    {
        var manager = new NoopNicManager();
        await manager.RemoveAsync();
        await manager.RemoveAsync(); // 幂等
        Assert.False(await manager.RemoveLeftoverAsync()); // false=未实际移除（FR-C-203）
        Assert.False(await manager.RemoveLeftoverAsync());
        // no-op 后 Ensure 语义不变（无状态可被破坏）
        Assert.Equal(NoopNicManager.LoopbackAdapterName, (await manager.EnsureAsync(Vip)).AdapterName);
    }
}

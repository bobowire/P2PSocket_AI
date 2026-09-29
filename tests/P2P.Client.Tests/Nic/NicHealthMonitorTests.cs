// M2-24 网卡与监听自愈测试（FR-C-202/FR-C-902 附、05 §1.2、M1 附录 A.4 遗留观察收口）：
// - 自愈循环：替身适配器被删/IP 被改 → 自动重建（Remove+Ensure）→ 恢复沿事件触发；
// - listen_failed 自动重试：端口占用（模拟地址/端口延迟生效）→ 进重试队列 → 释放后周期收敛
//   （用例 A）与恢复沿立即收敛（用例 B——NicHealthMonitor.Restored 接线同入口），
//   全程无手工 RetryAsync（不再需要手工 retry）。
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using P2P.Client.Mapping;
using P2P.Client.Nic;
using P2P.Client.Punch;
using P2P.Client.Tunnel;
using P2P.Core.Protocol;
using P2P.Nic;
using Xunit;

namespace P2P.Client.Tests;

public sealed class NicHealthMonitorTests
{
    private static async Task UntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = Environment.TickCount64 + (long)(timeout ?? TimeSpan.FromSeconds(8)).TotalMilliseconds;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException($"等待超时：{what}");
            await Task.Delay(10);
        }
    }

    /// <summary>可外部致病的网卡替身：Exists/BoundIp 模拟被删/IP 被改；Ensure 即重建。</summary>
    private sealed class FakeNic : INicManager
    {
        public volatile bool Exists = true;
        public IPAddress? BoundIp;
        public int EnsureCount;
        public int RemoveCount;

#pragma warning disable CS0067 // 自愈异常经 NicHealthMonitor 日志展示
        public event Action<string>? Degraded;
#pragma warning restore CS0067

        public Task<NicHandle> EnsureAsync(IPAddress virtualIp, CancellationToken ct = default)
        {
            Interlocked.Increment(ref EnsureCount);
            Exists = true; // 重建：适配器在位 + 恢复绑定
            BoundIp = virtualIp;
            return Task.FromResult(new NicHandle("fake-0", virtualIp));
        }

        public Task RemoveAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref RemoveCount);
            return Task.CompletedTask;
        }

        public Task<bool> RemoveLeftoverAsync(CancellationToken ct = default)
        {
            if (!Exists) return Task.FromResult(false);
            Exists = false;
            return Task.FromResult(true);
        }

        public NicHealth CheckHealth(IPAddress expectedIp)
            => !Exists ? new NicHealth(NicHealthState.AdapterMissing, null)
            : BoundIp is null || !BoundIp.Equals(expectedIp)
                ? new NicHealth(NicHealthState.IpMismatch, BoundIp)
                : new NicHealth(NicHealthState.Healthy, BoundIp);
    }

    [Fact]
    public async Task 网卡自愈_适配器被删_自动重建并触发恢复沿()
    {
        var vip = IPAddress.Parse("100.64.0.7");
        var nic = new FakeNic { BoundIp = vip };
        var restored = 0;
        await using var monitor = new NicHealthMonitor(nic, vip, TimeSpan.FromMilliseconds(50));
        monitor.Restored += () => Interlocked.Increment(ref restored);

        // 健康期不重建（防误报）
        await Task.Delay(150);
        Assert.Equal(0, Volatile.Read(ref nic.EnsureCount));

        nic.Exists = false; // 模拟外部删除（FR-C-202）

        await UntilAsync(() => Volatile.Read(ref nic.EnsureCount) >= 1
            && nic.CheckHealth(vip).State == NicHealthState.Healthy, "适配器重建恢复");
        Assert.True(Volatile.Read(ref nic.RemoveCount) >= 1, "重建前须先 Remove 拆除残留");
        Assert.True(Volatile.Read(ref restored) >= 1, "恢复沿须触发（驱动映射监听重试）");
    }

    [Fact]
    public async Task 网卡自愈_IP被改动_重建恢复配置()
    {
        var vip = IPAddress.Parse("100.64.0.9");
        var nic = new FakeNic { BoundIp = IPAddress.Parse("10.9.9.9") }; // 在位但绑定被改
        var restored = 0;
        await using var monitor = new NicHealthMonitor(nic, vip, TimeSpan.FromMilliseconds(50));
        monitor.Restored += () => Interlocked.Increment(ref restored);

        await UntilAsync(() => nic.BoundIp!.Equals(vip)
            && Volatile.Read(ref restored) >= 1, "IP 一致性重建");
        Assert.Equal(NicHealthState.Healthy, nic.CheckHealth(vip).State);
    }

    // ── listen_failed 自动重试（引擎侧，M2-24）──────────────────────

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private sealed class FakePuncher : IPuncher
    {
        public ConcurrentQueue<Guid> Initiated { get; } = [];

        public Task<PunchOutcome> InitiateAsync(Guid targetDeviceId, Guid? triggerMappingId, string proto,
            CancellationToken ct = default)
        {
            Initiated.Enqueue(targetDeviceId);
            return Task.FromResult(PunchOutcome.Failure(targetDeviceId, "no_behavior"));
        }

        public Task<PunchOutcome> RespondAsync(PunchInvite invite, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static MappingConfig TcpConfig(ushort port, Guid peer) =>
        new(Guid.NewGuid(), "m", port, "tcp", "self", 8080, peer);

    [Fact]
    public async Task 监听失败自动重试_端口延迟释放_周期收敛无需手工重试()
    {
        var peer = Guid.NewGuid();
        var puncher = new FakePuncher();
        await using var host = new TunnelHost();
        await using var scheduler = new PunchScheduler(puncher);
        await using var engine = new MappingEngine(host, scheduler, IPAddress.Loopback,
            new MappingEngineOptions { ListenRetryInterval = TimeSpan.FromMilliseconds(100) });

        var port = FreePort();
        var blocker = new TcpListener(IPAddress.Loopback, port); // 占住端口=模拟地址未就绪（AddressInUse）
        blocker.Start();
        try
        {
            await engine.EnableAsync(TcpConfig((ushort)port, peer));
            var snap = engine.Snapshots.Single();
            Assert.Equal(MappingState.Failed, snap.State);
            Assert.Contains("listen_failed", snap.Detail);

            blocker.Stop(); // 释放=网卡就绪（延迟生效）
            blocker.Dispose();

            // 周期重试收敛：绑定成功 → 打洞入队（无任何手工 RetryAsync 调用）
            await UntilAsync(() => puncher.Initiated.Count == 1, "监听重试后打洞入队");

            // 收敛后不再重复入队（重试集已摘）
            await Task.Delay(300);
            Assert.Single(puncher.Initiated);
        }
        finally
        {
            await engine.DisableAsync(engine.Snapshots.Single().Config.MappingId);
        }
    }

    [Fact]
    public async Task 监听重试_恢复沿入口立即收敛_不等周期()
    {
        var peer = Guid.NewGuid();
        var puncher = new FakePuncher();
        await using var host = new TunnelHost();
        await using var scheduler = new PunchScheduler(puncher);
        await using var engine = new MappingEngine(host, scheduler, IPAddress.Loopback,
            new MappingEngineOptions { ListenRetryInterval = TimeSpan.FromSeconds(10) }); // 周期不触达

        var port = FreePort();
        var blocker = new TcpListener(IPAddress.Loopback, port);
        blocker.Start();
        try
        {
            await engine.EnableAsync(TcpConfig((ushort)port, peer));
            Assert.Equal(MappingState.Failed, engine.Snapshots.Single().State);

            blocker.Stop();
            blocker.Dispose();

            // NicHealthMonitor.Restored 接线即调本入口：恢复沿立即重试，不待周期
            //（入队后调度器异步出队——3s 内收敛即证触发来自本入口而非 10s 周期）
            await engine.RetryListenFailedAsync();
            await UntilAsync(() => puncher.Initiated.Count == 1, "恢复沿立即收敛", TimeSpan.FromSeconds(3));
            Assert.Single(puncher.Initiated);
        }
        finally
        {
            await engine.DisableAsync(engine.Snapshots.Single().Config.MappingId);
        }
    }
}

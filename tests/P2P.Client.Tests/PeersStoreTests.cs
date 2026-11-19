// M2-23 PeersStore 单测（任务清单验收：端点形状契约在集成测；此处为文件语义——
// 默认关闭、往返持久化、损坏自愈重建默认、单条损坏键容错、落盘字段形态 03 §5）。
// M7-01 双字段扩展（设计 12 §10.1）：punchMode 默认/往返/旧文件容错/非法值容错（含 "udp"）/
// 落盘 camelCase 双字段/SetAsync 合并写不丢字段/非法值写抛校验异常。
using System.Text.Json;
using P2P.Client.Storage;
using Xunit;

namespace P2P.Client.Tests;

public sealed class PeersStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "p2p-peers-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void 无文件_默认回退关与打洞模式tcp()
    {
        var store = new PeersStore(_dir);
        store.Load();
        Assert.False(store.GetRelayFallback(Guid.NewGuid())); // PRD 06 §2：默认关闭
        Assert.Equal("tcp", store.GetPunchMode(Guid.NewGuid())); // 设计 12 决策①：默认 tcp
    }

    [Fact]
    public async Task 设置后往返持久化_覆盖生效()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var store = new PeersStore(_dir);
        store.Load();
        await store.SetAsync(a, relayFallback: true);
        await store.SetAsync(b, relayFallback: false, punchMode: "kcp");

        var reloaded = new PeersStore(_dir); // 新实例=重启语义
        reloaded.Load();
        Assert.True(reloaded.GetRelayFallback(a));
        Assert.False(reloaded.GetRelayFallback(b));
        Assert.Equal("tcp", reloaded.GetPunchMode(a));
        Assert.Equal("kcp", reloaded.GetPunchMode(b));

        await reloaded.SetAsync(a, relayFallback: false); // 覆盖
        await reloaded.SetAsync(b, punchMode: "tcp");
        Assert.False(reloaded.GetRelayFallback(a));
        Assert.Equal("tcp", reloaded.GetPunchMode(b));
        var again = new PeersStore(_dir);
        again.Load();
        Assert.False(again.GetRelayFallback(a));
        Assert.Equal("tcp", again.GetPunchMode(b));
    }

    [Fact]
    public async Task 落盘字段形态_deviceId键与camelCase双字段()
    {
        var target = Guid.NewGuid();
        var store = new PeersStore(_dir);
        store.Load();
        await store.SetAsync(target, relayFallback: true, punchMode: "kcp");

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, ClientPaths.PeersFileName)));
        var entry = doc.RootElement.GetProperty(target.ToString());
        Assert.True(entry.GetProperty("relayFallback").GetBoolean()); // 03 §5 { relayFallback, punchMode }
        Assert.Equal("kcp", entry.GetProperty("punchMode").GetString());
    }

    [Fact]
    public void 损坏文件_自愈重建默认_告警触发()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, ClientPaths.PeersFileName), "{ not json !!!");

        var store = new PeersStore(_dir);
        string? recovered = null;
        store.Recovered += m => recovered = m;
        store.Load(); // 不抛：允许进程继续（任务清单 M2-23：损坏自愈重建默认）
        Assert.False(store.GetRelayFallback(Guid.NewGuid()));
        Assert.Equal("tcp", store.GetPunchMode(Guid.NewGuid()));
        Assert.NotNull(recovered);
        Assert.Contains("peers.json", recovered);
    }

    [Fact]
    public void 非法键容错_合法条目保留()
    {
        Directory.CreateDirectory(_dir);
        var good = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_dir, ClientPaths.PeersFileName),
            $$"""{ "not-a-guid": { "relayFallback": true }, "{{good}}": { "relayFallback": true, "punchMode": "kcp" } }""");

        var store = new PeersStore(_dir);
        store.Load();
        Assert.True(store.GetRelayFallback(good)); // 单条损坏键丢弃，整体不作废
        Assert.Equal("kcp", store.GetPunchMode(good));
        Assert.False(store.GetRelayFallback(Guid.NewGuid()));
    }

    [Fact]
    public void 旧文件缺punchMode_容错回tcp_无损升级()
    {
        Directory.CreateDirectory(_dir);
        var target = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_dir, ClientPaths.PeersFileName),
            $$"""{ "{{target}}": { "relayFallback": true } }"""); // M2-23 旧形态（仅 relayFallback）

        var store = new PeersStore(_dir);
        store.Load();
        Assert.True(store.GetRelayFallback(target)); // 既有字段不受影响（无损升级）
        Assert.Equal("tcp", store.GetPunchMode(target)); // 反序列化 null → 规范化 tcp（设计 12 §3.2）
    }

    [Theory]
    [InlineData("TCP")]  // 大小写非规范（配置层值域小写）
    [InlineData("udp")]  // 执行层口径混入——配置层值域只有 tcp/kcp（设计 12 §2.1）
    [InlineData("xxx")]  // 任意手改垃圾值
    public void 非法punchMode容错_单条回tcp_整体不作废(string bad)
    {
        Directory.CreateDirectory(_dir);
        var target = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_dir, ClientPaths.PeersFileName),
            $$"""{ "{{target}}": { "relayFallback": true, "punchMode": "{{bad}}" } }""");

        var store = new PeersStore(_dir);
        store.Load();
        Assert.Equal("tcp", store.GetPunchMode(target)); // 单条容错回默认（与非法键丢弃同口径）
        Assert.True(store.GetRelayFallback(target)); // 同条目另一字段不受影响
    }

    [Fact]
    public async Task SetAsync合并写_单字段更新不丢另一字段()
    {
        var target = Guid.NewGuid();
        var store = new PeersStore(_dir);
        store.Load();
        await store.SetAsync(target, relayFallback: true, punchMode: "kcp");

        await store.SetAsync(target, relayFallback: false); // 只改回退：punchMode 须保持
        Assert.False(store.GetRelayFallback(target));
        Assert.Equal("kcp", store.GetPunchMode(target));

        await store.SetAsync(target, punchMode: "tcp"); // 只改模式：回退须保持
        Assert.False(store.GetRelayFallback(target));
        Assert.Equal("tcp", store.GetPunchMode(target));

        var reloaded = new PeersStore(_dir); // 落盘亦未丢（重启语义）
        reloaded.Load();
        Assert.False(reloaded.GetRelayFallback(target));
        Assert.Equal("tcp", reloaded.GetPunchMode(target));
    }

    [Fact]
    public async Task SetAsync非法punchMode_抛校验异常()
    {
        var store = new PeersStore(_dir);
        store.Load();
        // 配置层值域 tcp/kcp（"udp" 属执行层口径，设计 12 §2.1）；API 层依赖此异常转 1001
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync(Guid.NewGuid(), punchMode: "udp"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync(Guid.NewGuid(), punchMode: "TCP"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync(Guid.NewGuid(), punchMode: "xxx"));
        // 合法值不抛（kcp 往返）
        var ok = Guid.NewGuid();
        await store.SetAsync(ok, punchMode: "kcp");
        Assert.Equal("kcp", store.GetPunchMode(ok));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}

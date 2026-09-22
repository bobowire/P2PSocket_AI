// M2-23 PeersStore 单测（任务清单验收：端点形状契约在集成测；此处为文件语义——
// 默认关闭、往返持久化、损坏自愈重建默认、单条损坏键容错、落盘字段形态 03 §5）。
using System.Text.Json;
using P2P.Client.Storage;
using Xunit;

namespace P2P.Client.Tests;

public sealed class PeersStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "p2p-peers-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void 无文件_默认全部关闭()
    {
        var store = new PeersStore(_dir);
        store.Load();
        Assert.False(store.GetRelayFallback(Guid.NewGuid())); // PRD 06 §2：默认关闭
    }

    [Fact]
    public async Task 设置后往返持久化_覆盖生效()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var store = new PeersStore(_dir);
        store.Load();
        await store.SetRelayFallbackAsync(a, true);
        await store.SetRelayFallbackAsync(b, false);

        var reloaded = new PeersStore(_dir); // 新实例=重启语义
        reloaded.Load();
        Assert.True(reloaded.GetRelayFallback(a));
        Assert.False(reloaded.GetRelayFallback(b));

        await reloaded.SetRelayFallbackAsync(a, false); // 覆盖
        Assert.False(reloaded.GetRelayFallback(a));
        var again = new PeersStore(_dir);
        again.Load();
        Assert.False(again.GetRelayFallback(a));
    }

    [Fact]
    public async Task 落盘字段形态_deviceId键与camelCase()
    {
        var target = Guid.NewGuid();
        var store = new PeersStore(_dir);
        store.Load();
        await store.SetRelayFallbackAsync(target, true);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, ClientPaths.PeersFileName)));
        var entry = doc.RootElement.GetProperty(target.ToString());
        Assert.True(entry.GetProperty("relayFallback").GetBoolean()); // 03 §5 { relayFallback }
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
        Assert.NotNull(recovered);
        Assert.Contains("peers.json", recovered);
    }

    [Fact]
    public void 非法键容错_合法条目保留()
    {
        Directory.CreateDirectory(_dir);
        var good = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_dir, ClientPaths.PeersFileName),
            $$"""{ "not-a-guid": { "relayFallback": true }, "{{good}}": { "relayFallback": true } }""");

        var store = new PeersStore(_dir);
        store.Load();
        Assert.True(store.GetRelayFallback(good)); // 单条损坏键丢弃，整体不作废
        Assert.False(store.GetRelayFallback(Guid.NewGuid()));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}

// M2-11 LanSegmentsStore 单测（任务清单：文件语义——缺失默认空集 fail closed、往返持久化、
// 损坏自愈重建默认、单条非法条目容错、落盘字段形态 03 §5、enabled 过滤）。
using System.Text.Json;
using P2P.Client.Storage;
using Xunit;

namespace P2P.Client.Tests;

public sealed class LanSegmentsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "p2p-lansegs-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void 无文件_默认无段_非self一律不覆盖()
    {
        var store = new LanSegmentsStore(_dir);
        store.Load();
        Assert.Empty(store.EnabledCidrs()); // fail closed（SEC-52 第二道：空集=全拒）
    }

    [Fact]
    public async Task 全量替换后往返持久化_enabled过滤()
    {
        var store = new LanSegmentsStore(_dir);
        store.Load();
        await store.ReplaceAllAsync(
        [
            new LanSegmentsStore.LanSegmentEntry(Guid.NewGuid(), "192.168.1.0/24", true),
            new LanSegmentsStore.LanSegmentEntry(Guid.NewGuid(), "10.0.0.0/8", false), // disabled 不入快照
        ]);

        var reloaded = new LanSegmentsStore(_dir); // 新实例=重启语义
        reloaded.Load();
        var cidrs = reloaded.EnabledCidrs();
        Assert.Equal(["192.168.1.0/24"], cidrs);
    }

    [Fact]
    public async Task 落盘字段形态_数组小驼峰()
    {
        var id = Guid.NewGuid();
        var store = new LanSegmentsStore(_dir);
        store.Load();
        await store.ReplaceAllAsync([new LanSegmentsStore.LanSegmentEntry(id, "192.168.1.0/24", true)]);

        using var doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(_dir, ClientPaths.LanSegmentsFileName)));
        var entry = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal(id.ToString(), entry.GetProperty("segmentId").GetString()); // 03 §5 { segmentId, cidr, enabled }
        Assert.Equal("192.168.1.0/24", entry.GetProperty("cidr").GetString());
        Assert.True(entry.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void 损坏文件_自愈重建默认_告警触发()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, ClientPaths.LanSegmentsFileName), "{ not json !!!");

        var store = new LanSegmentsStore(_dir);
        string? recovered = null;
        store.Recovered += m => recovered = m;
        store.Load(); // 不抛：允许进程继续（段可在设备页重设，不阻断启动）
        Assert.Empty(store.EnabledCidrs());
        Assert.NotNull(recovered);
        Assert.Contains("lan-segments.json", recovered);
    }

    [Fact]
    public void 非法条目容错_合法条目保留()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, ClientPaths.LanSegmentsFileName),
            """
            [
              { "segmentId": "not-a-guid", "cidr": "192.168.1.0/24", "enabled": true },
              { "segmentId": "00000000-0000-0000-0000-000000000000", "cidr": "  ", "enabled": true },
              { "segmentId": "11111111-1111-1111-1111-111111111111", "cidr": "10.0.0.0/8", "enabled": true }
            ]
            """);

        var store = new LanSegmentsStore(_dir);
        store.Load();
        Assert.Equal(["10.0.0.0/8"], store.EnabledCidrs()); // 单条损坏丢弃，整体不作废
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}

// M3-12 单测（任务清单完成判定）：BuildSummary 两维聚合与映射/设备归属正确
// （目标侧归对端映射口径同 M2-22——引擎快照已归端，聚合只验证分组/求和/排序/总计一致性）。
using P2P.Client.Mapping;
using Xunit;

namespace P2P.Client.Tests;

public class StatsSummaryTests
{
    private static MappingStatsView Row(string name, string remoteCode, long up, long down, long relay,
        string path = "direct", string proto = "tcp", int seq = 1) => new(
        Guid.Parse($"00000000-0000-0000-0000-{seq:d12}"), name, proto, 8080, remoteCode, 80, path, up, down, relay);

    [Fact]
    public void 两维聚合_同远程码两映射并入一设备行_总计等于全量和()
    {
        var rows = new List<MappingStatsView>
        {
            Row("网站", "712152", 1000, 2000, 300, seq: 3),
            Row("文件", "712152", 40, 60, 100, seq: 2),
            Row("办公", "883456", 5, 5, 0, path: "relay", proto: "udp", seq: 1),
        };

        var s = MappingSyncService.BuildSummary(rows);

        // 映射维：名称+Id 序（与 List() 同口径）
        Assert.Equal(["办公", "文件", "网站"], s.ByMappings.Select(m => m.Name).ToArray());
        Assert.Equal("relay", s.ByMappings[0].Path); // 当前路径透传
        Assert.Equal("udp", s.ByMappings[0].Proto);

        // 设备维：远程码分组求和、字典序
        Assert.Equal(2, s.ByDevices.Length);
        Assert.Equal("712152", s.ByDevices[0].TargetRemoteCode);
        Assert.Equal(2, s.ByDevices[0].Mappings);
        Assert.Equal(1040L, s.ByDevices[0].BytesUp);
        Assert.Equal(2060L, s.ByDevices[0].BytesDown);
        Assert.Equal(400L, s.ByDevices[0].RelayBytes);
        Assert.Equal(1, s.ByDevices[1].Mappings);
        Assert.Equal(0L, s.ByDevices[1].RelayBytes);

        // 总计=映射维全量和=设备维全量和（两维互证）
        Assert.Equal(1045L, s.TotalBytesUp);
        Assert.Equal(2065L, s.TotalBytesDown);
        Assert.Equal(400L, s.TotalRelayBytes);
        Assert.Equal(s.ByDevices.Sum(d => d.BytesUp), s.TotalBytesUp);
        Assert.Equal(s.ByDevices.Sum(d => d.RelayBytes), s.TotalRelayBytes);
    }

    [Fact]
    public void 空映射_零值空表()
    {
        var s = MappingSyncService.BuildSummary([]);
        Assert.Empty(s.ByMappings);
        Assert.Empty(s.ByDevices);
        Assert.Equal(0L, s.TotalBytesUp);
        Assert.Equal(0L, s.TotalBytesDown);
        Assert.Equal(0L, s.TotalRelayBytes);
    }

    [Fact]
    public void 同名映射_Id_次序稳定()
    {
        var a = Row("服务", "712152", 1, 1, 0) with { MappingId = Guid.Parse("00000000-0000-0000-0000-000000000002") };
        var b = Row("服务", "712152", 2, 2, 0) with { MappingId = Guid.Parse("00000000-0000-0000-0000-000000000001") };
        var s = MappingSyncService.BuildSummary([a, b]);
        Assert.Equal(b.MappingId, s.ByMappings[0].MappingId); // Id 小者先
        Assert.Equal(3L, s.ByDevices[0].BytesUp);
    }
}

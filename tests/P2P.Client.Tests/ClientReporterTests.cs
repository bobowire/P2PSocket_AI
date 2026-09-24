using System.Net;
using P2P.Client.Control;
using P2P.Client.Mapping;
using Xunit;

namespace P2P.Client.Tests;

/// <summary>M2-22 上报判定纯逻辑单测（发送路径经 ScenarioIntegrationTests 全链落库断言衔接 M2-08）：
/// 0x72 结果端点映射三态（与服务端落库判据互证）、0x62 上报态过滤（direct/relay/failed 三态入流水）。</summary>
public sealed class ClientReporterTests
{
    [Fact]
    public void 端点映射_直连成功_携带对端公网端点()
    {
        var ep = ClientReporter.EndpointFor(ok: true, viaRelay: false,
            new IPEndPoint(IPAddress.Parse("203.0.113.7"), 51234));
        Assert.NotNull(ep);
        Assert.Equal("203.0.113.7", ep!.Host);
        Assert.Equal((ushort)51234, ep.Port);
    }

    [Fact]
    public void 端点映射_中继成功_不携带端点()
        // Ok 无端点 = relay 的表达约定（M2-08 服务端判据互证；Puncher 回退成功 PeerEndpoint 是中继地址，不外发）
        => Assert.Null(ClientReporter.EndpointFor(ok: true, viaRelay: true,
            new IPEndPoint(IPAddress.Parse("198.51.100.9"), 40000)));

    [Fact]
    public void 端点映射_失败_不携带端点()
        => Assert.Null(ClientReporter.EndpointFor(ok: false, viaRelay: false,
            new IPEndPoint(IPAddress.Parse("203.0.113.7"), 51234)));

    [Fact]
    public void 端点映射_直连成功但端点缺失_容错为无端点()
        => Assert.Null(ClientReporter.EndpointFor(ok: true, viaRelay: false, peerEndpoint: null));

    [Theory]
    [InlineData(MappingState.Direct, "direct")]
    [InlineData(MappingState.Relay, "relay")]
    [InlineData(MappingState.Failed, "failed")]
    public void 上报态过滤_三终态入流水(MappingState state, string expected)
        => Assert.Equal(expected, ClientReporter.StatusStringFor(state));

    [Theory]
    [InlineData(MappingState.Punching)]  // 本地过渡态：不入流水
    [InlineData(MappingState.Disabled)]  // 本地终态：不入流水
    [InlineData(MappingState.Invalid)]   // 预留 0x75 联动（M2-26+），届时随事件补
    public void 上报态过滤_其余态不上报(MappingState state)
        => Assert.Null(ClientReporter.StatusStringFor(state));
}

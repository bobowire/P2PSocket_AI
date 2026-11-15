using System.Net;
using System.Net.Sockets;
using P2P.Client.Control;
using P2P.Client.Punch;
using P2P.Client.Tunnel;
using P2P.Core.Crypto;
using P2P.Core.Tunnel;
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

    // ── M6-08：0x72 bearer 承载类型映射（NET-77 L2 包络统计维度）────────

    [Fact]
    public async Task bearer映射_真实UDP承载报0_非UDP承载报null()
    {
        // ① 真实 UdpPunchTransport 会话 → 裸 UDP=0（KCP=1 分支见 PuncherKcpBearerTests 真实 KCP 会话断言）
        var sa = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var sb = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sa.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        sb.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var ta = new UdpPunchTransport(sa, (IPEndPoint)sb.LocalEndPoint!);
        var tb = new UdpPunchTransport(sb, (IPEndPoint)sa.LocalEndPoint!);
        var (staticA, staticB) = (EcKeyPair.Generate(), EcKeyPair.Generate());
        var sessionId = Guid.NewGuid();
        var opts = new TunnelSessionOptions { KeepaliveInterval = TimeSpan.FromHours(1) };
        var connectTask = TunnelSession.ConnectAsync(sessionId, Guid.NewGuid(), staticA,
            staticB.ExportPublicKey(), ta, NullChannelHandler.Instance, opts);
        var t1 = await tb.ReceiveAsync() ?? throw new IOException("无 THello1");
        var b = await TunnelSession.AcceptAsync(Guid.NewGuid(), t1, staticB,
            staticA.ExportPublicKey(), tb, NullChannelHandler.Instance, opts);
        var a = await connectTask;
        try
        {
            Assert.True(a.RawUdpBearer);
            Assert.Equal((byte)0, ClientReporter.BearerFor(new PunchOutcome(
                true, sessionId, Guid.NewGuid(), a, null, null, null)));
        }
        finally
        {
            await a.DisposeAsync();
            await b.DisposeAsync();
        }

        // ② 非 UDP 承载形态（内存管道=TCP 承载同型）→ null（实机修正：TCP 直连不污染 UDP 包络）
        var (tma, tmb) = MemoryTransport.CreatePair();
        var connect2 = TunnelSession.ConnectAsync(Guid.NewGuid(), Guid.NewGuid(), staticA,
            staticB.ExportPublicKey(), tma, NullChannelHandler.Instance, opts);
        var t2 = await tmb.ReceiveAsync() ?? throw new IOException("无 THello1");
        var b2 = await TunnelSession.AcceptAsync(Guid.NewGuid(), t2, staticB,
            staticA.ExportPublicKey(), tmb, NullChannelHandler.Instance, opts);
        var a2 = await connect2;
        try
        {
            Assert.Null(ClientReporter.BearerFor(new PunchOutcome(
                true, Guid.NewGuid(), Guid.NewGuid(), a2, null, null, null)));
        }
        finally
        {
            await a2.DisposeAsync();
            await b2.DisposeAsync();
        }
    }

    [Fact]
    public void bearer映射_中继与失败_不携带()
    {
        Assert.Null(ClientReporter.BearerFor(new PunchOutcome(true, Guid.NewGuid(), Guid.NewGuid(),
            null, null, null, null, RelayAllowed: true))); // 无会话（中继回退终态形态）
        Assert.Null(ClientReporter.BearerFor(new PunchOutcome(false, Guid.Empty, Guid.NewGuid(),
            null, null, null, "punch_timeout")));
    }
}

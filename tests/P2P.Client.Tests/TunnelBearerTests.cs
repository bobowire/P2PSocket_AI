// M7-04 TunnelSession.Bearer 四态判别单测（设计 12 §10.1/§7.1）：relay/kcp/raw-udp/tcp——
// 各承载形态 transport 各构造一例（FromEstablishedKeys 建会话，密钥经 PtpHandshake 纯内存交换
// 产出——不打洞、不依赖 NAT 世界）。relay 形态经 RelayTransport.JoinAsync + 本地 UDP JOIN 桩
//（回 [0x02] 即地址学习完成，02 §6.1③）。
using System.Net;
using System.Net.Sockets;
using P2P.Client.Tunnel;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using P2P.Core.Tunnel;
using Xunit;

namespace P2P.Client.Tests;

public sealed class TunnelBearerTests
{
    private static readonly TimeSpan Idle = TimeSpan.FromHours(1); // 关闭心跳防干扰

    private sealed class NopHandler : ITunnelChannelHandler
    {
        public static readonly NopHandler Instance = new();
        public void OnOpen(TunnelSession s, uint ch, OpenPayload p) { }
        public void OnOpenResult(TunnelSession s, uint ch, OpenResultPayload r) { }
        public void OnData(TunnelSession s, uint ch, ReadOnlyMemory<byte> d) { }
        public void OnUdpDgram(TunnelSession s, uint ch, ReadOnlyMemory<byte> d) { }
        public void OnClose(TunnelSession s, uint ch) { }
    }

    /// <summary>纯内存握手产双侧会话密钥（THello1/THello2/TConfirm 帧不经网络直接交换）。</summary>
    private static (PtpSessionKeys A, PtpSessionKeys B) DeriveKeys()
    {
        using var staticA = EcKeyPair.Generate();
        using var staticB = EcKeyPair.Generate();
        using var initiator = PtpHandshake.StartInitiator(Guid.NewGuid(), staticA, staticB.ExportPublicKey());
        using var responder = PtpHandshake.AcceptTHello1(initiator.THello1Wire, staticB, staticA.ExportPublicKey());
        var (tConfirm, keysA) = initiator.HandleTHello2(responder.THello2Wire);
        var keysB = responder.VerifyTConfirm(tConfirm);
        return (keysA, keysB);
    }

    /// <summary>中继 JOIN 桩（UDP）：收任意 JOIN 帧回单字节 [0x02]（JoinAck，02 §6.1③）。</summary>
    private static async Task<IPEndPoint> StartRelayJoinStubAsync()
    {
        var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var ep = (IPEndPoint)udp.Client.LocalEndPoint!;
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var r = await udp.ReceiveAsync();
                    await udp.SendAsync(new byte[] { 0x02 }, r.RemoteEndPoint); // [0x02]=地址学习完成
                }
            }
            catch { /* 桩关闭 */ }
        });
        return ep;
    }

    [Fact]
    public async Task Bearer中继态_relay()
    {
        var relayEp = await StartRelayJoinStubAsync();
        var transport = await RelayTransport.JoinAsync(
            new RelayGrant(1, 0, MsgType.RelayAllocate, BitConverter.ToUInt64(RandomGenerator.Bytes(8)),
                new EndpointPair(new Endpoint(relayEp.Address.ToString(), (ushort)relayEp.Port), null)),
            RelayCarrier.Udp, joinTimeout: TimeSpan.FromSeconds(5), time: null, CancellationToken.None);
        var (keysA, _) = DeriveKeys();
        await using var session = TunnelSession.FromEstablishedKeys(Guid.NewGuid(), Guid.NewGuid(), true,
            keysA, transport, NopHandler.Instance, new TunnelSessionOptions { KeepaliveInterval = Idle });
        Assert.Equal("relay", session.Bearer); // 设计 12 §7.1：RelayTransport → relay
        Assert.True(session.ViaRelay); // 既有判别一致（M2-18）
    }

    [Fact]
    public async Task Bearer_KCP承载态_kcp()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var transport = KcpTunnelTransport.Adopt(socket, new IPEndPoint(IPAddress.Loopback, 9)); // 对端端点仅判型用
        var (keysA, _) = DeriveKeys();
        await using var session = TunnelSession.FromEstablishedKeys(Guid.NewGuid(), Guid.NewGuid(), true,
            keysA, transport, NopHandler.Instance, new TunnelSessionOptions { KeepaliveInterval = Idle });
        Assert.Equal("kcp", session.Bearer); // 设计 12 §7.1：KcpTunnelTransport → kcp
        Assert.True(session.UsesKcpBearer); // 既有判别一致（M6）
    }

    [Fact]
    public async Task Bearer裸UDP直连态_raw_udp()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var transport = new P2P.Client.Punch.UdpPunchTransport(socket,
            new IPEndPoint(IPAddress.Loopback, 9)); // 对端端点仅判型用（不收发）
        var (keysA, _) = DeriveKeys();
        await using var session = TunnelSession.FromEstablishedKeys(Guid.NewGuid(), Guid.NewGuid(), true,
            keysA, transport, NopHandler.Instance, new TunnelSessionOptions { KeepaliveInterval = Idle });
        Assert.Equal("raw-udp", session.Bearer); // 设计 12 §7.1：UdpPunchTransport → raw-udp
        Assert.True(session.RawUdpBearer); // 既有判别一致（M6-08）
    }

    [Fact]
    public async Task Bearer_TCP直连态_tcp()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var server = await listener.AcceptTcpClientAsync();
        listener.Stop();
        var transport = new TcpFrameTransport(client.Client); // 已连接句柄（生命周期归 transport）
        var (keysA, _) = DeriveKeys();
        await using var session = TunnelSession.FromEstablishedKeys(Guid.NewGuid(), Guid.NewGuid(), true,
            keysA, transport, NopHandler.Instance, new TunnelSessionOptions { KeepaliveInterval = Idle });
        Assert.Equal("tcp", session.Bearer); // 设计 12 §7.1：TcpFrameTransport → tcp
        Assert.False(session.ViaRelay);
        Assert.False(session.UsesKcpBearer);
        Assert.False(session.RawUdpBearer); // TCP 直连不参与 UDP 包络（M6-08 三态上报 null 的判别基础）
        _ = server; // 对端句柄随用例收尾释放
    }

    [Fact]
    public async Task Bearer兜底态_未知transport落tcp()
    {
        // switch 兜底 arm：MemoryTransport（测试管道，非四种生产承载）与 TcpFrameTransport 同落 tcp——
        // TCP 直连为唯一剩余形态（设计 12 §7.1 注释口径）
        var (ta, _) = MemoryTransport.CreatePair();
        var (keysA, _) = DeriveKeys();
        await using var session = TunnelSession.FromEstablishedKeys(Guid.NewGuid(), Guid.NewGuid(), true,
            keysA, ta, NopHandler.Instance, new TunnelSessionOptions { KeepaliveInterval = Idle });
        Assert.Equal("tcp", session.Bearer);
    }
}

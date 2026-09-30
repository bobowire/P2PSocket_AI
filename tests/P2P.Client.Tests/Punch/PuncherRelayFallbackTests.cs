// M2-18 Puncher 中继回退单测（02 §4.5 承载绑定/§6.1①）：Ack 后失败且回退资格真 → 0x74 分配 →
// JOIN 承载兜底；回退失败并入打洞失败原因（映射态 failed）。成功路径需 in-proc 中继服务 → 集成测覆盖。
using System.Net;
using System.Net.Sockets;
using P2P.Client.Control;
using P2P.Client.Punch;
using P2P.Client.Tunnel;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using P2P.Core.Tunnel;
using Xunit;

namespace P2P.Client.Tests;

public sealed class PuncherRelayFallbackTests
{
    private static readonly IPEndPoint Unreachable = new(IPAddress.Loopback, 1); // 无监听：握手必超时
    private static readonly byte[] PeerPubKey = EcKeyPair.Generate().ExportPublicKey(); // 65B 合法公钥

    /// <summary>Grant 替身：UDP/TCP 端点均指向无监听端口（JOIN 必失败/必超时）。</summary>
    private static RelayGrant DeadGrant(ulong sid) => new(1, 0, MsgType.RelayAllocate, sid,
        new EndpointPair(
            new P2P.Core.Protocol.Endpoint("127.0.0.1", 1),
            new P2P.Core.Protocol.Endpoint("127.0.0.1", 1)));

    private static PunchOptions FastFail => new()
    {
        PunchTimeout = TimeSpan.FromMilliseconds(400),
        BurstCount = 2,
        BurstInterval = TimeSpan.FromMilliseconds(50),
        RelayFallbackTimeout = TimeSpan.FromMilliseconds(1500), // 短预算：截断 JOIN 重发循环
    };

    private static Task<IPEndPoint> ProbeAsync(Socket socket, CancellationToken ct)
        => Task.FromResult((IPEndPoint)socket.LocalEndPoint!);

    [Fact]
    public async Task 回退资格真_分配成功但JOIN不可达_失败原因合并中继细节()
    {
        using var staticKey = EcKeyPair.Generate();
        var target = Guid.NewGuid();
        var allocated = false;
        using var puncher = new Puncher(
            (_, _, _, _, _) => Task.FromResult(new PunchRequestAck(0, 0, MsgType.PunchRequest, Guid.NewGuid(),
                new PeerInfo(Guid.NewGuid(), "?", "000000", PeerPubKey),
                new EndpointPair(new P2P.Core.Protocol.Endpoint("127.0.0.1", 1), null), 3, true)),
            (_, _, _) => Task.CompletedTask,
            ProbeAsync, staticKey,
            options: FastFail,
            relayFallbackLookup: _ => true,
            relayAllocator: (sessionId, ct) => { allocated = true; return Task.FromResult(DeadGrant(0x51)); });

        var outcome = await puncher.InitiateAsync(target, null, "udp");

        Assert.True(allocated); // 资格真 → 确实走了 0x74
        Assert.False(outcome.Ok);
        Assert.Contains("punch_timeout", outcome.FailReason); // 打洞失败原委保留
        Assert.Contains("relay_failed", outcome.FailReason); // 回退失败并入（映射态 failed 的明细源）
    }

    [Fact]
    public async Task 回退中_服务端拒绝5002_失败原因携带错误码()
    {
        using var staticKey = EcKeyPair.Generate();
        using var puncher = new Puncher(
            (_, _, _, _, _) => Task.FromResult(new PunchRequestAck(0, 0, MsgType.PunchRequest, Guid.NewGuid(),
                new PeerInfo(Guid.NewGuid(), "?", "000000", PeerPubKey),
                new EndpointPair(new P2P.Core.Protocol.Endpoint("127.0.0.1", 1), null), 3, true)),
            (_, _, _) => Task.CompletedTask,
            ProbeAsync, staticKey,
            options: FastFail,
            relayFallbackLookup: _ => true,
            relayAllocator: (_, _) => throw new ControlErrorException(5002, "relay_disabled"));

        var outcome = await puncher.InitiateAsync(Guid.NewGuid(), null, "udp");

        Assert.False(outcome.Ok);
        Assert.Contains("relay_failed", outcome.FailReason);
        Assert.Contains("relay_disabled", outcome.FailReason); // 5002 细节可见（全局关场景）
    }

    [Fact]
    public async Task 分配缝未装_资格真也不尝试回退_纯打洞失败()
    {
        using var staticKey = EcKeyPair.Generate();
        using var puncher = new Puncher(
            (_, _, _, _, _) => Task.FromResult(new PunchRequestAck(0, 0, MsgType.PunchRequest, Guid.NewGuid(),
                new PeerInfo(Guid.NewGuid(), "?", "000000", PeerPubKey),
                new EndpointPair(new P2P.Core.Protocol.Endpoint("127.0.0.1", 1), null), 3, true)),
            (_, _, _) => Task.CompletedTask,
            ProbeAsync, staticKey,
            options: FastFail,
            relayFallbackLookup: _ => true); // relayAllocator 缺省

        var outcome = await puncher.InitiateAsync(Guid.NewGuid(), null, "udp");

        Assert.False(outcome.Ok);
        Assert.Equal("punch_timeout", outcome.FailReason); // 无 relay_failed 并入
        Assert.True(outcome.RelayAllowed); // 资格标记保留（M2-23 语义不变）
    }

    // ── M2-37 回退复用（05 §4：回切周期重打失败 → 活中继会话在位不再 0x74 整体替换）──

    /// <summary>内存管道对上双侧真实 PTP 握手（与 TunnelSessionTests.EstablishAsync 同构）：
    /// 打洞器复用缝只关心"设备对有一个存活会话"，承载是否 RelayTransport 由宿主接线过滤。</summary>
    private static async Task<TunnelSession> EstablishSessionAsync(EcKeyPair staticKey)
    {
        var peerKey = EcKeyPair.Generate();
        var (ta, tb) = MemoryTransport.CreatePair();
        var connect = TunnelSession.ConnectAsync(Guid.NewGuid(), Guid.NewGuid(), staticKey,
            peerKey.ExportPublicKey(), ta, NullChannelHandler.Instance,
            new TunnelSessionOptions { KeepaliveInterval = TimeSpan.FromHours(1) });
        var t1 = await tb.ReceiveAsync() ?? throw new IOException("无 THello1");
        var accept = TunnelSession.AcceptAsync(Guid.NewGuid(), t1, peerKey,
            staticKey.ExportPublicKey(), tb, NullChannelHandler.Instance,
            new TunnelSessionOptions { KeepaliveInterval = TimeSpan.FromHours(1) });
        var session = await connect;
        await accept;
        return session;
    }

    [Fact]
    public async Task 活中继会话在位_回切重打失败_复用不新分配()
    {
        using var staticKey = EcKeyPair.Generate();
        var target = Guid.NewGuid();
        await using var existing = await EstablishSessionAsync(staticKey);
        var allocated = false;
        using var puncher = new Puncher(
            (_, _, _, _, _) => Task.FromResult(new PunchRequestAck(0, 0, MsgType.PunchRequest, Guid.NewGuid(),
                new PeerInfo(Guid.NewGuid(), "?", "000000", PeerPubKey),
                new EndpointPair(new P2P.Core.Protocol.Endpoint("127.0.0.1", 1), null), 3, true)),
            (_, _, _) => Task.CompletedTask,
            ProbeAsync, staticKey,
            options: FastFail,
            relayFallbackLookup: _ => true,
            liveRelaySessionLookup: peerId => peerId == target ? existing : null,
            relayAllocator: (_, _) => { allocated = true; return Task.FromResult(DeadGrant(0)); });

        var outcome = await puncher.InitiateAsync(target, null, "udp");

        Assert.False(allocated); // 活会话在位：不 0x74（对端亦不被动重建，M2-37）
        Assert.True(outcome.Ok);
        Assert.Same(existing, outcome.Session); // 复用既有会话（宿主 Attach 引用相等=无替换无排水）
        Assert.Null(outcome.PeerEndpoint); // 复用不带新端点（映射明细 tunnel_reused）
        Assert.False(existing.IsClosed);
    }

    [Fact]
    public async Task 复用缝命中但会话已亡_回全新分配()
    {
        using var staticKey = EcKeyPair.Generate();
        var target = Guid.NewGuid();
        var dead = await EstablishSessionAsync(staticKey);
        dead.Close("test"); // 已亡：不复用（TunnelHost 断链摘表后查找落空的同构路径）
        var allocated = false;
        using var puncher = new Puncher(
            (_, _, _, _, _) => Task.FromResult(new PunchRequestAck(0, 0, MsgType.PunchRequest, Guid.NewGuid(),
                new PeerInfo(Guid.NewGuid(), "?", "000000", PeerPubKey),
                new EndpointPair(new P2P.Core.Protocol.Endpoint("127.0.0.1", 1), null), 3, true)),
            (_, _, _) => Task.CompletedTask,
            ProbeAsync, staticKey,
            options: FastFail,
            relayFallbackLookup: _ => true,
            liveRelaySessionLookup: _ => dead,
            relayAllocator: (_, _) => { allocated = true; return Task.FromResult(DeadGrant(0x51)); });

        var outcome = await puncher.InitiateAsync(target, null, "udp");

        Assert.True(allocated); // 已亡不挡路：全新 0x74（JOIN 死端点失败并入原因，映射态 failed）
        Assert.False(outcome.Ok);
        Assert.Contains("relay_failed", outcome.FailReason);
        await dead.DisposeAsync();
    }
}

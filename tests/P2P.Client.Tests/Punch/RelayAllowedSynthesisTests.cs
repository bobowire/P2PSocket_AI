// M2-23 RelayAllowed 合成单测（05 §3.1：Puncher 出队执行时 本地设备级配置 AND 服务端 Ack.relayAllowed；
// 消费方 M2-18）。对端端点恒不可达（127.0.0.1:1）迫使 Ack 后握手失败——恰为中继回退的触发条件
// （02 §4.5：打洞失败 → 按设备级配置决定）。Ack 前失败（服务端拒绝）无会话在册，恒不可回退。
using System.Net;
using System.Net.Sockets;
using P2P.Client.Control;
using P2P.Client.Punch;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using Xunit;

namespace P2P.Client.Tests;

public sealed class RelayAllowedSynthesisTests
{
    private static readonly IPEndPoint Unreachable = new(IPAddress.Loopback, 1); // 无监听：握手必超时
    private static readonly byte[] PeerPubKey = EcKeyPair.Generate().ExportPublicKey(); // 65B 合法公钥

    private static PunchRequestAck Ack(bool relayAllowed)
        => new(0, 0, MsgType.PunchRequest, Guid.NewGuid(),
            new PeerInfo(Guid.NewGuid(), "?", "000000", PeerPubKey),
            new EndpointPair(new P2P.Core.Protocol.Endpoint("127.0.0.1", 1), null), 3, relayAllowed);

    private static PunchOptions FastFail => new()
    {
        PunchTimeout = TimeSpan.FromMilliseconds(400),
        BurstCount = 2,
        BurstInterval = TimeSpan.FromMilliseconds(50),
    };

    private static Task<IPEndPoint> ProbeAsync(Socket socket, CancellationToken ct)
        => Task.FromResult((IPEndPoint)socket.LocalEndPoint!);

    [Theory]
    [InlineData(true, true, true)]   // 本地开 × 服务端开 → 可回退
    [InlineData(true, false, false)] // 服务端全局关 → 不可
    [InlineData(false, true, false)] // 本地设备级关（默认）→ 不可
    public async Task UDP_Ack后打洞失败_回退资格按AND合成(bool local, bool server, bool expected)
    {
        using var staticKey = EcKeyPair.Generate();
        var target = Guid.NewGuid();
        using var puncher = new Puncher(
            (_, _, _, _, _) => Task.FromResult(Ack(server)),
            (_, _, _) => Task.CompletedTask,
            ProbeAsync, staticKey,
            options: FastFail,
            relayFallbackLookup: id => id == target && local);

        var outcome = await puncher.InitiateAsync(target, null, "udp");

        Assert.False(outcome.Ok);
        Assert.Equal("punch_timeout", outcome.FailReason); // 对端不可达 → 握手超时
        Assert.Equal(expected, outcome.RelayAllowed);
    }

    [Fact]
    public async Task TCP_承载同构_Ack后失败携带回退资格()
    {
        using var staticKey = EcKeyPair.Generate();
        var target = Guid.NewGuid();
        using var puncher = new Puncher(
            (_, _, _, _, _) => Task.FromResult(new PunchRequestAck(0, 0, MsgType.PunchRequest, Guid.NewGuid(),
                new PeerInfo(Guid.NewGuid(), "?", "000000", PeerPubKey),
                new EndpointPair(null, new P2P.Core.Protocol.Endpoint("127.0.0.1", 1)), 3, true)),
            (_, _, _) => Task.CompletedTask,
            ProbeAsync, staticKey,
            options: FastFail,
            tcpProbe: (socket, ct) => Task.FromResult(Unreachable),
            relayFallbackLookup: _ => true);

        var outcome = await puncher.InitiateAsync(target, null, "tcp");

        Assert.False(outcome.Ok);
        Assert.True(outcome.RelayAllowed); // 连接被拒/超时均为 Ack 后失败
    }

    [Fact]
    public async Task Ack前失败_无会话在册_恒不可回退()
    {
        using var staticKey = EcKeyPair.Generate();
        var target = Guid.NewGuid();
        using var puncher = new Puncher(
            (_, _, _, _, _) => Task.FromException<PunchRequestAck>(
                new ControlErrorException(4005, "target_offline")),
            (_, _, _) => Task.CompletedTask,
            ProbeAsync, staticKey,
            options: FastFail,
            relayFallbackLookup: _ => true); // 即便本地开

        var outcome = await puncher.InitiateAsync(target, null, "udp");

        Assert.False(outcome.Ok);
        Assert.StartsWith("server_4005", outcome.FailReason);
        Assert.False(outcome.RelayAllowed);
    }

    [Fact]
    public async Task 未装配本地配置缝_等同默认关闭()
    {
        using var staticKey = EcKeyPair.Generate();
        using var puncher = new Puncher(
            (_, _, _, _, _) => Task.FromResult(Ack(true)),
            (_, _, _) => Task.CompletedTask,
            ProbeAsync, staticKey,
            options: FastFail); // relayFallbackLookup 缺省

        var outcome = await puncher.InitiateAsync(Guid.NewGuid(), null, "udp");

        Assert.False(outcome.Ok);
        Assert.False(outcome.RelayAllowed); // 与 peers.json 无条目同口径（PRD 06 §2 默认关）
    }
}

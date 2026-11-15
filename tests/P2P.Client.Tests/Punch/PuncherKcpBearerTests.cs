// M6-04/M6-05 Puncher 承载协商与 KCP 分支单测（设计 11 §7/§8）：
// - 双端 caps 开 → 判定一致 true → KCP 承载建立（UsesKcpBearer 双真）+ PING 走 conv 0 可靠往返；
// - 单端关 / kill-switch 回 0 / 旧服务端剥字段 → 双端同判 false → 裸 UDP（现状路径，双假）。
// 布线=两段式全链路（真实 UDP 回环 + 假控制面缝；"服务端"=测试内协程，caps 透传可开关——混布模拟）。
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using P2P.Client.Punch;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using Xunit;

namespace P2P.Client.Tests;

public sealed class PuncherKcpBearerTests
{
    /// <summary>控制面假缝（PunchTests.FakeSeams 同款形态 + caps 上送记录）。</summary>
    private sealed class CapsSeams
    {
        public TaskCompletionSource<EndpointPair> Probed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<EndpointPair> Reported { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<uint?> ReportedCaps { get; } = []; // 0x76 上送位图（wire 观测）

        public Task<IPEndPoint> ProbeAsync(Socket socket, CancellationToken ct)
        {
            // 模拟 STUN 映射：socket 绑 Any → LocalEndPoint=0.0.0.0 不可作目标，回环化（原版同款）
            var mapped = new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)socket.LocalEndPoint!).Port);
            Probed.TrySetResult(new EndpointPair(new Endpoint(mapped.Address.ToString(), (ushort)mapped.Port), null));
            return Task.FromResult(mapped);
        }

        public Task ReportAsync(Guid sessionId, EndpointPair endpoints, Endpoint[]? localCandidates,
            uint? bearerCaps, CancellationToken ct)
        {
            ReportedCaps.Enqueue(bearerCaps);
            Reported.TrySetResult(endpoints);
            return Task.CompletedTask;
        }
    }

    /// <summary>双端全链路（A 0x70 发起 / B 0x71 应答）；"服务端"行为=A 的 0x70 假缝内等 B 的
    /// 0x76 后回延后 Ack（PunchTests.两段式全链路 同款编排）。transferCaps=false 模拟旧服务端
    /// 剥字段（Ack/Invite 均不透传 caps，设计 11 §8.3）。</summary>
    private static async Task<(PunchOutcome OutA, PunchOutcome OutB, uint? AWireCaps, uint? BWireCaps)>
        PunchBothSidesAsync(Func<uint>? aCaps, Func<uint>? bCaps, bool transferCaps = true)
    {
        using var staticA = EcKeyPair.Generate();
        using var staticB = EcKeyPair.Generate();
        var deviceA = Guid.NewGuid();
        var deviceB = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var seamsA = new CapsSeams();
        var seamsB = new CapsSeams();
        var aSentCaps = new TaskCompletionSource<uint?>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var puncherA = new Puncher(
            // A 的 0x70 假缝（模拟服务端）：记录 A caps → 等 B 的 0x76 → 延后 Ack（携 B caps 或剥）
            (target, trigger, proto, endpoints, candidates, caps, ct) =>
            {
                aSentCaps.TrySetResult(caps);
                return Task.Run(async () =>
                {
                    var bReported = await seamsB.Reported.Task;
                    return new PunchRequestAck(0, 0, MsgType.PunchRequest, sessionId,
                        new PeerInfo(deviceB, "B", "b00000", staticB.ExportPublicKey()),
                        new EndpointPair(bReported.Udp, null), 3, false,
                        PeerBearerCaps: transferCaps ? seamsB.ReportedCaps.LastOrDefault() : null);
                });
            },
            seamsA.ReportAsync, seamsA.ProbeAsync, staticA, localBearerCaps: aCaps);
        using var puncherB = new Puncher(
            (target, trigger, proto, endpoints, candidates, caps, ct) => Task.FromResult(
                new PunchRequestAck(0, 0, MsgType.PunchRequest, Guid.NewGuid(),
                    new PeerInfo(target, "?", "000000", []),
                    new EndpointPair(new Endpoint("127.0.0.1", 1), null), 3, false)), // B 侧不走 0x70
            seamsB.ReportAsync, seamsB.ProbeAsync, staticB, localBearerCaps: bCaps);

        var outcomeATask = puncherA.InitiateAsync(deviceB, null, "udp");
        var aEp = await seamsA.Probed.Task;
        // 服务端把 A 的端点与 caps（或剥字段）随 0x71 下发 B
        var invite = new PunchInvite(0, 0, MsgType.PunchInvite, sessionId,
            new PeerInfo(deviceA, "A", "a00000", staticA.ExportPublicKey()),
            new EndpointPair(aEp.Udp, null), 3, false,
            PeerBearerCaps: transferCaps ? await aSentCaps.Task : null);
        var outcomeB = await puncherB.RespondAsync(invite);
        var outcomeA = await outcomeATask;
        return (outcomeA, outcomeB, await aSentCaps.Task, seamsB.ReportedCaps.LastOrDefault());
    }

    private static Func<uint> CapsOn => () => BearerCaps.Kcp;

    [Fact]
    public async Task 双端caps开_KCP承载建立_判定一致()
    {
        var (outA, outB, aWire, bWire) = await PunchBothSidesAsync(CapsOn, CapsOn);
        Assert.True(outA.Ok, outA.FailReason);
        Assert.True(outB.Ok, outB.FailReason);
        Assert.Equal(BearerCaps.Kcp, aWire); // 0x70 上送位实证
        Assert.Equal(BearerCaps.Kcp, bWire); // 0x76 上送位实证
        Assert.True(outA.Session!.UsesKcpBearer, "A 侧判定 true → KCP 承载");
        Assert.True(outB.Session!.UsesKcpBearer, "B 侧判定 true → KCP 承载");
        // 0x72 bearer 上报映射（M6-08）：KCP 会话 → 1（ClientReporterTests 补裸=0 与中继/失败=null）
        Assert.Equal((byte)1, P2P.Client.Control.ClientReporter.BearerFor(
            new PunchOutcome(true, outA.SessionId, outA.PeerDeviceId, outA.Session, null, null, null)));
        var rtt = await outA.Session.PingAsync(); // conv 0 可靠往返（首帧走 KCP 实证）
        Assert.True(rtt >= TimeSpan.Zero);
        await outA.Session.DisposeAsync();
        await outB.Session!.DisposeAsync();
    }

    [Theory]
    [InlineData(true, false)]   // 新 A + 旧 B（B 不上送位）
    [InlineData(false, true)]   // 旧 A + 新 B
    [InlineData(false, false)]  // 双旧
    public async Task 单端或双端关_一致降级裸UDP(bool aCaps, bool bCaps)
    {
        var (outA, outB, aWire, bWire) = await PunchBothSidesAsync(
            aCaps ? CapsOn : null, bCaps ? CapsOn : null);
        Assert.True(outA.Ok, outA.FailReason);
        Assert.True(outB.Ok, outB.FailReason);
        if (!aCaps) Assert.Null(aWire); // 缝未装配=不上送位
        if (!bCaps) Assert.Null(bWire);
        Assert.False(outA.Session!.UsesKcpBearer, "判定 false → 裸承载（现状路径）");
        Assert.False(outB.Session!.UsesKcpBearer);
        var rtt = await outA.Session.PingAsync(); // 裸承载会话同样可 ping（现状行为不回归）
        Assert.True(rtt >= TimeSpan.Zero);
        await outA.Session.DisposeAsync();
        await outB.Session!.DisposeAsync();
    }

    [Fact]
    public async Task kill_switch本地回0_wire不上送位_降级裸承载()
    {
        // settings.KcpBearer=false 形态（ClientRuntime 缝合成 () => 0u）：wire null + 判定 false
        var (outA, outB, aWire, _) = await PunchBothSidesAsync(() => 0u, CapsOn);
        Assert.Null(aWire); // 0 → 不上送（null 字段=旧端形态）
        Assert.True(outA.Ok, outA.FailReason);
        Assert.True(outB.Ok, outB.FailReason);
        Assert.False(outA.Session!.UsesKcpBearer);
        Assert.False(outB.Session!.UsesKcpBearer);
        await outA.Session.DisposeAsync();
        await outB.Session!.DisposeAsync();
    }

    [Fact]
    public async Task 旧服务端剥字段_双端同判false_裸承载()
    {
        // 双端 caps 开 + 服务端不透传（旧服务端重序列化剥未知字段形态，设计 11 §8.3 第三行）
        var (outA, outB, aWire, bWire) = await PunchBothSidesAsync(CapsOn, CapsOn, transferCaps: false);
        Assert.Equal(BearerCaps.Kcp, aWire); // 上送照常
        Assert.Equal(BearerCaps.Kcp, bWire);
        Assert.True(outA.Ok, outA.FailReason);
        Assert.True(outB.Ok, outB.FailReason);
        Assert.False(outA.Session!.UsesKcpBearer, "旧服务端 → 双端同判 false 无脑裂");
        Assert.False(outB.Session!.UsesKcpBearer);
        await outA.Session.DisposeAsync();
        await outB.Session!.DisposeAsync();
    }
}

// M6-04 caps 协商单测（设计 11 §8，决议③）：判定公式两端同式（四组合一致性）+ 四消息
// MessagePack 尾部加法（新新/新旧双向 wire 容忍——旧端形态用少一字段 shim 记录模拟）。
using MessagePack;
using P2P.Core.Protocol;
using Xunit;

namespace P2P.Core.Tests;

public class BearerCapsTests
{
    // ── 判定公式（§8.2：两端同式、仅本地输入、无第三态 → 判定必然一致，无脑裂窗）──

    [Theory]
    [InlineData(0u, 0u, false)]              // 双旧：裸 UDP
    [InlineData(BearerCaps.Kcp, 0u, false)]  // 新旧：裸 UDP
    [InlineData(0u, BearerCaps.Kcp, false)]  // 旧新：裸 UDP
    [InlineData(BearerCaps.Kcp, BearerCaps.Kcp, true)] // 新新：KCP
    public void 判定公式_四组合(uint my, uint peer, bool expected)
        => Assert.Equal(expected, BearerCaps.KcpEnabled(my, peer));

    [Fact]
    public void 两端同式判定_A视角与B视角一致()
    {
        // A：my=随 0x70 上送值，peer=Ack.PeerBearerCaps；B：my=随 0x76 上送值，peer=Invite.PeerBearerCaps
        // 透传保真下两视角输入互为镜像 → 判定恒一致（无脑裂）
        foreach (var aCaps in new uint[] { 0, BearerCaps.Kcp })
        foreach (var bCaps in new uint[] { 0, BearerCaps.Kcp })
            Assert.True(
                BearerCaps.KcpEnabled(aCaps, bCaps) == BearerCaps.KcpEnabled(bCaps, aCaps),
                $"a={aCaps} b={bCaps} 两端判定须一致");
    }

    [Fact]
    public void 位图扩展域_与Kcp位无干扰()
    {
        // u32 余量（bit1+）后续扩展：未知位不改变 KCP 判定（位与语义）
        Assert.True(BearerCaps.KcpEnabled(BearerCaps.Kcp | 0xF0, BearerCaps.Kcp | 0x10));
        Assert.False(BearerCaps.KcpEnabled(0xF0, 0x10)); // 仅有未知位
    }

    // ── 四消息 MessagePack 尾部加法（02 §7 加法演进；缺省 null=0=不支持）────────

    [Fact]
    public void 四消息_caps字段_往返保真_缺省null()
    {
        var req = new PunchRequest(1, 2, MsgType.PunchRequest, Guid.NewGuid(), null, "udp", null, null, null,
            BearerCaps: BearerCaps.Kcp);
        Assert.Equal(BearerCaps.Kcp, MessagePackSerializer.Deserialize<PunchRequest>(
            MessagePackSerializer.Serialize(req)).BearerCaps);

        var ack = new PunchRequestAck(1, 2, MsgType.PunchRequest, Guid.NewGuid(),
            new PeerInfo(Guid.NewGuid(), "?", "000000", []), new EndpointPair(null, null), 3, false, null,
            PeerBearerCaps: BearerCaps.Kcp);
        Assert.Equal(BearerCaps.Kcp, MessagePackSerializer.Deserialize<PunchRequestAck>(
            MessagePackSerializer.Serialize(ack)).PeerBearerCaps);

        var invite = new PunchInvite(1, 2, MsgType.PunchInvite, Guid.NewGuid(),
            new PeerInfo(Guid.NewGuid(), "?", "000000", []), new EndpointPair(null, null), 3, false, null,
            PeerBearerCaps: BearerCaps.Kcp);
        Assert.Equal(BearerCaps.Kcp, MessagePackSerializer.Deserialize<PunchInvite>(
            MessagePackSerializer.Serialize(invite)).PeerBearerCaps);

        var ep = new PunchEndpoint(1, 2, MsgType.PunchEndpoint, Guid.NewGuid(),
            new EndpointPair(null, null), null, BearerCaps: BearerCaps.Kcp);
        Assert.Equal(BearerCaps.Kcp, MessagePackSerializer.Deserialize<PunchEndpoint>(
            MessagePackSerializer.Serialize(ep)).BearerCaps);

        Assert.Null(MessagePackSerializer.Deserialize<PunchRequest>(
            MessagePackSerializer.Serialize(new PunchRequest(1, 2, MsgType.PunchRequest,
                Guid.NewGuid(), null, "udp", null, null, null))).BearerCaps);
    }

    /// <summary>旧端 0x70 形态（无 Key(9) 字段的 shim 记录）——模拟现网旧版反序列化面。</summary>
    [MessagePackObject(AllowPrivate = true)]
    internal sealed record OldPunchRequest(
        [property: Key(0)] uint Seq,
        [property: Key(1)] ulong TimestampMs,
        [property: Key(2)] byte MsgType,
        [property: Key(3)] Guid TargetDeviceId,
        [property: Key(4)] Guid? TriggerMappingId,
        [property: Key(5)] string Proto,
        [property: Key(6)] EndpointPair? RequesterEndpoints,
        [property: Key(7)] byte? PunchConcurrency,
        [property: Key(8)] Endpoint[]? LocalCandidates = null);

    [Fact]
    public void 混布wire容忍_旧端读新帧_尾字段忽略()
    {
        var fresh = new PunchRequest(1, 2, MsgType.PunchRequest, Guid.NewGuid(), null, "udp", null, null, null,
            BearerCaps: BearerCaps.Kcp);
        var old = MessagePackSerializer.Deserialize<OldPunchRequest>(MessagePackSerializer.Serialize(fresh));
        Assert.Equal(fresh.TargetDeviceId, old.TargetDeviceId); // 旧端可读新帧（尾部未知字段忽略）
    }

    [Fact]
    public void 混布wire容忍_新端读旧帧_缺省null()
    {
        var legacy = new OldPunchRequest(1, 2, MsgType.PunchRequest, Guid.NewGuid(), null, "udp", null, null);
        var modern = MessagePackSerializer.Deserialize<PunchRequest>(MessagePackSerializer.Serialize(legacy));
        Assert.Null(modern.BearerCaps); // 旧帧无 caps → null=0 → 判定 false（一致降级）
    }
}

// ── M6-08：0x72 bearer 上报字段（尾部加法，NET-77 L2 包络统计维度）────────

public class PunchResultBearerTests
{
    [Fact]
    public void PunchResult_bearer字段_往返保真_缺省null()
    {
        var direct = new PunchResult(1, 2, MsgType.PunchResult, Guid.NewGuid(), true,
            new Endpoint("1.2.3.4", 80), null, Bearer: 1);
        var roundDirect = PcpCodec.Decode<PunchResult>(PcpCodec.Encode(direct));
        Assert.Equal((byte)1, roundDirect.Bearer);

        var raw = new PunchResult(1, 2, MsgType.PunchResult, Guid.NewGuid(), true,
            new Endpoint("1.2.3.4", 80), null, Bearer: 0);
        Assert.Equal((byte)0, PcpCodec.Decode<PunchResult>(PcpCodec.Encode(raw)).Bearer);

        var legacy = new PunchResult(1, 2, MsgType.PunchResult, Guid.NewGuid(), false, null, "no_reply");
        Assert.Null(PcpCodec.Decode<PunchResult>(PcpCodec.Encode(legacy)).Bearer);
    }
}

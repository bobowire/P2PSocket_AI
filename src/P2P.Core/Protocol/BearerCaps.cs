// M6-04 承载能力位图与判定公式（设计 11 §8，决议③）：
// - 载体=0x70 PunchRequest.bearerCaps / 0x71 PunchInvite.peerBearerCaps / 0x76 PunchEndpoint.bearerCaps
//   / 0x70 Ack.peerBearerCaps 四消息 u32 可选字段（MessagePack 尾部加法惯例，缺省 null=0=不支持）；
// - 服务端纯存转零解析（localCandidates v1.63 同款）；判定两端同式、仅本地输入、无第三态
//  （开火前完成、无脑裂窗）。
namespace P2P.Core.Protocol;

/// <summary>承载能力位图：bit0=CAP_KCP（本期唯一位；u32 余量后续扩展，02 §7 加法演进）。</summary>
public static class BearerCaps
{
    /// <summary>KCP 承载（M6：UDP 直连可靠层，多 conv 按 channel 分流）。</summary>
    public const uint Kcp = 1u << 0;

    /// <summary>KCP 承载启用判定（§8.2）：两端输入对称可得、公式同式 → 判定必然一致。
    /// my=本端随 0x70/0x76 上送值；peer=对端经 0x70 Ack/0x71 透传值。</summary>
    public static bool KcpEnabled(uint myCaps, uint peerCaps) => (myCaps & peerCaps & Kcp) != 0;
}

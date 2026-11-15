// M6-03 会话层 KCP conv 控制缝（设计 11 §6，决议①）：TunnelSession 经此驱动承载层 conv 生命周期。
// transport 未实现（TCP/中继/裸 UDP 承载）即 no-op——软转换判空，零回归（设计 11 §6 同款形态）。
namespace P2P.Client.Tunnel;

/// <summary>KCP 承载 conv 生命周期控制（实现者：<see cref="KcpTunnelTransport"/>）。</summary>
public interface IKcpConvControl
{
    /// <summary>登记 channel conv（幂等；发送 OPEN 前/接收 OPEN 后调用——conv 凭据=经 AEAD 的 OPEN）。</summary>
    void EnsureChannel(uint channelId);

    /// <summary>销毁 channel conv（幂等；排空后销毁——在途段重传至确认或承载消亡）。</summary>
    void RemoveChannel(uint channelId);
}

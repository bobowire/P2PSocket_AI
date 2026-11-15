// M6-01 KCP 注入缝与参数（设计 11 §2.3 决议④构造性质 ①②、§5.4 参数表默认值——M6-07 校准回填）。
namespace P2P.Core.Transport.Kcp;

/// <summary>
/// KCP 输出缝：flush 产出的完整 KCP 包（一段或多段 MTU 内聚合）经此送出。算法层不认
/// Socket 类型（QUIC 否决案死穴同源，设计 11 §2.1/§2.3）；实现须同步返回（自行缓冲/入队）。
/// </summary>
public interface IKcpDatagramSink
{
    void Send(ReadOnlySpan<byte> datagram);
}

/// <summary>
/// KCP 输入缝（Source 侧）：壳层收包泵完成魔数/conv 解复用后，经 <see cref="KcpPeer.Input"/>
/// 注入算法层——外部 socket 收发的唯一入口。
/// </summary>
public interface IKcpDatagramSource
{
    /// <summary>注入一个 KCP 包（可含多段）。返回 false=畸形/conv 不符（调用方丢弃+计数）。</summary>
    bool Input(ReadOnlySpan<byte> datagram);
}

/// <summary>
/// KCP 端点参数（设计 11 §5.4 默认表；M6-07 基准调参后回填终值）。
/// Nc=true（关 KCP 拥塞窗）为默认：在途量由端到端 WINDOW 信用（64KiB/channel）界定，
/// KCP CC 冗余且弱网伤吞吐；nc=0 进基准对照组。
/// </summary>
public sealed record KcpPeerOptions
{
    /// <summary>更新节奏（参考 ikcp_interval；夹取 [10,5000]ms）。</summary>
    public int IntervalMs { get; init; } = 10;

    /// <summary>nodelay=1 快速模式（RTO ×1.5 退避 + rx_minrto=30 + 无 rtomin 提前量）。</summary>
    public bool NoDelay { get; init; } = true;

    /// <summary>快重传触发阈值（后续段 ACK 越过本段次数 ≥ 此值即重传；0=关）。</summary>
    public int Resend { get; init; } = 2;

    /// <summary>关 KCP 拥塞窗（nc=1，设计 11 §5.4 默认）。</summary>
    public bool NoCongestionWindow { get; init; } = true;

    /// <summary>发送窗（段/conv；64≈单 channel 信用 48 帧+余量）。</summary>
    public int SndWnd { get; init; } = 64;

    /// <summary>接收窗（段/conv；通告对端约束在途，兼作应用背压——接收侧不读则窗收敛）。</summary>
    public int RcvWnd { get; init; } = 64;

    /// <summary>KCP 包上限（段头 24B 含内）→ MSS=Mtu-24。注：线上数据报另有 1B 魔数前缀
    /// （壳层解复用，算法层不感知）。</summary>
    public int Mtu { get; init; } = 1400;

    /// <summary>毫秒时钟注入（确定性单测）；null=Environment.TickCount64（uint32 回绕安全）。</summary>
    public Func<long>? ClockMs { get; init; }
}

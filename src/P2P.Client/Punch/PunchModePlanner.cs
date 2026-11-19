// M7-02 设备级打洞协议规划器（设计 12 §4，决策①②）：打洞协议决策权从"入队携带映射 proto"
// 上收为"出队执行时按目标设备 punchMode 解析"——首次/新触发=优先模式并重置交替计数；
// 周期重试=以优先模式为起点的 TCP/KCP 交替（tcp→kcp→tcp… / kcp→tcp→kcp…，计数取模实现）。
// 口径分层（设计 12 §2.1）：本组件入参 punchMode 为配置层口径（tcp/kcp，来源 PeersStore.GetPunchMode），
// 返回值统一为执行层口径（tcp/udp——kcp 映射为 udp；0x70 proto/punch_stats/PunchOutcome.Proto 均落执行口径，
// 不引入 "kcp" 值污染既有 tcp/udp 二值统计）。
// 计数为内存态（设计 12 §4.4）：重启归零=重试序列重新自优先模式开始，与"重启后首打即新序列"自洽，不持久化。
namespace P2P.Client.Punch;

/// <summary>设备级打洞协议规划器（设计 12 §4）：首次/新触发=优先模式并重置序列；周期重试=以优先模式
/// 为起点的交替。返回值为执行层口径（tcp/udp，§2.1 口径分层——punchMode="kcp" 出队映射为 "udp"）。</summary>
public sealed class PunchModePlanner
{
    private readonly Func<Guid, string> _punchModeLookup;
    private readonly Dictionary<Guid, int> _retryCount = []; // 设备对 → 已消耗的重试序号
    private readonly object _gate = new();                   // 调度循环与重试循环可能并发访问

    /// <param name="punchModeLookup">目标设备 punchMode 查找缝（宿主接 PeersStore.GetPunchMode——
    /// 发送时点取现值，设备页修改对后续打洞会话即时生效，与 KcpBearer kill-switch 同口径）。</param>
    public PunchModePlanner(Func<Guid, string> punchModeLookup)
        => _punchModeLookup = punchModeLookup;

    /// <summary>新序列开始（映射启用/手动重试/断链/重连沿，设计 12 §5.4 触发点分类）：
    /// 返回优先模式（执行口径），重置该设备对交替计数——"人工干预=从头再试"。</summary>
    public string Begin(Guid peerDeviceId)
    {
        lock (_gate) _retryCount.Remove(peerDeviceId);
        return ToExecution(_punchModeLookup(peerDeviceId));
    }

    /// <summary>周期重试取下一个协议（执行口径）：n=计数++，n%2==0 → 优先模式，否则另一模式；
    /// kcp→udp 映射在此完成（设计 12 §2.1/§4）。出队时点调用——每次真实执行的打洞会话才消耗
    /// 一个序列号（入队被同对合并丢弃不消耗，防序列跳号，设计 12 §4.2）。</summary>
    public string NextRetry(Guid peerDeviceId)
    {
        int n;
        lock (_gate)
        {
            _retryCount.TryGetValue(peerDeviceId, out n);
            _retryCount[peerDeviceId] = n + 1;
        }
        var preferred = _punchModeLookup(peerDeviceId);
        var mode = n % 2 == 0 ? preferred : Other(preferred);
        return ToExecution(mode);
    }

    /// <summary>互补模式（设计 12 §2.2 对称规则）：tcp↔kcp；lookup 返回非规范值时按 tcp 处理
    ///（PeersStore 已规范化，防御性兜底）。</summary>
    private static string Other(string mode) => mode == "kcp" ? "tcp" : "kcp";

    /// <summary>配置层 → 执行层单向映射（设计 12 §2.1）：kcp→udp、tcp→tcp。</summary>
    private static string ToExecution(string mode) => mode == "kcp" ? "udp" : "tcp";
}

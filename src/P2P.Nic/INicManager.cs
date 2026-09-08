// M1-20 虚拟网卡管理接口（05 §1.1）：Windows=Wintun（05 §1.2）、Linux=TUN（05 §1.3，M1-21）。
// 客户端注册成功取得虚拟 IP 后 EnsureAsync，退出/解绑时 RemoveAsync（FR-C-201）。
using System.Net;

namespace P2P.Nic;

/// <summary>虚拟网卡管理器。实现须可幂等调用：重复 Ensure 同一 IP 与重复 Remove 均安全。</summary>
public interface INicManager
{
    /// <summary>运行期降级告警（05 §1.1：网卡被删/IP 异常等）。
    /// M1-20 无周期自愈扫描（FR-C-202 按里程碑映射属 M2），接口预留。</summary>
    event Action<string>? Degraded;

    /// <summary>创建/复用虚拟网卡并应用虚拟 IP（幂等；FR-C-201「启动时创建/复用」）。</summary>
    Task<NicHandle> EnsureAsync(IPAddress virtualIp, CancellationToken ct = default);

    /// <summary>移除本管理器建立的虚拟网卡并清理地址（幂等）。</summary>
    Task RemoveAsync(CancellationToken ct = default);
}

/// <summary>网卡就绪句柄：适配器名 + 已应用的虚拟 IP。</summary>
public sealed record NicHandle(string AdapterName, IPAddress VirtualIp);

/// <summary>网卡操作失败（原因可读，供本地 Web/日志展示）。</summary>
public sealed class NicException(string message) : Exception(message);

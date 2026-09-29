// M1-20 虚拟网卡管理接口（05 §1.1）：Windows=Wintun（05 §1.2）、Linux=TUN（05 §1.3，M1-21）。
// 客户端注册成功取得虚拟 IP 后 EnsureAsync，退出/解绑时 RemoveAsync（FR-C-201）。
// M2-24 增 CheckHealth 只读探测原语（FR-C-202：自愈循环探测适配器存在性与 IP 一致性）。
using System.Net;

namespace P2P.Nic;

/// <summary>虚拟网卡管理器。实现须可幂等调用：重复 Ensure 同一 IP 与重复 Remove 均安全。</summary>
public interface INicManager
{
    /// <summary>运行期降级告警（05 §1.1：网卡被删/IP 异常等）。</summary>
    event Action<string>? Degraded;

    /// <summary>创建/复用虚拟网卡并应用虚拟 IP（幂等；FR-C-201「启动时创建/复用」）。</summary>
    Task<NicHandle> EnsureAsync(IPAddress virtualIp, CancellationToken ct = default);

    /// <summary>移除本管理器建立的虚拟网卡并清理地址（幂等）。</summary>
    Task RemoveAsync(CancellationToken ct = default);

    /// <summary>只读健康探测（FR-C-202，M2-24）：适配器存在性 + 期望 IP 绑定一致性。
    /// 不修改任何状态；探测手段不可用时按健康返回（不因探测失败触发重建）。</summary>
    NicHealth CheckHealth(IPAddress expectedIp);

    /// <summary>移除本产品名下的遗留虚拟网卡（非本管理器实例建立；FR-C-203 卸载清理，M2-25）。
    /// 返回是否实际移除（无遗留=false）。幂等；与 RemoveAsync 独立可各自调用。</summary>
    Task<bool> RemoveLeftoverAsync(CancellationToken ct = default);
}

/// <summary>网卡就绪句柄：适配器名 + 已应用的虚拟 IP。</summary>
public sealed record NicHandle(string AdapterName, IPAddress VirtualIp);

/// <summary>网卡健康状态（FR-C-202 自愈探测结果）。</summary>
public enum NicHealthState
{
    /// <summary>适配器在位且绑定 IP 与期望一致。</summary>
    Healthy,
    /// <summary>适配器不存在（被外部删除）。</summary>
    AdapterMissing,
    /// <summary>适配器在位但 IP 绑定缺失或与期望不符（被外部改动）。</summary>
    IpMismatch,
}

/// <summary>健康探测快照：状态 + 当前实际绑定 IP（Healthy/IpMismatch 时有值，缺失为 null）。</summary>
public sealed record NicHealth(NicHealthState State, IPAddress? BoundIp);

/// <summary>网卡操作失败（原因可读，供本地 Web/日志展示）。</summary>
public sealed class NicException(string message) : Exception(message);

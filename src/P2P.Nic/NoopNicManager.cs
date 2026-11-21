// M8-01 无卡模式空操作网卡管理器（设计 13 §4.2/TD-32）：Android 端口映射模式的产品级实现。
// 端口映射模式下映射监听退绑 127.0.0.1（LoopbackMappingMode），无需任何虚拟网卡/VPN 权限——
// EnsureAsync 恒成功返回 loopback 句柄（注册路径 nic.EnsureAsync 无 try/catch，依赖恒成功语义，
// 设计 13 §1.3 障碍 5）、CheckHealth 恒 Healthy（NicHealthMonitor 30s 探测空转无害，05 §1.2）、
// Remove/RemoveLeftover no-op（无资源可清，FR-C-201/203 幂等语义的空集形态）。
using System.Net;

namespace P2P.Nic;

/// <summary>空操作网卡管理器（无卡模式）：不创建任何网卡——恒成功/恒健康/零清理副作用。</summary>
public sealed class NoopNicManager : INicManager
{
    /// <summary>句柄适配器名（展示与 SubnetConflictDetector.OnNicApplied 记名消费；loopback
    /// 适配器在枚举侧本就被排除，不构成冲突检测的排除盲区，设计 13 §2.5）。</summary>
    public const string LoopbackAdapterName = "loopback";

#pragma warning disable CS0067 // 接口事件保留位（无卡模式无降级场景）
    public event Action<string>? Degraded;
#pragma warning restore CS0067

    /// <summary>恒成功：回显请求 IP 的 loopback 句柄（幂等——多次 Ensure 同值安全）。</summary>
    public Task<NicHandle> EnsureAsync(IPAddress virtualIp, CancellationToken ct = default)
        => Task.FromResult(new NicHandle(LoopbackAdapterName, virtualIp));

    /// <summary>无本管理器建立的网卡可移除。</summary>
    public Task RemoveAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>恒健康：无网卡可失（「探测不可用按健康返回」接口契约的无条件形态，FR-C-202）。</summary>
    public NicHealth CheckHealth(IPAddress expectedIp) => new(NicHealthState.Healthy, expectedIp);

    /// <summary>无遗留可清理（false=未实际移除，FR-C-203）。</summary>
    public Task<bool> RemoveLeftoverAsync(CancellationToken ct = default) => Task.FromResult(false);
}

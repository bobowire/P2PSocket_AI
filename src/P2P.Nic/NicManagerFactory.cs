// M1-21 平台分发：Windows=Wintun（05 §1.2）、Linux=TUN（05 §1.3）。客户端宿主装配（M1-30）与
// 集成替身注入（M1-35）经此取默认实现；macOS 不在 M1 支持范围（01 §2.1）。
using System.Runtime.InteropServices;

namespace P2P.Nic;

public static class NicManagerFactory
{
    /// <summary>按当前操作系统创建虚拟网卡管理器（不支持的平台抛 PlatformNotSupportedException）。</summary>
    public static INicManager Create()
    {
        if (OperatingSystem.IsWindows()) return new Windows.WintunNicManager();
        if (OperatingSystem.IsLinux()) return new Linux.LinuxTunNicManager();
        throw new PlatformNotSupportedException("虚拟网卡仅支持 Windows（Wintun）与 Linux（TUN），05 §1.2/§1.3");
    }
}

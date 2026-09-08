// M1-20 Wintun 官方 API P/Invoke（FR-C-201、05 §1.2）。
// AI-03：签名对照 WireGuard/wintun 官方 api/wintun.h 逐参数核对（2026-09 拉取 master 版），勿凭记忆改动。
// 适配器/会话句柄均为不透明指针（WINTUN_ADAPTER_HANDLE / WINTUN_SESSION_HANDLE）。
using System.Runtime.InteropServices;

namespace P2P.Nic.Windows;

internal static class WintunNative
{
    private const string Library = "Wintun.dll";

    /// <summary>wintun.h WINTUN_MIN_RING_CAPACITY = 0x20000（128kiB）：保活会话用最小 ring。</summary>
    public const uint MinRingCapacity = 0x20000;

    // WINTUN_ADAPTER_HANDLE WINAPI WintunCreateAdapter(LPCWSTR Name, LPCWSTR TunnelType, const GUID *RequestedGUID);
    // 成功返回适配器句柄（须 WintunCloseAdapter 释放，且释放时一并移除适配器）；失败返回 NULL（GetLastError）。
    // RequestedGUID 传 NULL → GUID 由系统随机分配（M1 无需确定性 GUID）。
    [DllImport(Library, SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern IntPtr WintunCreateAdapter(string name, string tunnelType, IntPtr requestedGuid);

    // WINTUN_ADAPTER_HANDLE WINAPI WintunOpenAdapter(LPCWSTR Name);
    // 同名适配器不存在时返回 NULL——EnsureAsync 先 Open 后 Create 实现适配器复用（FR-C-201）。
    [DllImport(Library, SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern IntPtr WintunOpenAdapter(string name);

    // VOID WINAPI WintunCloseAdapter(WINTUN_ADAPTER_HANDLE Adapter);
    // 释放句柄；对 WintunCreateAdapter 创建的适配器同时移除适配器（RemoveAsync 删除语义来源）。
    [DllImport(Library, ExactSpelling = true)]
    internal static extern void WintunCloseAdapter(IntPtr adapter);

    // VOID WINAPI WintunGetAdapterLUID(WINTUN_ADAPTER_HANDLE Adapter, NET_LUID *Luid);
    // NET_LUID 为 64 位联合体，按 ULONG64 传出（仅用于 IP Helper 配址定位接口）。
    [DllImport(Library, ExactSpelling = true)]
    internal static extern void WintunGetAdapterLUID(IntPtr adapter, out ulong luid);

    // WINTUN_SESSION_HANDLE WINAPI WintunStartSession(WINTUN_ADAPTER_HANDLE Adapter, DWORD Capacity);
    // Capacity 须在 0x20000~0x4000000 且为 2 的幂；失败返回 NULL（GetLastError）。
    // 会话存续期间适配器保持 up（05 §1.2：保活、不读写 ring）。
    [DllImport(Library, SetLastError = true, ExactSpelling = true)]
    internal static extern IntPtr WintunStartSession(IntPtr adapter, uint capacity);

    // VOID WINAPI WintunEndSession(WINTUN_SESSION_HANDLE Session);
    [DllImport(Library, ExactSpelling = true)]
    internal static extern void WintunEndSession(IntPtr session);
}

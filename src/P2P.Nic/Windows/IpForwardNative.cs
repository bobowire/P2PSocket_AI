// M3-13 IP Helper 路由表 API（iphlpapi.dll 导出；05 §1.4 冲突检测的路由维枚举）。
// AI-03：结构布局对照 Microsoft Learn（iphlpapi.h MIB_IPFORWARDROW）逐字段核对，全 DWORD 自然对齐。
// 缓冲协议：首调 size=0 得所需长度（ERROR_INSUFFICIENT_BUFFER=122）→ AllocHGlobal → 复调 → FreeHGlobal
//（缓冲由调用方分配，不交 FreeMibTable——那是 Get*Table2 族内部 LocalAlloc 内存的释放口）。
using System.Net;
using System.Runtime.InteropServices;

namespace P2P.Nic.Windows;

internal static class IpForwardNative
{
    private const string Library = "iphlpapi.dll";
    public const uint ErrorInsufficientBuffer = 122;

    /// <summary>MIB_IPFORWARDROW（iphlpapi.h）：14 个 DWORD 全自然对齐 56B——
    /// Dest/Mask/NextHop 为网络序 in_addr 原始位（IPAddress(uint) 直取）；IfIndex 与
    /// NetworkInterface.IPv4InterfaceProperties.Index 同一索引空间（自身路由排除的匹配键）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MibIpForwardRow
    {
        public uint Dest;
        public uint Mask;
        public uint Policy;
        public uint NextHop;
        public uint IfIndex;
        public uint Type;
        public uint Proto;
        public uint Age;
        public uint NextHopAs;
        public uint Metric1;
        public uint Metric2;
        public uint Metric3;
        public uint Metric4;
        public uint Metric5;
    }

    /// <summary>MIB_IPFORWARDTABLE：NumEntries(4)+行数组（首行偏移 4，无对齐填充）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MibIpForwardTable
    {
        public uint NumEntries;
        public MibIpForwardRow First; // Table[1]
    }

    // DWORD GetIpForwardTable(PMIB_IPFORWARDTABLE pIpForwardTable, PULONG pdwSize, BOOL bOrder)——
    // 首调传 IntPtr.Zero 探长度（返回 ERROR_INSUFFICIENT_BUFFER）
    [DllImport(Library)]
    internal static extern uint GetIpForwardTable(IntPtr table, out uint size, bool order);

    /// <summary>整表快照（按目的地址排序）；失败抛 NicException（调用方按无路由降级）。</summary>
    internal static List<MibIpForwardRow> ReadTable()
    {
        var status = GetIpForwardTable(IntPtr.Zero, out var size, false);
        if (status != ErrorInsufficientBuffer || size == 0) return [];
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            status = GetIpForwardTable(buffer, out size, false);
            if (status != 0)
                throw new NicException($"GetIpForwardTable 失败：{status}");
            var table = Marshal.PtrToStructure<MibIpForwardTable>(buffer);
            var offset = (int)Marshal.OffsetOf<MibIpForwardTable>(nameof(MibIpForwardTable.First));
            var stride = Marshal.SizeOf<MibIpForwardRow>();
            var rows = new List<MibIpForwardRow>((int)table.NumEntries);
            for (var i = 0; i < table.NumEntries; i++)
                rows.Add(Marshal.PtrToStructure<MibIpForwardRow>(IntPtr.Add(buffer, offset + i * stride)));
            return rows;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>路由行目的/掩码（网络序 DWORD）→ IPAddress。</summary>
    internal static IPAddress ToAddress(uint value) => new(value);
}

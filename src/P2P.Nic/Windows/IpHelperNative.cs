// M1-20 IP Helper 配址 API（netioapi.h 声明、iphlpapi.dll 导出；05 §1.2「不依赖 netsh」）。
// AI-03：结构布局对照 Microsoft Learn（netioapi.h/ws2ipdef.h）逐字段核对，自然对齐（Pack=0）。
// 用法（官方推荐序）：InitializeUnicastIpAddressEntry 取默认值 → 覆写 Address/InterfaceLuid/
// OnLinkPrefixLength → CreateUnicastIpAddressEntry 写入；GetUnicastIpAddressTable 用于幂等预检与校验。
using System.Net;
using System.Runtime.InteropServices;

namespace P2P.Nic.Windows;

internal static class IpHelperNative
{
    private const string Library = "iphlpapi.dll";

    public const uint NoError = 0;
    public const ushort AfInet = 2; // AF_INET（ws2def.h）

    /// <summary>SOCKADDR_INET（ws2ipdef.h）：sockaddr_in(16B)/sockaddr_in6(28B) 联合体 → 28B、4B 对齐。</summary>
    [StructLayout(LayoutKind.Sequential, Size = 28)]
    internal struct SockaddrInet
    {
        public ushort Family; // sin_family / si_family
        public ushort Port;   // sin_port（网络序；配址不用，恒 0）
        public uint Address;  // sin_addr（IPv4 4 字节地址原序直存）
    }

    /// <summary>
    /// MIB_UNICASTIPADDRESS_ROW（netioapi.h）：自然对齐 80B——
    /// Address(28)+pad(4)+InterfaceLuid(8)+InterfaceIndex(4)+PrefixOrigin(4)+SuffixOrigin(4)+
    /// ValidLifetime(4)+PreferredLifetime(4)+OnLinkPrefixLength(1)+SkipAsSource(1)+pad(2)+
    /// DadState(4)+ScopeId(4)+CreationTimeStamp(8)。SkipAsSource 为 C BOOLEAN（UCHAR，1B），故用 byte 承载。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MibUnicastIpAddressRow
    {
        public SockaddrInet Address;
        public ulong InterfaceLuid;
        public uint InterfaceIndex;       // NET_IFINDEX
        public uint PrefixOrigin;         // NL_PREFIX_ORIGIN：1=IpPrefixOriginManual（Initialize 默认）
        public uint SuffixOrigin;         // NL_SUFFIX_ORIGIN：1=IpSuffixOriginManual（Initialize 默认）
        public uint ValidLifetime;        // 秒；0xffffffff=无限（Initialize 默认）
        public uint PreferredLifetime;    // 秒；0xffffffff=无限（Initialize 默认）
        public byte OnLinkPrefixLength;   // IPv4 合法值 0~32
        public byte SkipAsSource;         // BOOLEAN（UCHAR）
        public uint DadState;             // NL_DAD_STATE：4=IpDadStatePreferred
        public uint ScopeId;              // 仅 IPv6 适用
        public long CreationTimeStamp;
    }

    /// <summary>MIB_UNICASTIPADDRESS_TABLE（netioapi.h）：NumEntries(4)+pad(4)+行数组。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MibUnicastIpAddressTable
    {
        public uint NumEntries;
        public MibUnicastIpAddressRow First; // Table[1]（仅用于计算首行偏移）
    }

    /// <summary>Table[0] 相对表指针的偏移（= 8：NumEntries 4B + 对齐 4B）。</summary>
    public static readonly int TableOffset =
        (int)Marshal.OffsetOf<MibUnicastIpAddressTable>(nameof(MibUnicastIpAddressTable.First));

    // DWORD InitializeUnicastIpAddressEntry(PMIB_UNICASTIPADDRESS_ROW Row)
    [DllImport(Library)]
    internal static extern uint InitializeUnicastIpAddressEntry(ref MibUnicastIpAddressRow row);

    // DWORD CreateUnicastIpAddressEntry(const MIB_UNICASTIPADDRESS_ROW *Row)
    [DllImport(Library)]
    internal static extern uint CreateUnicastIpAddressEntry(ref MibUnicastIpAddressRow row);

    // DWORD GetUnicastIpAddressTable(ADDRESS_FAMILY Family, PMIB_UNICASTIPADDRESS_TABLE *Table)——调用方负责 FreeMibTable
    [DllImport(Library)]
    internal static extern uint GetUnicastIpAddressTable(ushort family, out IntPtr table);

    // VOID FreeMibTable(PVOID Memory)
    [DllImport(Library)]
    internal static extern void FreeMibTable(IntPtr table);
}

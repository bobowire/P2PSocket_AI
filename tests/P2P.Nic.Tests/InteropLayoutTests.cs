// M1-20 互操作结构布局回归测试（AI-03）：
// 对照 netioapi.h/ws2ipdef.h 官方文档的自然对齐布局做封送断言——布局漂移会在真实配址时
// 写错接口/越界，属高危回归点。仅托管封送计算，跨平台可跑（CI 常绿护栏）。
using System.Runtime.InteropServices;
using P2P.Nic.Windows;
using Xunit;

namespace P2P.Nic.Tests;

public sealed class InteropLayoutTests
{
    [Fact]
    public void SockaddrInet_Is28Bytes()
        => Assert.Equal(28, Marshal.SizeOf<IpHelperNative.SockaddrInet>());

    [Fact]
    public void UnicastRow_NaturalLayoutMatchesDocumentedOffsets()
    {
        Assert.Equal(80, Marshal.SizeOf<IpHelperNative.MibUnicastIpAddressRow>());
        Assert.Equal(32, Offset(nameof(IpHelperNative.MibUnicastIpAddressRow.InterfaceLuid)));
        Assert.Equal(40, Offset(nameof(IpHelperNative.MibUnicastIpAddressRow.InterfaceIndex)));
        Assert.Equal(44, Offset(nameof(IpHelperNative.MibUnicastIpAddressRow.PrefixOrigin)));
        Assert.Equal(48, Offset(nameof(IpHelperNative.MibUnicastIpAddressRow.SuffixOrigin)));
        Assert.Equal(52, Offset(nameof(IpHelperNative.MibUnicastIpAddressRow.ValidLifetime)));
        Assert.Equal(56, Offset(nameof(IpHelperNative.MibUnicastIpAddressRow.PreferredLifetime)));
        Assert.Equal(60, Offset(nameof(IpHelperNative.MibUnicastIpAddressRow.OnLinkPrefixLength)));
        Assert.Equal(61, Offset(nameof(IpHelperNative.MibUnicastIpAddressRow.SkipAsSource)));
        Assert.Equal(64, Offset(nameof(IpHelperNative.MibUnicastIpAddressRow.DadState)));
        Assert.Equal(68, Offset(nameof(IpHelperNative.MibUnicastIpAddressRow.ScopeId)));
        Assert.Equal(72, Offset(nameof(IpHelperNative.MibUnicastIpAddressRow.CreationTimeStamp)));
    }

    [Fact]
    public void UnicastTable_FirstRowOffsetIs8()
    {
        Assert.Equal(8, Marshal.OffsetOf<IpHelperNative.MibUnicastIpAddressTable>(
            nameof(IpHelperNative.MibUnicastIpAddressTable.First)));
        Assert.Equal(88, Marshal.SizeOf<IpHelperNative.MibUnicastIpAddressTable>());
        Assert.Equal(8, IpHelperNative.TableOffset);
    }

    private static int Offset(string field)
        => (int)Marshal.OffsetOf<IpHelperNative.MibUnicastIpAddressRow>(field);
}

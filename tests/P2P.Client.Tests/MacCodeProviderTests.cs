using System.Text.RegularExpressions;
using P2P.Client.Registration;
using Xunit;

namespace P2P.Client.Tests;

/// <summary>
/// M1-24 macCode 生成单测（05 §10）：网卡枚举黑名单表驱动、多网卡排序稳定性、
/// 网关优先级、回退指纹路径（09 §2.1）。
/// </summary>
public sealed class MacCodeProviderTests
{
    private static MacCodeProvider.NicCandidate Nic(string name, string mac,
        bool up = true, bool gateway = true, bool virtualType = false)
        => new(name, mac, up, gateway, virtualType);

    [Theory]
    [InlineData("Hyper-V Virtual Ethernet Adapter")]
    [InlineData("vEthernet (Default Switch)")]
    [InlineData("VirtualBox Host-Only Adapter")]
    [InlineData("VMware Network Adapter VMnet1")]
    [InlineData("Wintun Userspace Tunnel")]
    [InlineData("p2p-tun")]
    [InlineData("Docker NAT")]
    [InlineData("WSL")]
    [InlineData("Loopback Pseudo-Interface 1")]
    [InlineData("Tailscale")]
    [InlineData("ZeroTier One")]
    public void 黑名单名称_命中即排除(string name)
        => Assert.Null(MacCodeProvider.SelectMac([Nic(name, "AABBCCDDEE01")]));

    [Fact]
    public void 类型排除_Tunnel与Loopback()
        => Assert.Null(MacCodeProvider.SelectMac(
        [
            Nic("eth9", "AABBCCDDEE01", virtualType: true),
            Nic("eth8", "AABBCCDDEE02", virtualType: true),
        ]));

    [Theory]
    [InlineData("AABBCCDDEE0")]        // 11 位
    [InlineData("AABBCCDDEE012")]      // 13 位
    [InlineData("000000000000")]       // 全零
    [InlineData("GGHHCCDDEE01")]       // 非 hex
    [InlineData("")]
    public void 无效MAC_排除(string mac)
        => Assert.Null(MacCodeProvider.SelectMac([Nic("eth0", mac)]));

    [Fact]
    public void 并列网卡_MAC字典序最小_结果稳定()
    {
        var candidates = new[]
        {
            Nic("eth2", "CC0000000002"),
            Nic("eth0", "AA0000000009"),
            Nic("eth1", "BB0000000001"),
        };
        // 输入乱序（洗牌两种排列）结果一致（05 §10 ③ 稳定性）
        Assert.Equal("AA0000000009", MacCodeProvider.SelectMac(candidates.Reverse()));
        Assert.Equal("AA0000000009", MacCodeProvider.SelectMac(candidates));
        Assert.Equal(MacCodeProvider.SelectMac(candidates), MacCodeProvider.SelectMac(candidates.Reverse()));
    }

    [Fact]
    public void 网关优先_胜过更小MAC()
    {
        // eth1 无网关但 MAC 更小；eth0 上线+有网关 → 选中 eth0（05 §10 ②）
        var selected = MacCodeProvider.SelectMac(
        [
            Nic("eth1", "000000000001", up: true, gateway: false),
            Nic("eth0", "FFFFFFFFFFFF", up: true, gateway: true),
        ]);
        Assert.Equal("FFFFFFFFFFFF", selected);
    }

    [Fact]
    public void 全部无网关_仍取合格MAC()
        => Assert.Equal("AA0000000009", MacCodeProvider.SelectMac(
        [
            Nic("eth0", "AA0000000009", gateway: false),
            Nic("eth1", "EE0000000001", gateway: false),
        ]));

    [Fact]
    public void 无合格网卡_回退机器指纹_Sha256前12位()
    {
        // 候选注入空表 → 强制走回退分支（真机总有网卡，不注入则无法稳定覆盖）
        var macCode = MacCodeProvider.Generate(() => "it-fingerprint", []);
        Assert.Equal("P2P-" + MacCodeProvider.FingerprintHex("it-fingerprint"), macCode);
        Assert.Matches(new Regex(@"^P2P-[0-9A-F]{12}$"), macCode);
    }

    [Fact]
    public void 指纹确定性_同输入同输出()
        => Assert.Equal(MacCodeProvider.FingerprintHex("stable"), MacCodeProvider.FingerprintHex("stable"));

    [Fact]
    public void 无回退源_抛异常()
        => Assert.Throws<InvalidOperationException>(() => MacCodeProvider.Generate(() => null, []));

    /// <summary>真机冒烟：本机总有指纹回退，格式必须合法（05 §10 ③）。</summary>
    [Fact]
    public void 真机生成_格式合法()
        => Assert.Matches(new Regex(@"^P2P-[0-9A-F]{12}$"), MacCodeProvider.Generate());
}

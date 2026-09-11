// M1-21 Linux TUN 测试：
// ① rtnetlink 消息构造字节快照——纯内存构造，跨平台常绿（互操作布局回归，AI-03）；
// ② 实机测试（任务清单完成判定：接口出现且 IP 配置成功→幂等→删除干净）——
//    平台守护：非 Linux / 无 /dev/net/tun / 非 root（配址需 CAP_NET_ADMIN）/ 本机已有同名接口时
//    运行期跳过（CI 与 Windows 开发机不依赖 Linux 实机；实机/容器冒烟归 M1-37 手工验收）。
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.Versioning;
using P2P.Nic;
using P2P.Nic.Linux;
using Xunit;

namespace P2P.Nic.Tests;

[SupportedOSPlatform("linux")]
public sealed class LinuxTunNicManagerTests
{
    // 虚拟网段（OQ-2，100.64.0.0/24）内取一测试地址：仅本机接口局部、即用即删
    private static readonly IPAddress TestIp = IPAddress.Parse("100.64.0.201");

    // ── rtnetlink 消息字节快照（跨平台常绿）──────────────────────────

    [Fact]
    public void BuildNewAddrMessage_MatchesKernelLayout()
    {
        var msg = LinuxTunNicManager.BuildNewAddrMessage(IPAddress.Parse("100.64.0.5"), ifIndex: 7, seq: 0x11223344u);

        Assert.Equal(40, msg.Length);
        // nlmsghdr
        Assert.Equal(40u, BinaryPrimitives.ReadUInt32LittleEndian(msg));                         // nlmsg_len
        Assert.Equal((ushort)20, BinaryPrimitives.ReadUInt16LittleEndian(msg.AsSpan(4)));        // RTM_NEWADDR
        Assert.Equal((ushort)0x605, BinaryPrimitives.ReadUInt16LittleEndian(msg.AsSpan(6)));    // REQUEST|ACK|CREATE|EXCL
        Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32LittleEndian(msg.AsSpan(8)));      // seq
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(msg.AsSpan(12)));              // pid
        // ifaddrmsg
        Assert.Equal(2, msg[16]);      // ifa_family = AF_INET
        Assert.Equal(32, msg[17]);     // ifa_prefixlen = /32
        Assert.Equal(0x80, msg[18]);   // ifa_flags = IFA_F_PERMANENT
        Assert.Equal(0, msg[19]);      // ifa_scope = RT_SCOPE_UNIVERSE
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(msg.AsSpan(20)));              // ifa_index
        // rtattr IFA_LOCAL（本端地址）
        Assert.Equal((ushort)8, BinaryPrimitives.ReadUInt16LittleEndian(msg.AsSpan(24)));       // rta_len
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(msg.AsSpan(26)));       // IFA_LOCAL
        Assert.Equal(new byte[] { 100, 64, 0, 5 }, msg[28..32]);
        // rtattr IFA_ADDRESS（点对点设备语义下与本端同值）
        Assert.Equal((ushort)8, BinaryPrimitives.ReadUInt16LittleEndian(msg.AsSpan(32)));       // rta_len
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(msg.AsSpan(34)));       // IFA_ADDRESS
        Assert.Equal(new byte[] { 100, 64, 0, 5 }, msg[36..40]);
    }

    [Fact]
    public void BuildNewLinkMessage_MatchesKernelLayout()
    {
        var msg = LinuxTunNicManager.BuildNewLinkMessage(ifIndex: 9, seq: 0xAABBCCDDu);

        Assert.Equal(32, msg.Length);
        // nlmsghdr
        Assert.Equal(32u, BinaryPrimitives.ReadUInt32LittleEndian(msg));                        // nlmsg_len
        Assert.Equal((ushort)16, BinaryPrimitives.ReadUInt16LittleEndian(msg.AsSpan(4)));       // RTM_NEWLINK
        Assert.Equal((ushort)0x5, BinaryPrimitives.ReadUInt16LittleEndian(msg.AsSpan(6)));      // REQUEST|ACK
        Assert.Equal(0xAABBCCDDu, BinaryPrimitives.ReadUInt32LittleEndian(msg.AsSpan(8)));      // seq
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(msg.AsSpan(12)));              // pid
        // ifinfomsg
        Assert.Equal(0, msg[16]);                                    // ifi_family = AF_UNSPEC
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(msg.AsSpan(18)));       // ifi_type 不修改
        Assert.Equal(9, BinaryPrimitives.ReadInt32LittleEndian(msg.AsSpan(20)));                // ifi_index
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(msg.AsSpan(24)));              // ifi_flags = IFF_UP
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(msg.AsSpan(28)));              // ifi_change = 仅 UP 位
    }

    // ── 实机测试（Linux + root 守护）────────────────────────────────

    [SkippableFact]
    public async Task Ensure_CreatesAppliesIp_Idempotent_RemovesClean()
    {
        Skip.IfNot(TryGetSkipReason(out var reason), reason);

        await using var manager = new LinuxTunNicManager();

        // 创建 + 配址（完成判定：接口出现且 IP 配置成功）
        var handle = await manager.EnsureAsync(TestIp);
        Assert.Equal(LinuxTunNicManager.InterfaceName, handle.AdapterName);
        Assert.Equal(TestIp, handle.VirtualIp);
        var (exitCode, addrOutput) = RunIp($"-4 -o addr show dev {LinuxTunNicManager.InterfaceName}");
        Assert.True(exitCode == 0, $"接口未出现：{addrOutput}");
        Assert.Contains($"{TestIp}/32", addrOutput);

        // 复用幂等：同 IP 二次 Ensure 返回同一句柄
        Assert.Equal(handle, await manager.EnsureAsync(TestIp));

        // 删除干净（幂等）：接口移除经 RCU 异步生效，轮询兜底
        await manager.RemoveAsync();
        await manager.RemoveAsync();
        var deadline = Environment.TickCount64 + 10_000;
        while (InterfaceExists())
        {
            if (Environment.TickCount64 > deadline)
                Assert.Fail("删除后接口仍存在（10s）");
            await Task.Delay(200);
        }
    }

    [SkippableFact]
    public async Task Remove_WithoutEnsure_IsIdempotentNoThrow()
    {
        Skip.IfNot(TryGetSkipReason(out var reason), reason);

        var manager = new LinuxTunNicManager();
        await manager.RemoveAsync();
        await manager.DisposeAsync();
    }

    [SkippableFact]
    public async Task Ensure_NonIpv4_RejectedBeforeTouchingNative()
    {
        Skip.IfNot(TryGetSkipReason(out var reason), reason);

        var manager = new LinuxTunNicManager();
        await Assert.ThrowsAsync<NicException>(() => manager.EnsureAsync(IPAddress.IPv6Loopback));
        Assert.False(InterfaceExists(), "校验失败不应创建接口");
        await manager.DisposeAsync();
    }

    // ── 守护与辅助 ─────────────────────────────────────────────────────

    private static bool TryGetSkipReason([NotNullWhen(false)] out string? reason)
    {
        if (!OperatingSystem.IsLinux()) { reason = "仅 Linux 实机（05 §1 平台边界；CI/Windows 跳过，实机验收归 M1-37）"; return false; }
        if (!File.Exists("/dev/net/tun")) { reason = "内核未提供 /dev/net/tun（modprobe tun；实机用例，M1-37）"; return false; }
        if (LinuxNative.geteuid() != 0) { reason = "需要 root（TUN 配址需 CAP_NET_ADMIN；实机用例，M1-37）"; return false; }
        if (InterfaceExists()) { reason = "本机已有 p2p-tun 接口（外部残留），避免误删"; return false; }
        reason = null;
        return true;
    }

    private static bool InterfaceExists()
        => RunIp($"link show {LinuxTunNicManager.InterfaceName}").ExitCode == 0;

    private static (int ExitCode, string Output) RunIp(string arguments)
    {
        var psi = new ProcessStartInfo("ip", arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        return (process.WaitForExit(5_000) ? process.ExitCode : -1, output);
    }
}

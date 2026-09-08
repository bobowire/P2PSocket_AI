// M1-20 Windows 实机网卡测试（任务清单完成判定：创建→IP 生效→复用幂等→删除干净）。
// 平台守护：非 Windows / 非管理员 / 程序目录无 Wintun.dll / 本机已有同名适配器时运行期跳过
// （CI 与驱动未分发阶段不依赖驱动；实机冒烟归 M1-37 手工验收）。
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.Versioning;
using P2P.Nic;
using P2P.Nic.Windows;
using Xunit;

namespace P2P.Nic.Tests;

[SupportedOSPlatform("windows")]
public sealed class WintunNicManagerTests
{
    // 虚拟网段（OQ-2，100.64.0.0/24）内取一测试地址：仅本机适配器局部、即用即删
    private static readonly IPAddress TestIp = IPAddress.Parse("100.64.0.201");

    [SkippableFact]
    public async Task Ensure_CreatesAppliesIp_Idempotent_RemovesClean()
    {
        Skip.IfNot(TryGetSkipReason(out var reason), reason);

        await using var manager = new WintunNicManager();

        // 创建 + IP 生效：单播表出现 /32 地址（DAD 未完成也视为已配置）
        var handle = await manager.EnsureAsync(TestIp);
        Assert.Equal(WintunNicManager.AdapterName, handle.AdapterName);
        Assert.Equal(TestIp, handle.VirtualIp);
        Assert.Contains(WintunNicManager.QueryLocalAddresses(),
            a => a.Address.Equals(TestIp) && a.PrefixLength == 32);

        // 复用幂等：同 IP 二次 Ensure 返回同一句柄
        Assert.Equal(handle, await manager.EnsureAsync(TestIp));

        // 删除干净（幂等）：地址与适配器先后消失（适配器移除为异步生效，轮询兜底）
        await manager.RemoveAsync();
        await manager.RemoveAsync();
        await WaitForAddressGoneAsync();
        Assert.False(AdapterExists(), "删除后适配器仍存在");
    }

    [SkippableFact]
    public async Task Remove_WithoutEnsure_IsIdempotentNoThrow()
    {
        Skip.IfNot(TryGetSkipReason(out var reason), reason);

        var manager = new WintunNicManager();
        await manager.RemoveAsync();
        await manager.DisposeAsync();
    }

    [SkippableFact]
    public async Task Ensure_NonIpv4_RejectedBeforeTouchingNative()
    {
        Skip.IfNot(TryGetSkipReason(out var reason), reason);

        var manager = new WintunNicManager();
        await Assert.ThrowsAsync<NicException>(() => manager.EnsureAsync(IPAddress.IPv6Loopback));
        Assert.False(AdapterExists(), "校验失败不应创建适配器");
        await manager.DisposeAsync();
    }

    // ── 守护与辅助 ─────────────────────────────────────────────────────

    private static async Task WaitForAddressGoneAsync()
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (WintunNicManager.QueryLocalAddresses().Any(a => a.Address.Equals(TestIp)))
        {
            if (Environment.TickCount64 > deadline)
                Assert.Fail("删除后单播地址仍存在（10s）");
            await Task.Delay(200);
        }
    }

    private static bool AdapterExists()
    {
        var adapter = WintunNative.WintunOpenAdapter(WintunNicManager.AdapterName);
        if (adapter == IntPtr.Zero) return false;
        WintunNative.WintunCloseAdapter(adapter); // 非本调用创建：仅释放句柄，不移除
        return true;
    }

    private static bool TryGetSkipReason([NotNullWhen(false)] out string? reason)
    {
        if (!OperatingSystem.IsWindows()) { reason = "仅 Windows 实机（CI/Linux 跳过）"; return false; }
        if (!RunningAsAdmin()) { reason = "需要管理员权限（Wintun 驱动加载）"; return false; }
        if (!WintunDllAvailable()) { reason = "程序目录无 Wintun.dll（M1-37 随安装包分发）"; return false; }
        if (AdapterExists()) { reason = "本机已有 P2P-Tun 适配器（外部残留），避免误删"; return false; }
        reason = null;
        return true;
    }

    private static bool RunningAsAdmin()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static bool WintunDllAvailable()
        => File.Exists(Path.Combine(AppContext.BaseDirectory, "Wintun.dll"))
           || (Environment.GetEnvironmentVariable("PATH") ?? "")
               .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Any(dir => File.Exists(Path.Combine(dir, "Wintun.dll")));
}

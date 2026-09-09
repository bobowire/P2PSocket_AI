// M1-24 设备识别码生成（05 §10、FR-C-104）：
// ① 多网卡选取：排除虚拟/回环（类型 + 名称前缀黑名单表驱动）→ 优先已上线且有默认网关 → MAC 字典序最小（稳定）；
// ② 回退：机器指纹（Linux /etc/machine-id、Windows 注册表 MachineGuid）SHA-256 前 12 位十六进制充当"MAC"；
// ③ macCode = "P2P-" + MAC 去冒号大写。仅注册时使用，注册后权威身份为 deviceId（D6）。
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace P2P.Client.Registration;

/// <summary>注册用初始识别码生成器（纯选择逻辑与平台采集解耦，便于表驱动单测）。</summary>
public static class MacCodeProvider
{
    /// <summary>05 §10 名称黑名单（表驱动可扩展：虚拟化/TUN/覆盖网产品）。</summary>
    internal static readonly string[] NameBlacklist =
    [
        "Hyper-V", "vEthernet", "VirtualBox", "VMware", "Wintun", "p2p-tun",
        "Docker", "WSL", "Loopback", "Tailscale", "ZeroTier", "WireGuard", "OpenVPN", "TAP",
    ];

    /// <summary>网卡候选（NetworkInterface 的可注入投影）。</summary>
    /// <param name="Name">接口名（Windows 描述/友好名，Linux if 名）。</param>
    /// <param name="MacHex">MAC 去冒号大写（12 hex）。</param>
    /// <param name="IsUp">OperationalStatus == Up。</param>
    /// <param name="HasGateway">拥有默认网关路由。</param>
    /// <param name="IsVirtualType">InterfaceType 为 Tunnel/Loopback。</param>
    public sealed record NicCandidate(string Name, string MacHex, bool IsUp, bool HasGateway, bool IsVirtualType);

    /// <summary>生成 macCode（黑名单过滤 → 网关优先 → MAC 字典序最小；无合格网卡回退机器指纹）。
    /// 测试可注入 <paramref name="candidatesOverride"/>（空表强制走回退分支）。</summary>
    public static string Generate(Func<string?>? machineFingerprint = null,
        IEnumerable<NicCandidate>? candidatesOverride = null)
    {
        var mac = SelectMac(candidatesOverride ?? EnumerateCandidates())
            ?? FingerprintHex((machineFingerprint ?? ReadMachineFingerprint)()
                ?? throw new InvalidOperationException("取不到合格 MAC 且无机器指纹回退源（05 §10）"));
        return "P2P-" + mac;
    }

    /// <summary>多网卡选取（05 §10 ①②③；纯函数，输入顺序无关）。</summary>
    internal static string? SelectMac(IEnumerable<NicCandidate> candidates)
        => candidates
            .Where(c => !c.IsVirtualType && !IsBlacklisted(c.Name) && IsValidMac(c.MacHex))
            .OrderByDescending(c => c.IsUp && c.HasGateway) // ② 上线+网关优先
            .ThenBy(c => c.MacHex, StringComparer.Ordinal)  // ③ 字典序最小（稳定）
            .Select(c => c.MacHex)
            .FirstOrDefault();

    internal static bool IsBlacklisted(string name)
        => NameBlacklist.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>12 位 hex 且非全零（00:00... 与空地址不合格）。</summary>
    internal static bool IsValidMac(string macHex)
        => macHex.Length == 12 && macHex.All(Uri.IsHexDigit) && macHex.Any(c => c != '0');

    private static IEnumerable<NicCandidate> EnumerateCandidates()
        => NetworkInterface.GetAllNetworkInterfaces()
            .Select(ni => new NicCandidate(
                ni.Name,
                string.Concat(ni.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2"))),
                ni.OperationalStatus == OperationalStatus.Up,
                ni.GetIPProperties().GatewayAddresses.Count > 0,
                ni.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Loopback));

    /// <summary>指纹 → 12 位 hex 充当 MAC（05 §10 ②回退）。</summary>
    internal static string FingerprintHex(string fingerprint)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)))[..12];

    /// <summary>平台机器指纹：Windows 注册表 MachineGuid；Linux /etc/machine-id。</summary>
    private static string? ReadMachineFingerprint()
    {
        if (OperatingSystem.IsWindows())
            return ReadWindowsMachineGuid();
        if (OperatingSystem.IsLinux() && File.Exists("/etc/machine-id"))
            return File.ReadAllText("/etc/machine-id").Trim();
        return null;
    }

    private const UIntPtr HKeyLocalMachine = 0x80000002u; // HKEY_LOCAL_MACHINE
    private const uint RrfRtRegSz = 0x00000002;           // 仅接受 REG_SZ

    /// <summary>advapi32 RegGetValueW 直读（MachineGuid，05 §10 回退；免注册表包依赖）。</summary>
    [SupportedOSPlatform("windows")]
    private static string? ReadWindowsMachineGuid()
    {
        var data = new StringBuilder(128);
        uint cb = (uint)data.Capacity;
        return RegGetValueW(HKeyLocalMachine, @"SOFTWARE\Microsoft\Cryptography", "MachineGuid",
            RrfRtRegSz, 0, data, ref cb) == 0 ? data.ToString() : null;
    }

    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern int RegGetValueW(
        UIntPtr hive, string subKey, string value, uint flags, uint type,
        System.Text.StringBuilder data, ref uint cbData);
}

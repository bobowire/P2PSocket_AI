// M3-13 本机网络面枚举（05 §1.4、FR-C-204 冲突检测的数据源）：
// - 地址维：NetworkInterface.GetAllNetworkInterfaces() 单播 IPv4（跨平台；排除 loopback——
//   回环段与本产品虚拟网段无关，且 StubNic 替身/本机回环别名场景不误报）；
// - 路由维：Windows=GetIpForwardTable（行 IfIndex 经 NetworkInterface 索引→接口名翻译）；
//   Linux=/proc/net/route（Iface 列即接口名；Destination/Mask 小端十六进制原序）。
// 仅只读枚举不写任何网络状态；调用方（SubnetConflictDetector）对异常降级为空快照。
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace P2P.Nic;

/// <summary>枚举到的单播地址（接口名 + IPv4 地址）。</summary>
public sealed record SurveyAddress(string InterfaceName, IPAddress Address);

/// <summary>枚举到的 IPv4 路由（接口名 + 目的网络 + 掩码；目的已是网络地址口径由调用方防御掩码）。</summary>
public sealed record SurveyRoute(string InterfaceName, IPAddress Destination, IPAddress Mask);

public static class SubnetSurvey
{
    /// <summary>地址维 + 路由维整表快照（任一维失败降级为空列表，不抛——检测按可得数据求交）。</summary>
    public static (IReadOnlyList<SurveyAddress> Addresses, IReadOnlyList<SurveyRoute> Routes) Snapshot()
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces();
        var addresses = new List<SurveyAddress>();
        foreach (var nic in interfaces)
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                addresses.Add(new SurveyAddress(nic.Name, addr.Address));
            }
        }
        return (addresses, RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? ReadLinuxRoutes()
            : RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? ReadWindowsRoutes(interfaces)
                : []);
    }

    /// <summary>Windows：GetIpForwardTable 行 IfIndex → 接口名（与 NetworkInterface 索引同空间）。</summary>
    private static IReadOnlyList<SurveyRoute> ReadWindowsRoutes(NetworkInterface[] interfaces)
    {
        var names = new Dictionary<uint, string>();
        foreach (var nic in interfaces)
        {
            var index = nic.GetIPProperties().GetIPv4Properties()?.Index;
            if (index is > 0) names[(uint)index] = nic.Name;
        }
        var routes = new List<SurveyRoute>();
        try
        {
            foreach (var row in Windows.IpForwardNative.ReadTable())
                routes.Add(new SurveyRoute(
                    names.TryGetValue(row.IfIndex, out var name) ? name : row.IfIndex.ToString(),
                    Windows.IpForwardNative.ToAddress(row.Dest),
                    Windows.IpForwardNative.ToAddress(row.Mask)));
        }
        catch (NicException) { return []; }
        return routes;
    }

    /// <summary>Linux：/proc/net/route——Iface/Destination/Mask 列（Destination/Mask 按小端
    /// 十六进制逐字节逆序即网络序字节，如 "0100A8C0"→192.168.0.1）。</summary>
    private static IReadOnlyList<SurveyRoute> ReadLinuxRoutes()
    {
        var routes = new List<SurveyRoute>();
        try
        {
            using var reader = new StreamReader("/proc/net/route");
            _ = reader.ReadLine(); // 表头
            while (reader.ReadLine() is { } line)
            {
                var cols = line.Split('\t');
                if (cols.Length < 8) continue;
                var dest = ParseLeHex(cols[1]);
                var mask = ParseLeHex(cols[7]);
                if (dest is null || mask is null) continue;
                routes.Add(new SurveyRoute(cols[0], dest, mask));
            }
        }
        catch (IOException) { return []; }
        return routes;
    }

    private static IPAddress? ParseLeHex(string hex)
    {
        if (hex.Length != 8) return null;
        var bytes = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out var v))
                return null;
            bytes[3 - i] = v; // 小端：字符串首组是地址末字节
        }
        return new IPAddress(bytes);
    }
}

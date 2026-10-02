// M3-13 虚拟网段冲突检测（FR-C-204、05 §1.4）：枚举本机单播地址与 IPv4 路由表，与虚拟网段
//（virtualIp 推导 /24——服务端 virtual_subnet 变更→下发 IP 变→推导跟随，客户端无独立配置）求交。
// 判定口径：
// - 地址维：非自身接口的单播 IPv4 落在虚拟网段内即冲突（外部占用本段地址）；
// - 路由维：非自身接口、前缀长度 ≥ /24 且与虚拟网段相交（更长/等长前缀会在最长前缀匹配中
//   抢占本段流量；更宽路由如默认路由 0.0.0.0/0 不构成冲突——本段 on-link /24 恒胜出）；
// - 排除自身虚拟网卡（OnNicApplied 记名，OrdinalIgnoreCase）与 loopback（枚举器侧）。
// 检测时机：注册后路径 Ensure 完成与网卡恢复沿各主动 Check 一次；/api/system/state 每请求
// 现场重算（冲突解除无本机事件可听，惰性重算才可靠——REST 真相源驱动 WS 提示，TD-16）。
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using P2P.Nic;

namespace P2P.Client.Nic;

/// <summary>冲突明细项：kind=address（本机地址占用）| route（路由前缀抢占）；value=地址/前缀；interface=所属接口。</summary>
public sealed record SubnetConflictItem(string Kind, string Value, string Interface);

/// <summary>冲突状态快照：虚拟网段 + 冲突明细（无冲突时整字段为 null，04 §2.1 /api/system/state.conflict）。</summary>
public sealed record SubnetConflictState(string Subnet, SubnetConflictItem[] Items);

public sealed class SubnetConflictDetector
{
    /// <summary>枚举源（生产=SubnetSurvey.Snapshot；测试注入可变替身驱动命中/解除）。</summary>
    public delegate (IReadOnlyList<SurveyAddress> Addresses, IReadOnlyList<SurveyRoute> Routes) SurveySource();

    private readonly SurveySource _survey;
    private readonly object _gate = new();
    private volatile string? _ownAdapter;
    private SubnetConflictState? _last;

    /// <summary>冲突出现/解除/明细变化（→WS subnet_conflict 提示，前端 refetch /api/system/state）。</summary>
    public event Action<SubnetConflictState?>? ConflictsChanged;

    /// <summary>诊断日志（ClientRuntime 接 Serilog）。</summary>
    public event Action<string>? Log;

    public SubnetConflictDetector(SurveySource survey) => _survey = survey;

    /// <summary>记录自身虚拟网卡适配器名（Ensure 句柄来源；null=未应用/未知——不排除任何接口，
    /// 未应用时枚举中通常也不存在自身地址，不构成误报源）。</summary>
    public void OnNicApplied(string? adapterName) => _ownAdapter = adapterName;

    /// <summary>现场检测（无冲突返回 null）。与上次结果比对，变化即发 ConflictsChanged。</summary>
    public SubnetConflictState? Check(string? virtualIp)
    {
        SubnetConflictState? state = null;
        if (IPAddress.TryParse(virtualIp, out var vip) && vip.AddressFamily == AddressFamily.InterNetwork)
        {
            var subnet = DeriveSubnet(vip);
            var (addresses, routes) = SafeSurvey();
            var items = FindConflicts(subnet, _ownAdapter, addresses, routes);
            if (items.Count > 0) state = new SubnetConflictState(subnet.ToString(), [.. items]);
        }

        lock (_gate)
        {
            if (StateEquals(_last, state)) return state;
            _last = state;
        }
        if (state is null) Log?.Invoke($"[nic] 虚拟网段冲突已解除（FR-C-204 告警条消失）");
        else Log?.Invoke($"[nic] 虚拟网段冲突：{state.Subnet} 与本机 {state.Items.Length} 项重叠" +
                         $"（{string.Join("; ", state.Items.Select(i => $"{i.Kind} {i.Value}@{i.Interface}"))}）" +
                         "——建议管理员在服务端调整网段（virtual_subnet）");
        ConflictsChanged?.Invoke(state);
        return state;
    }

    /// <summary>求交纯函数（单测直调；排除自身接口 + 路由前缀长度 ≥ 虚拟网段才可能抢占匹配）。</summary>
    internal static List<SubnetConflictItem> FindConflicts(
        IPNetwork subnet, string? ownAdapter,
        IReadOnlyList<SurveyAddress> addresses, IReadOnlyList<SurveyRoute> routes)
    {
        var items = new List<SubnetConflictItem>();
        foreach (var a in addresses)
        {
            if (IsOwn(a.InterfaceName, ownAdapter)) continue;
            if (subnet.Contains(a.Address))
                items.Add(new SubnetConflictItem("address", a.Address.ToString(), a.InterfaceName));
        }
        foreach (var r in routes)
        {
            if (IsOwn(r.InterfaceName, ownAdapter)) continue;
            var prefix = PrefixLength(r.Mask);
            if (prefix < subnet.PrefixLength) continue; // 更宽路由不抢本段匹配（/0 默认路由族恒不报）
            var network = ApplyMask(r.Destination, r.Mask);
            var routeNet = new IPNetwork(network, prefix);
            if (!Overlaps(routeNet, subnet)) continue; // 不相交
            items.Add(new SubnetConflictItem("route", routeNet.ToString(), r.InterfaceName));
        }
        items.Sort((x, y) => string.CompareOrdinal(x.Kind, y.Kind) != 0
            ? string.CompareOrdinal(x.Kind, y.Kind)
            : string.CompareOrdinal(x.Interface, y.Interface) != 0
                ? string.CompareOrdinal(x.Interface, y.Interface)
                : string.CompareOrdinal(x.Value, y.Value));
        return items;
    }

    /// <summary>virtualIp 推导 /24 网段（虚拟网段末字节清零；IPv4 限定）。</summary>
    internal static IPNetwork DeriveSubnet(IPAddress virtualIp)
    {
        var b = virtualIp.GetAddressBytes();
        b[3] = 0;
        return new IPNetwork(new IPAddress(b), 24);
    }

    private (IReadOnlyList<SurveyAddress>, IReadOnlyList<SurveyRoute>) SafeSurvey()
    {
        try { return _survey(); }
        catch (Exception e)
        {
            Log?.Invoke($"[nic] 网络面枚举失败（按空快照降级）：{e.Message}");
            return ([], []);
        }
    }

    private static bool StateEquals(SubnetConflictState? a, SubnetConflictState? b) =>
        a is null && b is null
        || a is not null && b is not null
            && string.Equals(a.Subnet, b.Subnet, StringComparison.Ordinal)
            && a.Items.SequenceEqual(b.Items); // 项为 record 值相等；数组须逐项比（SequenceEqual）

    private static bool IsOwn(string iface, string? ownAdapter) =>
        ownAdapter is not null && string.Equals(iface, ownAdapter, StringComparison.OrdinalIgnoreCase);

    /// <summary>两 IPv4 网段相交（按较短前缀掩码比对网络地址——.NET IPNetwork 无 Contains(IPNetwork)）。</summary>
    private static bool Overlaps(IPNetwork a, IPNetwork b)
    {
        var ab = a.BaseAddress.GetAddressBytes();
        var bb = b.BaseAddress.GetAddressBytes();
        var len = Math.Min(a.PrefixLength, b.PrefixLength);
        for (var i = 0; i < 4; i++)
        {
            byte mask = i * 8 + 8 <= len ? (byte)0xFF
                : i * 8 >= len ? (byte)0
                : (byte)(0xFF << (8 - (len - i * 8)));
            if ((ab[i] & mask) != (bb[i] & mask)) return false;
        }
        return true;
    }

    private static int PrefixLength(IPAddress mask)
    {
        var b = mask.GetAddressBytes();
        return BitOperations.PopCount(BinaryPrimitives.ReadUInt32BigEndian(b));
    }

    private static IPAddress ApplyMask(IPAddress address, IPAddress mask)
    {
        var a = address.GetAddressBytes();
        var m = mask.GetAddressBytes();
        for (var i = 0; i < 4; i++) a[i] &= m[i];
        return new IPAddress(a);
    }
}

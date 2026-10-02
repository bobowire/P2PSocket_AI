// M3-13 虚拟网段冲突检测测试（FR-C-204、05 §1.4，清单完成判定）：
// - 纯函数求交：构造冲突地址/路由表→命中矩阵（地址维、等宽/更窄前缀、宽路由/默认路由不报）；
// - 自身接口排除（OrdinalIgnoreCase）与无冲突静默；
// - 检测器级：网段变更重算（服务端 virtual_subnet 变→下发 IP 变→推导跟随）、
//   ConflictsChanged 出现/解除事件、OnNicApplied 记名、未注册（无 VirtualIp）恒 null。
using System.Net;
using P2P.Client.Nic;
using P2P.Nic;
using Xunit;

namespace P2P.Client.Tests;

public sealed class SubnetConflictTests
{
    private static SurveyAddress A(string iface, string ip) => new(iface, IPAddress.Parse(ip));

    private static SurveyRoute R(string iface, string dest, string mask)
        => new(iface, IPAddress.Parse(dest), IPAddress.Parse(mask));

    private static IPNetwork SubnetOf(string vip)
        => SubnetConflictDetector.DeriveSubnet(IPAddress.Parse(vip));

    // ── 纯函数求交 ─────────────────────────────────────────────────

    [Fact]
    public void 求交_地址维命中与排序与路由维矩阵()
    {
        var subnet = SubnetOf("100.64.0.2"); // → 100.64.0.0/24
        var addresses = new[]
        {
            A("eth0", "192.168.1.10"),   // 段外地址：不报
            A("eth0", "100.64.0.50"),    // 段内地址：报
            A("wlan1", "100.64.0.99"),   // 段内地址：报（多接口各自成项）
        };
        var routes = new[]
        {
            R("eth0", "100.64.0.0", "255.255.255.0"),   // 等宽外部路由：报
            R("eth0", "100.64.0.128", "255.255.255.128"), // 更窄 /25 抢占：报
            R("eth0", "100.64.0.0", "255.255.0.0"),     // 更宽 /16 不抢本段匹配：不报
            R("eth0", "0.0.0.0", "0.0.0.0"),            // 默认路由：不报
            R("eth0", "192.168.1.0", "255.255.255.0"),  // 段外路由：不报
        };

        var items = SubnetConflictDetector.FindConflicts(subnet, ownAdapter: null, addresses, routes);

        // 排序：kind 字典序 address 在前、同类内按接口名字典序
        Assert.Equal(
        [
            new SubnetConflictItem("address", "100.64.0.50", "eth0"),
            new SubnetConflictItem("address", "100.64.0.99", "wlan1"),
            new SubnetConflictItem("route", "100.64.0.0/24", "eth0"),
            new SubnetConflictItem("route", "100.64.0.128/25", "eth0"),
        ], items);
    }

    [Fact]
    public void 求交_自身接口地址与路由均排除_大小写不敏感()
    {
        var subnet = SubnetOf("100.64.0.2");
        var addresses = new[] { A("P2P-TUN", "100.64.0.2") };
        var routes = new[] { R("p2p-tun", "100.64.0.0", "255.255.255.0") }; // 自身 on-link 路由

        var items = SubnetConflictDetector.FindConflicts(subnet, ownAdapter: "P2P-Tun", addresses, routes);
        Assert.Empty(items);
    }

    [Fact]
    public void 求交_普通办公网络无冲突()
    {
        var subnet = SubnetOf("100.64.0.2");
        var addresses = new[] { A("eth0", "192.168.1.10"), A("wlan1", "10.0.0.5") };
        var routes = new[]
        {
            R("eth0", "192.168.1.0", "255.255.255.0"),
            R("eth0", "10.0.0.0", "255.255.0.0"),
            R("eth0", "0.0.0.0", "0.0.0.0"),
        };
        Assert.Empty(SubnetConflictDetector.FindConflicts(subnet, null, addresses, routes));
    }

    [Theory]
    [InlineData("100.64.0.2", "100.64.0.0/24")]   // 服务端 virtual_subnet 默认段
    [InlineData("100.65.7.9", "100.65.7.0/24")]   // 管理员改段→下发 IP 变→推导跟随（末字节清零）
    [InlineData("10.1.2.3", "10.1.2.0/24")]
    public void 网段推导_virtualIp_末字节清零(string vip, string expected)
        => Assert.Equal(expected, SubnetConflictDetector.DeriveSubnet(IPAddress.Parse(vip)).ToString());

    // ── 检测器级（Check 重算与事件）───────────────────────────────

    [Fact]
    public void 检测器_冲突出现_网段变更重算_解除消失_事件序列()
    {
        var addresses = new List<SurveyAddress> { A("eth9", "100.64.0.50") };
        var routes = new List<SurveyRoute>();
        var detector = new SubnetConflictDetector(() => (addresses, routes));
        var changes = new List<SubnetConflictState?>();
        detector.ConflictsChanged += s => changes.Add(s);

        // ① 命中：地址维 1 项
        var hit = detector.Check("100.64.0.2");
        Assert.NotNull(hit);
        Assert.Equal("100.64.0.0/24", hit.Subnet);
        var item = Assert.Single(hit.Items);
        Assert.Equal(("address", "100.64.0.50", "eth9"), (item.Kind, item.Value, item.Interface));

        // ② 网段变更（virtual_ip 换段）：原冲突不再落在推导段内→空；解除事件
        Assert.Null(detector.Check("10.0.0.2"));

        // ③ 再注入路由冲突：等宽外部路由
        routes.Add(R("corp0", "10.0.0.0", "255.255.255.0"));
        var routeHit = detector.Check("10.0.0.2");
        Assert.NotNull(routeHit);
        Assert.Equal(("route", "10.0.0.0/24", "corp0"),
            (routeHit.Items[0].Kind, routeHit.Items[0].Value, routeHit.Items[0].Interface));

        // ④ 解除：清空枚举源→null
        addresses.Clear();
        routes.Clear();
        Assert.Null(detector.Check("10.0.0.2"));

        // 事件序列与状态变迁一一对应（重复同值 Check 不再发——①后无冗余事件）
        Assert.Equal(4, changes.Count);
        Assert.NotNull(changes[0]);
        Assert.Null(changes[1]);
        Assert.NotNull(changes[2]);
        Assert.Null(changes[3]);

        // ⑤ 未注册（VirtualIp 空）/非法值：恒 null 且不发事件
        Assert.Null(detector.Check(null));
        Assert.Null(detector.Check("not-an-ip"));
        Assert.Equal(4, changes.Count);
    }

    [Fact]
    public void 检测器_OnNicApplied_排除自身接口()
    {
        var addresses = new List<SurveyAddress> { A("stub-0", "100.64.0.2"), A("eth9", "100.64.0.50") };
        var detector = new SubnetConflictDetector(() => (addresses, (IReadOnlyList<SurveyRoute>)[]));

        var hit = detector.Check("100.64.0.2");
        Assert.NotNull(hit);
        Assert.Equal(2, hit.Items.Length); // 自身未记名：stub-0 与 eth9 都报

        detector.OnNicApplied("stub-0"); // Ensure 句柄记名
        var after = detector.Check("100.64.0.2");
        Assert.NotNull(after);
        var item = Assert.Single(after.Items);
        Assert.Equal("eth9", item.Interface); // 仅剩外部冲突项
    }

    [Fact]
    public void 检测器_枚举源异常降级为空快照()
    {
        var detector = new SubnetConflictDetector(() => throw new IOException("枚举不可用"));
        var logs = new List<string>();
        detector.Log += logs.Add;

        Assert.Null(detector.Check("100.64.0.2")); // 降级空快照→无冲突，不抛
        Assert.Contains(logs, l => l.Contains("枚举失败"));
    }
}

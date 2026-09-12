using System.Net;
using System.Net.Sockets;
using System.Text;
using P2P.Core.Crypto;
using P2P.Core.Stun;
using P2P.IntegrationTests.NatSimulator;
using Xunit;

namespace P2P.IntegrationTests;

/// <summary>
/// M2-30 TcpNatSimulator TCP NAT 模拟（TD-17"导演+桥接"，任务清单完成判定：模式参数化拉起；
/// 映射分配/过滤行为符合定义）。拓扑（单宿主机回环别名，09 §2.2）：模拟公网 127.0.0.1｜
/// C1=127.0.0.4（子段 20001..，SymmetricSequential）｜C2=127.0.0.5（子段 20101..，
/// SymmetricSequential）｜C3=127.0.0.6（子段 20201..，SymmetricRandom）。<br/>
/// 代数（02 §5.2 端口预测，N=并发数、P=探测所得映射端口）：对端 c_k 的外部端口为 P+1+k，
/// 打洞目标 T=P+(N−1)（N=1 时 T=P 即探测端口，走 listen 交付）。对称双端下 A.c_{N−2} 的
/// 源端口恰为 A_P+N−1=B 的 c_{N−2} 的目的端口 → 四元组交叉命中（M2-30 断言该代数在模拟器成立；
/// 全链路 N=1~5 命中矩阵归 M2-31 A-5）。
/// </summary>
public sealed class TcpNatSimulatorTests : IAsyncLifetime
{
    private static readonly IPAddress Pub = IPAddress.Loopback;
    private static readonly IPAddress C1Ip = IPAddress.Parse("127.0.0.4");
    private static readonly IPAddress C2Ip = IPAddress.Parse("127.0.0.5");
    private static readonly IPAddress C3Ip = IPAddress.Parse("127.0.0.6");
    private static readonly IPAddress RIp = IPAddress.Parse("127.0.0.9");

    /// <summary>C1 子段基址（顺序分配首个外部端口）。</summary>
    private const int C1Base = 20001;

    /// <summary>C2 子段基址。</summary>
    private const int C2Base = 20101;

    private TcpNatSimulator _sim = null!;

    public Task InitializeAsync()
    {
        _sim = new TcpNatSimulator(new TcpNatOptions { PublicAddress = Pub });
        _sim.RegisterClient(C1Ip, TcpNatMode.SymmetricSequential, "C1");
        _sim.RegisterClient(C2Ip, TcpNatMode.SymmetricSequential, "C2");
        _sim.RegisterClient(C3Ip, TcpNatMode.SymmetricRandom, "C3");
        return _sim.StartAsync();
    }

    public async Task DisposeAsync() => await _sim.DisposeAsync();

    [Fact]
    public async Task STUN探测_导演改写映射地址_顺序分配加一_窗口就位()
    {
        // C1 探测：XOR-MAPPED-ADDRESS 改写为导演分配端口（子段基址，Sequential 确定性）
        var (p1, l1) = await ProbeAsync(C1Ip);
        Assert.Equal(Pub, p1.Address);
        Assert.Equal(C1Base, p1.Port);

        var map1 = Assert.Single(_sim.Mappings, m => m.Client == "C1");
        Assert.Equal(new IPEndPoint(C1Ip, l1), map1.Internal);
        Assert.Equal(C1Base, map1.PublicPort);
        Assert.Equal(_sim.StunEndpoint, map1.Destination); // 探测映射

        // 第二次探测（新连接）：外部端口 +1 递增——端口预测的模拟基础
        var (p2, _) = await ProbeAsync(C1Ip);
        Assert.Equal(C1Base + 1, p2.Port);

        // 窗口就位：C2 连 P1（C1 探测端口=窗口监听）→ accept 后按探测映射交付 C1 内部 listen——
        // 此刻无 listen → listener_refused（连接被关 + miss 记录，负向断言即时化）
        var probe = await ConnectFromAsync(C2Ip, p1);
        await AssertClosedAsync(probe);
        await UntilMissedAsync(C1Base, "listener_refused");
    }

    [Fact]
    public async Task Random模式_分配落在子段_映射逐连接记录()
    {
        var (p1, _) = await ProbeAsync(C3Ip);
        var (p2, _) = await ProbeAsync(C3Ip);
        Assert.InRange(p1.Port, 20201, 20299);
        Assert.InRange(p2.Port, 20201, 20299);
        Assert.Equal(2, _sim.Mappings.Count(m => m.Client == "C3")); // 每连接独立记录
    }

    [Fact]
    public async Task 探测端口listen交付_N1直连路径_双向桥接()
    {
        // C1 探测后在端口 L listen（打洞第②步，02 §5.2）→ C2 连 P1 → 模拟器终结三方握手并桥接
        var (p1, l1) = await ProbeAsync(C1Ip);
        using var listener = ListenAt(C1Ip, l1);

        var c2 = await ConnectFromAsync(C2Ip, p1);
        using var _ = c2;
        using var accepted = await listener.AcceptAsync();

        await SendAsync(c2, "ping");
        Assert.Equal("ping", await ReceiveTextAsync(accepted));
        await SendAsync(accepted, "pong");
        Assert.Equal("pong", await ReceiveTextAsync(c2)); // 双向桥接（NAT 会话字节流透明）

        // C2 侧出站映射：首个导演分配 + 目标即 P1
        var c2Map = Assert.Single(_sim.Mappings, m => m.Client == "C2");
        Assert.Equal(C2Base, c2Map.PublicPort);
        Assert.Equal(p1, c2Map.Destination);
    }

    [Fact]
    public async Task 对称rendezvous_N2_精确身份过滤_四元组交叉()
    {
        // 代数（N=2）：a0 外部端口 P1+1、目标 P2+1；b0 外部端口 P2+1、目标 P1+1 → 交叉互指
        var (p1, _) = await ProbeAsync(C1Ip);
        var (p2, _) = await ProbeAsync(C2Ip);

        var a0 = await ConnectFromAsync(C1Ip, new IPEndPoint(Pub, p2.Port + 1));
        using var _a = a0;
        var b0 = await ConnectFromAsync(C2Ip, new IPEndPoint(Pub, p1.Port + 1));
        using var _b = b0;

        await SendAsync(a0, "ping");
        Assert.Equal("ping", await ReceiveTextAsync(b0));
        await SendAsync(b0, "pong");
        Assert.Equal("pong", await ReceiveTextAsync(a0));

        // 映射代数可视：a0 → (P1+1, dest P2+1)；b0 → (P2+1, dest P1+1)
        var a0Map = Assert.Single(_sim.Mappings, m => m.Client == "C1" && m.PublicPort == p1.Port + 1);
        Assert.Equal(p2.Port + 1, a0Map.Destination.Port);
        var b0Map = Assert.Single(_sim.Mappings, m => m.Client == "C2" && m.PublicPort == p2.Port + 1);
        Assert.Equal(p1.Port + 1, b0Map.Destination.Port);
    }

    [Fact]
    public async Task 无映射与身份不匹配_入站miss_连接关闭并记录()
    {
        var (p1, _) = await ProbeAsync(C1Ip);
        var (p2, _) = await ProbeAsync(C2Ip);

        // ① 不对称 N：C2 打 C1 的预测目标 P1+4，C1 只有探测映射（P1）→ 无映射 miss
        var over = await ConnectFromAsync(C2Ip, new IPEndPoint(Pub, p1.Port + 4));
        using var _over = over;
        await AssertClosedAsync(over);
        await UntilMissedAsync(p1.Port + 4, "no_mapping");

        // ② 身份不匹配（APDF）：a0 先连 P2+4 建立映射（外部端口 P1+1、dest P2+4）；
        //    bX 连 P1+1，但 a0 映射的目的端口是 P2+4 ≠ bX 分配的 P2+1 → 过滤 miss
        var a0 = await ConnectFromAsync(C1Ip, new IPEndPoint(Pub, p2.Port + 4));
        using var _a = a0;
        await AssertClosedAsync(a0); // C2 在 P2+4 上本无映射（同 ①）
        await UntilMissedAsync(p2.Port + 4, "no_mapping");

        var bX = await ConnectFromAsync(C2Ip, new IPEndPoint(Pub, p1.Port + 1));
        using var _b = bX;
        await AssertClosedAsync(bX);
        await UntilMissedAsync(p1.Port + 1, "filtered_identity");
    }

    [Fact]
    public async Task 未注册来源_窗口拒绝并记录()
    {
        var (p1, _) = await ProbeAsync(C1Ip);
        using var r = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        r.Bind(new IPEndPoint(RIp, 0));
        await r.ConnectAsync(p1);
        await AssertClosedAsync(r);
        await UntilMissedAsync(p1.Port, "unregistered_source");
    }

    // ── 工具 ───────────────────────────────────────────────────────────

    /// <summary>STUN-TCP 探测（真实 StunCodec 组包/StunTcpFraming 定界，DEVICE-AUTH 由模拟器忽略）：
    /// 绑定内网 IP 的专用端口 L 完成事务即关——返回 (导演改写后的映射端点, L)。</summary>
    private async Task<(IPEndPoint Mapped, int LocalPort)> ProbeAsync(IPAddress localIp)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Bind(new IPEndPoint(localIp, 0));
        var local = (IPEndPoint)socket.LocalEndPoint!;

        using var cts = new CancellationTokenSource(3000);
        await socket.ConnectAsync(_sim.StunEndpoint, cts.Token);
        using var stream = new NetworkStream(socket, ownsSocket: false);
        var tid = StunCodec.NewTransactionId();
        var request = StunCodec.BuildBindingRequest(tid, Guid.NewGuid(), RandomGenerator.Bytes(32),
            (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), RandomGenerator.Bytes(16));
        await StunTcpFraming.WriteAsync(stream, request, cts.Token);
        var wire = await StunTcpFraming.TryReadAsync(stream, cts.Token);
        Assert.NotNull(wire);
        Assert.True(StunCodec.TryParseBindingResponse(wire, out var resp));
        return (resp!.Mapped, local.Port);
    }

    /// <summary>客户端内网侧出站连接（源=localIp 任意端口）。</summary>
    private static async Task<Socket> ConnectFromAsync(IPAddress localIp, IPEndPoint target)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(localIp, 0));
        await socket.ConnectAsync(target);
        return socket;
    }

    /// <summary>在 (ip, port) listen（SO_REUSEADDR——端口 L 为探测连接刚关闭，复用打洞第②步语义）。</summary>
    private static Socket ListenAt(IPAddress ip, int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Bind(new IPEndPoint(ip, port));
        socket.Listen(8);
        return socket;
    }

    private static async Task SendAsync(Socket s, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await s.SendAsync(bytes, SocketFlags.None);
    }

    private static async Task<string> ReceiveTextAsync(Socket s, int ms = 3000)
    {
        using var cts = new CancellationTokenSource(ms);
        var buf = new byte[64];
        var got = 0;
        while (got == 0)
        {
            var n = await s.ReceiveAsync(buf, SocketFlags.None, cts.Token);
            Assert.True(n > 0, "对端在载荷到达前关闭连接");
            got = n;
        }
        return Encoding.UTF8.GetString(buf, 0, got);
    }

    /// <summary>断言连接已被模拟器关闭（FIN 到达且无后续数据）。</summary>
    private static async Task AssertClosedAsync(Socket s, int ms = 3000)
    {
        var deadline = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < deadline)
        {
            if (s.Poll(1, SelectMode.SelectRead) && s.Available == 0) return;
            await Task.Delay(20);
        }
        Assert.Fail("连接应被模拟器关闭（miss/拒绝路径）");
    }

    /// <summary>轮询直至 target 端口出现指定 reason 的 miss 记录（负向断言即时化）。</summary>
    private async Task UntilMissedAsync(int targetPort, string reason)
    {
        var deadline = Environment.TickCount64 + 3000;
        while (true)
        {
            if (_sim.Missed.Any(m => m.TargetPort == targetPort && m.Reason == reason)) return;
            if (Environment.TickCount64 > deadline)
                Assert.Fail($"miss 记录未出现：target={targetPort} reason={reason}，实际：{string.Join("；", _sim.Missed)}");
            await Task.Delay(20);
        }
    }
}

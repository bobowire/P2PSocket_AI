using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using P2P.Core.Stun;

namespace P2P.IntegrationTests.NatSimulator;

/// <summary>TCP NAT 模式（09 §2.2；A-5/A-6 消费）。</summary>
public enum TcpNatMode
{
    /// <summary>每次出站新连接外部端口 +1 递增——TCP 端口预测 +N 命中的模拟基础（A-5）。</summary>
    SymmetricSequential,

    /// <summary>每次出站新连接随机分配外部端口——预测必 miss（TCP 打洞失败路径）。</summary>
    SymmetricRandom,
}

/// <summary>TcpNatSimulator 配置。</summary>
public sealed class TcpNatOptions
{
    /// <summary>公网侧监听地址（模拟"互联网"的回环别名；注册客户端在内网侧各用独立回环 IP）。</summary>
    public IPAddress PublicAddress { get; init; } = IPAddress.Loopback;

    /// <summary>STUN-TCP 监听端口（0=随机）。TD-07 派生端点=控制地址主机+:3478——场景注入须置 3478。</summary>
    public int StunPort { get; init; }

    /// <summary>模拟公网端口段下界（08 §4：该段需从 OS 临时端口排除）。</summary>
    public int PortBase { get; init; } = 20000;

    /// <summary>模拟公网端口段长度。</summary>
    public int PortCount { get; init; } = 1000;

    /// <summary>每客户端独占子段长度（探测窗口与顺序分配都落在子段内，客户端间互不重叠）。</summary>
    public int SubRangeSize { get; init; } = 100;

    /// <summary>打洞窗口监听数 = 端口预测目标空间上限（OQ-1 N≤5）。</summary>
    public int PunchWindow { get; init; } = 5;

    /// <summary>单事务（STUN-TCP Binding）整体超时。</summary>
    public TimeSpan TransactionTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>入站交付 miss 前的重试窗（模拟 SYN 重传：早到 SYN 等对侧映射/内部 listen 就绪）。</summary>
    public TimeSpan RetryWindow { get; init; } = TimeSpan.FromMilliseconds(600);
}

/// <summary>单条 TCP NAT 映射的观测视图（测试断言分配序/目标用）。</summary>
public sealed record TcpMappingView(string Client, TcpNatMode Mode, IPEndPoint Internal, int PublicPort, IPEndPoint Destination);

/// <summary>入站未命中/被过滤的观测记录（负向断言即时化）。</summary>
public sealed record TcpMissView(string Receiver, string Sender, int TargetPort, string Reason);

/// <summary>
/// 单宿主机 TCP NAT 模拟器（09 §2.2、TD-17"导演+桥接"）：用户态无法让真实 SYN 交叉，故模拟器
/// 代理一切 TCP 流量并作三方交付——<br/>
/// ① STUN-TCP 代理：监听 <see cref="TcpNatOptions.StunPort"/>，为注册客户端的 Binding 事务
/// **分配导演外部端口 E**（Sequential 自子段基址 +1 递增 / Random 随机）并改写
/// XOR-MAPPED-ADDRESS 为 (PublicAddress, E)——模拟器自身即权威，不经上游；<br/>
/// ② 打洞窗口：探测后在 E+0..E+(PunchWindow−1) 开窗监听（对端 N≤5 的预测目标全落窗内，
/// 监听先于响应发出，杜绝先连后开竞态）；<br/>
/// ③ 出站归因：窗口 accept 到注册客户端连接 = 其一次出站——按 accept 序为其分配下一个导演端口
/// 并记录映射（真实 NAT 按 SYN 到达序分配，多路 connect 的端口归属是排列的，命中不受影响：
/// 预测目标只有一个端口值，N 路中恰有一路持有之）；<br/>
/// ④ 入站交付（接收方端口 W 的映射查表）：**出站映射**按精确身份过滤——源侧为本次连接分配的
/// 端口须等于映射的目的端口（APDF，对称 rendezvous c_{N-2} 四元组交叉）；**探测映射**交付到
/// 探测时的内部端点 listen（N=1 直连 STUN 端口前提，02 §5.2，任意注册来源可入、多次可入）；<br/>
/// ⑤ 桥接：应用层终结——出站映射 splice 双 accept 套接字；探测映射以新套接字 connect 内部
/// listen 后 splice。PTP 字节流透明、内核不可见（不可验证真机 SYN 时差——A-5 保留手工样本）。<br/>
/// 未注册来源 / 无映射 / 身份不匹配 / 桥接重复 → 关闭 + <see cref="Missed"/> 记录（负向断言即时化）。
/// </summary>
public sealed class TcpNatSimulator : IAsyncDisposable
{
    private readonly TcpNatOptions _options;
    private readonly Dictionary<IPAddress, ClientNat> _clients = [];
    private readonly Dictionary<int, Window> _windows = []; // 窗口端口 → 监听与属主
    private readonly List<TcpMissView> _missed = [];
    private readonly List<Task> _loops = [];
    private readonly HashSet<Socket> _bridged = []; // 已桥接套接字（同对幂等去重 + 停机兜底关闭）
    private readonly object _lock = new();
    private TcpListener _stunListener = null!;
    private CancellationTokenSource _cts = null!;
    private int _registered;

    /// <summary>STUN-TCP 监听地址（客户端配置的"STUN 服务器"，测试注入）。</summary>
    public IPEndPoint StunEndpoint { get; private set; } = null!;

    public TcpNatSimulator(TcpNatOptions? options = null)
    {
        _options = options ?? new TcpNatOptions();
    }

    /// <summary>注册一个客户端 NAT：internalIp 为其内网侧地址（建议独立回环 IP）；端口子段按注册序分配。</summary>
    public void RegisterClient(IPAddress internalIp, TcpNatMode mode, string name)
    {
        lock (_lock)
        {
            if (_clients.ContainsKey(internalIp))
                throw new InvalidOperationException($"内网 IP 已注册：{internalIp}");
            var subBase = _options.PortBase + _registered * _options.SubRangeSize;
            if (subBase + _options.SubRangeSize > _options.PortBase + _options.PortCount)
                throw new InvalidOperationException("模拟公网端口段耗尽（子段分配越界）");
            _clients[internalIp] = new ClientNat(name, mode, internalIp, subBase + 1, subBase + _options.SubRangeSize - 1);
            _registered++;
        }
    }

    /// <summary>绑定 STUN-TCP 监听（客户端在此之后方可探测）。</summary>
    public Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        _stunListener = new TcpListener(_options.PublicAddress, _options.StunPort);
        _stunListener.Start(16);
        StunEndpoint = (IPEndPoint)_stunListener.LocalEndpoint!;
        _loops.Add(Task.Run(() => StunAcceptLoopAsync(_cts.Token)));
        return Task.CompletedTask;
    }

    /// <summary>当前全部映射快照（按客户端注册序，映射创建序）。</summary>
    public IReadOnlyList<TcpMappingView> Mappings
    {
        get
        {
            lock (_lock)
                return _clients.Values.SelectMany(c => c.Mappings.Values, (c, m) =>
                    new TcpMappingView(c.Name, c.Mode, m.Internal, m.PublicPort, m.Destination)).ToList();
        }
    }

    /// <summary>入站未命中/被过滤记录快照（负向断言与调试用）。</summary>
    public IReadOnlyList<TcpMissView> Missed
    {
        get { lock (_lock) return _missed.ToList(); }
    }

    // ── STUN-TCP 代理（导演） ──────────────────────────────────────────

    /// <summary>STUN-TCP accept 循环：注册客户端的 Binding 事务 → 分配导演端口 + 开窗 + 改写响应（事务即关）。</summary>
    private async Task StunAcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket conn;
            try { conn = await _stunListener.AcceptSocketAsync(ct); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException) { break; }

            _ = Task.Run(() => StunServeAsync(conn, ct));
        }
    }

    private async Task StunServeAsync(Socket conn, CancellationToken ct)
    {
        try
        {
            using var connScope = conn;
            var remote = (IPEndPoint)conn.RemoteEndPoint!;
            ClientNat? nat;
            lock (_lock) nat = _clients.GetValueOrDefault(remote.Address);
            if (nat is null) return; // 未注册来源不代理

            using var stream = new NetworkStream(conn, ownsSocket: false);
            using var txnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            txnCts.CancelAfter(_options.TransactionTimeout);
            var request = await StunTcpFraming.TryReadAsync(stream, txnCts.Token);
            if (request is null || !TryGetHeaderTransactionId(request, out var tid)) return;

            int pub;
            lock (_lock)
            {
                pub = AllocateNoLock(nat);
                nat.Mappings[pub] = new Mapping(nat, remote, pub, StunEndpoint, null); // 探测映射：交付目标=内部端点 listen
            }
            OpenWindow(nat, pub); // 窗口先于响应（响应触发对端打洞时窗口必须就位）
            await StunTcpFraming.WriteAsync(stream, StunCodec.BuildBindingResponse(tid, _options.PublicAddress, (ushort)pub), txnCts.Token);
        }
        catch { /* 单事务尽力而为 */ }
    }

    /// <summary>STUN 头事务 ID 提取（不校验属性——代理对任意合法 STUN 请求透明）。</summary>
    private static bool TryGetHeaderTransactionId(ReadOnlySpan<byte> wire, out byte[] tid)
    {
        tid = Array.Empty<byte>();
        if (wire.Length < StunCodec.HeaderLen) return false;
        if (BinaryPrimitives.ReadUInt32BigEndian(wire.Slice(4, 4)) != StunCodec.MagicCookie) return false;
        tid = wire.Slice(8, StunCodec.TransactionIdLen).ToArray();
        return true;
    }

    // ── 打洞窗口（导演） ───────────────────────────────────────────────

    /// <summary>在 pub..pub+PunchWindow−1 开窗监听（须在响应发出前；同段已监听则跳过——单会话子段无重叠）。</summary>
    private void OpenWindow(ClientNat nat, int pub)
    {
        for (var k = 0; k < _options.PunchWindow; k++)
        {
            var port = pub + k;
            TcpListener? listener = null;
            try
            {
                listener = new TcpListener(_options.PublicAddress, port);
                listener.Start(8);
            }
            catch (SocketException)
            {
                listener = null; // 端口被占（前窗残留/系统抢占）→ 该预测目标自然 miss
            }
            if (listener is null) continue;
            lock (_lock)
            {
                _windows[port] = new Window(listener, nat);
                _loops.Add(Task.Run(() => WindowAcceptLoopAsync(nat, port, _cts.Token)));
            }
        }
    }

    /// <summary>窗口 accept 循环：注册来源连接 = 双重角色——源侧出站（分配+记录映射）∧ 接收方 W 端口映射入站交付。</summary>
    private async Task WindowAcceptLoopAsync(ClientNat owner, int portW, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket accepted;
            try { accepted = await _windows[portW].Listener.AcceptSocketAsync(ct); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException) { break; }

            _ = Task.Run(() => HandleInboundAsync(owner, portW, accepted, ct));
        }
    }

    private async Task HandleInboundAsync(ClientNat owner, int portW, Socket accepted, CancellationToken ct)
    {
        try
        {
            var remote = (IPEndPoint)accepted.RemoteEndPoint!;
            ClientNat? srcNat;
            lock (_lock) srcNat = _clients.GetValueOrDefault(remote.Address);
            if (srcNat is null)
            {
                accepted.Dispose();
                RecordMiss(owner, "foreign", portW, "unregistered_source");
                return;
            }

            // ① 源侧出站：按 accept 序分配下一个导演端口并记录映射（真实 NAT 按 SYN 到达序分配）
            int eSrc;
            lock (_lock)
            {
                eSrc = AllocateNoLock(srcNat);
                srcNat.Mappings[eSrc] = new Mapping(srcNat, remote, eSrc, new IPEndPoint(_options.PublicAddress, portW), accepted);
            }

            // ② 接收方 W 端口映射入站交付；no_mapping/listener_refused 有界重试——模拟 SYN 重传：
            //    早到 SYN 等对侧映射/内部 listen 就绪（真实 NAT 靠 ~1s 重传获得同样容忍度）
            var deadline = Environment.TickCount64 + (long)_options.RetryWindow.TotalMilliseconds;
            while (true)
            {
                Mapping? target;
                lock (_lock) target = owner.Mappings.GetValueOrDefault(portW);
                if (target is null)
                {
                    if (await RetryOrMissAsync(owner, srcNat.Name, portW, "no_mapping", deadline, ct)) continue;
                    accepted.Dispose();
                    return;
                }

                if (target.Socket is null)
                {
                    // 探测映射 → 交付内部端点 listen（N=1 直连 STUN 端口前提；任意注册来源可入、多次可入）
                    var delivery = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await delivery.ConnectAsync(target.Internal, ct);
                    }
                    catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
                    {
                        delivery.Dispose();
                        if (await RetryOrMissAsync(owner, srcNat.Name, portW, "listener_refused", deadline, ct)) continue;
                        accepted.Dispose();
                        return;
                    }
                    await BridgeAsync(accepted, delivery);
                    return;
                }

                // 出站映射 → 精确身份过滤（APDF）：本次分配的源端口须等于映射的目的端口（对称 rendezvous）
                lock (_lock)
                {
                    if (target.Bridged)
                    {
                        accepted.Dispose();
                        RecordMiss(owner, srcNat.Name, portW, "duplicate_bridge");
                        return;
                    }
                    if (eSrc != target.Destination.Port)
                    {
                        accepted.Dispose();
                        RecordMiss(owner, srcNat.Name, portW, "filtered_identity");
                        return;
                    }
                    target.Bridged = true;
                }
                await BridgeAsync(accepted, target.Socket);
                return;
            }
        }
        catch { /* 交付尽力而为：客户端中止/停机竞态按 miss 收场 */ }
    }

    /// <summary>miss 前有界重试（true=继续重查，false=窗口耗尽已记录）。过滤身份/重复桥接是确定性拒绝，不经此路径。</summary>
    private async Task<bool> RetryOrMissAsync(ClientNat owner, string sender, int portW, string reason, long deadline, CancellationToken ct)
    {
        if (Environment.TickCount64 < deadline)
        {
            await Task.Delay(25, ct);
            return true;
        }
        RecordMiss(owner, sender, portW, reason);
        return false;
    }

    /// <summary>应用层桥接：双侧任一关闭即双侧关闭（NAT 会话终结语义）；套接字由桥接持有、停机兜底关闭。
    /// 同对套接字经双向窗口各命中一次（A 窗口与 B 窗口互为镜像）→ 幂等去重，避免双泵复读。</summary>
    private async Task BridgeAsync(Socket a, Socket b)
    {
        lock (_lock)
        {
            if (_bridged.Contains(a) || _bridged.Contains(b)) return;
            _bridged.Add(a);
            _bridged.Add(b);
        }
        try
        {
            using var sa = new NetworkStream(a, ownsSocket: true);
            using var sb = new NetworkStream(b, ownsSocket: true);
            var t1 = sa.CopyToAsync(sb);
            var t2 = sb.CopyToAsync(sa);
            await Task.WhenAny(t1, t2);
        }
        catch { /* 桥接中断：任一侧异常关闭均属正常终结 */ }
    }

    private void RecordMiss(ClientNat owner, string sender, int portW, string reason)
    {
        lock (_lock) _missed.Add(new TcpMissView(owner.Name, sender, portW, reason));
    }

    // ── 分配 ───────────────────────────────────────────────────────────

    /// <summary>分配下一个导演端口（须持锁）：Sequential 自子段基址 +1 递增；Random 子段内随机（撞已用键重抽）。</summary>
    private int AllocateNoLock(ClientNat nat)
    {
        if (nat.Mode == TcpNatMode.SymmetricRandom)
        {
            for (var attempt = 0; attempt < 16; attempt++)
            {
                var p = Random.Shared.Next(nat.SubBase, nat.SubCeiling + 1);
                if (!nat.Mappings.ContainsKey(p)) return p;
            }
        }
        if (nat.NextPort > nat.SubCeiling)
            throw new InvalidOperationException($"客户端 {nat.Name} 端口子段耗尽");
        return nat.NextPort++;
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null) return; // StartAsync 未调用
        _cts.Cancel();
        _stunListener.Stop();
        lock (_lock)
        {
            foreach (var w in _windows.Values) w.Listener.Stop();
            foreach (var s in _bridged) s.Dispose();
        }
        try { await Task.WhenAll(_loops.ToArray()); }
        catch { /* 收尾竞态：循环退出路径已吞套接字异常，防御性兜底 */ }
        _cts.Dispose();
    }

    // ── 内部状态 ───────────────────────────────────────────────────────

    private sealed class ClientNat(string name, TcpNatMode mode, IPAddress internalIp, int subBase, int subCeiling)
    {
        public string Name { get; } = name;
        public TcpNatMode Mode { get; } = mode;
        public IPAddress InternalIp { get; } = internalIp;
        public int SubBase { get; } = subBase;
        public int SubCeiling { get; } = subCeiling;
        public int NextPort { get; set; } = subBase;
        public Dictionary<int, Mapping> Mappings { get; } = [];
    }

    /// <summary>映射：Socket 为空 = 探测映射（交付内部 listen）；非空 = 出站映射（桥接对象）。</summary>
    private sealed class Mapping(ClientNat owner, IPEndPoint internalEp, int pub, IPEndPoint destination, Socket? socket)
    {
        public ClientNat Owner { get; } = owner;
        public IPEndPoint Internal { get; } = internalEp;
        public int PublicPort { get; } = pub;
        public IPEndPoint Destination { get; } = destination;
        public Socket? Socket { get; } = socket;
        public bool Bridged { get; set; }
    }

    private sealed record Window(TcpListener Listener, ClientNat Owner);
}

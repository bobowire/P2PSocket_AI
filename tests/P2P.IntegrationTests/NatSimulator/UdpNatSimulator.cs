using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using P2P.Core.Stun;

namespace P2P.IntegrationTests.NatSimulator;

/// <summary>UDP NAT 模式（09 §2.2；SymmetricSequential/TcpBlocked → M2）。</summary>
public enum UdpNatMode
{
    /// <summary>同一内部端点 → 固定外部端点，任意外部可入（打洞必成功基线）。</summary>
    FullCone,

    /// <summary>固定映射，仅允许曾出站联系过的对端 IP 入站。</summary>
    RestrictedCone,

    /// <summary>固定映射，入站须匹配曾联系过的对端 IP+端口。</summary>
    PortRestricted,

    /// <summary>每个出站目的地独立映射，外部端口随机分配（打洞必失败 → 中继回退，A-6）。</summary>
    SymmetricRandom,
}

/// <summary>NatSimulator 配置（测试注入地址，09 §2.2：客户端 STUN/打洞目标均指向模拟器）。</summary>
public sealed class UdpNatOptions
{
    /// <summary>上游真实 STUN 服务（模拟器代理其流量并改写 XOR-MAPPED-ADDRESS）。默认本机替身 127.0.0.2。</summary>
    public IPEndPoint Upstream { get; init; } = new(IPAddress.Parse("127.0.0.2"), 3478);

    /// <summary>公网侧监听地址（模拟"互联网"的回环别名；注册客户端在内网侧各用独立回环 IP）。</summary>
    public IPAddress PublicAddress { get; init; } = IPAddress.Loopback;

    /// <summary>模拟公网端口段下界（08 §4：该段需从 OS 临时端口排除）。</summary>
    public int ExternalPortBase { get; init; } = 20000;

    /// <summary>模拟公网端口段长度。</summary>
    public int ExternalPortCount { get; init; } = 1000;

    /// <summary>上游 STUN 响应等待窗（超时丢弃挂起事务）。</summary>
    public TimeSpan UpstreamTimeout { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>单条 NAT 映射的观测视图（测试断言映射身份/数量用）。</summary>
public sealed record NatMappingView(string Client, UdpNatMode Mode, IPEndPoint Internal, IPEndPoint Public, IPEndPoint Destination);

/// <summary>被过滤入站包的观测记录（负向断言即时化：不必靠静默超时）。</summary>
public sealed record FilterRecord(string Receiver, string Sender, IPEndPoint SenderIdentity, IPEndPoint Target);

/// <summary>
/// 单宿主机 UDP NAT 模拟器（09 §2.2、TD-17）：一个实例同时扮演"互联网"与全部客户端 NAT——
/// 拥有全部公网套接字（绑定 <see cref="UdpNatOptions.PublicAddress"/>，端口取自模拟端口段），
/// 代理 STUN（请求经映射公网套接字转发上游，响应按事务 ID 改写 XOR-MAPPED-ADDRESS 后回内部端点）。
/// 单宿主机上内部套接字互发会物理绕过 NAT，故发送方公网身份在入站侧惰性解析：
/// 包"语义上穿越了发送方 NAT"（映射创建/ contacted 标记照常发生），投递时从发送方公网套接字发出，
/// 接收方看到的源地址即发送方公网身份（源地址改写语义成立）。未注册来源（外部主机）无法伪造源，
/// 投递经接收侧公网套接字代发——过滤判定不受影响（矩阵测试只对注册来源断言源端口）。
/// </summary>
public sealed class UdpNatSimulator : IAsyncDisposable
{
    private readonly UdpNatOptions _options;
    private readonly Dictionary<IPEndPoint, ClientNat> _clients = [];
    private readonly Dictionary<string, PendingStun> _pending = []; // tid hex → 回程信息
    private readonly List<FilterRecord> _filtered = [];
    private readonly List<Task> _loops = [];
    private readonly object _lock = new();
    private UdpClient _stunListener = null!;
    private CancellationTokenSource _cts = null!;
    private int _nextPort;

    /// <summary>STUN 监听地址（客户端配置的"STUN 服务器"，测试注入）。</summary>
    public IPEndPoint StunEndpoint { get; private set; } = null!;

    public UdpNatSimulator(UdpNatOptions? options = null)
    {
        _options = options ?? new UdpNatOptions();
        _nextPort = _options.ExternalPortBase;
    }

    /// <summary>注册一个客户端 NAT：internalEndpoint 为其内网侧套接字地址（建议独立回环 IP）。</summary>
    public void RegisterClient(IPEndPoint internalEndpoint, UdpNatMode mode, string name)
    {
        lock (_lock)
        {
            if (_clients.ContainsKey(internalEndpoint))
                throw new InvalidOperationException($"内部端点已注册：{internalEndpoint}");
            _clients[internalEndpoint] = new ClientNat(name, mode, internalEndpoint);
        }
    }

    /// <summary>绑定 STUN 监听（客户端在此之后方可探测）。</summary>
    public Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        _stunListener = new UdpClient(new IPEndPoint(_options.PublicAddress, 0));
        StunEndpoint = (IPEndPoint)_stunListener.Client.LocalEndPoint!;
        _loops.Add(Task.Run(() => StunLoopAsync(_cts.Token)));
        return Task.CompletedTask;
    }

    /// <summary>当前全部映射快照（按创建序）。</summary>
    public IReadOnlyList<NatMappingView> Mappings
    {
        get
        {
            lock (_lock)
                return _clients.Values.SelectMany(c => c.Mappings, (c, m) =>
                    new NatMappingView(c.Name, c.Mode, c.Internal, m.Public, m.Destination)).ToList();
        }
    }

    /// <summary>被过滤入站记录快照（负向断言与调试用）。</summary>
    public IReadOnlyList<FilterRecord> Filtered
    {
        get { lock (_lock) return _filtered.ToList(); }
    }

    // ── 主循环 ─────────────────────────────────────────────────────────

    /// <summary>STUN 监听循环：注册客户端的请求经其映射公网套接字转发上游，并挂起事务等改写回程。</summary>
    private async Task StunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _stunListener.ReceiveAsync(ct); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException) { break; }

            Mapping mapping;
            lock (_lock)
            {
                if (!_clients.TryGetValue(r.RemoteEndPoint, out var nat)) continue; // 未注册来源不代理
                mapping = UseMapping(nat, StunEndpoint);
                if (TryGetHeaderTransactionId(r.Buffer, out var tid))
                {
                    PurgeExpiredPendingNoLock();
                    _pending[Convert.ToHexString(tid)] = new PendingStun(nat.Internal, mapping.Public);
                }
            }
            mapping.Socket.Send(r.Buffer, r.Buffer.Length, _options.Upstream);
        }
    }

    /// <summary>公网套接字循环：①上游 STUN 响应按 tid 改写回程；②注册/外部来源入站按接收方模式过滤后投递。</summary>
    private async Task PublicLoopAsync(Mapping m, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await m.Socket.ReceiveAsync(ct); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException) { break; }

            IPEndPoint deliverTo;
            UdpClient deliverFrom;
            lock (_lock)
            {
                // ① 上游 STUN 响应：改写 XOR-MAPPED-ADDRESS 为分配的公网身份后经 STUN 监听回客户端
                //    （客户端请求发往 StunEndpoint，回程源须一致；tid 匹配使 PTP 帧/杂包不可能误中）
                if (StunCodec.TryParseBindingResponse(r.Buffer, out var resp) &&
                    _pending.Remove(Convert.ToHexString(resp!.TransactionId), out var pend))
                {
                    var rewritten = StunCodec.BuildBindingResponse(
                        resp.TransactionId, pend.Public.Address, (ushort)pend.Public.Port);
                    _stunListener.Send(rewritten, rewritten.Length, pend.ClientInternal);
                    continue;
                }

                // ② 发送方身份解析：注册来源 → 惰性穿越发送方 NAT（映射/ contacted 照常），外部来源 → 原样地址
                IPEndPoint senderIdentity;
                string senderName;
                UdpClient? senderSocket = null;
                if (_clients.TryGetValue(r.RemoteEndPoint, out var senderNat))
                {
                    var sm = UseMapping(senderNat, m.Public);
                    senderIdentity = sm.Public;
                    senderName = senderNat.Name;
                    senderSocket = sm.Socket;
                }
                else
                {
                    senderIdentity = r.RemoteEndPoint;
                    senderName = "foreign";
                }

                // ③ 接收方（映射属主）模式过滤
                if (!IsInboundAllowed(m, senderIdentity))
                {
                    _filtered.Add(new FilterRecord(m.Owner.Name, senderName, senderIdentity, m.Public));
                    continue;
                }

                // ④ 投递：从发送方公网套接字发出 → 接收方看到的源即发送方公网身份；
                //    外部来源无法伪造源地址，经接收侧公网套接字代发
                deliverTo = m.Owner.Internal;
                deliverFrom = senderSocket ?? m.Socket;
            }
            deliverFrom.Send(r.Buffer, r.Buffer.Length, deliverTo);
        }
    }

    /// <summary>入站过滤矩阵（09 §2.2 各模式定义）。symmetric 只放行映射目的地本身——STUN 映射对外不可达，忠实于"打洞必失败"。</summary>
    private static bool IsInboundAllowed(Mapping m, IPEndPoint sender)
    {
        var contacted = m.Owner.Contacted;
        return m.Owner.Mode switch
        {
            UdpNatMode.FullCone => true,
            UdpNatMode.RestrictedCone => contacted.Any(c => c.Address.Equals(sender.Address)),
            UdpNatMode.PortRestricted => contacted.Contains(sender),
            UdpNatMode.SymmetricRandom => m.Destination.Equals(sender),
            _ => false,
        };
    }

    // ── 映射管理 ───────────────────────────────────────────────────────

    /// <summary>出站使用映射（须持锁）：cone 复用首条；symmetric 按目的地一条；同时标记 contacted。</summary>
    private Mapping UseMapping(ClientNat nat, IPEndPoint destination)
    {
        Mapping? existing;
        if (nat.Mode == UdpNatMode.SymmetricRandom)
            existing = nat.Mappings.FirstOrDefault(x => x.Destination.Equals(destination));
        else
            existing = nat.Mappings.FirstOrDefault();
        var mapping = existing ?? CreateMappingNoLock(nat, destination);
        nat.Contacted.Add(destination);
        return mapping;
    }

    /// <summary>创建映射（须持锁）：绑定公网套接字（cone 顺序分配 / symmetric 随机）并启动入站循环。</summary>
    private Mapping CreateMappingNoLock(ClientNat nat, IPEndPoint destination)
    {
        var socket = BindPublicSocket(nat.Mode);
        var mapping = new Mapping(nat, socket, (IPEndPoint)socket.Client.LocalEndPoint!, destination);
        nat.Mappings.Add(mapping);
        _loops.Add(Task.Run(() => PublicLoopAsync(mapping, _cts.Token)));
        return mapping;
    }

    private UdpClient BindPublicSocket(UdpNatMode mode)
    {
        for (var attempt = 0; attempt < _options.ExternalPortCount; attempt++)
        {
            var port = mode == UdpNatMode.SymmetricRandom
                ? Random.Shared.Next(_options.ExternalPortBase, _options.ExternalPortBase + _options.ExternalPortCount)
                : _nextPort++;
            if (_nextPort >= _options.ExternalPortBase + _options.ExternalPortCount) _nextPort = _options.ExternalPortBase;
            try { return new UdpClient(new IPEndPoint(_options.PublicAddress, port)); }
            catch (SocketException) { /* 端口被占（系统抢占/他测残留）→ 顺延重试 */ }
        }
        throw new InvalidOperationException("模拟公网端口段耗尽");
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

    private void PurgeExpiredPendingNoLock()
    {
        var cutoff = DateTime.UtcNow - _options.UpstreamTimeout;
        foreach (var (tid, p) in _pending.Where(kv => kv.Value.CreatedAt < cutoff).Select(kv => (kv.Key, kv.Value)).ToList())
            _pending.Remove(tid);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null) return; // StartAsync 未调用
        _cts.Cancel();
        _stunListener.Dispose();
        lock (_lock)
            foreach (var m in _clients.Values.SelectMany(c => c.Mappings))
                m.Socket.Dispose();
        try { await Task.WhenAll(_loops.ToArray()); }
        catch { /* 收尾竞态：循环退出路径已吞套接字异常，防御性兜底 */ }
        _cts.Dispose();
    }

    // ── 内部状态 ───────────────────────────────────────────────────────

    private sealed class ClientNat(string name, UdpNatMode mode, IPEndPoint internalEp)
    {
        public string Name { get; } = name;
        public UdpNatMode Mode { get; } = mode;
        public IPEndPoint Internal { get; } = internalEp;
        public List<Mapping> Mappings { get; } = [];
        public HashSet<IPEndPoint> Contacted { get; } = [];
    }

    private sealed class Mapping(ClientNat owner, UdpClient socket, IPEndPoint pub, IPEndPoint destination)
    {
        public ClientNat Owner { get; } = owner;
        public UdpClient Socket { get; } = socket;
        public IPEndPoint Public { get; } = pub;
        public IPEndPoint Destination { get; } = destination;
    }

    private sealed record PendingStun(IPEndPoint ClientInternal, IPEndPoint Public)
    {
        public DateTime CreatedAt { get; } = DateTime.UtcNow;
    }
}

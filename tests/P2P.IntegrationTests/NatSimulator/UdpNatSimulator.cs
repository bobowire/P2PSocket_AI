using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using P2P.Core.Stun;

namespace P2P.IntegrationTests.NatSimulator;

/// <summary>UDP NAT 模式（09 §2.2）。</summary>
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

    /// <summary>每个出站目的地独立映射，外部端口 +1 递增——与 TCP 端口预测同构的 UDP 变体（M2-30）。</summary>
    SymmetricSequential,

    /// <summary>丢弃全部 UDP 出站（不建映射）——迫使 TCP 打洞/TCP 中继承载路径（FR-S-704 场景，M2-30）。</summary>
    UdpBlocked,
}

/// <summary>NatSimulator 配置（测试注入地址，09 §2.2：客户端 STUN/打洞目标均指向模拟器）。</summary>
public sealed class UdpNatOptions
{
    /// <summary>上游真实 STUN 服务（模拟器代理其流量并改写 XOR-MAPPED-ADDRESS）。默认本机替身 127.0.0.2。</summary>
    public IPEndPoint Upstream { get; init; } = new(IPAddress.Parse("127.0.0.2"), 3478);

    /// <summary>公网侧监听地址（模拟"互联网"的回环别名；注册客户端在内网侧各用独立回环 IP）。</summary>
    public IPAddress PublicAddress { get; init; } = IPAddress.Loopback;

    /// <summary>STUN 监听端口（0=随机）。TD-07 客户端 STUN 端点=控制地址主机+:3478——
    /// 控制服务与本模拟器同宿主时须置 3478 才能被真实派生逻辑命中（M1-35 A-3）。</summary>
    public int StunPort { get; init; }

    /// <summary>M3-15 双地址世界：辅上游（真实 StunService 的 alt 端点）。null=单地址世界——
    /// 响应不带 RFC5780 属性、无辅监听，行为与既往逐字节一致。</summary>
    public IPEndPoint? AltUpstream { get; init; }

    /// <summary>辅监听公网地址（须与主地址不同 IP——回环别名构造异 IP 世界，EIF/ADF 才可细分）。</summary>
    public IPAddress AltPublicAddress { get; init; } = IPAddress.Parse("127.0.0.2");

    /// <summary>辅 STUN 监听端口（0=随机）。</summary>
    public int AltStunPort { get; init; }

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

/// <summary>UdpBlocked 客户端出站被丢弃的观测记录（负向断言即时化）。</summary>
public sealed record BlockedRecord(string Client, IPEndPoint Target);

/// <summary>
/// 单宿主机 UDP NAT 模拟器（09 §2.2、TD-17）：一个实例同时扮演"互联网"与全部客户端 NAT——
/// 拥有全部公网套接字（绑定 <see cref="UdpNatOptions.PublicAddress"/>，端口取自模拟端口段），
/// 代理 STUN（请求经映射公网套接字转发上游，响应按事务 ID 改写 XOR-MAPPED-ADDRESS 后回内部端点）。
/// 客户端 NAT 按**内网 IP**注册（一台设备一个 NAT，设备内多个 socket 共享——映射仍按内部端点区分，
/// 与真实 NAT 一致）；单宿主机上内部套接字互发会物理绕过 NAT，故发送方公网身份在入站侧惰性解析：
/// 包"语义上穿越了发送方 NAT"（映射创建/contacted 标记照常发生），投递时从发送方公网套接字发出，
/// 接收方看到的源地址即发送方公网身份（源地址改写语义成立）。未注册来源（外部主机）无法伪造源，
/// 投递经接收侧公网套接字代发——过滤判定不受影响（矩阵测试只对注册来源断言源端口）。
/// </summary>
public sealed class UdpNatSimulator : IAsyncDisposable
{
    private readonly UdpNatOptions _options;
    private readonly Dictionary<IPAddress, ClientNat> _clients = [];
    private readonly Dictionary<string, PendingStun> _pending = []; // tid hex → 回程信息
    private readonly List<FilterRecord> _filtered = [];
    private readonly List<BlockedRecord> _blocked = [];
    private readonly List<Task> _loops = [];
    private readonly object _lock = new();
    private UdpClient _stunListener = null!;
    private UdpClient? _altStunListener;
    private CancellationTokenSource _cts = null!;
    private int _nextPort;

    /// <summary>STUN 监听地址（客户端配置的"STUN 服务器"，测试注入）。</summary>
    public IPEndPoint StunEndpoint { get; private set; } = null!;

    /// <summary>辅 STUN 监听地址（M3-15 双地址世界；AltUpstream 未配置=null）。</summary>
    public IPEndPoint? AltStunEndpoint { get; private set; }

    public UdpNatSimulator(UdpNatOptions? options = null)
    {
        _options = options ?? new UdpNatOptions();
        _nextPort = _options.ExternalPortBase;
    }

    /// <summary>注册一个客户端 NAT：internalIp 为其内网侧地址（建议独立回环 IP）。</summary>
    public void RegisterClient(IPAddress internalIp, UdpNatMode mode, string name)
    {
        lock (_lock)
        {
            if (_clients.ContainsKey(internalIp))
                throw new InvalidOperationException($"内网 IP 已注册：{internalIp}");
            _clients[internalIp] = new ClientNat(name, mode, internalIp);
        }
    }

    /// <summary>绑定 STUN 监听（客户端在此之后方可探测）；双地址世界同时绑辅监听。</summary>
    public Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        _stunListener = new UdpClient(new IPEndPoint(_options.PublicAddress, _options.StunPort));
        StunEndpoint = (IPEndPoint)_stunListener.Client.LocalEndPoint!;
        if (_options.AltUpstream is not null)
        {
            _altStunListener = new UdpClient(new IPEndPoint(_options.AltPublicAddress, _options.AltStunPort));
            AltStunEndpoint = (IPEndPoint)_altStunListener.Client.LocalEndPoint!;
        }
        _loops.Add(Task.Run(() => StunLoopAsync(_stunListener, StunEndpoint, _options.Upstream, _cts.Token)));
        if (_altStunListener is not null)
            _loops.Add(Task.Run(() => StunLoopAsync(_altStunListener, AltStunEndpoint!, _options.AltUpstream!, _cts.Token)));
        return Task.CompletedTask;
    }

    /// <summary>当前全部映射快照（按创建序）。</summary>
    public IReadOnlyList<NatMappingView> Mappings
    {
        get
        {
            lock (_lock)
                return _clients.Values.SelectMany(c => c.Mappings, (c, m) =>
                    new NatMappingView(c.Name, c.Mode, m.Internal, m.Public, m.Destination)).ToList();
        }
    }

    /// <summary>被过滤入站记录快照（负向断言与调试用）。</summary>
    public IReadOnlyList<FilterRecord> Filtered
    {
        get { lock (_lock) return _filtered.ToList(); }
    }

    /// <summary>UdpBlocked 出站丢弃记录快照（负向断言与调试用）。</summary>
    public IReadOnlyList<BlockedRecord> Blocked
    {
        get { lock (_lock) return _blocked.ToList(); }
    }

    // ── 主循环 ─────────────────────────────────────────────────────────

    /// <summary>STUN 监听循环（主/辅共用）：注册客户端的请求经其映射公网套接字转发对应上游，
    /// 并挂起事务等改写回程（localEp=该监听端点——映射目的地与 contacted 标记据此区分主辅）。</summary>
    private async Task StunLoopAsync(UdpClient listener, IPEndPoint localEp, IPEndPoint upstream, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await listener.ReceiveAsync(ct); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException) { break; }

            Mapping mapping;
            lock (_lock)
            {
                if (!_clients.TryGetValue(r.RemoteEndPoint.Address, out var nat)) continue; // 未注册来源不代理
                if (nat.Mode == UdpNatMode.UdpBlocked) // 丢弃 UDP 出站：不建映射、不转发（M2-30）
                {
                    _blocked.Add(new BlockedRecord(nat.Name, localEp));
                    continue;
                }
                mapping = UseMapping(nat, r.RemoteEndPoint, localEp);
                if (TryGetHeaderTransactionId(r.Buffer, out var tid))
                {
                    PurgeExpiredPendingNoLock();
                    _pending[Convert.ToHexString(tid)] = new PendingStun(r.RemoteEndPoint, mapping.Public);
                }
            }
            mapping.Socket.Send(r.Buffer, r.Buffer.Length, upstream);
        }
    }

    /// <summary>公网套接字循环：①上游 STUN 响应按 tid 改写回程（M3-15 双地址：过接收方 NAT 入站
    /// 过滤+RFC5780 属性改写+回程监听切换）；②注册/外部来源入站按接收方模式过滤后投递。</summary>
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
                // ① 上游 STUN 响应：改写 XOR-MAPPED-ADDRESS 为分配的公网身份后回客户端。
                //    回程源=该事务请求所发往的模拟端点（fromAlt 按上游响应来源判定）；
                //    tid 匹配使 PTP 帧/杂包不可能误中。
                //    M3-15 回程过滤：正常回包（源=事务目的地）全模式放行（向后兼容——单地址世界
                //    行为不变）；change 回包（源=sim 辅，RFC5780 filtering 测试）按接收方模式矩阵
                //    判（FullCone 收 → EIF、Restricted 族丢 → ADF·APDF、symmetric 恒丢）。
                if (StunCodec.TryParseBindingResponse(r.Buffer, out var resp) &&
                    _pending.Remove(Convert.ToHexString(resp!.TransactionId), out var pend))
                {
                    var fromAlt = AltStunEndpoint is not null && r.RemoteEndPoint.Equals(_options.AltUpstream);
                    var simSource = fromAlt ? AltStunEndpoint! : StunEndpoint;
                    if (!IsInboundAllowed(m, simSource))
                    {
                        _filtered.Add(new FilterRecord(m.Owner.Name, fromAlt ? "stun-alt" : "stun", simSource, m.Public));
                        continue;
                    }
                    // RFC5780 属性（相对语义）：主回包通告辅端点 / 辅回包通告主端点；单地址世界不带属性
                    var other = fromAlt ? StunEndpoint : AltStunEndpoint;
                    var rewritten = StunCodec.BuildBindingResponse(
                        resp.TransactionId, pend.Public.Address, (ushort)pend.Public.Port,
                        otherAddress: other, responseOrigin: other is null ? null : simSource);
                    var replyListener = fromAlt ? _altStunListener! : _stunListener;
                    replyListener.Send(rewritten, rewritten.Length, pend.ClientInternal);
                    continue;
                }

                // ② 发送方身份解析：注册来源 → 惰性穿越发送方 NAT（映射/contacted 照常），外部来源 → 原样地址
                IPEndPoint senderIdentity;
                string senderName;
                UdpClient? senderSocket = null;
                if (_clients.TryGetValue(r.RemoteEndPoint.Address, out var senderNat))
                {
                    if (senderNat.Mode == UdpNatMode.UdpBlocked) // 丢弃 UDP 出站：穿越发送方 NAT 前拦截（M2-30）
                    {
                        _blocked.Add(new BlockedRecord(senderNat.Name, m.Public));
                        continue;
                    }
                    var sm = UseMapping(senderNat, r.RemoteEndPoint, m.Public);
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
                deliverTo = m.Internal;
                deliverFrom = senderSocket ?? m.Socket;
            }
            deliverFrom.Send(r.Buffer, r.Buffer.Length, deliverTo);
        }
    }

    /// <summary>入站过滤矩阵（09 §2.2 各模式定义）。symmetric（Random/Sequential）只放行映射目的地本身——STUN 映射对外不可达，忠实于"打洞必失败"。</summary>
    private static bool IsInboundAllowed(Mapping m, IPEndPoint sender)
    {
        var contacted = m.Owner.Contacted;
        return m.Owner.Mode switch
        {
            UdpNatMode.FullCone => true,
            UdpNatMode.RestrictedCone => contacted.Any(c => c.Address.Equals(sender.Address)),
            UdpNatMode.PortRestricted => contacted.Contains(sender),
            UdpNatMode.SymmetricRandom => m.Destination.Equals(sender),
            UdpNatMode.SymmetricSequential => m.Destination.Equals(sender),
            _ => false,
        };
    }

    // ── 映射管理 ───────────────────────────────────────────────────────

    /// <summary>出站使用映射（须持锁）：同一内部端点 cone 固定一条、symmetric（Random/Sequential）按目的地一条（真实 NAT 语义）；同时标记 contacted。</summary>
    private Mapping UseMapping(ClientNat nat, IPEndPoint internalEndpoint, IPEndPoint destination)
    {
        var symmetric = nat.Mode is UdpNatMode.SymmetricRandom or UdpNatMode.SymmetricSequential;
        var existing = symmetric
            ? nat.Mappings.FirstOrDefault(x => x.Internal.Equals(internalEndpoint) && x.Destination.Equals(destination))
            : nat.Mappings.FirstOrDefault(x => x.Internal.Equals(internalEndpoint));
        var mapping = existing ?? CreateMappingNoLock(nat, internalEndpoint, destination);
        nat.Contacted.Add(destination);
        return mapping;
    }

    /// <summary>创建映射（须持锁）：绑定公网套接字（cone 顺序分配 / symmetric 随机）并启动入站循环。</summary>
    private Mapping CreateMappingNoLock(ClientNat nat, IPEndPoint internalEndpoint, IPEndPoint destination)
    {
        var socket = BindPublicSocket(nat.Mode);
        var mapping = new Mapping(nat, internalEndpoint, socket, (IPEndPoint)socket.Client.LocalEndPoint!, destination);
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
        _altStunListener?.Dispose();
        lock (_lock)
            foreach (var m in _clients.Values.SelectMany(c => c.Mappings))
                m.Socket.Dispose();
        try { await Task.WhenAll(_loops.ToArray()); }
        catch { /* 收尾竞态：循环退出路径已吞套接字异常，防御性兜底 */ }
        _cts.Dispose();
    }

    // ── 内部状态 ───────────────────────────────────────────────────────

    private sealed class ClientNat(string name, UdpNatMode mode, IPAddress internalIp)
    {
        public string Name { get; } = name;
        public UdpNatMode Mode { get; } = mode;
        public IPAddress InternalIp { get; } = internalIp;
        public List<Mapping> Mappings { get; } = [];
        public HashSet<IPEndPoint> Contacted { get; } = [];
    }

    private sealed class Mapping(ClientNat owner, IPEndPoint internalEp, UdpClient socket, IPEndPoint pub, IPEndPoint destination)
    {
        public ClientNat Owner { get; } = owner;
        public IPEndPoint Internal { get; } = internalEp;
        public UdpClient Socket { get; } = socket;
        public IPEndPoint Public { get; } = pub;
        public IPEndPoint Destination { get; } = destination;
    }

    private sealed record PendingStun(IPEndPoint ClientInternal, IPEndPoint Public)
    {
        public DateTime CreatedAt { get; } = DateTime.UtcNow;
    }
}

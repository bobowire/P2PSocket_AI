using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using P2P.Core.Stun;
using P2P.Server.Data;

namespace P2P.Server.Services;

/// <summary>
/// STUN-R 服务（02 §3、05 §7）：UDP（FR-S-601）3478 单 socket 收发循环 + TCP（FR-S-602）
/// 3478/TCP 短事务（收 Binding Request 即回即关，02 §3.3 定界）。DEVICE-AUTH 校验
/// （闸②查表先于验签 → HMAC → ts ±120s → nonce 去重 5min，nonce 缓存两运输层共用——
/// 跨 UDP/TCP 重放同被拦截）→ XOR-MAPPED-ADDRESS 回包。校验不通过一律静默丢弃
/// （UDP 不回应即无回显放大；TCP 直接断连）。风暴防护四道闸（TD-18/FR-S-603）
/// 由 <see cref="Guard"/> 承载：④ 全局熔断 → ① 单 IP（UDP 令牌桶/TCP 并发）→
/// ②（本服务查表）→ ③ 每设备 QPS，逐层先于 HMAC。
/// M3-15 RFC5780 判型子集（05 §7.2）：alt 端点配置时增辅监听（UDP+TCP 同端口），
/// 全部响应携带 RESPONSE-ORIGIN（实际回包源）与 OTHER-ADDRESS（另一端点，相对语义）；
/// CHANGE-REQUEST 请求从辅 socket 回包（TCP 连接导向忽略该属性）。未配置 alt 时
/// 输出与 M1 形状逐字节一致（零属性零回归）。
/// </summary>
public sealed class StunService : IAsyncDisposable
{
    public const int DefaultPort = 3478;
    public static readonly TimeSpan TsWindow = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan NonceTtl = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan TcpTransactionTimeout = TimeSpan.FromSeconds(3);

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly bool _requireAuth; // stun_auth=0 仅测试环境关闭（05 §7）
    private readonly TimeProvider _time;
    private readonly StunGuard _guard;
    private readonly CancellationTokenSource _cts = new();
    private readonly System.Threading.Lock _nonceGate = new();
    private readonly Dictionary<string, long> _nonces = []; // nonce(b64) → 过期时刻 ticks
    private readonly Task _sweeper;
    private readonly IPEndPoint? _alt; // 配置的辅端点（地址=通告 IP；端口 0=UDP 随机 TCP 跟随）
    private readonly IPAddress? _advertisedIp; // 主侧通告 IP：public_addr（M2-36 同语义）优先，空=按对端路由派生
    private UdpClient _udp = null!;
    private TcpListener _tcpListener = null!;
    private UdpClient? _altUdp;
    private TcpListener? _altTcpListener;
    private Task _loop = Task.CompletedTask;
    private Task _tcpLoop = Task.CompletedTask;
    private Task _altLoop = Task.CompletedTask;
    private Task _altTcpLoop = Task.CompletedTask;
    private int _port;
    private int _tcpPort;
    private int _disposed; // 宿主 StopAsync 与容器释放各调一次（幂等）

    public StunService(IDbContextFactory<AppDbContext> dbFactory, bool requireAuth = true,
        TimeProvider? time = null, StunGuardOptions? guard = null, IPEndPoint? alt = null)
    {
        _dbFactory = dbFactory;
        _requireAuth = requireAuth;
        _time = time ?? TimeProvider.System;
        _guard = new StunGuard(guard, _time);
        _alt = alt;
        if (alt is not null)
        {
            // 主侧通告 IP：public_addr 是 IP 时直接采用（云 NAT 部署派生会得内网地址，M2-36 同问题域）；
            // 域名形式不适配 STUN 地址属性语义（属性承载 IP），走派生兜底。
            using var db = _dbFactory.CreateDbContext();
            if (IPAddress.TryParse(new ServerConfigStore(db).Get("public_addr"), out var advertised))
                _advertisedIp = advertised;
        }
        _sweeper = SweepAsync(_cts.Token);
    }

    /// <summary>UDP 实际绑定端口（0 → 系统分配，测试用）。</summary>
    public int Port => _port;

    /// <summary>TCP 实际绑定端口（同上）。</summary>
    public int TcpPort => _tcpPort;

    /// <summary>辅端点通告值（未配置 alt=null）：地址取配置 IP，端口=辅 UDP 实际端口（TCP 同口跟随，单一通告值）。</summary>
    public IPEndPoint? AltEndpoint { get; private set; }

    /// <summary>四道闸（丢弃计数经 <see cref="StunGuard.Snapshot"/> 读取，测试与仪表盘消费）。</summary>
    public StunGuard Guard => _guard;

    public Task StartAsync(int udpPort, int tcpPort, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_cts.IsCancellationRequested, this);
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, udpPort));
        _port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        _tcpListener = new TcpListener(IPAddress.Any, tcpPort);
        _tcpListener.Start(64);
        _tcpPort = ((IPEndPoint)_tcpListener.LocalEndpoint!).Port;
        if (_alt is not null)
        {
            // 辅端点（M3-15）：UDP 先绑（配置端口或 0 随机）→ TCP 跟随 UDP 实际端口（单一通告值）
            _altUdp = new UdpClient(AddressFamily.InterNetwork);
            _altUdp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _altUdp.Client.Bind(new IPEndPoint(IPAddress.Any, _alt.Port));
            var altPort = ((IPEndPoint)_altUdp.Client.LocalEndPoint!).Port;
            _altTcpListener = new TcpListener(IPAddress.Any, altPort);
            _altTcpListener.Start(64);
            AltEndpoint = new IPEndPoint(_alt.Address, altPort);
            _altLoop = AltReceiveLoopAsync(_cts.Token);
            _altTcpLoop = TcpAcceptLoopAsync(_altTcpListener, fromAlt: true, _cts.Token);
        }
        _loop = ReceiveLoopAsync(_cts.Token);
        _tcpLoop = TcpAcceptLoopAsync(_tcpListener, fromAlt: false, _cts.Token);
        return Task.CompletedTask;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var datagram = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
                await HandleAsync(datagram).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* 停机 */ }
        catch (ObjectDisposedException) { /* 停机 */ }
        catch (SocketException) { /* 停机 */ }
    }

    private async Task AltReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var datagram = await _altUdp!.ReceiveAsync(ct).ConfigureAwait(false);
                await HandleAltAsync(datagram).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* 停机 */ }
        catch (ObjectDisposedException) { /* 停机 */ }
        catch (SocketException) { /* 停机 */ }
    }

    private async Task HandleAsync(UdpReceiveResult datagram)
    {
        var wire = datagram.Buffer;
        if (wire.Length < StunCodec.HeaderLen) return;

        // 四道闸（TD-18）：④ 熔断 → ① 单 IP 令牌桶（与 auth 开关无关，纯风暴防护）
        if (!_guard.AdmitArrival()) return;
        if (!_guard.TryAcquireUdp(datagram.RemoteEndPoint.Address)) return;

        StunCodec.DeviceAuth? auth = null;
        if (_requireAuth && !TryAuthorize(wire, out auth)) return; // 闸② + DEVICE-AUTH（失败计 reason=auth）
        if (auth is not null && !_guard.TryAcquireDevice(auth.DeviceId)) return; // 闸③ 每设备 QPS

        // CHANGE-REQUEST（M3-15）：任一 flag 置位 → 从辅 socket 回包（RFC5780 filtering 测试）；
        // 未配置 alt 时忽略该属性（单 IP 降级，客户端据 OTHER-ADDRESS 缺席判降级）
        var fromAlt = _altUdp is not null && StunCodec.ParseChangeRequest(wire) != 0;
        var response = BuildResponse(
            wire.AsSpan(8, StunCodec.TransactionIdLen), datagram.RemoteEndPoint, fromAlt);
        try
        {
            var sender = fromAlt ? _altUdp! : _udp;
            await sender.SendAsync(response, datagram.RemoteEndPoint).ConfigureAwait(false);
        }
        catch (SocketException) { /* 对端口已换：丢弃 */ }
    }

    /// <summary>辅监听请求处理（M3-15）：闸+认证与主监听共用（nonce 缓存亦共用），
    /// 正常从辅回包——OTHER-ADDRESS 相对语义=通告主端点（RFC5780：辅 socket 即"另一端点"视角）。</summary>
    private async Task HandleAltAsync(UdpReceiveResult datagram)
    {
        var wire = datagram.Buffer;
        if (wire.Length < StunCodec.HeaderLen) return;

        if (!_guard.AdmitArrival()) return;
        if (!_guard.TryAcquireUdp(datagram.RemoteEndPoint.Address)) return;

        StunCodec.DeviceAuth? auth = null;
        if (_requireAuth && !TryAuthorize(wire, out auth)) return;
        if (auth is not null && !_guard.TryAcquireDevice(auth.DeviceId)) return;

        var response = BuildResponse(
            wire.AsSpan(8, StunCodec.TransactionIdLen), datagram.RemoteEndPoint, fromAlt: true);
        try { await _altUdp!.SendAsync(response, datagram.RemoteEndPoint).ConfigureAwait(false); }
        catch (SocketException) { /* 对端口已换：丢弃 */ }
    }

    /// <summary>Binding 响应组包（M3-15）：alt 未配置=M1 形状（仅 XOR-MAPPED）；
    /// 配置后携带 RESPONSE-ORIGIN（实际回包源）与 OTHER-ADDRESS（另一端点，相对语义）。</summary>
    private byte[] BuildResponse(ReadOnlySpan<byte> transactionId, IPEndPoint remote, bool fromAlt)
    {
        if (AltEndpoint is null)
            return StunCodec.BuildBindingResponse(transactionId, remote.Address, (ushort)remote.Port);
        var primary = AdvertisedPrimary(remote);
        return StunCodec.BuildBindingResponse(transactionId, remote.Address, (ushort)remote.Port,
            otherAddress: fromAlt ? primary : AltEndpoint,
            responseOrigin: fromAlt ? AltEndpoint : primary);
    }

    /// <summary>主侧通告端点：public_addr（启动期读取）优先；未配置时按对端地址探路由源 IP
    /// （临时 UDP connect 仅借路由表选源不发包，多网卡/公网直连场景取真实出口地址）。</summary>
    private IPEndPoint AdvertisedPrimary(IPEndPoint remote)
        => new(_advertisedIp ?? PickSourceAddress(remote.Address), _port);

    private static IPAddress PickSourceAddress(IPAddress destination)
    {
        try
        {
            using var probe = new UdpClient(AddressFamily.InterNetwork);
            probe.Client.Connect(destination, 9); // discard 端口：只选路由不发包
            return ((IPEndPoint)probe.Client.LocalEndPoint!).Address;
        }
        catch (SocketException) { return IPAddress.Any; } // 无路由极端场景：通告值退化（客户端比对自然失败）
    }

    // ── STUN over TCP（FR-S-602）：短事务 ─────────────────────────────

    private async Task TcpAcceptLoopAsync(TcpListener listener, bool fromAlt, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Socket conn;
                try { conn = await listener.AcceptSocketAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException) { break; }

                _ = Task.Run(() => ServeTcpAsync(conn, fromAlt, ct), ct);
            }
        }
        catch (OperationCanceledException) { /* 停机 */ }
    }

    /// <summary>单连接短事务：闸④ → 闸① 并发 → 读一事务（3s 超时防慢速连接占坑）→
    /// 闸②+DEVICE-AUTH → 闸③ → 回 Binding Response 即关（02 §3.3）。
    /// 协议错/对端中断直接断连不回应（无回显放大，02 §3.2 精神）。
    /// M3-15：连接导向，从哪条 listener 接入就从哪回（忽略 CHANGE-REQUEST），响应携带两属性。</summary>
    private async Task ServeTcpAsync(Socket conn, bool fromAlt, CancellationToken ct)
    {
        var remote = (IPEndPoint)conn.RemoteEndPoint!;
        if (!_guard.AdmitArrival()) { conn.Dispose(); return; }
        if (!_guard.TryAcquireTcp(remote.Address)) { conn.Dispose(); return; }
        try
        {
            await using var stream = new NetworkStream(conn, ownsSocket: true); // 处理器即套接字唯一持有者
            using var txn = CancellationTokenSource.CreateLinkedTokenSource(ct);
            txn.CancelAfter(TcpTransactionTimeout);
            var wire = await StunTcpFraming.TryReadAsync(stream, txn.Token).ConfigureAwait(false);
            if (wire is null) return; // 消息边界处干净关闭（客户端"连而不发即关"探测惯例）

            StunCodec.DeviceAuth? auth = null;
            if (_requireAuth && !TryAuthorize(wire, out auth)) return;
            if (auth is not null && !_guard.TryAcquireDevice(auth.DeviceId)) return;

            var response = BuildResponse(
                wire.AsSpan(8, StunCodec.TransactionIdLen), remote, fromAlt);
            await StunTcpFraming.WriteAsync(stream, response, txn.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or SocketException
            or ObjectDisposedException or OperationCanceledException)
        {
            // 协议错（msgLen 超限/中途断开）/对端中止/3s 超时/停机：短事务断连收场
        }
        finally { _guard.ReleaseTcp(remote.Address); }
    }

    /// <summary>DEVICE-AUTH 全链（闸②查表在 <see cref="StunCodec.TryParseDeviceAuth"/> 的
    /// secret 回调内先于 HMAC；任一步失败计 reason=auth 并拒）。</summary>
    private bool TryAuthorize(ReadOnlySpan<byte> wire, out StunCodec.DeviceAuth? auth)
    {
        auth = null;
        if (!StunCodec.TryParseDeviceAuth(wire, LookupSecret)) { _guard.CountAuth(); return false; }

        var parsed = StunCodec.ParseDeviceAuthFields(wire)!;
        // ts 窗口 ±120s（客户端以控制连接 offset 校准后组包，OQ-12）
        var nowMs = _time.GetLocalNow().ToUnixTimeMilliseconds();
        var drift = nowMs > (long)parsed.TsMs ? nowMs - (long)parsed.TsMs : (long)parsed.TsMs - nowMs;
        if (drift > TsWindow.TotalMilliseconds) { _guard.CountAuth(); return false; }

        // nonce 去重 5min（防重放；transactionId 由 HMAC 绑定无法替换；UDP/TCP 共用缓存）
        if (!TryAddNonce(parsed.Nonce)) { _guard.CountAuth(); return false; }

        auth = parsed;
        return true;
    }

    /// <summary>查表先于验签（05 §7 闸②语义：未注册 deviceId 直接丢，无 HMAC 消耗）。</summary>
    private byte[]? LookupSecret(Guid deviceId)
    {
        using var db = _dbFactory.CreateDbContext();
        return db.Devices.AsNoTracking()
            .Where(d => d.Id == deviceId && !d.Disabled)
            .Select(d => d.DeviceSecret)
            .SingleOrDefault();
    }

    // ── nonce 缓存（容量上限 + 周期清扫，防重放窗口 5min）─────────────

    private bool TryAddNonce(byte[] nonce)
    {
        var key = Convert.ToBase64String(nonce);
        var now = _time.GetLocalNow().UtcTicks;
        lock (_nonceGate)
        {
            if (_nonces.TryGetValue(key, out var expiry) && expiry > now) return false; // 5min 内重放
            _nonces[key] = now + NonceTtl.Ticks;
            if (_nonces.Count > 65_536) // 容量兜底：清扫过期项（正常速率远达不到）
                _nonces.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList()
                    .ForEach(k => _nonces.Remove(k));
            return true;
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), _time);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var now = _time.GetLocalNow().UtcTicks;
                lock (_nonceGate)
                {
                    foreach (var key in _nonces.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
                        _nonces.Remove(key);
                }
            }
        }
        catch (OperationCanceledException) { /* 停机 */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await _cts.CancelAsync().ConfigureAwait(false);
        _udp?.Close();
        if (_tcpListener is not null) _tcpListener.Stop();
        _altUdp?.Close();
        if (_altTcpListener is not null) _altTcpListener.Stop();
        try { await _loop.ConfigureAwait(false); } catch { }
        try { await _tcpLoop.ConfigureAwait(false); } catch { }
        try { await _altLoop.ConfigureAwait(false); } catch { }
        try { await _altTcpLoop.ConfigureAwait(false); } catch { }
        try { await _sweeper.ConfigureAwait(false); } catch { }
        _udp?.Dispose();
        _altUdp?.Dispose();
        _cts.Dispose();
    }
}

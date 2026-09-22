using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using P2P.Server.Data;

namespace P2P.Server.Services;

/// <summary>RelayService 可调参数（08 §5.1 relay.idleTimeoutSec）。</summary>
public sealed class RelayServiceOptions
{
    /// <summary>会话空闲回收（02 §6.2：任端 90s 无包即回收——客户端 KEEPALIVE 维持）。</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(90);
}

/// <summary>中继运行统计（FR-S-810 仪表盘数据源，M3 展示）。</summary>
public sealed record RelayStats(int Sessions, long BytesForwarded, long Reaped);

/// <summary>
/// 中继服务（02 §6、05 §6，FR-S-701/702/704，TD-11 零解密）：UDP（单 socket 收发循环）+
/// TCP（每连接转发对，u16 小端分帧）双承载。**只解析外层 8B 会话头
/// [u64 relaySessionId 小端]**，剥离后转发 PTP 密文帧——无任何密钥，帧内容不可解（SEC-11）。
/// 0x74 RelayAllocate：relay_enabled 校验（关 → 5002）→ 台账解析打洞会话设备对 →
/// 分配 relaySessionId → 向双方下发 RelayGrant（端点=各自控制连接本地侧地址：
/// relayPorts[0]/UDP、relayPorts[1]/TCP）。地址学习：RELAY_JOIN 按源端点认领端槽
/// （已学地址精确匹配刷新 → 空槽按控制源 IP 偏好 → 先到先得；双端同 IP 时先 JOIN 者入 A 槽——
/// 槽位仅作地址归属，转发"另一端"与标签无关）；数据包源地址不匹配两端即丢弃（保守不学习，防劫持）。
/// 回收：任端 90s 空闲 / TCP 端断连即收会话（02 §6.2）；字节计数累计。
/// </summary>
public sealed class RelayService : IAsyncDisposable
{
    public const byte JoinPrefix = 0x01;
    public const byte JoinAckPrefix = 0x02;
    public const int HeaderLen = 8;   // 外层会话头（TD-11 唯一解析点）
    public const int JoinLen = 1 + 8 + 4; // [0x01][sid][nonce]（nonce 仅保形，不参与语义）
    /// <summary>RLP-TCP 帧载荷上限 = 8B 头 + PTP 帧上限（16B 头 + 16KiB DATA + 16B tag，02 §4.2）。
    /// UDP 数据包 ≥ 8+32B（PTP 帧最小 32B），与 13B 的 JOIN 无前缀歧义。</summary>
    public const int MaxFrameLen = HeaderLen + 16 + 16 * 1024 + 16;
    public static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(10); // TCP 首帧 JOIN 须及时（防占坑）

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly DeviceRegistry _registry;
    private readonly Func<Guid, (Guid InitiatorId, Guid TargetId)?>? _peerResolver; // SignalingCoordinator 台账
    private readonly RelayServiceOptions _options;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<ulong, RelayEntry> _table = []; // relaySessionId → 会话
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _reaper;
    private readonly SemaphoreSlim _udpSend = new(1, 1); // UDP 发送串行化（多来源转发共用单 socket）
    private UdpClient _udp = null!;
    private TcpListener _tcpListener = null!;
    private Task _udpLoop = Task.CompletedTask;
    private Task _tcpLoop = Task.CompletedTask;
    private long _bytesForwarded;
    private long _reaped;
    private int _disposed; // 宿主 StopAsync 与容器释放各调一次（幂等）

    public RelayService(IDbContextFactory<AppDbContext> dbFactory, DeviceRegistry registry,
        Func<Guid, (Guid InitiatorId, Guid TargetId)?>? peerResolver = null,
        RelayServiceOptions? options = null, TimeProvider? time = null)
    {
        _dbFactory = dbFactory;
        _registry = registry;
        _peerResolver = peerResolver;
        _options = options ?? new RelayServiceOptions();
        _time = time ?? TimeProvider.System;
        _reaper = ReaperAsync(_cts.Token);
    }

    /// <summary>UDP 实际绑定端点（端口 0 → 系统分配，测试用）。</summary>
    public IPEndPoint? UdpEndpoint => _udp?.Client.LocalEndPoint as IPEndPoint;

    /// <summary>TCP 实际绑定端点（同上）。</summary>
    public IPEndPoint? TcpEndpoint => _tcpListener?.LocalEndpoint as IPEndPoint;

    /// <summary>运行统计（测试断言与仪表盘）。</summary>
    public RelayStats Stats
    {
        get { lock (_gate) return new(_table.Count, Interlocked.Read(ref _bytesForwarded), Interlocked.Read(ref _reaped)); }
    }

    public Task StartAsync(int udpPort, int tcpPort, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_cts.IsCancellationRequested, this);
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, udpPort));
        _tcpListener = new TcpListener(IPAddress.Any, tcpPort);
        _tcpListener.Start(64);
        _udpLoop = UdpLoopAsync(_cts.Token);
        _tcpLoop = TcpAcceptLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    // ── 0x74 RelayAllocate（控制面，02 §6.1②）─────────────────────────

    /// <summary>中继分配：全局开关 → 台账解析设备对 → 建会话表项 → 向双方下发 RelayGrant。</summary>
    public async Task HandleAllocateAsync(ControlSession session, RelayAllocate msg)
    {
        using var db = _dbFactory.CreateDbContext();
        if (!new ServerConfigStore(db).GetBool("relay_enabled"))
        {
            await session.SendErrorAsync(ErrorCode.RelayDisabled, "relay_disabled"); // 5002（03 §2.8）
            return;
        }

        var peers = _peerResolver?.Invoke(msg.SessionId);
        if (peers is not { } p || p.InitiatorId != session.DeviceId)
        {
            // 台账无此会话/非发起方申请（迟到超限或异常流）：诚实拒绝，客户端可重新打洞取新会话
            await session.SendErrorAsync(ErrorCode.BadRequest, "session_unknown");
            return;
        }

        var target = _registry.TryGet(p.TargetId);
        if (target is null)
        {
            await session.SendErrorAsync(ErrorCode.TargetOffline, "target_offline");
            return;
        }

        ulong sid;
        lock (_gate)
        {
            do { sid = BinaryPrimitives.ReadUInt64LittleEndian(RandomGenerator.Bytes(HeaderLen)); }
            while (_table.ContainsKey(sid));
            _table[sid] = new RelayEntry
            {
                RelaySessionId = sid,
                A = new RelayEnd { DeviceId = p.InitiatorId, ControlIp = session.RemoteEndPoint?.Address ?? IPAddress.Any },
                B = new RelayEnd { DeviceId = p.TargetId, ControlIp = target.RemoteEndPoint?.Address ?? IPAddress.Any },
                CreatedAt = _time.GetLocalNow(),
                LastActivity = _time.GetLocalNow(),
            };
        }

        await SafePushAsync(session, BuildGrant(session, sid)); // A（申请方）
        await SafePushAsync(target, BuildGrant(target, sid));   // B（对端，02 §6.1② 双方下发）
    }

    /// <summary>RelayGrant 端点派生：本控制连接的本地侧地址（客户端经哪块网卡到达即回哪个地址）。</summary>
    private RelayGrant BuildGrant(ControlSession to, ulong sid)
    {
        var host = to.LocalEndPoint?.Address.ToString() ?? IPAddress.Loopback.ToString();
        return new RelayGrant(to.NextSeq(), to.ServerTimestamp(), MsgType.RelayAllocate, sid,
            new EndpointPair(
                new P2P.Core.Protocol.Endpoint(host, (ushort)(UdpEndpoint?.Port ?? 0)),
                new P2P.Core.Protocol.Endpoint(host, (ushort)(TcpEndpoint?.Port ?? 0))));
    }

    // ── UDP 承载（单 socket 收发循环，05 §6）─────────────────────────

    private async Task UdpLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var datagram = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
                try { await HandleUdpAsync(datagram).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                {
                    // 单包转发失败（如对端 TCP 已死）不终止收包循环——会话回收由对端读循环触发
                }
            }
        }
        catch (OperationCanceledException) { /* 停机 */ }
        catch (ObjectDisposedException) { /* 停机 */ }
        catch (SocketException) { /* 停机 */ }
    }

    private async Task HandleUdpAsync(UdpReceiveResult datagram)
    {
        var wire = datagram.Buffer;
        var from = datagram.RemoteEndPoint;

        if (wire.Length == JoinLen && wire[0] == JoinPrefix)
        {
            RelayEntry? entry;
            RelayEnd? claimed;
            lock (_gate)
            {
                if (!_table.TryGetValue(BinaryPrimitives.ReadUInt64LittleEndian(wire.AsSpan(1)), out entry)) return;
                claimed = ClaimEnd(entry, from);
                if (claimed is null) return; // 两端已满且不匹配：迟到/异常 JOIN 忽略
                claimed.UdpAddr = from;      // 地址学习（可重入：同端重 JOIN 刷新）
                entry.LastActivity = _time.GetLocalNow();
            }
            await SendUdpAsync(new byte[] { JoinAckPrefix }, from).ConfigureAwait(false); // [0x02] 确认（02 §6.1③）
            return;
        }

        if (wire.Length < HeaderLen) return;
        RelayEnd? other;
        lock (_gate)
        {
            if (!_table.TryGetValue(BinaryPrimitives.ReadUInt64LittleEndian(wire), out var entry)) return;
            var self = entry.A.UdpAddr?.Equals(from) == true ? entry.A
                     : entry.B.UdpAddr?.Equals(from) == true ? entry.B : null;
            if (self is null) return; // 未知源：保守丢弃（防会话劫持——重绑定由空闲回收+重新分配兜底）
            entry.LastActivity = _time.GetLocalNow();
            other = ReferenceEquals(self, entry.A) ? entry.B : entry.A;
        }
        await ForwardAsync(other!, wire.AsMemory(HeaderLen)).ConfigureAwait(false); // 剥离 8B → 密文原样转发
    }

    private async Task SendUdpAsync(ReadOnlyMemory<byte> datagram, IPEndPoint to)
    {
        await _udpSend.WaitAsync().ConfigureAwait(false);
        try { await _udp.SendAsync(datagram, to).ConfigureAwait(false); }
        catch (SocketException) { /* 目的已换：丢弃（地址学习由后续包自愈） */ }
        finally { _udpSend.Release(); }
    }

    // ── TCP 承载（每连接转发对，02 §6.2：u16 小端分帧）────────────────

    private async Task TcpAcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _tcpListener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException) { break; }

                _ = Task.Run(() => ServeTcpAsync(client, ct), ct);
            }
        }
        catch (OperationCanceledException) { /* 停机 */ }
    }

    /// <summary>单连接：首帧 JOIN 认领端槽（10s 超时）→ 回 [0x02] → 循环收 [sid][PTP] 剥离转发。
    /// 连接断开 = 该端 KEEPALIVE 停止 → 会话即时回收并关闭对端连接（02 §6.2）。</summary>
    private async Task ServeTcpAsync(TcpClient client, CancellationToken ct)
    {
        RelayEntry? entry = null;
        RelayEnd? self = null;
        try
        {
            var stream = client.GetStream();
            var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
            using (var joinCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                joinCts.CancelAfter(JoinTimeout);
                var join = await ReadFrameAsync(stream, joinCts.Token).ConfigureAwait(false);
                if (join is null || join.Length != JoinLen || join[0] != JoinPrefix) return;
                lock (_gate)
                {
                    if (!_table.TryGetValue(BinaryPrimitives.ReadUInt64LittleEndian(join.AsSpan(1)), out entry)) return;
                    self = ClaimEnd(entry, remote);
                    if (self is null) return;
                    self.Tcp = client; // 地址学习：连接即端点
                    entry.LastActivity = _time.GetLocalNow();
                }
            }
            await WriteFrameAsync(self!.WriteGate, stream, new byte[] { JoinAckPrefix }, ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                var frame = await ReadFrameAsync(stream, ct).ConfigureAwait(false);
                if (frame is null) break; // 对端干净关闭
                if (frame.Length < HeaderLen
                    || BinaryPrimitives.ReadUInt64LittleEndian(frame) != entry!.RelaySessionId)
                    break; // 协议错/会话头不符：断连（AI-19）
                RelayEnd other;
                lock (_gate)
                {
                    entry.LastActivity = _time.GetLocalNow();
                    other = ReferenceEquals(self, entry.A) ? entry.B : entry.A;
                }
                await ForwardAsync(other, frame.AsMemory(HeaderLen)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or SocketException
            or ObjectDisposedException or OperationCanceledException)
        {
            // 协议错/对端中止/停机：连接收场（会话回收见 finally）
        }
        finally
        {
            client.Dispose();
            if (entry is not null && self is not null)
            {
                List<TcpClient> close = [];
                bool removed;
                lock (_gate)
                {
                    // KEEPALIVE 停止 → 即时回收（02 §6.2）；先到者移除成功，被动关闭的对端连接不重复计数
                    removed = _table.Remove(entry.RelaySessionId);
                    if (ReferenceEquals(self.Tcp, client)) self.Tcp = null;
                    if (entry.A.Tcp is not null) close.Add(entry.A.Tcp); // 对端连接一并关闭（会话已不存在）
                    if (entry.B.Tcp is not null) close.Add(entry.B.Tcp);
                }
                if (removed) Interlocked.Increment(ref _reaped);
                foreach (var c in close) c.Dispose();
            }
        }
    }

    // ── 转发（两承载共用）─────────────────────────────────────────────

    /// <summary>向端转发（已剥离 8B 的 PTP 密文帧）：TCP 优先（连接在即用），次 UDP 已学地址；
    /// 均无 = 对端未 JOIN——丢弃（无缓冲设计，JOIN 确认后对端方可收）。</summary>
    private async Task ForwardAsync(RelayEnd to, ReadOnlyMemory<byte> ptpFrame)
    {
        TcpClient? tcp;
        IPEndPoint? udp;
        lock (_gate) { tcp = to.Tcp; udp = to.UdpAddr; }
        if (tcp is not null)
        {
            try
            {
                await WriteFrameAsync(to.WriteGate, tcp.GetStream(), ptpFrame, _cts.Token).ConfigureAwait(false);
                Interlocked.Add(ref _bytesForwarded, ptpFrame.Length);
            }
            catch (Exception ex) when (ex is IOException or SocketException
                or ObjectDisposedException or OperationCanceledException)
            {
                // 对端 TCP 已死：本包丢弃（其读循环即将触发会话回收，本端连接不受牵连）
            }
        }
        else if (udp is not null)
        {
            await SendUdpAsync(ptpFrame, udp).ConfigureAwait(false);
            Interlocked.Add(ref _bytesForwarded, ptpFrame.Length);
        }
    }

    /// <summary>端槽认领（须持锁）：已学地址精确匹配刷新；空槽按控制源 IP 偏好（异 IP 部署确定性）；
    /// 同 IP 双空槽先到先入 A——槽位仅作地址归属，转发"另一端"与标签无关。</summary>
    private static RelayEnd? ClaimEnd(RelayEntry entry, IPEndPoint from)
    {
        if (entry.A.UdpAddr?.Equals(from) == true) return entry.A;
        if (entry.B.UdpAddr?.Equals(from) == true) return entry.B;
        if (IsEmpty(entry.A) && entry.A.ControlIp.Equals(from.Address)) return entry.A;
        if (IsEmpty(entry.B) && entry.B.ControlIp.Equals(from.Address)) return entry.B;
        if (IsEmpty(entry.A)) return entry.A;
        if (IsEmpty(entry.B)) return entry.B;
        return null; // 两端均被他端占用：不替换（重连场景由空闲回收+重新分配兜底）

        static bool IsEmpty(RelayEnd e) => e.UdpAddr is null && e.Tcp is null;
    }

    // ── 空闲回收（02 §6.2）────────────────────────────────────────────

    private async Task ReaperAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), _time);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var now = _time.GetLocalNow();
                List<RelayEntry> expired = [];
                lock (_gate)
                {
                    foreach (var e in _table.Values.Where(e => now - e.LastActivity > _options.IdleTimeout))
                        expired.Add(e);
                    foreach (var e in expired) _table.Remove(e.RelaySessionId);
                }
                foreach (var e in expired)
                {
                    Interlocked.Increment(ref _reaped);
                    e.A.Tcp?.Dispose();
                    e.B.Tcp?.Dispose();
                }
            }
        }
        catch (OperationCanceledException) { /* 停机 */ }
    }

    /// <summary>向可能已断连的会话推送（发送失败=无接收方，静默；AI-19）。</summary>
    private static async Task SafePushAsync<T>(ControlSession session, T message) where T : class, IPcpMessage
    {
        try { await session.PushAsync(message).ConfigureAwait(false); }
        catch (Exception) { /* 连接已关：消息无处投递 */ }
    }

    // ── RLP-TCP 分帧（u16 小端长度前缀，02 §6.2）──────────────────────

    /// <summary>读一帧：null=边界处干净关闭；帧长非法/中途断开抛 InvalidDataException（断连契约）。</summary>
    private static async Task<byte[]?> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[2];
        if (!await ReadExactAsync(stream, header, ct).ConfigureAwait(false)) return null;
        var len = BinaryPrimitives.ReadUInt16LittleEndian(header);
        if (len == 0 || len > MaxFrameLen)
            throw new InvalidDataException($"RLP-TCP 帧长非法：{len}B");
        var payload = new byte[len];
        if (!await ReadExactAsync(stream, payload, ct).ConfigureAwait(false))
            throw new InvalidDataException("RLP-TCP 帧中途断开");
        return payload;
    }

    /// <summary>写一帧（端级信号量串行化：UDP 来源与 TCP 来源的转发可能并发写同一流）。</summary>
    private static async Task WriteFrameAsync(SemaphoreSlim gate, NetworkStream stream,
        ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var buf = new byte[2 + payload.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(buf, (ushort)payload.Length);
            payload.Span.CopyTo(buf.AsSpan(2));
            await stream.WriteAsync(buf, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, Memory<byte> buf, CancellationToken ct)
    {
        var read = 0;
        while (read < buf.Length)
        {
            var n = await stream.ReadAsync(buf[read..], ct).ConfigureAwait(false);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await _cts.CancelAsync().ConfigureAwait(false);
        _udp?.Close();
        if (_tcpListener is not null) _tcpListener.Stop();
        try { await _udpLoop.ConfigureAwait(false); } catch { }
        try { await _tcpLoop.ConfigureAwait(false); } catch { }
        try { await _reaper.ConfigureAwait(false); } catch { }
        lock (_gate)
        {
            foreach (var e in _table.Values) { e.A.Tcp?.Dispose(); e.B.Tcp?.Dispose(); }
            _table.Clear();
        }
        _udp?.Dispose();
        _udpSend.Dispose();
        _cts.Dispose();
    }

    // ── 内部状态 ─────────────────────────────────────────────────────

    private sealed class RelayEntry
    {
        public required ulong RelaySessionId { get; init; }
        public required RelayEnd A { get; init; } // 访问方（0x74 发起侧）
        public required RelayEnd B { get; init; } // 服务方
        public required DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset LastActivity { get; set; }
    }

    /// <summary>单端：UDP 已学地址与 TCP 连接至多各一（转发 TCP 优先）；WriteGate 串行化该端出站帧。</summary>
    private sealed class RelayEnd
    {
        public required Guid DeviceId { get; init; }
        public required IPAddress ControlIp { get; init; } // 控制连接源 IP（空槽认领偏好）
        public IPEndPoint? UdpAddr { get; set; }
        public TcpClient? Tcp { get; set; }
        public SemaphoreSlim WriteGate { get; } = new(1, 1);
    }
}

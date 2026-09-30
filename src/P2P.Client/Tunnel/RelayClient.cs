// M2-17 RLP 客户端（02 §6.1，TD-11）：0x74 RelayAllocate 发起 → Grant 后向 relay 端点
// RELAY_JOIN（UDP：单发重试直至 [0x02]；TCP：长连接首帧 u16 分帧）→ 此后每包附加/剥离外层 8B
// 会话头——加密与路径解耦（05 §4）：TunnelSession/帧逻辑不变，RelayTransport 即 ITunnelTransport
// 承载实现（02 §4.5 中继回退，绑定接入 M2-18）。KEEPALIVE 维持：TunnelSession 20s 周期
// （NET-72）经本通道发送即刷新 relay 空闲时钟；无隧道挂载场景由本类 90s 空闲自关闭兜底
// （与服务端回收口径对齐，02 §6.2）。
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using P2P.Client.Control;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using P2P.Core.Tunnel;

namespace P2P.Client.Tunnel;

/// <summary>RLP 承载选择（Grant 双端点按需取用，02 §6.1③）。</summary>
public enum RelayCarrier
{
    /// <summary>UDP：JOIN 单发重试直至确认（数据报语义）。</summary>
    Udp,
    /// <summary>TCP：长连接，首帧 JOIN 与数据帧同 u16 分帧（UDP 被封场景，FR-S-704）。</summary>
    Tcp,
}

/// <summary>RLP 客户端编排（02 §6.1①~③）：分配 → 加入两步；产物 RelayTransport 供 TunnelSession 绑定。</summary>
public static class RelayClient
{
    /// <summary>0x74 申请中继（打洞失败且回退开启时，触发链 M2-18）：经控制连接请求 → RelayGrant。
    /// 服务端错误（5002/1001/4005）以 ControlErrorException 抛出（族类型配对 0x74）。</summary>
    public static async Task<RelayGrant> AllocateAsync(ControlClient control, Guid punchSessionId,
        CancellationToken ct = default)
        => await control.SendRequestAsync<RelayGrant>(new RelayAllocate(
            control.NextSeq(), control.TimestampMs(), MsgType.RelayAllocate, punchSessionId), ct).ConfigureAwait(false);

    /// <summary>向 relay 端点 RELAY_JOIN（按承载选择 Grant 端点）→ 确认后返回就绪数据面通道。</summary>
    public static async Task<RelayTransport> JoinAsync(RelayGrant grant, RelayCarrier carrier,
        TimeSpan? joinTimeout = null, TimeProvider? time = null, CancellationToken ct = default)
        => await RelayTransport.JoinAsync(grant, carrier, joinTimeout, time, ct).ConfigureAwait(false);

    /// <summary>承载选择（02 §6.2，M2-38 口径反转：TCP 优先；UDP 兜底=7020 被封场景）：先 TCP、
    /// 失败（JOIN 超时/端点缺失/承载故障）再 UDP 兜底。服务端按端独立转发（M2-07），两端承载可异构。
    /// 反转动机（公网实测 M2-38）：UDP 承载无重传——并发大流量丢 DATA 帧（对端 TCP 字节流缺段，
    /// 应用收不满 Content-Length 卡死）或丢 WINDOW 信用回报（M2-21 发送侧信用耗尽永久挂起）、
    /// OPEN 控制帧丢失（连接即断）；单流轻载丢包率低不易察觉，多 channel 并发 burst 放大。
    /// TCP 承载内核重传彻底消除丢帧面，代价仅拥塞控制自适应（中继场景可靠性 > 峰值速率）。
    /// 外部取消（ct）不吞——传播给调用方。</summary>
    public static async Task<RelayTransport> JoinWithCarrierFallbackAsync(RelayGrant grant,
        TimeSpan? joinTimeout = null, TimeProvider? time = null, CancellationToken ct = default)
    {
        try
        {
            return await RelayTransport.JoinAsync(grant, RelayCarrier.Tcp, joinTimeout, time, ct).ConfigureAwait(false);
        }
        catch (Exception e) when ((e is IOException or SocketException) && !ct.IsCancellationRequested)
        {
            // TCP 承载不可用：转 UDP 兜底（留待下方重试；此处不吞外部取消）
        }
        return await RelayTransport.JoinAsync(grant, RelayCarrier.Udp, joinTimeout, time, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// 中继数据面通道（02 §6.1④）：发送侧附加外层 8B [u64 relaySessionId 小端]（UDP 裸 datagram /
/// TCP u16 前缀分帧），接收侧即服务端剥离后的裸 PTP 帧（M2-07 转发语义）——[0x02] 残留 JOIN 确认
/// 跳过。空闲 90s 自关闭（ReceiveAsync 返 null，承载关闭契约）。
/// </summary>
public sealed class RelayTransport : ITunnelTransport
{
    // 协议字节（02 §6.1③；服务端 RelayService 各自实现同值——协议常量不跨端引用）
    private const byte JoinPrefix = 0x01;
    private const byte JoinAckPrefix = 0x02;
    private const int HeaderLen = 8;       // 外层会话头（TD-11 唯一附加/剥离点）
    private const int JoinLen = 1 + 8 + 4; // [0x01][sid][nonce]（nonce 保形）

    /// <summary>UDP 承载整帧上限（同 UdpPunchTransport：避免 IP 分片，02 §4.3）。</summary>
    public const int MaxUdpFrame = 1400;
    /// <summary>TCP 承载整帧上限（同 TcpFrameTransport：16B 头 + 16KiB DATA + 16B tag）。</summary>
    public const int MaxTcpFrame = PtpHeader.WireLen + PtpFrameCodec.MaxDataPayload + Aead.TagLen;

    /// <summary>JOIN 确认等待（默认 10s，与服务端 TCP 首窗一致；UDP 内部 500ms 周期重发）。</summary>
    public static readonly TimeSpan DefaultJoinTimeout = TimeSpan.FromSeconds(10);
    /// <summary>本端空闲自关闭（02 §6.2 服务端 90s 回收口径；KEEPALIVE 流量即刷新）。</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(90);

    /// <summary>relay 数据面端点（Grant 派生；诊断/打洞结果明细用，M2-18）。</summary>
    public IPEndPoint RemoteEndPoint => _relayEp;

    private readonly RelayCarrier _carrier;
    private readonly ulong _sid;
    private readonly IPEndPoint _relayEp;
    private readonly TimeProvider _time;
    private UdpClient? _udp;          // UDP 模式（含 JOIN 重试源端口=数据源端口：地址学习一致）
    private TcpClient? _tcp;          // TCP 模式
    private NetworkStream? _stream;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _idleWatch;
    private long _lastActivityTicks;  // 空闲时钟（送收均刷新）
    private int _disposed;

    private RelayTransport(RelayCarrier carrier, ulong sid, IPEndPoint relayEp, TimeProvider time)
    {
        _carrier = carrier;
        _sid = sid;
        _relayEp = relayEp;
        _time = time;
        _lastActivityTicks = time.GetLocalNow().UtcTicks;
        _idleWatch = IdleWatchAsync(_cts.Token);
    }

    /// <summary>JOIN 建立（02 §6.1③）：UDP 单发重试直至 [0x02]；TCP 长连接首帧。确认超时/协议错抛 IOException。</summary>
    internal static async Task<RelayTransport> JoinAsync(RelayGrant grant, RelayCarrier carrier,
        TimeSpan? joinTimeout, TimeProvider? time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var endpoint = carrier == RelayCarrier.Udp ? grant.RelayEndpoints.Udp : grant.RelayEndpoints.Tcp;
        if (endpoint is null)
            throw new IOException($"Grant 未携带 {(carrier == RelayCarrier.Udp ? "UDP" : "TCP")} 端点（02 §6.1②）");
        var hostText = endpoint.Host.Contains(':') ? $"[{endpoint.Host}]" : endpoint.Host; // IPv6 字面量加括号
        if (!IPEndPoint.TryParse($"{hostText}:{endpoint.Port}", out var relayEp))
            throw new IOException($"Grant 端点不可解析：{endpoint.Host}:{endpoint.Port}");
        var timeout = joinTimeout ?? DefaultJoinTimeout;
        var transport = new RelayTransport(carrier, grant.RelaySessionId, relayEp, time ?? TimeProvider.System);

        try
        {
            if (carrier == RelayCarrier.Udp)
                await transport.JoinUdpAsync(timeout, ct).ConfigureAwait(false);
            else
                await transport.JoinTcpAsync(timeout, ct).ConfigureAwait(false);
            return transport;
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task JoinUdpAsync(TimeSpan timeout, CancellationToken ct)
    {
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var join = BuildJoin();
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overall.CancelAfter(timeout);
        while (true)
        {
            await _udp.SendAsync(join, _relayEp, ct).ConfigureAwait(false); // 单发直至确认（02 §6.1③）
            using var window = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
            window.CancelAfter(TimeSpan.FromMilliseconds(500));
            byte[]? got = null;
            try { got = (await _udp.ReceiveAsync(window.Token).ConfigureAwait(false)).Buffer; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) when (overall.IsCancellationRequested)
            {
                throw new IOException($"RELAY_JOIN {timeout.TotalSeconds:F0}s 未确认（{_relayEp}）");
            }
            catch (OperationCanceledException) { /* 本窗未确认：继续重发 */ }
            if (got is not null && got.Length == 1 && got[0] == JoinAckPrefix)
                return; // [0x02]：地址学习完成（其他包=迟到数据/杂散，丢弃重试）
        }
    }

    private async Task JoinTcpAsync(TimeSpan timeout, CancellationToken ct)
    {
        _tcp = new TcpClient(AddressFamily.InterNetwork);
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overall.CancelAfter(timeout);
        await _tcp.ConnectAsync(_relayEp, overall.Token).ConfigureAwait(false);
        _stream = _tcp.GetStream();
        await WriteTcpFrameAsync(BuildJoin(), overall.Token).ConfigureAwait(false); // 首帧 JOIN（u16 分帧）
        var ack = await ReadTcpFrameAsync(overall.Token).ConfigureAwait(false);
        if (ack is null || ack.Length != 1 || ack[0] != JoinAckPrefix)
            throw new IOException($"RELAY_JOIN TCP 首帧应答非 [0x02]（{_relayEp}）");
    }

    private byte[] BuildJoin()
    {
        var join = new byte[JoinLen];
        join[0] = JoinPrefix;
        BinaryPrimitives.WriteUInt64LittleEndian(join.AsSpan(1), _sid);
        RandomGenerator.Bytes(4).CopyTo(join.AsSpan(9)); // nonce 保形（不参与语义）
        return join;
    }

    // ── ITunnelTransport（发送附加 8B；接收即服务端剥离后的裸帧）─────────

    public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (_carrier == RelayCarrier.Udp)
        {
            if (frame.Length is < PtpHeader.WireLen or > MaxUdpFrame)
                throw new ProtocolException($"中继 UDP PTP 帧 {frame.Length}B 越界（{PtpHeader.WireLen}~{MaxUdpFrame}B，02 §4.3）");
            var wire = new byte[HeaderLen + frame.Length];
            BinaryPrimitives.WriteUInt64LittleEndian(wire, _sid);
            frame.Span.CopyTo(wire.AsSpan(HeaderLen));
            try { await _udp!.SendAsync(wire, _relayEp, ct).ConfigureAwait(false); }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            { throw new IOException($"中继 UDP 发送失败（{_relayEp}）：{e.Message}", e); }
        }
        else
        {
            if (frame.Length is < PtpHeader.WireLen or > MaxTcpFrame)
                throw new ProtocolException($"中继 TCP PTP 帧 {frame.Length}B 越界（{PtpHeader.WireLen}~{MaxTcpFrame}B）");
            var wire = new byte[HeaderLen + frame.Length];
            BinaryPrimitives.WriteUInt64LittleEndian(wire, _sid);
            frame.Span.CopyTo(wire.AsSpan(HeaderLen));
            await WriteTcpFrameAsync(wire, ct).ConfigureAwait(false);
        }
        Volatile.Write(ref _lastActivityTicks, _time.GetLocalNow().UtcTicks);
    }

    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken ct = default)
    {
        // 承载关闭契约：null = 传输已关闭（触发会话销毁，ITunnelTransport）
        try
        {
            while (true)
            {
                byte[]? payload;
                if (_carrier == RelayCarrier.Udp)
                {
                    if (_udp is null) return null;
                    var got = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
                    payload = got.Buffer;
                }
                else
                {
                    if (_stream is null) return null;
                    payload = await ReadTcpFrameAsync(ct).ConfigureAwait(false);
                }
                if (payload is null) return null; // TCP 对端关闭（承载关闭契约）
                if (payload.Length == 1 && payload[0] == JoinAckPrefix)
                    continue; // 重发 JOIN 的残留确认：跳过
                if (payload.Length < PtpHeader.WireLen)
                    continue; // UDP 杂散短包容忍丢弃（非帧）
                Volatile.Write(ref _lastActivityTicks, _time.GetLocalNow().UtcTicks);
                return payload;
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            return null; // 空闲自关闭/对端回收断连
        }
        // OperationCanceledException 正常传播（停机/断链取消）
    }

    // ── 空闲自关闭（02 §6.2：与服务端 90s 回收口径对齐）────────────────

    private async Task IdleWatchAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), _time);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var idle = _time.GetLocalNow().UtcTicks - Volatile.Read(ref _lastActivityTicks);
                if (idle > IdleTimeout.Ticks)
                {
                    CloseCore(); // 无 KEEPALIVE 挂载的孤儿资源兜底（与服务端回收对齐）
                    return;
                }
            }
        }
        catch (OperationCanceledException) { /* 释放 */ }
    }

    // ── TCP 分帧（02 §6.2：与 TcpFrameTransport 同定界）────────────────

    private async Task WriteTcpFrameAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var wire = new byte[2 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(wire, (ushort)payload.Length);
        payload.Span.CopyTo(wire.AsSpan(2));
        try { await _stream!.WriteAsync(wire, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        { throw new IOException($"中继 TCP 发送失败（{_relayEp}）：{e.Message}", e); }
    }

    /// <summary>读一帧（null=对端关闭）；长度非法视为流错位返回 null（承载关闭契约）。</summary>
    private async Task<byte[]?> ReadTcpFrameAsync(CancellationToken ct)
    {
        var prefix = await ReadExactAsync(2, ct).ConfigureAwait(false);
        if (prefix is null) return null;
        var len = BinaryPrimitives.ReadUInt16LittleEndian(prefix);
        if (len == 0 || len > MaxTcpFrame) return null; // 流错位/恶意长度：不可恢复
        return await ReadExactAsync(len, ct).ConfigureAwait(false);
    }

    private async Task<byte[]?> ReadExactAsync(int n, CancellationToken ct)
    {
        var buf = new byte[n];
        var read = 0;
        while (read < n)
        {
            var got = await _stream!.ReadAsync(buf.AsMemory(read), ct).ConfigureAwait(false);
            if (got == 0) return null;
            read += got;
        }
        return buf;
    }

    /// <summary>拆承载（幂等）：阻塞中的 ReceiveAsync 随之失败 → null（承载关闭契约）。</summary>
    private void CloseCore()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _cts.Cancel();
        _udp?.Dispose();
        _stream?.Dispose();
        _tcp?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        CloseCore();
        try { await _idleWatch.ConfigureAwait(false); } catch { /* 取消路径 */ }
        _cts.Dispose();
    }
}

// M1-26 Puncher（02 §5.1、05 §3，FR-C-401、OQ-11/18/TD-19）：
// - 访问方 A：出队后即时 STUN 探测（专用 socket）→ 端点随 0x70 上送（第一段）→
//   收延后 Ack（对端端点+static 公钥）→ 对 B 端点连发 THello1（10ms×20）→ PTP 握手；
// - 被邀请方 B：收 0x71 → 即时 STUN 探测 → 0x76 上报端点（第二段回传）→
//   不等中转、立即对 A 端点连发 THello1（打开本端 NAT）+ 应答对端 THello1 完成握手；
// - 打洞包与 STUN 探测同一 socket（NAT 映射即隧道承载端口）；
// - 0x72 结果上报属 FR-C-404 → M2；TCP 打洞（§5.2）与中继回退 → M2。
using System.Net;
using System.Net.Sockets;
using P2P.Client.Control;
using P2P.Client.Tunnel;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using P2P.Core.Tunnel;

namespace P2P.Client.Punch;

/// <summary>打洞可调参数（02 §5.1：超时 10s 可配；连发 10ms×20）。</summary>
public sealed record PunchOptions
{
    public TimeSpan PunchTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public int BurstCount { get; init; } = 20;
    public TimeSpan BurstInterval { get; init; } = TimeSpan.FromMilliseconds(10);
    public int KeepaliveSec { get; init; } = 20; // NET-72 ≤25s
}

/// <summary>打洞结果（本地事件驱动映射状态机；成功带双方端点）。</summary>
public sealed record PunchOutcome(
    bool Ok,
    Guid SessionId,
    Guid PeerDeviceId,
    TunnelSession? Session,
    IPEndPoint? LocalEndpoint,
    IPEndPoint? PeerEndpoint,
    string? FailReason)
{
    public static PunchOutcome Success(Guid sessionId, Guid peerDeviceId, TunnelSession? session,
        IPEndPoint local, IPEndPoint peer)
        => new(true, sessionId, peerDeviceId, session, local, peer, null);

    public static PunchOutcome Failure(Guid peerDeviceId, string reason)
        => new(false, Guid.Empty, peerDeviceId, null, null, null, reason);
}

/// <summary>打洞器接口（PunchScheduler 依赖；测试替身用）。</summary>
public interface IPuncher
{
    /// <summary>访问方：STUN 探测 → 0x70 → Ack → 连发握手。由串行队列出队调用。</summary>
    Task<PunchOutcome> InitiateAsync(Guid targetDeviceId, Guid? triggerMappingId, CancellationToken ct = default);

    /// <summary>被邀请方：0x71 → STUN 探测 → 0x76 上报 → 立即对发起方打洞应答（不进本地队列）。</summary>
    Task<PunchOutcome> RespondAsync(PunchInvite invite, CancellationToken ct = default);
}

/// <summary>
/// UDP 打洞器。控制面经委托缝注入（宿主 M1-30 接 ControlClient；单测注入假件）：
/// 0x70 请求-延后 Ack、0x76 无 Ack 上报、STUN 探测（绑定打洞 socket）。
/// </summary>
public sealed class Puncher : IPuncher, IDisposable
{
    /// <summary>0x70 发送缝：组包（seq/ts 由实现侧盖戳）→ 等待延后 Ack；失败抛 <see cref="ControlErrorException"/>。</summary>
    public delegate Task<PunchRequestAck> PunchRequestSender(
        Guid targetDeviceId, Guid? triggerMappingId, string proto, EndpointPair requesterEndpoints,
        CancellationToken ct);

    /// <summary>0x76 上报缝（无 Ack 单发；02 §5.1③ 不等中转）。</summary>
    public delegate Task EndpointReporter(Guid sessionId, EndpointPair endpoints, CancellationToken ct);

    /// <summary>STUN 探测缝：在打洞 socket 上探测本端公网端点。</summary>
    public delegate Task<IPEndPoint> StunProbeDelegate(Socket punchSocket, CancellationToken ct);

    private readonly PunchRequestSender _sendPunchRequest;
    private readonly EndpointReporter _reportEndpoints;
    private readonly StunProbeDelegate _probe;
    private readonly EcKeyPair _staticKey; // 宿主所有（随 state 落盘重建），Puncher 不释放
    private readonly ITunnelChannelHandler _handler;
    private readonly PunchOptions _options;
    private int _disposed;

    public Puncher(PunchRequestSender sendPunchRequest, EndpointReporter reportEndpoints,
        StunProbeDelegate probe, EcKeyPair staticKey,
        ITunnelChannelHandler? handler = null, PunchOptions? options = null)
    {
        _sendPunchRequest = sendPunchRequest;
        _reportEndpoints = reportEndpoints;
        _probe = probe;
        _staticKey = staticKey;
        _handler = handler ?? NullChannelHandler.Instance;
        _options = options ?? new PunchOptions();
    }

    // ── 访问方 A（02 §5.1①④⑤）──────────────────────────────────────

    public async Task<PunchOutcome> InitiateAsync(Guid targetDeviceId, Guid? triggerMappingId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        var socket = CreatePunchSocket();
        UdpPunchTransport? transport = null; // 接管 socket 后的释放责任
        var established = false;
        try
        {
            using var punchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            punchCts.CancelAfter(_options.PunchTimeout);

            // ① 出队后即时 STUN 探测（专用 socket=后续承载）
            IPEndPoint local;
            try { local = await _probe(socket, punchCts.Token); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return PunchOutcome.Failure(targetDeviceId, $"stun_failed: {e.Message}");
            }

            // ② 第一段上送：端点随 0x70；Ack 延后至 B 的 0x76（4005/5001 即时失败）
            PunchRequestAck ack;
            try
            {
                ack = await _sendPunchRequest(targetDeviceId, triggerMappingId, "udp",
                    new EndpointPair(ToProtocolEndpoint(local), null),
                    punchCts.Token);
            }
            catch (ControlErrorException e)
            {
                return PunchOutcome.Failure(targetDeviceId, $"server_{e.Code}: {e.HttpLikeMsg}");
            }

            // ④ 收 Ack → 对 B 端点连发 THello1（10ms×20，NAT 窗口容丢）→ PTP 握手
            var peerEp = ParseEndpoint(ack.PeerEndpoints.Udp)
                ?? throw new IOException("PunchRequest Ack 未携带对端 UDP 端点");
            transport = new UdpPunchTransport(socket, peerEp);
            var session = await TunnelSession.ConnectAsync(ack.SessionId, ack.Peer.DeviceId,
                _staticKey, ack.Peer.StaticPubKey, transport, _handler,
                new TunnelSessionOptions
                {
                    KeepaliveInterval = TimeSpan.FromSeconds(_options.KeepaliveSec),
                    HandshakeTimeout = _options.PunchTimeout,
                    HandshakeResendCount = _options.BurstCount,
                    HandshakeResendInterval = _options.BurstInterval,
                });
            established = true; // 成功：socket 归会话（Disconnect 时 transport 释放）
            return PunchOutcome.Success(ack.SessionId, ack.Peer.DeviceId, session, local, peerEp);
        }
        catch (Exception e)
        {
            return PunchOutcome.Failure(targetDeviceId,
                e is OperationCanceledException && !ct.IsCancellationRequested
                    ? "punch_timeout"
                    : $"punch_error: {e.Message}");
        }
        finally
        {
            if (transport is null) socket.Dispose();
            else if (!established) await transport.DisposeAsync(); // 握手失败：释放接管中的承载
        }
    }

    // ── 被邀请方 B（02 §5.1③；05 §3.1：不进本地队列）─────────────────

    public async Task<PunchOutcome> RespondAsync(PunchInvite invite, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invite);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        var socket = CreatePunchSocket();
        UdpPunchTransport? transport = null;
        var established = false;
        try
        {
            using var punchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            punchCts.CancelAfter(_options.PunchTimeout);

            // ③ 即时 STUN 探测（失败无有效端点可报：不上报 0x76，服务端 invite_timeout 兜底 A 侧）
            IPEndPoint local;
            try { local = await _probe(socket, punchCts.Token); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return PunchOutcome.Failure(invite.Peer.DeviceId, $"stun_failed: {e.Message}");
            }

            // ③ 先校验发起方端点（无效邀请不污染 0x76 上报），再回传端点（第二段，无 Ack 单发）
            var peerEp = ParseEndpoint(invite.PeerEndpoints.Udp)
                ?? throw new IOException("PunchInvite 未携带发起方 UDP 端点");
            await _reportEndpoints(invite.SessionId,
                new EndpointPair(ToProtocolEndpoint(local), null),
                punchCts.Token);

            transport = new UdpPunchTransport(socket, peerEp);

            // ③ 立即对发起方端点连发 THello1（打开本端 NAT；A 为 PTP 发起方将忽略这些帧）
            var burst = BurstAsync(transport, invite.SessionId, invite.Peer.StaticPubKey, punchCts.Token);
            try
            {
                while (true)
                {
                    var frame = await transport.ReceiveAsync(punchCts.Token)
                        ?? throw new IOException("打洞承载关闭");
                    if (PtpFrameCodec.ParseHeader(frame).Type != PtpFrameType.THello1) continue; // 无关包丢弃
                    var session = await TunnelSession.AcceptAsync(invite.Peer.DeviceId, frame,
                        _staticKey, invite.Peer.StaticPubKey, transport, _handler,
                        new TunnelSessionOptions
                        {
                            KeepaliveInterval = TimeSpan.FromSeconds(_options.KeepaliveSec),
                            HandshakeTimeout = _options.PunchTimeout,
                        });
                    established = true;
                    return PunchOutcome.Success(invite.SessionId, invite.Peer.DeviceId, session, local, peerEp);
                }
            }
            finally
            {
                punchCts.Cancel();
                try { await burst; } catch { /* 尽力而为连发 */ }
            }
        }
        catch (Exception e)
        {
            return PunchOutcome.Failure(invite.Peer.DeviceId,
                e is OperationCanceledException && !ct.IsCancellationRequested
                    ? "punch_timeout"
                    : $"punch_error: {e.Message}");
        }
        finally
        {
            if (transport is null) socket.Dispose();
            else if (!established) await transport.DisposeAsync();
        }
    }

    /// <summary>被动侧 NAT 打开连发（02 §5.1③）：对发起方端点连发 THello1 × BurstCount @ BurstInterval。</summary>
    private async Task BurstAsync(UdpPunchTransport transport, Guid sessionId, byte[] peerStaticPub,
        CancellationToken ct)
    {
        try
        {
            using var initiator = PtpHandshake.StartInitiator(sessionId, _staticKey, peerStaticPub);
            for (var i = 0; i < _options.BurstCount; i++)
            {
                await transport.SendAsync(initiator.THello1Wire, ct);
                if (i + 1 < _options.BurstCount)
                    await Task.Delay(_options.BurstInterval, ct);
            }
        }
        catch (OperationCanceledException) { /* 握手完成/超时停止 */ }
        catch (Exception) { /* 连发尽力而为：正式握手由应答路径完成 */ }
    }

    private static P2P.Core.Protocol.Endpoint ToProtocolEndpoint(IPEndPoint ep)
        => new(ep.Address.ToString(), (ushort)ep.Port);

    private static IPEndPoint? ParseEndpoint(P2P.Core.Protocol.Endpoint? ep)
        => ep is null ? null
            : IPEndPoint.TryParse($"{ep.Host}:{ep.Port}", out var parsed) ? parsed : null;

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

    /// <summary>新建打洞 socket（IPv4/UDP，随机本地端口）。
    /// Windows 下禁用 UDP ConnectionReset：向未就绪对端端口发包引发的 ICMP 会让后续 Receive 抛异常
    /// （打洞期对端尚未绑定的窗口必然出现），禁用后表现为无包到达（SIO_UDP_CONNRESET；Linux 不支持则忽略）。</summary>
    internal static Socket CreatePunchSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            const int SioUdpConnreset = -1744830452;
            socket.IOControl(SioUdpConnreset, [0], null);
        }
        catch (SocketException) { /* Linux/macOS：无此语义 */ }
        catch (PlatformNotSupportedException) { /* 同上 */ }
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        return socket;
    }
}

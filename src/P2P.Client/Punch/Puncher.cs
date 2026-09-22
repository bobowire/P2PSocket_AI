// M1-26 Puncher（02 §5.1、05 §3，FR-C-401、OQ-11/18/TD-19）；M2-16 增 TCP 打洞（02 §5.2、FR-C-402、OQ-1/19）：
// - 访问方 A：出队后即时 STUN 探测（专用 socket）→ 端点随 0x70 上送（第一段）→
//   收延后 Ack（对端端点+static 公钥）→ 打洞 → PTP 握手；
// - 被邀请方 B：收 0x71 → 即时 STUN 探测 → 0x76 上报端点（第二段回传）→ 对称执行打洞与握手；
// - UDP（02 §5.1）：探测/打洞包/承载同一 socket，THello1 连发 10ms×20；
// - TCP（02 §5.2）：端口 L STUN-TCP 探测（M2-04，单事务即关）→ listen(L)+N 并发 connect
//   （第 1 条沿用 L，目标统一 portTcp+(N−1)）→ 双方 simultaneous open → 首个完成握手帧交换的
//   连接胜出、其余关闭；N 以 0x71/0x70 Ack 服务端回填值为准（OQ-19）；
// - 0x72 结果上报属 FR-C-404 → M2-22。
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

    /// <summary>打洞 socket 绑定地址（null=Any 全接口，生产行为）。
    /// M1-35 测试缝：NatSimulator 按源 IP 识别客户端 NAT，打洞 socket 须绑定各自内网回环别名。</summary>
    public IPAddress? BindAddress { get; init; }
}

/// <summary>打洞结果（本地事件驱动映射状态机；成功带双方端点）。
/// <see cref="RelayAllowed"/>（M2-23）：失败结果的中继回退资格 = 本地 peers.json 设备级配置 AND
/// 服务端 PunchRequestAck.relayAllowed（05 §3.1，Puncher 出队执行时合成；消费方 M2-18 走 0x74）。
/// 仅 Ack 后失败（会话两段式完成、服务端台账在册）携带 true；Ack 前失败无会话不可中继。</summary>
public sealed record PunchOutcome(
    bool Ok,
    Guid SessionId,
    Guid PeerDeviceId,
    TunnelSession? Session,
    IPEndPoint? LocalEndpoint,
    IPEndPoint? PeerEndpoint,
    string? FailReason,
    bool RelayAllowed = false)
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
    /// <summary>访问方：STUN 探测 → 0x70 → Ack → 打洞握手。由串行队列出队调用；
    /// proto 取触发映射的协议（tcp/udp，02 §4.5 承载绑定随打洞结果）。</summary>
    Task<PunchOutcome> InitiateAsync(Guid targetDeviceId, Guid? triggerMappingId, string proto,
        CancellationToken ct = default);

    /// <summary>被邀请方：0x71 → STUN 探测 → 0x76 上报 → 立即对发起方打洞应答（不进本地队列）。</summary>
    Task<PunchOutcome> RespondAsync(PunchInvite invite, CancellationToken ct = default);
}

/// <summary>
/// 打洞器（UDP 02 §5.1 / TCP 02 §5.2）。控制面经委托缝注入（宿主 M1-30 接 ControlClient；单测注入假件）：
/// 0x70 请求-延后 Ack、0x76 无 Ack 上报、STUN-UDP/STUN-TCP 探测。
/// </summary>
public sealed class Puncher : IPuncher, IDisposable
{
    /// <summary>0x70 发送缝：组包（seq/ts 由实现侧盖戳）→ 等待延后 Ack；失败抛 <see cref="ControlErrorException"/>。</summary>
    public delegate Task<PunchRequestAck> PunchRequestSender(
        Guid targetDeviceId, Guid? triggerMappingId, string proto, EndpointPair requesterEndpoints,
        CancellationToken ct);

    /// <summary>0x76 上报缝（无 Ack 单发；02 §5.1③ 不等中转）。</summary>
    public delegate Task EndpointReporter(Guid sessionId, EndpointPair endpoints, CancellationToken ct);

    /// <summary>STUN-UDP 探测缝：在打洞 socket 上探测本端公网端点。</summary>
    public delegate Task<IPEndPoint> StunProbeDelegate(Socket punchSocket, CancellationToken ct);

    /// <summary>STUN-TCP 探测缝（M2-04 StunTcpProber）：socket 为预绑定端口 L 的未连接句柄
    /// （SO_REUSEADDR 已设）；探测事务完成即关连接并释放句柄——调用方仅保留端口号 L 复用 listen。</summary>
    public delegate Task<IPEndPoint> StunTcpProbeDelegate(Socket punchSocket, CancellationToken ct);

    private readonly PunchRequestSender _sendPunchRequest;
    private readonly EndpointReporter _reportEndpoints;
    private readonly StunProbeDelegate _probe;
    private readonly StunTcpProbeDelegate? _tcpProbe;
    private readonly EcKeyPair _staticKey; // 宿主所有（随 state 落盘重建），Puncher 不释放
    private readonly ITunnelChannelHandler _handler;
    private readonly PunchOptions _options;
    private readonly Func<Guid, bool> _relayFallback; // 本地设备级回退配置（缺省恒 false=默认关，PRD 06 §2）
    private int _disposed;

    public Puncher(PunchRequestSender sendPunchRequest, EndpointReporter reportEndpoints,
        StunProbeDelegate probe, EcKeyPair staticKey,
        ITunnelChannelHandler? handler = null, PunchOptions? options = null,
        StunTcpProbeDelegate? tcpProbe = null, Func<Guid, bool>? relayFallbackLookup = null)
    {
        _sendPunchRequest = sendPunchRequest;
        _reportEndpoints = reportEndpoints;
        _probe = probe;
        _tcpProbe = tcpProbe;
        _staticKey = staticKey;
        _handler = handler ?? NullChannelHandler.Instance;
        _options = options ?? new PunchOptions();
        _relayFallback = relayFallbackLookup ?? (_ => false); // 未装配=默认关闭（与 peers.json 无条目同口径）
    }

    // ── 访问方 A（02 §5.1①④⑤ / §5.2①③④）──────────────────────────

    public async Task<PunchOutcome> InitiateAsync(Guid targetDeviceId, Guid? triggerMappingId, string proto,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (proto is not ("udp" or "tcp"))
            return PunchOutcome.Failure(targetDeviceId, $"proto_not_supported: {proto}");
        using var punchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        punchCts.CancelAfter(_options.PunchTimeout);
        try
        {
            return proto == "tcp"
                ? await InitiateTcpAsync(targetDeviceId, triggerMappingId, punchCts.Token, ct)
                : await InitiateUdpAsync(targetDeviceId, triggerMappingId, punchCts.Token, ct);
        }
        catch (Exception e)
        {
            return PunchOutcome.Failure(targetDeviceId,
                e is OperationCanceledException && !ct.IsCancellationRequested
                    ? "punch_timeout"
                    : $"punch_error: {e.Message}");
        }
    }

    private async Task<PunchOutcome> InitiateUdpAsync(Guid targetDeviceId, Guid? triggerMappingId,
        CancellationToken punchCt, CancellationToken ct)
    {
        var socket = CreatePunchSocket(_options.BindAddress);
        UdpPunchTransport? transport = null; // 接管 socket 后的释放责任
        var established = false;
        try
        {
            // ① 出队后即时 STUN 探测（专用 socket=后续承载）
            IPEndPoint local;
            try { local = await _probe(socket, punchCt); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return PunchOutcome.Failure(targetDeviceId, $"stun_failed: {e.Message}");
            }

            // ② 第一段上送：端点随 0x70；Ack 延后至 B 的 0x76（4005/5001 即时失败）
            PunchRequestAck ack;
            try
            {
                ack = await _sendPunchRequest(targetDeviceId, triggerMappingId, "udp",
                    new EndpointPair(ToProtocolEndpoint(local), null), punchCt);
            }
            catch (ControlErrorException e)
            {
                return PunchOutcome.Failure(targetDeviceId, $"server_{e.Code}: {e.HttpLikeMsg}");
            }

            // 回退资格合成（05 §3.1，M2-23 供 M2-18）：本地设备级配置 AND 服务端中继开关
            var relayAllowed = _relayFallback(targetDeviceId) && ack.RelayAllowed;

            // ④ 收 Ack → 对 B 端点连发 THello1（10ms×20，NAT 窗口容丢）→ PTP 握手
            try
            {
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
                return FailAfterAck(targetDeviceId, e, ct, relayAllowed);
            }
        }
        finally
        {
            if (transport is null) socket.Dispose();
            else if (!established) await transport.DisposeAsync(); // 握手失败：释放接管中的承载
        }
    }

    /// <summary>TCP 打洞访问方（02 §5.2①③④）：端口 L 探测 → 0x70{tcp:L'} → Ack{对端 L', N} →
    /// listen(L)+N 并发 connect（目标 portTcp+(N−1)）→ 全部候选连接扇出 THello1、首条 THello2
    /// 到达的连接胜出（对端仅在收到 THello1 的连接上应答，天然消歧，02 §5.2④）。</summary>
    private async Task<PunchOutcome> InitiateTcpAsync(Guid targetDeviceId, Guid? triggerMappingId,
        CancellationToken punchCt, CancellationToken ct)
    {
        // ① 专用本地端口 L → STUN-TCP 探测（M2-04：事务即关、句柄由探测方释放，仅保留端口号 L）
        var portL = 0;
        IPEndPoint mappedL;
        try
        {
            var socketL = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            TcpPunchFleet.SetPunchReuse(socketL);
            socketL.Bind(new IPEndPoint(_options.BindAddress ?? IPAddress.Any, 0));
            portL = ((IPEndPoint)socketL.LocalEndPoint!).Port;
            if (_tcpProbe is null) throw new IOException("tcp_probe_missing: STUN-TCP 探测缝未接线");
            mappedL = await _tcpProbe(socketL, punchCt);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return PunchOutcome.Failure(targetDeviceId, $"stun_failed: {e.Message}");
        }

        // ② 第一段上送（端点在 tcp 槽位）；Ack 延后至 B 的 0x76（4005/5001 即时失败）
        PunchRequestAck ack;
        try
        {
            ack = await _sendPunchRequest(targetDeviceId, triggerMappingId, "tcp",
                new EndpointPair(null, ToProtocolEndpoint(mappedL)), punchCt);
        }
        catch (ControlErrorException e)
        {
            return PunchOutcome.Failure(targetDeviceId, $"server_{e.Code}: {e.HttpLikeMsg}");
        }

        // 回退资格合成（05 §3.1，M2-23 供 M2-18）：本地设备级配置 AND 服务端中继开关
        var relayAllowed = _relayFallback(targetDeviceId) && ack.RelayAllowed;

        try
        {
            var peerMapped = ParseEndpoint(ack.PeerEndpoints.Tcp)
                ?? throw new IOException("PunchRequest Ack 未携带对端 TCP 端点");
            var n = PunchPolicy.Normalize(ack.PunchCount); // OQ-19：以服务端回填 N 为准
            var target = TcpPunchPlan.TargetEndpoint(peerMapped, n);

            // ③④ listen(L) + N 并发 connect → 候选连接扇出 THello1 → 首条 THello2 的连接胜出
            using var initiator = PtpHandshake.StartInitiator(ack.SessionId, _staticKey, ack.Peer.StaticPubKey);
            var fleet = TcpPunchFleet.Create(_options.BindAddress, portL, target, n, PtpFrameType.THello2,
                onConnection: t => t.SendAsync(initiator.THello1Wire, punchCt)); // 建立即发首帧
            try
            {
                using var burstCts = CancellationTokenSource.CreateLinkedTokenSource(punchCt);
                var burst = TcpBurstAsync(fleet, initiator.THello1Wire, burstCts.Token); // 周期补发（SYN 重传窗口容丢）
                try
                {
                    var (winner, tHello2) = await fleet.WaitFrameAsync(punchCt);
                    var (tConfirm, keys) = initiator.HandleTHello2(tHello2);
                    var transport = fleet.Release(winner); // 胜出连接移交会话；其余随池销毁（02 §5.2④）
                    await transport.SendAsync(tConfirm, punchCt);
                    var session = TunnelSession.FromEstablishedKeys(ack.SessionId, ack.Peer.DeviceId, true,
                        keys, transport, _handler, new TunnelSessionOptions
                        {
                            KeepaliveInterval = TimeSpan.FromSeconds(_options.KeepaliveSec),
                            HandshakeTimeout = _options.PunchTimeout,
                        });
                    return PunchOutcome.Success(ack.SessionId, ack.Peer.DeviceId, session,
                        winner.LocalEndPoint!, winner.RemoteEndPoint!);
                }
                finally
                {
                    burstCts.Cancel();
                    try { await burst; } catch { /* 补发尽力而为 */ }
                }
            }
            finally
            {
                await fleet.DisposeAsync(); // 幂等：成功路径已 Release 胜出，仅关其余 N−1 与 listener
            }
        }
        catch (Exception e)
        {
            return FailAfterAck(targetDeviceId, e, ct, relayAllowed);
        }
    }

    /// <summary>Ack 后失败（打洞/握手阶段）：失败语义同外层（超时/错误二分），并携带回退资格（05 §3.1）。</summary>
    private PunchOutcome FailAfterAck(Guid targetDeviceId, Exception e, CancellationToken ct, bool relayAllowed)
        => new(false, Guid.Empty, targetDeviceId, null, null, null,
            e is OperationCanceledException && !ct.IsCancellationRequested
                ? "punch_timeout"
                : $"punch_error: {e.Message}",
            relayAllowed);

    // ── 被邀请方 B（02 §5.1③/§5.2③；05 §3.1：不进本地队列）───────────

    public async Task<PunchOutcome> RespondAsync(PunchInvite invite, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invite);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        using var punchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        punchCts.CancelAfter(_options.PunchTimeout);
        try
        {
            // 发起方仅携带其打洞 proto 的端点（0x70 requesterEndpoints → 0x71 透传）：tcp 槽位在即 TCP 打洞
            return invite.PeerEndpoints.Tcp is not null
                ? await RespondTcpAsync(invite, punchCts.Token)
                : await RespondUdpAsync(invite, punchCts.Token);
        }
        catch (Exception e)
        {
            return PunchOutcome.Failure(invite.Peer.DeviceId,
                e is OperationCanceledException && !ct.IsCancellationRequested
                    ? "punch_timeout"
                    : $"punch_error: {e.Message}");
        }
    }

    private async Task<PunchOutcome> RespondUdpAsync(PunchInvite invite, CancellationToken punchCt)
    {
        var socket = CreatePunchSocket(_options.BindAddress);
        UdpPunchTransport? transport = null;
        var established = false;
        try
        {
            // ③ 即时 STUN 探测（失败无有效端点可报：不上报 0x76，服务端 invite_timeout 兜底 A 侧）
            IPEndPoint local;
            try { local = await _probe(socket, punchCt); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return PunchOutcome.Failure(invite.Peer.DeviceId, $"stun_failed: {e.Message}");
            }

            // ③ 先校验发起方端点（无效邀请不污染 0x76 上报），再回传端点（第二段，无 Ack 单发）
            var peerEp = ParseEndpoint(invite.PeerEndpoints.Udp)
                ?? throw new IOException("PunchInvite 未携带发起方 UDP 端点");
            await _reportEndpoints(invite.SessionId,
                new EndpointPair(ToProtocolEndpoint(local), null), punchCt);

            transport = new UdpPunchTransport(socket, peerEp);

            // ③ 立即对发起方端点连发 THello1（打开本端 NAT；A 为 PTP 发起方将忽略这些帧）
            using var burstCts = CancellationTokenSource.CreateLinkedTokenSource(punchCt);
            var burst = BurstAsync(transport, invite.SessionId, invite.Peer.StaticPubKey, burstCts.Token);
            try
            {
                while (true)
                {
                    var frame = await transport.ReceiveAsync(punchCt)
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
                burstCts.Cancel();
                try { await burst; } catch { /* 尽力而为连发 */ }
            }
        }
        finally
        {
            if (transport is null) socket.Dispose();
            else if (!established) await transport.DisposeAsync();
        }
    }

    /// <summary>TCP 打洞被邀请方（02 §5.2②③④，对称执行）：端口 L 探测 → 建池（listen+connect 的
    /// SYN 即打洞包，simultaneous open）→ 0x76 回传 → 等任一连接上的 THello1（首个到达即胜出连接）→ 应答握手。</summary>
    private async Task<PunchOutcome> RespondTcpAsync(PunchInvite invite, CancellationToken punchCt)
    {
        // ③ 端口 L 探测（失败无有效端点可报：不上报 0x76）
        var portL = 0;
        IPEndPoint mappedL;
        try
        {
            var socketL = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            TcpPunchFleet.SetPunchReuse(socketL);
            socketL.Bind(new IPEndPoint(_options.BindAddress ?? IPAddress.Any, 0));
            portL = ((IPEndPoint)socketL.LocalEndPoint!).Port;
            if (_tcpProbe is null) throw new IOException("tcp_probe_missing: STUN-TCP 探测缝未接线");
            mappedL = await _tcpProbe(socketL, punchCt);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return PunchOutcome.Failure(invite.Peer.DeviceId, $"stun_failed: {e.Message}");
        }

        // ③ 先校验发起方端点（无效邀请不污染 0x76 上报）
        var peerMapped = ParseEndpoint(invite.PeerEndpoints.Tcp)
            ?? throw new IOException("PunchInvite 未携带发起方 TCP 端点");
        var n = PunchPolicy.Normalize(invite.PunchCount); // OQ-19：与发起方同一 N（服务端统一下发）
        var target = TcpPunchPlan.TargetEndpoint(peerMapped, n);

        // 建池先于 0x76：connect 的 SYN 尽早出发与 A 交叉（小时差靠 SYN 重传覆盖，02 §5.2 实现备注）
        var fleet = TcpPunchFleet.Create(_options.BindAddress, portL, target, n, PtpFrameType.THello1);
        try
        {
            await _reportEndpoints(invite.SessionId,
                new EndpointPair(null, ToProtocolEndpoint(mappedL)), punchCt);

            // B 不扇出（响应方等 THello1；对端仅在收到 THello1 的连接上收 THello2，天然消歧）
            var (winner, tHello1) = await fleet.WaitFrameAsync(punchCt);
            var transport = fleet.Release(winner);
            var session = await TunnelSession.AcceptAsync(invite.Peer.DeviceId, tHello1,
                _staticKey, invite.Peer.StaticPubKey, transport, _handler,
                new TunnelSessionOptions
                {
                    KeepaliveInterval = TimeSpan.FromSeconds(_options.KeepaliveSec),
                    HandshakeTimeout = _options.PunchTimeout,
                });
            return PunchOutcome.Success(invite.SessionId, invite.Peer.DeviceId, session,
                winner.LocalEndPoint!, winner.RemoteEndPoint!);
        }
        finally
        {
            await fleet.DisposeAsync(); // 幂等：成功路径已 Release 胜出，仅关其余候选与 listener
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

    /// <summary>TCP 打洞周期补发（02 §5.2③④）：向池内全部活跃连接重发同一 THello1
    /// （SYN 重传/NAT 窗口容丢；首帧已由 onConnection 回调发出）。</summary>
    private async Task TcpBurstAsync(TcpPunchFleet fleet, byte[] wire, CancellationToken ct)
    {
        try
        {
            for (var i = 0; i < _options.BurstCount; i++)
            {
                await Task.Delay(_options.BurstInterval, ct);
                foreach (var t in fleet.Connections)
                    try { await t.SendAsync(wire, ct); }
                    catch { /* 单连接瞬断不阻断其余 */ }
            }
        }
        catch (OperationCanceledException) { /* 胜出/超时停止 */ }
        catch (Exception) { /* 补发尽力而为 */ }
    }

    private static P2P.Core.Protocol.Endpoint ToProtocolEndpoint(IPEndPoint ep)
        => new(ep.Address.ToString(), (ushort)ep.Port);

    private static IPEndPoint? ParseEndpoint(P2P.Core.Protocol.Endpoint? ep)
        => ep is null ? null
            : IPEndPoint.TryParse($"{ep.Host}:{ep.Port}", out var parsed) ? parsed : null;

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

    /// <summary>新建打洞 socket（IPv4/UDP，随机本地端口；bindAddress 缺省 Any 全接口）。
    /// Windows 下禁用 UDP ConnectionReset：向未就绪对端端口发包引发的 ICMP 会让后续 Receive 抛异常
    /// （打洞期对端尚未绑定的窗口必然出现），禁用后表现为无包到达（SIO_UDP_CONNRESET；Linux 不支持则忽略）。</summary>
    internal static Socket CreatePunchSocket(IPAddress? bindAddress = null)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            const int SioUdpConnreset = -1744830452;
            socket.IOControl(SioUdpConnreset, [0], null);
        }
        catch (SocketException) { /* Linux/macOS：无此语义 */ }
        catch (PlatformNotSupportedException) { /* 同上 */ }
        socket.Bind(new IPEndPoint(bindAddress ?? IPAddress.Any, 0));
        return socket;
    }
}

// M1-26 Puncher（02 §5.1、05 §3，FR-C-401、OQ-11/18/TD-19）；M2-16 增 TCP 打洞（02 §5.2、FR-C-402、OQ-1/19）：
// - 访问方 A：出队后即时 STUN 探测（专用 socket）→ 端点随 0x70 上送（第一段）→
//   收延后 Ack（对端端点+static 公钥）→ 打洞 → PTP 握手；
// - 被邀请方 B：收 0x71 → 即时 STUN 探测 → 0x76 上报端点（第二段回传）→ 对称执行打洞与握手；
// - UDP（02 §5.1）：探测/打洞包/承载同一 socket，THello1 连发 10ms×20；
// - TCP（02 §5.2）：端口 L STUN-TCP 探测（M2-04，单事务即关）→ listen(L)+N 并发 connect
//   （第 1 条沿用 L，目标统一 portTcp+(N−1)）→ 双方 simultaneous open → 首个完成握手帧交换的
//   连接胜出、其余关闭；N 以 0x71/0x70 Ack 服务端回填值为准（OQ-19）；
// - §5.2⑤ 齐射同步+波次补射（2026-10-24）：A 以 0x70 Ack 到达为锚、B 以同刻下发的 0x77 PunchFire
//   为锚，各倒数同一 Δ（PunchPolicy.TcpVolleyCountdown）到点齐射——SYN 空中交叉，规避「B 建池即打
//   早于 A 映射建立、SYN 打空门（RST 即死+Windows SYN 预算仅 ~2 次重传）」竞态；打洞窗内每
//   TcpWaveInterval 重建一波 connect 补射（每波全新 SYN 预算）；锚点丢失（旧服务端）B 超时兜底立即开火；
// - M2-18 中继回退（02 §4.5/§6.1①）：打洞 Ack 后失败且回退资格合成真 → 0x74 分配 →
//   JOIN（先 UDP 后 TCP）→ PTP 握手即经中继（加密与路径解耦：同一 TunnelSession 帧改发 relay 地址）；
// - M2-37 回退复用（05 §4）：回切重打（M2-19 周期）失败再回退时，设备对已有活中继会话 →
//   直接复用不重新 0x74（整体替换会经排水窗杀死传输中 channel，60s 周期下长传输必断）；
// - 0x72 结果上报属 FR-C-404 → M2-22。
// - §5.4 直连优先（2026-10-21）：本机候选（网卡 IPv4 × 打洞监听口）随 0x70/0x76 上送、
//   服务端透传至对端——UDP 承载扇出发往映射端点+全部候选（首个到达包钉定回程）；
//   TCP fleet 在 N 条预测 connect 之外对「对端端点原样 + 候选」各起拒连重试直连探测，
//   accept 侧连入亦即时扇出。同网段/单侧公网可直达时直连即刻胜出，否则行为同现状。
using System.Net;
using System.Net.Sockets;
using P2P.Client.Control;
using P2P.Client.Tunnel;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using P2P.Core.Stun;
using P2P.Core.Tunnel;
using P2P.Nic;

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

    /// <summary>中继回退整体预算（0x74 分配 + JOIN 承载兜底 + 握手；M2-18）。须 > 打洞超时
    /// 之外独立计量（打洞 cts 已耗尽，回退用外层令牌重新起算）。</summary>
    public TimeSpan RelayFallbackTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>中继握手 THello1 重发步进（覆盖对端 JOIN 就绪窗口：Grant 推送+JOIN 往返）。</summary>
    public TimeSpan RelayHandshakeResendInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>TCP 波次补射步进（02 §5.2③b′ 方案 B）：打洞窗内每步重建一波 N 条 connect
    ///（全新 socket=全新 SYN 预算，覆盖「对端瞄准点槽位存活」时段）；单测可调（缩短加速/加宽近似单波）。</summary>
    public TimeSpan TcpWaveInterval { get; init; } = TcpPunchFleet.WaveInterval;

    /// <summary>KCP 承载参数（M6-05，设计 11 §5.4 默认表）：null=缺省参数组（M6-07 调参后经宿主注入）。</summary>
    public KcpTunnelTransportOptions? KcpBearerOptions { get; init; }

    /// <summary>B 侧 0x77 齐射锚点等待上限（02 §5.2⑤ 方案 A）：超时（旧服务端不推送/丢失）立即
    /// 开火兜底；取值≈TcpVolleyCountdown+一次控制往返——兜底路径与 A 的倒计时终点仍近似重合。</summary>
    public TimeSpan PunchFireFallbackTimeout { get; init; } = TimeSpan.FromMilliseconds(600);

    /// <summary>0x78 PunchStartGo 应答等待（02 §5.2⑥）：旧服务端对未知 msgType **静默丢弃**（02 §7）
    /// ——超时即判旧服务端，降级旧 0x70 流程（混布自由升级序）。</summary>
    public TimeSpan PunchStartReplyTimeout { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>打洞结果（本地事件驱动映射状态机；成功带双方端点）。
/// <see cref="SessionId"/>：成功与 Ack 后失败携带（会话两段式完成、服务端台账在册——0x72 上报归属，
/// M2-22）；Ack 前失败为 Guid.Empty（无会话）。
/// <see cref="RelayAllowed"/>（M2-23）：失败结果的中继回退资格 = 本地 peers.json 设备级配置 AND
/// 服务端 PunchRequestAck.relayAllowed（05 §3.1，Puncher 出队执行时合成；消费方 M2-18 走 0x74）。
/// 仅 Ack 后失败携带 true；Ack 前失败无会话不可中继。
/// <see cref="Proto"/>（M7-04，设计 12 §6.1）：本次打洞使用的协议——执行层口径 tcp/udp（与
/// 0x70/punch_stats 对账一致；punchMode=kcp 的打洞记 "udp"）。中继回退产物继承本次尝试的 proto
/// （日志语义="本次用 X 协议打洞失败后回退中继"）。</summary>
public sealed record PunchOutcome(
    bool Ok,
    Guid SessionId,
    Guid PeerDeviceId,
    TunnelSession? Session,
    IPEndPoint? LocalEndpoint,
    IPEndPoint? PeerEndpoint,
    string? FailReason,
    bool RelayAllowed = false,
    string? Proto = null)
{
    public static PunchOutcome Success(Guid sessionId, Guid peerDeviceId, TunnelSession? session,
        IPEndPoint local, IPEndPoint peer, string? proto = null)
        => new(true, sessionId, peerDeviceId, session, local, peer, null, Proto: proto);

    public static PunchOutcome Failure(Guid peerDeviceId, string reason, string? proto = null)
        => new(false, Guid.Empty, peerDeviceId, null, null, null, reason, Proto: proto);
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

    /// <summary>被邀请方：0x78 PunchStartGo → STUN-TCP 交换（应答即锚点）→ 扇形齐射 → 应答握手
    ///（02 §5.2⑥ 晚探测流程 B 侧；不进本地队列）。</summary>
    Task<PunchOutcome> RespondGoAsync(PunchStartGo go, CancellationToken ct = default);
}

/// <summary>
/// 打洞器（UDP 02 §5.1 / TCP 02 §5.2）。控制面经委托缝注入（宿主 M1-30 接 ControlClient；单测注入假件）：
/// 0x70 请求-延后 Ack、0x76 无 Ack 上报、STUN-UDP/STUN-TCP 探测。
/// </summary>
public sealed class Puncher : IPuncher, IDisposable
{
    /// <summary>0x70 发送缝：组包（seq/ts 由实现侧盖戳）→ 等待延后 Ack；失败抛 <see cref="ControlErrorException"/>。
    /// localCandidates=本机直连候选（§5.4，随 0x70 上送经 0x71 透传给对端）。
    /// bearerCaps=本端承载能力位图（M6-04，设计 11 §8.1；null=不上送——kill-switch/旧端形态）。</summary>
    public delegate Task<PunchRequestAck> PunchRequestSender(
        Guid targetDeviceId, Guid? triggerMappingId, string proto, EndpointPair requesterEndpoints,
        P2P.Core.Protocol.Endpoint[]? localCandidates, uint? bearerCaps, CancellationToken ct);

    /// <summary>0x76 上报缝（无 Ack 单发；02 §5.1③ 不等中转）。localCandidates 同 §5.4（经 Ack 透传）；
    /// bearerCaps 同 M6-04（仅 UDP 路径上送——TCP 打洞与 caps 正交，设计 11 §7.4）。</summary>
    public delegate Task EndpointReporter(Guid sessionId, EndpointPair endpoints,
        P2P.Core.Protocol.Endpoint[]? localCandidates, uint? bearerCaps, CancellationToken ct);

    /// <summary>STUN-UDP 探测缝：在打洞 socket 上探测本端公网端点。</summary>
    public delegate Task<IPEndPoint> StunProbeDelegate(Socket punchSocket, CancellationToken ct);

    /// <summary>STUN-TCP 探测缝（M2-04 StunTcpProber）：socket 为预绑定端口 L 的未连接句柄
    /// （SO_REUSEADDR 已设）；探测事务完成即关连接并释放句柄——调用方仅保留端口号 L 复用 listen。</summary>
    public delegate Task<IPEndPoint> StunTcpProbeDelegate(Socket punchSocket, CancellationToken ct);

    /// <summary>0x74 中继分配缝（M2-18，02 §6.1①）：宿主接 ControlClient（RelayClient.AllocateAsync）；
    /// 服务端错误（5002/1001/4005）以 <see cref="ControlErrorException"/> 抛出。未装配=不尝试回退。</summary>
    public delegate Task<RelayGrant> RelayAllocator(Guid punchSessionId, CancellationToken ct);

    /// <summary>设备对活中继会话查找缝（M2-37，05 §4）：宿主接 TunnelHost.Get（ViaRelay 且未关闭）。
    /// 命中 → 回切重打失败后的回退直接复用该会话，不再 0x74 新分配整体替换（NET-75 排水
    /// 会杀死传输中 channel——60s 回切周期下跨周期传输必断，公网实测 M2-37 缺陷）。
    /// 未装配=不复用（单测缺省行为，与 M2-18 语义一致）。</summary>
    public delegate TunnelSession? LiveRelaySessionLookup(Guid peerDeviceId);

    /// <summary>0x77 齐射锚点等待缝（02 §5.2⑤，宿主接 ControlClient.WaitForPunchFireAsync）：
    /// true=锚点到（与 A 的 0x70 Ack 同刻下发）→ 倒计时 Δ 后与 A 齐射；false=超时兜底立即开火。
    /// 未装配=建池即打（单测缺省，行为同旧版——时差由波次补射覆盖）。</summary>
    public delegate Task<bool> PunchFireWaiter(Guid sessionId, TimeSpan timeout, CancellationToken ct);

    /// <summary>0x78 起跑申请缝（02 §5.2⑥，宿主接 ControlClient.SendRequestAsync&lt;PunchStartGo&gt;；
    /// N 由宿主闭包取 settings 当前值，同 0x70 口径）。未装配=晚探测流程整体跳过（单测缺省）。</summary>
    public delegate Task<PunchStartGo> PunchStartSender(Guid targetDeviceId, Guid? triggerMappingId,
        string proto, CancellationToken ct);

    /// <summary>STUN-TCP 打洞交换缝（02 §5.2⑥，宿主接 StunTcpProber.ProbeExchangeAsync）：
    /// socket 为预绑定端口 L 的未连接句柄；**保持打开**返回（映射钉活，生命周期归调用方）。</summary>
    public delegate Task<StunTcpProber.PunchExchange> StunTcpExchangeDelegate(Socket punchSocket,
        Guid punchSessionId, IReadOnlyList<IPEndPoint>? candidates, CancellationToken ct);

    private readonly PunchRequestSender _sendPunchRequest;
    private readonly EndpointReporter _reportEndpoints;
    private readonly StunProbeDelegate _probe;
    private readonly StunTcpProbeDelegate? _tcpProbe;
    private readonly EcKeyPair _staticKey; // 宿主所有（随 state 落盘重建），Puncher 不释放
    private readonly ITunnelChannelHandler _handler;
    private readonly PunchOptions _options;
    private readonly Func<Guid, bool> _relayFallback; // 本地设备级回退配置（缺省恒 false=默认关，PRD 06 §2）
    private readonly RelayAllocator? _relayAllocator; // 0x74 分配缝（缺省不回退，M2-18）
    private readonly LiveRelaySessionLookup? _liveRelaySession; // 活中继会话复用缝（缺省不复用，M2-37）
    private readonly Action<string>? _log; // 诊断日志缝（P4 修复：TCP 打洞计划/池收尾线，缺省静默）
    private readonly Func<IPAddress?, IReadOnlyList<IPAddress>> _localIps; // 本机 IP 枚举缝（§5.4 候选源，缺省真实枚举）
    private readonly PunchFireWaiter? _punchFireWaiter; // 0x77 锚点等待缝（§5.2⑤，缺省建池即打）
    private readonly PunchStartSender? _sendPunchStart; // 0x78 起跑缝（§5.2⑥，缺省跳过晚探测流程）
    private readonly StunTcpExchangeDelegate? _tcpExchange; // STUN-TCP 交换缝（§5.2⑥，缺省跳过晚探测流程）
    private readonly Func<uint>? _localBearerCaps; // 本端承载能力位图缝（M6-04，设计 11 §8.2：
    // 发送时点取值随 0x70/0x76 上送+判定用同值；缺省 null=不上送位、判定恒 false=裸 UDP——
    // DisableKcpBearer 测试缝/kill-switch 双层合成后的最终形态）
    private int _disposed;

    public Puncher(PunchRequestSender sendPunchRequest, EndpointReporter reportEndpoints,
        StunProbeDelegate probe, EcKeyPair staticKey,
        ITunnelChannelHandler? handler = null, PunchOptions? options = null,
        StunTcpProbeDelegate? tcpProbe = null, Func<Guid, bool>? relayFallbackLookup = null,
        RelayAllocator? relayAllocator = null, LiveRelaySessionLookup? liveRelaySessionLookup = null,
        Action<string>? log = null, Func<IPAddress?, IReadOnlyList<IPAddress>>? localIpEnumerator = null,
        PunchFireWaiter? punchFireWaiter = null, PunchStartSender? punchStartSender = null,
        StunTcpExchangeDelegate? tcpExchange = null, Func<uint>? localBearerCaps = null)
    {
        _sendPunchRequest = sendPunchRequest;
        _reportEndpoints = reportEndpoints;
        _log = log;
        _probe = probe;
        _tcpProbe = tcpProbe;
        _staticKey = staticKey;
        _handler = handler ?? NullChannelHandler.Instance;
        _options = options ?? new PunchOptions();
        _relayFallback = relayFallbackLookup ?? (_ => false); // 未装配=默认关闭（与 peers.json 无条目同口径）
        _relayAllocator = relayAllocator;
        _liveRelaySession = liveRelaySessionLookup;
        _localIps = localIpEnumerator ?? EnumerateLocalIps;
        _punchFireWaiter = punchFireWaiter;
        _sendPunchStart = punchStartSender;
        _tcpExchange = tcpExchange;
        _localBearerCaps = localBearerCaps;
    }

    /// <summary>本端 caps 上送值（发送时点取值；0=不上送——null 字段=旧端/kill-switch 形态）。</summary>
    private uint? CapsForWire()
    {
        var caps = _localBearerCaps?.Invoke() ?? 0u;
        return caps != 0 ? caps : null;
    }

    /// <summary>承载协商判定（M6-04，设计 11 §8.2 两端同式）：my=0x70/0x76 上送值（判定用
    /// 发送时点同值——与本端设置后续变化解耦），peer=对端透传值（null=旧端=0）。</summary>
    private static bool KcpBearerEnabled(uint myCaps, uint? peerCaps)
        => BearerCaps.KcpEnabled(myCaps, peerCaps ?? 0u);

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
                ? await InitiateTcpAsync(targetDeviceId, triggerMappingId, ct) // 编排器自管双流程窗口（§5.2⑥）
                : await InitiateUdpAsync(targetDeviceId, triggerMappingId, punchCts.Token, ct);
        }
        catch (Exception e)
        {
            return PunchOutcome.Failure(targetDeviceId,
                e is OperationCanceledException && !ct.IsCancellationRequested
                    ? "punch_timeout"
                    : $"punch_error: {e.Message}", proto);
        }
    }

    private async Task<PunchOutcome> InitiateUdpAsync(Guid targetDeviceId, Guid? triggerMappingId,
        CancellationToken punchCt, CancellationToken ct)
    {
        var socket = CreatePunchSocket(_options.BindAddress);
        UdpPunchTransport? transport = null; // 接管 socket 后的释放责任
        KcpTunnelTransport? kcpTransport = null; // M6-05：Adopt 后释放责任（socket 所有权随 Adopt 移交）
        var established = false;
        try
        {
            // ① 出队后即时 STUN 探测（专用 socket=后续承载）
            IPEndPoint local;
            try { local = await _probe(socket, punchCt); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return PunchOutcome.Failure(targetDeviceId, $"stun_failed: {e.Message}", "udp");
            }

            // ② 第一段上送：端点+本机直连候选+承载能力位图随 0x70；Ack 延后至 B 的 0x76（4005/5001 即时失败）
            var candidates = BuildLocalCandidates(((IPEndPoint)socket.LocalEndPoint!).Port);
            var myCaps = _localBearerCaps?.Invoke() ?? 0u; // 判定用上送同值（§8.2 两端同式输入）
            PunchRequestAck ack;
            try
            {
                ack = await _sendPunchRequest(targetDeviceId, triggerMappingId, "udp",
                    new EndpointPair(ToProtocolEndpoint(local), null), candidates,
                    myCaps != 0 ? myCaps : null, punchCt);
            }
            catch (ControlErrorException e)
            {
                return PunchOutcome.Failure(targetDeviceId, $"server_{e.Code}: {e.HttpLikeMsg}", "udp");
            }

            // 回退资格合成（05 §3.1，M2-23 供 M2-18）：本地设备级配置 AND 服务端中继开关
            var relayAllowed = _relayFallback(targetDeviceId) && ack.RelayAllowed;

            // ④ 收 Ack → 承载协商判定（开火前完成，§8.2：Ack 到手即两端输入齐备）→
            //    THello1 扇发映射端点+对端候选（§5.4 直连优先；首个到达包钉定回程）→ PTP 握手
            try
            {
                var peerEp = ParseEndpoint(ack.PeerEndpoints.Udp)
                    ?? throw new IOException("PunchRequest Ack 未携带对端 UDP 端点");
                transport = new UdpPunchTransport(socket, peerEp, ParseEndpoints(ack.PeerLocalCandidates));
                TunnelSession session;
                if (KcpBearerEnabled(myCaps, ack.PeerBearerCaps))
                {
                    // M6-05 KCP 承载（设计 11 §7.2）：Puncher 外驱动握手（M2-16 形态）→
                    // Adopt（socket 所有权移交）→ FromEstablishedKeys 接 KCP 承载
                    var keys = await HandshakeInitiateAsync(transport, ack.SessionId, ack.Peer.StaticPubKey,
                        punchCt).ConfigureAwait(false);
                    kcpTransport = KcpTunnelTransport.Adopt(socket, transport.Peer, _options.KcpBearerOptions);
                    transport.ReleaseSocket(); // 此后 UdpPunchTransport 释放不关 socket（§7.3）
                    session = TunnelSession.FromEstablishedKeys(ack.SessionId, ack.Peer.DeviceId, true,
                        keys, kcpTransport, _handler,
                        new TunnelSessionOptions { KeepaliveInterval = TimeSpan.FromSeconds(_options.KeepaliveSec) },
                        staticKey: _staticKey, peerStaticPub: ack.Peer.StaticPubKey);
                }
                else
                {
                    // 现状裸路径：逐字节不动（混布降级形态）
                    session = await TunnelSession.ConnectAsync(ack.SessionId, ack.Peer.DeviceId,
                        _staticKey, ack.Peer.StaticPubKey, transport, _handler,
                        new TunnelSessionOptions
                        {
                            KeepaliveInterval = TimeSpan.FromSeconds(_options.KeepaliveSec),
                            HandshakeTimeout = _options.PunchTimeout,
                            HandshakeResendCount = _options.BurstCount,
                            HandshakeResendInterval = _options.BurstInterval,
                        });
                }
                established = true; // 成功：socket 归会话（Disconnect 时承载释放）
                return PunchOutcome.Success(ack.SessionId, ack.Peer.DeviceId, session, local, transport.Peer, "udp");
            }
            catch (Exception e)
            {
                return await FailOrFallbackAsync(ack.SessionId, ack.Peer.DeviceId, ack.Peer.StaticPubKey,
                    targetDeviceId, e, ct, relayAllowed, "udp").ConfigureAwait(false);
            }
        }
        finally
        {
            // 所有权链（§7.3）：Adopt 前失败=transport 关 socket；Adopt 后失败=kcpTransport 关
            // socket（transport 已 ReleaseSocket 不再关）；成功=承载随会话生灭
            if (kcpTransport is not null)
            {
                if (!established) await kcpTransport.DisposeAsync();
            }
            else if (transport is null) socket.Dispose();
            else if (!established) await transport.DisposeAsync();
        }
    }

    /// <summary>KCP 路径发起方握手（M6-05，设计 11 §7；M2-16 TCP 先例同款外驱动形态）：
    /// THello1 连发（打洞窗口丢包容忍；收到 THello2 即停）→ HandleTHello2 → 发 TConfirm →
    /// 产出会话密钥。时序语义与 <see cref="TunnelSession.ConnectAsync"/> 握手段一致（02 §5.1）。</summary>
    private async Task<PtpSessionKeys> HandshakeInitiateAsync(UdpPunchTransport transport,
        Guid sessionId, byte[] peerStaticPub, CancellationToken punchCt)
    {
        using var initiator = PtpHandshake.StartInitiator(sessionId, _staticKey, peerStaticPub);
        await transport.SendAsync(initiator.THello1Wire, punchCt);
        using var resendCts = CancellationTokenSource.CreateLinkedTokenSource(punchCt);
        var resend = ResendHandshakeBurstAsync(transport, initiator.THello1Wire,
            _options.BurstCount, _options.BurstInterval, resendCts.Token);
        try
        {
            while (true)
            {
                var frame = await transport.ReceiveAsync(punchCt)
                    ?? throw new IOException("承载在握手期间关闭");
                if (PtpFrameCodec.ParseHeader(frame).Type != PtpFrameType.THello2) continue; // 打洞窗口杂包丢弃
                var (tConfirm, keys) = initiator.HandleTHello2(frame);
                await transport.SendAsync(tConfirm, punchCt);
                return keys;
            }
        }
        finally
        {
            resendCts.Cancel();
            try { await resend; } catch { /* 尽力而为重发 */ }
        }
    }

    /// <summary>打洞窗握手帧周期连发（ConnectAsync.ResendHandshakeAsync 同语义副本：首发已由
    /// 调用方发出，此处补发 N-1 次；收到应答/超时经 linked cts 停止）。</summary>
    private static async Task ResendHandshakeBurstAsync(UdpPunchTransport transport, byte[] wire,
        int burstCount, TimeSpan burstInterval, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(burstInterval, TimeProvider.System);
            for (var i = 1; i < burstCount; i++)
            {
                if (!await timer.WaitForNextTickAsync(ct)) return;
                await transport.SendAsync(wire, ct);
            }
        }
        catch (OperationCanceledException) { /* 收到应答/超时：停止连发 */ }
        catch (Exception) { /* 重发尽力而为，失败不阻断握手等待 */ }
    }

    /// <summary>TCP 打洞编排（02 §5.2⑥，2026-10-24）：晚探测新流程优先——0x78 起跑（双端并行探测、
    /// 端点经 STUN-TCP 交换晚收集、基准映射被探针连接钉活、扇形 +0..+(N−1)）；服务端/对端不支持
    /// （0x78 无应答=旧服务端静默丢弃，02 §7）→ 全窗降级旧 0x70 流程。两流程各自独立打洞窗。</summary>
    private async Task<PunchOutcome> InitiateTcpAsync(Guid targetDeviceId, Guid? triggerMappingId,
        CancellationToken ct)
    {
        if (_sendPunchStart is not null && _tcpExchange is not null)
        {
            using var lateCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lateCts.CancelAfter(_options.PunchTimeout);
            var late = await TryInitiateTcpLateAsync(targetDeviceId, triggerMappingId, lateCts.Token, ct)
                .ConfigureAwait(false);
            if (late is not null) return late; // null=流程不可用（旧服务端）→ 降级
        }
        using var punchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        punchCts.CancelAfter(_options.PunchTimeout);
        return await InitiateTcpLegacyAsync(targetDeviceId, triggerMappingId, punchCts.Token, ct)
            .ConfigureAwait(false);
    }

    /// <summary>晚探测新流程 A 侧（02 §5.2⑥）：0x78 起跑（服务端同刻双推 Go）→ 本地端口 L 交换探测
    ///（连接**保持打开**=映射钉活，应答携对端端点=开火锚点）→ 扇形齐射（无闸即打：双端应答同刻
    /// 出发、开火差≈投递差）。返回 null=降级旧流程（仅 0x78 无应答/发送层瞬时失败——服务端未识别）。</summary>
    private async Task<PunchOutcome?> TryInitiateTcpLateAsync(Guid targetDeviceId, Guid? triggerMappingId,
        CancellationToken punchCt, CancellationToken ct)
    {
        var sendStart = _sendPunchStart!; // 编排器已判非空（InitiateTcpAsync 装配检查）
        var exchange = _tcpExchange!;

        // ① 0x78 起跑申请：2s 无应答=旧服务端（未知类型静默丢弃）→ null 降级
        PunchStartGo go;
        try
        {
            using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(punchCt);
            reqCts.CancelAfter(_options.PunchStartReplyTimeout);
            go = await sendStart(targetDeviceId, triggerMappingId, "tcp", reqCts.Token)
                .ConfigureAwait(false);
        }
        catch (ControlErrorException e) when (e.Code == ErrorCode.BadRequest && e.HttpLikeMsg.Contains("not_implemented"))
        {
            return null; // 已注册未挂载：服务端不识 0x78（防御路径——旧服务端实为静默丢弃走超时）
        }
        catch (ControlErrorException e)
        {
            return PunchOutcome.Failure(targetDeviceId, $"server_{e.Code}: {e.HttpLikeMsg}", "tcp"); // 服务端已识别并拒绝（授权/离线）——旧流程同结果
        }
        catch (OperationCanceledException) when (!punchCt.IsCancellationRequested)
        {
            return null; // 2s 无应答：旧服务端静默丢弃（02 §7）→ 降级旧流程
        }
        catch (Exception)
        {
            return null; // 控制层瞬时故障：降级旧流程（其发送路径另试）
        }

        var relayAllowed = _relayFallback(targetDeviceId) && go.RelayAllowed;
        var n = PunchPolicy.Normalize(go.PunchCount);

        // ② 本地端口 L + STUN-TCP 交换探测（保持打开）：对端端点即开火锚点
        var portL = 0;
        Socket exchangeSocket;
        StunTcpProber.PunchExchange x;
        try
        {
            exchangeSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            TcpPunchFleet.SetPunchReuse(exchangeSocket);
            exchangeSocket.Bind(new IPEndPoint(_options.BindAddress ?? IPAddress.Any, 0));
            portL = ((IPEndPoint)exchangeSocket.LocalEndPoint!).Port;
            x = await exchange(exchangeSocket, go.SessionId,
                ToIpEndPoints(BuildLocalCandidates(portL)), punchCt).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // 交换失败（对端旧版/丢失/连接不通）：不降级——0x78 会话已在服务端建立，旧流程会被
            // 设备对去重吞掉；直接失败走中继回退（133102 类 CGNAT 的兜底路径不变）
            exchangeSocket = null!;
            var fail = new PunchOutcome(false, go.SessionId, targetDeviceId, null, null, null,
                $"exchange_failed: {e.Message}", relayAllowed, "tcp");
            return await RelayOrReturnAsync(fail, go.Peer.StaticPubKey, ct).ConfigureAwait(false);
        }

        // ③ 扇形齐射（应答即锚点：无闸即打；波次补射保窗内重试）
        try
        {
            var targets = TcpPunchPlan.TargetEndpoints(x.PeerEndpoint, n);
            var directTargets = BuildDirectTargets(targets, ToProtocolEndpoints(x.PeerCandidates));
            _log?.Invoke($"计划(A′) target={targetDeviceId.ToString()[..8]} mapped={x.Mapped} L={portL}"
                        + $" N={n} peerMapped={x.PeerEndpoint} fan=[{string.Join(",", (IEnumerable<IPEndPoint>)targets)}]"
                        + (directTargets.Count > 0 ? $" direct=[{string.Join(",", directTargets)}]" : ""));

            using var initiator = PtpHandshake.StartInitiator(go.SessionId, _staticKey, go.Peer.StaticPubKey);
            var fleet = TcpPunchFleet.Create(_options.BindAddress, portL, targets, directTargets, n,
                PtpFrameType.THello2,
                onConnection: t => t.SendAsync(initiator.THello1Wire, punchCt),
                waveInterval: _options.TcpWaveInterval);
            try
            {
                using var burstCts = CancellationTokenSource.CreateLinkedTokenSource(punchCt);
                var burst = TcpBurstAsync(fleet, initiator.THello1Wire, burstCts.Token);
                try
                {
                    var (winner, tHello2) = await fleet.WaitFrameAsync(punchCt).ConfigureAwait(false);
                    var (tConfirm, keys) = initiator.HandleTHello2(tHello2);
                    var transport = fleet.Release(winner);
                    await transport.SendAsync(tConfirm, punchCt).ConfigureAwait(false);
                    var session = TunnelSession.FromEstablishedKeys(go.SessionId, go.Peer.DeviceId, true,
                        keys, transport, _handler, new TunnelSessionOptions
                        {
                            KeepaliveInterval = TimeSpan.FromSeconds(_options.KeepaliveSec),
                            HandshakeTimeout = _options.PunchTimeout,
                        }, staticKey: _staticKey, peerStaticPub: go.Peer.StaticPubKey);
                    return PunchOutcome.Success(go.SessionId, go.Peer.DeviceId, session,
                        winner.LocalEndPoint!, winner.RemoteEndPoint!, "tcp");
                }
                finally
                {
                    burstCts.Cancel();
                    try { await burst; } catch { /* 补发尽力而为 */ }
                }
            }
            finally
            {
                _log?.Invoke($"池收尾(A′) live={fleet.LiveConnections} failedConnect={fleet.FailedConnects}"
                            + $" waves={fleet.Waves} directRefused={fleet.DirectRefused}");
                await fleet.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            var fail = new PunchOutcome(false, go.SessionId, go.Peer.DeviceId, null, null, null,
                e is OperationCanceledException && !ct.IsCancellationRequested
                    ? "punch_timeout" : $"punch_error: {e.Message}", relayAllowed, "tcp");
            return await RelayOrReturnAsync(fail, go.Peer.StaticPubKey, ct).ConfigureAwait(false);
        }
        finally
        {
            exchangeSocket?.Close(); // 交换连接生命周期=本次打洞（收尾释放；映射钉活使命已完成）
        }
    }

    /// <summary>TCP 打洞访问方·旧流程（02 §5.2①③④）：端口 L 探测 → 0x70{tcp:L'} → Ack{对端 L', N} →
    /// listen(L)+N 并发 connect（扇形 +0..+(N−1)）→ 全部候选连接扇出 THello1、首条 THello2
    /// 到达的连接胜出（对端仅在收到 THello1 的连接上应答，天然消歧，02 §5.2④）。
    /// 新客户端亦为对端旧版的混布路径（0x78 不可用时兜底）。</summary>
    private async Task<PunchOutcome> InitiateTcpLegacyAsync(Guid targetDeviceId, Guid? triggerMappingId,
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
            return PunchOutcome.Failure(targetDeviceId, $"stun_failed: {e.Message}", "tcp");
        }

        // ② 第一段上送（端点+本机直连候选在 tcp 槽位随行）；Ack 延后至 B 的 0x76（4005/5001 即时失败）
        var candidates = BuildLocalCandidates(portL);
        PunchRequestAck ack;
        try
        {
            ack = await _sendPunchRequest(targetDeviceId, triggerMappingId, "tcp",
                new EndpointPair(null, ToProtocolEndpoint(mappedL)), candidates,
                bearerCaps: null, ct: punchCt); // TCP 路径与 caps 正交（设计 11 §7.4）
        }
        catch (ControlErrorException e)
        {
            return PunchOutcome.Failure(targetDeviceId, $"server_{e.Code}: {e.HttpLikeMsg}", "tcp");
        }

        // 回退资格合成（05 §3.1，M2-23 供 M2-18）：本地设备级配置 AND 服务端中继开关
        var relayAllowed = _relayFallback(targetDeviceId) && ack.RelayAllowed;

        try
        {
            var peerMapped = ParseEndpoint(ack.PeerEndpoints.Tcp)
                ?? throw new IOException("PunchRequest Ack 未携带对端 TCP 端点");
            var n = PunchPolicy.Normalize(ack.PunchCount); // OQ-19：以服务端回填 N 为准
            var targets = TcpPunchPlan.TargetEndpoints(peerMapped, n);
            var directTargets = BuildDirectTargets(targets, ack.PeerLocalCandidates); // §5.4 直连优先（扇形已含偏移 0）
            // 计划线（P4 修复）：本端映射端点/端口 L/扇形 N/瞄准端点全量落日志——现场判
            // "瞄准是否命中对端实际分配"（H2 漂移）/ 对照对端计划线定位偏差侧
            _log?.Invoke($"计划(A) target={targetDeviceId.ToString()[..8]} mapped={mappedL} L={portL}"
                        + $" N={n} peerMapped={peerMapped} fan=[{string.Join(",", (IEnumerable<IPEndPoint>)targets)}]"
                        + (directTargets.Count > 0 ? $" direct=[{string.Join(",", directTargets)}]" : ""));

            // ③④ listen(L) + N 并发 connect（波次补射）→ 候选连接扇出 THello1 → 首条 THello2 的连接胜出；
            //    齐射闸（§5.2⑤ 方案 A）：A 以 Ack 到达为锚倒数 Δ——与 B 的 0x77 锚点同刻（服务端背靠背
            //    下发），双端到点齐射 SYN 空中交叉；directTargets 各起拒连重试直连探测（§5.4，后发不扰预测基准）
            using var initiator = PtpHandshake.StartInitiator(ack.SessionId, _staticKey, ack.Peer.StaticPubKey);
            var fleet = TcpPunchFleet.Create(_options.BindAddress, portL, targets, directTargets, n,
                PtpFrameType.THello2,
                onConnection: t => t.SendAsync(initiator.THello1Wire, punchCt), // 建立即发首帧
                volleyGate: ct => Task.Delay(PunchPolicy.TcpVolleyCountdown, ct),
                waveInterval: _options.TcpWaveInterval);
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
                        }, staticKey: _staticKey, peerStaticPub: ack.Peer.StaticPubKey);
                    return PunchOutcome.Success(ack.SessionId, ack.Peer.DeviceId, session,
                        winner.LocalEndPoint!, winner.RemoteEndPoint!, "tcp");
                }
                finally
                {
                    burstCts.Cancel();
                    try { await burst; } catch { /* 补发尽力而为 */ }
                }
            }
            finally
            {
                // 池收尾线（P4 修复）：failedConnect>0=有候选被明确拒（RST，过滤/防火墙征象）；
                // 全 0 而超时=静默丢弃（NAT 不回注/H1 入站拦截）——H4/H1 现场判别依据；
                // waves=波次补射已发波数（failed/(waves×N)≈RST 密度）；directRefused=直连探测累计拒连（§5.4）
                _log?.Invoke($"池收尾(A) live={fleet.LiveConnections} failedConnect={fleet.FailedConnects}"
                            + $" waves={fleet.Waves} directRefused={fleet.DirectRefused}");
                await fleet.DisposeAsync(); // 幂等：成功路径已 Release 胜出，仅关其余 N−1 与 listener
            }
        }
        catch (Exception e)
        {
            return await FailOrFallbackAsync(ack.SessionId, ack.Peer.DeviceId, ack.Peer.StaticPubKey,
                targetDeviceId, e, ct, relayAllowed, "tcp").ConfigureAwait(false);
        }
    }

    /// <summary>Ack 后失败（打洞/握手阶段）：失败语义同外层（超时/错误二分），并携带回退资格（05 §3.1）。
    /// SessionId=会话两段式完成的服务端台账在册 id（0x72 failed 行落库前提，M2-22）。
    /// proto（M7-04，设计 12 §6.1）：执行层口径——中继回退产物继承此值。</summary>
    private PunchOutcome FailAfterAck(Guid sessionId, Guid targetDeviceId, Exception e, CancellationToken ct,
        bool relayAllowed, string proto)
        => new(false, sessionId, targetDeviceId, null, null, null,
            e is OperationCanceledException && !ct.IsCancellationRequested
                ? "punch_timeout"
                : $"punch_error: {e.Message}",
            relayAllowed, proto);

    /// <summary>失败统一出口（参数化对端三元组——0x70 Ack 与 0x78 Go 两流程共用）。</summary>
    private async Task<PunchOutcome> FailOrFallbackAsync(Guid sessionId, Guid peerDeviceId,
        byte[] peerStaticPubKey, Guid targetDeviceId, Exception e, CancellationToken ct, bool relayAllowed,
        string proto)
        => await RelayOrReturnAsync(FailAfterAck(sessionId, targetDeviceId, e, ct, relayAllowed, proto),
            peerStaticPubKey, ct).ConfigureAwait(false);

    /// <summary>回退裁决：资格假/未装 0x74 缝=纯失败；M2-37 回退复用：设备对已有活中继会话
    ///（回切周期重打失败场景）→ 直接复用该会话返回成功结果，不再 0x74 新分配——新会话整体替换
    /// 旧会话走 NET-75 排水窗会杀死传输中 channel，60s 回切周期下跨周期传输必断；对端被动跟随
    /// 0x74（02 §6.1②），本侧不分配则对端同样不重建。复用会话此后真亡（keepalive 判死摘表，
    /// TunnelHost.OnSessionDisconnected）→ 查找落空自然回到全新分配路径。</summary>
    private async Task<PunchOutcome> RelayOrReturnAsync(PunchOutcome fail, byte[] peerStaticPubKey,
        CancellationToken ct)
    {
        if (!fail.RelayAllowed || _relayAllocator is null) return fail;
        if (_liveRelaySession?.Invoke(fail.PeerDeviceId) is { IsClosed: false } existing)
            return new PunchOutcome(true, fail.SessionId, fail.PeerDeviceId, existing, null, null, null,
                Proto: fail.Proto); // M7-04：继承本次尝试协议（设计 12 §6.1 中继回退产物）
        return await FallbackToRelayAsync(fail, peerStaticPubKey, ct).ConfigureAwait(false);
    }

    /// <summary>中继回退（02 §4.5 承载绑定/§6.1①，M2-18）：0x74 分配（打洞会话须在服务端台账 120s 内，
    /// 仅 Ack 后失败有会话可解析）→ JOIN（TCP 优先、DROP 型封锁 OCE 转 UDP 兜底）→ PTP 握手即经
    /// 中继承载——同一 <see cref="TunnelSession"/> 帧改发 relay 地址，解帧逻辑不变（05 §4 加密与路径解耦）。
    /// 回退失败并入打洞失败原因（映射态 failed）。</summary>
    private async Task<PunchOutcome> FallbackToRelayAsync(PunchOutcome fail, byte[] peerStaticPubKey,
        CancellationToken ct)
    {
        RelayTransport? transport = null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.RelayFallbackTimeout); // 打洞 cts 已耗尽，回退独立计量
            var grant = await _relayAllocator!(fail.SessionId, cts.Token).ConfigureAwait(false);
            transport = await RelayClient.JoinWithCarrierFallbackAsync(grant, ct: cts.Token).ConfigureAwait(false);
            // THello1 周期重发覆盖对端 JOIN 就绪窗口（relay 无缓冲：对端未 JOIN 期间包丢弃，M2-07）
            var resend = Math.Max(2, (int)(_options.RelayFallbackTimeout / _options.RelayHandshakeResendInterval));
            var session = await TunnelSession.ConnectAsync(fail.SessionId, fail.PeerDeviceId,
                _staticKey, peerStaticPubKey, transport, _handler,
                new TunnelSessionOptions
                {
                    KeepaliveInterval = TimeSpan.FromSeconds(_options.KeepaliveSec),
                    HandshakeTimeout = _options.RelayFallbackTimeout,
                    HandshakeResendCount = resend,
                    HandshakeResendInterval = _options.RelayHandshakeResendInterval,
                }).ConfigureAwait(false);
            return new PunchOutcome(true, fail.SessionId, fail.PeerDeviceId, session,
                null, transport.RemoteEndPoint, null, Proto: fail.Proto); // 继承本次尝试协议（M7-04）
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            if (transport is not null) await transport.DisposeAsync().ConfigureAwait(false);
            return fail with { FailReason = $"{fail.FailReason ?? "punch_failed"}；relay_failed: {e.Message}" };
        }
    }

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
                    : $"punch_error: {e.Message}",
                invite.PeerEndpoints.Tcp is not null ? "tcp" : "udp"); // 与分派同判（M7-04）
        }
    }

    private async Task<PunchOutcome> RespondUdpAsync(PunchInvite invite, CancellationToken punchCt)
    {
        var socket = CreatePunchSocket(_options.BindAddress);
        UdpPunchTransport? transport = null;
        KcpTunnelTransport? kcpTransport = null; // M6-05：Adopt 后释放责任（socket 所有权随 Adopt 移交）
        var established = false;
        try
        {
            // ③ 即时 STUN 探测（失败无有效端点可报：不上报 0x76，服务端 invite_timeout 兜底 A 侧）
            IPEndPoint local;
            try { local = await _probe(socket, punchCt); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return PunchOutcome.Failure(invite.Peer.DeviceId, $"stun_failed: {e.Message}", "udp");
            }

            // ③ 先校验发起方端点（无效邀请不污染 0x76 上报），再回传端点+本机候选+承载能力位图
            //（第二段，无 Ack 单发）。承载协商判定输入=Invite.PeerBearerCaps（A 的 caps 透传，§8.2）
            var peerEp = ParseEndpoint(invite.PeerEndpoints.Udp)
                ?? throw new IOException("PunchInvite 未携带发起方 UDP 端点");
            var candidates = BuildLocalCandidates(((IPEndPoint)socket.LocalEndPoint!).Port);
            var myCaps = _localBearerCaps?.Invoke() ?? 0u;
            await _reportEndpoints(invite.SessionId,
                new EndpointPair(ToProtocolEndpoint(local), null), candidates,
                myCaps != 0 ? myCaps : null, punchCt);

            transport = new UdpPunchTransport(socket, peerEp, ParseEndpoints(invite.LocalCandidates));

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

                    if (!KcpBearerEnabled(myCaps, invite.PeerBearerCaps))
                    {
                        // 现状裸路径：逐字节不动（混布降级形态）
                        var rawSession = await TunnelSession.AcceptAsync(invite.Peer.DeviceId, frame,
                            _staticKey, invite.Peer.StaticPubKey, transport, _handler,
                            new TunnelSessionOptions
                            {
                                KeepaliveInterval = TimeSpan.FromSeconds(_options.KeepaliveSec),
                                HandshakeTimeout = _options.PunchTimeout,
                            });
                        established = true;
                        return PunchOutcome.Success(invite.SessionId, invite.Peer.DeviceId, rawSession, local,
                            transport.Peer, "udp"); // §5.4：钉定后的实际可达对端（直连路径≠映射端点）
                    }

                    // M6-05 KCP 承载（设计 11 §7.2）：响应方握手外驱动（THello1 在手→THello2→
                    // TConfirm）→ Adopt（socket 所有权移交）→ FromEstablishedKeys
                    using var responder = PtpHandshake.AcceptTHello1(frame, _staticKey,
                        invite.Peer.StaticPubKey);
                    await transport.SendAsync(responder.THello2Wire, punchCt);
                    byte[] confirmWire;
                    while (true)
                    {
                        var next = await transport.ReceiveAsync(punchCt)
                            ?? throw new IOException("打洞承载关闭");
                        if (PtpFrameCodec.ParseHeader(next).Type != PtpFrameType.TConfirm) continue;
                        confirmWire = next;
                        break;
                    }
                    var keys = responder.VerifyTConfirm(confirmWire);
                    kcpTransport = KcpTunnelTransport.Adopt(socket, transport.Peer, _options.KcpBearerOptions);
                    transport.ReleaseSocket(); // 此后 UdpPunchTransport 释放不关 socket（§7.3）
                    var session = TunnelSession.FromEstablishedKeys(invite.SessionId, invite.Peer.DeviceId,
                        false, keys, kcpTransport, _handler,
                        new TunnelSessionOptions { KeepaliveInterval = TimeSpan.FromSeconds(_options.KeepaliveSec) },
                        staticKey: _staticKey, peerStaticPub: invite.Peer.StaticPubKey);
                    established = true;
                    return PunchOutcome.Success(invite.SessionId, invite.Peer.DeviceId, session, local,
                        transport.Peer, "udp");
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
            // 所有权链（§7.3）：Adopt 前失败=transport 关 socket；Adopt 后失败=kcpTransport 关
            // socket；成功=承载随会话生灭
            if (kcpTransport is not null)
            {
                if (!established) await kcpTransport.DisposeAsync();
            }
            else if (transport is null) socket.Dispose();
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
            return PunchOutcome.Failure(invite.Peer.DeviceId, $"stun_failed: {e.Message}", "tcp");
        }

        // ③ 先校验发起方端点（无效邀请不污染 0x76 上报）
        var peerMapped = ParseEndpoint(invite.PeerEndpoints.Tcp)
            ?? throw new IOException("PunchInvite 未携带发起方 TCP 端点");
        var n = PunchPolicy.Normalize(invite.PunchCount); // OQ-19：与发起方同一 N（服务端统一下发）
        var targets = TcpPunchPlan.TargetEndpoints(peerMapped, n);
        var directTargets = BuildDirectTargets(targets, invite.LocalCandidates); // §5.4（扇形已含偏移 0）
        _log?.Invoke($"计划(B) target={invite.Peer.DeviceId.ToString()[..8]} mapped={mappedL} L={portL}"
                    + $" N={n} peerMapped={peerMapped} fan=[{string.Join(",", (IEnumerable<IPEndPoint>)targets)}]"
                    + (directTargets.Count > 0 ? $" direct=[{string.Join(",", directTargets)}]" : ""));

        // 建池先于 0x76（listen 口尽早开门迎 A 的直连探测/accept）；齐射经锚点闸（§5.2⑤ 方案 A）：
        // 等 0x77 PunchFire（服务端与 A 的 Ack 同刻下发）→ 双端各倒数同一 Δ 到点齐射（SYN 空中交叉，
        // 规避旧版「建池即打早于 A 映射建立、SYN 打空门」竞态）；锚点超时/缝未装配 → 立即开火兜底
        //（旧行为，残余时差由波次补射覆盖）
        var fleet = TcpPunchFleet.Create(_options.BindAddress, portL, targets, directTargets, n, PtpFrameType.THello1,
            volleyGate: async ct =>
            {
                if (_punchFireWaiter is null) return; // 单测缺省：建池即打
                bool fired;
                try { fired = await _punchFireWaiter(invite.SessionId, _options.PunchFireFallbackTimeout, ct); }
                catch (OperationCanceledException) { throw; } // 打洞窗收尾/胜出：照常出闸取消
                catch { fired = false; }                       // 等待器异常：兜底立即开火
                _log?.Invoke($"锚点(B) fired={fired}");         // 现场判 0x77 链路（false=旧服务端/丢失）
                if (fired) await Task.Delay(PunchPolicy.TcpVolleyCountdown, ct); // 与 A 的 Ack 锚同 Δ
            },
            waveInterval: _options.TcpWaveInterval);
        try
        {
            await _reportEndpoints(invite.SessionId,
                new EndpointPair(null, ToProtocolEndpoint(mappedL)), BuildLocalCandidates(portL),
                bearerCaps: null, ct: punchCt); // TCP 路径与 caps 正交（设计 11 §7.4）

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
                winner.LocalEndPoint!, winner.RemoteEndPoint!, "tcp");
        }
        finally
        {
            _log?.Invoke($"池收尾(B) live={fleet.LiveConnections} failedConnect={fleet.FailedConnects}"
                        + $" waves={fleet.Waves} directRefused={fleet.DirectRefused}");
            await fleet.DisposeAsync(); // 幂等：成功路径已 Release 胜出，仅关其余候选与 listener
        }
    }

    /// <summary>晚探测新流程 B 侧（02 §5.2⑥，2026-10-24）：0x78 PunchStartGo 推送 → 本地端口 L 交换探测
    ///（连接保持打开=映射钉活；对端端点应答即开火锚点）→ 扇形齐射（无闸即打）→ 等任一连接 THello1 → 应答握手。</summary>
    public async Task<PunchOutcome> RespondGoAsync(PunchStartGo go, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(go);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (_tcpExchange is null)
            return PunchOutcome.Failure(go.Peer.DeviceId, "exchange_seam_missing: 交换探测缝未接线");
        using var punchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        punchCts.CancelAfter(_options.PunchTimeout);
        Socket? exchangeSocket = null;
        try
        {
            // ① 本地端口 L + STUN-TCP 交换探测（保持打开）：对端端点=开火锚点
            exchangeSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            TcpPunchFleet.SetPunchReuse(exchangeSocket);
            exchangeSocket.Bind(new IPEndPoint(_options.BindAddress ?? IPAddress.Any, 0));
            var portL = ((IPEndPoint)exchangeSocket.LocalEndPoint!).Port;
            var x = await _tcpExchange(exchangeSocket, go.SessionId,
                ToIpEndPoints(BuildLocalCandidates(portL)), punchCts.Token).ConfigureAwait(false);

            // ② 扇形齐射（应答即锚点：无闸即打；B 不扇出 THello1——等 A 的首帧天然消歧）
            var n = PunchPolicy.Normalize(go.PunchCount);
            var targets = TcpPunchPlan.TargetEndpoints(x.PeerEndpoint, n);
            var directTargets = BuildDirectTargets(targets, ToProtocolEndpoints(x.PeerCandidates));
            _log?.Invoke($"计划(B′) target={go.Peer.DeviceId.ToString()[..8]} mapped={x.Mapped} L={portL}"
                        + $" N={n} peerMapped={x.PeerEndpoint} fan=[{string.Join(",", (IEnumerable<IPEndPoint>)targets)}]"
                        + (directTargets.Count > 0 ? $" direct=[{string.Join(",", directTargets)}]" : ""));
            var fleet = TcpPunchFleet.Create(_options.BindAddress, portL, targets, directTargets, n,
                PtpFrameType.THello1, waveInterval: _options.TcpWaveInterval);
            try
            {
                var (winner, tHello1) = await fleet.WaitFrameAsync(punchCts.Token).ConfigureAwait(false);
                var transport = fleet.Release(winner);
                var session = await TunnelSession.AcceptAsync(go.Peer.DeviceId, tHello1,
                    _staticKey, go.Peer.StaticPubKey, transport, _handler,
                    new TunnelSessionOptions
                    {
                        KeepaliveInterval = TimeSpan.FromSeconds(_options.KeepaliveSec),
                        HandshakeTimeout = _options.PunchTimeout,
                    });
                return PunchOutcome.Success(go.SessionId, go.Peer.DeviceId, session,
                    winner.LocalEndPoint!, winner.RemoteEndPoint!, "tcp");
            }
            finally
            {
                _log?.Invoke($"池收尾(B′) live={fleet.LiveConnections} failedConnect={fleet.FailedConnects}"
                            + $" waves={fleet.Waves} directRefused={fleet.DirectRefused}");
                await fleet.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            return PunchOutcome.Failure(go.Peer.DeviceId,
                e is OperationCanceledException && !ct.IsCancellationRequested
                    ? "punch_timeout"
                    : $"punch_error: {e.Message}", "tcp");
        }
        finally
        {
            exchangeSocket?.Close(); // 交换连接生命周期=本次打洞
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

    private static List<IPEndPoint>? ParseEndpoints(P2P.Core.Protocol.Endpoint[]? endpoints)
        => endpoints is null ? null
            : endpoints.Select(ParseEndpoint).Where(ep => ep is not null).Select(ep => ep!).ToList();

    /// <summary>本机直连候选（§5.4）：本机 IPv4 × 打洞监听口（UDP=打洞 socket 本地口；TCP=端口 L）。
    /// BindAddress 非空（测试/NAT 模拟回环别名）时候选=该别名本身——同网段直连拓扑可测。</summary>
    private P2P.Core.Protocol.Endpoint[] BuildLocalCandidates(int port)
        => _localIps(_options.BindAddress)
            .Select(ip => ToProtocolEndpoint(new IPEndPoint(ip, port)))
            .ToArray();

    /// <summary>TCP 直连目标集（§5.4⑥）：对端本机候选，剔除与扇形目标重叠者
    ///（映射端点原样=扇形偏移 0 已内置扇形——候选仅为同网段/公网直达加成）。</summary>
    private static List<IPEndPoint> BuildDirectTargets(IReadOnlyList<IPEndPoint> fanTargets,
        P2P.Core.Protocol.Endpoint[]? peerCandidates)
    {
        // 对端无候选（旧端 null / 枚举降级空表）→ 整体跳过直连探测，退化为纯扇形路径
        //（02 §5.4 混布口径；映射端点原样=扇形偏移 0 已内置，候选仅为同网段/公网直达加成）
        if (peerCandidates is null || peerCandidates.Length == 0) return [];
        var list = new List<IPEndPoint>();
        foreach (var ep in peerCandidates)
            if (ParseEndpoint(ep) is { } parsed && !fanTargets.Contains(parsed) && !list.Contains(parsed))
                list.Add(parsed);
        return list;
    }

    /// <summary>协议端点 → IPEndPoint 集（交换探测候选入参用；不可解析项丢弃）。</summary>
    private static IPEndPoint[] ToIpEndPoints(P2P.Core.Protocol.Endpoint[]? endpoints)
        => endpoints is null ? [] : endpoints.Select(ParseEndpoint).Where(ep => ep is not null).Select(ep => ep!).ToArray();

    /// <summary>IPEndPoint 集 → 协议端点（交换应答候选出参用）。</summary>
    private static P2P.Core.Protocol.Endpoint[]? ToProtocolEndpoints(IPEndPoint[] endpoints)
        => endpoints.Length == 0 ? null : endpoints.Select(ToProtocolEndpoint).ToArray();

    /// <summary>本机 IPv4 枚举（§5.4 缺省实现）：BindAddress 非空 → 仅该地址；否则复用
    /// SubnetSurvey（仅 IPv4、排除 loopback），滤 100.64.0.0/10 虚拟网段，去重限 8 个；异常降级空表。</summary>
    private static IReadOnlyList<IPAddress> EnumerateLocalIps(IPAddress? bind)
    {
        if (bind is not null) return [bind];
        try
        {
            return SubnetSurvey.Snapshot().Addresses
                .Select(a => a.Address)
                .Where(ip => !IsVirtualOverlay(ip))
                .Distinct()
                .Take(8)
                .ToList();
        }
        catch (Exception)
        {
            return []; // 枚举失败：无候选上报，打洞行为同现状
        }
    }

    /// <summary>100.64.0.0/10 CGNAT/本产品虚拟网段——候选对其直连无意义（两端均在虚拟网段时的
    /// 真实互通由映射端点打洞路径覆盖），剔除可省对端无谓探测。</summary>
    private static bool IsVirtualOverlay(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b.Length == 4 && b[0] == 100 && b[1] is >= 64 and <= 127;
    }

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

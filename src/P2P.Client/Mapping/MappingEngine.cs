// M1-27 MappingEngine（05 §2，FR-C-301/302/306、D15、02 §4.5）：
// - 每条启用映射一个 TcpListener.Bind(virtualIp:localPort)，accept → 分配 channelId →
//   OPEN{proto, targetAddr, targetPort} → OPEN_OK 后双向 splice；
// - 背压（M2-21，05 §2.3）：出站=每 channel 64KiB WINDOW 信用（TunnelSession.SendDataAsync 内挂起
//   =暂停读本地 socket；对端 SpliceIn 消费后回 WINDOW 恢复）；入站写队列 256KiB 有界兜底
//   （信用窗 64KiB < 队列容量，正常不触达满则断开路径）；
// - 状态机 disabled→punching→direct/relay/failed（M2-18：relay 态=打洞失败且回退开→中继承载；
//   invalid=授权失效（M2-15：0x75 到达 MarkInvalid）；
//   enable 前隧道复用检查：设备对隧道存活 → 直达 direct 不排队（02 §4.5 复用规则）；
// - 隧道断链 → 该设备对映射回 punching 重新排队（02 §4.5 重建=新 sessionId）；
// - M2-19 回切：直连会话挂入替换中继会话 → relay 态映射翻 direct（TunnelHost 排水窗内
//   旧会话在途帧照常送达，channel 随旧会话关闭收尾——先排水后切换，NET-75）；
// - 目标侧 self=127.0.0.1（D15）；非 self 走本机 lan_segments 白名单（SEC-52 双保险的执行点）；
// - UDP 映射（M2-20，05 §2.4/FR-C-303/TD-15）：UdpClient.Bind 监听；channel=本地应用端点——
//   端点首包建 channel+OPEN 后乐观直发 UDP_DGRAM（不等 OPEN_OK）；OPEN_FAIL→该端点丢包+映射 failed；
//   服务侧每 channel 一个随机源端口 UdpClient（Connect 目标）+L3 校验同 TCP；双向空闲 60s 回收
//   （CLOSE+双端释放）；每映射并发 channel ≤256 超限丢弃+计数；>1368B 数据报 FRAG 分片/重组在会话层。
// - listen_failed 自动重试（M2-24，FR-C-902 附）：监听绑定失败（AddressNotAvailable 等——
//   Wintun 进程切换窗口 IP 未生效的规律复现）入重试集，周期+网卡恢复沿重绑直至成功，无需手工 retry。
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using P2P.Client.Punch;
using P2P.Client.Tunnel;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using P2P.Core.Tunnel;

namespace P2P.Client.Mapping;

/// <summary>映射状态机（PRD 06 §5）：disabled → punching → direct / relay / failed；任意态 → invalid。
/// relay（M2）进入条件=打洞失败且设备级回退开启（D3/OQ-10）。</summary>
public enum MappingState
{
    Disabled,
    Punching,
    Direct,
    Relay,   // M2
    Failed,
    Invalid, // 授权失效（预留：服务端禁用/0x64 事件）
}

/// <summary>状态变迁事件（M1-29 WS 推送 / UI）。</summary>
public sealed record MappingStateEvent(Guid MappingId, MappingState State, string? Detail);

/// <summary>引擎侧映射配置（M1-28 同步层解析 TargetRemoteCode → PeerDeviceId 后下发）。</summary>
public sealed record MappingConfig(
    Guid MappingId,
    string Name,
    ushort LocalPort,
    string Proto,        // M1 仅 "tcp"
    string TargetAddr,   // "self"（M1）
    ushort TargetPort,
    Guid PeerDeviceId);

public sealed record MappingEngineOptions
{
    /// <summary>每 channel 出站暂存上限（满则暂停读本地 socket，05 §2.3）。</summary>
    public int BacklogBytes { get; init; } = 256 * 1024;
    /// <summary>目标侧连接本地服务超时。</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>UDP channel 双向空闲回收超时（05 §2.4 默认 60s 可配；测试注入缩短）。</summary>
    public TimeSpan UdpIdleTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>UDP channel 空闲扫描周期（默认 5s；测试注入缩短）。</summary>
    public TimeSpan UdpSweepInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>每 UDP 映射并发 channel 上限（05 §2.4 默认 256；防 DNS 风暴型放大，超限丢弃+计数）。</summary>
    public int UdpMaxChannels { get; init; } = 256;

    /// <summary>listen_failed 自动重试周期（M2-24，FR-C-902 附：Wintun 进程切换窗口 IP 未生效
    /// 的规律复现收口）：默认 5s；重试直至绑定成功（网卡就绪即收敛），测试注入缩短。</summary>
    public TimeSpan ListenRetryInterval { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// 映射引擎：监听/channel 管理/双向 splice/状态机。实现 <see cref="ITunnelChannelHandler"/>——
/// 同一客户端既可为访问方（本地 accept 发 OPEN）也可为目标方（收 OPEN 连本地服务）。
/// </summary>
public sealed class MappingEngine : ITunnelChannelHandler, IAsyncDisposable
{
    /// <summary>splice 分块：UDP 承载整帧 ≤1400B（02 §4.3）→ 明文上限 = 1400-16(头)-16(tag)。
    /// 与 UDP_DGRAM 单帧上限同源（M2-20 起常量归 PtpFrameCodec）。</summary>
    public static int ChunkSize => PtpFrameCodec.MaxUdpDgramPlain;

    private readonly TunnelHost _tunnels;
    private readonly PunchScheduler _scheduler;
    private IPAddress _virtualIp; // UpdateVirtualIp 可切换（向导注册完成时点）
    private readonly MappingEngineOptions _options;
    private readonly Func<IReadOnlyCollection<string>>? _enabledCidrs; // M2-11 白名单快照读（SEC-52 第二道）
    private readonly ConcurrentDictionary<Guid, Runtime> _mappings = new();
    private readonly ConcurrentDictionary<(Guid SessionId, uint ChannelId), ChannelEntry> _channels = new();
    private readonly ConcurrentDictionary<(Guid SessionId, uint ChannelId), UdpChannelEntry> _udpChannels = new();
    /// <summary>listen_failed 待重试映射集（M2-24）：键=映射 id；成功绑定/停用/失效即摘除。</summary>
    private readonly ConcurrentDictionary<Guid, byte> _listenRetry = new();
    /// <summary>重试轮串行闸（M2-24）：周期循环与网卡恢复沿可能并发触发；非阻塞——
    /// 已有轮次在跑则本次跳过（在飞轮次覆盖在册项，后续周期兜底新增）。</summary>
    private readonly SemaphoreSlim _listenRetryGate = new(1, 1);
    private readonly Task _udpSweepLoop;
    private readonly Task _listenRetryLoop;
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;

    /// <summary>映射状态变迁（M1-29 WS/UI；状态机全轨迹）。</summary>
    public event Action<MappingStateEvent>? StateChanged;

    /// <summary>诊断日志（宿主接 Serilog）。</summary>
    public event Action<string>? Log;

    /// <summary>诊断快照（06 §3 /api/mappings；M1-29 使用）。</summary>
    public sealed record MappingSnapshot(MappingConfig Config, MappingState State, string? Detail);

    public IReadOnlyCollection<MappingSnapshot> Snapshots
        => _mappings.Values.Select(r => new MappingSnapshot(r.Config, r.State, r.Detail)).ToList();

    /// <summary>流量快照（04 §2.8 mapping_stats 事件源；访问侧 channel 计数，目标侧归对端映射）。
    /// RelayBytes=双向经中继字节合计，含于 BytesUp+BytesDown 总量内（03 §2.6 relay_bytes 口径；
    /// channel 所属会话 ViaRelay 计入，M2-22 供 0x64）。</summary>
    public sealed record MappingTraffic(Guid MappingId, long BytesUp, long BytesDown, long RelayBytes);

    public IReadOnlyCollection<MappingTraffic> TrafficSnapshots()
        => _mappings.Values.Select(r => new MappingTraffic(r.Config.MappingId,
               Volatile.Read(ref r.BytesUp), Volatile.Read(ref r.BytesDown),
               Volatile.Read(ref r.BytesRelay))).ToList();

    public MappingEngine(TunnelHost tunnels, PunchScheduler scheduler, IPAddress virtualIp,
        MappingEngineOptions? options = null, Func<IReadOnlyCollection<string>>? enabledCidrsProvider = null)
    {
        _tunnels = tunnels;
        _scheduler = scheduler;
        _virtualIp = virtualIp;
        _options = options ?? new MappingEngineOptions();
        _enabledCidrs = enabledCidrsProvider; // null=空集：非 self 一律拒绝（fail closed，M1 行为保持）
        _scheduler.PunchCompleted += OnPunchCompleted;
        _tunnels.SessionDisconnected += OnTunnelDisconnected;
        _tunnels.SessionAttached += OnTunnelAttached;
        _udpSweepLoop = UdpSweepLoopAsync(_cts.Token);
        _listenRetryLoop = ListenRetryLoopAsync(_cts.Token);
    }

    /// <summary>更新监听绑定地址（01 §3.2 监听绑虚拟 IP）。向导路径冷启动时 VirtualIp 尚空、
    /// 构造初值是 Loopback 兜底——注册完成、虚拟 IP 落盘后由运行时切换至此。只影响后续新监听：
    /// 该时点客户端必然无已启用映射（未注册期间无映射可启），故无既有监听迁移需求。</summary>
    public void UpdateVirtualIp(IPAddress virtualIp) => _virtualIp = virtualIp;

    private sealed class Runtime
    {
        public required MappingConfig Config;
        public volatile MappingState State = MappingState.Disabled;
        public string? Detail;
        public TcpListener? Listener;          // tcp 映射监听
        public Task? AcceptLoop;
        public UdpClient? UdpListener;         // udp 映射监听（M2-20）
        public Task? UdpRecvLoop;
        /// <summary>访问侧端点→channel 表（05 §2.4：channel=本地应用端点）；lock (UdpByEndpoint) 保护。</summary>
        public readonly Dictionary<IPEndPoint, UdpChannelEntry> UdpByEndpoint = [];
        public long UdpDroppedDatagrams;      // 超限/无隧道丢弃计数（04 §3.2 计数告警源）
        public long UdpOverflowChannels;      // 超 256 上限被拒的建 channel 尝试计数
        public long BytesUp;     // 访问侧累计（mapping_stats 速率采样，04 §2.8）
        public long BytesDown;
        public long BytesRelay;  // 其中经中继承载的累计（0x64 relayBytes，M2-22）
    }

    // ── 映射生命周期（M1-28 同步层调用）──────────────────────────────

    /// <summary>启用映射：绑监听 → 隧道复用检查（存活直达 direct，02 §4.5）→ 否则 punching+排队打洞。</summary>
    public Task EnableAsync(MappingConfig config)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        ArgumentNullException.ThrowIfNull(config);

        if (config.Proto is not ("tcp" or "udp"))
            throw new ArgumentException($"proto 须为 tcp|udp（得 {config.Proto}）", nameof(config));

        if (_mappings.TryGetValue(config.MappingId, out var existing)
            && existing.Config.Equals(config) && (existing.Listener is not null || existing.UdpListener is not null))
            return Task.CompletedTask; // 幂等（同步层 diff 后才会重复到达）
        if (existing is not null)
            _ = DisableAsync(config.MappingId);

        var rt = new Runtime { Config = config };
        _mappings[config.MappingId] = rt;

        if (config.Proto == "udp")
        {
            // UDP 监听（M2-20，05 §2.4）：UdpClient.Bind(virtualIp:localPort)；channel 由端点首包按需建
            UdpClient listener;
            try
            {
                listener = new UdpClient(new IPEndPoint(_virtualIp, config.LocalPort));
            }
            catch (SocketException e)
            {
                SetState(rt, MappingState.Failed, $"listen_failed: {e.SocketErrorCode}");
                _listenRetry[config.MappingId] = 0; // 自动重试直至网卡就绪（M2-24）
                return Task.CompletedTask;
            }
            rt.UdpListener = listener;
            rt.UdpRecvLoop = UdpReceiveLoopAsync(rt, _cts.Token);
        }
        else
        {
            var listener = new TcpListener(_virtualIp, config.LocalPort);
            try
            {
                listener.Start(backlog: 16);
            }
            catch (SocketException e)
            {
                SetState(rt, MappingState.Failed, $"listen_failed: {e.SocketErrorCode}");
                _listenRetry[config.MappingId] = 0; // 自动重试直至网卡就绪（M2-24）
                return Task.CompletedTask;
            }
            rt.Listener = listener;
            rt.AcceptLoop = AcceptLoopAsync(rt, _cts.Token);
        }

        // 隧道复用检查（02 §4.5：设备对隧道存活 → 复用当前承载不排队打洞——M2-18：中继会话 → relay 态）
        if (_tunnels.Get(config.PeerDeviceId) is { } reused)
        {
            SetState(rt, reused.ViaRelay ? MappingState.Relay : MappingState.Direct, "tunnel_reused");
            return Task.CompletedTask;
        }
        SetState(rt, MappingState.Punching, null);
        _scheduler.Enqueue(config.PeerDeviceId, config.MappingId, config.Proto); // OQ-11 串行队列（同对合并；proto 随映射，M2-16）
        return Task.CompletedTask;
    }

    /// <summary>停用映射：关监听与全部 channel，disabled。</summary>
    public async Task DisableAsync(Guid mappingId)
    {
        if (_mappings.TryRemove(mappingId, out var rt))
            await TeardownAsync(rt, MappingState.Disabled, null);
    }

    /// <summary>失败手动重试（04 §2.5 /retry）：打洞失败态仅重排打洞（监听保持）；
    /// listen_failed 等无监听失败走全量重新启用（重新绑端口）。其余态幂等空操作。</summary>
    public Task RetryAsync(Guid mappingId)
    {
        if (!_mappings.TryGetValue(mappingId, out var rt))
            return Task.CompletedTask; // 未启用/不存在：幂等
        if (rt.Listener is not null || rt.UdpListener is not null)
        {
            if (rt.State == MappingState.Failed)
            {
                SetState(rt, MappingState.Punching, null);
                _scheduler.Enqueue(rt.Config.PeerDeviceId, mappingId, rt.Config.Proto); // OQ-11 串行队列
            }
            return Task.CompletedTask;
        }
        var config = rt.Config; // EnableAsync 内部会替换旧 Runtime（先拆后建）
        return EnableAsync(config);
    }

    /// <summary>授权失效（预留：服务端禁用/0x64 事件到达即标 invalid，任意态可入）。</summary>
    public async Task MarkInvalidAsync(Guid mappingId, string detail)
    {
        if (_mappings.TryGetValue(mappingId, out var rt))
            await TeardownAsync(rt, MappingState.Invalid, detail);
    }

    private async Task TeardownAsync(Runtime rt, MappingState final, string? detail)
    {
        _listenRetry.TryRemove(rt.Config.MappingId, out _); // 停用/失效/引擎释放：摘出重试集（M2-24）
        rt.Listener?.Stop(); // accept 循环随之退出
        if (rt.AcceptLoop is not null) { try { await rt.AcceptLoop; } catch { /* 监听关闭 */ } }
        rt.UdpListener?.Close(); // UDP 接收循环随之退出
        if (rt.UdpRecvLoop is not null) { try { await rt.UdpRecvLoop; } catch { /* 监听关闭 */ } }
        rt.UdpListener?.Dispose();
        foreach (var entry in _channels.Values.Where(e => e.MappingId == rt.Config.MappingId).ToList())
            await CloseEntryAsync(entry, notifyPeer: true);
        foreach (var entry in _udpChannels.Values.Where(e => e.MappingId == rt.Config.MappingId).ToList())
            await CloseUdpChannelAsync(entry, notifyPeer: true);
        SetState(rt, final, detail);
    }

    private async Task AcceptLoopAsync(Runtime rt, CancellationToken ct)
    {
        var listener = rt.Listener!;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var socket = await listener.AcceptSocketAsync(ct);
                _ = HandleAcceptAsync(rt, socket, ct);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
        { /* 监听停用/停机 */ }
    }

    // ── 访问侧：本地 accept → OPEN → OPEN_OK 后 splice ───────────────

    private async Task HandleAcceptAsync(Runtime rt, Socket socket, CancellationToken ct)
    {
        var session = _tunnels.Get(rt.Config.PeerDeviceId);
        if (session is null)
        {
            // punching/failed 期间到达的连接：拒绝（M1 简化；应用侧重连即得后续直连）
            Log?.Invoke($"映射 {rt.Config.Name} 无可用隧道，拒绝本地连接");
            socket.Dispose();
            return;
        }
        var channelId = session.AllocateChannelId();
        var entry = new ChannelEntry(rt.Config.MappingId, socket, session, channelId, _options.BacklogBytes)
        { Owner = rt };
        _channels[(session.SessionId, channelId)] = entry;
        try
        {
            await session.SendOpenAsync(channelId,
                new OpenPayload(rt.Config.Proto, rt.Config.TargetAddr, rt.Config.TargetPort), ct);
            // OPEN_OK/OPEN_FAIL 由 OnOpenResult 驱动（超时兜底=隧道断链清理）
        }
        catch (Exception e)
        {
            Log?.Invoke($"OPEN 发送失败：{e.Message}");
            await CloseEntryAsync(entry, notifyPeer: false);
        }
    }

    // ── ITunnelChannelHandler（访问侧+目标侧统一入口）────────────────

    /// <summary>目标侧：对端请求打开本地目标连接（tcp=连接目标服务；udp=建随机源端口 channel）。</summary>
    public void OnOpen(TunnelSession session, uint channelId, OpenPayload open)
    {
        if (open.TargetProto == "udp")
            _ = HandleTargetUdpOpenAsync(session, channelId, open);
        else
            _ = HandleTargetOpenAsync(session, channelId, open);
    }

    private async Task HandleTargetOpenAsync(TunnelSession session, uint channelId, OpenPayload open)
    {
        try
        {
            if (open.TargetProto != "tcp")
            {
                await session.SendOpenResultAsync(channelId,
                    new OpenResultPayload(false, $"proto_not_supported: {open.TargetProto}"));
                return;
            }
            // L3 白名单本地校验（M2-11，SEC-52 双保险第二道；05 §2.5）：self=127.0.0.1 恒放行；
            // 非 self 须被本机 enabled 段 CIDR 覆盖——服务端 0x60/0x70 双路径校验（第一道）后的
            // 最后一道防线（防服务端校验后段被移除/绕过）。无 provider/无段 = 不覆盖（fail closed）
            if (!TryResolveTarget(open.TargetAddr, out var connectAddr))
            {
                await session.SendOpenResultAsync(channelId,
                    new OpenResultPayload(false, $"l3_not_permitted: {open.TargetAddr}"));
                return;
            }

            // self → 127.0.0.1:targetPort（05 §2.5/D15）；段内 → 该地址
            using var connectCts = new CancellationTokenSource(_options.ConnectTimeout);
            var target = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await target.ConnectAsync(connectAddr, open.TargetPort, connectCts.Token);
            }
            catch (Exception e)
            {
                target.Dispose();
                await session.SendOpenResultAsync(channelId,
                    new OpenResultPayload(false, $"connect_failed: {e.Message}"));
                return;
            }

            var entry = new ChannelEntry(null, target, session, channelId, _options.BacklogBytes);
            _channels[(session.SessionId, channelId)] = entry;
            await session.SendOpenResultAsync(channelId, new OpenResultPayload(true, null));
            StartSplice(entry);
        }
        catch (Exception e)
        {
            Log?.Invoke($"目标侧 OPEN 处理失败：{e.Message}");
        }
    }

    /// <summary>L3 解析（TCP/UDP 共用，SEC-52）：self → 127.0.0.1 恒放行；非 self 须被本机
    /// enabled 段覆盖（无 provider/无段 = fail closed）。</summary>
    private bool TryResolveTarget(string targetAddr, out IPAddress address)
    {
        if (targetAddr == "self")
        {
            address = IPAddress.Loopback;
            return true;
        }
        if (IsCoveredBySegments(targetAddr))
        {
            address = IPAddress.Parse(targetAddr); // 覆盖判定已确保可解析
            return true;
        }
        address = IPAddress.None;
        return false;
    }

    /// <summary>targetAddr 是否被本机 enabled 段覆盖（M2-11，SEC-52）：IP 字面量逐段
    /// IPNetwork.Contains；非法地址/段（手改文件）fail closed。provider 每次现调取快照
    /// （读无锁、列表不可变——LanSegmentsStore 同步替换）。</summary>
    private bool IsCoveredBySegments(string targetAddr)
    {
        if (!System.Net.IPAddress.TryParse(targetAddr, out var addr))
            return false;
        var cidrs = _enabledCidrs?.Invoke() ?? [];
        foreach (var cidr in cidrs)
            if (System.Net.IPNetwork.TryParse(cidr, out var net) && net.Contains(addr))
                return true;
        return false;
    }

    /// <summary>访问侧：OPEN 结果——TCP OK 进 splice，FAIL 关本地连接（05 §2.2）；
    /// UDP FAIL → 该端点 OpenFailed 静默丢包 + 映射 failed（监听保持，05 §2.4）。</summary>
    public void OnOpenResult(TunnelSession session, uint channelId, OpenResultPayload result)
    {
        if (_channels.TryGetValue((session.SessionId, channelId), out var entry))
        {
            if (result.Ok) StartSplice(entry);
            else _ = CloseEntryAsync(entry, notifyPeer: false);
            return;
        }
        if (!result.Ok && _udpChannels.TryGetValue((session.SessionId, channelId), out var udpEntry)
            && udpEntry.Owner is { } rt)
        {
            Volatile.Write(ref udpEntry.OpenFailed, 1);
            if (rt.State != MappingState.Failed)
                SetState(rt, MappingState.Failed, $"open_failed: {result.FailReason}");
        }
    }

    /// <summary>隧道→本地：入站数据进有界写队列。</summary>
    public void OnData(TunnelSession session, uint channelId, ReadOnlyMemory<byte> data)
    {
        if (!_channels.TryGetValue((session.SessionId, channelId), out var entry)) return; // 迟到容忍
        if (!entry.Inbound.Writer.TryWrite(data.ToArray()))
        {
            // 本地应用不消费且队列已满（256KiB）：断开该 channel（M1 入站侧简化，05 §2.3）
            Log?.Invoke($"channel {channelId} 入站积压超限，断开");
            _ = CloseEntryAsync(entry, notifyPeer: true);
        }
    }

    /// <summary>对端关闭 channel（TCP splice / UDP channel 统一入口）。</summary>
    public void OnClose(TunnelSession session, uint channelId)
    {
        if (_channels.TryGetValue((session.SessionId, channelId), out var entry))
            _ = CloseEntryAsync(entry, notifyPeer: false);
        if (_udpChannels.TryGetValue((session.SessionId, channelId), out var udpEntry))
            _ = CloseUdpChannelAsync(udpEntry, notifyPeer: false);
    }

    // ── UDP 映射（M2-20，05 §2.4/FR-C-303/TD-15）────────────────────

    /// <summary>UDP channel：访问侧=本地应用端点上下文；服务侧=连目标的本地 socket。
    /// 双端同构存于 _udpChannels（服务侧 MappingId/Owner 为 null，流量归对端映射计数）。</summary>
    private sealed class UdpChannelEntry
    {
        public required Guid? MappingId;   // 访问侧归属映射 id；服务侧 null
        public required Runtime? Owner;    // 访问侧流量计数归属；服务侧 null
        public required TunnelSession Session;
        public required uint ChannelId;
        /// <summary>访问侧：本地应用端点（channel 身份即此端点，回发目标）。</summary>
        public required IPEndPoint? AppEndpoint;
        /// <summary>服务侧：Connect(target) 的本地 socket（随机源端口，仅收该目标回包）。</summary>
        public UdpClient? Local;
        public Task? LocalRecvLoop;        // 服务侧目标→隧道回发循环
        public long LastActive;            // Environment.TickCount64；双向收发均刷新（空闲回收依据）
        public int OpenFailed;             // 访问侧：OPEN_FAIL 置 1 → 该端点后续包静默丢弃（表项保留）
        public int Closed;                 // CloseUdpChannelAsync 幂等闸
    }

    /// <summary>访问侧监听循环：收本地应用数据报 → 端点表查/建 channel → OPEN{udp} 后乐观直发
    /// UDP_DGRAM（不等 OPEN_OK，05 §2.4）。单 runtime 单循环（UdpClient.ReceiveAsync 非并发安全）。</summary>
    private async Task UdpReceiveLoopAsync(Runtime rt, CancellationToken ct)
    {
        var listener = rt.UdpListener!;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try { result = await listener.ReceiveAsync(ct); }
                catch (SocketException) { continue; } // ICMP 端口不可达等：监听保持（UDP 无连接）

                var entry = LookupUdpChannel(rt, result.RemoteEndPoint);
                if (entry is not null && (Volatile.Read(ref entry.OpenFailed) == 1 || Volatile.Read(ref entry.Closed) == 1))
                {
                    Interlocked.Increment(ref rt.UdpDroppedDatagrams); // OPEN_FAIL/回收中端点：静默丢弃
                    continue;
                }
                if (entry is null)
                {
                    var session = _tunnels.Get(rt.Config.PeerDeviceId);
                    if (session is null || session.IsClosed)
                    {
                        Interlocked.Increment(ref rt.UdpDroppedDatagrams); // punching/failed 期间：丢（映射态不变）
                        continue;
                    }
                    entry = OpenUdpChannel(rt, session, result.RemoteEndPoint);
                    if (entry is null)
                    {
                        Interlocked.Increment(ref rt.UdpDroppedDatagrams); // 超 256 上限（建 channel 尝试另计入 Overflow）
                        continue;
                    }
                    try
                    {
                        await session.SendOpenAsync(entry.ChannelId,
                            new OpenPayload("udp", rt.Config.TargetAddr, rt.Config.TargetPort), ct);
                        // OPEN_OK/OPEN_FAIL 由 OnOpenResult 驱动；数据报乐观直发不等结果
                    }
                    catch (Exception e)
                    {
                        Log?.Invoke($"映射 {rt.Config.Name} UDP OPEN 发送失败：{e.Message}");
                        await CloseUdpChannelAsync(entry, notifyPeer: false);
                        Interlocked.Increment(ref rt.UdpDroppedDatagrams);
                        continue;
                    }
                }
                try
                {
                    await entry.Session.SendUdpDgramAsync(entry.ChannelId, result.Buffer, ct);
                    Volatile.Write(ref entry.LastActive, Environment.TickCount64);
                    Interlocked.Add(ref rt.BytesUp, result.Buffer.Length);
                    if (entry.Session.ViaRelay) Interlocked.Add(ref rt.BytesRelay, result.Buffer.Length);
                }
                catch
                {
                    Interlocked.Increment(ref rt.UdpDroppedDatagrams); // 会话异常：丢（断链事件统一清理）
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        { /* 监听关闭（停用/引擎释放） */ }
    }

    private static UdpChannelEntry? LookupUdpChannel(Runtime rt, IPEndPoint endpoint)
    {
        lock (rt.UdpByEndpoint)
            return rt.UdpByEndpoint.TryGetValue(endpoint, out var entry) ? entry : null;
    }

    /// <summary>端点首包建 channel：端点表+channel 表登记（上限检查与登记同锁，两表一致）。</summary>
    private UdpChannelEntry? OpenUdpChannel(Runtime rt, TunnelSession session, IPEndPoint appEndpoint)
    {
        lock (rt.UdpByEndpoint)
        {
            if (rt.UdpByEndpoint.Count >= _options.UdpMaxChannels)
            {
                Interlocked.Increment(ref rt.UdpOverflowChannels); // DNS 风暴型放大防护（05 §2.4）
                return null;
            }
            var entry = new UdpChannelEntry
            {
                MappingId = rt.Config.MappingId,
                Owner = rt,
                Session = session,
                ChannelId = session.AllocateChannelId(),
                AppEndpoint = appEndpoint,
                LastActive = Environment.TickCount64,
            };
            _udpChannels[(session.SessionId, entry.ChannelId)] = entry;
            rt.UdpByEndpoint[appEndpoint] = entry;
            return entry;
        }
    }

    /// <summary>服务侧 UDP OPEN（05 §2.4）：L3 校验同 TCP → 每 channel 一个随机源端口
    /// UdpClient.Connect(target)（仅收该目标回包）→ OPEN_OK → 本地接收循环回发。</summary>
    private async Task HandleTargetUdpOpenAsync(TunnelSession session, uint channelId, OpenPayload open)
    {
        try
        {
            if (!TryResolveTarget(open.TargetAddr, out var targetAddr))
            {
                await session.SendOpenResultAsync(channelId,
                    new OpenResultPayload(false, $"l3_not_permitted: {open.TargetAddr}"));
                return;
            }
            var local = new UdpClient();
            try { local.Connect(targetAddr, open.TargetPort); }
            catch (SocketException e)
            {
                local.Dispose();
                await session.SendOpenResultAsync(channelId,
                    new OpenResultPayload(false, $"connect_failed: {e.Message}"));
                return;
            }
            var entry = new UdpChannelEntry
            {
                MappingId = null,
                Owner = null,
                Session = session,
                ChannelId = channelId,
                AppEndpoint = null,
                Local = local,
                LastActive = Environment.TickCount64,
            };
            _udpChannels[(session.SessionId, channelId)] = entry;
            await session.SendOpenResultAsync(channelId, new OpenResultPayload(true, null));
            entry.LocalRecvLoop = TargetUdpRecvLoopAsync(entry, local, _cts.Token);
        }
        catch (Exception e)
        {
            Log?.Invoke($"目标侧 UDP OPEN 处理失败：{e.Message}");
        }
    }

    /// <summary>服务侧：目标回包 → UDP_DGRAM 回访问侧。</summary>
    private async Task TargetUdpRecvLoopAsync(UdpChannelEntry entry, UdpClient local, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await local.ReceiveAsync(ct);
                Volatile.Write(ref entry.LastActive, Environment.TickCount64);
                await entry.Session.SendUdpDgramAsync(entry.ChannelId, result.Buffer, ct);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException
            or SocketException)
        { /* channel 回收/引擎释放/ICMP 不可达：退出（残留表项由空闲扫描收敛） */ }
    }

    /// <summary>UDP_DGRAM 到达（会话层已重组完整数据报）：访问侧回发本地应用端点；服务侧转发目标。</summary>
    public void OnUdpDgram(TunnelSession session, uint channelId, ReadOnlyMemory<byte> datagram)
    {
        if (!_udpChannels.TryGetValue((session.SessionId, channelId), out var entry)
            || Volatile.Read(ref entry.Closed) == 1)
            return; // 迟到/已回收：丢（UDP 语义容忍丢包）
        Volatile.Write(ref entry.LastActive, Environment.TickCount64);
        if (entry.Owner is { } rt)
        {
            try
            {
                rt.UdpListener?.Send(datagram.Span, entry.AppEndpoint!);
                Interlocked.Add(ref rt.BytesDown, datagram.Length);
                if (entry.Session.ViaRelay) Interlocked.Add(ref rt.BytesRelay, datagram.Length);
            }
            catch (SocketException)
            {
                Interlocked.Increment(ref rt.UdpDroppedDatagrams);
            }
        }
        else if (entry.Local is { } local)
        {
            try { local.Send(datagram.Span); } // Connect 过：无目标参数
            catch (SocketException) { /* 目标不可达：丢（接收循环 ICMP 收敛） */ }
        }
    }

    /// <summary>关 UDP channel（幂等）：双端表项释放 + 通知对端 CLOSE + 服务侧本地 socket 关闭。</summary>
    private async Task CloseUdpChannelAsync(UdpChannelEntry entry, bool notifyPeer)
    {
        if (Interlocked.Exchange(ref entry.Closed, 1) == 1) return;
        _udpChannels.TryRemove((entry.Session.SessionId, entry.ChannelId), out _);
        if (entry.Owner is { } o && entry.AppEndpoint is { } ep)
            lock (o.UdpByEndpoint)
                o.UdpByEndpoint.Remove(ep); // 同端点再发包建新 channel（05 §2.4 回收语义）
        if (notifyPeer && !entry.Session.IsClosed)
        {
            try { await entry.Session.SendCloseAsync(entry.ChannelId); }
            catch { /* 会话已关 */ }
        }
        entry.Local?.Close(); // 服务侧目标接收循环随之退出
        if (entry.LocalRecvLoop is not null) { try { await entry.LocalRecvLoop; } catch { } }
        entry.Local?.Dispose();
    }

    /// <summary>空闲扫描：周期遍历 _udpChannels，双向无流量超 UdpIdleTimeout 即回收（CLOSE+双端释放）。</summary>
    private async Task UdpSweepLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.UdpSweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var now = Environment.TickCount64;
                foreach (var entry in _udpChannels.Values)
                    if (Volatile.Read(ref entry.Closed) == 0
                        && now - Volatile.Read(ref entry.LastActive) >= _options.UdpIdleTimeout.TotalMilliseconds)
                        await CloseUdpChannelAsync(entry, notifyPeer: true);
            }
        }
        catch (OperationCanceledException) { /* 引擎释放 */ }
    }

    // ── listen_failed 自动重试（M2-24，FR-C-902 附）──────────────────

    /// <summary>重试周期循环：对重试集内映射执行一轮重绑定。另有 NicHealthMonitor.Restored
    /// 恢复沿触发的外加一轮（立即响应网卡重建，不必等下个周期）。</summary>
    private async Task ListenRetryLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.ListenRetryInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                await RetryListenFailedAsync(ct);
        }
        catch (OperationCanceledException) { /* 引擎释放 */ }
    }

    /// <summary>listen_failed 映射重试一轮（周期循环与网卡恢复沿共用入口）：Failed 且无监听 →
    /// 全量重新启用（重新绑端口，即手工 RetryAsync 同路径——不再需要手工 retry）。
    /// 先摘后启：成功即出列，失败由 EnableAsync 重新登记（重试集不叠加）。</summary>
    public async Task RetryListenFailedAsync(CancellationToken ct = default)
    {
        if (!await _listenRetryGate.WaitAsync(0, ct).ConfigureAwait(false))
            return; // 已有轮次在飞：跳过本次触发（在飞轮次覆盖在册项，后续周期兜底）
        try
        {
            foreach (var mappingId in _listenRetry.Keys.ToList())
            {
                if (ct.IsCancellationRequested) return;
                if (!_mappings.TryGetValue(mappingId, out var rt))
                {
                    _listenRetry.TryRemove(mappingId, out _); // 已不在（外部停用竞态）：收敛出列
                    continue;
                }
                if (rt.Listener is not null || rt.UdpListener is not null)
                {
                    _listenRetry.TryRemove(mappingId, out _); // 已恢复（手工 retry 竞态）：出列
                    continue;
                }
                if (rt.State != MappingState.Failed) continue; // 其他无监听态（invalid 等）不重试
                _listenRetry.TryRemove(mappingId, out _);
                Log?.Invoke($"映射 {rt.Config.Name} 监听自动重试（listen_failed 自愈，M2-24）");
                await EnableAsync(rt.Config);
            }
        }
        finally
        {
            _listenRetryGate.Release();
        }
    }

    // ── 状态机驱动 ────────────────────────────────────────────────────

    private void OnPunchCompleted(PunchOutcome outcome)
    {
        var targets = _mappings.Values.Where(r => r.Config.PeerDeviceId == outcome.PeerDeviceId).ToList();
        if (outcome.Ok && outcome.Session is not null && !outcome.Session.IsClosed)
        {
            _tunnels.Attach(outcome.Session);
            // 承载绑定（02 §4.5）：中继承载（M2-18 回退成功）→ relay 态；直连 → direct
            var state = outcome.Session.ViaRelay ? MappingState.Relay : MappingState.Direct;
            var detail = outcome.Session.ViaRelay
                ? $"relay={outcome.PeerEndpoint}"
                : $"local={outcome.LocalEndpoint} peer={outcome.PeerEndpoint}";
            foreach (var rt in targets)
                if (rt.State is MappingState.Punching or MappingState.Failed)
                    SetState(rt, state, detail);
        }
        else
        {
            // 打洞失败（含回退关/回退建立失败）：failed——relay 进入条件在 Puncher 侧合成（M2-23/M2-18）
            foreach (var rt in targets)
                if (rt.State == MappingState.Punching)
                    SetState(rt, MappingState.Failed, outcome.FailReason);
        }
    }

    /// <summary>会话挂入（M2-19）：本端无打洞结果的 Attach 路径（被邀请方应答/中继回退被动侧）——
    /// 直连会话替换中继会话即回切完成，relay 态映射翻 direct（访问方重打路径经 OnPunchCompleted
    /// 携带明细；此处统一兜底两侧，Punching/Failed 的常规翻牌仍由打洞结果驱动）。</summary>
    private void OnTunnelAttached(TunnelSession session)
    {
        if (session.ViaRelay) return; // 中继会话挂入：relay 进入条件由打洞结果合成（M2-23/M2-18）
        foreach (var rt in _mappings.Values.Where(r => r.Config.PeerDeviceId == session.PeerDeviceId))
            if (rt.State == MappingState.Relay)
                SetState(rt, MappingState.Direct, "relay_to_direct"); // 旧中继会话排水窗内收尾（NET-75）
    }

    private void OnTunnelDisconnected(TunnelSession session, string reason)
    {
        // 排水期新旧会话并存（M2-19）：只清理本会话的 channel（新会话的 channel 不受旧会话关闭影响）
        foreach (var entry in _channels.Values.Where(e => e.Session == session).ToList())
            _ = CloseEntryAsync(entry, notifyPeer: false); // 会话已亡：只清本地
        foreach (var udpEntry in _udpChannels.Values.Where(e => e.Session == session).ToList())
            _ = CloseUdpChannelAsync(udpEntry, notifyPeer: false); // 端点表同步摘除→新隧道重开 channel

        if (_tunnels.Get(session.PeerDeviceId) is not null) return; // 已有新隧道（替换场景）：不回打

        foreach (var rt in _mappings.Values.Where(r => r.Config.PeerDeviceId == session.PeerDeviceId))
        {
            // direct/relay 态承载断链均回 punching 重新竞争（02 §4.5 重建=新 sessionId）
            if (rt.State is not (MappingState.Direct or MappingState.Relay)) continue;
            SetState(rt, MappingState.Punching, $"reconnect: {reason}");
            _scheduler.Enqueue(session.PeerDeviceId, rt.Config.MappingId, rt.Config.Proto);
        }
    }

    private void SetState(Runtime rt, MappingState state, string? detail)
    {
        rt.State = state;
        rt.Detail = detail;
        var evt = new MappingStateEvent(rt.Config.MappingId, state, detail);
        try { StateChanged?.Invoke(evt); }
        catch (Exception e) { Log?.Invoke($"StateChanged 订阅方异常：{e.Message}"); }
    }

    // ── splice 与 channel 生命周期 ───────────────────────────────────

    private sealed class ChannelEntry(Guid? mappingId, Socket socket, TunnelSession session,
        uint channelId, int backlog)
    {
        /// <summary>本地映射 id（访问侧）；目标侧 channel 无本地映射 → null。</summary>
        public Guid? MappingId { get; } = mappingId;
        /// <summary>访问侧归属 Runtime（流量计数；目标侧 null）。</summary>
        public Runtime? Owner { get; set; }
        /// <summary>本地应用侧 socket（访问侧=accepted；目标侧=连到本地服务）。</summary>
        public Socket Socket { get; } = socket;
        public TunnelSession Session { get; } = session;
        public uint ChannelId { get; } = channelId;
        /// <summary>隧道→本地 有界写队列。容量按字节上限折算为条数（单条 ≤ChunkSize，
        /// backlog=256KiB → 191 条 ≈ 256KiB；M2-21 起对端发送受 64KiB 信用约束，
        /// 队列为本地应用慢消费的兜底（正常不触达满则断开路径），05 §2.3）。</summary>
        public Channel<byte[]> Inbound { get; } = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(Math.Max(1, backlog / ChunkSize)) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        public CancellationTokenSource Cts { get; } = new();
        public Task? OutLoop { get; set; }
        public Task? InLoop { get; set; }
        public int Closed;
    }

    private void StartSplice(ChannelEntry entry)
    {
        entry.OutLoop = SpliceOutAsync(entry, entry.Cts.Token);
        entry.InLoop = SpliceInAsync(entry, entry.Cts.Token);
    }

    /// <summary>本地→隧道：读本地 socket（≤ChunkSize）→ DATA。信用耗尽时 SendDataAsync 内部挂起
    /// = 暂停读本地 socket（M2-21 WINDOW 背压，05 §2.3；对端消费回报到达恢复）。</summary>
    private async Task SpliceOutAsync(ChannelEntry entry, CancellationToken ct)
    {
        var buf = new byte[ChunkSize];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var n = await entry.Socket.ReceiveAsync(buf.AsMemory(), ct);
                if (n == 0) break; // 本地半关闭
                var chunk = new byte[n];
                buf.AsSpan(0, n).CopyTo(chunk);
                await entry.Session.SendDataAsync(entry.ChannelId, chunk, ct);
                if (entry.Owner is { } o)
                {
                    Interlocked.Add(ref o.BytesUp, n); // 流量计数（成功发出后）
                    if (entry.Session.ViaRelay) Interlocked.Add(ref o.BytesRelay, n);
                }
            }
            await CloseEntryAsync(entry, notifyPeer: true); // EOF → CLOSE
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException
            or IOException or ObjectDisposedException)
        {
            await CloseEntryAsync(entry, notifyPeer: !entry.Session.IsClosed);
        }
    }

    /// <summary>隧道→本地：消费有界写队列写本地 socket。</summary>
    private async Task SpliceInAsync(ChannelEntry entry, CancellationToken ct)
    {
        try
        {
            await foreach (var data in entry.Inbound.Reader.ReadAllAsync(ct))
            {
                await entry.Socket.SendAsync(data, ct);
                // 消费回报（M2-21，05 §2.3）：本地应用已吸收 → 恢复对端发送信用；失败静默（会话将断链）
                try { await entry.Session.SendWindowCreditAsync(entry.ChannelId, data.Length, ct); }
                catch { /* 会话关闭中 */ }
                if (entry.Owner is { } o)
                {
                    Interlocked.Add(ref o.BytesDown, data.Length);
                    if (entry.Session.ViaRelay) Interlocked.Add(ref o.BytesRelay, data.Length);
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException
            or IOException or ObjectDisposedException)
        { /* 对端关闭/取消：由 CloseEntry 统一清理 */ }
    }

    private async Task CloseEntryAsync(ChannelEntry entry, bool notifyPeer)
    {
        if (Interlocked.Exchange(ref entry.Closed, 1) == 1) return;
        _channels.TryRemove((entry.Session.SessionId, entry.ChannelId), out _);
        entry.Session.RemoveChannelCredit(entry.ChannelId); // 摘信用账本（幂等；对端 CLOSE 路径双侧各摘一次）
        if (notifyPeer && !entry.Session.IsClosed)
        {
            try { await entry.Session.SendCloseAsync(entry.ChannelId); }
            catch { /* 会话已关 */ }
        }
        entry.Inbound.Writer.TryComplete();
        entry.Cts.Cancel();
        try { entry.Socket.Shutdown(SocketShutdown.Both); } catch { /* 未连接/已关 */ }
        entry.Socket.Dispose();
        _ = FinalizeEntryAsync(entry);
    }

    /// <summary>等两条 splice 循环退出后释放 entry 资源（CloseEntry 可能由循环自身调用，不可内联 await）。</summary>
    private static async Task FinalizeEntryAsync(ChannelEntry entry)
    {
        try { if (entry.OutLoop is not null) await entry.OutLoop; } catch { }
        try { if (entry.InLoop is not null) await entry.InLoop; } catch { }
        entry.Cts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _scheduler.PunchCompleted -= OnPunchCompleted;
        _tunnels.SessionDisconnected -= OnTunnelDisconnected;
        _tunnels.SessionAttached -= OnTunnelAttached;
        foreach (var rt in _mappings.Values.ToList())
            await TeardownAsync(rt, MappingState.Disabled, "engine_disposed");
        _mappings.Clear();
        _listenRetry.Clear();
        _cts.Cancel();
        try { await _udpSweepLoop; } catch { /* 取消即退出 */ }
        try { await _listenRetryLoop; } catch { /* 取消即退出 */ }
        _cts.Dispose();
        _listenRetryGate.Dispose();
    }
}

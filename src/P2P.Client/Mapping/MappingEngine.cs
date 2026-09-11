// M1-27 MappingEngine（05 §2，FR-C-301/302/306、D15、02 §4.5）：
// - 每条启用映射一个 TcpListener.Bind(virtualIp:localPort)，accept → 分配 channelId →
//   OPEN{proto, targetAddr, targetPort} → OPEN_OK 后双向 splice；
// - 背压（05 §2.3 M1 简化）：每 channel 出站在飞上限 256KiB（按 ChunkSize 分槽），满则暂停读本地 socket
//   （TCP 窗口反压应用）；
//   入站写队列 256KiB 有界，满则断开该 channel（本地应用不消费）；
// - 状态机 disabled→punching→direct/failed（relay 态 M2；invalid=授权失效预留）；
//   enable 前隧道复用检查：设备对隧道存活 → 直达 direct 不排队（02 §4.5 复用规则）；
// - 隧道断链 → 该设备对映射回 punching 重新排队（02 §4.5 重建=新 sessionId）；
// - 目标侧 self=127.0.0.1（D15）；非 self 属 M2 白名单（SEC-52 双保险的执行点）；
// - UDP 映射（05 §2.4/FR-C-303/TD-15）→ M2。
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
}

/// <summary>
/// 映射引擎：监听/channel 管理/双向 splice/状态机。实现 <see cref="ITunnelChannelHandler"/>——
/// 同一客户端既可为访问方（本地 accept 发 OPEN）也可为目标方（收 OPEN 连本地服务）。
/// </summary>
public sealed class MappingEngine : ITunnelChannelHandler, IAsyncDisposable
{
    /// <summary>splice 分块：UDP 承载整帧 ≤1400B（02 §4.3）→ 明文上限 = 1400-16(头)-16(tag)。</summary>
    public static int ChunkSize => UdpPunchTransport.MaxUdpFrame - PtpHeader.WireLen - Aead.TagLen;

    private readonly TunnelHost _tunnels;
    private readonly PunchScheduler _scheduler;
    private readonly IPAddress _virtualIp;
    private readonly MappingEngineOptions _options;
    private readonly ConcurrentDictionary<Guid, Runtime> _mappings = new();
    private readonly ConcurrentDictionary<(Guid SessionId, uint ChannelId), ChannelEntry> _channels = new();
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

    /// <summary>流量快照（04 §2.8 mapping_stats 事件源；访问侧 channel 计数，目标侧归对端映射）。</summary>
    public sealed record MappingTraffic(Guid MappingId, long BytesUp, long BytesDown);

    public IReadOnlyCollection<MappingTraffic> TrafficSnapshots()
        => _mappings.Values.Select(r => new MappingTraffic(r.Config.MappingId,
               Volatile.Read(ref r.BytesUp), Volatile.Read(ref r.BytesDown))).ToList();

    public MappingEngine(TunnelHost tunnels, PunchScheduler scheduler, IPAddress virtualIp,
        MappingEngineOptions? options = null)
    {
        _tunnels = tunnels;
        _scheduler = scheduler;
        _virtualIp = virtualIp;
        _options = options ?? new MappingEngineOptions();
        _scheduler.PunchCompleted += OnPunchCompleted;
        _tunnels.SessionDisconnected += OnTunnelDisconnected;
    }

    private sealed class Runtime
    {
        public required MappingConfig Config;
        public volatile MappingState State = MappingState.Disabled;
        public string? Detail;
        public TcpListener? Listener;
        public Task? AcceptLoop;
        public long BytesUp;     // 访问侧累计（mapping_stats 速率采样，04 §2.8）
        public long BytesDown;
    }

    // ── 映射生命周期（M1-28 同步层调用）──────────────────────────────

    /// <summary>启用映射：绑监听 → 隧道复用检查（存活直达 direct，02 §4.5）→ 否则 punching+排队打洞。</summary>
    public Task EnableAsync(MappingConfig config)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        ArgumentNullException.ThrowIfNull(config);

        if (config.Proto != "tcp")
        {
            // UDP 映射属 05 §2.4/FR-C-303/TD-15 → M2
            var rtUdp = new Runtime { Config = config };
            _mappings[config.MappingId] = rtUdp;
            SetState(rtUdp, MappingState.Failed, "udp_mapping_not_in_m1");
            return Task.CompletedTask;
        }

        if (_mappings.TryGetValue(config.MappingId, out var existing)
            && existing.Config.Equals(config) && existing.Listener is not null)
            return Task.CompletedTask; // 幂等（同步层 diff 后才会重复到达）
        if (existing is not null)
            _ = DisableAsync(config.MappingId);

        var rt = new Runtime { Config = config };
        _mappings[config.MappingId] = rt;

        var listener = new TcpListener(_virtualIp, config.LocalPort);
        try
        {
            listener.Start(backlog: 16);
        }
        catch (SocketException e)
        {
            SetState(rt, MappingState.Failed, $"listen_failed: {e.SocketErrorCode}");
            return Task.CompletedTask;
        }
        rt.Listener = listener;
        rt.AcceptLoop = AcceptLoopAsync(rt, _cts.Token);

        // 隧道复用检查（02 §4.5：设备对隧道存活 → 直接 direct，不排队打洞）
        if (_tunnels.Get(config.PeerDeviceId) is not null)
        {
            SetState(rt, MappingState.Direct, "tunnel_reused");
            return Task.CompletedTask;
        }
        SetState(rt, MappingState.Punching, null);
        _scheduler.Enqueue(config.PeerDeviceId, config.MappingId); // OQ-11 串行队列（同对合并）
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
        if (rt.Listener is not null)
        {
            if (rt.State == MappingState.Failed)
            {
                SetState(rt, MappingState.Punching, null);
                _scheduler.Enqueue(rt.Config.PeerDeviceId, mappingId); // OQ-11 串行队列
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
        rt.Listener?.Stop(); // accept 循环随之退出
        if (rt.AcceptLoop is not null) { try { await rt.AcceptLoop; } catch { /* 监听关闭 */ } }
        foreach (var entry in _channels.Values.Where(e => e.MappingId == rt.Config.MappingId).ToList())
            await CloseEntryAsync(entry, notifyPeer: true);
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

    /// <summary>目标侧：对端请求打开本地目标连接（self=127.0.0.1，D15；非 self → M2 白名单）。</summary>
    public void OnOpen(TunnelSession session, uint channelId, OpenPayload open)
        => _ = HandleTargetOpenAsync(session, channelId, open);

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
            if (open.TargetAddr != "self")
            {
                // L3 白名单校验属 M2（SEC-52 执行点）；M1 仅 self（任务清单 M1-27）
                await session.SendOpenResultAsync(channelId,
                    new OpenResultPayload(false, "addr_requires_m2_whitelist"));
                return;
            }

            // self → 127.0.0.1:targetPort（05 §2.5/D15）
            using var connectCts = new CancellationTokenSource(_options.ConnectTimeout);
            var target = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await target.ConnectAsync(IPAddress.Loopback, open.TargetPort, connectCts.Token);
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

    /// <summary>访问侧：OPEN 结果——OK 进 splice，FAIL 关闭本地连接（05 §2.2）。</summary>
    public void OnOpenResult(TunnelSession session, uint channelId, OpenResultPayload result)
    {
        if (!_channels.TryGetValue((session.SessionId, channelId), out var entry)) return; // 迟到：已关
        if (result.Ok) StartSplice(entry);
        else _ = CloseEntryAsync(entry, notifyPeer: false);
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

    /// <summary>对端关闭 channel。</summary>
    public void OnClose(TunnelSession session, uint channelId)
    {
        if (_channels.TryGetValue((session.SessionId, channelId), out var entry))
            _ = CloseEntryAsync(entry, notifyPeer: false);
    }

    // ── 状态机驱动 ────────────────────────────────────────────────────

    private void OnPunchCompleted(PunchOutcome outcome)
    {
        var targets = _mappings.Values.Where(r => r.Config.PeerDeviceId == outcome.PeerDeviceId).ToList();
        if (outcome.Ok && outcome.Session is not null && !outcome.Session.IsClosed)
        {
            _tunnels.Attach(outcome.Session);
            foreach (var rt in targets)
                if (rt.State is MappingState.Punching or MappingState.Failed)
                    SetState(rt, MappingState.Direct,
                        $"local={outcome.LocalEndpoint} peer={outcome.PeerEndpoint}");
        }
        else
        {
            // relay（M2）：打洞失败且设备级回退开启才进入；M1 relayAllowed 恒 false → failed
            foreach (var rt in targets)
                if (rt.State == MappingState.Punching)
                    SetState(rt, MappingState.Failed, outcome.FailReason);
        }
    }

    private void OnTunnelDisconnected(Guid peerDeviceId, string reason)
    {
        foreach (var entry in _channels.Values.Where(e => e.Session.PeerDeviceId == peerDeviceId).ToList())
            _ = CloseEntryAsync(entry, notifyPeer: false); // 会话已亡：只清本地

        if (_tunnels.Get(peerDeviceId) is not null) return; // 已有新隧道（替换场景）：不回打

        foreach (var rt in _mappings.Values.Where(r => r.Config.PeerDeviceId == peerDeviceId))
        {
            if (rt.State != MappingState.Direct) continue;
            SetState(rt, MappingState.Punching, $"reconnect: {reason}"); // 02 §4.5 重建
            _scheduler.Enqueue(peerDeviceId, rt.Config.MappingId);
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

    /// <summary>出站槽位数（在飞字节上限 backlog ÷ 每块 ChunkSize，至少 1）。</summary>
    private static int OutboundSlots(int backlog) => Math.Max(1, backlog / ChunkSize);

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
        /// <summary>出站槽位配额：backlog/ChunkSize 个槽（每槽一个 ≤ChunkSize 分块，在飞字节 ≤backlog）。
        /// 满则暂停读本地 socket（05 §2.3）。注：SemaphoreSlim 无"一次申请 n 许可"重载
        /// （WaitAsync(int,ct) 是超时语义），故按分块槽位计而非字节计。</summary>
        public SemaphoreSlim OutQuota { get; } = new(OutboundSlots(backlog), OutboundSlots(backlog));
        /// <summary>隧道→本地 有界写队列。容量按字节上限折算为条数（单条 ≤ChunkSize，
        /// backlog=256KiB → 191 条 ≈ 256KiB；满则 OnData 断开该 channel，05 §2.3）。</summary>
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

    /// <summary>本地→隧道：读本地 socket（≤ChunkSize）→ 配额 → DATA。配额满=暂停读（05 §2.3）。</summary>
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
                await entry.OutQuota.WaitAsync(ct); // 背压：在飞槽位满则暂停读本地 socket
                try { await entry.Session.SendDataAsync(entry.ChannelId, chunk, ct); }
                finally { entry.OutQuota.Release(); }
                if (entry.Owner is { } o) Interlocked.Add(ref o.BytesUp, n); // 流量计数（成功发出后）
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
                if (entry.Owner is { } o) Interlocked.Add(ref o.BytesDown, data.Length);
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
        entry.OutQuota.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _scheduler.PunchCompleted -= OnPunchCompleted;
        _tunnels.SessionDisconnected -= OnTunnelDisconnected;
        foreach (var rt in _mappings.Values.ToList())
            await TeardownAsync(rt, MappingState.Disabled, "engine_disposed");
        _mappings.Clear();
        _cts.Cancel();
        _cts.Dispose();
    }
}

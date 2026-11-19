// M1-26 PunchScheduler（05 §3.1，OQ-11）：客户端全局单例 FIFO 串行打洞队列。
// 串行化动机：多目标并发打洞会交叉消耗本端 NAT 的顺序端口分配、污染 +(N−1) 预测基准
// （TCP 打洞 M2 亦受益）；上一会话 完成/超时(10s) 后才处理下一个。
// 同设备对合并：已在队列/正在打 → 丢弃重复入队。被邀请方路径不进本队列（02 §5.1③）。
// 队列深度与当前会话进度暴露给本地 Web 诊断页（06 §3 /api/diagnostics）。
// M7-02 设备级协议决策（设计 12 §4/§5.1）：构造可选参 planner——装配后出队时点解析协议：
// 普通 Enqueue=Begin（设备级接管，覆盖入队 proto）；EnqueueRetry=NextRetry（交替序列）。
// 计数消耗在出队时点（入队被同对合并丢弃不消耗，防序列跳号，设计 12 §4.2）。
// M7-04 打洞日志协议化（设计 12 §6.2）：四型结果日志统一携带 proto=（取 outcome.Proto，
// 出队上下文同值兜底）。
using System.Threading.Channels;
using P2P.Core.Protocol;

namespace P2P.Client.Punch;

/// <summary>串行打洞调度器（访问方路径）。</summary>
public sealed class PunchScheduler : IAsyncDisposable
{
    private readonly IPuncher _puncher;
    private readonly PunchModePlanner? _planner; // M7-02：null=旧行为（proto 用入队值——测试兼容缝）
    private readonly Channel<(Guid Target, Guid? Trigger, string Proto, bool Retry)> _queue =
        Channel.CreateUnbounded<(Guid, Guid?, string, bool)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly HashSet<Guid> _queuedPeers = [];
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private Guid? _currentPeer;
    private int _disposed;

    /// <summary>打洞完成（成功/失败均发；驱动映射状态机。0x72 上报属 FR-C-404 → M2）。</summary>
    public event Action<PunchOutcome>? PunchCompleted;

    /// <summary>诊断日志（宿主接 Serilog）。</summary>
    public event Action<string>? Log;

    /// <summary>待打洞目标数（诊断端点，06 §3）。</summary>
    public int QueueDepth
    {
        get { lock (_gate) return _queuedPeers.Count; }
    }

    /// <summary>当前正在打洞的设备对（诊断端点，06 §3）。</summary>
    public Guid? CurrentPeer
    {
        get { lock (_gate) return _currentPeer; }
    }

    public PunchScheduler(IPuncher puncher, PunchModePlanner? planner = null)
    {
        ArgumentNullException.ThrowIfNull(puncher);
        _puncher = puncher;
        _planner = planner;
        _loop = LoopAsync(_cts.Token);
    }

    /// <summary>入队打洞需求（映射 enable/隧道重建/手动重试——新序列触发）。同对已在队列或
    /// 进行中 → 合并（false）；planner 装配后出队 proto 由设备级 punchMode 决定（Begin，
    /// 设计 12 决策①——入队 proto 仅 planner 未装配时生效）；未装配时 proto 取触发映射的协议
    /// （02 §4.5：隧道为设备对级资源，首条触发映射的 proto 决定打洞方式，缺省 udp）。</summary>
    public bool Enqueue(Guid targetDeviceId, Guid? triggerMappingId = null, string proto = "udp")
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        lock (_gate)
        {
            if (_queuedPeers.Contains(targetDeviceId)) return false; // OQ-11 同对合并
            if (_currentPeer == targetDeviceId) return false;
            _queuedPeers.Add(targetDeviceId);
        }
        _queue.Writer.TryWrite((targetDeviceId, triggerMappingId, proto, false));
        return true;
    }

    /// <summary>周期重试入队（M7-02，设计 12 §5.1：中继回切/Failed 周期重试）——出队时按
    /// planner 交替序列解析协议（NextRetry，以设备级优先模式为起点 TCP/KCP 交替）。
    /// planner 未装配（测试形态）时防御性使用入队占位 proto（"udp"=旧缺省口径；生产装配即成对，
    /// 不发生）。同对合并与普通 Enqueue 一视同仁（防周期重试与手动触发双打，OQ-11）。</summary>
    public bool EnqueueRetry(Guid targetDeviceId, Guid? triggerMappingId = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        lock (_gate)
        {
            if (_queuedPeers.Contains(targetDeviceId)) return false; // OQ-11 同对合并
            if (_currentPeer == targetDeviceId) return false;
            _queuedPeers.Add(targetDeviceId);
        }
        _queue.Writer.TryWrite((targetDeviceId, triggerMappingId, "udp", true));
        return true;
    }

    /// <summary>被邀请方路径（0x71 PunchInvite）：不进本地队列，直接执行（02 §5.1③/05 §3.1）。</summary>
    public Task<PunchOutcome> RespondAsync(PunchInvite invite, CancellationToken ct = default)
        => _puncher.RespondAsync(invite, ct);

    /// <summary>被邀请方路径（0x78 PunchStartGo，02 §5.2⑥）：同 0x71——不进本地队列直接执行。</summary>
    public Task<PunchOutcome> RespondGoAsync(PunchStartGo go, CancellationToken ct = default)
        => _puncher.RespondGoAsync(go, ct);

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var (target, trigger, queuedProto, retry) in _queue.Reader.ReadAllAsync(ct))
            {
                lock (_gate)
                {
                    _queuedPeers.Remove(target);
                    _currentPeer = target;
                }
                // 出队时点协议决策（M7-02，设计 12 §4.2/§5.1）：planner 未装配=旧行为（入队 proto 直用）；
                // 普通 Enqueue=Begin（设备级接管，覆盖入队 proto）；Retry=NextRetry（交替）。
                // 出队时点消耗计数保证：入队被同对合并丢弃的会话不消耗序列号（防跳号）。
                var proto = _planner is null ? queuedProto
                    : retry ? _planner.NextRetry(target)
                    : _planner.Begin(target);
                PunchOutcome outcome;
                try
                {
                    outcome = await _puncher.InitiateAsync(target, trigger, proto, ct);
                }
                catch (Exception e)
                {
                    outcome = PunchOutcome.Failure(target, $"punch_error: {e.Message}", proto);
                }
                lock (_gate) _currentPeer = null;
                // 四型结果日志（M7-04，设计 12 §6.2）：统一携带 proto=（outcome.Proto，出队上下文同值兜底）
                var protoText = $"proto={outcome.Proto ?? proto}";
                Log?.Invoke(outcome.Ok
                    ? outcome.Session is { ViaRelay: true }
                        ? outcome.PeerEndpoint is { } relayEp
                            ? $"中继承载建立 peer={outcome.PeerDeviceId} {protoText} relay={relayEp}（打洞回退，02 §4.5）"
                            : $"中继承载复用 peer={outcome.PeerDeviceId} {protoText}（回切重打失败，保持既有中继，M2-37）"
                        : $"打洞成功 peer={outcome.PeerDeviceId} {protoText} local={outcome.LocalEndpoint} peer={outcome.PeerEndpoint}"
                    : $"打洞失败 peer={target} {protoText}：{outcome.FailReason}");
                try { PunchCompleted?.Invoke(outcome); }
                catch (Exception e) { Log?.Invoke($"PunchCompleted 订阅方异常：{e.Message}"); }
            }
        }
        catch (OperationCanceledException) { /* 停机 */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _queue.Writer.TryComplete();
        await _cts.CancelAsync();
        try { await _loop; } catch { /* 取消路径 */ }
        _cts.Dispose();
    }
}

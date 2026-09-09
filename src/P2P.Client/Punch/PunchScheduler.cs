// M1-26 PunchScheduler（05 §3.1，OQ-11）：客户端全局单例 FIFO 串行打洞队列。
// 串行化动机：多目标并发打洞会交叉消耗本端 NAT 的顺序端口分配、污染 +(N−1) 预测基准
// （TCP 打洞 M2 亦受益）；上一会话 完成/超时(10s) 后才处理下一个。
// 同设备对合并：已在队列/正在打 → 丢弃重复入队。被邀请方路径不进本队列（02 §5.1③）。
// 队列深度与当前会话进度暴露给本地 Web 诊断页（06 §3 /api/diagnostics）。
using System.Threading.Channels;
using P2P.Core.Protocol;

namespace P2P.Client.Punch;

/// <summary>串行打洞调度器（访问方路径）。</summary>
public sealed class PunchScheduler : IAsyncDisposable
{
    private readonly IPuncher _puncher;
    private readonly Channel<(Guid Target, Guid? Trigger)> _queue =
        Channel.CreateUnbounded<(Guid, Guid?)>(new UnboundedChannelOptions { SingleReader = true });
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

    /// <summary>当前正在打洞的设备对（诊断端点）。</summary>
    public Guid? CurrentPeer
    {
        get { lock (_gate) return _currentPeer; }
    }

    public PunchScheduler(IPuncher puncher)
    {
        ArgumentNullException.ThrowIfNull(puncher);
        _puncher = puncher;
        _loop = LoopAsync(_cts.Token);
    }

    /// <summary>入队打洞需求（映射 enable/隧道重建/中继回切重试）。同对已在队列或进行中 → 合并（false）。</summary>
    public bool Enqueue(Guid targetDeviceId, Guid? triggerMappingId = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        lock (_gate)
        {
            if (_queuedPeers.Contains(targetDeviceId)) return false; // OQ-11 同对合并
            if (_currentPeer == targetDeviceId) return false;
            _queuedPeers.Add(targetDeviceId);
        }
        _queue.Writer.TryWrite((targetDeviceId, triggerMappingId));
        return true;
    }

    /// <summary>被邀请方路径（0x71 PunchInvite）：不进本地队列，直接执行（02 §5.1③/05 §3.1）。</summary>
    public Task<PunchOutcome> RespondAsync(PunchInvite invite, CancellationToken ct = default)
        => _puncher.RespondAsync(invite, ct);

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var (target, trigger) in _queue.Reader.ReadAllAsync(ct))
            {
                lock (_gate)
                {
                    _queuedPeers.Remove(target);
                    _currentPeer = target;
                }
                PunchOutcome outcome;
                try
                {
                    outcome = await _puncher.InitiateAsync(target, trigger, ct);
                }
                catch (Exception e)
                {
                    outcome = PunchOutcome.Failure(target, $"punch_error: {e.Message}");
                }
                lock (_gate) _currentPeer = null;
                Log?.Invoke(outcome.Ok
                    ? $"打洞成功 peer={outcome.PeerDeviceId} local={outcome.LocalEndpoint} peer={outcome.PeerEndpoint}"
                    : $"打洞失败 peer={target}：{outcome.FailReason}");
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

// M2-22 客户端上报三消息（02 §2.4、FR-C-404/FR-C-1002、05 §5 与服务端 M2-08 口径互证）：
// - 0x72 PunchResult：PunchScheduler.PunchCompleted 会话完成点（仅访问方路径——被邀请方 RespondAsync
//   不触发该事件，服务端亦仅受理发起方，M2-08）。结果映射与服务端落库口径互证：Ok+端点=direct /
//   Ok 无端点=relay（中继成功不携带端点，Puncher 回退路径构造保证）/ !Ok=failed（FailReason 原样）。
//   Ack 前失败（SessionId 空：stun_failed/server_*）无服务端台账会话可归属，不上报（对端窗内必 drop
//   +punch_result_unknown 审计噪声）。
// - 0x62 MappingStatus：状态机迁移点——direct/relay/failed 三态入流水（02 §2.4 行）；
//   punching/disabled 属本地过渡/终态不入（invalid 预留 0x75 联动，届时随事件补）。
// - 0x64 StatsReport：MappingEngine.TrafficSnapshots 30s 周期 + 优雅停机补报（DisposeAsync 终刷）。
//   覆盖式绝对值（M2-08 幂等口径）→ 零值条目也上报：重启清零须如实覆盖服务端旧行，否则残留过期累计。
// 三者均无 Ack（fire-and-forget，02 §2.4）；连接未就绪/发送失败静默记日志——0x64 下周期自然重试，
// 0x62/0x72 为即时事件快照，丢失可容忍（下次状态迁移/会话重建即有新样本）。
using P2P.Client.Mapping;
using P2P.Client.Punch;
using P2P.Core.Protocol;
using ProtocolEndpoint = P2P.Core.Protocol.Endpoint;

namespace P2P.Client.Control;

/// <summary>可调参数（0x64 周期=02 §2.4 定值 30s；测试缝注入秒级以下验证周期触发与停机补报）。</summary>
public sealed record ClientReporterOptions
{
    public TimeSpan StatsInterval { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>客户端上报器：0x72 打洞结果 / 0x62 映射状态 / 0x64 流量统计（事件与周期双驱动）。</summary>
public sealed class ClientReporter : IAsyncDisposable
{
    private readonly ControlClient _control;
    private readonly PunchScheduler _scheduler;
    private readonly MappingEngine _engine;
    private readonly ClientReporterOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _statsLoop;
    private int _disposed;

    /// <summary>诊断日志（宿主接 Serilog）。</summary>
    public event Action<string>? Log;

    public ClientReporter(ControlClient control, PunchScheduler scheduler, MappingEngine engine,
        ClientReporterOptions? options = null)
    {
        _control = control;
        _scheduler = scheduler;
        _engine = engine;
        _options = options ?? new ClientReporterOptions();
        _scheduler.PunchCompleted += OnPunchCompleted;
        _engine.StateChanged += OnStateChanged;
        _statsLoop = StatsLoopAsync(_cts.Token);
    }

    // ── 0x72 打洞结果（会话完成点）──────────────────────────────────

    private void OnPunchCompleted(PunchOutcome outcome)
    {
        if (outcome.SessionId == Guid.Empty) return; // Ack 前失败：无服务端会话可归属（M2-08 台账口径）
        _ = SendQuietlyAsync(new PunchResult(
            _control.NextSeq(), _control.TimestampMs(), MsgType.PunchResult,
            outcome.SessionId, outcome.Ok,
            EndpointFor(outcome.Ok, outcome.Session?.ViaRelay == true, outcome.PeerEndpoint),
            outcome.FailReason));
    }

    /// <summary>结果端点映射（与服务端 M2-08 三态判据互证）：直连成功携带对端公网端点；
    /// 中继成功（ViaRelay）与失败均不携带——Ok 无端点即 relay 的表达约定。</summary>
    internal static ProtocolEndpoint? EndpointFor(bool ok, bool viaRelay, System.Net.IPEndPoint? peerEndpoint)
        => ok && !viaRelay && peerEndpoint is { } ep
            ? new ProtocolEndpoint(ep.Address.ToString(), (ushort)ep.Port)
            : null;

    // ── 0x62 映射状态（状态机迁移点）────────────────────────────────

    private void OnStateChanged(MappingStateEvent e)
    {
        if (StatusStringFor(e.State) is not { } state) return; // punching/disabled/invalid 不入流水
        _ = SendQuietlyAsync(new MappingStatus(
            _control.NextSeq(), _control.TimestampMs(), MsgType.MappingStatus,
            e.MappingId, state, e.Detail));
    }

    /// <summary>上报态过滤：direct/relay/failed（02 §2.4 行"打洞结果/直连/中继/失败"）；
    /// 其余返回 null。字符串与 04 §2.8 mapping_state.state 同表（MappingSyncService.StateString）。</summary>
    internal static string? StatusStringFor(MappingState state) => state switch
    {
        MappingState.Direct => "direct",
        MappingState.Relay => "relay",
        MappingState.Failed => "failed",
        _ => null,
    };

    // ── 0x64 流量统计（30s 周期 + 停机补报）─────────────────────────

    private async Task StatsLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.StatsInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                await ReportStatsAsync(ct);
        }
        catch (OperationCanceledException) { /* 停机 */ }
    }

    private Task ReportStatsAsync(CancellationToken ct)
    {
        // 全量条目（含零值）：覆盖式 upsert 语义下，重启清零须如实到达服务端（M2-08 决议）
        var entries = _engine.TrafficSnapshots()
            .Select(t => new StatsEntry(t.MappingId, (ulong)t.BytesUp, (ulong)t.BytesDown,
                (ulong)t.RelayBytes))
            .ToArray();
        if (entries.Length == 0) return Task.CompletedTask;
        return SendQuietlyAsync(new StatsReport(
            _control.NextSeq(), _control.TimestampMs(), MsgType.StatsReport, entries), ct);
    }

    // ── 出站与生命周期 ───────────────────────────────────────────────

    private async Task SendQuietlyAsync(IPcpMessage message, CancellationToken ct = default)
    {
        try { await _control.SendAsync(message, ct); }
        catch (Exception e)
        {
            Log?.Invoke($"上报 0x{message.MsgType:X2} 失败：{e.Message}（0x64 下周期重试；事件快照待下次迁移）");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _scheduler.PunchCompleted -= OnPunchCompleted;
        _engine.StateChanged -= OnStateChanged;
        _cts.Cancel();
        try { await _statsLoop; } catch { /* 取消路径 */ }
        // 优雅停机补报（02 §2.4 行）：终刷一次累计值——末周期后的增量不丢。
        // 须先于引擎/控制通道销毁（调用方停机序：reporter → engine → … → control）
        try { await ReportStatsAsync(CancellationToken.None); }
        catch (Exception e) { Log?.Invoke($"停机补报失败：{e.Message}"); }
        _cts.Dispose();
    }
}

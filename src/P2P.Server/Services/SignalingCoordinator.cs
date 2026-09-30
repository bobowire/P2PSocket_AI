using Microsoft.EntityFrameworkCore;
using P2P.Core.Protocol;
using P2P.Server.Data;

namespace P2P.Server.Services;

/// <summary>
/// 打洞信令协调（02 §5.1、05 §5，OQ-11/18/TD-19）：
/// 0x70（含发起方端点）→ L1/L2 授权 → ① 向 B 下发 PunchInvite → 收 B 的 0x76 →
/// ② 向 A 下发延后 Ack（B 端点）。per-device 至多一个活跃会话：并发申请排队、同对去重。
/// B 离线 → 4005；0x76 超 10s 未达 → 5001 失败收尾。
/// relayAllowed 真实合成（M2-07）：= server_config relay_enabled（全局开关）AND 中继限速余量
/// （TD-23/M3-07 收口：RelayRateLimiter.HasBudget——令牌欠账期拒新，0x74 分配闸同口径）
/// ——0x71/0x70 Ack 随会话携带，客户端 Puncher 出队时与本地 peers.json 合成。
/// 并发路数 N（OQ-19/TD-20，M2-16）：取 0x70 punchConcurrency 经 PunchPolicy.Normalize 校验
/// （1~5 缺省 3），经 0x71/0x70 Ack 的 PunchCount 统一回填——双方该次打洞执行同一 N（02 §5.2②）。
/// 会话台账（M2-07）：结束后短期保留 sessionId → 设备对（M2-08 扩为含 proto/N/起始时刻），
/// 供 0x74 RelayAllocate 解析对端与 0x72 PunchResult 补齐统计上下文。
/// 0x73 PunchRetry 回切协调（M2-19，02 §6.2/OQ-7）：台账 → 活中继反查解析设备对 →
/// 校验访问方=原发起方 → 向双端下发 0x73（通知重打；A 侧应答兼受理回执，随后走全新 0x70）。
/// 0x72 PunchResult → punch_stats 落库（M2-08，FR-S-503、03 §2.9）：发起方上报、sessionId 去重。
/// </summary>
public sealed class SignalingCoordinator : IAsyncDisposable
{
    public static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(10);

    /// <summary>打洞会话台账保留期：覆盖打洞执行超时（10s）与失败上报/中继申请的到达余量。</summary>
    public static readonly TimeSpan RelayLedgerTtl = TimeSpan.FromSeconds(120);

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly DeviceRegistry _registry;
    private readonly Authorizer _authorizer;
    private readonly AuditLogger _audit;
    private readonly TimeProvider _time;
    private readonly TimeSpan _sessionTimeout; // appsettings punch.timeoutSec（08 §5.1）
    private readonly Func<Guid, (Guid InitiatorId, Guid TargetId)?>? _activeRelayLookup; // 活中继反查（M2-19，RelayService）
    private readonly RelayRateLimiter? _rateLimiter; // TD-23/M3-07：relayAllowed 余量合成（null=不限恒真）
    private readonly object _gate = new();
    private readonly Dictionary<Guid, PunchSession> _sessions = [];   // sessionId → 活跃会话
    private readonly Dictionary<Guid, RelayLedgerEntry> _relayLedger = []; // 结束会话台账（0x74 对端解析 + 0x72 统计上下文）
    private readonly Queue<(ControlSession Session, PunchRequest Msg)> _pending = new(); // 排队申请
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _reaper;
    private int _disposed; // 宿主 StopAsync 与容器释放各调一次（幂等）

    public SignalingCoordinator(IDbContextFactory<AppDbContext> dbFactory, DeviceRegistry registry,
        Authorizer authorizer, AuditLogger audit, TimeProvider? time = null, TimeSpan? sessionTimeout = null,
        Func<Guid, (Guid InitiatorId, Guid TargetId)?>? activeRelayLookup = null,
        RelayRateLimiter? rateLimiter = null)
    {
        _dbFactory = dbFactory;
        _registry = registry;
        _authorizer = authorizer;
        _audit = audit;
        _time = time ?? TimeProvider.System;
        _sessionTimeout = sessionTimeout ?? SessionTimeout;
        _activeRelayLookup = activeRelayLookup; // 台账（120s）外的回切解析兜底：中继会话存活即设备对在册
        _rateLimiter = rateLimiter;
        _reaper = ReaperAsync(_cts.Token);
    }

    private sealed class PunchSession
    {
        public required Guid SessionId { get; init; }
        public required Guid InitiatorId { get; init; }
        public required Guid TargetId { get; init; }
        public required ControlSession Initiator { get; init; }   // A 连接（收延后 Ack）
        public required ControlSession Target { get; init; }      // B 连接（收 PunchInvite）
        public required PeerInfo InitiatorInfo { get; init; }
        public required PeerInfo TargetInfo { get; init; }
        public required EndpointPair RequesterEndpoints { get; init; }
        public required string Proto { get; init; }               // 0x70 载荷（udp|tcp，M2-08 统计）
        public required byte PunchCount { get; init; }
        public required bool RelayAllowed { get; init; }               // relay_enabled 合成（M2-07）
        public required DateTimeOffset CreatedAt { get; init; }
    }

    /// <summary>结束会话台账条目（120s）：0x74/0x73 对端解析 + 0x72 统计上下文（proto/N/起始时刻）。</summary>
    public sealed record RelayLedgerEntry(
        Guid InitiatorId, Guid TargetId, string Proto, byte PunchCount,
        DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

    // ── 0x70 PunchRequest（发起方 A）──────────────────────────────────

    public async Task HandlePunchRequestAsync(ControlSession session, PunchRequest msg)
    {
        // L1+L2+L3（M2-11：TriggerMappingId 关联映射现值过白名单，防换段绕过 0x60 校验点）
        var verdict = await _authorizer.CheckPunchAsync(session, msg.TargetDeviceId, msg.TriggerMappingId);
        if (!verdict.Allowed)
        {
            await _audit.WriteAsync("punch_deny", session.DeviceId,
                detail: new { msg.TargetDeviceId, verdict.Reason, verdict.Code });
            await session.SendErrorAsync(verdict.Code, verdict.Reason);
            return;
        }

        var targetSession = _registry.TryGet(msg.TargetDeviceId);
        if (targetSession is null)
        {
            // B 离线 → 立即 4005，A 不等超时（OQ-18）
            await session.SendErrorAsync(ErrorCode.TargetOffline, "target_offline");
            return;
        }

        lock (_gate)
        {
            // 同设备对去重：进行中不重复邀请（02 §2.4；客户端侧串行队列下属异常重发）
            if (_sessions.Values.Any(s =>
                    (s.InitiatorId == session.DeviceId && s.TargetId == msg.TargetDeviceId)
                    || (s.InitiatorId == msg.TargetDeviceId && s.TargetId == session.DeviceId)))
                return;

            // per-device 至多一个活跃会话：A 或 B 忙 → 排队（Ack 延后语义覆盖）
            if (IsDeviceBusy(session.DeviceId) || IsDeviceBusy(msg.TargetDeviceId))
            {
                _pending.Enqueue((session, msg));
                return;
            }
        }

        await StartSessionAsync(session, msg, targetSession);
    }

    private async Task StartSessionAsync(ControlSession initiator, PunchRequest msg, ControlSession target)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var devices = await db.Devices.AsNoTracking()
            .Where(d => d.Id == msg.TargetDeviceId || d.Id == initiator.DeviceId)
            .ToDictionaryAsync(d => d.Id);
        if (!devices.TryGetValue(initiator.DeviceId, out var a) ||
            !devices.TryGetValue(msg.TargetDeviceId, out var b))
            return; // 设备记录消失（解绑竞态）：静默丢弃，A 侧由超时兜底

        var s = new PunchSession
        {
            SessionId = Guid.NewGuid(),
            InitiatorId = a.Id,
            TargetId = b.Id,
            Initiator = initiator,
            Target = target,
            InitiatorInfo = new PeerInfo(a.Id, a.DeviceName, a.RemoteCode, a.StaticPubKey),
            TargetInfo = new PeerInfo(b.Id, b.DeviceName, b.RemoteCode, b.StaticPubKey),
            RequesterEndpoints = msg.RequesterEndpoints ?? new EndpointPair(null, null),
            Proto = string.IsNullOrWhiteSpace(msg.Proto) ? "udp" : msg.Proto, // 旧端缺省容忍（02 §7）
            // OQ-19/TD-20（M2-16）：取发起方请求值（越界/缺省 → 3，容忍哲学），Ack/Invite 统一回填
            PunchCount = PunchPolicy.Normalize(msg.PunchConcurrency),
            // M2-07→M3-07（TD-23 收口）：全局开关 AND 限速余量（欠账期假；0x74 分配闸同口径）
            // ——与设备级 peers.json 在客户端合成（05 §3.1）
            RelayAllowed = new ServerConfigStore(db).GetBool("relay_enabled")
                && (_rateLimiter?.HasBudget() ?? true),
            CreatedAt = _time.GetLocalNow(),
        };

        lock (_gate)
        {
            // 双检：入队后设备可能已被更早启动的会话占用
            if (IsDeviceBusy(s.InitiatorId) || IsDeviceBusy(s.TargetId))
            {
                _pending.Enqueue((initiator, msg));
                return;
            }
            _sessions[s.SessionId] = s;
        }

        // ① 第一段：向 B 下发 PunchInvite（A 信息/端点/N/relayAllowed，02 §5.1②）
        var invite = new PunchInvite(s.Target.NextSeq(), s.Target.ServerTimestamp(), MsgType.PunchInvite,
            s.SessionId, s.InitiatorInfo, s.RequesterEndpoints, s.PunchCount, s.RelayAllowed);
        await SafePushAsync(s.Target, invite); // B 中途掉线：由 reaper 超时收尾
    }

    // ── 0x76 PunchEndpoint（被邀请方 B 回传端点）───────────────────────

    public async Task HandlePunchEndpointAsync(ControlSession session, PunchEndpoint msg)
    {
        PunchSession? s;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(msg.SessionId, out s)) return; // 迟到上报：会话已收尾
            if (s.TargetId != session.DeviceId) return;               // 仅被邀请方可上报本会话
            _sessions.Remove(msg.SessionId); // 端点就绪 → 会话完成
            RetainForRelayNoLock(s); // 台账：0x74 打洞失败后解析对端（M2-07）
        }

        // ② 第二段：向 A 下发延后 Ack（B 信息 + B 端点；OQ-18/TD-19）
        var ack = new PunchRequestAck(s.Initiator.NextSeq(), s.Initiator.ServerTimestamp(),
            MsgType.PunchRequest, s.SessionId, s.TargetInfo, msg.Endpoints, s.PunchCount, s.RelayAllowed);
        await SafePushAsync(s.Initiator, ack); // A 中途掉线：无接收方，静默

        await ProcessQueueAsync();
    }

    /// <summary>0x74 中继分配的设备对解析（02 §6.1①，M2-07）：打洞会话结束后台账短期保留。</summary>
    public (Guid InitiatorId, Guid TargetId)? ResolveRelayPeers(Guid punchSessionId)
    {
        lock (_gate)
        {
            if (_relayLedger.TryGetValue(punchSessionId, out var r)
                && r.ExpiresAt > _time.GetLocalNow())
                return (r.InitiatorId, r.TargetId);
            return null;
        }
    }

    // ── 0x72 PunchResult 打洞结果统计（M2-08，FR-S-503/FR-C-404、03 §2.9）──

    /// <summary>0x72 落库上下文（台账未过期部分）：proto/N 取自 0x70 会话，时长 = CreatedAt→上报到达。</summary>
    public RelayLedgerEntry? ResolvePunchContext(Guid sessionId)
    {
        lock (_gate)
        {
            if (_relayLedger.TryGetValue(sessionId, out var r)
                && r.ExpiresAt > _time.GetLocalNow())
                return r;
            return null;
        }
    }

    /// <summary>
    /// 打洞结果落 punch_stats（FR-S-503）：仅发起方可报、sessionId 去重（先到先记）。
    /// result 映射：Ok+端点=direct；Ok 无端点=relay（回退成功，FailReason 携带打洞失败原因）；
    /// !Ok=failed。proto/concurrency/duration_ms 不在 0x72 载荷 → 台账上下文补齐（120s 窗覆盖
    /// 直连/失败/中继回退全部上报时点；窗外=异常迟到，drop+审计）。
    /// </summary>
    public async Task HandlePunchResultAsync(ControlSession session, PunchResult msg)
    {
        var ctx = ResolvePunchContext(msg.SessionId);
        if (ctx is null || ctx.InitiatorId != session.DeviceId)
        {
            await _audit.WriteAsync("punch_result_unknown", session.DeviceId,
                detail: new { msg.SessionId, msg.Ok, hasEndpoint = msg.Endpoint is not null });
            return;
        }

        var result = !msg.Ok ? "failed" : msg.Endpoint is not null ? "direct" : "relay";
        var durationMs = (int)Math.Clamp(
            (_time.GetLocalNow() - ctx.CreatedAt).TotalMilliseconds, 0, int.MaxValue);

        await using var db = await _dbFactory.CreateDbContextAsync();
        // 去重：同 session 重复上报（重发/竞态）不重复落行——首份结果即定论
        if (await db.PunchStats.AsNoTracking().AnyAsync(p => p.SessionId == msg.SessionId))
            return;

        db.PunchStats.Add(new PunchStat
        {
            Ts = _time.GetLocalNow().UtcDateTime,
            SessionId = msg.SessionId,
            InitiatorId = ctx.InitiatorId,
            TargetId = ctx.TargetId,
            Proto = ctx.Proto,
            Concurrency = ctx.PunchCount,
            Result = result,
            Reason = msg.FailReason,
            DurationMs = durationMs,
        });
        await db.SaveChangesAsync();
    }

    // ── 0x73 PunchRetry 回切协调（M2-19，02 §6.2/OQ-7）───────────────

    /// <summary>中继回切直连（OQ-7）：访问方 60s 周期请求协调重打。台账 → 活中继反查解析设备对，
    /// 校验申请方=原发起方；通过则向双端下发 0x73{原 sessionId}（A 侧同族应答=受理回执，
    /// B 侧推送=重打预备通知——实际端点交换与打洞由 A 随后的全新 0x70 驱动，02 §5.1 两段式不变）。</summary>
    public async Task HandlePunchRetryAsync(ControlSession session, PunchRetry msg)
    {
        var peers = ResolveRelayPeers(msg.SessionId)
            ?? _activeRelayLookup?.Invoke(msg.SessionId);
        if (peers is not { } p || p.InitiatorId != session.DeviceId)
        {
            // 台账与活中继均无此会话/非发起方申请：1001——客户端退化为全新 0x70（等效回切路径）
            await SafeErrorAsync(session, ErrorCode.BadRequest, "session_unknown");
            return;
        }

        var target = _registry.TryGet(p.TargetId);
        if (target is null)
        {
            await SafeErrorAsync(session, ErrorCode.TargetOffline, "target_offline");
            return;
        }

        // 通知双端重打（02 §6.2：载荷均为原 sessionId）：A 的应答走同族 0x73（请求-应答配对，02 §2.4）
        await SafePushAsync(session, new PunchRetry(session.NextSeq(), session.ServerTimestamp(),
            MsgType.PunchRetry, msg.SessionId));
        await SafePushAsync(target, new PunchRetry(target.NextSeq(), target.ServerTimestamp(),
            MsgType.PunchRetry, msg.SessionId));
    }

    /// <summary>结束会话入台账（须持锁）：完成与超时两路径共用。</summary>
    private void RetainForRelayNoLock(PunchSession s)
        => _relayLedger[s.SessionId] = new RelayLedgerEntry(
            s.InitiatorId, s.TargetId, s.Proto, s.PunchCount, s.CreatedAt,
            _time.GetLocalNow() + RelayLedgerTtl);

    /// <summary>排队申请推进：A、B 均空闲者依次启动。</summary>
    private async Task ProcessQueueAsync()
    {
        List<(ControlSession Session, PunchRequest Msg)> ready = [];
        lock (_gate)
        {
            var retries = new Queue<(ControlSession, PunchRequest)>();
            while (_pending.Count > 0)
            {
                var item = _pending.Dequeue();
                if (!IsDeviceBusy(item.Session.DeviceId) && !IsDeviceBusy(item.Msg.TargetDeviceId))
                    ready.Add(item);
                else
                    retries.Enqueue(item); // 仍忙：保留（维持到达顺序）
            }
            while (retries.Count > 0) _pending.Enqueue(retries.Dequeue());
        }
        foreach (var (session, msg) in ready)
        {
            var target = _registry.TryGet(msg.TargetDeviceId);
            if (target is null)
            {
                await session.SendErrorAsync(ErrorCode.TargetOffline, "target_offline");
                continue;
            }
            await StartSessionAsync(session, msg, target);
        }
    }

    private bool IsDeviceBusy(Guid deviceId)
        => _sessions.Values.Any(s => s.InitiatorId == deviceId || s.TargetId == deviceId);

    /// <summary>向可能已断连的会话推送（发送失败=无接收方，静默；AI-19）。</summary>
    private static async Task SafePushAsync<T>(ControlSession session, T message) where T : class, IPcpMessage
    {
        try { await session.PushAsync(message).ConfigureAwait(false); }
        catch (Exception) { /* 连接已关：消息无处投递 */ }
    }

    private static async Task SafeErrorAsync(ControlSession session, int code, string reason)
    {
        try { await session.SendErrorAsync(code, reason).ConfigureAwait(false); }
        catch (Exception) { /* 连接已关 */ }
    }

    // ── 会话超时收尾（0x76 超 10s 未达 → 5001，05 §5）────────────────

    private async Task ReaperAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _time);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var now = _time.GetLocalNow();
                List<PunchSession> expired = [];
                lock (_gate)
                {
                    foreach (var s in _sessions.Values.Where(s => now - s.CreatedAt > _sessionTimeout))
                        expired.Add(s);
                    foreach (var s in expired)
                    {
                        _sessions.Remove(s.SessionId);
                        RetainForRelayNoLock(s); // 超时收尾同样可中继（0x76 未达≠不可回退，M2-07）
                    }
                    foreach (var stale in _relayLedger.Where(kv => kv.Value.ExpiresAt <= now)
                                 .Select(kv => kv.Key).ToList())
                        _relayLedger.Remove(stale);
                }
                foreach (var s in expired)
                    await SafeErrorAsync(s.Initiator, ErrorCode.PunchFailed, "invite_timeout");
                if (expired.Count > 0)
                    await ProcessQueueAsync();
            }
        }
        catch (OperationCanceledException) { /* 停机 */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await _cts.CancelAsync().ConfigureAwait(false);
        try { await _reaper.ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}

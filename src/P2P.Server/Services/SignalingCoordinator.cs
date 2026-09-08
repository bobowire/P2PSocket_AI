using Microsoft.EntityFrameworkCore;
using P2P.Core.Protocol;
using P2P.Server.Data;

namespace P2P.Server.Services;

/// <summary>
/// 打洞信令协调（02 §5.1、05 §5，OQ-11/18/TD-19）：
/// 0x70（含发起方端点）→ L1/L2 授权 → ① 向 B 下发 PunchInvite → 收 B 的 0x76 →
/// ② 向 A 下发延后 Ack（B 端点）。per-device 至多一个活跃会话：并发申请排队、同对去重。
/// B 离线 → 4005；0x76 超 10s 未达 → 5001 失败收尾；M1 relayAllowed 恒 false（中继 M2）。
/// 0x72 PunchResult 处理属 FR-C-404 → M2。
/// </summary>
public sealed class SignalingCoordinator : IAsyncDisposable
{
    /// <summary>UDP 打洞连发路数（PunchInvite 统一下发；02 §5.1⑤）。</summary>
    public const byte DefaultPunchCount = 3;
    public static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(10);

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly DeviceRegistry _registry;
    private readonly Authorizer _authorizer;
    private readonly AuditLogger _audit;
    private readonly TimeProvider _time;
    private readonly TimeSpan _sessionTimeout; // appsettings punch.timeoutSec（08 §5.1）
    private readonly object _gate = new();
    private readonly Dictionary<Guid, PunchSession> _sessions = [];   // sessionId → 活跃会话
    private readonly Queue<(ControlSession Session, PunchRequest Msg)> _pending = new(); // 排队申请
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _reaper;
    private int _disposed; // 宿主 StopAsync 与容器释放各调一次（幂等）

    public SignalingCoordinator(IDbContextFactory<AppDbContext> dbFactory, DeviceRegistry registry,
        Authorizer authorizer, AuditLogger audit, TimeProvider? time = null, TimeSpan? sessionTimeout = null)
    {
        _dbFactory = dbFactory;
        _registry = registry;
        _authorizer = authorizer;
        _audit = audit;
        _time = time ?? TimeProvider.System;
        _sessionTimeout = sessionTimeout ?? SessionTimeout;
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
        public required byte PunchCount { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
    }

    // ── 0x70 PunchRequest（发起方 A）──────────────────────────────────

    public async Task HandlePunchRequestAsync(ControlSession session, PunchRequest msg)
    {
        var verdict = await _authorizer.CheckPunchAsync(session, msg.TargetDeviceId);
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
            PunchCount = DefaultPunchCount,
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

        // ① 第一段：向 B 下发 PunchInvite（A 信息/端点/N/relayAllowed=false，02 §5.1②）
        var invite = new PunchInvite(s.Target.NextSeq(), s.Target.ServerTimestamp(), MsgType.PunchInvite,
            s.SessionId, s.InitiatorInfo, s.RequesterEndpoints, s.PunchCount, RelayAllowed: false);
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
        }

        // ② 第二段：向 A 下发延后 Ack（B 信息 + B 端点；OQ-18/TD-19）
        var ack = new PunchRequestAck(s.Initiator.NextSeq(), s.Initiator.ServerTimestamp(),
            MsgType.PunchRequest, s.SessionId, s.TargetInfo, msg.Endpoints, s.PunchCount, RelayAllowed: false);
        await SafePushAsync(s.Initiator, ack); // A 中途掉线：无接收方，静默

        await ProcessQueueAsync();
    }

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
                    foreach (var s in expired) _sessions.Remove(s.SessionId);
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

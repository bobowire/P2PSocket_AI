using Microsoft.EntityFrameworkCore;
using P2P.Core.Protocol;
using P2P.Server.Data;

namespace P2P.Server.Services;

/// <summary>
/// 设备离线判定（FR-S-104、01 §3.1 后台任务）：PeriodicTimer 扫描在线会话，
/// 心跳超时（默认 30s，可配）→ 关闭会话（DeviceRegistry 移除 + last_seen_at 落库）。
/// M2-13 兼管理标志兜底（FR-S-105/204）：跨进程 CLI 写库触不到运行中服务器的内存注册表——
/// 每会话按 admin_check_interval（默认 30s）读库复核 disabled：设备禁用 → 引用方 0x75(device_disabled)
/// + 踢线；用户禁用 → 降级 passive + 0x75(user_disabled, newCapability)（进程内 AdminService 即时路径
/// 已踢线者自然移出扫描集，无重复推送）。
/// </summary>
public sealed class PresenceMonitor : IAsyncDisposable
{
    private readonly DeviceRegistry _registry;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _time;
    private readonly InvalidationPusher? _invalidation;
    private readonly TimeSpan _adminCheckInterval;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private int _disposed; // 宿主 StopAsync 与容器释放各调一次（幂等）

    public PresenceMonitor(DeviceRegistry registry, IDbContextFactory<AppDbContext> dbFactory,
        TimeSpan? timeout = null, TimeSpan? period = null, TimeProvider? time = null,
        InvalidationPusher? invalidation = null, TimeSpan? adminCheckInterval = null)
    {
        _registry = registry;
        _dbFactory = dbFactory;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        _time = time ?? TimeProvider.System;
        _invalidation = invalidation;
        _adminCheckInterval = adminCheckInterval ?? TimeSpan.FromSeconds(30);
        _loop = RunAsync(_cts.Token, period ?? TimeSpan.FromSeconds(5));
    }

    private async Task RunAsync(CancellationToken ct, TimeSpan period)
    {
        try
        {
            using var timer = new PeriodicTimer(period, _time);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var now = _time.GetLocalNow();
                foreach (var session in _registry.Sessions())
                {
                    if (now - session.LastSeen > _timeout)
                    {
                        await session.CloseAsync("heartbeat_timeout").ConfigureAwait(false);
                        continue;
                    }
                    await CheckAdminFlagsAsync(session, now).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { /* 停机 */ }
    }

    /// <summary>管理标志兜底（节流：每会话 admin_check_interval 一次读库；瞬时库错不中断扫描轮）。</summary>
    private async Task CheckAdminFlagsAsync(ControlSession session, DateTimeOffset now)
    {
        if (!session.IsEstablished || now - session.LastAdminCheck < _adminCheckInterval)
            return;
        session.LastAdminCheck = now;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
            var flags = await db.Devices.AsNoTracking()
                .Where(d => d.Id == session.DeviceId)
                .Select(d => new
                {
                    d.Disabled,
                    UserDisabled = d.OwnerUserId != null
                        && db.Users.Any(u => u.Id == d.OwnerUserId && u.Disabled),
                })
                .SingleOrDefaultAsync().ConfigureAwait(false);
            if (flags is null)
            {
                await session.CloseAsync("device_gone").ConfigureAwait(false); // 解绑竞态防御
                return;
            }
            if (flags.Disabled)
            {
                if (_invalidation is not null)
                    await _invalidation.PushTargetingAsync(session.DeviceId, InvalidationReason.DeviceDisabled)
                        .ConfigureAwait(false);
                await session.CloseAsync("device_disabled").ConfigureAwait(false);
                return;
            }
            if (flags.UserDisabled && session.Capability == CapabilityMode.Normal)
            {
                session.Capability = CapabilityMode.Passive;
                if (_invalidation is not null)
                    await _invalidation.PushOwnedAsync(session.DeviceId, InvalidationReason.UserDisabled,
                        newCapability: CapabilityMode.Passive).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // 兜底检查不因瞬时库错误中断扫描轮（下轮重试）
        }
    }

    /// <summary>离线落库（CloseAsync 之外的兜底：崩溃后启动时刷新遗留在线标记）。</summary>
    public async Task PersistLastSeenAsync(Guid deviceId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var stamp = _time.GetLocalNow().UtcDateTime;
        await db.Devices.Where(d => d.Id == deviceId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSeenAt, stamp), ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await _cts.CancelAsync().ConfigureAwait(false);
        try { await _loop.ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}

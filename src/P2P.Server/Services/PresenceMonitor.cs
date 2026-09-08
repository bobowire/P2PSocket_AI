using Microsoft.EntityFrameworkCore;
using P2P.Server.Data;

namespace P2P.Server.Services;

/// <summary>
/// 设备离线判定（FR-S-104、01 §3.1 后台任务）：PeriodicTimer 扫描在线会话，
/// 心跳超时（默认 30s，可配）→ 关闭会话（DeviceRegistry 移除 + last_seen_at 落库）。
/// </summary>
public sealed class PresenceMonitor : IAsyncDisposable
{
    private readonly DeviceRegistry _registry;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private int _disposed; // 宿主 StopAsync 与容器释放各调一次（幂等）

    public PresenceMonitor(DeviceRegistry registry, IDbContextFactory<AppDbContext> dbFactory,
        TimeSpan? timeout = null, TimeSpan? period = null, TimeProvider? time = null)
    {
        _registry = registry;
        _dbFactory = dbFactory;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        _time = time ?? TimeProvider.System;
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
                        await session.CloseAsync("heartbeat_timeout").ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { /* 停机 */ }
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

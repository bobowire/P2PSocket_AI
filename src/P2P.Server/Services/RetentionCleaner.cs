using Microsoft.EntityFrameworkCore;
using P2P.Server.Data;

namespace P2P.Server.Services;

/// <summary>
/// 统计保留清理（M2-08，OQ-17、03 §2.7/§2.9）：audit_logs 与 punch_stats 一并执行——
/// 启动即清一次，其后每日一轮；天数键每轮现读（改库配置次轮生效）。
/// 过期口径：ts &lt; now − retention 天（保留期整日内不删）。
/// </summary>
public sealed class RetentionCleaner : IAsyncDisposable
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _loop;
    private int _disposed;

    public RetentionCleaner(IDbContextFactory<AppDbContext> dbFactory, TimeProvider? time = null,
        TimeSpan? interval = null)
    {
        _dbFactory = dbFactory;
        _time = time ?? TimeProvider.System;
        _interval = interval ?? TimeSpan.FromHours(24);
        _loop = LoopAsync(_cts.Token);
    }

    /// <summary>启动轮完成信号（测试确定性驱动用；成功失败均完成）。</summary>
    public Task StartupRound => _startup.Task;

    /// <summary>单轮清理（构造即跑；公开供测试直接驱动）。</summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var auditDays = ReadDays(db, "audit_retention_days");
        var punchDays = ReadDays(db, "punch_retention_days");
        var now = _time.GetLocalNow().UtcDateTime;

        var auditCut = now.AddDays(-auditDays);
        if (await db.AuditLogs.AnyAsync(a => a.Ts < auditCut, ct))
            await db.AuditLogs.Where(a => a.Ts < auditCut).ExecuteDeleteAsync(ct);

        var punchCut = now.AddDays(-punchDays);
        if (await db.PunchStats.AnyAsync(p => p.Ts < punchCut, ct))
            await db.PunchStats.Where(p => p.Ts < punchCut).ExecuteDeleteAsync(ct);
    }

    /// <summary>键缺失/非法回退 90（种子默认；库内值被改坏不致拒启）。</summary>
    private static int ReadDays(AppDbContext db, string key)
    {
        var raw = db.ServerConfig.AsNoTracking().SingleOrDefault(c => c.Key == key)?.Value;
        return int.TryParse(raw, out var days) && days >= 1 ? days : 90;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_interval, _time);
        try
        {
            await RunQuietlyAsync(ct).ConfigureAwait(false); // 启动首轮
        }
        finally
        {
            _startup.TrySetResult();
        }
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            await RunQuietlyAsync(ct).ConfigureAwait(false); // 周期轮：单轮失败不拖垮循环（AI-19）
    }

    private async Task RunQuietlyAsync(CancellationToken ct)
    {
        try { await RunOnceAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { /* 本轮清理失败：静默，下轮/下次启动重试 */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await _cts.CancelAsync().ConfigureAwait(false);
        try { await _loop.ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}

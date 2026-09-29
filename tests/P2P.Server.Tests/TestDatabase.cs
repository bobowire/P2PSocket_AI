using System.Linq.Expressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using P2P.Server.Data;

namespace P2P.Server.Tests;

/// <summary>夹具级独立 SQLite 临时文件库：多上下文各持物理连接共享同一库（页级锁 + BUSY 等待，
/// 并发安全）。替代单连接共享实例的 :memory: 模式——后者在多会话 handler 并发时互踩语句缓存，
/// 抛 SQLite Error 5 'unable to modify user-function due to active statements'（M2-12 定位，
/// M1-31 满载瞬态家族同源）。每夹具唯一文件名，Dispose 清池删文件。</summary>
internal sealed class TestDatabase : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"p2p-test-{Guid.NewGuid():N}.db");

    public string DataSource { get; }

    public TestDatabase()
    {
        DataSource = $"Data Source={_path.Replace('\\', '/')};Default Timeout=2";
        // WAL：读写不互斥、无 journal 文件往复，多连接并发吞吐最优
        using var boot = new SqliteConnection(DataSource);
        boot.Open();
        using var wal = boot.CreateCommand();
        wal.CommandText = "PRAGMA journal_mode=WAL;";
        _ = wal.ExecuteScalar();
    }

    public void Dispose()
    {
        try { SqliteConnection.ClearAllPools(); } catch { /* 并行夹具占用中：留待系统临时目录清理 */ }
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_path + suffix); } catch { /* 句柄占用即放弃（临时目录兜底） */ }
    }
}

internal static class AuditWaitExtensions
{
    /// <summary>轮询等待审计行落库（应答/错误帧先于审计写提交，立即查询存在读早于写竞态；
    /// 截止后末查一次并以该结果定论）。</summary>
    public static async Task<bool> WaitAuditAsync(this AppDbContext db,
        Expression<Func<AuditLog, bool>> predicate, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            if (await db.AuditLogs.AsNoTracking().AnyAsync(predicate)) return true;
            if (Environment.TickCount64 > deadline) return false;
            await Task.Delay(50);
        }
    }

    /// <summary>轮询等待审计行数达标（多行场景：如连续两次拒绝各写一行）。</summary>
    public static async Task<int> WaitAuditCountAsync(this AppDbContext db,
        Expression<Func<AuditLog, bool>> predicate, int expect, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var count = await db.AuditLogs.AsNoTracking().CountAsync(predicate);
            if (count >= expect || Environment.TickCount64 > deadline) return count;
            await Task.Delay(50);
        }
    }
}

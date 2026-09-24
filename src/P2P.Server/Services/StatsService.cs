using Microsoft.EntityFrameworkCore;
using P2P.Core.Protocol;
using P2P.Server.Data;

namespace P2P.Server.Services;

/// <summary>
/// 状态与流量上报处理（M2-08，FR-C-404/FR-C-1002、03 §2.6）：
/// 0x62 MappingStatus → 审计流水（映射运行态不落表——客户端状态机为真相源，服务端留观测轨迹）；
/// 0x64 StatsReport → mapping_stats 覆盖式 upsert（载荷值 = 客户端本地累计绝对值 → 重发不叠加、
/// 重复/乱序天然幂等——控制通道单连接有序，跨重连迟到旧值亦以"最新到达=客户端当前真相"覆盖）。
/// 两消息均被动同步类：passive 允许（02 §2.5）、fire-and-forget 无 Ack。
/// </summary>
public sealed class StatsService(IDbContextFactory<AppDbContext> dbFactory, AuditLogger audit, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task HandleMappingStatusAsync(ControlSession session, MappingStatus msg)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        // 仅受理本人映射的状态：他人/已删映射（删后迟到上报竞态）静默忽略
        var owned = await db.Mappings.AsNoTracking()
            .AnyAsync(m => m.Id == msg.MappingId && m.OwnerDeviceId == session.DeviceId);
        if (!owned)
            return;

        await audit.WriteAsync("mapping_status", session.DeviceId,
            detail: new { msg.MappingId, msg.State, msg.Detail });
    }

    public async Task HandleStatsReportAsync(ControlSession session, StatsReport msg)
    {
        if (msg.Entries is not { Length: > 0 })
            return;

        await using var db = await dbFactory.CreateDbContextAsync();
        var ids = msg.Entries.Select(e => e.MappingId).Distinct().ToList();
        var owned = await db.Mappings.AsNoTracking()
            .Where(m => m.OwnerDeviceId == session.DeviceId && ids.Contains(m.Id))
            .Select(m => m.Id)
            .ToHashSetAsync();

        var existing = await db.MappingStats
            .Where(s => ids.Contains(s.MappingId))
            .ToDictionaryAsync(s => s.MappingId);

        var now = _time.GetLocalNow().UtcDateTime;
        HashSet<Guid> seen = []; // 同报告内重复项去重（避免新增路径 PK 撞车）
        foreach (var e in msg.Entries)
        {
            if (!owned.Contains(e.MappingId) || !seen.Add(e.MappingId))
                continue; // 他人/未知映射或同报告重复项：跳过（批量报告逐项容错）

            if (existing.TryGetValue(e.MappingId, out var stat))
            {
                stat.BytesUp = (long)e.BytesUp;
                stat.BytesDown = (long)e.BytesDown;
                stat.RelayBytes = (long)e.RelayBytes;
                stat.UpdatedAt = now;
            }
            else
            {
                db.MappingStats.Add(new MappingStat
                {
                    MappingId = e.MappingId,
                    BytesUp = (long)e.BytesUp,
                    BytesDown = (long)e.BytesDown,
                    RelayBytes = (long)e.RelayBytes,
                    UpdatedAt = now,
                });
            }
        }
        await db.SaveChangesAsync();
    }
}

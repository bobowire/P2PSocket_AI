// M3-08 映射数据 API（FR-S-823、04 §3.2、TD-22）：GET /api/mappings 全量只读——
// 配置（Name/LocalPort/Proto/TargetDeviceId/TargetAddr/TargetPort/Enabled）+归属设备名与
// 远程码+对端设备名+mapping_stats 累计流量 join+**最后已知状态**（TD-22：0x62 mapping_status
// 审计流水每映射最新一条投影 direct/relay/failed/invalid；无流水=unknown——客户端状态机是
// 真相源，服务端管理面仅流水投影）。?deviceId=（归属设备）/?status=（投影态）过滤。
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using P2P.Server.Data;
using P2P.Server.Services;

namespace P2P.Server.Web;

/// <summary>映射数据端点组（挂 ServerWebHost 装配的 WebApplication，认证中间件之后）。
/// 投影核=AdminDashboardApi.LatestStatusByMappingAsync 共享（M3-06 仪表盘同口径）。
/// disabled 映射同样投影最后已知流水（enabled 独立字段供前端置灰——仪表盘分布的
/// enabled 限定是聚合口径，列表是逐映射明细口径）。</summary>
public sealed class AdminMappingsApi(IDbContextFactory<AppDbContext> dbFactory)
{
    /// <summary>0x62 已上报的状态域（StatusStringFor 三态 + invalid 预留）；域外值按 unknown 归一。</summary>
    private static readonly HashSet<string> KnownStates = ["direct", "relay", "failed", "invalid"];

    public void Map(WebApplication app)
    {
        app.MapGet("/api/mappings", async ctx =>
        {
            Guid? deviceFilter = Guid.TryParse(ctx.Request.Query["deviceId"].ToString(), out var dv) ? dv : null;
            var statusFilter = ctx.Request.Query["status"].ToString().ToLowerInvariant();

            await using var db = await dbFactory.CreateDbContextAsync(ctx.RequestAborted);
            var mappings = await db.Mappings.AsNoTracking()
                .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
                .Select(m => new
                {
                    m.Id,
                    m.OwnerDeviceId,
                    m.TargetDeviceId,
                    m.Name,
                    m.LocalPort,
                    m.Proto,
                    m.TargetAddr,
                    m.TargetPort,
                    m.Enabled,
                    m.CreatedAt,
                    OwnerDeviceName = db.Devices.Where(d => d.Id == m.OwnerDeviceId)
                        .Select(d => d.DeviceName).FirstOrDefault() ?? "",
                    OwnerRemoteCode = db.Devices.Where(d => d.Id == m.OwnerDeviceId)
                        .Select(d => d.RemoteCode).FirstOrDefault() ?? "",
                    TargetDeviceName = db.Devices.Where(d => d.Id == m.TargetDeviceId)
                        .Select(d => d.DeviceName).FirstOrDefault() ?? "",
                    BytesUp = db.MappingStats.Where(s => s.MappingId == m.Id)
                        .Select(s => (long?)s.BytesUp).FirstOrDefault() ?? 0,
                    BytesDown = db.MappingStats.Where(s => s.MappingId == m.Id)
                        .Select(s => (long?)s.BytesDown).FirstOrDefault() ?? 0,
                    BytesRelay = db.MappingStats.Where(s => s.MappingId == m.Id)
                        .Select(s => (long?)s.RelayBytes).FirstOrDefault() ?? 0,
                    StatsUpdatedAt = db.MappingStats.Where(s => s.MappingId == m.Id)
                        .Select(s => (DateTime?)s.UpdatedAt).FirstOrDefault(),
                })
                .ToListAsync(ctx.RequestAborted);

            // TD-22 投影（全量映射，含 disabled——逐映射明细口径）
            var latest = await AdminDashboardApi.LatestStatusByMappingAsync(db,
                mappings.Select(m => m.Id).ToHashSet(), ctx.RequestAborted);

            var items = mappings
                .Select(m => new MappingView(
                    m.Id, m.Name, m.LocalPort, m.Proto, m.TargetDeviceId, m.TargetAddr, m.TargetPort,
                    m.Enabled, m.CreatedAt, m.OwnerDeviceId, m.OwnerDeviceName, m.OwnerRemoteCode,
                    m.TargetDeviceName,
                    new MappingBytesView(m.BytesUp, m.BytesDown, m.BytesRelay),
                    m.StatsUpdatedAt,
                    latest.TryGetValue(m.Id, out var s) && KnownStates.Contains(s) ? s : "unknown"))
                .Where(m => deviceFilter is null || m.OwnerDeviceId == deviceFilter)
                .Where(m => statusFilter.Length == 0 || m.Status == statusFilter)
                .ToList();

            await WriteAsync(ctx, StatusCodes.Status200OK, 0, "ok", new MappingListView(items, items.Count));
        });
    }

    private static async Task WriteAsync(HttpContext ctx, int status, int code, string message, object? data = null)
    {
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(new { code, message, data });
    }
}

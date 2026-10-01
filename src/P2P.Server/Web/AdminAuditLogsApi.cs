// M3-08 审计日志 API（NFR-54、04 §3.2）：GET /api/audit-logs?event=&page=&pageSize=——
// newest-first（Id 自增=写入序，降序即最新在前）+事件精确过滤+分页；detail 为原始 JSON 文本
// 透传（前端格式化展示；SEC-51 写入侧已保证不含凭据材料）。
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using P2P.Server.Data;

namespace P2P.Server.Web;

/// <summary>审计日志端点组（挂 ServerWebHost 装配的 WebApplication，认证中间件之后）。</summary>
public sealed class AdminAuditLogsApi(IDbContextFactory<AppDbContext> dbFactory)
{
    private const int MaxPageSize = 100;

    public void Map(WebApplication app)
    {
        app.MapGet("/api/audit-logs", async ctx =>
        {
            var page = Math.Max(1, QueryInt(ctx, "page") ?? 1);
            var pageSize = Math.Clamp(QueryInt(ctx, "pageSize") ?? 20, 1, MaxPageSize);
            var eventFilter = ctx.Request.Query["event"].ToString();

            await using var db = await dbFactory.CreateDbContextAsync(ctx.RequestAborted);
            var query = db.AuditLogs.AsNoTracking();
            if (eventFilter.Length > 0)
                query = query.Where(a => a.Event == eventFilter);
            var total = await query.CountAsync(ctx.RequestAborted);
            var items = await query
                .OrderByDescending(a => a.Id)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(a => new AuditLogView(a.Id, a.Ts, a.Event, a.DeviceId, a.UserId, a.Detail))
                .ToListAsync(ctx.RequestAborted);
            await WriteAsync(ctx, StatusCodes.Status200OK, 0, "ok", new AuditLogListView(items, total, page, pageSize));
        });
    }

    private static int? QueryInt(HttpContext ctx, string key)
        => int.TryParse(ctx.Request.Query[key].ToString(), out var v) ? v : null;

    private static async Task WriteAsync(HttpContext ctx, int status, int code, string message, object? data = null)
    {
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(new { code, message, data });
    }
}

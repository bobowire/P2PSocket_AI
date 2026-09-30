// M3-05 分组与审批 API（FR-S-822/305、04 §3.2）：分组总览、默认分组准入策略、跨分组待审批单。
// 审批动作走 GroupService.DecideJoinRequestAsync 共享核——与 0x53 客户端侧所有者审批同一执行链
// （pending 单向状态机+已成员不重复插行 ⇒ Web 与 0x53 审同一单不双入组）；actor=管理员。
// 默认分组策略双写：default_join_policy 键（种子口径，下次建组生效同 M2-09）+ 默认分组行
// JoinPolicy（存量分组即时生效——0x51 凭码入组读行判断 free/approval）。
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using P2P.Server.Data;
using P2P.Server.Services;

namespace P2P.Server.Web;

/// <summary>分组与审批端点组（挂 ServerWebHost 装配的 WebApplication，认证中间件之后）。</summary>
public sealed class AdminGroupsApi(
    IDbContextFactory<AppDbContext> dbFactory,
    GroupService groups,
    AuditLogger audit)
{
    private record PolicyRequest(string? Policy);

    public void Map(WebApplication app)
    {
        app.MapGet("/api/groups", async ctx =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ctx.RequestAborted);
            var items = await db.Groups.AsNoTracking()
                .OrderBy(g => g.IsDefault ? 0 : 1).ThenBy(g => g.Name).ThenBy(g => g.Id)
                .Select(g => new
                {
                    g.Id,
                    g.Name,
                    g.JoinPolicy,
                    g.IsDefault,
                    g.CreatedAt,
                    OwnerUsername = db.Users.Where(u => u.Id == g.OwnerUserId)
                        .Select(u => u.Username).FirstOrDefault(),
                    MemberCount = db.GroupMembers.Count(m => m.GroupId == g.Id && m.Approved),
                    PendingCount = db.JoinRequests.Count(r => r.GroupId == g.Id && r.Status == "pending"),
                })
                .ToListAsync(ctx.RequestAborted);
            await WriteAsync(ctx, StatusCodes.Status200OK, 0, "ok", new
            {
                items = items.Select(g => new
                {
                    groupId = g.Id,
                    g.Name,
                    g.JoinPolicy,
                    g.IsDefault,
                    g.OwnerUsername,
                    g.MemberCount,
                    g.PendingCount,
                    g.CreatedAt,
                }),
            });
        });

        app.MapPut("/api/groups/default", async ctx =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<PolicyRequest>(ctx.RequestAborted);
            var policy = body?.Policy?.Trim();
            if (policy is not ("free" or "approval"))
            {
                await WriteAsync(ctx, StatusCodes.Status400BadRequest, 1001, "参数错误（policy 须为 free|approval）");
                return;
            }

            await using var db = await dbFactory.CreateDbContextAsync(ctx.RequestAborted);
            // 双写：键（种子/下次建组口径）+ 默认分组行（存量即时生效——0x51 读行判定）
            var entry = await db.ServerConfig.SingleAsync(c => c.Key == "default_join_policy",
                ctx.RequestAborted);
            entry.Value = policy;
            await db.Groups.Where(g => g.IsDefault).ExecuteUpdateAsync(
                s => s.SetProperty(g => g.JoinPolicy, policy), ctx.RequestAborted);
            await db.SaveChangesAsync(ctx.RequestAborted);
            await audit.WriteAsync("default_join_policy_change", detail: new { policy },
                ct: ctx.RequestAborted);
            await WriteAsync(ctx, StatusCodes.Status200OK, 0, "ok", new { policy });
        });

        app.MapGet("/api/group-requests", async ctx =>
        {
            var status = ctx.Request.Query["status"].ToString();
            if (string.IsNullOrEmpty(status))
                status = "pending";
            if (status != "pending") // 历史单不投影：队列只关心待办
            {
                await WriteAsync(ctx, StatusCodes.Status400BadRequest, 1001, "参数错误（status 仅支持 pending）");
                return;
            }

            await using var db = await dbFactory.CreateDbContextAsync(ctx.RequestAborted);
            var items = await (from r in db.JoinRequests.AsNoTracking()
                    where r.Status == "pending"
                    join g in db.Groups on r.GroupId equals g.Id
                    join u in db.Users on g.OwnerUserId equals u.Id into owners
                    from u in owners.DefaultIfEmpty()
                    join d in db.Devices on r.DeviceId equals d.Id into devices
                    from d in devices.DefaultIfEmpty()
                    orderby r.CreatedAt, r.Id
                    select new
                    {
                        r.Id,
                        r.GroupId,
                        GroupName = g.Name,
                        r.DeviceId,
                        DeviceName = d.DeviceName ?? "",
                        OwnerUsername = u.Username ?? "",
                        r.CreatedAt,
                    })
                .ToListAsync(ctx.RequestAborted);
            await WriteAsync(ctx, StatusCodes.Status200OK, 0, "ok", new
            {
                items = items.Select(r => new
                {
                    requestId = r.Id,
                    groupId = r.GroupId,
                    groupName = r.GroupName,
                    deviceId = r.DeviceId,
                    deviceName = r.DeviceName,
                    ownerUsername = r.OwnerUsername,
                    createdAt = r.CreatedAt,
                }),
            });
        });

        app.MapPost("/api/group-requests/{id}/approve", ctx => DecideAsync(ctx, approve: true));
        app.MapPost("/api/group-requests/{id}/reject", ctx => DecideAsync(ctx, approve: false));
    }

    /// <summary>审批/拒绝（GroupService 共享核；actor=admin）。
    /// 单不存在 404；已处理（非 pending）200 {ok:false} 诚实应答（0x53 Ack Ok=false 同口径）。</summary>
    private async Task DecideAsync(HttpContext ctx, bool approve)
    {
        if (!Guid.TryParse(ctx.Request.RouteValues["id"]?.ToString(), out var requestId))
        {
            await WriteAsync(ctx, StatusCodes.Status400BadRequest, 1001, "参数错误");
            return;
        }
        await using var db = await dbFactory.CreateDbContextAsync(ctx.RequestAborted);
        var row = await db.JoinRequests.AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == requestId, ctx.RequestAborted);
        if (row is null)
        {
            await WriteAsync(ctx, StatusCodes.Status404NotFound, 1002, "申请单不存在");
            return;
        }
        if (row.Status != "pending")
        {
            await WriteAsync(ctx, StatusCodes.Status200OK, 0, "已处理", new { ok = false });
            return;
        }

        var adminId = await db.Users.AsNoTracking().Where(u => u.IsAdmin).Select(u => u.Id)
            .SingleOrDefaultAsync(ctx.RequestAborted);
        var ok = await groups.DecideJoinRequestAsync(requestId, approve, actorUserId: adminId);
        await WriteAsync(ctx, StatusCodes.Status200OK, 0, "ok", new { ok });
    }

    private static async Task WriteAsync(HttpContext ctx, int status, int code, string message, object? data = null)
    {
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(new { code, message, data });
    }
}

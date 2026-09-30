// M3-03 用户管理 API（FR-S-820/204、04 §3.2、M2-13）：列表（分页）/禁用/启用/密码重置。
// Web 载体与 CLI 的本质差异=AdminService 进程内直调：传入真实 DeviceRegistry+InvalidationPusher，
// 踢线/降级/0x75 即时生效不待 30s 心跳兜底（CLI 独立进程触不到内存注册表才靠兜底）。
// 密码重置=随机临时密码返回一次（响应即唯一明文出口）+审计（AI-17：detail 不含口令材料）。
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using P2P.Core.Crypto;
using P2P.Server.Data;
using P2P.Server.Services;

namespace P2P.Server.Web;

/// <summary>用户管理端点组（挂 ServerWebHost 装配的 WebApplication，认证中间件之后）。</summary>
public sealed class AdminUsersApi(
    IDbContextFactory<AppDbContext> dbFactory,
    AdminService admin,
    AuditLogger audit)
{
    private const int MaxPageSize = 100;

    /// <summary>临时密码字符集：去混淆字母数字（同邀请码口径，便于人工转录）。</summary>
    private const string TempPasswordCharset = "abcdefghjkmnpqrstuvwxyz23456789";

    public void Map(WebApplication app)
    {
        app.MapGet("/api/users", async ctx =>
        {
            var page = Math.Max(1, QueryInt(ctx, "page") ?? 1);
            var pageSize = Math.Clamp(QueryInt(ctx, "pageSize") ?? 20, 1, MaxPageSize);

            await using var db = await dbFactory.CreateDbContextAsync(ctx.RequestAborted);
            var total = await db.Users.AsNoTracking().CountAsync(ctx.RequestAborted);
            var items = await db.Users.AsNoTracking()
                .OrderBy(u => u.CreatedAt).ThenBy(u => u.Username)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(u => new
                {
                    u.Id,
                    u.Username,
                    u.Disabled,
                    u.IsAdmin,
                    DeviceCount = db.Devices.Count(d => d.OwnerUserId == u.Id),
                    u.CreatedAt,
                })
                .ToListAsync(ctx.RequestAborted);
            await WriteAsync(ctx, StatusCodes.Status200OK, 0, "ok",
                new { items, total, page, pageSize });
        });

        app.MapPost("/api/users/{id}/disable", async ctx =>
        {
            var user = await FindAsync(ctx, ctx.Request.RouteValues["id"]?.ToString());
            if (user is null) return;
            if (user.IsAdmin)
            {
                await WriteAsync(ctx, StatusCodes.Status400BadRequest, 1003, "不能禁用内置管理员（单管理员 D16）");
                return;
            }
            // AdminService 进程内直调：即时降级 passive + 0x75（区别 CLI 30s 兜底窗口）
            if (!await admin.DisableUserAsync(user.Username))
            {
                await WriteAsync(ctx, StatusCodes.Status404NotFound, 1002, "用户不存在");
                return;
            }
            await WriteAsync(ctx, StatusCodes.Status200OK, 0, "ok");
        });

        app.MapPost("/api/users/{id}/enable", async ctx =>
        {
            var user = await FindAsync(ctx, ctx.Request.RouteValues["id"]?.ToString());
            if (user is null) return;
            if (!await admin.EnableUserAsync(user.Username))
            {
                await WriteAsync(ctx, StatusCodes.Status404NotFound, 1002, "用户不存在");
                return;
            }
            await WriteAsync(ctx, StatusCodes.Status200OK, 0, "ok");
        });

        app.MapPut("/api/users/{id}/password-reset", async ctx =>
        {
            var user = await FindAsync(ctx, ctx.Request.RouteValues["id"]?.ToString());
            if (user is null) return;
            if (user.IsAdmin)
            {
                await WriteAsync(ctx, StatusCodes.Status400BadRequest, 1003,
                    "管理员密码请用 change-password 修改（Web 控制台本人操作）");
                return;
            }

            var tempPassword = TempPassword();
            await using var db = await dbFactory.CreateDbContextAsync(ctx.RequestAborted);
            var row = await db.Users.SingleAsync(u => u.Id == user.Id, ctx.RequestAborted);
            row.PasswordHash = PasswordHasher.Hash(tempPassword);
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ctx.RequestAborted);
            await audit.WriteAsync("user_password_reset", userId: user.Id,
                detail: new { user.Username }, ct: ctx.RequestAborted); // 明文只出现在本次响应
            await WriteAsync(ctx, StatusCodes.Status200OK, 0, "ok", new { tempPassword });
        });
    }

    private async Task<User?> FindAsync(HttpContext ctx, string? id)
    {
        if (!Guid.TryParse(id, out var userId))
        {
            await WriteAsync(ctx, StatusCodes.Status400BadRequest, 1001, "参数错误");
            return null;
        }
        await using var db = await dbFactory.CreateDbContextAsync(ctx.RequestAborted);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ctx.RequestAborted);
        if (user is null)
            await WriteAsync(ctx, StatusCodes.Status404NotFound, 1002, "用户不存在");
        return user;
    }

    private static int? QueryInt(HttpContext ctx, string key)
        => int.TryParse(ctx.Request.Query[key].ToString(), out var v) ? v : null;

    private static string TempPassword()
    {
        var chars = new char[10];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = TempPasswordCharset[RandomGenerator.Bytes(1)[0] % TempPasswordCharset.Length];
        return new string(chars);
    }

    private static async Task WriteAsync(HttpContext ctx, int status, int code, string message, object? data = null)
    {
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(new { code, message, data });
    }
}

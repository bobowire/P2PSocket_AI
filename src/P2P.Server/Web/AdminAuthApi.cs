// M3-02 管理员会话与账号（FR-S-203 收口、04 §3.1、07 §8、OQ-3/D16、AI-17）：
// login（admin/admin 默认；mustChangePassword=仍是默认口令即 true——提示不强制阻断）/
// change-password（独立于 0x23 的 Web 载体实现：admin 行直接改库+审计）/
// logout（会话即删）。登录成功/失败审计 admin_login/admin_login_fail；同 IP 连续失败
// 递增延迟防爆破（成功即复位——限速挡的是爆破不是管理员本人）。
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using P2P.Core.Crypto;
using P2P.Server.Data;
using P2P.Server.Services;

namespace P2P.Server.Web;

/// <summary>管理端认证端点组（挂在 ServerWebHost 装配的 WebApplication 上）。</summary>
public sealed class AdminAuthApi(
    IDbContextFactory<AppDbContext> dbFactory,
    AuditLogger audit)
{
    /// <summary>同 IP 连续失败递增延迟阶梯（0 次不延迟；封顶 3s——挡爆破不断服务）。</summary>
    private static readonly TimeSpan[] FailureDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(3),
    ];

    // 实例态（DI 单例）：跨请求保持同 IP 失败计数；静态字段会跨测试夹具泄漏
    private readonly ConcurrentDictionary<string, int> _failures = new();

    public void Map(WebApplication app, AdminSessionStore sessions)
    {
        app.MapPost("/api/auth/login", async ctx =>
        {
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            LoginRequest? req;
            try { req = await ctx.Request.ReadFromJsonAsync<LoginRequest>(ctx.RequestAborted); }
            catch (JsonException) { req = null; }
            if (req is null || string.IsNullOrEmpty(req.Username) || string.IsNullOrEmpty(req.Password))
            {
                await WriteAsync(ctx, StatusCodes.Status400BadRequest, 1001, "参数错误");
                return;
            }

            // 限速先于校验（爆破者无法跳过）；正常路径 0 次失败不延迟
            var fails = _failures.TryGetValue(ip, out var f) ? f : 0;
            if (fails > 0)
                await Task.Delay(FailureDelays[Math.Min(fails, FailureDelays.Length - 1)], ctx.RequestAborted);

            await using var db = await dbFactory.CreateDbContextAsync(ctx.RequestAborted);
            var admin = await db.Users.SingleOrDefaultAsync(
                u => u.Username == DbInitializer.AdminUsername && u.IsAdmin, ctx.RequestAborted);
            if (admin is null || !PasswordHasher.Verify(req.Password, admin.PasswordHash))
            {
                _failures.AddOrUpdate(ip, 1, static (_, c) => c + 1);
                await audit.WriteAsync("admin_login_fail", userId: admin?.Id,
                    detail: new { ip, username = req.Username }, ct: ctx.RequestAborted); // AI-17：无凭据材料
                await WriteAsync(ctx, StatusCodes.Status401Unauthorized, 2001, "用户名或密码错误");
                return;
            }

            _failures.TryRemove(ip, out _);
            var sessionId = sessions.Create();
            ctx.Response.Cookies.Append(AdminSessionStore.CookieName, sessionId, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = false, // HTTP（D11 仅本机监听）
                Path = "/",
            });
            await audit.WriteAsync("admin_login", userId: admin.Id,
                detail: new { ip }, ct: ctx.RequestAborted);
            // 首登判定（编制小口径）：仍是默认口令即提示改密（不阻断）
            var mustChange = PasswordHasher.Verify(DbInitializer.AdminUsername, admin.PasswordHash);
            await WriteAsync(ctx, StatusCodes.Status200OK, 0, "ok", new { mustChangePassword = mustChange });
        });

        app.MapPost("/api/auth/change-password", async ctx =>
        {
            ChangePasswordRequest? req;
            try { req = await ctx.Request.ReadFromJsonAsync<ChangePasswordRequest>(ctx.RequestAborted); }
            catch (JsonException) { req = null; }
            if (req is null || string.IsNullOrEmpty(req.OldPassword) || string.IsNullOrEmpty(req.NewPassword))
            {
                await WriteAsync(ctx, StatusCodes.Status400BadRequest, 1001, "参数错误");
                return;
            }
            if (req.NewPassword.Length < 6) // 与 0x23 同口径（04 §2.3）
            {
                await WriteAsync(ctx, StatusCodes.Status400BadRequest, 1001, "新密码长度至少 6 位");
                return;
            }

            await using var db = await dbFactory.CreateDbContextAsync(ctx.RequestAborted);
            var admin = await db.Users.SingleOrDefaultAsync(
                u => u.Username == DbInitializer.AdminUsername && u.IsAdmin, ctx.RequestAborted);
            if (admin is null || !PasswordHasher.Verify(req.OldPassword, admin.PasswordHash))
            {
                await WriteAsync(ctx, StatusCodes.Status401Unauthorized, 2001, "当前密码不正确");
                return;
            }

            admin.PasswordHash = PasswordHasher.Hash(req.NewPassword);
            admin.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ctx.RequestAborted);
            await audit.WriteAsync("admin_change_password", userId: admin.Id, ct: ctx.RequestAborted);
            await WriteAsync(ctx, StatusCodes.Status200OK, 0, "ok");
        });

        app.MapPost("/api/auth/logout", async ctx =>
        {
            if (ctx.Request.Cookies.TryGetValue(AdminSessionStore.CookieName, out var sid))
                sessions.Remove(sid);
            ctx.Response.Cookies.Delete(AdminSessionStore.CookieName, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Path = "/",
            });
            await WriteAsync(ctx, StatusCodes.Status200OK, 0, "ok");
        });
    }

    private static async Task WriteAsync(HttpContext ctx, int status, int code, string message, object? data = null)
    {
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(new { code, message, data });
    }

    private sealed record LoginRequest(string Username, string Password);

    private sealed record ChangePasswordRequest(string OldPassword, string NewPassword);
}

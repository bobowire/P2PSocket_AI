// M3-07 中继管理 API（FR-S-824/703、04 §3.2、TD-23）：会话表快照 + 全局开关/限速配置。
// PUT 即时生效双路径：relay_enabled 每次 0x74 现读（写库即生效，存量会话不受杀）；
// relay_rate_limit 进程内直调 RelayRateLimiter.UpdateRate（新分配按余量闸 5002，
// 存量转发欠账等待=仅降速不中断）。sid 以字符串承载（u64 超 JS Number 53 位精度）。
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using P2P.Server.Data;
using P2P.Server.Services;

namespace P2P.Server.Web;

/// <summary>中继管理端点组（挂 ServerWebHost 装配的 WebApplication，认证中间件之后）。
/// relay/limiter 缺省（裸 API 测试形态）：sessions 空表、限速变更不生效（键值仍落库）。</summary>
public sealed class AdminRelayApi(
    IDbContextFactory<AppDbContext> dbFactory,
    RelayService? relay,
    RelayRateLimiter? limiter,
    AuditLogger audit)
{
    private sealed record ConfigRequest(bool? RelayEnabled, long? RateLimitBytes);

    public void Map(WebApplication app)
    {
        app.MapGet("/api/relay/sessions", async ctx =>
        {
            var sessions = relay?.ListSessions() ?? [];
            await WriteAsync(ctx, StatusCodes.Status200OK, 0, "ok",
                new RelaySessionsView(sessions.Select(s => new RelaySessionView(
                    s.RelaySessionId.ToString(), // u64 → 字符串（JS 精度边界）
                    s.PunchSessionId, End(s.A), End(s.B),
                    s.BytesForwarded, s.CreatedAt, s.LastActivity)).ToList()));
        });

        app.MapGet("/api/relay/config", async ctx =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ctx.RequestAborted);
            var cfg = new ServerConfigStore(db);
            await WriteAsync(ctx, StatusCodes.Status200OK, 0, "ok", ReadConfigAsync(cfg));
        });

        app.MapPut("/api/relay/config", async ctx =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<ConfigRequest>(ctx.RequestAborted);
            if (body is null || (body.RelayEnabled is null && body.RateLimitBytes is null))
            {
                await WriteAsync(ctx, StatusCodes.Status400BadRequest, 1001,
                    "参数错误（relayEnabled/rateLimitBytes 至少提供一项）");
                return;
            }
            if (body.RateLimitBytes is < 0 or > int.MaxValue)
            {
                await WriteAsync(ctx, StatusCodes.Status400BadRequest, 1001,
                    "参数错误（rateLimitBytes 须为 0~2147483647，0=不限）");
                return;
            }

            await using var db = await dbFactory.CreateDbContextAsync(ctx.RequestAborted);
            if (body.RelayEnabled is { } enabled)
                await db.ServerConfig.Where(c => c.Key == "relay_enabled")
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.Value, enabled ? "1" : "0"),
                        ctx.RequestAborted);
            if (body.RateLimitBytes is { } rate)
            {
                await db.ServerConfig.Where(c => c.Key == "relay_rate_limit")
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.Value, rate.ToString()),
                        ctx.RequestAborted);
                limiter?.UpdateRate(rate); // 进程内直调即时生效（沿 AdminService 先例）
            }
            await audit.WriteAsync("relay_config_change", detail: new
            {
                relayEnabled = body.RelayEnabled,
                rateLimitBytes = body.RateLimitBytes,
            }, ct: ctx.RequestAborted);
            var cfg = new ServerConfigStore(db);
            await WriteAsync(ctx, StatusCodes.Status200OK, 0, "ok", ReadConfigAsync(cfg));
        });
    }

    private static RelayConfigView ReadConfigAsync(ServerConfigStore cfg)
        => new(cfg.GetBool("relay_enabled"), cfg.GetInt("relay_rate_limit"));

    /// <summary>端点承载表达：tcp 优先（与转发偏好同口径），次 udp 已学地址，未 JOIN=pending。</summary>
    private static RelayEndView End(RelayEndpointView e) => new(
        e.DeviceId, e.ControlIp, e.UdpAddr,
        e.TcpConnected ? "tcp" : e.UdpAddr is not null ? "udp" : "pending");

    private static async Task WriteAsync(HttpContext ctx, int status, int code, string message, object? data = null)
    {
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(new { code, message, data });
    }
}

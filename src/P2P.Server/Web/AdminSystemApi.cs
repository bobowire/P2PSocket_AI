// M3-08 系统配置 API（FR-S-825、04 §3.2、08 §5.1 分工）：GET/PUT /api/system/config——
// server_config 键值读写，白名单=ConfigDefaults 全集（listen.* 进程级在 appsettings 不入此端）；
// 值校验（log_level 枚举/数值范围/virtual_subnet CIDR/public_addr IP 或域名/free|approval）；
// 改动重启生效语义以 restartRequired 标注（启动期一次性读取的键 true；运行期现读的键即时生效）；
// relay_rate_limit 特例=进程内直调 RelayRateLimiter.UpdateRate 即时生效（与 M3-07 PUT
// /api/relay/config 同执行链——两条写路径共享限速热路径，避免库值与进程桶漂移）；审计
// system_config_change（AI-17：config 值非敏感材料，detail 如实记录变更键值）。
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using P2P.Server.Data;
using P2P.Server.Services;

namespace P2P.Server.Web;

/// <summary>系统配置端点组（挂 ServerWebHost 装配的 WebApplication，认证中间件之后）。</summary>
public sealed class AdminSystemApi(
    IDbContextFactory<AppDbContext> dbFactory,
    RelayRateLimiter? limiter,
    AuditLogger audit)
{
    /// <summary>键→（校验器， 错误提示）。白名单即此表键集（=ConfigDefaults 全集，03 §2.8）。</summary>
    private static readonly Dictionary<string, (Func<string, bool> Valid, string Reason)> Validators = new()
    {
        ["registration_open"] = (v => v is "0" or "1", "须为 0|1"),
        ["relay_enabled"] = (v => v is "0" or "1", "须为 0|1"),
        ["relay_rate_limit"] = (v => long.TryParse(v, out var n) && n is >= 0 and <= int.MaxValue,
            "须为 0~2147483647（字节/秒，0=不限）"),
        ["public_addr"] = (IsValidHost, "须为空（本地侧派生）或 IP/域名"),
        ["stun_auth"] = (v => v is "0" or "1", "须为 0|1"),
        ["virtual_subnet"] = (v => LanSegmentService.NormalizeCidr(v) is not null,
            "须为合法 CIDR（如 100.64.0.0/24）"),
        ["audit_retention_days"] = (v => int.TryParse(v, out var n) && n is >= 1 and <= 3650, "须为 1~3650（天）"),
        ["log_level"] = (v => LogLevelCanonical(v) is not null,
            "须为 Verbose|Debug|Information|Warning|Error|Fatal"),
        ["max_devices"] = (v => int.TryParse(v, out var n) && n is >= 1 and <= 100000, "须为 1~100000"),
        ["punch_retention_days"] = (v => int.TryParse(v, out var n) && n is >= 1 and <= 3650, "须为 1~3650（天）"),
        ["stun_rate_per_ip"] = (v => int.TryParse(v, out var n) && n is >= 1 and <= 1000000, "须为 1~1000000（pps）"),
        ["stun_rate_per_device"] = (v => int.TryParse(v, out var n) && n is >= 1 and <= 100000, "须为 1~100000（QPS）"),
        ["stun_circuit_pps"] = (v => int.TryParse(v, out var n) && n is >= 1 and <= 10000000, "须为 1~10000000（pps）"),
        ["default_join_policy"] = (v => v is "free" or "approval", "须为 free|approval"),
        ["update_latest_version"] = (v => v.Trim().Length is >= 1 and <= 32, "须为非空版本号（≤32 字符）"),
        ["update_min_protocol"] = (v => int.TryParse(v, out var n) && n is >= 1 and <= 9999, "须为 1~9999"),
        ["update_url"] = (v => v.Length == 0
            || (v.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || v.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) && v.Length <= 500,
            "须为空或 http(s):// 前缀地址（≤500 字符）"),
        ["update_notes"] = (v => v.Length <= 500, "须 ≤500 字符"),
    };

    /// <summary>启动期一次性读取的键（restartRequired=true）：public_addr/stun_* 在服务构造时读
    /// （M2-36/M2-06），log_level 在 Serilog 装配时读——改动须重启方生效；其余键运行期现读即时生效
    /// （registration_open/max_devices/default_join_policy/update_*/保留天数每轮现读）。</summary>
    private static readonly HashSet<string> RestartKeys =
        ["public_addr", "stun_auth", "stun_rate_per_ip", "stun_rate_per_device", "stun_circuit_pps", "log_level"];

    public void Map(WebApplication app)
    {
        app.MapGet("/api/system/config", async ctx =>
        {
            await WriteAsync(ctx, StatusCodes.Status200OK, 0, "ok",
                new { items = await ReadConfigAsync(ctx.RequestAborted) });
        });

        app.MapPut("/api/system/config", async ctx =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>(ctx.RequestAborted);
            if (body is null || body.Count == 0)
            {
                await WriteAsync(ctx, StatusCodes.Status400BadRequest, 1001, "参数错误（须提供至少一个配置键）");
                return;
            }
            foreach (var (key, value) in body)
            {
                if (!Validators.TryGetValue(key, out var rule))
                {
                    await WriteAsync(ctx, StatusCodes.Status400BadRequest, 1001,
                        $"参数错误（{key}：不在配置白名单内）");
                    return;
                }
                if (!rule.Valid(value))
                {
                    await WriteAsync(ctx, StatusCodes.Status400BadRequest, 1001,
                        $"参数错误（{key}：{rule.Reason}）");
                    return;
                }
            }

            // 全量校验通过后落库（log_level 存规范形态，与 Serilog ignoreCase 解析对齐）
            await using var db = await dbFactory.CreateDbContextAsync(ctx.RequestAborted);
            var keys = body.Keys.ToList();
            var rows = await db.ServerConfig.Where(c => keys.Contains(c.Key)).ToListAsync(ctx.RequestAborted);
            foreach (var row in rows)
                row.Value = row.Key == "log_level"
                    ? LogLevelCanonical(body[row.Key])! : body[row.Key];
            await db.SaveChangesAsync(ctx.RequestAborted);

            if (body.TryGetValue("relay_rate_limit", out var rate)
                && long.TryParse(rate, out var bytesPerSec))
                limiter?.UpdateRate(bytesPerSec); // M3-07 同执行链：限速键即时驱动进程桶

            await audit.WriteAsync("system_config_change", detail: new
            {
                keys = body.Select(kv => new { key = kv.Key, value = kv.Value }),
            }, ct: ctx.RequestAborted);

            await WriteAsync(ctx, StatusCodes.Status200OK, 0, "ok",
                new { items = await ReadConfigAsync(ctx.RequestAborted) });
        });
    }

    /// <summary>白名单全集现值（缺键回退 ConfigDefaults 种子值——理论不可达，Seed 幂等补齐）。</summary>
    private async Task<List<object>> ReadConfigAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var current = await db.ServerConfig.AsNoTracking().ToDictionaryAsync(c => c.Key, c => c.Value, ct);
        return [.. DbInitializer.ConfigDefaults
            .Select(seed => (object)new
            {
                key = seed.Key,
                value = current.TryGetValue(seed.Key, out var v) ? v : seed.Value,
                restartRequired = RestartKeys.Contains(seed.Key),
            })];
    }

    private static async Task WriteAsync(HttpContext ctx, int status, int code, string message, object? data = null)
    {
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(new { code, message, data });
    }

    /// <summary>Serilog LogEventLevel 规范形态（大小写不敏感匹配；未匹配 null）。</summary>
    private static string? LogLevelCanonical(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "verbose" => "Verbose",
            "debug" => "Debug",
            "information" => "Information",
            "warning" => "Warning",
            "error" => "Error",
            "fatal" => "Fatal",
            _ => null,
        };

    /// <summary>通告地址合法性：空（=派生）｜IP｜域名（字母数字点横线，≤253）。</summary>
    private static bool IsValidHost(string value)
        => value.Length == 0
           || IPAddress.TryParse(value, out _)
           || (value.Length <= 253 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'));
    }

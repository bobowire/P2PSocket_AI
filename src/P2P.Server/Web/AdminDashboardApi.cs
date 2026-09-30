// M3-06 仪表盘 API（FR-S-810、04 §3.2、05 §7.1/§6）：单端点 GET /api/dashboard 聚合六指标组——
// ① 在线设备数（DeviceRegistry 连接级真相源）② 分组数（库行计数）③ 活跃映射数与最后已知状态分布
// （enabled 计数 + TD-22 口径投影：0x62 mapping_status 审计流水每映射最新一条 direct/relay/failed/invalid，
// 无流水=unknown——客户端状态机是真相源，服务端仅流水投影）④ 中继总流量（RelayService.Stats 进程内
// 累计+自启动时长）⑤ STUN 到达吞吐与丢弃三分桶（StunGuard.Metrics：rate/auth/circuit——
// stun_dropped_total，05 §7.1）⑥ 打洞近 24h 成功率（punch_stats：direct+relay/total）+按小时桶时序
// （成功率折线数据源）。数据源均为只读快照，无副作用无审计。
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using P2P.Server.Data;
using P2P.Server.Services;

namespace P2P.Server.Web;

/// <summary>仪表盘端点组（挂 ServerWebHost 装配的 WebApplication，认证中间件之后）。
/// relay/stun 为进程内单例直读（可为 null——测试宿主/裸 API 形态下该节呈零值快照）。</summary>
public sealed class AdminDashboardApi(
    IDbContextFactory<AppDbContext> dbFactory,
    DeviceRegistry registry,
    RelayService? relay,
    StunService? stun)
{
    public void Map(WebApplication app)
    {
        app.MapGet("/api/dashboard", async ctx =>
        {
            var now = DateTime.UtcNow;
            var since24h = now.AddHours(-24);

            await using var db = await dbFactory.CreateDbContextAsync(ctx.RequestAborted);
            var groupCount = await db.Groups.AsNoTracking()
                .CountAsync(ctx.RequestAborted);
            var mappings = await db.Mappings.AsNoTracking()
                .Select(m => new { m.Id, m.Enabled })
                .ToListAsync(ctx.RequestAborted);
            var byStatus = await LatestMappingStatusAsync(db, mappings.Where(m => m.Enabled)
                .Select(m => m.Id).ToHashSet(), ctx.RequestAborted);
            var punches = (await db.PunchStats.AsNoTracking()
                    .Where(p => p.Ts >= since24h)
                    .Select(p => new { p.Ts, p.Result })
                    .ToListAsync(ctx.RequestAborted))
                .Select(p => (p.Ts, p.Result))
                .ToList();

            var direct = punches.Count(p => p.Result == "direct");
            var relayed = punches.Count(p => p.Result == "relay");
            var total = punches.Count;
            var buckets = HourlyBuckets(punches, now);

            var relayStats = relay?.Stats;
            var stunStats = stun?.Guard.Metrics();

            ctx.Response.StatusCode = StatusCodes.Status200OK;
            await ctx.Response.WriteAsJsonAsync(new
            {
                code = 0,
                message = "ok",
                data = new DashboardView(
                    OnlineDevices: registry.OnlineDeviceIds.Count,
                    Groups: groupCount,
                    Mappings: new MappingsSummaryView(
                        Total: mappings.Count,
                        Enabled: mappings.Count(m => m.Enabled),
                        ByStatus: byStatus), // TD-22 口径：enabled 映射的最后已知状态分布
                    Relay: new RelaySnapshotView(
                        Sessions: relayStats?.Sessions ?? 0,
                        BytesForwarded: relayStats?.BytesForwarded ?? 0,
                        Reaped: relayStats?.Reaped ?? 0,
                        StartedAt: relayStats?.StartedAtUtc,
                        UptimeSec: relayStats is null ? 0
                            : Math.Max(0, (now - relayStats.StartedAtUtc).TotalSeconds)),
                    Stun: new StunSnapshotView(
                        Admitted: stunStats?.Admitted ?? 0, // 闸④ 放行到达累计（平均 QPS 分子）
                        Qps: stunStats is null || stunStats.UptimeSec <= 0 ? 0
                            : stunStats.Admitted / stunStats.UptimeSec,
                        Dropped: new StunDroppedView(
                            Rate: stunStats?.Rate ?? 0,
                            Auth: stunStats?.Auth ?? 0,
                            Circuit: stunStats?.Circuit ?? 0),
                        UptimeSec: stunStats?.UptimeSec ?? 0),
                    Punch: new PunchStatsView(
                        Total24h: total,
                        Direct24h: direct,
                        Relay24h: relayed,
                        Failed24h: total - direct - relayed,
                        SuccessRate24h: total == 0 ? null : (direct + relayed) / (double)total,
                        Hourly: buckets)), // 24 桶（最旧→最新，整点对齐）：折线数据源
            }, ctx.RequestAborted);
        });
    }

    /// <summary>TD-22 口径投影：mapping_status 审计流水（detail JSON 含 MappingId/State）按映射取
    /// 最新一条的 State。行按 Id 升序扫描、字典覆盖取末次（Id 自增=写入序）。0x62 仅在状态迁移时
    /// 写行（M2-22），量级=状态变更数且受 90 天保留约束；M3-08 /api/mappings 同口径复用
    /// （LatestStatusByMappingAsync 共享核），若量大再考虑 json_extract 下推（此处先全扫内存投影
    /// ——管理面低频轮询可承受）。</summary>
    private static async Task<Dictionary<string, int>> LatestMappingStatusAsync(AppDbContext db,
        HashSet<Guid> enabledIds, CancellationToken ct)
    {
        var latest = await LatestStatusByMappingAsync(db, enabledIds, ct);

        var byStatus = new Dictionary<string, int>
        {
            ["direct"] = 0, ["relay"] = 0, ["failed"] = 0, ["invalid"] = 0, ["unknown"] = 0,
        };
        foreach (var id in enabledIds)
            byStatus[latest.TryGetValue(id, out var s) && byStatus.ContainsKey(s) ? s : "unknown"]++;
        return byStatus;
    }

    /// <summary>TD-22 投影共享核（M3-06 仪表盘分布与 M3-08 /api/mappings 列表同口径）：
    /// 返回 每映射→最后已知 State；无流水/坏行映射缺席（调用方以 unknown 兜底）。</summary>
    internal static async Task<Dictionary<Guid, string>> LatestStatusByMappingAsync(AppDbContext db,
        HashSet<Guid> mappingIds, CancellationToken ct)
    {
        var latest = new Dictionary<Guid, string>();
        if (mappingIds.Count == 0) return latest;
        var rows = await db.AuditLogs.AsNoTracking()
            .Where(a => a.Event == "mapping_status")
            .OrderBy(a => a.Id)
            .Select(a => a.Detail)
            .ToListAsync(ct);
        foreach (var detail in rows)
        {
            if (detail is null) continue;
            try
            {
                using var doc = JsonDocument.Parse(detail);
                var mappingId = doc.RootElement.TryGetProperty("MappingId", out var mid)
                    && Guid.TryParse(mid.GetString(), out var id) ? id : Guid.Empty;
                if (mappingId != Guid.Empty && mappingIds.Contains(mappingId)
                    && doc.RootElement.TryGetProperty("State", out var st))
                    latest[mappingId] = st.GetString() ?? "";
            }
            catch (JsonException)
            {
                // 理论不可达（本服务序列化写入）：坏行跳过不致整个端点 500
            }
        }
        return latest;
    }

    /// <summary>近 24h 按小时桶（整点对齐、最旧→最新共 24 桶）：每桶 total/success（direct+relay）计数。
    /// 折线数据源——空桶也保留（前端零点连线）。</summary>
    private static List<HourlyBucketView> HourlyBuckets(List<(DateTime Ts, string Result)> punches, DateTime now)
    {
        var currentHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
        var index = new Dictionary<DateTime, (int Total, int Success)>();
        for (var h = 0; h < 24; h++) index[currentHour.AddHours(-h)] = (0, 0);
        foreach (var (ts, result) in punches)
        {
            var hour = new DateTime(ts.Year, ts.Month, ts.Day, ts.Hour, 0, 0, DateTimeKind.Utc);
            if (!index.ContainsKey(hour)) continue; // 时钟边界外行（理论不可达——查询窗即 24h）
            var (total, success) = index[hour];
            index[hour] = (total + 1, success + (result is "direct" or "relay" ? 1 : 0));
        }
        return [.. index.OrderBy(kv => kv.Key) // 最旧→最新（时间升序）
            .Select(kv => new HourlyBucketView(
                HourStart: new DateTimeOffset(kv.Key).ToUnixTimeMilliseconds(),
                Total: kv.Value.Total,
                Success: kv.Value.Success))];
    }
}

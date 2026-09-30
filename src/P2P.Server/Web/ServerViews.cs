// M3-09 服务端 Web 展示 DTO（06 §5 类型同源、编制定案③）：服务端 API 响应载荷的具名形态——
// ExportTs 反射输出 api-server.d.ts（前端禁止手写类型/路径字符串）；属性名与序列化形状
// 自匿名对象逐字段等价迁移（camelCase 序列化不变，既有 JSON 断言零影响）。
// 无机密字段（AI-17 同口径）：仅统计快照与登录提示标志。

namespace P2P.Server.Web;

/// <summary>管理员登录结果（POST /api/auth/login）：mustChangePassword=仍是默认口令（提示不阻断）。</summary>
public sealed record LoginResult(bool MustChangePassword);

/// <summary>仪表盘聚合（GET /api/dashboard，FR-S-810 六指标组）。</summary>
public sealed record DashboardView(
    int OnlineDevices,
    int Groups,
    MappingsSummaryView Mappings,
    RelaySnapshotView Relay,
    StunSnapshotView Stun,
    PunchStatsView Punch);

/// <summary>映射概览：enabled 计数 + TD-22 口径状态分布（direct/relay/failed/invalid/unknown 五桶）。</summary>
public sealed record MappingsSummaryView(int Total, int Enabled, Dictionary<string, int> ByStatus);

/// <summary>中继快照：进程内累计（RelayService.Stats；裸 API 形态呈零值）。</summary>
public sealed record RelaySnapshotView(
    int Sessions,
    long BytesForwarded,
    long Reaped,
    DateTime? StartedAt,
    double UptimeSec);

/// <summary>STUN 快照：闸④放行累计与丢弃三分桶（StunGuard.Metrics；05 §7.1）。</summary>
public sealed record StunSnapshotView(long Admitted, double Qps, StunDroppedView Dropped, double UptimeSec);

/// <summary>丢弃三分桶（stun_dropped_total：限速/认证/断路器）。</summary>
public sealed record StunDroppedView(long Rate, long Auth, long Circuit);

/// <summary>打洞近 24h 统计与按小时桶时序（成功率折线数据源；无数据 successRate=JSON null）。</summary>
public sealed record PunchStatsView(
    int Total24h,
    int Direct24h,
    int Relay24h,
    int Failed24h,
    double? SuccessRate24h,
    List<HourlyBucketView> Hourly);

/// <summary>整点对齐小时桶（最旧→最新共 24 桶，空桶保留；hourStart=Unix ms）。</summary>
public sealed record HourlyBucketView(long HourStart, int Total, int Success);

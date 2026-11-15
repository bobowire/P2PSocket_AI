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
    PunchStatsView Punch,
    ServerVersionView Version);

/// <summary>服务端宿主版本（M4-01：Directory.Build.props 注入，仪表盘角标展示）。</summary>
public sealed record ServerVersionView(string Version, string Commit, string BuildDate);

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

/// <summary>打洞近 24h 统计与按小时桶时序（成功率折线数据源；无数据 successRate=JSON null）。
/// kcpDirect24h/rawDirect24h（M6-08，NET-77）：直连按承载分组（bearer 列 1/0；null 不计）——
/// L2 KCP 采用率与包络对照的可见面。</summary>
public sealed record PunchStatsView(
    int Total24h,
    int Direct24h,
    int Relay24h,
    int Failed24h,
    double? SuccessRate24h,
    List<HourlyBucketView> Hourly,
    int KcpDirect24h = 0,
    int RawDirect24h = 0);

/// <summary>整点对齐小时桶（最旧→最新共 24 桶，空桶保留；hourStart=Unix ms）。</summary>
public sealed record HourlyBucketView(long HourStart, int Total, int Success);

// ── M3-10 管理页载荷（FR-S-820/821/822/823；形状自各 Admin*Api 匿名对象逐字段迁移）──

/// <summary>用户列表项（GET /api/users）：deviceCount=名下设备数子查询。</summary>
public sealed record UserView(
    Guid Id, string Username, bool Disabled, bool IsAdmin, int DeviceCount, DateTime CreatedAt);

/// <summary>用户分页列表（GET /api/users）。</summary>
public sealed record UserListView(List<UserView> Items, int Total, int Page, int PageSize);

/// <summary>密码重置结果（PUT /api/users/{id}/password-reset）：临时密码仅本次响应返回一次。</summary>
public sealed record TempPasswordResult(string TempPassword);

/// <summary>设备列表项（GET /api/devices）：online=DeviceRegistry 连接级真相源（与 Disabled 正交）。</summary>
public sealed record DeviceView(
    Guid DeviceId, string DeviceName, string Os, string RemoteCode, string? VirtualIp,
    string? OwnerUsername, string[] Groups, bool Online, bool Disabled, DateTime CreatedAt);

/// <summary>设备全量列表（GET /api/devices，无分页）。</summary>
public sealed record DeviceListView(List<DeviceView> Items);

/// <summary>远程码重置结果（POST /api/devices/{id}/reset-remote-code）：新码仅本次响应返回一次。</summary>
public sealed record RemoteCodeResult(string RemoteCode);

/// <summary>分组总览项（GET /api/groups）：memberCount=approved 成员；pendingCount=待审批单。</summary>
public sealed record GroupView(
    Guid GroupId, string Name, string JoinPolicy, bool IsDefault, string? OwnerUsername,
    int MemberCount, int PendingCount, DateTime CreatedAt);

/// <summary>分组总览列表（默认分组置顶排序）。</summary>
public sealed record GroupListView(List<GroupView> Items);

/// <summary>默认分组策略变更结果（PUT /api/groups/default，回读新值）。</summary>
public sealed record PolicyResult(string Policy);

/// <summary>跨分组待审批申请单（GET /api/group-requests?status=pending）。</summary>
public sealed record GroupRequestView(
    Guid RequestId, Guid GroupId, string GroupName, Guid DeviceId, string DeviceName,
    string OwnerUsername, DateTime CreatedAt);

/// <summary>审批队列（GET /api/group-requests）。</summary>
public sealed record GroupRequestListView(List<GroupRequestView> Items);

/// <summary>审批动作结果（approve/reject）：已处理单 {ok:false} 诚实应答（0x53 Ack Ok=false 同口径）。</summary>
public sealed record DecisionResult(bool Ok);

/// <summary>映射累计流量三向（mapping_stats join）。</summary>
public sealed record MappingBytesView(long Up, long Down, long Relay);

/// <summary>映射明细（GET /api/mappings）：status=TD-22 投影（无流水=unknown；disabled 仍投影=明细口径）。</summary>
public sealed record MappingView(
    Guid Id, string Name, int LocalPort, string Proto, Guid TargetDeviceId, string TargetAddr,
    int TargetPort, bool Enabled, DateTime CreatedAt, Guid OwnerDeviceId, string OwnerDeviceName,
    string OwnerRemoteCode, string TargetDeviceName, MappingBytesView Bytes,
    DateTime? StatsUpdatedAt, string Status);

/// <summary>映射列表（GET /api/mappings，过滤后 total=items.Count）。</summary>
public sealed record MappingListView(List<MappingView> Items, int Total);

/// <summary>server_config 键值项（GET/PUT /api/system/config）：restartRequired=启动期读取键（改动须重启）。</summary>
public sealed record ConfigItemView(string Key, string Value, bool RestartRequired);

/// <summary>配置键值集合（白名单=ConfigDefaults 全集现值）。</summary>
public sealed record ConfigListView(List<ConfigItemView> Items);

/// <summary>中继配置现值（GET/PUT /api/relay/config；PUT 响应=回读）。</summary>
public sealed record RelayConfigView(bool RelayEnabled, int RateLimitBytes);

/// <summary>中继会话端点（carrier=tcp 优先/次 udp 已学地址/未 JOIN=pending，与转发偏好同口径）。</summary>
public sealed record RelayEndView(Guid DeviceId, string ControlIp, string? UdpAddr, string Carrier);

/// <summary>中继会话行（GET /api/relay/sessions）：sid 以字符串承载（u64 超 JS Number 53 位精度）。
/// 时间戳=RelayService 现值 DateTimeOffset（与仪表盘 RelaySnapshotView 同源）。</summary>
public sealed record RelaySessionView(
    string Sid, Guid PunchSessionId, RelayEndView A, RelayEndView B,
    long BytesForwarded, DateTimeOffset CreatedAt, DateTimeOffset LastActivity);

/// <summary>中继会话表快照（GET /api/relay/sessions）。</summary>
public sealed record RelaySessionsView(List<RelaySessionView> Sessions);

/// <summary>审计日志行（GET /api/audit-logs）：detail 为原始 JSON 文本透传（SEC-51 写入侧已保证不含凭据）。</summary>
public sealed record AuditLogView(long Id, DateTime Ts, string Event, Guid? DeviceId, Guid? UserId, string? Detail);

/// <summary>审计日志列表（newest-first + event 过滤 + 分页）。</summary>
public sealed record AuditLogListView(List<AuditLogView> Items, int Total, int Page, int PageSize);

// ── M5-09 安全中心载荷（SEC-72、FR-S-827；07 §13.2~§13.4）──

/// <summary>时间窗计数（GET /api/security/overview；07 §13.2 三族聚合：登录失败/新注册/授权拒绝）。
/// 今日=UTC 日界、近 7 天=滚动 168h，血缘=audit_logs 时间窗查询（今日集为近 7 天子集）。</summary>
public sealed record SecurityCountView(int LoginFail, int Register, int Deny);

/// <summary>STUN 丢弃三分桶（07 §13.2）——StunGuard 进程累计无时间窗事件源，呈自启动累计；
/// since=启动时刻（uptime 反推，UTC；裸 API 形态 null）。</summary>
public sealed record SecurityStunView(long Rate, long Auth, long Circuit, DateTime? Since);

/// <summary>自动封禁规则现值（07 §13.3 四键 tick 现读回显；改键经 /system 配置页）。</summary>
public sealed record AutobanRuleView(bool Enabled, int WindowSec, int FailCount, int DurationSec);

/// <summary>安全中心聚合（GET /api/security/overview）：hits=黑名单四面命中计数（内存态重启清零）。
/// M5-13 尾参 encryption=静态数据加密状态（SEC-83、07 §14.3）。</summary>
public sealed record SecurityOverviewView(
    SecurityCountView Today, SecurityCountView Week, SecurityStunView Stun,
    List<Services.BlacklistEntry> Blacklist, Dictionary<string, long> Hits,
    AutobanRuleView Autoban, bool RegistrationOpen, EncryptionView Encryption);

/// <summary>静态数据加密状态（M5-13、SEC-83、07 §14.3）：enabled=masterKey 在位（device_secret
/// 列加密生效+存量迁移已执行）；decryptFailures=进程内解密失败计数（不可解行凭据失效口径
/// OQ-22②；重启清零）。</summary>
public sealed record EncryptionView(bool Enabled, int DecryptFailures);

/// <summary>踢全部会话结果（POST /api/security/lockdown，07 §13.4）：kicked=断开的在线控制会话数。</summary>
public sealed record LockdownResultView(int Kicked);

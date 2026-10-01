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

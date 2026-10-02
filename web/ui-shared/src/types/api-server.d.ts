// 本文件由 export-ts 反射生成（06 §5、编制定案③），禁止手改。
// 源：P2P.Server Web 展示 DTO（ServerViews，src/Tools/ExportTs @ 2026-10-02T07:28:41Z）。

/** 服务端响应包裹（04 §3：code=0 成功；!=0 见错误码表 04 §5）。注意字段名 message（客户端为 msg）。 */
export interface ServerApiEnvelope<T> {
  code: number;
  message: string;
  data: T;
}

export interface LoginResult {
  mustChangePassword: boolean;
}


export interface DashboardView {
  onlineDevices: number;
  groups: number;
  mappings: MappingsSummaryView;
  relay: RelaySnapshotView;
  stun: StunSnapshotView;
  punch: PunchStatsView;
}


export interface MappingsSummaryView {
  total: number;
  enabled: number;
  byStatus: Record<string, number>;
}


export interface RelaySnapshotView {
  sessions: number;
  bytesForwarded: number;
  reaped: number;
  startedAt: string | null;
  uptimeSec: number;
}


export interface StunSnapshotView {
  admitted: number;
  qps: number;
  dropped: StunDroppedView;
  uptimeSec: number;
}


export interface StunDroppedView {
  rate: number;
  auth: number;
  circuit: number;
}


export interface PunchStatsView {
  total24h: number;
  direct24h: number;
  relay24h: number;
  failed24h: number;
  successRate24h: number | null;
  hourly: HourlyBucketView[];
}


export interface HourlyBucketView {
  hourStart: number;
  total: number;
  success: number;
}


export interface UserView {
  id: string;
  username: string;
  disabled: boolean;
  isAdmin: boolean;
  deviceCount: number;
  createdAt: string;
}


export interface UserListView {
  items: UserView[];
  total: number;
  page: number;
  pageSize: number;
}


export interface TempPasswordResult {
  tempPassword: string;
}


export interface DeviceView {
  deviceId: string;
  deviceName: string;
  os: string;
  remoteCode: string;
  virtualIp: string | null;
  ownerUsername: string | null;
  groups: string[];
  online: boolean;
  disabled: boolean;
  createdAt: string;
}


export interface DeviceListView {
  items: DeviceView[];
}


export interface RemoteCodeResult {
  remoteCode: string;
}


export interface GroupView {
  groupId: string;
  name: string;
  joinPolicy: string;
  isDefault: boolean;
  ownerUsername: string | null;
  memberCount: number;
  pendingCount: number;
  createdAt: string;
}


export interface GroupListView {
  items: GroupView[];
}


export interface PolicyResult {
  policy: string;
}


export interface GroupRequestView {
  requestId: string;
  groupId: string;
  groupName: string;
  deviceId: string;
  deviceName: string;
  ownerUsername: string;
  createdAt: string;
}


export interface GroupRequestListView {
  items: GroupRequestView[];
}


export interface DecisionResult {
  ok: boolean;
}


export interface MappingView {
  id: string;
  name: string;
  localPort: number;
  proto: string;
  targetDeviceId: string;
  targetAddr: string;
  targetPort: number;
  enabled: boolean;
  createdAt: string;
  ownerDeviceId: string;
  ownerDeviceName: string;
  ownerRemoteCode: string;
  targetDeviceName: string;
  bytes: MappingBytesView;
  statsUpdatedAt: string | null;
  status: string;
}


export interface MappingBytesView {
  up: number;
  down: number;
  relay: number;
}


export interface MappingListView {
  items: MappingView[];
  total: number;
}


export interface ConfigItemView {
  key: string;
  value: string;
  restartRequired: boolean;
}


export interface ConfigListView {
  items: ConfigItemView[];
}


export interface RelayConfigView {
  relayEnabled: boolean;
  rateLimitBytes: number;
}


export interface RelayEndView {
  deviceId: string;
  controlIp: string;
  udpAddr: string | null;
  carrier: string;
}


export interface RelaySessionView {
  sid: string;
  punchSessionId: string;
  a: RelayEndView;
  b: RelayEndView;
  bytesForwarded: number;
  createdAt: string;
  lastActivity: string;
}


export interface RelaySessionsView {
  sessions: RelaySessionView[];
}


export interface AuditLogView {
  id: number;
  ts: string;
  event: string;
  deviceId: string | null;
  userId: string | null;
  detail: string | null;
}


export interface AuditLogListView {
  items: AuditLogView[];
  total: number;
  page: number;
  pageSize: number;
}


/** TD-22 映射状态投影域（0x62 流水末次；无流水=unknown——客户端状态机是真相源）。 */
export type MappingStatusProjection = "direct" | "relay" | "failed" | "invalid" | "unknown";

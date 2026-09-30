// 本文件由 export-ts 反射生成（06 §5、08 §2③），禁止手改。
// 源：P2P.Client 展示 DTO + WsEventNames 常量（src/Tools/ExportTs @ 2026-09-30T13:07:54Z）。

/** 响应包裹（04 §0：code=0 成功；!=0 见错误码表 04 §5）。 */
export interface ApiEnvelope<T> {
  code: number;
  msg: string;
  data: T;
}

export interface MappingView {
  mappingId: string;
  name: string;
  localPort: number;
  proto: string;
  targetRemoteCode: string;
  targetAddr: string;
  targetPort: number;
  enabled: boolean;
  state: string;
  detail: string | null;
}


export interface MappingTrafficView {
  mappingId: string;
  bytesUp: number;
  bytesDown: number;
  path: string;
}


export interface RegistrationResult {
  deviceId: string;
  remoteCode: string;
  virtualIp: string;
  groups: GroupInfo[];
}


export interface GroupInfo {
  groupId: string;
  groupName: string;
}


export interface ClientSettings {
  serverAddrs: string[];
  localWebPort: number;
  punchConcurrency: number;
  keepaliveSec: number;
  reconnect: ReconnectSettings;
}


export interface ReconnectSettings {
  minSec: number;
  maxSec: number;
}


export interface UpgradeInfoView {
  latestVersion: string;
  minProtocol: number;
  maxProtocol: number;
  upgradeUrl: string;
  notes: string;
}


export interface GroupView {
  groupId: string;
  groupName: string;
  policy: string;
  isOwner: boolean;
  memberCount: number;
}


export interface GroupRequestView {
  requestId: string;
  groupId: string;
  deviceId: string;
  deviceName: string;
  createdAtMs: number;
}


export interface GroupsView {
  items: GroupView[];
  requests: GroupRequestView[];
}


export interface LanSegmentView {
  segmentId: string;
  cidr: string;
  enabled: boolean;
}


export interface LogEntryView {
  ts: string;
  level: string;
  message: string;
}


export interface LogPageView {
  items: LogEntryView[];
  page: number;
  pageSize: number;
  total: number;
  hasMore: boolean;
}


/** 映射状态机（02 §4.5：disabled→punching→direct/failed；relay/invalid 为 M2 预留）。 */
export type MappingState = "disabled" | "punching" | "direct" | "relay" | "failed" | "invalid";

/** 系统阶段（04 §2.1 /api/system/state.phase）。 */
export type SystemPhase = "unregistered" | "wizard" | "running" | "degraded";

/** 能力模式（02 §2.5：Normal=可主动；Passive=哑节点仅响应）。 */
export type CapabilityMode = "normal" | "passive";

// ── WS 事件（04 §2.8；事件名反射自 WsEventNames 单一事实源）──────
// MappingState = "mapping_state"
// MappingStats = "mapping_stats"
// DeviceList = "device_list"
// LoginState = "login_state"
// UpgradeRequired = "upgrade_required"

/** 高频数值类：直写 store（下秒覆盖，丢失无害，TD-16）。 */
export interface MappingStatsEvent {
  ev: "mapping_stats";
  id: string;
  rateUp: number;
  rateDown: number;
  path: "direct" | "relay";
}

/** 状态类：不直接改 store，仅触发 refetch（TD-16）。 */
export interface MappingStateEvent {
  ev: "mapping_state";
  id: string;
  state: MappingState;
  reason: string;
}

export interface LoginStateEvent {
  ev: "login_state";
  mode: CapabilityMode;
}

/** 设备列表（0x41 属 M2；M1 前端以 10s 轮询 refetch 兜底）。 */
export interface DeviceListEvent {
  ev: "device_list";
}

/** 升级引导（M2-26，FR-C-904：版本拒答/升级信息到达→refetch /api/upgrade/info）。 */
export interface UpgradeRequiredEvent {
  ev: "upgrade_required";
  latestVersion: string;
}

export type WsEvent = MappingStatsEvent | MappingStateEvent | LoginStateEvent | DeviceListEvent | UpgradeRequiredEvent;

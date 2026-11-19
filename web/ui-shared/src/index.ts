// @p2p/ui-shared 入口（06 §1）。全部 UI 资产两 app 共用；类型/路径常量来自 export-ts 生成物。
export { default as StatusTag } from "./components/StatusTag.vue";
export { default as CodeText } from "./components/CodeText.vue";
export { default as DeviceCard } from "./components/DeviceCard.vue";
export type { DeviceCardModel } from "./components/DeviceCard.vue";
export { default as AdminLayout } from "./layouts/AdminLayout.vue";
export type { LayoutMenuItem } from "./layouts/AdminLayout.vue";
export { useApi, createApiClient, ApiError, ERROR_TEXT, apiPath } from "./composables/useApi";
export {
  useWs,
  RESYNC_RESOURCES,
  REFETCH_DEBOUNCE_MS,
  type RefetchResource,
  type WsOptions,
  type WebSocketLike,
} from "./composables/useWs";
// export-ts 生成物（路径常量 + DTO 类型，M1-31；禁止手写字符串路径——06 §5）
// M7-05 补齐渐增 DTO 的漏导出（与 server 侧 M5-09 同因：手维护白名单滞后——
// UpgradeInfoView/LogEntryView/LogPageView/MappingStatsView/DeviceStatsView/StatsSummaryView/
// SubnetConflictItem/SubnetConflictState/KcpBearerStatsSnapshot/SubnetConflictEvent）
export { ApiPaths, ApiEndpoints } from "./types/api-paths";
export type {
  ApiEnvelope,
  MappingView,
  MappingTrafficView,
  RegistrationResult,
  GroupInfo,
  GroupView,
  GroupRequestView,
  ClientSettings,
  ReconnectSettings,
  MappingState,
  SystemPhase,
  CapabilityMode,
  MappingStatsEvent,
  MappingStateEvent,
  LoginStateEvent,
  DeviceListEvent,
  WsEvent,
  StunTestView,
  PingDeviceView,
  ServerTestView,
  ServerTestResultView,
  TunnelView,
  TunnelListView,
  RekeyResultView,
  UpgradeInfoView,
  LogEntryView,
  LogPageView,
  MappingStatsView,
  DeviceStatsView,
  StatsSummaryView,
  SubnetConflictItem,
  SubnetConflictState,
  KcpBearerStatsSnapshot,
  SubnetConflictEvent,
  LanSegmentView,
} from "./types/api";
// M3-09 服务端 Web 生成物（编制定案③：server-app 消费；与客户端分文件防常量名冲突）。
// M5-09 起随 ExportTs 生成物对齐重导出（此前手维护白名单漏 M3-10/11 起渐增的 DTO——
// server-app 消费侧长期以 TS2305 存量形式存在）；GroupView/GroupRequestView/MappingView
// 三名与客户端域同名（ExportTs 侧全限定同因）——上方客户端块先占，此处不入（现状语义不变）
export { ServerApiPaths, ServerApiEndpoints } from "./types/api-server-paths";
export type {
  ServerApiEnvelope,
  LoginResult,
  DashboardView,
  MappingsSummaryView,
  RelaySnapshotView,
  StunSnapshotView,
  StunDroppedView,
  PunchStatsView,
  HourlyBucketView,
  UserView,
  UserListView,
  TempPasswordResult,
  DeviceView,
  DeviceListView,
  RemoteCodeResult,
  GroupListView,
  PolicyResult,
  GroupRequestListView,
  DecisionResult,
  MappingBytesView,
  MappingListView,
  ConfigItemView,
  ConfigListView,
  RelayConfigView,
  RelayEndView,
  RelaySessionView,
  RelaySessionsView,
  AuditLogView,
  AuditLogListView,
  ServerVersionView,
  SecurityCountView,
  SecurityStunView,
  BlacklistEntry,
  AutobanRuleView,
  SecurityOverviewView,
  EncryptionView,
  LockdownResultView,
  MappingStatusProjection,
} from "./types/api-server";

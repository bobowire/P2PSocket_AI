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
export { ApiPaths, ApiEndpoints } from "./types/api-paths";
export type {
  ApiEnvelope,
  MappingView,
  MappingTrafficView,
  RegistrationResult,
  GroupInfo,
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
} from "./types/api";

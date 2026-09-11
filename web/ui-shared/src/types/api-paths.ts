// 本文件由 export-ts 反射生成（06 §5、08 §2③），禁止手改；
// 源：LocalWebApi 真实路由表（src/Tools/ExportTs @ 2026-09-10T15:00:49Z）。前端禁止手写 API 字符串路径。

export const ApiPaths = {
  AuthLogin: "/api/auth/login",
  AuthLogout: "/api/auth/logout",
  AuthMe: "/api/auth/me",
  AuthRegister: "/api/auth/register",
  Device: "/api/device",
  Devices: "/api/devices",
  Diagnostics: "/api/diagnostics",
  Mappings: "/api/mappings",
  MappingsById: "/api/mappings/{id}",
  MappingsByIdDisable: "/api/mappings/{id}/disable",
  MappingsByIdEnable: "/api/mappings/{id}/enable",
  MappingsByIdRetry: "/api/mappings/{id}/retry",
  Settings: "/api/settings",
  SystemState: "/api/system/state",
  WizardRegister: "/api/wizard/register",
  WizardResult: "/api/wizard/result",
  WizardServerTest: "/api/wizard/server-test",
  WsStatus: "/ws/status",
} as const;

export type ApiPath = (typeof ApiPaths)[keyof typeof ApiPaths];

/** 端点元数据（method×path；WS 路由 methods 为 ["WS"]）。 */
export const ApiEndpoints = [
  { path: "/api/auth/login", methods: ["POST"] },
  { path: "/api/auth/logout", methods: ["POST"] },
  { path: "/api/auth/me", methods: ["GET"] },
  { path: "/api/auth/register", methods: ["POST"] },
  { path: "/api/device", methods: ["GET"] },
  { path: "/api/device", methods: ["PUT"] },
  { path: "/api/devices", methods: ["GET"] },
  { path: "/api/diagnostics", methods: ["GET"] },
  { path: "/api/mappings", methods: ["GET"] },
  { path: "/api/mappings", methods: ["POST"] },
  { path: "/api/mappings/{id}", methods: ["PUT"] },
  { path: "/api/mappings/{id}", methods: ["DELETE"] },
  { path: "/api/mappings/{id}/disable", methods: ["POST"] },
  { path: "/api/mappings/{id}/enable", methods: ["POST"] },
  { path: "/api/mappings/{id}/retry", methods: ["POST"] },
  { path: "/api/settings", methods: ["GET"] },
  { path: "/api/settings", methods: ["PUT"] },
  { path: "/api/system/state", methods: ["GET"] },
  { path: "/api/wizard/register", methods: ["POST"] },
  { path: "/api/wizard/result", methods: ["GET"] },
  { path: "/api/wizard/server-test", methods: ["POST"] },
  { path: "/ws/status", methods: ["WS"] },
] as const;

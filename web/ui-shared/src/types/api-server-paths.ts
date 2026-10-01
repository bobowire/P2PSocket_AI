// 本文件由 export-ts 反射生成（06 §5、08 §2③），禁止手改；
// 源：ServerWebHost 服务端 Web 路由表（src/Tools/ExportTs @ 2026-10-01T07:07:40Z）。前端禁止手写 API 字符串路径。

export const ServerApiPaths = {
  AuditLogs: "/api/audit-logs",
  AuthChangePassword: "/api/auth/change-password",
  AuthLogin: "/api/auth/login",
  AuthLogout: "/api/auth/logout",
  Dashboard: "/api/dashboard",
  Devices: "/api/devices",
  DevicesByIdDisable: "/api/devices/{id}/disable",
  DevicesByIdEnable: "/api/devices/{id}/enable",
  DevicesByIdResetRemoteCode: "/api/devices/{id}/reset-remote-code",
  DevicesByIdUnbind: "/api/devices/{id}/unbind",
  GroupRequests: "/api/group-requests",
  GroupRequestsByIdApprove: "/api/group-requests/{id}/approve",
  GroupRequestsByIdReject: "/api/group-requests/{id}/reject",
  Groups: "/api/groups",
  GroupsDefault: "/api/groups/default",
  Mappings: "/api/mappings",
  RelayConfig: "/api/relay/config",
  RelaySessions: "/api/relay/sessions",
  SystemConfig: "/api/system/config",
  Users: "/api/users",
  UsersByIdDisable: "/api/users/{id}/disable",
  UsersByIdEnable: "/api/users/{id}/enable",
  UsersByIdPasswordReset: "/api/users/{id}/password-reset",
} as const;

export type ServerApiPath = (typeof ServerApiPaths)[keyof typeof ServerApiPaths];

/** 端点元数据（method×path；WS 路由 methods 为 ["WS"]）。 */
export const ServerApiEndpoints = [
  { path: "/api/audit-logs", methods: ["GET"] },
  { path: "/api/auth/change-password", methods: ["POST"] },
  { path: "/api/auth/login", methods: ["POST"] },
  { path: "/api/auth/logout", methods: ["POST"] },
  { path: "/api/dashboard", methods: ["GET"] },
  { path: "/api/devices", methods: ["GET"] },
  { path: "/api/devices/{id}/disable", methods: ["POST"] },
  { path: "/api/devices/{id}/enable", methods: ["POST"] },
  { path: "/api/devices/{id}/reset-remote-code", methods: ["POST"] },
  { path: "/api/devices/{id}/unbind", methods: ["POST"] },
  { path: "/api/group-requests", methods: ["GET"] },
  { path: "/api/group-requests/{id}/approve", methods: ["POST"] },
  { path: "/api/group-requests/{id}/reject", methods: ["POST"] },
  { path: "/api/groups", methods: ["GET"] },
  { path: "/api/groups/default", methods: ["PUT"] },
  { path: "/api/mappings", methods: ["GET"] },
  { path: "/api/relay/config", methods: ["GET"] },
  { path: "/api/relay/config", methods: ["PUT"] },
  { path: "/api/relay/sessions", methods: ["GET"] },
  { path: "/api/system/config", methods: ["GET"] },
  { path: "/api/system/config", methods: ["PUT"] },
  { path: "/api/users", methods: ["GET"] },
  { path: "/api/users/{id}/disable", methods: ["POST"] },
  { path: "/api/users/{id}/enable", methods: ["POST"] },
  { path: "/api/users/{id}/password-reset", methods: ["PUT"] },
] as const;

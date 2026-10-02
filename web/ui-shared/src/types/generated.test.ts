// M1-31 生成物一致性测试（任务清单 M1-31 完成判定：生成物与 04 §2 端点一致）。
// 期望集为 04 §2 文档口径硬编码（HTTP 方法×路径 + /ws/status）；生成物漂移即红。
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import { ApiEndpoints, ApiPaths } from "./api-paths";

/** 04 §2 本地 Web 端点全集（2.1/2.2/2.3/2.4/2.5/2.6/2.7/2.8）。
 * M2-15 加 /api/device/reset-remote-code；M2-23 加 /api/peers/{deviceId}；
 * M2-26 加 /api/upgrade/info；M2-27 加 auth/change-password、groups 全套、
 * lan-segments、logs（本集自 M2-15/23 起补齐漂移收口）。 */
const DOCUMENTED: ReadonlyArray<{ path: string; methods: readonly string[] }> = [
  { path: "/api/system/state", methods: ["GET"] },
  { path: "/api/device", methods: ["GET", "PUT"] },
  { path: "/api/device/reset-remote-code", methods: ["POST"] },
  { path: "/api/devices", methods: ["GET"] },
  { path: "/api/auth/register", methods: ["POST"] },
  { path: "/api/auth/login", methods: ["POST"] },
  { path: "/api/auth/logout", methods: ["POST"] },
  { path: "/api/auth/change-password", methods: ["POST"] },
  { path: "/api/auth/me", methods: ["GET"] },
  { path: "/api/wizard/server-test", methods: ["POST"] },
  { path: "/api/wizard/register", methods: ["POST"] },
  { path: "/api/wizard/result", methods: ["GET"] },
  { path: "/api/mappings", methods: ["GET", "POST"] },
  { path: "/api/mappings/{id}", methods: ["PUT", "DELETE"] },
  { path: "/api/mappings/{id}/enable", methods: ["POST"] },
  { path: "/api/mappings/{id}/disable", methods: ["POST"] },
  { path: "/api/mappings/{id}/retry", methods: ["POST"] },
  { path: "/api/peers/{deviceId}", methods: ["GET", "PUT"] },
  { path: "/api/groups", methods: ["GET", "POST"] },
  { path: "/api/groups/join", methods: ["POST"] },
  { path: "/api/groups/{id}", methods: ["PUT", "DELETE"] },
  { path: "/api/groups/{id}/leave", methods: ["POST"] },
  { path: "/api/groups/{id}/invite", methods: ["GET", "DELETE"] },
  { path: "/api/groups/{id}/requests", methods: ["GET"] },
  { path: "/api/groups/{id}/members/{deviceId}/kick", methods: ["POST"] },
  { path: "/api/group-requests/{id}/approve", methods: ["POST"] },
  { path: "/api/group-requests/{id}/reject", methods: ["POST"] },
  { path: "/api/lan-segments", methods: ["GET", "POST"] },
  { path: "/api/lan-segments/{id}", methods: ["DELETE"] },
  { path: "/api/logs", methods: ["GET"] },
  { path: "/api/logs/export", methods: ["GET"] },
  { path: "/api/stats/summary", methods: ["GET"] }, // M3-12（FR-C-1002）
  { path: "/api/stats/export", methods: ["GET"] },
  { path: "/api/upgrade/info", methods: ["GET"] },
  { path: "/api/settings", methods: ["GET", "PUT"] },
  { path: "/api/diagnostics", methods: ["GET"] },
  { path: "/ws/status", methods: ["WS"] },
];

describe("export-ts 生成物", () => {
  it("端点集合与 04 §2 一致（路径×方法）", () => {
    // 生成物每注册一条一行（GET/PUT 同路径分行）——先按路径归并再比对
    const merged = new Map<string, string[]>();
    for (const e of ApiEndpoints)
      merged.set(e.path, [...(merged.get(e.path) ?? []), ...e.methods].sort());
    const generated = [...merged.entries()]
      .map(([path, methods]) => ({ path, methods }))
      .sort((a, b) => a.path.localeCompare(b.path));
    expect(generated).toEqual(
      DOCUMENTED.map((d) => ({ path: d.path, methods: [...d.methods].sort() }))
        .sort((a, b) => a.path.localeCompare(b.path)),
    );
  });

  it("ApiPaths 覆盖全部端点路径且值唯一", () => {
    const values = Object.values(ApiPaths);
    expect(new Set(values).size).toBe(values.length);
    const pathSet = new Set(ApiEndpoints.map((e) => e.path));
    expect(new Set(values)).toEqual(pathSet);
  });

  it("api.d.ts 含 envelope/WS 事件联合/WsEventNames 同源注释", () => {
    // cwd=包根（vitest 运行目录）；happy-dom 会改写 import.meta.url 故用 cwd 相对定位
    const dts = readFileSync(resolve("src/types/api.d.ts"), "utf8");
    for (const snippet of [
      "export interface ApiEnvelope<T>",
      "MappingState = \"disabled\" | \"punching\" | \"direct\" | \"relay\" | \"failed\" | \"invalid\"",
      "MappingStatsEvent",
      "MappingStateEvent",
      "LoginStateEvent",
      "DeviceListEvent",
      "UpgradeRequiredEvent",
      "export type WsEvent",
      'MappingState = "mapping_state"', // WsEventNames 反射同源注释
      'MappingStats = "mapping_stats"',
      'DeviceList = "device_list"',
      'LoginState = "login_state"',
      'UpgradeRequired = "upgrade_required"',
      'SubnetConflict = "subnet_conflict"', // M3-13
      "interface MappingView",
      "interface ClientSettings",
      "interface UpgradeInfoView", // M2-26
      "interface GroupView", // M2-27
      "interface GroupsView",
      "interface LanSegmentView",
      "interface LogPageView",
      "interface StatsSummaryView", // M3-12
      "interface MappingStatsView",
      "interface DeviceStatsView",
      "interface SubnetConflictState", // M3-13
      "interface SubnetConflictItem",
      "SubnetConflictEvent",
    ])
      expect(dts).toContain(snippet);
  });
});

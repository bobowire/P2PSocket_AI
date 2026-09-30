// M3-09 服务端生成物一致性测试（编制定案③：export-ts 扩输出 api-server.d.ts）。
// 期望集为 04 §3.1/§3.2 文档口径硬编码（M3-02~08 已落地的服务端 Web 端点全集）；
// 生成物漂移即红（M2-27 生成物漂移教训的同款纪律）。
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import { ServerApiEndpoints, ServerApiPaths } from "./api-server-paths";

/** 04 §3 服务端 Web 端点全集（§3.1 认证 + §3.2 管理 API，M3-02~08 落地）。 */
const DOCUMENTED: ReadonlyArray<{ path: string; methods: readonly string[] }> = [
  { path: "/api/auth/login", methods: ["POST"] },
  { path: "/api/auth/change-password", methods: ["POST"] },
  { path: "/api/auth/logout", methods: ["POST"] },
  { path: "/api/dashboard", methods: ["GET"] },
  { path: "/api/users", methods: ["GET"] },
  { path: "/api/users/{id}/disable", methods: ["POST"] },
  { path: "/api/users/{id}/enable", methods: ["POST"] },
  { path: "/api/users/{id}/password-reset", methods: ["PUT"] },
  { path: "/api/devices", methods: ["GET"] },
  { path: "/api/devices/{id}/disable", methods: ["POST"] },
  { path: "/api/devices/{id}/enable", methods: ["POST"] },
  { path: "/api/devices/{id}/unbind", methods: ["POST"] },
  { path: "/api/devices/{id}/reset-remote-code", methods: ["POST"] },
  { path: "/api/groups", methods: ["GET"] },
  { path: "/api/groups/default", methods: ["PUT"] },
  { path: "/api/group-requests", methods: ["GET"] },
  { path: "/api/group-requests/{id}/approve", methods: ["POST"] },
  { path: "/api/group-requests/{id}/reject", methods: ["POST"] },
  { path: "/api/mappings", methods: ["GET"] },
  { path: "/api/relay/sessions", methods: ["GET"] },
  { path: "/api/relay/config", methods: ["GET", "PUT"] },
  { path: "/api/system/config", methods: ["GET", "PUT"] },
  { path: "/api/audit-logs", methods: ["GET"] },
];

describe("export-ts 服务端生成物", () => {
  it("端点集合与 04 §3 一致（路径×方法）", () => {
    const merged = new Map<string, string[]>();
    for (const e of ServerApiEndpoints)
      merged.set(e.path, [...(merged.get(e.path) ?? []), ...e.methods].sort());
    const generated = [...merged.entries()]
      .map(([path, methods]) => ({ path, methods }))
      .sort((a, b) => a.path.localeCompare(b.path));
    expect(generated).toEqual(
      DOCUMENTED.map((d) => ({ path: d.path, methods: [...d.methods].sort() }))
        .sort((a, b) => a.path.localeCompare(b.path)),
    );
  });

  it("ServerApiPaths 覆盖全部端点路径且值唯一", () => {
    const values = Object.values(ServerApiPaths);
    expect(new Set(values).size).toBe(values.length);
    const pathSet = new Set(ServerApiEndpoints.map((e) => e.path));
    expect(new Set(values)).toEqual(pathSet);
  });

  it("api-server.d.ts 含 envelope 与 M3-09 交付 DTO（登录/仪表盘六指标族）", () => {
    const dts = readFileSync(resolve("src/types/api-server.d.ts"), "utf8");
    for (const snippet of [
      "export interface ServerApiEnvelope<T>",
      "  message: string;", // 服务端 envelope 字段名（客户端为 msg）
      "interface LoginResult",
      "interface DashboardView",
      "interface MappingsSummaryView",
      "interface RelaySnapshotView",
      "interface StunSnapshotView",
      "interface StunDroppedView",
      "interface PunchStatsView",
      "interface HourlyBucketView",
      "hourly: HourlyBucketView[]", // List<T> 生成正确性（曾漏映射为 number）
      "byStatus: Record<string, number>",
      "startedAt: string | null",
      "export type MappingStatusProjection",
    ])
      expect(dts).toContain(snippet);
  });
});

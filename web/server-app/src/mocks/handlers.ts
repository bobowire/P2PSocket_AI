// M3-09 MSW mock（06 §5，server-app 域）：服务端 Web 端点镜像（M3-09 交付范围=认证三端点+
// 仪表盘；其余端点 M3-10/11 页面落地时扩）。带内存状态的工厂形态，供 Vitest 走查（自定义初始态）。
// envelope { code, message, data }（04 §3：注意服务端字段名 message）；未认证 HTTP 401 {code:2001}
// ——与生产中间件形状一致（api.ts 拦截器按 -401/2001 跳登录）。
import { http, HttpResponse } from "msw";
import type { DashboardView } from "@p2p/ui-shared";

export interface MockServerState {
  authed: boolean;
  adminPassword: string; // 登录/改密校验（默认 admin/admin，FR-S-203）
  mustChangePassword: boolean; // 登录响应（仍是默认口令=true）
  dashboard: DashboardView;
}

function emptyDashboard(): DashboardView {
  return {
    onlineDevices: 0,
    groups: 1,
    mappings: { total: 0, enabled: 0, byStatus: { direct: 0, relay: 0, failed: 0, invalid: 0, unknown: 0 } },
    relay: { sessions: 0, bytesForwarded: 0, reaped: 0, startedAt: null, uptimeSec: 0 },
    stun: { admitted: 0, qps: 0, dropped: { rate: 0, auth: 0, circuit: 0 }, uptimeSec: 0 },
    punch: { total24h: 0, direct24h: 0, relay24h: 0, failed24h: 0, successRate24h: null, hourly: [] },
  };
}

export function createDefaultServerState(): MockServerState {
  return {
    authed: false,
    adminPassword: "admin",
    mustChangePassword: true,
    dashboard: {
      ...emptyDashboard(),
      onlineDevices: 3,
      groups: 2,
      mappings: { total: 5, enabled: 4, byStatus: { direct: 2, relay: 1, failed: 1, invalid: 0, unknown: 0 } },
      relay: { sessions: 1, bytesForwarded: 1048576, reaped: 2, startedAt: "2026-10-01T00:00:00Z", uptimeSec: 3725 },
      stun: { admitted: 3600, qps: 1.5, dropped: { rate: 12, auth: 3, circuit: 0 }, uptimeSec: 2400 },
      punch: {
        total24h: 40,
        direct24h: 30,
        relay24h: 6,
        failed24h: 4,
        successRate24h: 0.9,
        hourly: [
          { hourStart: 1759276800000, total: 0, success: 0 },
          { hourStart: 1759280400000, total: 10, success: 9 },
          { hourStart: 1759284000000, total: 20, success: 15 },
        ],
      },
    },
  };
}

const ok = (data: unknown) => HttpResponse.json({ code: 0, message: "ok", data });
const unauthorized = () =>
  new HttpResponse(JSON.stringify({ code: 2001, message: "未登录或会话失效" }), {
    status: 401,
    headers: { "Content-Type": "application/json" },
  });

export function createServerHandlers(state: MockServerState) {
  return [
    http.post("*/api/auth/login", async ({ request }) => {
      const body = (await request.json()) as { username?: string; password?: string };
      if (body.username !== "admin" || body.password !== state.adminPassword)
        return new HttpResponse(JSON.stringify({ code: 2001, message: "用户名或密码错误" }), {
          status: 401,
          headers: { "Content-Type": "application/json" },
        });
      state.authed = true;
      return ok({ mustChangePassword: state.mustChangePassword });
    }),

    http.post("*/api/auth/change-password", async ({ request }) => {
      if (!state.authed) return unauthorized();
      const body = (await request.json()) as { oldPassword?: string; newPassword?: string };
      if (body.oldPassword !== state.adminPassword)
        return new HttpResponse(JSON.stringify({ code: 2001, message: "当前密码不正确" }), {
          status: 401,
          headers: { "Content-Type": "application/json" },
        });
      if ((body.newPassword ?? "").length < 6)
        return HttpResponse.json({ code: 1001, message: "新密码长度至少 6 位" });
      state.adminPassword = body.newPassword!;
      state.mustChangePassword = false;
      return ok(null);
    }),

    http.post("*/api/auth/logout", () => {
      state.authed = false;
      return ok(null);
    }),

    http.get("*/api/dashboard", () => (state.authed ? ok(state.dashboard) : unauthorized())),
  ];
}

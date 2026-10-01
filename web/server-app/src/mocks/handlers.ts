// M3-09/10 MSW mock（06 §5，server-app 域）：服务端 Web 端点镜像（认证三端点+仪表盘+四管理页
// 全端点）。带内存状态的工厂形态，供 Vitest 走查（自定义初始态）；动作端点真实改内存态
// （禁用翻转/解绑删行/重置码换值/审批移单入组/策略双写/配置部分更新），断言走查闭环。
// envelope { code, message, data }（04 §3：注意服务端字段名 message）；未认证 HTTP 401 {code:2001}
// ——与生产中间件形状一致（api.ts 拦截器按 -401/2001 跳登录）。
import { http, HttpResponse } from "msw";
import type {
  DashboardView,
  DeviceView,
  GroupRequestView,
  GroupView,
  MappingView,
  UserView,
} from "@p2p/ui-shared";

export interface MockServerState {
  authed: boolean;
  adminPassword: string; // 登录/改密校验（默认 admin/admin，FR-S-203）
  mustChangePassword: boolean; // 登录响应（仍是默认口令=true）
  dashboard: DashboardView;
  users: UserView[];
  devices: DeviceView[];
  groups: GroupView[];
  groupRequests: GroupRequestView[];
  mappings: MappingView[];
  config: Record<string, string>; // server_config 现值（白名单键）
}

/// 测试固定身份（过滤联动断言用稳定 UUID）
export const DEV_A = "11111111-1111-1111-1111-111111111111";
export const DEV_B = "22222222-2222-2222-2222-222222222222";
export const USER_ALICE = "33333333-3333-3333-3333-333333333333";
export const USER_BOB = "44444444-4444-4444-4444-444444444444";
export const GROUP_DEFAULT = "55555555-5555-5555-5555-555555555555";
export const GROUP_OFFICE = "66666666-6666-6666-6666-666666666666";
export const REQUEST_B = "77777777-7777-7777-7777-777777777777";
export const ADMIN_ID = "88888888-8888-8888-8888-888888888888";

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
    users: [
      { id: ADMIN_ID, username: "admin", disabled: false, isAdmin: true, deviceCount: 0, createdAt: "2026-09-01T08:00:00Z" },
      { id: USER_ALICE, username: "alice", disabled: false, isAdmin: false, deviceCount: 2, createdAt: "2026-09-10T10:30:00Z" },
      { id: USER_BOB, username: "bob", disabled: true, isAdmin: false, deviceCount: 1, createdAt: "2026-09-12T14:00:00Z" },
    ],
    devices: [
      {
        deviceId: DEV_A, deviceName: "A 机", os: "windows", remoteCode: "712152",
        virtualIp: "100.64.0.2", ownerUsername: "alice", groups: ["默认分组"],
        online: true, disabled: false, createdAt: "2026-09-10T10:30:00Z",
      },
      {
        deviceId: DEV_B, deviceName: "B 机", os: "linux", remoteCode: "883456",
        virtualIp: "100.64.0.3", ownerUsername: "bob", groups: ["默认分组", "办公室"],
        online: false, disabled: false, createdAt: "2026-09-12T14:00:00Z",
      },
    ],
    groups: [
      {
        groupId: GROUP_DEFAULT, name: "默认分组", joinPolicy: "free", isDefault: true,
        ownerUsername: "admin", memberCount: 2, pendingCount: 0, createdAt: "2026-09-01T08:00:00Z",
      },
      {
        groupId: GROUP_OFFICE, name: "办公室", joinPolicy: "approval", isDefault: false,
        ownerUsername: "alice", memberCount: 1, pendingCount: 1, createdAt: "2026-09-11T09:00:00Z",
      },
    ],
    groupRequests: [
      {
        requestId: REQUEST_B, groupId: GROUP_OFFICE, groupName: "办公室", deviceId: DEV_B,
        deviceName: "B 机", ownerUsername: "alice", createdAt: "2026-09-30T16:20:00Z",
      },
    ],
    mappings: [
      {
        id: "aaaaaaaa-0000-0000-0000-000000000001", name: "网站", localPort: 8080, proto: "tcp",
        targetDeviceId: DEV_B, targetAddr: "self", targetPort: 80, enabled: true,
        createdAt: "2026-09-20T12:00:00Z", ownerDeviceId: DEV_A, ownerDeviceName: "A 机",
        ownerRemoteCode: "712152", targetDeviceName: "B 机",
        bytes: { up: 1048576, down: 2097152, relay: 0 },
        statsUpdatedAt: "2026-10-01T00:30:00Z", status: "direct",
      },
      {
        id: "aaaaaaaa-0000-0000-0000-000000000002", name: "文件服务", localPort: 2121, proto: "tcp",
        targetDeviceId: DEV_B, targetAddr: "self", targetPort: 21, enabled: true,
        createdAt: "2026-09-21T12:00:00Z", ownerDeviceId: DEV_A, ownerDeviceName: "A 机",
        ownerRemoteCode: "712152", targetDeviceName: "B 机",
        bytes: { up: 512, down: 1024, relay: 1536 },
        statsUpdatedAt: "2026-10-01T00:30:00Z", status: "relay",
      },
      {
        id: "aaaaaaaa-0000-0000-0000-000000000003", name: "归档 DNS", localPort: 53, proto: "udp",
        targetDeviceId: DEV_A, targetAddr: "10.0.0.5", targetPort: 53, enabled: false,
        createdAt: "2026-09-22T12:00:00Z", ownerDeviceId: DEV_B, ownerDeviceName: "B 机",
        ownerRemoteCode: "883456", targetDeviceName: "A 机",
        bytes: { up: 0, down: 0, relay: 0 },
        statsUpdatedAt: null, status: "unknown",
      },
    ],
    config: { registration_open: "1", default_join_policy: "free" },
  };
}

/// server_config 白名单默认值（DbInitializer.ConfigDefaults 同表；mock 只回 state.config 覆盖后的现值）
const CONFIG_DEFAULTS: Record<string, string> = {
  registration_open: "1",
  relay_enabled: "1",
  relay_rate_limit: "0",
  public_addr: "",
  stun_auth: "1",
  virtual_subnet: "100.64.0.0/24",
  audit_retention_days: "90",
  log_level: "Information",
  max_devices: "500",
  punch_retention_days: "90",
  stun_rate_per_ip: "50",
  stun_rate_per_device: "10",
  stun_circuit_pps: "2000",
  default_join_policy: "free",
  update_latest_version: "0.1.0",
  update_min_protocol: "1",
  update_url: "",
  update_notes: "",
};

const ok = (data: unknown) => HttpResponse.json({ code: 0, message: "ok", data });
const badRequest = (message: string) => HttpResponse.json({ code: 1001, message, data: null });
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
        return HttpResponse.json({ code: 1001, message: "新密码长度至少 6 位", data: null });
      state.adminPassword = body.newPassword!;
      state.mustChangePassword = false;
      return ok(null);
    }),

    http.post("*/api/auth/logout", () => {
      state.authed = false;
      return ok(null);
    }),

    http.get("*/api/dashboard", () => (state.authed ? ok(state.dashboard) : unauthorized())),

    // ── 用户（M3-03 端点形状）──────────────────────────────────────────
    http.get("*/api/users", ({ request }) => {
      if (!state.authed) return unauthorized();
      const url = new URL(request.url);
      const page = Math.max(1, Number(url.searchParams.get("page") ?? 1));
      const pageSize = Math.min(100, Math.max(1, Number(url.searchParams.get("pageSize") ?? 20)));
      const items = state.users.slice((page - 1) * pageSize, page * pageSize);
      return ok({ items, total: state.users.length, page, pageSize });
    }),

    http.post("*/api/users/:id/disable", ({ params }) => {
      if (!state.authed) return unauthorized();
      const u = state.users.find((x) => x.id === params.id);
      if (!u) return HttpResponse.json({ code: 1002, message: "用户不存在", data: null });
      if (u.isAdmin)
        return HttpResponse.json({ code: 1003, message: "不能禁用内置管理员（单管理员 D16）", data: null });
      u.disabled = true;
      return ok(null);
    }),

    http.post("*/api/users/:id/enable", ({ params }) => {
      if (!state.authed) return unauthorized();
      const u = state.users.find((x) => x.id === params.id);
      if (!u) return HttpResponse.json({ code: 1002, message: "用户不存在", data: null });
      u.disabled = false;
      return ok(null);
    }),

    http.put("*/api/users/:id/password-reset", ({ params }) => {
      if (!state.authed) return unauthorized();
      const u = state.users.find((x) => x.id === params.id);
      if (!u) return HttpResponse.json({ code: 1002, message: "用户不存在", data: null });
      if (u.isAdmin)
        return HttpResponse.json({
          code: 1003,
          message: "管理员密码请用 change-password 修改（Web 控制台本人操作）",
          data: null,
        });
      return ok({ tempPassword: "tmp-pw-x7k9" }); // 固定值便于走查断言
    }),

    // ── 设备（M3-04）──────────────────────────────────────────────────
    http.get("*/api/devices", () =>
      state.authed ? ok({ items: state.devices }) : unauthorized()),

    http.post("*/api/devices/:id/disable", ({ params }) => {
      if (!state.authed) return unauthorized();
      const d = state.devices.find((x) => x.deviceId === params.id);
      if (!d) return HttpResponse.json({ code: 1002, message: "设备不存在", data: null });
      d.disabled = true;
      d.online = false; // 踢线（AdminService 即时断连）
      return ok(null);
    }),

    http.post("*/api/devices/:id/enable", ({ params }) => {
      if (!state.authed) return unauthorized();
      const d = state.devices.find((x) => x.deviceId === params.id);
      if (!d) return HttpResponse.json({ code: 1002, message: "设备不存在", data: null });
      d.disabled = false;
      return ok(null);
    }),

    http.post("*/api/devices/:id/unbind", ({ params }) => {
      if (!state.authed) return unauthorized();
      const i = state.devices.findIndex((x) => x.deviceId === params.id);
      if (i < 0) return HttpResponse.json({ code: 1002, message: "设备不存在", data: null });
      state.devices.splice(i, 1); // 清理全集=行消失
      return ok(null);
    }),

    http.post("*/api/devices/:id/reset-remote-code", ({ params }) => {
      if (!state.authed) return unauthorized();
      const d = state.devices.find((x) => x.deviceId === params.id);
      if (!d) return HttpResponse.json({ code: 1002, message: "设备不存在", data: null });
      d.remoteCode = "NEW-CODE"; // 固定值便于走查断言
      return ok({ remoteCode: d.remoteCode });
    }),

    // ── 分组与审批（M3-05）────────────────────────────────────────────
    http.get("*/api/groups", () =>
      state.authed ? ok({ items: state.groups }) : unauthorized()),

    http.put("*/api/groups/default", async ({ request }) => {
      if (!state.authed) return unauthorized();
      const body = (await request.json()) as { policy?: string };
      if (body.policy !== "free" && body.policy !== "approval")
        return badRequest("参数错误（policy 须为 free|approval）");
      // 双写：键（下次建组口径）+ 默认分组行（存量即时生效）
      state.config.default_join_policy = body.policy!;
      const g = state.groups.find((x) => x.isDefault);
      if (g) g.joinPolicy = body.policy!;
      return ok({ policy: body.policy });
    }),

    http.get("*/api/group-requests", ({ request }) => {
      if (!state.authed) return unauthorized();
      const status = new URL(request.url).searchParams.get("status") ?? "pending";
      if (status !== "pending") return badRequest("参数错误（status 仅支持 pending）");
      return ok({ items: state.groupRequests });
    }),

    http.post("*/api/group-requests/:id/approve", ({ params }) => {
      if (!state.authed) return unauthorized();
      const i = state.groupRequests.findIndex((r) => r.requestId === params.id);
      if (i < 0) return HttpResponse.json({ code: 1002, message: "申请单不存在", data: null });
      const [r] = state.groupRequests.splice(i, 1);
      const g = state.groups.find((x) => x.groupId === r.groupId);
      if (g) {
        g.memberCount += 1;
        g.pendingCount = Math.max(0, g.pendingCount - 1);
      }
      return ok({ ok: true });
    }),

    http.post("*/api/group-requests/:id/reject", ({ params }) => {
      if (!state.authed) return unauthorized();
      const i = state.groupRequests.findIndex((r) => r.requestId === params.id);
      if (i < 0) return HttpResponse.json({ code: 1002, message: "申请单不存在", data: null });
      const [r] = state.groupRequests.splice(i, 1);
      const g = state.groups.find((x) => x.groupId === r.groupId);
      if (g) g.pendingCount = Math.max(0, g.pendingCount - 1);
      return ok({ ok: true });
    }),

    // ── 映射（M3-08；过滤=服务端 ?deviceId=/?status= 同口径）──────────
    http.get("*/api/mappings", ({ request }) => {
      if (!state.authed) return unauthorized();
      const q = new URL(request.url).searchParams;
      const deviceId = q.get("deviceId") ?? "";
      const status = q.get("status") ?? "";
      const items = state.mappings.filter(
        (m) => (!deviceId || m.ownerDeviceId === deviceId) && (!status || m.status === status),
      );
      return ok({ items, total: items.length });
    }),

    // ── 系统配置（M3-08；mock 白名单=全集键，校验示例口径）────────────
    http.get("*/api/system/config", () => {
      if (!state.authed) return unauthorized();
      const restartKeys = new Set([
        "public_addr", "stun_auth", "stun_rate_per_ip", "stun_rate_per_device", "stun_circuit_pps", "log_level",
      ]);
      return ok({
        items: Object.entries(CONFIG_DEFAULTS).map(([key, def]) => ({
          key,
          value: state.config[key] ?? def,
          restartRequired: restartKeys.has(key),
        })),
      });
    }),

    http.put("*/api/system/config", async ({ request }) => {
      if (!state.authed) return unauthorized();
      const body = (await request.json()) as Record<string, string>;
      if (!body || Object.keys(body).length === 0) return badRequest("参数错误（须提供至少一个配置键）");
      for (const [key, value] of Object.entries(body)) {
        if (!(key in CONFIG_DEFAULTS)) return badRequest(`参数错误（${key}：不在配置白名单内）`);
        if (key === "registration_open" && value !== "0" && value !== "1")
          return badRequest(`参数错误（${key}：须为 0|1）`);
      }
      Object.assign(state.config, body);
      const restartKeys = new Set([
        "public_addr", "stun_auth", "stun_rate_per_ip", "stun_rate_per_device", "stun_circuit_pps", "log_level",
      ]);
      return ok({
        items: Object.entries(CONFIG_DEFAULTS).map(([key, def]) => ({
          key,
          value: state.config[key] ?? def,
          restartRequired: restartKeys.has(key),
        })),
      });
    }),
  ];
}

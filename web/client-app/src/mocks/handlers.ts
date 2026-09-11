// M1-32 MSW mock（06 §5）：04 §2 全端点镜像——带内存状态的工厂形态，
// 供浏览器 dev（VITE_USE_MSW=1，默认演示态）与 Vitest 走查（自定义初始态）共用。
// envelope { code, msg, data }（04 §1：业务错误 HTTP 200）；错误码口径 04 §5。
import { http, HttpResponse } from "msw";

export interface MockMapping {
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

export interface MockState {
  phase: "unregistered" | "wizard" | "running" | "degraded";
  serverReachable: boolean;
  deviceId: string | null;
  remoteCode: string | null;
  virtualIp: string | null;
  username: string | null;
  capability: "normal" | "passive";
  users: Record<string, string>; // username → password（注册/登录校验）
  devices: {
    deviceId: string;
    deviceName: string;
    remoteCode: string;
    virtualIp: string;
    online: boolean;
    groups: string[];
    lanSegments: string[];
  }[];
  mappings: MockMapping[];
  settings: {
    serverAddrs: string[];
    localWebPort: number;
    punchConcurrency: number;
    keepaliveSec: number;
    reconnect: { minSec: number; maxSec: number };
  };
}

export function createDefaultState(): MockState {
  return {
    phase: "running",
    serverReachable: true,
    deviceId: "11111111-1111-4111-8111-111111111111",
    remoteCode: "a1b2c3",
    virtualIp: "100.64.0.2",
    username: null,
    capability: "passive",
    users: { demo: "secret123" },
    devices: [
      {
        deviceId: "11111111-1111-4111-8111-111111111111",
        deviceName: "本机（web-dev）",
        remoteCode: "a1b2c3",
        virtualIp: "100.64.0.2",
        online: true,
        groups: ["默认分组"],
        lanSegments: [],
      },
      {
        deviceId: "22222222-2222-4222-8222-222222222222",
        deviceName: "办公室主机",
        remoteCode: "d4e5f6",
        virtualIp: "100.64.0.2",
        online: true,
        groups: ["默认分组"],
        lanSegments: ["192.168.1.0/24"],
      },
      {
        deviceId: "33333333-3333-4333-8333-333333333333",
        deviceName: "家里 NAS",
        remoteCode: "0a1b2c",
        virtualIp: "100.64.0.2",
        online: false,
        groups: ["我的分组"],
        lanSegments: [],
      },
    ],
    mappings: [
      {
        mappingId: "44444444-4444-4444-8444-444444444444",
        name: "办公室 web",
        localPort: 18080,
        proto: "tcp",
        targetRemoteCode: "d4e5f6",
        targetAddr: "self",
        targetPort: 80,
        enabled: true,
        state: "direct",
        detail: null,
      },
      {
        mappingId: "55555555-5555-4555-8555-555555555555",
        name: "NAS ssh",
        localPort: 12222,
        proto: "tcp",
        targetRemoteCode: "0a1b2c",
        targetAddr: "self",
        targetPort: 22,
        enabled: true,
        state: "failed",
        detail: "punch_timeout",
      },
      {
        mappingId: "66666666-6666-4666-8666-666666666666",
        name: "备用",
        localPort: 19090,
        proto: "tcp",
        targetRemoteCode: "d4e5f6",
        targetAddr: "self",
        targetPort: 9090,
        enabled: false,
        state: "disabled",
        detail: null,
      },
    ],
    settings: {
      serverAddrs: ["127.0.0.1:7101"],
      localWebPort: 7100,
      punchConcurrency: 3,
      keepaliveSec: 20,
      reconnect: { minSec: 1, maxSec: 30 },
    },
  };
}

function ok<T>(data: T) {
  return HttpResponse.json({ code: 0, msg: "ok", data });
}

function fail(code: number, msg: string) {
  return HttpResponse.json({ code, msg, data: null });
}

/** 04 §2 端点全集处理器（path-only 模式：任意源匹配，浏览器/node 通用）。 */
export function createHandlers(state: MockState = createDefaultState()) {
  return [
    // ── 2.1 系统 ──────────────────────────────────────────────
    http.get("*/api/system/state", () =>
      ok({ phase: state.phase, serverReachable: state.serverReachable, protocolVersion: 1 })),
    http.get("*/api/device", () =>
      ok({
        deviceId: state.deviceId,
        remoteCode: state.remoteCode,
        virtualIp: state.virtualIp,
        username: state.username,
        capability: state.capability,
      })),
    http.put("*/api/device", async ({ request }) => {
      const body = (await request.json()) as { deviceName?: string };
      if (!body.deviceName?.trim()) return fail(1001, "device_name_empty");
      const me = state.devices.find((d) => d.deviceId === state.deviceId);
      if (me) me.deviceName = body.deviceName;
      return ok({ deviceName: body.deviceName });
    }),

    // ── 2.4 设备发现 ─────────────────────────────────────────
    http.get("*/api/devices", () => {
      if (state.capability === "passive") return fail(2002, "passive_forbidden");
      return ok(state.devices);
    }),

    // ── 2.2 账号 ─────────────────────────────────────────────
    http.post("*/api/auth/register", async ({ request }) => {
      const { username, password } = (await request.json()) as {
        username?: string; password?: string;
      };
      if (!username || username.length < 3 || !password || password.length < 6)
        return fail(1001, "bad_credentials");
      if (state.users[username]) return fail(1001, "username_taken");
      state.users[username] = password;
      return ok({ username });
    }),
    http.post("*/api/auth/login", async ({ request }) => {
      const { username, password } = (await request.json()) as {
        username?: string; password?: string;
      };
      if (state.users[username] !== password) return fail(2001, "bad_credentials");
      state.username = username ?? null;
      state.capability = "normal";
      return ok({ username, mode: "normal" });
    }),
    http.post("*/api/auth/logout", () => {
      state.username = null;
      state.capability = "passive";
      return ok(null);
    }),
    http.get("*/api/auth/me", () =>
      ok({ username: state.username, mode: state.capability })),

    // ── 2.3 向导 ─────────────────────────────────────────────
    http.post("*/api/wizard/server-test", async ({ request }) => {
      const { serverAddr } = (await request.json()) as { serverAddr?: string };
      if (!serverAddr) return fail(1001, "server_addr_empty");
      if (serverAddr === "127.0.0.1:1")
        return ok({ ok: false, detail: "连接超时：127.0.0.1:1" });
      return ok({ ok: true, detail: "" });
    }),
    http.post("*/api/wizard/register", async ({ request }) => {
      if (state.phase !== "unregistered") return fail(1003, "already_registered");
      const body = (await request.json()) as {
        serverAddr?: string; mode?: string; username?: string; password?: string; deviceName?: string;
      };
      if (body.mode === "account" && (!body.username || !body.password || body.password.length < 6))
        return fail(1001, "bad_credentials");
      state.phase = "running";
      state.deviceId = state.deviceId ?? "77777777-7777-4777-8777-777777777777";
      state.remoteCode = state.remoteCode ?? "7g8h9j";
      state.virtualIp = state.virtualIp ?? "100.64.0.2";
      if (body.mode === "account") {
        state.users[body.username!] = body.password!;
        state.username = body.username!;
        state.capability = "normal";
      }
      return ok({
        deviceId: state.deviceId,
        remoteCode: state.remoteCode,
        virtualIp: state.virtualIp,
        groups: [{ groupId: "g1", groupName: "我的分组" }],
      });
    }),
    http.get("*/api/wizard/result", () =>
      state.remoteCode
        ? ok({
            deviceId: state.deviceId,
            remoteCode: state.remoteCode,
            virtualIp: state.virtualIp,
            groups: [{ groupId: "g1", groupName: "我的分组" }],
          })
        : fail(1002, "not_registered")),

    // ── 2.5 映射 ─────────────────────────────────────────────
    http.get("*/api/mappings", ({ request }) => {
      const url = new URL(request.url);
      const page = Math.max(1, Number(url.searchParams.get("page") ?? 1));
      const pageSize = Math.min(100, Math.max(1, Number(url.searchParams.get("pageSize") ?? 20)));
      const items = state.mappings.slice((page - 1) * pageSize, page * pageSize);
      return ok({ items, total: state.mappings.length });
    }),
    http.post("*/api/mappings", async ({ request }) => {
      const body = (await request.json()) as {
        name?: string; localPort?: number; proto?: string;
        targetRemoteCode?: string; targetPort?: number;
      };
      if (!body.name?.trim() || !body.localPort || !body.targetRemoteCode || !body.targetPort)
        return fail(1001, "bad_fields");
      if (!state.devices.some((d) => d.remoteCode === body.targetRemoteCode))
        return fail(4003, "remote_code_invalid");
      if (state.mappings.some((m) => m.proto === body.proto && m.localPort === body.localPort))
        return fail(1003, "port_conflict");
      const m: MockMapping = {
        mappingId: crypto.randomUUID(),
        name: body.name,
        localPort: body.localPort,
        proto: body.proto ?? "tcp",
        targetRemoteCode: body.targetRemoteCode,
        targetAddr: "self",
        targetPort: body.targetPort,
        enabled: false,
        state: "disabled",
        detail: null,
      };
      state.mappings.push(m);
      return ok(m);
    }),
    http.put("*/api/mappings/:id", async ({ request, params }) => {
      const m = state.mappings.find((x) => x.mappingId === params.id);
      if (!m) return fail(1002, "not_found");
      const body = (await request.json()) as {
        name?: string; localPort?: number; targetRemoteCode?: string; targetPort?: number;
      };
      if (m.enabled && body.localPort !== m.localPort) return fail(1003, "port_change_requires_disabled");
      Object.assign(m, {
        name: body.name ?? m.name,
        localPort: body.localPort ?? m.localPort,
        targetRemoteCode: body.targetRemoteCode ?? m.targetRemoteCode,
        targetPort: body.targetPort ?? m.targetPort,
      });
      return ok(m);
    }),
    http.delete("*/api/mappings/:id", ({ params }) => {
      const i = state.mappings.findIndex((x) => x.mappingId === params.id);
      if (i >= 0) state.mappings.splice(i, 1);
      return ok(null);
    }),
    http.post("*/api/mappings/:id/enable", ({ params }) => {
      const m = state.mappings.find((x) => x.mappingId === params.id);
      if (!m) return fail(1002, "not_found");
      m.enabled = true;
      m.state = "punching";
      return ok(m);
    }),
    http.post("*/api/mappings/:id/disable", ({ params }) => {
      const m = state.mappings.find((x) => x.mappingId === params.id);
      if (!m) return fail(1002, "not_found");
      m.enabled = false;
      m.state = "disabled";
      return ok(m);
    }),
    http.post("*/api/mappings/:id/retry", ({ params }) => {
      const m = state.mappings.find((x) => x.mappingId === params.id);
      if (!m) return fail(1002, "not_found");
      if (m.state === "failed") m.state = "punching";
      return ok(m);
    }),

    // ── 2.1 设置 ─────────────────────────────────────────────
    http.get("*/api/settings", () => ok(state.settings)),
    http.put("*/api/settings", async ({ request }) => {
      const body = (await request.json()) as Partial<MockState["settings"]>;
      if (body.localWebPort !== undefined && (body.localWebPort < 1 || body.localWebPort > 65535))
        return fail(1001, "local_web_port_out_of_range");
      if (body.punchConcurrency !== undefined &&
        (body.punchConcurrency < 1 || body.punchConcurrency > 5))
        return fail(1001, "punch_concurrency_out_of_range");
      const before = { port: state.settings.localWebPort, addrs: state.settings.serverAddrs };
      Object.assign(state.settings, body);
      return ok({
        restartRequired: state.settings.localWebPort !== before.port,
        serverAddrsChanged:
          JSON.stringify(state.settings.serverAddrs) !== JSON.stringify(before.addrs),
        settings: state.settings,
      });
    }),

    // ── 2.6 诊断 ─────────────────────────────────────────────
    http.get("*/api/diagnostics", () =>
      ok({ punchQueueDepth: 0, currentPunchPeer: null })),
  ];
}

/** dev 演示态（VITE_USE_MSW=1；06 §5 独立联调）。 */
export const handlers = createHandlers();

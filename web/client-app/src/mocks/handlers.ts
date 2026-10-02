// M1-32 MSW mock（06 §5）：04 §2 全端点镜像——带内存状态的工厂形态，
// 供浏览器 dev（VITE_USE_MSW=1，默认演示态）与 Vitest 走查（自定义初始态）共用。
// envelope { code, msg, data }（04 §1：业务错误 HTTP 200）；错误码口径 04 §5。
// M2-27 同步：分组全套（0x42 聚合形态+审批闭环）、lan-segments、logs（过滤/分页/导出）、
// change-password；一并补齐 M2-15/23/26 漂移端点（reset-remote-code/peers/upgrade）。
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
  /** M3-12 流量汇总演示值（本地引擎累计口径；缺省 0）。 */
  bytes?: { up: number; down: number; relay: number };
}

export interface MockGroup {
  groupId: string;
  groupName: string;
  policy: "free" | "approval";
  isOwner: boolean;
  memberCount: number;
  inviteCode: string | null;
}

export interface MockJoinRequest {
  requestId: string;
  groupId: string;
  deviceId: string;
  deviceName: string;
  createdAtMs: number;
}

export interface MockLanSegment {
  segmentId: string;
  cidr: string;
  enabled: boolean;
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
  groups: MockGroup[]; // M2-27：已加入分组（04 §2.4 /api/groups）
  joinRequests: MockJoinRequest[]; // 待审批申请（所有者侧聚合）
  lanSegments: MockLanSegment[]; // M2-27：开放内网段白名单（04 §2.5）
  peers: Record<string, boolean>; // M2-23：deviceId → relayFallback
  conflict: { subnet: string; items: { kind: string; value: string; interface: string }[] } | null; // M3-13：网段冲突（null=无）
  logs: { ts: string; level: string; message: string }[]; // M2-27：演示日志（04 §2.6）
  upgrade: { latestVersion: string; minProtocol: number; maxProtocol: number; upgradeUrl: string; notes: string };
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
    conflict: null, // M3-13：默认无冲突（走查用例按需注入）
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
        bytes: { up: 1048576, down: 2097152, relay: 0 },
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
        name: "办公室 DNS",
        localPort: 15353,
        proto: "udp",
        targetRemoteCode: "d4e5f6",
        targetAddr: "self",
        targetPort: 53,
        enabled: true,
        state: "relay",
        detail: null,
        bytes: { up: 524288, down: 524288, relay: 524288 },
      },
    ],
    groups: [
      { groupId: "g-11111111-1111-4111-8111-111111111111", groupName: "默认分组", policy: "free", isOwner: false, memberCount: 3, inviteCode: null },
      { groupId: "g-22222222-2222-4222-8222-222222222222", groupName: "项目协作组", policy: "approval", isOwner: true, memberCount: 2, inviteCode: null },
    ],
    joinRequests: [
      {
        requestId: "r-33333333-3333-4333-8333-333333333333",
        groupId: "g-22222222-2222-4222-8222-222222222222",
        deviceId: "33333333-3333-4333-8333-333333333333",
        deviceName: "家里 NAS",
        createdAtMs: Date.UTC(2026, 8, 28, 10, 0, 0),
      },
    ],
    lanSegments: [
      { segmentId: "s-44444444-4444-4444-8444-444444444444", cidr: "192.168.1.0/24", enabled: true },
    ],
    peers: {},
    logs: [
      { ts: "2026-09-30 08:00:00.100 +08:00", level: "INF", message: "控制通道已建立（127.0.0.1:7101）" },
      { ts: "2026-09-30 08:00:01.200 +08:00", level: "INF", message: "端口映射「办公室 web」进入 direct" },
      { ts: "2026-09-30 08:05:00.300 +08:00", level: "WRN", message: "心跳 Ack 延迟 2.1s（时钟漂移重校准）" },
      { ts: "2026-09-30 09:12:33.400 +08:00", level: "ERR", message: "映射「NAS ssh」打洞失败：punch_timeout" },
    ],
    upgrade: {
      latestVersion: "0.3.0",
      minProtocol: 1,
      maxProtocol: 1,
      upgradeUrl: "https://example.com/p2p-client-0.3.0.msi",
      notes: "演示态升级信息（M2-26 /api/upgrade/info）",
    },
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

/** M3-12 汇总派生（生产 MappingSyncService.BuildSummary 同口径：映射行名+Id 序、
 * 设备维按目标远程码分组求和、总计=全量和）。 */
function summaryOf(state: MockState) {
  const byMappings = state.mappings
    .map((m) => ({
      mappingId: m.mappingId, name: m.name, proto: m.proto, localPort: m.localPort,
      targetRemoteCode: m.targetRemoteCode, targetPort: m.targetPort, path: m.state,
      bytesUp: m.bytes?.up ?? 0, bytesDown: m.bytes?.down ?? 0, relayBytes: m.bytes?.relay ?? 0,
    }))
    .sort((a, b) => a.name.localeCompare(b.name) || a.mappingId.localeCompare(b.mappingId));
  const byDevices = Object.values(
    byMappings.reduce<Record<string, { targetRemoteCode: string; mappings: number; bytesUp: number; bytesDown: number; relayBytes: number }>>(
      (acc, m) => {
        const d = (acc[m.targetRemoteCode] ??= {
          targetRemoteCode: m.targetRemoteCode, mappings: 0, bytesUp: 0, bytesDown: 0, relayBytes: 0,
        });
        d.mappings += 1;
        d.bytesUp += m.bytesUp;
        d.bytesDown += m.bytesDown;
        d.relayBytes += m.relayBytes;
        return acc;
      },
      {},
    ),
  ).sort((a, b) => a.targetRemoteCode.localeCompare(b.targetRemoteCode));
  return {
    byMappings,
    byDevices,
    totalBytesUp: byMappings.reduce((s, m) => s + m.bytesUp, 0),
    totalBytesDown: byMappings.reduce((s, m) => s + m.bytesDown, 0),
    totalRelayBytes: byMappings.reduce((s, m) => s + m.relayBytes, 0),
  };
}

function fail(code: number, msg: string) {
  return HttpResponse.json({ code, msg, data: null });
}

/** 04 §2 端点全集处理器（path-only 模式：任意源匹配，浏览器/node 通用）。 */
export function createHandlers(state: MockState = createDefaultState()) {
  return [
    // ── 2.1 系统 ──────────────────────────────────────────────
    http.get("*/api/system/state", () =>
      ok({ phase: state.phase, serverReachable: state.serverReachable, protocolVersion: 1,
        conflict: state.conflict })),
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
        targetRemoteCode?: string; targetAddr?: string; targetPort?: number;
      };
      if (!body.name?.trim() || !body.localPort || !body.targetRemoteCode || !body.targetPort)
        return fail(1001, "bad_fields");
      if (!state.devices.some((d) => d.remoteCode === body.targetRemoteCode))
        return fail(4003, "remote_code_invalid");
      if (state.mappings.some((m) => m.proto === (body.proto ?? "tcp") && m.localPort === body.localPort))
        return fail(1003, "port_conflict");
      const m: MockMapping = {
        mappingId: crypto.randomUUID(),
        name: body.name,
        localPort: body.localPort,
        proto: body.proto ?? "tcp",
        targetRemoteCode: body.targetRemoteCode,
        targetAddr: body.targetAddr ?? "self",
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
        name?: string; localPort?: number; proto?: string;
        targetRemoteCode?: string; targetAddr?: string; targetPort?: number;
      };
      if (m.enabled && body.localPort !== m.localPort) return fail(1003, "port_change_requires_disabled");
      Object.assign(m, {
        name: body.name ?? m.name,
        localPort: body.localPort ?? m.localPort,
        proto: body.proto ?? m.proto,
        targetRemoteCode: body.targetRemoteCode ?? m.targetRemoteCode,
        targetAddr: body.targetAddr ?? m.targetAddr,
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

    // ── M2-15 远程码重置（04 §2.1）───────────────────────────
    http.post("*/api/device/reset-remote-code", () => {
      state.remoteCode = Array.from({ length: 6 }, () =>
        "0123456789abc"[Math.floor(Math.random() * 13)]).join("");
      const me = state.devices.find((d) => d.deviceId === state.deviceId);
      if (me) me.remoteCode = state.remoteCode;
      return ok({ remoteCode: state.remoteCode });
    }),

    // ── M2-23 目标设备级配置（04 §2.4）───────────────────────
    http.get("*/api/peers/:deviceId", ({ params }) =>
      ok({ deviceId: params.deviceId, relayFallback: state.peers[params.deviceId as string] ?? false })),
    http.put("*/api/peers/:deviceId", async ({ request, params }) => {
      const body = (await request.json()) as { relayFallback?: boolean };
      state.peers[params.deviceId as string] = body.relayFallback ?? false;
      return ok({ deviceId: params.deviceId, relayFallback: body.relayFallback ?? false });
    }),

    // ── M2-26 升级信息（04 §2.7）─────────────────────────────
    http.get("*/api/upgrade/info", () => ok(state.upgrade)),

    // ── M2-27 改密（04 §2.3）────────────────────────────────
    http.post("*/api/auth/change-password", async ({ request }) => {
      const { oldPassword, newPassword } = (await request.json()) as {
        oldPassword?: string; newPassword?: string;
      };
      if (!state.username || !oldPassword || state.users[state.username] !== oldPassword)
        return fail(2001, "bad_old_password");
      if (!newPassword || newPassword.length < 6) return fail(1001, "bad_new_password");
      state.users[state.username] = newPassword;
      return ok(null);
    }),

    // ── M2-27 分组全套（04 §2.4；主动类 passive 2002）────────
    http.get("*/api/groups", () => {
      if (state.capability === "passive") return fail(2002, "passive_forbidden");
      return ok({ items: state.groups, requests: state.joinRequests });
    }),
    http.post("*/api/groups", async ({ request }) => {
      if (state.capability === "passive") return fail(2002, "passive_forbidden");
      const { name, joinPolicy } = (await request.json()) as { name?: string; joinPolicy?: string };
      if (!name?.trim() || name.trim().length > 64) return fail(1001, "bad_name");
      if (joinPolicy !== "free" && joinPolicy !== "approval") return fail(1001, "bad_policy");
      const g: MockGroup = {
        groupId: crypto.randomUUID(),
        groupName: name.trim(),
        policy: joinPolicy,
        isOwner: true,
        memberCount: 1,
        inviteCode: null,
      };
      state.groups.push(g);
      return ok({ groupId: g.groupId });
    }),
    http.post("*/api/groups/join", async ({ request }) => {
      if (state.capability === "passive") return fail(2002, "passive_forbidden");
      const { inviteCode } = (await request.json()) as { inviteCode?: string };
      const g = state.groups.find((x) => x.inviteCode && x.inviteCode === inviteCode);
      if (!g) return fail(3001, "invite_invalid");
      if (g.policy === "approval") {
        state.joinRequests.push({
          requestId: crypto.randomUUID(),
          groupId: g.groupId,
          deviceId: state.deviceId ?? crypto.randomUUID(),
          deviceName: "本机（web-dev）",
          createdAtMs: Date.now(),
        });
        return fail(3002, "group_need_approval");
      }
      g.memberCount++;
      return ok({ groupId: g.groupId });
    }),
    http.post("*/api/groups/:id/leave", ({ params }) => {
      const i = state.groups.findIndex((x) => x.groupId === params.id);
      if (i < 0 || state.groups[i].memberCount <= 0) return fail(1001, "not_member");
      state.groups.splice(i, 1); // 退组后不再出现在 0x42 已加入列表（04 §2.4 语义）
      return ok(null);
    }),
    http.put("*/api/groups/:id", async ({ request, params }) => {
      const g = state.groups.find((x) => x.groupId === params.id);
      if (!g || !g.isOwner) return fail(1001, "not_group_owner");
      const { name, joinPolicy } = (await request.json()) as { name?: string; joinPolicy?: string };
      if (joinPolicy !== undefined && joinPolicy !== "free" && joinPolicy !== "approval")
        return fail(1001, "bad_policy");
      if (!name?.trim() && !joinPolicy) return fail(1001, "nothing_to_update");
      if (name?.trim()) g.groupName = name.trim();
      if (joinPolicy) g.policy = joinPolicy;
      return ok(null);
    }),
    http.delete("*/api/groups/:id", ({ params }) => {
      const i = state.groups.findIndex((x) => x.groupId === params.id);
      if (i < 0 || !state.groups[i].isOwner) return fail(1001, "not_group_owner");
      const [removed] = state.groups.splice(i, 1);
      state.joinRequests = state.joinRequests.filter((r) => r.groupId !== removed.groupId);
      return ok(null);
    }),
    http.post("*/api/groups/:id/members/:deviceId/kick", ({ params }) => {
      const g = state.groups.find((x) => x.groupId === params.id);
      if (!g || !g.isOwner) return fail(1001, "not_group_owner");
      if (g.memberCount <= 0) return fail(1001, "not_member");
      g.memberCount--;
      state.joinRequests = state.joinRequests.filter(
        (r) => !(r.groupId === params.id && r.deviceId === params.deviceId));
      return ok(null);
    }),
    http.get("*/api/groups/:id/invite", ({ params }) => {
      const g = state.groups.find((x) => x.groupId === params.id);
      if (!g || !g.isOwner) return fail(1001, "not_group_owner");
      g.inviteCode = Array.from({ length: 6 }, () =>
        "23456789ABCDEFGHJKMNPQRSTUVWXYZ"[Math.floor(Math.random() * 31)]).join("");
      return ok({ inviteCode: g.inviteCode });
    }),
    http.delete("*/api/groups/:id/invite", ({ params }) => {
      const g = state.groups.find((x) => x.groupId === params.id);
      if (!g || !g.isOwner) return fail(1001, "not_group_owner");
      g.inviteCode = null;
      return ok(null);
    }),
    http.get("*/api/groups/:id/requests", ({ params }) =>
      ok(state.joinRequests.filter((r) => r.groupId === params.id))),
    http.post("*/api/group-requests/:id/approve", ({ params }) => {
      const i = state.joinRequests.findIndex((r) => r.requestId === params.id);
      if (i < 0) return fail(1001, "request_handled");
      const [r] = state.joinRequests.splice(i, 1);
      const g = state.groups.find((x) => x.groupId === r.groupId);
      if (g) g.memberCount++;
      return ok(null);
    }),
    http.post("*/api/group-requests/:id/reject", ({ params }) => {
      const i = state.joinRequests.findIndex((r) => r.requestId === params.id);
      if (i < 0) return fail(1001, "request_handled");
      state.joinRequests.splice(i, 1);
      return ok(null);
    }),

    // ── M2-27 lan-segments（04 §2.5；passive 允许）────────────
    http.get("*/api/lan-segments", () => ok(state.lanSegments)),
    http.post("*/api/lan-segments", async ({ request }) => {
      const { cidr } = (await request.json()) as { cidr?: string };
      // 规范化镜像服务端 NormalizeCidr 三步：显式前缀原样、裸 IPv4 补 /32、非法 1001
      const m = cidr?.trim().match(/^(\d{1,3}(?:\.\d{1,3}){3})(?:\/(\d{1,2}))?$/);
      if (!m || m[1].split(".").some((o) => Number(o) > 255) || Number(m[2]) > 32)
        return fail(1001, "bad_cidr");
      const normalized = m[2] !== undefined ? `${m[1]}/${m[2]}` : `${m[1]}/32`;
      const seg: MockLanSegment = { segmentId: crypto.randomUUID(), cidr: normalized, enabled: true };
      state.lanSegments.push(seg);
      return ok(seg);
    }),
    http.delete("*/api/lan-segments/:id", ({ params }) => {
      const i = state.lanSegments.findIndex((s) => s.segmentId === params.id);
      if (i >= 0) state.lanSegments.splice(i, 1);
      return ok(null);
    }),

    // ── M2-27 日志（04 §2.6；newest-first + 级别过滤 + 分页）──
    http.get("*/api/logs", ({ request }) => {
      const url = new URL(request.url);
      const level = url.searchParams.get("level");
      const levelToken = level
        ? ({ debug: "DBG", info: "INF", information: "INF", warning: "WRN", warn: "WRN", error: "ERR", fatal: "FTL" } as Record<string, string>)[level.toLowerCase()] ?? null
        : null;
      if (level && !levelToken) return fail(1001, "bad_level");
      const all = [...state.logs].reverse().filter((l) => !levelToken || l.level === levelToken);
      const page = Math.max(1, Number(url.searchParams.get("page") ?? 1));
      const pageSize = 200;
      const items = all.slice((page - 1) * pageSize, page * pageSize);
      return ok({ items, page, pageSize, total: all.length, hasMore: page * pageSize < all.length });
    }),
    http.get("*/api/logs/export", ({ request }) => {
      const url = new URL(request.url);
      const level = url.searchParams.get("level");
      const levelToken = level
        ? ({ debug: "DBG", info: "INF", information: "INF", warning: "WRN", warn: "WRN", error: "ERR", fatal: "FTL" } as Record<string, string>)[level.toLowerCase()] ?? null
        : null;
      if (level && !levelToken) return fail(1001, "bad_level");
      const text = state.logs
        .filter((l) => !levelToken || l.level === levelToken)
        .map((l) => `${l.ts} [${l.level}] ${l.message}`)
        .join("\n");
      return new HttpResponse(text, {
        headers: {
          "content-type": "text/plain; charset=utf-8",
          "content-disposition": 'attachment; filename="p2p-logs-demo.txt"',
        },
      });
    }),

    // ── M3-12 流量汇总与导出（FR-C-1002；summary 由 mappings 派生两维聚合）──
    http.get("*/api/stats/summary", () => ok(summaryOf(state))),
    http.get("*/api/stats/export", ({ request }) => {
      const format = new URL(request.url).searchParams.get("format");
      if ((format ?? "").toLowerCase() !== "csv") return fail(1001, "format 仅支持 csv");
      const s = summaryOf(state);
      const lines = [
        "维度,名称,协议,本地端口,目标远程码,目标端口,当前路径,累计上行(B),累计下行(B),其中中继(B)",
        ...s.byMappings.map((m) =>
          `mapping,${m.name},${m.proto},${m.localPort},${m.targetRemoteCode},${m.targetPort},${m.path},${m.bytesUp},${m.bytesDown},${m.relayBytes}`),
        "",
        "维度,目标远程码,映射数,累计上行(B),累计下行(B),其中中继(B)",
        ...s.byDevices.map((d) =>
          `device,${d.targetRemoteCode},${d.mappings},${d.bytesUp},${d.bytesDown},${d.relayBytes}`),
        `total,,${s.byMappings.length},${s.totalBytesUp},${s.totalBytesDown},${s.totalRelayBytes}`,
      ];
      return new HttpResponse("﻿" + lines.join("\n"), {
        headers: {
          "content-type": "text/csv; charset=utf-8",
          "content-disposition": 'attachment; filename="p2p-stats-demo.csv"',
        },
      });
    }),
  ];
}

/** dev 演示态（VITE_USE_MSW=1；06 §5 独立联调）。 */
export const handlers = createHandlers();

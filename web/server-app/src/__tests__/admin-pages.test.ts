// M3-10 四管理页走查测试（任务清单完成判定：列表渲染/confirm 流/审批后队列清空/过滤联动；
// 另含注册开关切换/内置管理员行按钮禁用/解绑警示文案/一次性密码与重置码 alert）。
// 装配同 pages.test.ts：每用例独立 MSW 处理器+pinia/router/ElementPlus，页面经真实
// HTTP→store→渲染链路；confirm/alert 挂 body → document.querySelector 定位（教训⑲）；
// ElSwitch/ElSelect/ElPagination 交互走 findAllComponents vm.$emit（VTU 走 vnode 树）。
import { setupServer } from "msw/node";
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { createPinia, setActivePinia, type Pinia } from "pinia";
import type { Router } from "vue-router";
import ElementPlus, { ElPagination, ElSelect } from "element-plus";
import { createAppRouter } from "../router";
import Devices from "../pages/Devices.vue";
import Groups from "../pages/Groups.vue";
import Mappings from "../pages/Mappings.vue";
import Users from "../pages/Users.vue";
import { AUTH_KEY } from "../api";
import {
  ADMIN_ID, DEV_A, createDefaultServerState, createServerHandlers, type MockServerState,
} from "../mocks/handlers";

const server = setupServer();
/** attachTo 挂载的页面逐用例拆净（ElMessageBox 挂 body，防跨用例串台） */
const live: VueWrapper[] = [];

beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterEach(() => {
  server.resetHandlers();
  for (const w of live.splice(0)) w.unmount();
  document.body.innerHTML = ""; // confirm/alert/message 均挂 body
  sessionStorage.clear();
});
afterAll(() => server.close());

async function mountAdminPage(
  component: Parameters<typeof mount>[0],
  state: MockServerState,
  initialPath: string,
): Promise<{ wrapper: VueWrapper; router: Router; pinia: Pinia }> {
  state.authed = true;
  server.use(...createServerHandlers(state));
  sessionStorage.setItem(AUTH_KEY, "1"); // 受保护页直挂：刷新存活标记（M3-09 同款）
  const pinia = createPinia();
  setActivePinia(pinia);
  const router = createAppRouter();
  await router.push(initialPath);
  await router.isReady();
  const wrapper = mount(component, {
    global: { plugins: [pinia, ElementPlus, router] },
    attachTo: document.body,
  });
  live.push(wrapper);
  await flushPromises();
  return { wrapper, router, pinia };
}

/** 可见的 message-box（ElMessageBox 关闭后 overlay 走 v-show 残留 DOM——须按 display 过滤，
 *  否则 querySelector 第一个命中上一只已关弹窗，二次弹窗断言读到旧文本） */
function visibleBoxes(): HTMLElement[] {
  return [...document.querySelectorAll<HTMLElement>(".el-message-box")].filter(
    (b) => (b.closest(".el-overlay") as HTMLElement | null)?.style.display !== "none",
  );
}

/** 确认弹窗主按钮（挂 body，不走 wrapper 树）；等待 ElMessageBox 异步挂载 */
async function clickConfirm(): Promise<void> {
  const btn = await vi.waitFor(() => {
    const vis = visibleBoxes();
    const b = vis[vis.length - 1]?.querySelector<HTMLElement>(".el-message-box__btns .el-button--primary");
    expect(b).not.toBeNull();
    return b!;
  });
  btn.click();
  await flushPromises();
  await flushPromises(); // 动作请求 + 动作后 refresh 两轮微任务
}

/** 当前（最新一只可见）弹窗正文——同一用例内二次弹窗（confirm→alert）时取后挂载者 */
function boxMessage(): string {
  const vis = visibleBoxes();
  return vis[vis.length - 1]?.querySelector(".el-message-box__message")?.textContent ?? "";
}

/** 任一可见弹窗正文含片段（alert 断言用：confirm 残留尚未隐藏时新 alert 已挂载） */
function anyBoxTextIncludes(s: string): boolean {
  return visibleBoxes().some((b) => b.textContent?.includes(s));
}

/** 等全部弹窗关闭（overlay v-show 隐藏完成）——连续动作前的隔离点 */
async function waitBoxesClosed(): Promise<void> {
  await vi.waitFor(() => expect(visibleBoxes().length).toBe(0), { timeout: 3000 });
  await flushPromises();
}

/** 关闭最新弹窗（alert 的“我已保存”等一次性出口） */
async function closeTopBox(): Promise<void> {
  const vis = visibleBoxes();
  (vis[vis.length - 1]?.querySelector<HTMLElement>(".el-message-box__btns .el-button--primary"))?.click();
  await flushPromises();
}

// ── 用户页（完成判定：列表渲染/confirm 流/注册开关/管理员行禁用）────────

describe("用户页走查", () => {
  it("列表渲染：三用户/禁用与管理员 tag/admin 行按钮禁用（1003 前置 UX）", async () => {
    const { wrapper } = await mountAdminPage(Users, createDefaultServerState(), "/users");
    const table = wrapper.find('[data-testid="users-table"]');
    expect(table.findAll(".el-table__row")).toHaveLength(3);
    expect(table.text()).toContain("admin");
    expect(table.text()).toContain("alice");
    expect(table.text()).toContain("bob"); // bob=已禁用（mock 初始态）
    // 内置管理员行：禁用/重置密码均禁用（服务端 400{1003} 的前端前置）
    expect(wrapper.find('[data-testid="users-toggle-admin"]').attributes("disabled")).toBeDefined();
    expect(wrapper.find('[data-testid="users-reset-admin"]').attributes("disabled")).toBeDefined();
    expect(wrapper.find('[data-testid="users-reset-alice"]').attributes("disabled")).toBeUndefined();
  });

  it("禁用 confirm 流：确认后 mock 态翻转+行变已禁用；已禁用行点击=直接启用（无 confirm）", async () => {
    const state = createDefaultServerState();
    const { wrapper } = await mountAdminPage(Users, state, "/users");

    // 禁用 alice：confirm 弹出（降级哑节点警示）→ 确认 → mock 翻转 + 行刷新
    await wrapper.find('[data-testid="users-toggle-alice"]').trigger("click");
    await vi.waitFor(() => expect(boxMessage()).toContain("哑节点"));
    await clickConfirm();
    expect(state.users.find((u) => u.username === "alice")?.disabled).toBe(true);
    await vi.waitFor(() =>
      expect(wrapper.find('[data-testid="users-table"]').findAll(".el-table__row")[1].text()).toContain("已禁用"),
    );

    // bob（已禁用）→ 点击=enable 直调（无弹窗）；先等 alice 的 confirm 完成隐藏再验"无新弹窗"
    await waitBoxesClosed();
    await wrapper.find('[data-testid="users-toggle-bob"]').trigger("click");
    await flushPromises();
    await flushPromises();
    expect(visibleBoxes()).toHaveLength(0);
    expect(state.users.find((u) => u.username === "bob")?.disabled).toBe(false);
    await vi.waitFor(() =>
      expect(wrapper.find('[data-testid="users-table"]').findAll(".el-table__row")[2].text()).toContain("正常"),
    );
  });

  it("重置密码 confirm 流：临时密码一次性 alert（固定值可见）", async () => {
    const state = createDefaultServerState();
    const { wrapper } = await mountAdminPage(Users, state, "/users");

    await wrapper.find('[data-testid="users-reset-alice"]').trigger("click");
    await vi.waitFor(() => expect(boxMessage()).toContain("仅显示一次"));
    await clickConfirm(); // 确认重置 → 响应 tempPassword → alert
    await vi.waitFor(() => expect(anyBoxTextIncludes("tmp-pw-x7k9")).toBe(true)); // 一次性密码可见
    await closeTopBox(); // 关闭一次性密码 alert
    await waitBoxesClosed();
  });

  it("注册开关：切换写 registration_open 键（PUT system/config）", async () => {
    const state = createDefaultServerState();
    const { wrapper } = await mountAdminPage(Users, state, "/users");

    // loadRegistration 定值前开关不渲染（ElSwitch 对 null 自纠偏 emit change(false) 的防误触，
    // 见 Users.vue 注释）——先等组件出现且为 true（"1"=开放）
    const sw = await vi.waitFor(() => {
      const c = wrapper.findComponent('[data-testid="users-registration-switch"]');
      expect(c.exists()).toBe(true);
      expect(c.props("modelValue")).toBe(true);
      return c;
    });
    sw.vm.$emit("change", false);
    await flushPromises();
    await flushPromises();
    expect(state.config.registration_open).toBe("0"); // mock 侧键已更新
    sw.vm.$emit("change", true);
    await flushPromises();
    await flushPromises();
    expect(state.config.registration_open).toBe("1");
  });

  it("分页：第 2 页请求 page=2（25 用户 → 首页 20 行/次页 5 行）", async () => {
    const state = createDefaultServerState();
    state.users = Array.from({ length: 25 }, (_, i) => ({
      id: `${ADMIN_ID.slice(0, 8)}-${i}`, username: `u${String(i + 1).padStart(2, "0")}`,
      disabled: false, isAdmin: false, deviceCount: 0, createdAt: "2026-09-01T00:00:00Z",
    }));
    const { wrapper } = await mountAdminPage(Users, state, "/users");
    expect(wrapper.find('[data-testid="users-table"]').findAll(".el-table__row")).toHaveLength(20);

    wrapper.findComponent(ElPagination).vm.$emit("current-change", 2);
    await flushPromises();
    const rows = wrapper.find('[data-testid="users-table"]').findAll(".el-table__row");
    expect(rows).toHaveLength(5);
    expect(wrapper.find('[data-testid="users-table"]').text()).toContain("u21");
  });
});

// ── 设备页（完成判定：列表渲染/解绑警示/重置码 alert/远程码复制）──────────

describe("设备页走查", () => {
  it("列表渲染：在线/离线 tag、双分组、远程码列", async () => {
    const { wrapper } = await mountAdminPage(Devices, createDefaultServerState(), "/devices");
    const table = wrapper.find('[data-testid="devices-table"]');
    expect(table.findAll(".el-table__row")).toHaveLength(2);
    const online = table.findAll('[data-testid="device-online"]');
    expect(online[0].text()).toBe("在线"); // A 机
    expect(online[1].text()).toBe("离线"); // B 机
    expect(table.text()).toContain("712152");
    expect(table.text()).toContain("办公室"); // B 机双分组
  });

  it("解绑 confirm 流：警示文案含“全新身份”→ 确认后行消失（mock 清理全集）", async () => {
    const state = createDefaultServerState();
    const { wrapper } = await mountAdminPage(Devices, state, "/devices");

    await wrapper.find('[data-testid="device-unbind-B 机"]').trigger("click");
    await vi.waitFor(() => expect(boxMessage()).toContain("全新身份"));
    await clickConfirm();
    expect(state.devices).toHaveLength(1); // B 机行已删
    expect(state.devices[0].deviceName).toBe("A 机");
    await vi.waitFor(() =>
      expect(wrapper.find('[data-testid="devices-table"]').findAll(".el-table__row")).toHaveLength(1),
    );
  });

  it("重置码 confirm 流：新码一次性 alert → 表格远程码列刷新", async () => {
    const state = createDefaultServerState();
    const { wrapper } = await mountAdminPage(Devices, state, "/devices");

    await wrapper.find('[data-testid="device-reset-A 机"]').trigger("click");
    await vi.waitFor(() => expect(boxMessage()).toContain("旧码立即失效"));
    await clickConfirm();
    await vi.waitFor(() => expect(anyBoxTextIncludes("NEW-CODE")).toBe(true)); // 新码一次性 alert
    await closeTopBox();
    await vi.waitFor(() =>
      expect(wrapper.find('[data-testid="devices-table"]').text()).toContain("NEW-CODE"),
    );
    expect(state.devices.find((d) => d.deviceName === "A 机")?.remoteCode).toBe("NEW-CODE");
  });

  it("远程码点击复制（clipboard 写入+成功提示；jsdom 无剪贴板时降级提示码值）", async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, "clipboard", { value: { writeText }, configurable: true });
    const { wrapper } = await mountAdminPage(Devices, createDefaultServerState(), "/devices");

    await wrapper.find('[data-testid="device-copy-code"]').trigger("click");
    await flushPromises();
    expect(writeText).toHaveBeenCalledWith("712152");
    await vi.waitFor(() =>
      expect([...document.querySelectorAll(".el-message")].some((m) => m.textContent?.includes("已复制远程码 712152"))),
    );
  });
});

// ── 分组页（完成判定：总览渲染/审批后队列清空/默认策略编辑）──────────────

describe("分组页走查", () => {
  it("总览渲染：默认组置顶 tag/两行/pendingCount；策略保存初始禁用（草稿=当前）", async () => {
    const { wrapper } = await mountAdminPage(Groups, createDefaultServerState(), "/groups");
    const table = wrapper.find('[data-testid="groups-table"]');
    expect(table.findAll(".el-table__row")).toHaveLength(2);
    expect(table.text()).toContain("默认"); // 默认分组 tag
    expect(table.text()).toContain("办公室");
    expect(wrapper.find('[data-testid="groups-policy-save"]').attributes("disabled")).toBeDefined();
    expect(wrapper.find('[data-testid="group-request-B 机"]').exists()).toBe(true); // 队列 1 条
  });

  it("批准申请：confirm 后队列清空+目标组 memberCount+1（走查闭环核心）", async () => {
    const state = createDefaultServerState();
    const { wrapper } = await mountAdminPage(Groups, state, "/groups");

    await wrapper.find('[data-testid="request-approve-B 机"]').trigger("click");
    await vi.waitFor(() => expect(boxMessage()).toContain("批准"));
    await clickConfirm();
    expect(state.groupRequests).toHaveLength(0); // 队列清空（mock 移单）
    const office = state.groups.find((g) => g.name === "办公室");
    expect(office?.memberCount).toBe(2); // 1 → 2
    expect(office?.pendingCount).toBe(0);
    await vi.waitFor(() =>
      expect(wrapper.find('[data-testid="groups-requests"]').text()).toContain("暂无待审批申请"),
    );
    // 总览行同步刷新（refresh 双拉）
    await vi.waitFor(() =>
      expect(wrapper.find('[data-testid="groups-table"]').text()).toContain("2"),
    );
  });

  it("拒绝申请：队列清空但 memberCount 不变", async () => {
    const state = createDefaultServerState();
    const { wrapper } = await mountAdminPage(Groups, state, "/groups");

    await wrapper.find('[data-testid="request-reject-B 机"]').trigger("click");
    await vi.waitFor(() => expect(boxMessage()).toContain("拒绝"));
    await clickConfirm();
    expect(state.groupRequests).toHaveLength(0);
    const office = state.groups.find((g) => g.name === "办公室");
    expect(office?.memberCount).toBe(1); // 不变
    expect(office?.pendingCount).toBe(0);
    await vi.waitFor(() =>
      expect(wrapper.find('[data-testid="groups-requests"]').text()).toContain("暂无待审批申请"),
    );
  });

  it("默认策略编辑：选 approval 后保存可点 → confirm → 双写生效（键+默认组行）", async () => {
    const state = createDefaultServerState();
    const { wrapper } = await mountAdminPage(Groups, state, "/groups");

    const select = wrapper.findAllComponents(ElSelect)[0]; // 页面首个 select=策略卡
    await select.vm.$emit("update:modelValue", "approval");
    await flushPromises();
    expect(wrapper.find('[data-testid="groups-policy-save"]').attributes("disabled")).toBeUndefined();

    await wrapper.find('[data-testid="groups-policy-save"]').trigger("click");
    await vi.waitFor(() => expect(boxMessage()).toContain("准入策略改为")); // message 正文（非 title）
    await clickConfirm();
    expect(state.config.default_join_policy).toBe("approval"); // 键（下次建组）
    expect(state.groups.find((g) => g.isDefault)?.joinPolicy).toBe("approval"); // 存量默认组行
    await vi.waitFor(() =>
      expect(wrapper.find('[data-testid="groups-policy"]').text()).toContain("需审批"),
    );
  });
});

// ── 映射页（完成判定：只读渲染/流量列/状态统计/双过滤联动）──────────────

describe("映射页走查", () => {
  it("只读渲染：三行/流量列 humanBytes/状态统计 chips 五态", async () => {
    const { wrapper } = await mountAdminPage(Mappings, createDefaultServerState(), "/mappings");
    const table = wrapper.find('[data-testid="mappings-table"]');
    expect(table.findAll(".el-table__row")).toHaveLength(3);
    expect(wrapper.find('[data-testid="mapping-bytes-网站"]').text()).toContain("1.0 MiB");
    expect(wrapper.find('[data-testid="mapping-bytes-网站"]').text()).toContain("2.0 MiB");
    const stats = wrapper.find('[data-testid="mappings-status-stats"]').text();
    expect(stats).toContain("直达 1");
    expect(stats).toContain("中继 1");
    expect(stats).toContain("未知 1");
    expect(table.text()).toContain("A 机（712152）"); // 归属设备（远程码）
    expect(table.text()).toContain("B 机 · self"); // 目标列（self 分支不带端口）
  });

  it("过滤联动：设备筛选 A 机 → 2 行；叠加状态 relay → 1 行（服务端同口径过滤）", async () => {
    const state = createDefaultServerState();
    const { wrapper } = await mountAdminPage(Mappings, state, "/mappings");
    const rows = () => wrapper.find('[data-testid="mappings-table"]').findAll(".el-table__row");
    expect(rows()).toHaveLength(3);

    const [deviceSelect, statusSelect] = wrapper.findAllComponents(ElSelect);
    await deviceSelect.vm.$emit("change", DEV_A);
    await flushPromises();
    expect(rows()).toHaveLength(2); // 网站+文件服务（归属 A 机）
    expect(state.mappings.length).toBe(3); // mock 全集不动（服务端过滤语义）

    await statusSelect.vm.$emit("change", "relay");
    await flushPromises();
    expect(rows()).toHaveLength(1);
    expect(wrapper.find('[data-testid="mappings-table"]').text()).toContain("文件服务");

    // 清空设备筛选（clearable → change(undefined)）：仅状态 relay 过滤
    await deviceSelect.vm.$emit("change", undefined);
    await flushPromises();
    expect(rows()).toHaveLength(1); // 仍只有文件服务（唯一 relay）
  });
});

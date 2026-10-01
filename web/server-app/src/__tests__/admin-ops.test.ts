// M3-11 运维页走查测试（任务清单完成判定：开关/限速保存与回显、config 校验错误显示、
// 审计过滤分页；另含会话表渲染+空态、18 键配置表+重启生效 tag、bool 键即时保存、
// newest-first 翻页）。装配同 admin-pages.test.ts（MSW+pinia/router/ElementPlus 独立实例；
// 弹窗按可见 overlay 过滤——ElMessageBox 关闭后 v-show 残留教训⑳）。
import { setupServer } from "msw/node";
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { createPinia, setActivePinia, type Pinia } from "pinia";
import type { Router } from "vue-router";
import ElementPlus from "element-plus";
import { createAppRouter } from "../router";
import Relay from "../pages/Relay.vue";
import System from "../pages/System.vue";
import { AUTH_KEY } from "../api";
import { createDefaultServerState, createServerHandlers, type MockServerState } from "../mocks/handlers";

const server = setupServer();
const live: VueWrapper[] = [];

beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterEach(() => {
  server.resetHandlers();
  for (const w of live.splice(0)) w.unmount();
  document.body.innerHTML = "";
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
  sessionStorage.setItem(AUTH_KEY, "1");
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

function visibleBoxes(): HTMLElement[] {
  return [...document.querySelectorAll<HTMLElement>(".el-message-box")].filter(
    (b) => (b.closest(".el-overlay") as HTMLElement | null)?.style.display !== "none",
  );
}

async function clickConfirm(): Promise<void> {
  const btn = await vi.waitFor(() => {
    const vis = visibleBoxes();
    const b = vis[vis.length - 1]?.querySelector<HTMLElement>(".el-message-box__btns .el-button--primary");
    expect(b).not.toBeNull();
    return b!;
  });
  btn.click();
  await flushPromises();
  await flushPromises();
}

function boxMessage(): string {
  const vis = visibleBoxes();
  return vis[vis.length - 1]?.querySelector(".el-message-box__message")?.textContent ?? "";
}

async function waitBoxesClosed(): Promise<void> {
  await vi.waitFor(() => expect(visibleBoxes().length).toBe(0), { timeout: 3000 });
  await flushPromises();
}

// ── 中继页（完成判定：开关/限速保存与回显）──────────────────────────────

describe("中继页走查", () => {
  it("配置卡+会话表渲染：开关 true/限速 0/会话行含两端承载与 humanBytes", async () => {
    const { wrapper } = await mountAdminPage(Relay, createDefaultServerState(), "/relay");
    const cfg = wrapper.find('[data-testid="relay-config"]');
    await vi.waitFor(() => {
      const sw = wrapper.findComponent('[data-testid="relay-switch"]');
      expect(sw.exists()).toBe(true); // config 定值后才渲染（ElSwitch null 教训）
      expect(sw.props("modelValue")).toBe(true);
    });
    expect(cfg.text()).toContain("0=不限速");
    const table = wrapper.find('[data-testid="relay-table"]');
    expect(table.findAll(".el-table__row")).toHaveLength(1);
    expect(table.text()).toContain("203.0.113.10:52001（tcp）"); // A 端承载
    expect(table.text()).toContain("pending"); // B 端未 JOIN
    expect(table.text()).toContain("1.0 MiB"); // 转发字节
  });

  it("开关 confirm 流：确认后 mock 键翻转+hint 变“存量不受杀”；取消则回读不动", async () => {
    const state = createDefaultServerState();
    const { wrapper } = await mountAdminPage(Relay, state, "/relay");
    const sw = await vi.waitFor(() => {
      const c = wrapper.findComponent('[data-testid="relay-switch"]');
      expect(c.exists()).toBe(true);
      return c;
    });

    sw.vm.$emit("change", false);
    await vi.waitFor(() => expect(boxMessage()).toContain("存量会话不受影响"));
    await clickConfirm();
    expect(state.config.relay_enabled).toBe("0");
    await vi.waitFor(() =>
      expect(wrapper.find('[data-testid="relay-config"]').text()).toContain("存量不受杀"),
    );

    // 取消分支：回读防漂移（mock 态不动）
    const sw2 = wrapper.findComponent('[data-testid="relay-switch"]');
    sw2.vm.$emit("change", true);
    await vi.waitFor(() => expect(visibleBoxes().length).toBeGreaterThan(0));
    (visibleBoxes()[visibleBoxes().length - 1]?.querySelector<HTMLElement>(".el-message-box__btns .el-button"))?.click(); // 取消按钮
    await flushPromises();
    await waitBoxesClosed();
    expect(state.config.relay_enabled).toBe("0"); // 未变
  });

  it("限速保存与回显：改 1024 → 保存可点 → PUT 回读 → 保存按钮复归禁用", async () => {
    const state = createDefaultServerState();
    const { wrapper } = await mountAdminPage(Relay, state, "/relay");
    const num = await vi.waitFor(() => {
      const c = wrapper.findComponent('[data-testid="relay-rate"]');
      expect(c.exists()).toBe(true);
      return c;
    });
    expect(wrapper.find('[data-testid="relay-rate-save"]').attributes("disabled")).toBeDefined(); // 草稿=现值

    await num.vm.$emit("update:modelValue", 1024);
    await flushPromises();
    expect(wrapper.find('[data-testid="relay-rate-save"]').attributes("disabled")).toBeUndefined();
    await wrapper.find('[data-testid="relay-rate-save"]').trigger("click");
    await flushPromises();
    expect(state.config.relay_rate_limit).toBe("1024");
    await vi.waitFor(() =>
      expect(wrapper.find('[data-testid="relay-rate-save"]').attributes("disabled")).toBeDefined(),
    );
  });

  it("会话空态：el-empty", async () => {
    const state = createDefaultServerState();
    state.relaySessions = [];
    const { wrapper } = await mountAdminPage(Relay, state, "/relay");
    await vi.waitFor(() =>
      expect(wrapper.find('[data-testid="relay-empty"]').exists()).toBe(true),
    );
    expect(wrapper.find('[data-testid="relay-table"]').exists()).toBe(false);
  });
});

// ── 系统页（完成判定：config 校验错误显示、审计过滤分页）────────────────

describe("系统页走查", () => {
  it("配置表 18 键渲染+重启生效 tag+bool 键即时保存", async () => {
    const state = createDefaultServerState();
    const { wrapper } = await mountAdminPage(System, state, "/system");
    const table = wrapper.find('[data-testid="system-table"]');
    await vi.waitFor(() => expect(table.findAll(".el-table__row")).toHaveLength(18));
    expect(table.text()).toContain("重启生效"); // public_addr 等 6 键
    expect(table.text()).toContain("中继服务"); // KEY_META 标签

    // bool 键即时存（无草稿）：relay_enabled 翻 false
    const sw = wrapper.findComponent('[data-testid="system-bool-relay_enabled"]');
    expect(sw.props("modelValue")).toBe(true);
    sw.vm.$emit("change", false);
    await flushPromises();
    await flushPromises();
    expect(state.config.relay_enabled).toBe("0");
  });

  it("文本键 dirty+保存+服务端校验错误显示（400{1001} message 如实透出）", async () => {
    const state = createDefaultServerState();
    const { wrapper } = await mountAdminPage(System, state, "/system");
    const input = await vi.waitFor(() => {
      const i = wrapper.find('[data-testid="system-text-public_addr"]');
      expect(i.exists()).toBe(true);
      return i;
    });
    const save = () => wrapper.find('[data-testid="system-save-public_addr"]');
    expect(save().attributes("disabled")).toBeDefined(); // 初始=现值（空）

    // public_addr 校验：IP 或域名（与 AdminSystemApi.Validators 同口径的 mock 简化）
    // el-input attrs 透传落内部 input（M1-32 教训）——data-testid 即在 input 上
    await input.setValue("bad addr!");
    await flushPromises();
    expect(save().attributes("disabled")).toBeUndefined(); // dirty
    await save().trigger("click");
    await flushPromises();
    await vi.waitFor(() =>
      expect([...document.querySelectorAll(".el-message")].some((m) =>
        m.textContent?.includes("public_addr")),
      ).toBe(true),
    );
    expect(state.config.public_addr).toBeUndefined(); // mock 态未动（整单拒绝）
  });

  it("审计过滤分页：25 行 → 首页 20/翻页 5（newest-first id 递减）/group_join 过滤 10 行", async () => {
    const state = createDefaultServerState();
    const { wrapper } = await mountAdminPage(System, state, "/system");
    const table = wrapper.find('[data-testid="audit-table"]');
    await vi.waitFor(() => expect(table.findAll(".el-table__row")).toHaveLength(20));
    expect(wrapper.find('[data-testid="system-audits"]').text()).toContain("共 25 条");

    // newest-first：首行 id=25（最大）
    expect(table.find(".el-table__row").text()).toContain("25");

    // 翻第 2 页：余 5 行、首行 id=5
    wrapper.findComponent('[data-testid="audit-pagination"]').vm.$emit("current-change", 2);
    await flushPromises();
    await vi.waitFor(() => expect(table.findAll(".el-table__row")).toHaveLength(5));
    expect(table.find(".el-table__row").text()).toContain("5");

    // event 过滤：group_join 25 行中出现 2/5 → 10 行（data-testid 透传在 input 上）
    await wrapper.find('[data-testid="audit-event-filter"]').setValue("group_join");
    await wrapper.find('[data-testid="audit-event-filter"]').trigger("change");
    await flushPromises();
    await vi.waitFor(() => expect(table.findAll(".el-table__row")).toHaveLength(10));
    expect(wrapper.find('[data-testid="system-audits"]').text()).toContain("共 10 条");
    // 过滤后首行 id=22（group_join 最新一条：i=22 → id 23？id=i+1，i%5∈{1,2}：22%5=2 ✓ id=23）
    expect(table.find(".el-table__row").text()).toContain("23");
  });
});

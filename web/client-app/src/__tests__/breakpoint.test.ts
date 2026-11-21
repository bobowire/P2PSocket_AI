// M8-04 移动适配单测（设计 13 §10.3）：useBreakpoint 两态 / 表格卡片双渲染 / 抽屉导航开合 /
// 弹窗 min() 宽度。matchMedia 桩驱动两态（happy-dom 原生实现恒 false，须桩）。
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { defineComponent, h } from "vue";
import { flushPromises, mount } from "@vue/test-utils";
import { dialogWidth, useBreakpoint } from "@p2p/ui-shared";
import { server, live, mountPage } from "./harness";
import { createDefaultState } from "../mocks/handlers";

beforeAll(() => server.listen({ onUnhandledRequest: "error" })); // pages.test.ts 同款生命周期

/** matchMedia 桩：matches 受控（useBreakpoint 在 onMounted 读取现值）。 */
function stubMatchMedia(mobile: boolean) {
  vi.stubGlobal("matchMedia", vi.fn().mockImplementation((query: string) => ({
    matches: mobile,
    media: query,
    onchange: null,
    addListener: vi.fn(),
    removeListener: vi.fn(),
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    dispatchEvent: vi.fn(() => true),
  })));
}

afterEach(() => {
  vi.unstubAllGlobals();
  server.resetHandlers();
  for (const w of live.splice(0)) w.unmount();
  document.body.innerHTML = "";
});
afterAll(() => server.close());

// ── useBreakpoint 组合式：两态 ────────────────────────────────────────

const Probe = defineComponent({
  setup() {
    const { isMobile, width } = useBreakpoint();
    return () => h("div", { "data-testid": "bp", "data-mobile": String(isMobile.value), "data-w": String(width.value) });
  },
});

describe("useBreakpoint", () => {
  it("小屏：matches=true → isMobile=true 且宽度上报", async () => {
    stubMatchMedia(true);
    const wrapper = mount(Probe);
    await flushPromises();
    expect(wrapper.find("[data-testid=bp]").attributes("data-mobile")).toBe("true");
    expect(Number(wrapper.find("[data-testid=bp]").attributes("data-w"))).toBeGreaterThan(0);
  });

  it("桌面：matches=false → isMobile=false", async () => {
    stubMatchMedia(false);
    const wrapper = mount(Probe);
    await flushPromises();
    expect(wrapper.find("[data-testid=bp]").attributes("data-mobile")).toBe("false");
  });
});

// ── 表格卡片双渲染（Mappings 页：桌面 el-table / 小屏卡片列表，桌面路径 DOM 不变） ──

describe("Mappings 表格卡片双渲染", () => {
  it("桌面：el-table 在位、卡片列表不渲染", async () => {
    stubMatchMedia(false);
    const { wrapper } = await mountPage((await import("../pages/Mappings.vue")).default, createDefaultState(), "/mappings");
    expect(wrapper.find('[data-testid="mappings-table"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="mappings-cards"]').exists()).toBe(false);
  });

  it("小屏：卡片列表在位、el-table 不渲染、关键操作保留", async () => {
    stubMatchMedia(true);
    const { wrapper } = await mountPage((await import("../pages/Mappings.vue")).default, createDefaultState(), "/mappings");
    expect(wrapper.find('[data-testid="mappings-cards"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="mappings-table"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="mappings-cards"] [data-testid="mapping-edit"]').exists()).toBe(true);
  });
});

// ── AdminLayout 抽屉导航开合（小屏）────────────────────────────────────

describe("AdminLayout 小屏抽屉导航", () => {
  it("小屏：汉堡开抽屉 → 点菜单项即收", async () => {
    stubMatchMedia(true);
    const { wrapper } = await mountPage((await import("../App.vue")).default, createDefaultState(), "/dashboard");
    expect(wrapper.find('[data-testid="mobile-menu-btn"]').exists()).toBe(true);
    expect(wrapper.find(".admin-layout .sidebar").exists()).toBe(false); // 桌面侧边栏不渲染

    const drawerEl = () => document.querySelector('[data-testid="mobile-nav-drawer"]') as HTMLElement | null;
    await wrapper.find('[data-testid="mobile-menu-btn"]').trigger("click");
    // el-drawer 根 class 随开关挂/摘 open（v-show 目标是 overlay 父层，class 判定更稳）
    await vi.waitFor(() => expect(drawerEl()?.className.includes("open")).toBe(true));
    // 点选菜单项（/devices）抽屉即收
    const item = drawerEl()?.querySelector('a[href="#/devices"]') as HTMLElement | null;
    expect(item).not.toBeNull();
    item!.click();
    await vi.waitFor(() => expect(drawerEl()?.className.includes("open")).toBe(false));
  });

  it("桌面：侧边栏常驻、无汉堡", async () => {
    stubMatchMedia(false);
    const { wrapper } = await mountPage((await import("../App.vue")).default, createDefaultState(), "/dashboard");
    expect(wrapper.find(".admin-layout .sidebar").exists()).toBe(true);
    expect(wrapper.find('[data-testid="mobile-menu-btn"]').exists()).toBe(false);
  });
});

// ── 弹窗宽度 min(Npx, 94vw)：小屏封顶、桌面原宽（CSS min() 与断点无关，一处表达） ──

describe("el-dialog 宽度 min() 封顶", () => {
  it("Groups 凭码入组弹窗宽度落 min(380px, 94vw)", async () => {
    stubMatchMedia(false);
    const { wrapper } = await mountPage((await import("../pages/Groups.vue")).default, { ...createDefaultState(), capability: "normal" }, "/groups");
    // 打开「凭码入组」弹窗（el-dialog 惰性渲染，须先触发；testid=pages.test.ts 既有口径）
    await wrapper.find('[data-testid="groups-join"]').trigger("click");
    await vi.waitFor(() => expect(document.querySelector('[data-testid="groups-join-dialog"]')).not.toBeNull());
    const dlg = document.querySelector('[data-testid="groups-join-dialog"]') as HTMLElement;
    // EP width prop → .el-dialog 行内 style（经 teleport 挂 body，从文档断言）
    const panel = dlg.classList.contains("el-dialog") ? dlg : (dlg.querySelector(".el-dialog") as HTMLElement);
    // EP 2.4+ width 落 --el-dialog-width CSS 变量（style 原文含 min() 表达式）
    await vi.waitFor(() => expect(panel.getAttribute("style") ?? "").toContain("min(380px, 94vw)"));
  });

  it("dialogWidth 纯函数口径", () => {
    expect(dialogWidth("560px")).toBe("min(560px, 94vw)");
  });
});

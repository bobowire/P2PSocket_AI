// M3-09 页面走查测试（任务清单完成判定：路由守卫/首登提示与改密流/指标卡渲染/折线数据变换；
// 另含 401 过期跳转与登出）。每用例装独立 MSW 处理器 + 独立 pinia/router/ElementPlus，
// 页面经真实 HTTP→store→渲染链路（client-app M1-32 同款装配）。
// 注意守卫判据含 sessionStorage 刷新标记——受保护页直挂用例须先置 AUTH_KEY，afterEach 统一清。
import { setupServer } from "msw/node";
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { createPinia, setActivePinia, type Pinia } from "pinia";
import { createAppRouter } from "../router";
import type { Router } from "vue-router";
import ElementPlus from "element-plus";
import App from "../App.vue";
import Dashboard from "../pages/Dashboard.vue";
import Login from "../pages/Login.vue";
import { AUTH_KEY } from "../api";
import { createDefaultServerState, createServerHandlers, type MockServerState } from "../mocks/handlers";
import { useDashboardStore } from "../stores/dashboard";
import { humanBytes, successChartGeom } from "../pages/punchChart";

const server = setupServer();
/** attachTo 挂载的页面（el-dialog 需文档流）逐用例拆净，防跨用例串台 */
const live: VueWrapper[] = [];

beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterEach(() => {
  server.resetHandlers();
  for (const w of live.splice(0)) w.unmount();
  document.body.innerHTML = "";
  sessionStorage.clear(); // AUTH_KEY 守卫标记逐用例隔离
});
afterAll(() => server.close());

interface Mounted {
  wrapper: VueWrapper;
  router: Router;
  pinia: Pinia;
}

async function mountPage(
  component: Parameters<typeof mount>[0],
  state: MockServerState = createDefaultServerState(),
  initialPath = "/login",
): Promise<Mounted> {
  server.use(...createServerHandlers(state));
  const pinia = createPinia();
  setActivePinia(pinia); // 守卫在导航期即调用 useAuthStore——挂载前须先激活
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

// ── 路由守卫（完成判定①）────────────────────────────────────────────

describe("路由守卫走查", () => {
  it("未登录访问受保护页 → 重定向 /login（登录表单渲染）", async () => {
    const { wrapper, router } = await mountPage(App, undefined, "/dashboard");
    expect(router.currentRoute.value.path).toBe("/login");
    expect(wrapper.find('[data-testid="login-form"]').exists()).toBe(true);
  });

  it("已登录（刷新标记）进后台 → 仪表盘加载 + 登出回登录页", async () => {
    const state = createDefaultServerState();
    state.authed = true;
    sessionStorage.setItem(AUTH_KEY, "1"); // 刷新存活标记通道（store 记忆通道由登录用例覆盖）
    const { wrapper, router } = await mountPage(App, state, "/dashboard");
    expect(router.currentRoute.value.path).toBe("/dashboard");
    expect(wrapper.find('[data-testid="dash-online"]').text()).toContain("3");

    await wrapper.find('[data-testid="app-logout"]').trigger("click");
    // 登出导航涉及懒加载路由 chunk（vite-node 冷转换可达秒级）——放宽轮询窗口
    await vi.waitFor(() => expect(router.currentRoute.value.path).toBe("/login"), { timeout: 5000 });
    await flushPromises();
    expect(wrapper.find('[data-testid="login-form"]').exists()).toBe(true);
  });
});

// ── 登录与改密流（完成判定②）────────────────────────────────────────

describe("登录走查", () => {
  it("首登（默认口令）：提示条 + 改密弹窗；旧密码错误拒绝，改密成功继续进仪表盘", async () => {
    const state = createDefaultServerState(); // admin/admin + mustChangePassword=true
    const { wrapper, router } = await mountPage(Login, state, "/login");

    await wrapper.find('input[data-testid="login-username"]').setValue("admin");
    await wrapper.find('input[data-testid="login-password"]').setValue("admin");
    await wrapper.find('[data-testid="login-submit"]').trigger("click");
    await flushPromises();

    // 首登提示条（FR-S-203）
    expect(wrapper.find('[data-testid="login-must-change"]').text()).toContain("默认口令");
    // 登录成功即弹改密弹窗（04 §3.1：成功不裁会话）
    const dialog = await vi.waitFor(() => {
      const d = document.querySelector('[data-testid="login-change-dialog"]');
      expect(d).not.toBeNull();
      return d!;
    });
    const [oldPw, newPw, confirmPw] = [...dialog.querySelectorAll("input[type=password]")];

    // 旧密码错误 → 401 → 错误提示，不进后台
    oldPw.value = "wrong-old";
    oldPw.dispatchEvent(new Event("input"));
    newPw.value = "newpass9";
    newPw.dispatchEvent(new Event("input"));
    confirmPw.value = "newpass9";
    confirmPw.dispatchEvent(new Event("input"));
    await flushPromises();
    (dialog.querySelector('[data-testid="login-change-save"]') as HTMLElement).click();
    await flushPromises();
    expect(
      [...document.querySelectorAll(".el-message")].some((m) => m.textContent?.includes("当前密码不正确")),
    ).toBe(true);
    expect(router.currentRoute.value.path).toBe("/login");

    // 正确旧密码 → 改密成功 → 会话保持继续进后台
    oldPw.value = "admin";
    oldPw.dispatchEvent(new Event("input"));
    await flushPromises();
    (dialog.querySelector('[data-testid="login-change-save"]') as HTMLElement).click();
    await flushPromises();
    expect(router.currentRoute.value.path).toBe("/dashboard");
    expect(state.adminPassword).toBe("newpass9"); // mock 侧已覆写（默认口令已换）
    expect(wrapper.find('[data-testid="login-must-change"]').exists()).toBe(false);
  });

  it("非首登：登录直达仪表盘（无提示条、无改密弹窗）", async () => {
    const state = createDefaultServerState();
    state.mustChangePassword = false;
    const { wrapper, router } = await mountPage(Login, state, "/login");

    await wrapper.find('input[data-testid="login-username"]').setValue("admin");
    await wrapper.find('input[data-testid="login-password"]').setValue("admin");
    await wrapper.find('[data-testid="login-submit"]').trigger("click");
    await flushPromises();

    expect(router.currentRoute.value.path).toBe("/dashboard");
    expect(wrapper.find('[data-testid="login-must-change"]').exists()).toBe(false);
    expect(document.querySelector('[data-testid="login-change-dialog"]')).toBeNull();
  });

  it("错误凭据 → 错误文案，不进入后台", async () => {
    const { wrapper, router } = await mountPage(Login, undefined, "/login");
    await wrapper.find('input[data-testid="login-username"]').setValue("admin");
    await wrapper.find('input[data-testid="login-password"]').setValue("wrong-pw");
    await wrapper.find('[data-testid="login-submit"]').trigger("click");
    await flushPromises();
    expect(wrapper.find('[data-testid="login-error"]').text()).toContain("用户名或密码错误");
    expect(router.currentRoute.value.path).toBe("/login");
  });
});

// ── 仪表盘（完成判定③）──────────────────────────────────────────────

describe("仪表盘走查", () => {
  it("六指标卡全量渲染（humanBytes/百分比/运行时长/丢弃三分桶）+ 折线空桶断段", async () => {
    const state = createDefaultServerState();
    state.authed = true;
    sessionStorage.setItem(AUTH_KEY, "1"); // 守卫放行（Dashboard 直挂）
    const { wrapper } = await mountPage(Dashboard, state, "/dashboard");

    expect(wrapper.find('[data-testid="dash-online"]').text()).toContain("3");
    expect(wrapper.find('[data-testid="dash-groups"]').text()).toContain("2");

    const mappings = wrapper.find('[data-testid="dash-mappings"]');
    expect(mappings.text()).toContain("4 / 5"); // enabled / total
    for (const s of ["直达：2", "中继：1", "失败：1", "失效：0", "未知：0"]) {
      expect(mappings.text()).toContain(s); // TD-22 byStatus 五态
    }

    const relay = wrapper.find('[data-testid="dash-relay"]');
    expect(relay.text()).toContain("1.0 MiB"); // humanBytes(1048576)
    expect(relay.text()).toContain("会话 1");
    expect(relay.text()).toContain("1h2m"); // fmtUptime(3725)

    const stun = wrapper.find('[data-testid="dash-stun"]');
    expect(stun.text()).toContain("1.5 QPS");
    expect(stun.text()).toContain("限速 12 / 认证 3 / 断路 0");

    const punch = wrapper.find('[data-testid="dash-punch"]');
    expect(punch.text()).toContain("90.0%");
    expect(punch.text()).toContain("40 次：直达 30 · 中继 6 · 失败 4");

    // 折线：hourly 首桶空 → 单段两点（数据变换细节由纯函数用例覆盖）
    const chart = wrapper.find('[data-testid="punch-chart"]');
    expect(chart.findAll("polyline").length).toBe(1);
    expect(chart.findAll("circle.dot").length).toBe(2);
  });

  it("会话失效（401）：拦截器清标记并跳登录", async () => {
    const state = createDefaultServerState();
    state.authed = true;
    sessionStorage.setItem(AUTH_KEY, "1");
    const { pinia } = await mountPage(Dashboard, state, "/dashboard");

    state.authed = false; // 服务端 Cookie 过期 → 下次轮询 401
    const dashboard = useDashboardStore(pinia);
    await dashboard.refresh();
    await vi.waitFor(() => expect(location.hash).toBe("#/login"));
    expect(sessionStorage.getItem(AUTH_KEY)).toBeNull();
  });
});

// ── 折线数据变换（完成判定④，纯函数直测）────────────────────────────

describe("successChartGeom 数据变换", () => {
  it("等距坐标 + 首桶空不参与连线（单段两点）", () => {
    const g = successChartGeom(
      [
        { hourStart: 1, total: 0, success: 0 }, // 空桶
        { hourStart: 2, total: 10, success: 9 }, // 0.9
        { hourStart: 3, total: 20, success: 15 }, // 0.75
      ],
      560,
      120,
      4,
    );
    // n=3 → step=276：x = 4 / 280 / 556；y = 116 - 112×rate
    expect(g.polylines).toEqual(["280.0,15.2 556.0,32.0"]);
    expect(g.dots.length).toBe(2);
    expect(g.dots[0].cx).toBe(280);
    expect(g.dots[0].cy).toBeCloseTo(15.2, 5);
    expect(g.dots[1].cx).toBe(556);
    expect(g.dots[1].cy).toBeCloseTo(32, 5);
  });

  it("中置空桶切断连线（两段各一点）；全空/空集 → 无段无点", () => {
    const g1 = successChartGeom([
      { hourStart: 1, total: 10, success: 10 }, // 100% 贴满线
      { hourStart: 2, total: 0, success: 0 },
      { hourStart: 3, total: 5, success: 5 },
    ]);
    expect(g1.polylines).toEqual(["4.0,4.0", "556.0,4.0"]);
    expect(g1.dots.length).toBe(2);

    const g2 = successChartGeom([
      { hourStart: 1, total: 0, success: 0 },
      { hourStart: 2, total: 0, success: 0 },
    ]);
    expect(g2.polylines).toEqual([]);
    expect(g2.dots).toEqual([]);

    expect(successChartGeom([])).toEqual({ polylines: [], dots: [] });
  });

  it("humanBytes 量级分档", () => {
    expect(humanBytes(512)).toBe("512 B");
    expect(humanBytes(2048)).toBe("2.0 KiB");
    expect(humanBytes(1048576)).toBe("1.0 MiB");
    expect(humanBytes(5 * 1024 * 1024 * 1024)).toBe("5.00 GiB");
  });
});

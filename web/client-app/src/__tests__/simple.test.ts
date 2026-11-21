// 简易版走查测试（FR-C-810 双模式 / FR-C-811 引导式新建，2026-10-20）：
// 基架复用 __tests__/harness.ts（与 pages.test.ts 同源，防复制漂移）。
// 覆盖：/ → /simple 默认重定向、SimpleLayout 顶栏、概览三卡（含 WS 速率注入）、
// 映射卡列表（反查/连接地址/失败卡/停用确认）、向导四步全流程（create→enable 两连调）、
// 离线设备禁选、分组行+凭码入组（free/3002/3001 内联）。
import { afterEach, beforeAll, afterAll, describe, expect, it } from "vitest";
import { flushPromises } from "@vue/test-utils";
import { createRouter, createWebHashHistory } from "vue-router";
import { defineComponent, h } from "vue";
import { RouterView } from "vue-router";
import { routes } from "../router";
import { createDefaultState } from "../mocks/handlers";
import { useMappingStore } from "../stores/mappings";
import { useSimpleThemeClass } from "../theme";
import { server, live, mountPage, clickMessageBox } from "./harness";

// 挂载根=<RouterView/>+主题类 composable（模拟 App.vue 渲染链）：手动直挂 SimpleLayout
// 会让其内层 RouterView 缺 depth 注入而把布局自身再渲染一层（实测 6 tab 重复）。
// 主题类管理 2026-11-21 上收 useSimpleThemeClass（App.vue 同款接线），Root 同走生产实现。
const Root = defineComponent({
  setup() {
    useSimpleThemeClass();
    return () => h(RouterView);
  },
});

beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterEach(() => {
  server.resetHandlers();
  for (const w of live.splice(0)) w.unmount();
  document.body.innerHTML = "";
});
afterAll(() => server.close());

/** normal 态（默认 mock 是 passive——设备列表 2002，反查/向导须登录态）。 */
function normalState() {
  const state = createDefaultState();
  state.username = "demo";
  state.capability = "normal";
  return state;
}

/** el-dialog 关闭后 DOM 常驻（overlay v-show）——按 display:none 判可见性（存在即真）。 */
function dialogShown(tid: string): boolean {
  const el = document.querySelector(`[data-testid="${tid}"]`) as HTMLElement | null;
  if (!el) return false;
  const hidden = el.style.display.includes("none") || !!el.closest('[style*="display: none"]');
  return !hidden;
}

// ── 路由与布局 ─────────────────────────────────────────────────────

describe("简易版路由与布局", () => {
  it("/ 默认重定向 /simple；高级版 /dashboard 原样可达", async () => {
    const router = createRouter({ history: createWebHashHistory(), routes });
    await router.push("/");
    await router.isReady();
    expect(router.currentRoute.value.path).toBe("/simple");

    await router.push("/dashboard");
    expect(router.currentRoute.value.path).toBe("/dashboard");
    expect(router.currentRoute.value.matched[0]!.components!.default).toBeDefined();
  });

  it("顶栏三 tab（精确激活）+ 高级版入口 + 品牌回首页", async () => {
    const { wrapper } = await mountPage(Root, normalState(), "/simple/mappings");

    const tabs = wrapper.findAll(".tab");
    expect(tabs.map((t) => t.text())).toEqual(["概览", "端口映射", "分组"]);
    // 前缀匹配下 /simple 不应同时激活——仅当前 tab 精确激活
    const active = tabs.filter((t) => t.classes().includes("router-link-exact-active"));
    expect(active.map((t) => t.text())).toEqual(["端口映射"]);

    const advanced = wrapper.find('[data-testid="simple-advanced-link"]');
    expect(advanced.attributes("href")).toBe("#/dashboard");
    expect(advanced.text()).toBe("高级版");
    expect(wrapper.find(".brand").attributes("href")).toBe("#/simple");
  });
});

// ── 概览 ───────────────────────────────────────────────────────────

describe("简易版·概览", () => {
  it("本机卡（设备名/远程码可复制/在线）+ 映射计数（失败标红可点）", async () => {
    const { wrapper } = await mountPage(Root, normalState(), "/simple");

    const device = wrapper.find('[data-testid="simple-overview-device"]');
    expect(device.text()).toContain("本机（web-dev）");
    expect(device.text()).toContain("在线");
    expect(wrapper.find('[data-testid="simple-overview-code"]').text()).toContain("a1b2c3");

    const maps = wrapper.find('[data-testid="simple-overview-mappings"]');
    expect(maps.text()).toContain("共 3 条");
    expect(maps.text()).toContain("直连 1");
    expect(maps.text()).toContain("中继 1");
    expect(maps.text()).toContain("打洞中 0");
    expect(maps.text()).toContain("停用 0");
    const failed = wrapper.find('[data-testid="simple-overview-failed"]');
    expect(failed.attributes("href")).toBe("#/simple/mappings");
    expect(failed.text()).toContain("失败 1");
  });

  it("实时速率卡：WS rates 注入求和；无数据示空态", async () => {
    const { wrapper, pinia } = await mountPage(Root, normalState(), "/simple");
    expect(wrapper.find('[data-testid="simple-overview-rate"]').text()).toContain("暂无数据");

    const mappings = useMappingStore(pinia);
    mappings.rates.set("44444444-4444-4444-8444-444444444444", {
      rateUp: 2048, rateDown: 4096, path: "direct",
    });
    mappings.rates.set("66666666-6666-4666-8666-666666666666", {
      rateUp: 1024, rateDown: 0, path: "relay",
    });
    await flushPromises();
    const rate = wrapper.find('[data-testid="simple-overview-rate"]');
    expect(rate.text()).toContain("3.0 KiB/s"); // ↑ 2048+1024
    expect(rate.text()).toContain("4.0 KiB/s"); // ↓ 4096+0
  });

  it("关于项目卡（FR-C-812）：介绍（AI 重构口径）/仓库链接/原项目链接/QQ 群可复制/关于页入口", async () => {
    const { wrapper } = await mountPage(Root, normalState(), "/simple");

    const about = wrapper.find('[data-testid="simple-overview-about"]');
    expect(about.text()).toContain("关于项目");
    expect(about.text()).toContain("内网穿透"); // 介绍关键词（projectInfo.ts 同源）
    const repo = wrapper.find('[data-testid="simple-overview-repo"]');
    expect(repo.attributes("href")).toBe("https://github.com/bobowire/P2PSocket_AI");
    const originRepo = wrapper.find('[data-testid="simple-overview-origin-repo"]');
    expect(originRepo.attributes("href")).toBe("https://github.com/bobowire/Wireboy.Socket.P2PSocket");
    expect(wrapper.find('[data-testid="simple-overview-qq"]').text()).toContain("417159195");
    expect(wrapper.find('[data-testid="simple-overview-about"] .about-link').attributes("href"))
      .toBe("#/about"); // 更多信息与时间轴 → 高级版关于页
  });
});

// ── 端口映射 ───────────────────────────────────────────────────────

describe("简易版·端口映射", () => {
  it("卡片渲染：连接地址/设备名反查/失败中文原因+重试；哑节点横幅+新建禁用", async () => {
    const { wrapper } = await mountPage(Root, normalState(), "/simple/mappings");

    const card = wrapper.find('[data-testid="map-card-44444444-4444-4444-8444-444444444444"]');
    expect(card.text()).toContain("办公室 web");
    expect(card.text()).toContain("办公室主机（d4e5f6）"); // /api/devices 反查
    expect(card.find('[data-testid="map-addr-44444444-4444-4444-8444-444444444444"]').text())
      .toContain("100.64.0.2:18080"); // 虚拟IP:本地端口（FR-C-810 调研定稿）

    const fail = wrapper.find('[data-testid="map-fail-55555555-5555-4555-8555-555555555555"]');
    expect(fail.text()).toContain("打洞超时"); // detailText 中文化（共享 mappingDetail.ts）
    expect(fail.find('[data-testid="map-retry-55555555-5555-4555-8555-555555555555"]').exists())
      .toBe(true);
  });

  it("哑节点：横幅在场、新建禁用、设备反查回落裸码", async () => {
    const { wrapper } = await mountPage(Root, undefined, "/simple/mappings");
    expect(wrapper.find('[data-testid="passive-banner"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="simple-mappings-new"]').attributes("disabled")).toBeDefined();
    expect(wrapper.find('[data-testid="map-card-44444444-4444-4444-8444-444444444444"]').text())
      .toContain("d4e5f6");
    expect(wrapper.find('[data-testid="map-card-44444444-4444-4444-8444-444444444444"]').text())
      .not.toContain("办公室主机（");
  });

  it("停用开关带二次确认；确认后状态转已禁用", async () => {
    const { wrapper } = await mountPage(Root, normalState(), "/simple/mappings");

    const card = wrapper.find('[data-testid="map-card-44444444-4444-4444-8444-444444444444"]');
    const switchEl = card.find('[data-testid="map-switch-44444444-4444-4444-8444-444444444444"]');
    await switchEl.trigger("click");
    await clickMessageBox("停用确认", "confirm");
    await flushPromises();

    expect(wrapper.find('[data-testid="map-card-44444444-4444-4444-8444-444444444444"]').text())
      .toContain("已禁用");
  });
});

// ── 引导式新建（FR-C-811）─────────────────────────────────────────

describe("简易版·引导式新建映射", () => {
  it("四步全流程：选设备→快捷端口→名称自动→完成并启用（create+enable 两连调）", async () => {
    const { wrapper } = await mountPage(Root, normalState(), "/simple/mappings?create=1");

    // ?create=1 自动开向导
    expect(dialogShown("simple-wizard")).toBe(true);

    // 步 1：选设备（ElSelect 组件树 emit——pages.test.ts 既有模式）
    const select = wrapper.findComponent({ name: "ElSelect" });
    await select.vm.$emit("update:modelValue", "d4e5f6");
    await flushPromises();
    await wrapper.find('[data-testid="simple-wizard-next"]').trigger("click");

    // 步 2：快捷 chip「22 SSH」→ 目标端口 22/tcp（本地端口镜像联动）
    await wrapper.find('[data-testid="simple-wizard-quick-22"]').trigger("click");
    await flushPromises();
    await wrapper.find('[data-testid="simple-wizard-next"]').trigger("click");

    // 步 3：名称自动「设备名-端口」、本地端口已镜像 22
    const nameInput = wrapper.find('[data-testid="simple-wizard-name"]');
    expect((nameInput.element as HTMLInputElement).value).toBe("办公室主机-22");
    const local = wrapper.findComponent({ name: "ElInputNumber" });
    expect(local.props("modelValue")).toBe(22);
    await wrapper.find('[data-testid="simple-wizard-next"]').trigger("click");

    // 步 4：摘要 + 完成并启用 → create(disabled) → enable(punching)，列表出现新卡
    const summary = wrapper.find('[data-testid="simple-wizard-summary"]');
    expect(summary.text()).toContain("办公室主机");
    expect(summary.text()).toContain("100.64.0.2:22");
    await wrapper.find('[data-testid="simple-wizard-finish"]').trigger("click");
    await flushPromises();

    expect(dialogShown("simple-wizard")).toBe(false); // 成功关窗（overlay 隐藏）
    const texts = wrapper.findAll(".map-card").map((c) => c.text());
    const created = texts.find((t) => t.includes("办公室主机-22"));
    expect(created).toBeDefined();
    expect(created).toContain("打洞中"); // enable 后 mock 态 punching
  });

  it("设备下拉：选项显示「设备名（远程码）」，离线设备禁选", async () => {
    const { wrapper } = await mountPage(Root, normalState(), "/simple/mappings");

    await wrapper.find('[data-testid="simple-mappings-new"]').trigger("click");
    await flushPromises();

    // EP select 菜单常驻挂载（persistent）——组件树直查 ElOption props（不必开下拉）
    const options = wrapper.findAllComponents({ name: "ElOption" });
    expect(options).toHaveLength(3);
    const byLabel = Object.fromEntries(options.map((o) => [String(o.props("label")), o]));
    expect(Object.keys(byLabel)).toContain("办公室主机（d4e5f6）");
    expect(byLabel["本机（web-dev）（a1b2c3）"]!.props("disabled")).toBe(false);
    expect(byLabel["办公室主机（d4e5f6）"]!.props("disabled")).toBe(false);
    expect(byLabel["家里 NAS（0a1b2c）"]!.props("disabled")).toBe(true); // 离线禁选
    // 步 1 下一步门禁：未选设备时禁用
    expect(wrapper.find('[data-testid="simple-wizard-next"]').attributes("disabled")).toBeDefined();
  });
});

// ── 分组 ───────────────────────────────────────────────────────────

describe("简易版·分组", () => {
  it("行式列表：组名/成员数/角色 tag；所有者待审入口跳高级版", async () => {
    const { wrapper } = await mountPage(Root, normalState(), "/simple/groups");

    const rows = wrapper.findAll('[data-testid^="group-row-"]');
    expect(rows).toHaveLength(2);
    expect(rows[0]!.text()).toContain("默认分组");
    expect(rows[0]!.text()).toContain("3 名成员");
    expect(rows[0]!.text()).toContain("成员");
    expect(rows[1]!.text()).toContain("项目协作组");
    expect(rows[1]!.text()).toContain("所有者");
    const pending = wrapper.find('[data-testid="simple-groups-pending"]');
    expect(pending.attributes("href")).toBe("#/groups");
    expect(pending.text()).toContain("待审 1");
  });

  it("凭码入组：free 直入（成员数+1）；approval 3002 转待审批提示；无效码 3001 内联", async () => {
    const state = normalState();
    state.groups[0]!.inviteCode = "ab2cde"; // free 组可直入
    state.groups[1]!.inviteCode = "c3d4e5"; // approval 组 → 3002
    const { wrapper } = await mountPage(Root, state, "/simple/groups");

    // free 直入
    await wrapper.find('[data-testid="simple-groups-join"]').trigger("click");
    await flushPromises();
    await wrapper.find('[data-testid="simple-join-code"]').setValue("ab2cde");
    await wrapper.find('[data-testid="simple-join-submit"]').trigger("click");
    await flushPromises();
    expect(dialogShown("simple-join-dialog")).toBe(false);
    expect(wrapper.findAll('[data-testid^="group-row-"]')[0]!.text()).toContain("4 名成员");

    // 无效码 → 3001 内联（弹窗不关）
    await wrapper.find('[data-testid="simple-groups-join"]').trigger("click");
    await flushPromises();
    await wrapper.find('[data-testid="simple-join-code"]').setValue("zzzzz9");
    await wrapper.find('[data-testid="simple-join-submit"]').trigger("click");
    await flushPromises();
    expect(wrapper.find('[data-testid="simple-join-error"]').exists()).toBe(true);
    expect(dialogShown("simple-join-dialog")).toBe(true);

    // approval 组 → 3002 → info 提示并关窗（申请单已建）
    await wrapper.find('[data-testid="simple-groups-join"]').trigger("click");
    await flushPromises();
    await wrapper.find('[data-testid="simple-join-code"]').setValue("c3d4e5");
    await wrapper.find('[data-testid="simple-join-submit"]').trigger("click");
    await flushPromises();
    expect(dialogShown("simple-join-dialog")).toBe(false);
  });
});

// ── 科技感主题（2026-10-20 用户指令"简易版要有科技感"） ─────────────────

describe("简易版科技感主题", () => {
  it("进入简易版挂 dark+simple-tech，切高级版即摘除还原浅色", async () => {
    const cl = document.documentElement.classList;
    const { router } = await mountPage(Root, normalState(), "/simple");
    expect(cl.contains("dark")).toBe(true);
    expect(cl.contains("simple-tech")).toBe(true);

    await router.push("/dashboard");
    await flushPromises();
    expect(cl.contains("dark")).toBe(false);
    expect(cl.contains("simple-tech")).toBe(false);
  });
});

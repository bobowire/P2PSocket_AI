// M1-32 页面走查测试（任务清单完成判定：MSW 全页面走查通过）。
// 每用例装独立 MSW 处理器 + 独立 pinia/router/ElementPlus，页面经真实 HTTP→store→渲染链路。
import { setupServer } from "msw/node";
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { createPinia, type Pinia } from "pinia";
import { createRouter, createWebHashHistory, type Router } from "vue-router";
import ElementPlus from "element-plus";
import Dashboard from "../pages/Dashboard.vue";
import SubnetConflictBanner from "../components/SubnetConflictBanner.vue";
import Devices from "../pages/Devices.vue";
import Groups from "../pages/Groups.vue";
import Login from "../pages/Login.vue";
import Logs from "../pages/Logs.vue";
import Mappings from "../pages/Mappings.vue";
import Segments from "../pages/Segments.vue";
import Settings from "../pages/Settings.vue";
import Upgrade from "../pages/Upgrade.vue";
import Wizard from "../pages/Wizard.vue";
import { routes } from "../router";
import { createDefaultState, createHandlers, type MockState } from "../mocks/handlers";
import { useSystemStore } from "../stores/system";

const server = setupServer();
/** attachTo 挂载的页面（el-table/el-drawer 需文档流）逐用例拆净，防跨用例串台 */
const live: VueWrapper[] = [];

beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterEach(() => {
  server.resetHandlers();
  for (const w of live.splice(0)) w.unmount();
  document.body.innerHTML = "";
});
afterAll(() => server.close());

interface Mounted {
  wrapper: VueWrapper;
  router: Router;
  pinia: Pinia;
}

async function mountPage(
  component: Parameters<typeof mount>[0],
  state: MockState = createDefaultState(),
  initialPath = "/",
): Promise<Mounted> {
  server.use(...createHandlers(state));
  const router = createRouter({ history: createWebHashHistory(), routes });
  await router.push(initialPath);
  await router.isReady();
  const pinia = createPinia();
  const wrapper = mount(component, {
    global: { plugins: [pinia, ElementPlus, router] },
    attachTo: document.body, // el-drawer/el-table 需真实文档流
  });
  live.push(wrapper);
  await flushPromises();
  return { wrapper, router, pinia };
}

// ── 仪表盘 ─────────────────────────────────────────────────────────

describe("Dashboard 走查", () => {
  it("本机信息卡三要素 + 登录态 + 隧道概览计数 + 速率图", async () => {
    const { wrapper } = await mountPage(Dashboard);

    const info = wrapper.find('[data-testid="dash-info"]');
    expect(info.text()).toContain("100.64.0.2");
    expect(info.text()).toContain("a1b2c3");
    expect(info.text()).toContain("在线");
    expect(info.text()).toContain("未登录（哑节点）");

    const overview = wrapper.find('[data-testid="dash-overview"]');
    expect(overview.text()).toContain("直达");
    expect(overview.text()).toContain("失败");
    expect(overview.text()).toContain("已禁用");
    expect(overview.text()).toContain("共 3 条映射");

    expect(wrapper.find('[data-testid="rate-chart"]').exists()).toBe(true);
  });

  it("已登录态信息卡展示账号名", async () => {
    const state = createDefaultState();
    state.username = "demo";
    state.capability = "normal";
    const { wrapper } = await mountPage(Dashboard, state);
    expect(wrapper.find('[data-testid="dash-info"]').text()).toContain("已登录：demo");
  });

  it("M3-12 流量汇总卡：总计 humanBytes+设备维聚合行+CSV 导出 window.open", async () => {
    const { wrapper } = await mountPage(Dashboard);
    const stats = await vi.waitFor(() => {
      const c = wrapper.find('[data-testid="dash-stats"]');
      expect(c.exists()).toBe(true);
      return c;
    });

    // 总计=映射维全量和（1 MiB+512 KiB 上行 / 2 MiB+512 KiB 下行 / 512 KiB 中继）
    expect(wrapper.find('[data-testid="dash-stats-up"]').text()).toBe("1.5 MiB");
    expect(stats.text()).toContain("2.5 MiB");
    expect(stats.text()).toContain("512 KiB");

    // 设备维（目标远程码字典序）：0a1b2c 一条零流量在前、d4e5f6 两映射聚合在后
    const rows = wrapper.findAll('[data-testid="dash-stats-devices"] .el-table__row');
    expect(rows).toHaveLength(2);
    expect(rows[0]!.text()).toContain("0a1b2c");
    expect(rows[1]!.text()).toContain("d4e5f6");
    expect(rows[1]!.text()).toContain("1.5 MiB / 2.5 MiB / 512 KiB");

    // 导出入口：同源新窗口直下（服务端 text/csv 附件，/logs 导出同模式）
    const openSpy = vi.spyOn(window, "open").mockImplementation(() => null);
    await wrapper.find('[data-testid="dash-stats-export"]').trigger("click");
    expect(openSpy).toHaveBeenCalledWith("/api/stats/export?format=csv", "_blank");
    openSpy.mockRestore();
  });
});

// ── 向导 ───────────────────────────────────────────────────────────

describe("Wizard 走查", () => {
  function unregisteredState() {
    const state = createDefaultState();
    state.phase = "unregistered";
    state.deviceId = null;
    state.remoteCode = null;
    state.virtualIp = null;
    return state;
  }

  it("① 连通性测试反馈与下一步门禁；空必填注册被表单校验拦截", async () => {
    const { wrapper } = await mountPage(Wizard, unregisteredState(), "/wizard");

    expect(wrapper.find('[data-testid="wizard-steps"]').exists()).toBe(true);
    const addr = wrapper.find('input[data-testid="wizard-addr"]');
    await addr.setValue("127.0.0.1:1");
    await wrapper.find('[data-testid="wizard-test"]').trigger("click");
    await flushPromises();
    expect(wrapper.find('[data-testid="wizard-test-result"]').text()).toContain("连接失败");
    expect(wrapper.find('[data-testid="wizard-next"]').attributes("disabled")).toBeDefined();

    await addr.setValue("203.0.113.10:7000");
    await wrapper.find('[data-testid="wizard-test"]').trigger("click");
    await flushPromises();
    expect(wrapper.find('[data-testid="wizard-test-result"]').text()).toContain("连接成功");
    expect(wrapper.find('[data-testid="wizard-next"]').attributes("disabled")).toBeUndefined();

    // ② 进入分组方式步，选 account；③ 空必填 → 校验拦截，不出结果页
    await wrapper.find('[data-testid="wizard-next"]').trigger("click");
    await flushPromises();
    const radios = wrapper.findAll('[data-testid="wizard-mode"] input');
    expect(radios.length).toBe(3);
    await radios[2].setValue(); // account
    await flushPromises();
    await wrapper.find('[data-testid="wizard-register"]').trigger("click");
    await flushPromises();
    expect(wrapper.find('[data-testid="wizard-enter"]').exists()).toBe(false);

    // 密码 5 位（< 6）→ 服务端口径 1001 的前端同款规则拦截
    const textInputs = wrapper
      .findAll("input")
      .filter((i) => (i.attributes("type") ?? "text") === "text");
    await textInputs[0].setValue("short-pw-user");
    await wrapper.find('input[type="password"]').setValue("12345");
    await textInputs[textInputs.length - 1].setValue("走查机");
    await wrapper.find('[data-testid="wizard-register"]').trigger("click");
    // 校验态 UI 经 refDebounced 100ms 才显错（Element Plus 行为）——轮询等待
    await vi.waitFor(() =>
      expect(wrapper.find(".el-form-item.is-error").text()).toContain("密码至少 6 位"));
    expect(wrapper.find('[data-testid="wizard-enter"]').exists()).toBe(false);
  });

  it("② account 模式填全注册 → 结果页展示三要素", async () => {
    const { wrapper } = await mountPage(Wizard, unregisteredState(), "/wizard");

    // ① 测试可达
    await wrapper.find('input[data-testid="wizard-addr"]').setValue("203.0.113.10:7000");
    await wrapper.find('[data-testid="wizard-test"]').trigger("click");
    await flushPromises();
    // ② 下一步 + 选 account
    await wrapper.find('[data-testid="wizard-next"]').trigger("click");
    await flushPromises();
    const radios = wrapper.findAll('[data-testid="wizard-mode"] input');
    await radios[2].setValue();
    await flushPromises();

    // 用户名/设备名（text 输入）+ 密码（password 输入）
    const textInputs = wrapper
      .findAll("input")
      .filter((i) => (i.attributes("type") ?? "text") === "text");
    await textInputs[0].setValue("wizard-user"); // 用户名（account 段首个 text 输入）
    await wrapper.find('input[type="password"]').setValue("secret123");
    await textInputs[textInputs.length - 1].setValue("走查机"); // 设备名
    await wrapper.find('[data-testid="wizard-register"]').trigger("click");
    await flushPromises();

    // ③ 结果：CodeText 远程码 + 虚拟 IP + 分组 + 进入控制台
    const done = wrapper.find(".done");
    expect(done.exists()).toBe(true);
    expect(done.text()).toContain("7g8h9j"); // mock 下发的远程码
    expect(done.text()).toContain("100.64.0.2");
    expect(done.text()).toContain("我的分组");
    expect(done.find('[data-testid="wizard-enter"]').exists()).toBe(true);
  });
});

// ── 登录 ───────────────────────────────────────────────────────────

describe("Login 走查", () => {
  it("未登录：注册新号并自动登录 → 已登录态 + 登出回哑节点", async () => {
    const state = createDefaultState();
    const { wrapper } = await mountPage(Login, state);

    expect(wrapper.find('[data-testid="login-form"]').exists()).toBe(true);
    // 切注册页签
    const tabs = wrapper.findAll(".el-tabs__item");
    await tabs[1].trigger("click");
    await flushPromises();

    const textInputs = wrapper.findAll('[data-testid="login-form"] input')
      .filter((i) => (i.attributes("type") ?? "text") === "text");
    await textInputs[0].setValue("new-user");
    await wrapper.find('input[data-testid="login-password"]').setValue("secret123");
    await wrapper.find('[data-testid="login-submit"]').trigger("click");
    await flushPromises();

    // 注册+登录成功 → profile 视图
    expect(wrapper.find('[data-testid="login-username"]').text()).toContain("new-user");
    expect(wrapper.find('[data-testid="login-logout"]').exists()).toBe(true);

    // 登出 → 回表单
    await wrapper.find('[data-testid="login-logout"]').trigger("click");
    await flushPromises();
    expect(wrapper.find('[data-testid="login-form"]').exists()).toBe(true);
  });

  it("错误密码 2001 → 错误文案、不进入已登录态", async () => {
    const { wrapper } = await mountPage(Login);
    const textInputs = wrapper.findAll('[data-testid="login-form"] input')
      .filter((i) => (i.attributes("type") ?? "text") === "text");
    await textInputs[0].setValue("demo");
    await wrapper.find('input[data-testid="login-password"]').setValue("wrong-pw");
    await wrapper.find('[data-testid="login-submit"]').trigger("click");
    await flushPromises();
    expect(wrapper.find('[data-testid="login-error"]').text()).toContain("未登录或凭据失效");
    expect(wrapper.find('[data-testid="login-profile"]').exists()).toBe(false);
  });
});

// ── 设备发现 ───────────────────────────────────────────────────────

describe("Devices 走查", () => {
  it("可见设备表：在线点/分组/远程码/开放网段 + 建映射跳转带 remoteCode", async () => {
    const state = createDefaultState();
    state.capability = "normal"; // /api/devices 属主动类
    const { wrapper, router } = await mountPage(Devices, state, "/devices");

    const table = wrapper.find('[data-testid="devices-table"]');
    expect(table.text()).toContain("本机（web-dev）");
    expect(table.text()).toContain("办公室主机");
    expect(table.text()).toContain("家里 NAS");
    expect(table.text()).toContain("192.168.1.0/24");
    expect(table.text()).toContain("默认分组");

    const create = wrapper.findAll('[data-testid="devices-create-mapping"]');
    expect(create.length).toBe(3);
    await create[1].trigger("click"); // 办公室主机 d4e5f6
    await flushPromises();
    expect(router.currentRoute.value.path).toBe("/mappings");
    expect(router.currentRoute.value.query.remoteCode).toBe("d4e5f6");
  });

  it("哑节点：建映射禁用 + 横幅显隐即时切换（M1-33，06 §2/05 §8）", async () => {
    // /api/devices 属主动类——passive 列表本身拉不到；故先以 normal 态加载，
    // 再把 system store 置为 passive（等价登录态被服务端降级/0x75），断言写按钮全部置灰
    const state = createDefaultState();
    state.capability = "normal";
    const { wrapper, pinia } = await mountPage(Devices, state, "/devices");
    expect(wrapper.findAll('[data-testid="devices-create-mapping"]').length).toBe(3);

    const system = useSystemStore(pinia);
    system.device = { ...system.device!, capability: "passive" };
    await flushPromises();
    const create = wrapper.findAll('[data-testid="devices-create-mapping"]');
    for (const btn of create) expect(btn.attributes("disabled")).toBeDefined();
    expect(wrapper.find('[data-testid="passive-banner"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="passive-banner"]').text()).toContain("哑节点模式");

    // 重新登录恢复 normal → 横幅消失、写操作恢复（login_state → refetch 同链路）
    system.device = { ...system.device!, capability: "normal" };
    await flushPromises();
    expect(wrapper.find('[data-testid="passive-banner"]').exists()).toBe(false);
    for (const btn of create) expect(btn.attributes("disabled")).toBeUndefined();
  });
});

// ── 端口映射 ───────────────────────────────────────────────────────

describe("Mappings 走查", () => {
  it("列表状态 Tag/目标/速率占位；重试仅 failed 行出现", async () => {
    const state = createDefaultState();
    state.capability = "normal";
    const { wrapper } = await mountPage(Mappings, state, "/mappings");

    const table = wrapper.find('[data-testid="mappings-table"]');
    expect(table.text()).toContain("办公室 web");
    expect(table.text()).toContain("d4e5f6 → self:80");
    // 无 WS：速率列占位"—"
    expect(wrapper.find('[data-testid="mapping-rate"]').exists()).toBe(false);

    const tags = wrapper.findAll('[data-testid="mapping-state"]');
    expect(tags.map((t) => t.attributes("data-state"))).toEqual(["direct", "failed", "relay"]);
    expect(wrapper.findAll('[data-testid="mapping-retry"]').length).toBe(1); // 仅 failed
  });

  it("新建抽屉：remoteCode query 预填 + 保存走 POST 成功入列表", async () => {
    const state = createDefaultState();
    state.capability = "normal";
    const { wrapper } = await mountPage(Mappings, state, "/mappings?remoteCode=d4e5f6");

    await wrapper.find('[data-testid="mappings-new"]').trigger("click");
    await flushPromises();
    // 抽屉 teleport 到 body；el-input 的透传 attrs（data-testid）落在内部 input 上
    const drawer = document.querySelector('[data-testid="mapping-drawer"]');
    expect(drawer).not.toBeNull();
    const codeInput = drawer!.querySelector("input[data-testid=mapping-remote-code]");
    expect((codeInput as HTMLInputElement).value).toBe("d4e5f6");

    const nameInput = drawer!.querySelector("input[data-testid=mapping-name]") as HTMLInputElement;
    nameInput.value = "走查新映射";
    nameInput.dispatchEvent(new Event("input"));
    await flushPromises();
    await (drawer!.querySelector('[data-testid="mapping-save"]') as HTMLElement).click();
    await flushPromises();

    expect(wrapper.find('[data-testid="mappings-table"]').text()).toContain("走查新映射");
  });

  it("停用/启用/删除操作经真实端点生效", async () => {
    const state = createDefaultState();
    state.capability = "normal";
    const { wrapper } = await mountPage(Mappings, state, "/mappings");

    // 停用 direct 行（第一行 enabled=true）
    await wrapper.find('[data-testid="mapping-disable"]').trigger("click");
    await flushPromises();
    const tags = wrapper.findAll('[data-testid="mapping-state"]');
    expect(tags[0].attributes("data-state")).toBe("disabled");

    // 删除（ElMessageBox confirm → 确认按钮）
    await wrapper.find('[data-testid="mapping-delete"]').trigger("click");
    await flushPromises();
    const confirm = document.querySelector(".el-message-box__btns .el-button--primary");
    (confirm as HTMLElement).click();
    await flushPromises();
    expect(wrapper.findAll('[data-testid="mapping-state"]').length).toBe(2);
  });
});

// ── passive 模式（M1-33）───────────────────────────────────────────

describe("passive 模式走查（M1-33）", () => {
  it("mappings：能力切换即时翻转——写操作置灰/恢复 + 横幅显隐；只读刷新不受限", async () => {
    const state = createDefaultState();
    state.capability = "normal";
    const { wrapper, pinia } = await mountPage(Mappings, state, "/mappings");

    // normal：无横幅、写操作可用
    expect(wrapper.find('[data-testid="passive-banner"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="mappings-new"]').attributes("disabled")).toBeUndefined();

    // 降级 passive（等价 login_state 事件 → /api/device refetch 写回 store，05 §8）
    const system = useSystemStore(pinia);
    system.device = { ...system.device!, capability: "passive" };
    await flushPromises();
    expect(wrapper.find('[data-testid="passive-banner"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="passive-banner"]').text()).toContain("哑节点模式");
    for (const tid of ["mappings-new", "mapping-enable", "mapping-disable", "mapping-retry", "mapping-edit", "mapping-delete"]) {
      for (const btn of wrapper.findAll(`[data-testid="${tid}"]`)) {
        expect(btn.attributes("disabled")).toBeDefined();
      }
    }
    expect(wrapper.find('[data-testid="mappings-refresh"]').attributes("disabled")).toBeUndefined();

    // 重新登录恢复 normal（0x21 → capability 翻转）→ 即时恢复
    system.device = { ...system.device!, capability: "normal" };
    await flushPromises();
    expect(wrapper.find('[data-testid="passive-banner"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="mappings-new"]').attributes("disabled")).toBeUndefined();
  });

  it("groups passive：写操作全置灰 + 横幅；GET 2002 → 列表空态", async () => {
    const state = createDefaultState();
    state.capability = "passive";
    const { wrapper } = await mountPage(Groups, state, "/groups");

    expect(wrapper.find('[data-testid="groups-join"]').attributes("disabled")).toBeDefined();
    expect(wrapper.find('[data-testid="groups-create"]').attributes("disabled")).toBeDefined();
    expect(wrapper.find('[data-testid="passive-banner"]').exists()).toBe(true);
    // /api/groups 属主动类：passive 2002 → 列表拉取失败保留空态（05 §8）
    expect(wrapper.find('[data-testid="groups-table"]').text()).toContain("暂未加入任何分组");
    // 只读刷新不受限
    expect(wrapper.find('[data-testid="groups-refresh"]').attributes("disabled")).toBeUndefined();
  });
});

// ── 分组全量（M2-28，替换 M1-33 占位）─────────────────────────────

describe("Groups 全量走查（M2-28）", () => {
  function normalState() {
    const state = createDefaultState();
    state.capability = "normal"; // 分组族主动类
    return state;
  }

  /** 按文本定位确认弹窗并点确定（前序弹窗关闭动画未摘除时避免点错按钮）。 */
  async function confirmBox(marker: string) {
    await vi.waitFor(() => {
      expect(
        [...document.querySelectorAll(".el-message-box")].find((b) => b.textContent?.includes(marker)),
      ).toBeDefined();
    });
    const hit = [...document.querySelectorAll(".el-message-box")]
      .find((b) => b.textContent?.includes(marker))!;
    (hit.querySelector(".el-message-box__btns .el-button--primary") as HTMLElement).click();
  }

  it("已加入列表（策略/成员数/角色）+ 审批队列聚合；批准 → 成员数+1 且队列清空", async () => {
    const { wrapper } = await mountPage(Groups, normalState(), "/groups");

    const table = wrapper.find('[data-testid="groups-table"]');
    expect(table.text()).toContain("默认分组");
    expect(table.text()).toContain("项目协作组");
    expect(table.text()).toContain("自由加入");
    expect(table.text()).toContain("需审批");
    expect(table.text()).toContain("所有者");
    expect(table.text()).toContain("成员");
    // 仅所有者行有操作组按钮；成员行为退组
    expect(wrapper.findAll('[data-testid="groups-dissolve"]').length).toBe(1);
    expect(wrapper.findAll('[data-testid="groups-leave"]').length).toBe(1);

    // 审批队列（所有者侧聚合）：申请设备 + 归属分组名
    const requests = wrapper.find('[data-testid="groups-requests"]');
    expect(requests.text()).toContain("家里 NAS");
    expect(requests.text()).toContain("项目协作组");

    // 批准 → memberCount 2→3、申请出队（卡片隐藏）
    await wrapper.find('[data-testid="groups-approve"]').trigger("click");
    await flushPromises();
    expect(wrapper.find('[data-testid="groups-requests-card"]').exists()).toBe(false);
    const rows = wrapper.findAll('[data-testid="groups-table"] .el-table__row');
    expect(rows[1].text()).toContain("项目协作组");
    expect(rows[1].text()).toContain("3");
  });

  it("凭码入组：approval 组 3002 → 待审批提示 + 申请入队；free 组即时入组", async () => {
    const state = normalState();
    state.groups[0].inviteCode = "XY9876"; // 默认分组（free）
    state.groups[1].inviteCode = "AB2345"; // 项目协作组（approval）
    const { wrapper } = await mountPage(Groups, state, "/groups");

    await wrapper.find('[data-testid="groups-join"]').trigger("click");
    await flushPromises();
    const dialog = document.querySelector('[data-testid="groups-join-dialog"]')!;
    const input = dialog.querySelector("input[data-testid=groups-join-code]") as HTMLInputElement;

    // approval 组：3002 原码透传 → 前端按"待审批"提示（非错误）
    input.value = "AB2345";
    input.dispatchEvent(new Event("input"));
    await flushPromises();
    (dialog.querySelector('[data-testid="groups-join-save"]') as HTMLElement).click();
    await flushPromises();
    expect(
      [...document.querySelectorAll(".el-message")].some((m) => m.textContent?.includes("等待所有者")),
    ).toBe(true);
    // mock 侧申请入队（演示态与真实 0x51 建单同构）
    expect(wrapper.findAll('[data-testid="groups-requests"] .el-table__row').length).toBe(2);

    // free 组：即时入组（memberCount 3→4）
    await wrapper.find('[data-testid="groups-join"]').trigger("click");
    await flushPromises();
    const dialog2 = document.querySelector('[data-testid="groups-join-dialog"]')!;
    const input2 = dialog2.querySelector("input[data-testid=groups-join-code]") as HTMLInputElement;
    input2.value = "XY9876";
    input2.dispatchEvent(new Event("input"));
    await flushPromises();
    (dialog2.querySelector('[data-testid="groups-join-save"]') as HTMLElement).click();
    await flushPromises();
    const rows = wrapper.findAll('[data-testid="groups-table"] .el-table__row');
    expect(rows[0].text()).toContain("4");
  });

  it("新建分组（准入策略单选）入列表", async () => {
    const { wrapper } = await mountPage(Groups, normalState(), "/groups");

    await wrapper.find('[data-testid="groups-create"]').trigger("click");
    await flushPromises();
    const dialog = document.querySelector('[data-testid="groups-create-dialog"]')!;
    const name = dialog.querySelector("input[data-testid=groups-name]") as HTMLInputElement;
    name.value = "走查新组";
    name.dispatchEvent(new Event("input"));
    await flushPromises();
    (dialog.querySelector('[data-testid="groups-create-save"]') as HTMLElement).click();
    await flushPromises();

    const rows = wrapper.findAll('[data-testid="groups-table"] .el-table__row');
    expect(rows.length).toBe(3);
    expect(rows[2].text()).toContain("走查新组");
    expect(rows[2].text()).toContain("所有者"); // 创建者即所有者
  });

  it("所有者操作：邀请码生成/撤销、编辑改名；成员行退组、所有者解散", async () => {
    const { wrapper } = await mountPage(Groups, normalState(), "/groups");

    // 邀请码：打开即生成（覆盖式），撤销后空态
    await wrapper.find('[data-testid="groups-invite"]').trigger("click");
    await flushPromises();
    const dialog = document.querySelector('[data-testid="groups-invite-dialog"]')!;
    expect(dialog.querySelector('[data-testid="groups-invite-code"]')?.textContent ?? "").toMatch(/[0-9A-Z]{6}/);
    (dialog.querySelector('[data-testid="groups-invite-revoke"]') as HTMLElement).click();
    await flushPromises();
    expect(document.querySelector('[data-testid="groups-invite-code"]')).toBeNull();

    // 编辑改名（0x55）
    await wrapper.find('[data-testid="groups-edit"]').trigger("click");
    await flushPromises();
    const edit = document.querySelector('[data-testid="groups-edit-dialog"]')!;
    const name = edit.querySelector("input[data-testid=groups-edit-name]") as HTMLInputElement;
    name.value = "项目组-改";
    name.dispatchEvent(new Event("input"));
    await flushPromises();
    (edit.querySelector('[data-testid="groups-edit-save"]') as HTMLElement).click();
    await flushPromises();
    expect(wrapper.find('[data-testid="groups-table"]').text()).toContain("项目组-改");

    // 成员行退组（0x52）→ 行消失
    await wrapper.find('[data-testid="groups-leave"]').trigger("click");
    await confirmBox("退组确认");
    await flushPromises();
    expect(wrapper.find('[data-testid="groups-table"]').text()).not.toContain("默认分组");

    // 所有者解散（0x56）→ 行消失
    await wrapper.find('[data-testid="groups-dissolve"]').trigger("click");
    await confirmBox("解散确认");
    await flushPromises();
    expect(wrapper.find('[data-testid="groups-table"]').text()).not.toContain("项目组-改");
  });

  it("成员管理（所有者）：同组可见设备列出（排除本机）+ 移出确认生效", async () => {
    const state = normalState();
    state.groups[0].isOwner = true; // 默认分组 → 所有者视角（成员：本机+办公室主机）
    const { wrapper } = await mountPage(Groups, state, "/groups");

    await wrapper.find('[data-testid="groups-members"]').trigger("click");
    await flushPromises();
    const dialog = document.querySelector('[data-testid="groups-members-dialog"]')!;
    const table = dialog.querySelector('[data-testid="groups-members-table"]')!;
    expect(table.textContent).toContain("办公室主机"); // 同组成员（可见设备维度）
    expect(table.textContent).not.toContain("本机"); // 所有者自身不可移出

    (dialog.querySelector('[data-testid="groups-kick"]') as HTMLElement).click();
    await flushPromises();
    (document.querySelector(".el-message-box__btns .el-button--primary") as HTMLElement).click();
    await flushPromises();
    expect(
      document.querySelector('[data-testid="groups-members-table"]')?.textContent,
    ).not.toContain("办公室主机");
    // 主表成员数 3→2（0x57 联动）
    expect(wrapper.findAll('[data-testid="groups-table"] .el-table__row')[0].text()).toContain("2");
  });
});

// ── 网段（M2-28）──────────────────────────────────────────────────

describe("Segments 走查（M2-28）", () => {
  it("列表/新增（裸 IP 规范化 /32）/非法校验/移除确认 + 即时生效提示", async () => {
    const { wrapper } = await mountPage(Segments, undefined, "/segments");

    expect(wrapper.text()).toContain("变更即时生效");
    expect(wrapper.find('[data-testid="segments-table"]').text()).toContain("192.168.1.0/24");

    // 非法输入 → 规则拦截（与服务端 NormalizeCidr 同口径）
    await wrapper.find('input[data-testid="segments-cidr"]').setValue("not-a-cidr");
    await wrapper.find('[data-testid="segments-add"]').trigger("click");
    await vi.waitFor(() =>
      expect(wrapper.find(".el-form-item.is-error").text()).toContain("地址部分"));
    expect(wrapper.findAll('[data-testid="segments-table"] .el-table__row').length).toBe(1);

    // 裸 IP → 服务端规范化补 /32
    await wrapper.find('input[data-testid="segments-cidr"]').setValue("10.0.0.5");
    await wrapper.find('[data-testid="segments-add"]').trigger("click");
    await flushPromises();
    expect(wrapper.find('[data-testid="segments-table"]').text()).toContain("10.0.0.5/32");

    // 移除（confirm）→ 行数回落
    await wrapper.findAll('[data-testid="segments-delete"]')[0].trigger("click");
    await flushPromises();
    (document.querySelector(".el-message-box__btns .el-button--primary") as HTMLElement).click();
    await flushPromises();
    expect(wrapper.findAll('[data-testid="segments-table"] .el-table__row').length).toBe(1);
  });
});

// ── 设备中继回退 + 仪表盘远程码重置 + 映射 UDP/目标地址（M2-28）─────

describe("Devices 中继回退开关（M2-28，D3 v0.4）", () => {
  it("开关现值随列表拉取；切换 → PUT 生效", async () => {
    const state = createDefaultState();
    state.capability = "normal";
    state.peers = { "22222222-2222-4222-8222-222222222222": true };
    const { wrapper } = await mountPage(Devices, state, "/devices");

    const switches = wrapper.findAllComponents({ name: "ElSwitch" });
    expect(switches.length).toBe(3);
    await vi.waitFor(() => expect(switches[1].props("modelValue")).toBe(true)); // 现值异步拉取

    await switches[0].vm.$emit("change", true); // 开启本机回退
    await flushPromises();
    expect(switches[0].props("modelValue")).toBe(true);
  });
});

describe("Dashboard 远程码重置（M2-28，0x14）", () => {
  it("确认弹窗 → 新码展示 + 信息卡更新（本机管理类 passive 亦可达）", async () => {
    const { wrapper } = await mountPage(Dashboard); // 默认 passive 演示态
    await wrapper.find('[data-testid="dash-reset-code"]').trigger("click");
    await flushPromises();
    (document.querySelector(".el-message-box__btns .el-button--primary") as HTMLElement).click();

    // 成功 alert 异步打开（mock 生成 6 位新码）；旧 confirm 关闭动画期间可能仍在 DOM——按文本定位
    await vi.waitFor(() => {
      expect(
        [...document.querySelectorAll(".el-message-box")].find((b) =>
          b.textContent?.match(/新远程码：[0-9a-z]{6}/)),
      ).toBeDefined();
    });
    const hit = [...document.querySelectorAll(".el-message-box")]
      .find((b) => b.textContent?.match(/新远程码：[0-9a-z]{6}/))!;
    (hit.querySelector(".el-message-box__btns .el-button--primary") as HTMLElement).click();
    await flushPromises();

    // 信息卡远程码已换新（≠初始 a1b2c3）
    expect(wrapper.find('[data-testid="dash-info"]').text()).not.toContain("a1b2c3");
  });
});

describe("Mappings UDP 与目标地址（M2-28）", () => {
  it("UDP 单选可选；目标地址 IP 模式 + 开放网段提示；保存带 targetAddr/proto", async () => {
    const state = createDefaultState();
    state.capability = "normal";
    const { wrapper } = await mountPage(Mappings, state, "/mappings?remoteCode=d4e5f6");

    await wrapper.find('[data-testid="mappings-new"]').trigger("click");
    await flushPromises();
    const drawer = document.querySelector('[data-testid="mapping-drawer"]')!;

    // UDP 不再禁用（M2-20 引擎已落地）；抽屉 radioGroup 经组件树定位（teleport 不影响）
    const radios = wrapper.findAllComponents({ name: "ElRadioGroup" });
    expect(radios.length).toBeGreaterThanOrEqual(2); // 协议 + 目标模式
    await radios[0].vm.$emit("update:modelValue", "udp");
    await radios[1].vm.$emit("update:modelValue", "ip");
    await flushPromises();

    const addr = drawer.querySelector("input[data-testid=mapping-target-addr]") as HTMLInputElement;
    expect(addr).not.toBeNull();
    addr.value = "192.168.1.50";
    addr.dispatchEvent(new Event("input"));
    await flushPromises();

    // 白名单网段提示：d4e5f6=办公室主机 开放 192.168.1.0/24
    await vi.waitFor(() =>
      expect(drawer.querySelector('[data-testid="mapping-seg-hint"]')?.textContent).toContain("192.168.1.0/24"));

    const nameInput = drawer.querySelector("input[data-testid=mapping-name]") as HTMLInputElement;
    nameInput.value = "走查 UDP 映射";
    nameInput.dispatchEvent(new Event("input"));
    await flushPromises();
    (drawer.querySelector('[data-testid="mapping-save"]') as HTMLElement).click();
    await flushPromises();

    const table = wrapper.find('[data-testid="mappings-table"]').text();
    expect(table).toContain("UDP :8080"); // proto=udp（本地监听列）
    expect(table).toContain("192.168.1.50:80"); // targetAddr=IP（目标列）
  });
});

// ── 日志 / 升级 / 改密（M2-29）─────────────────────────────────────

describe("Logs 走查（M2-29，FR-C-807）", () => {
  it("newest-first 列表 + 级别过滤 + 分页 total + 导出带 level（同源新窗口直下）", async () => {
    const { wrapper } = await mountPage(Logs, undefined, "/logs");

    // mock 服务端倒序：最新（09:12 ERR 打洞失败）在最前
    const rows = wrapper.findAll('[data-testid="logs-table"] .el-table__row');
    expect(rows.length).toBe(4);
    expect(rows[0].text()).toContain("NAS ssh");
    expect(rows[0].text()).toContain("ERR");
    expect(wrapper.find('[data-testid="logs-table"]').text()).toContain("punch_timeout");
    expect(wrapper.findComponent({ name: "ElPagination" }).props("total")).toBe(4);

    // 级别过滤（el-select 经组件树 emit；值=04 §2.6 token）
    await wrapper.findComponent({ name: "ElSelect" }).vm.$emit("update:modelValue", "error");
    await flushPromises();
    expect(wrapper.findAll('[data-testid="logs-table"] .el-table__row').length).toBe(1);
    expect(wrapper.find('[data-testid="logs-table"]').text()).not.toContain("心跳");

    // 导出：携带当前级别的同源下载（服务端 text/plain 附件）
    const openSpy = vi.spyOn(window, "open").mockImplementation(() => null);
    await wrapper.find('[data-testid="logs-export"]').trigger("click");
    expect(openSpy).toHaveBeenCalledWith(expect.stringContaining("/api/logs/export?level=error"), "_blank");
    openSpy.mockRestore();
  });
});

describe("Upgrade 走查（M2-29，FR-C-904）", () => {
  it("版本不符路径（?reason=version）警示 + UpdateInfo 全量渲染", async () => {
    const state = createDefaultState();
    state.upgrade = {
      latestVersion: "0.4.2",
      minProtocol: 1,
      maxProtocol: 2,
      upgradeUrl: "https://example.com/p2p-client-0.4.2.msi",
      notes: "修复打洞回切竞态；协议 v2 双向兼容。",
    };
    const { wrapper } = await mountPage(Upgrade, state, "/upgrade?reason=version");

    expect(wrapper.find('[data-testid="upgrade-reason-alert"]').text()).toContain("协议版本不兼容");
    expect(wrapper.find('[data-testid="upgrade-version"]').text()).toContain("0.4.2");
    expect(wrapper.find('[data-testid="upgrade-protocol"]').text()).toContain("1 ~ 2");
    expect(wrapper.find('[data-testid="upgrade-url"]').attributes("href"))
      .toBe("https://example.com/p2p-client-0.4.2.msi");
    expect(wrapper.find('[data-testid="upgrade-notes"]').text()).toContain("打洞回切");
  });

  it("常规直达（无 reason）：无警示条，信息卡照常", async () => {
    const { wrapper } = await mountPage(Upgrade, undefined, "/upgrade");
    expect(wrapper.find('[data-testid="upgrade-reason-alert"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="upgrade-version"]').text()).toContain("0.3.0");
  });
});

describe("Login 修改密码（M2-29，0x23）", () => {
  it("改密成功 → 登出回表单 → 旧密码 2001 拒绝、新密码登录成功", async () => {
    const state = createDefaultState();
    state.username = "demo";
    state.capability = "normal";
    const { wrapper } = await mountPage(Login, state, "/login");

    expect(wrapper.find('[data-testid="login-profile"]').exists()).toBe(true);
    await wrapper.find('[data-testid="login-change-password"]').trigger("click");
    await flushPromises();
    const dialog = document.querySelector('[data-testid="login-change-dialog"]')!;
    const [oldPw, newPw, confirmPw] = [...dialog.querySelectorAll("input[type=password]")];
    oldPw.value = "secret123";
    oldPw.dispatchEvent(new Event("input"));
    newPw.value = "newpass9";
    newPw.dispatchEvent(new Event("input"));
    confirmPw.value = "newpass9";
    confirmPw.dispatchEvent(new Event("input"));
    await flushPromises();
    (dialog.querySelector('[data-testid="login-change-save"]') as HTMLElement).click();
    await flushPromises();

    // 0x23 成功 → 主动登出引导重新登录（表单回归）
    expect(wrapper.find('[data-testid="login-form"]').exists()).toBe(true);

    // 旧密码被拒（mock 已覆写 users.demo）
    const userInput = wrapper.findAll('[data-testid="login-form"] input')
      .filter((i) => (i.attributes("type") ?? "text") === "text")[0];
    await userInput.setValue("demo");
    await wrapper.find('input[data-testid="login-password"]').setValue("secret123");
    await wrapper.find('[data-testid="login-submit"]').trigger("click");
    await flushPromises();
    expect(wrapper.find('[data-testid="login-error"]').text()).toContain("未登录或凭据失效");

    // 新密码登录成功 → 回已登录态
    await wrapper.find('input[data-testid="login-password"]').setValue("newpass9");
    await wrapper.find('[data-testid="login-submit"]').trigger("click");
    await flushPromises();
    expect(wrapper.find('[data-testid="login-profile"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="login-username"]').text()).toContain("demo");
  });
});

// ── 网段冲突告警条（M3-13，FR-C-204）──────────────────────────────

describe("网段冲突告警条（M3-13）", () => {
  it("conflict 有值 → 渲染网段+明细+调整指引；解除（null）→ 消失", async () => {
    const { wrapper, pinia } = await mountPage(SubnetConflictBanner);
    const system = useSystemStore(pinia);

    // 等价 /api/system/state 返回 conflict（App 链：subnet_conflict 事件 → refetch 写 store）
    system.state = {
      phase: "running",
      serverReachable: true,
      protocolVersion: 1,
      conflict: {
        subnet: "100.64.0.0/24",
        items: [
          { kind: "address", value: "100.64.0.50", interface: "eth0" },
          { kind: "route", value: "100.64.0.0/24", interface: "corp0" },
        ],
      },
    };
    await flushPromises();

    const banner = wrapper.find('[data-testid="conflict-banner"]');
    expect(banner.exists()).toBe(true);
    expect(banner.text()).toContain("100.64.0.0/24");
    expect(banner.text()).toContain("地址 100.64.0.50（eth0）");
    expect(banner.text()).toContain("路由 100.64.0.0/24（corp0）");
    expect(banner.text()).toContain("建议管理员在服务端调整网段");
    expect(banner.text()).toContain("自动消失");

    // 解除（服务端调整 virtual_subnet / 本机网络变化后现场重算返回 null）→ 告警条消失
    system.state = { ...system.state!, conflict: null };
    await flushPromises();
    expect(wrapper.find('[data-testid="conflict-banner"]').exists()).toBe(false);
  });
});

// ── 设置 ───────────────────────────────────────────────────────────
describe("Settings 走查", () => {
  it("加载现值；保存 → 即时生效提示；改 Web 端口 → 重启提示", async () => {
    const { wrapper } = await mountPage(Settings, undefined, "/settings");

    // 现值加载（serverAddrs 候选 + 滑块值）
    const addrInput = wrapper.find('[data-testid="settings-addrs"] input');
    expect((addrInput.element as HTMLInputElement).value).toBe("127.0.0.1:7101");
    const slider = wrapper.findComponent({ name: "ElSlider" });
    expect(slider.props("modelValue")).toBe(3);

    await wrapper.find('[data-testid="settings-save"]').trigger("click");
    await flushPromises();
    expect(wrapper.find('[data-testid="settings-restart-hint"]').exists()).toBe(false);

    // 改 Web 端口 → restartRequired
    const webport = wrapper.findComponent({ name: "ElInputNumber" });
    await webport.vm.$emit("update:modelValue", 7200);
    await flushPromises();
    await wrapper.find('[data-testid="settings-save"]').trigger("click");
    await flushPromises();
    expect(wrapper.find('[data-testid="settings-restart-hint"]').exists()).toBe(true);
  });

  it("增删候选地址；空候选集不可保存", async () => {
    const state = createDefaultState();
    state.capability = "normal";
    const { wrapper } = await mountPage(Settings, state, "/settings");

    const before = wrapper.findAll('[data-testid="settings-addrs"] input').length;
    await wrapper.find('[data-testid="settings-add-addr"]').trigger("click");
    await flushPromises();
    expect(wrapper.findAll('[data-testid="settings-addrs"] input').length).toBe(before + 1);

    // 删到空 → 保存禁用（serverAddrs 至少一项）
    for (const btn of wrapper.findAll('[data-testid="settings-addrs"] button')
      .filter((b) => b.text() === "删除")) {
      await btn.trigger("click");
      await flushPromises();
    }
    expect(wrapper.find('[data-testid="settings-save"]').attributes("disabled")).toBeDefined();
  });

  it("M3-15 stun-test 判型卡：两桶族标签+TCP 分配规律+注记渲染", async () => {
    const { wrapper } = await mountPage(Settings, undefined, "/settings");

    await wrapper.find('[data-testid="stun-run"]').trigger("click");
    await flushPromises();

    const card = wrapper.find('[data-testid="stun-result"]');
    expect(card.exists()).toBe(true);
    expect(card.text()).toContain("203.0.113.10:51234");
    expect(card.text()).toContain("对称（ADM/APDM）");
    expect(card.text()).toContain("受限（ADF/APDF）");
    expect(card.text()).toContain("顺序递增（端口预测适用）");
    expect(card.text()).toContain("4321 ms");
    expect(wrapper.find('[data-testid="stun-downgraded"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="stun-notes"]').text()).toContain("辅端点");
  });

  it("M3-15 stun-test 降级形态：downgraded 提示+UDP 两维不可判", async () => {
    const state = createDefaultState();
    state.stunTestView = {
      publicEndpoint: "203.0.113.10:51234",
      udpMapping: null,
      udpFiltering: null,
      tcpSequential: true,
      tcpPortDependent: null,
      downgraded: true,
      notes: ["服务端未通告辅端点（stun_alt_addr 未配置）：UDP mapping/filtering 不可判（05 §7.2 单公网 IP 降级）"],
      durationMs: 800,
    };
    const { wrapper } = await mountPage(Settings, state, "/settings");

    await wrapper.find('[data-testid="stun-run"]').trigger("click");
    await flushPromises();

    expect(wrapper.find('[data-testid="stun-downgraded"]').exists()).toBe(true);
    const card = wrapper.find('[data-testid="stun-result"]');
    expect(card.text()).toContain("不可判");
    expect(card.text()).not.toContain("对称（ADM/APDM）");
  });

  it("M3-16 ping-device：表单+结果卡渲染（目标/承载/收发/三统计）", async () => {
    const { wrapper } = await mountPage(Settings, undefined, "/settings");

    await wrapper.find('[data-testid="ping-code"]').setValue("d4e5f6");
    await wrapper.find('[data-testid="ping-run"]').trigger("click");
    await flushPromises();

    const card = wrapper.find('[data-testid="ping-result"]');
    expect(card.exists()).toBe(true);
    expect(card.text()).toContain("办公室 NAS");
    expect(card.text()).toContain("d4e5f6");
    expect(card.text()).toContain("直连");
    expect(card.text()).toContain("4 / 4");
    expect(card.text()).toContain("12 ms");
    expect(card.text()).toContain("13.5 ms");
    expect(card.text()).toContain("16 ms");
  });

  it("M3-16 ping-device 全丢形态：统计不可用+空码禁用按钮", async () => {
    const state = createDefaultState();
    state.pingDeviceView = {
      targetDevice: "家里 NAS",
      targetRemoteCode: "0a1b2c",
      sent: 4,
      received: 0,
      minMs: null,
      avgMs: null,
      maxMs: null,
      viaRelay: true,
      durationMs: 8123,
    };
    const { wrapper } = await mountPage(Settings, state, "/settings");

    // 空码时按钮禁用（前置防 1001 往返）
    expect(wrapper.find('[data-testid="ping-run"]').attributes("disabled")).toBeDefined();

    await wrapper.find('[data-testid="ping-code"]').setValue("0a1b2c");
    await wrapper.find('[data-testid="ping-run"]').trigger("click");
    await flushPromises();

    const card = wrapper.find('[data-testid="ping-result"]');
    expect(card.exists()).toBe(true);
    expect(card.text()).toContain("0 / 4");
    expect(card.text()).toContain("不可用");
    expect(card.text()).toContain("中继");
  });
});

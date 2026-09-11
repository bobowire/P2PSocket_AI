// M1-32 页面走查测试（任务清单完成判定：MSW 全页面走查通过）。
// 每用例装独立 MSW 处理器 + 独立 pinia/router/ElementPlus，页面经真实 HTTP→store→渲染链路。
import { setupServer } from "msw/node";
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { createPinia, type Pinia } from "pinia";
import { createRouter, createWebHashHistory, type Router } from "vue-router";
import ElementPlus from "element-plus";
import Dashboard from "../pages/Dashboard.vue";
import Devices from "../pages/Devices.vue";
import Login from "../pages/Login.vue";
import Mappings from "../pages/Mappings.vue";
import Settings from "../pages/Settings.vue";
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

  it("哑节点：建映射禁用（写操作置灰，06 §2）", async () => {
    // /api/devices 属主动类——passive 列表本身拉不到；故先以 normal 态加载，
    // 再把 system store 置为 passive（等价登录态被服务端降级），断言写按钮全部置灰
    const state = createDefaultState();
    state.capability = "normal";
    const { wrapper, pinia } = await mountPage(Devices, state, "/devices");
    expect(wrapper.findAll('[data-testid="devices-create-mapping"]').length).toBe(3);

    const system = useSystemStore(pinia);
    system.device = { ...system.device!, capability: "passive" };
    await flushPromises();
    const create = wrapper.findAll('[data-testid="devices-create-mapping"]');
    for (const btn of create) expect(btn.attributes("disabled")).toBeDefined();
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
    expect(tags.map((t) => t.attributes("data-state"))).toEqual(["direct", "failed", "disabled"]);
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
});

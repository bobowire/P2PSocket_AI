// M1-32 页面走查测试（任务清单完成判定：MSW 全页面走查通过）。
// 每用例装独立 MSW 处理器 + 独立 pinia/router/ElementPlus，页面经真实 HTTP→store→渲染链路。
// 基架（server/live/mountPage/clickMessageBox）上收 __tests__/harness.ts（2026-10-20
// 双模式 FR-C-810：simple.test.ts 复用，防复制漂移）。
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { http, HttpResponse } from "msw";
import { flushPromises, type VueWrapper } from "@vue/test-utils";
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
import About from "../pages/About.vue";
import { createDefaultState } from "../mocks/handlers";
import { useSystemStore } from "../stores/system";
import { server, live, mountPage, clickMessageBox } from "./harness";

beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterEach(() => {
  server.resetHandlers();
  for (const w of live.splice(0)) w.unmount();
  document.body.innerHTML = "";
});
afterAll(() => server.close());

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
    expect(overview.text()).toContain("直连");
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

  it("③ 空分组（默认组 approval 待审）→ 结果页呈现待审批标签", async () => {
    // SEC-62/07 §5.2：注册成功但 ack 仅 approved 成员行 → 空组=待审批，向导如实呈现。
    // 覆盖须在 mountPage 之后（后注册者优先——mountPage 内部注册默认 handlers）
    const { wrapper } = await mountPage(Wizard, unregisteredState(), "/wizard");
    server.use(
      http.post("*/api/wizard/register", () =>
        HttpResponse.json({ code: 0, msg: "ok", data: {
          deviceId: "77777777-7777-4777-8777-777777777777",
          remoteCode: "7g8h9j",
          virtualIp: "100.64.0.2",
          groups: [],
        } })),
    );

    // ① 测试可达 → ② 下一步（默认分组模式无附加必填）→ 注册
    await wrapper.find('input[data-testid="wizard-addr"]').setValue("203.0.113.10:7000");
    await wrapper.find('[data-testid="wizard-test"]').trigger("click");
    await flushPromises();
    await wrapper.find('[data-testid="wizard-next"]').trigger("click");
    await flushPromises();
    await wrapper.find('[data-testid="wizard-register"]').trigger("click");
    await flushPromises();

    const done = wrapper.find(".done");
    expect(done.exists()).toBe(true);
    const tag = done.find('[data-testid="wizard-pending-approval"]');
    expect(tag.exists()).toBe(true);
    expect(tag.text()).toContain("待审批");
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

    // 登出带二次确认（2026-10-20 批次 A U-1）：取消留在已登录态，确认后回表单
    await wrapper.find('[data-testid="login-logout"]').trigger("click");
    await clickMessageBox("登出确认", "cancel");
    expect(wrapper.find('[data-testid="login-username"]').exists()).toBe(true);

    await wrapper.find('[data-testid="login-logout"]').trigger("click");
    await clickMessageBox("登出确认", "confirm");
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

  it("新建抽屉：remoteCode query 预填（下拉显示「设备名（远程码）」）+ 保存走 POST 成功入列表", async () => {
    const state = createDefaultState();
    state.capability = "normal";
    const { wrapper } = await mountPage(Mappings, state, "/mappings?remoteCode=d4e5f6");

    await wrapper.find('[data-testid="mappings-new"]').trigger("click");
    await flushPromises();
    // 抽屉 teleport 到 body；el-select 透传 attrs（data-testid）落在根 div——选中项显示 label
    const drawer = document.querySelector('[data-testid="mapping-drawer"]');
    expect(drawer).not.toBeNull();
    const select = drawer!.querySelector('[data-testid="mapping-remote-code"]');
    expect(select!.textContent).toContain("办公室主机（d4e5f6）");

    const nameInput = drawer!.querySelector("input[data-testid=mapping-name]") as HTMLInputElement;
    nameInput.value = "走查新映射";
    nameInput.dispatchEvent(new Event("input"));
    await flushPromises();
    await (drawer!.querySelector('[data-testid="mapping-save"]') as HTMLElement).click();
    await flushPromises();

    expect(wrapper.find('[data-testid="mappings-table"]').text()).toContain("走查新映射");
  });

  it("新建抽屉：目标远程码下拉「设备名（远程码）」，离线设备可选不禁选", async () => {
    const state = createDefaultState();
    state.capability = "normal";
    const { wrapper } = await mountPage(Mappings, state, "/mappings");

    await wrapper.find('[data-testid="mappings-new"]').trigger("click");
    await flushPromises();

    // EP select 菜单常驻挂载（persistent）——组件树直查 ElOption props（不必开下拉）
    const options = wrapper.findAllComponents({ name: "ElOption" });
    expect(options).toHaveLength(3);
    const byLabel = Object.fromEntries(options.map((o) => [String(o.props("label")), o]));
    expect(Object.keys(byLabel)).toContain("办公室主机（d4e5f6）");
    expect(byLabel["本机（web-dev）（a1b2c3）"]!.props("disabled")).toBe(false);
    expect(byLabel["家里 NAS（0a1b2c）"]!.props("disabled")).toBe(false); // 离线可选（预建映射口径）
  });

  it("停用/启用/删除操作经真实端点生效", async () => {
    const state = createDefaultState();
    state.capability = "normal";
    const { wrapper } = await mountPage(Mappings, state, "/mappings");

    // 停用 direct 行（第一行 enabled=true）——停用侧二次确认（2026-10-20 批次 A U-7）
    await wrapper.find('[data-testid="mapping-disable"]').trigger("click");
    await clickMessageBox("停用确认", "confirm");
    const tags = wrapper.findAll('[data-testid="mapping-state"]');
    expect(tags[0].attributes("data-state")).toBe("disabled");

    // 删除（ElMessageBox confirm → 确认按钮）
    await wrapper.find('[data-testid="mapping-delete"]').trigger("click");
    await clickMessageBox("删除确认", "confirm");
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

  /** 勾选审批弹窗表格指定行（选择列原生 checkbox 在 happy-dom 点击不触发 change——教训⑲ 同族，走组件 API）。 */
  function checkApproveRow(wrapper: VueWrapper, index: number) {
    const table = wrapper.findAllComponents({ name: "ElTable" })
      .find((t) => (t.element as HTMLElement).getAttribute("data-testid") === "groups-approve-list")!;
    const row = (table.props("data") as { requestId: string }[])[index]!;
    (table.vm as { toggleRowSelection: (row: unknown, selected?: boolean) => void })
      .toggleRowSelection(row, true);
  }

  it("已加入列表（策略/成员数[所有者附待审]/角色）+ 审批弹窗：勾选→批量同意 → 待审清零", async () => {
    const { wrapper } = await mountPage(Groups, normalState(), "/groups");

    const table = wrapper.find('[data-testid="groups-table"]');
    expect(table.text()).toContain("默认分组");
    expect(table.text()).toContain("项目协作组");
    expect(table.text()).toContain("自由加入");
    expect(table.text()).toContain("需审批");
    expect(table.text()).toContain("所有者");
    expect(table.text()).toContain("成员");
    // 仅所有者行有操作组按钮；成员行为退组；审批入口仅所有者行
    expect(wrapper.findAll('[data-testid="groups-dissolve"]').length).toBe(1);
    expect(wrapper.findAll('[data-testid="groups-leave"]').length).toBe(1);
    expect(wrapper.findAll('[data-testid="groups-approve-entry"]').length).toBe(1);

    // 成员数列：非所有者行仅成员数；所有者行附待审计数（mock 种子 2 成员+1 待审）
    const rows = wrapper.findAll('[data-testid="groups-table"] .el-table__row');
    expect(rows[0].text()).toContain("3");
    expect(rows[0].text()).not.toContain("待审");
    expect(rows[1].text()).toContain("2（待审：1）");

    // 审批弹窗：申请设备 + 批量按钮无勾选时禁用
    await wrapper.find('[data-testid="groups-approve-entry"]').trigger("click");
    await flushPromises();
    const dialog = document.querySelector('[data-testid="groups-approve-dialog"]')!;
    expect(dialog.querySelector('[data-testid="groups-approve-list"]')!.textContent).toContain("家里 NAS");
    const batchOk = dialog.querySelector('[data-testid="groups-batch-approve"]') as HTMLButtonElement;
    expect(batchOk.className).toContain("is-disabled");

    // 勾选首行 → 批量同意 → 成员数 2→3、待审清零、弹窗空态（待审清零自动关闭，再开即空）
    checkApproveRow(wrapper, 0);
    await flushPromises();
    expect(batchOk.className).not.toContain("is-disabled");
    batchOk.click();
    await flushPromises();
    const rows2 = wrapper.findAll('[data-testid="groups-table"] .el-table__row');
    expect(rows2[1].text()).toContain("3（待审：0）");
    await wrapper.find('[data-testid="groups-approve-entry"]').trigger("click");
    await flushPromises();
    expect(document.querySelector('[data-testid="groups-approve-dialog"]')?.textContent).toContain("暂无待审批申请");
  });

  it("审批弹窗：批量拒绝 → 成员数不变、待审递减、弹窗保持", async () => {
    const state = normalState();
    state.joinRequests.push({
      requestId: "r-55555555-5555-4555-8555-555555555555",
      groupId: "g-22222222-2222-4222-8222-222222222222",
      deviceId: "55555555-5555-4555-8555-555555555555",
      deviceName: "测试机 B",
      createdAtMs: Date.UTC(2026, 8, 29, 9, 0, 0),
    });
    const { wrapper } = await mountPage(Groups, state, "/groups");
    expect(wrapper.findAll('[data-testid="groups-table"] .el-table__row')[1]!.text()).toContain("2（待审：2）");

    await wrapper.find('[data-testid="groups-approve-entry"]').trigger("click");
    await flushPromises();
    checkApproveRow(wrapper, 0);
    await flushPromises();
    (document.querySelector('[data-testid="groups-batch-reject"]') as HTMLButtonElement).click();
    await flushPromises();

    // 拒绝不加成员：memberCount 2 不变；待审 2→1；组内仍有待审 → 弹窗内容递减保持打开
    expect(wrapper.findAll('[data-testid="groups-table"] .el-table__row')[1]!.text()).toContain("2（待审：1）");
    expect(document.querySelector('[data-testid="groups-approve-list"]')!.textContent).not.toContain("家里 NAS");
    expect(document.querySelectorAll('[data-testid="groups-approve-list"] .el-table__row').length).toBe(1);
  });

  it("审批弹窗分页：12 条待审 10/页两页，翻页清勾选", async () => {
    const state = normalState();
    for (let i = 2; i <= 12; i++)
      state.joinRequests.push({
        requestId: `r-0000000${i}-0000-4000-8000-00000000000${i}`,
        groupId: "g-22222222-2222-4222-8222-222222222222",
        deviceId: `0000000${i}-0000-4000-8000-00000000000${i}`,
        deviceName: `批量设备 ${String(i).padStart(2, "0")}`,
        createdAtMs: Date.UTC(2026, 8, 28, 10, i, 0),
      });
    const { wrapper } = await mountPage(Groups, state, "/groups");
    expect(wrapper.findAll('[data-testid="groups-table"] .el-table__row')[1]!.text()).toContain("（待审：12）");

    await wrapper.find('[data-testid="groups-approve-entry"]').trigger("click");
    await flushPromises();
    const dialog = document.querySelector('[data-testid="groups-approve-dialog"]')!;
    // 第 1 页 10 行 + 分页总数
    expect(dialog.querySelectorAll('[data-testid="groups-approve-list"] .el-table__row').length).toBe(10);
    expect(dialog.querySelector('[data-testid="groups-approve-pager"]')!.textContent).toContain("12");

    // 翻到第 2 页：余 2 行；第 1 页勾选不跨页保留（批量按钮回禁用）
    checkApproveRow(wrapper, 0);
    await flushPromises();
    const page2 = [...dialog.querySelectorAll(".el-pager li")].find(
      (li) => li.textContent === "2",
    ) as HTMLElement;
    page2.click();
    await flushPromises();
    expect(dialog.querySelectorAll('[data-testid="groups-approve-list"] .el-table__row').length).toBe(2);
    const batchOk = dialog.querySelector('[data-testid="groups-batch-approve"]') as HTMLButtonElement;
    expect(batchOk.className).toContain("is-disabled");
  });

  it("凭码入组：approval 组 3002 → 待审批提示 + 申请入队；free 组即时入组", async () => {
    const state = normalState();
    state.groups[0].inviteCode = "xy9876"; // 默认分组（free；小写=服务端 InviteCharset 口径）
    state.groups[1].inviteCode = "ab2345"; // 项目协作组（approval）
    const { wrapper } = await mountPage(Groups, state, "/groups");

    await wrapper.find('[data-testid="groups-join"]').trigger("click");
    await flushPromises();
    const dialog = document.querySelector('[data-testid="groups-join-dialog"]')!;
    const input = dialog.querySelector("input[data-testid=groups-join-code]") as HTMLInputElement;

    // approval 组：3002 原码透传 → 前端按"待审批"提示（非错误）
    input.value = "ab2345";
    input.dispatchEvent(new Event("input"));
    await flushPromises();
    (dialog.querySelector('[data-testid="groups-join-save"]') as HTMLElement).click();
    await flushPromises();
    expect(
      [...document.querySelectorAll(".el-message")].some((m) => m.textContent?.includes("等待所有者")),
    ).toBe(true);
    // mock 侧申请入队（演示态与真实 0x51 建单同构）→ 所有者行待审计数 1→2
    expect(wrapper.findAll('[data-testid="groups-table"] .el-table__row')[1]!.text()).toContain("（待审：2）");

    // free 组：即时入组（memberCount 3→4）。输入大写抄写——校验放行（i 标志）、
    // 提交归一小写后匹配 mock 存码（服务端 BINARY 比较大小写敏感，归一是必须路径）
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
    expect(dialog.querySelector('[data-testid="groups-invite-code"]')?.textContent ?? "").toMatch(/[2-9a-hjkmnp-z]{6}/);
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
    state.peers = { "22222222-2222-4222-8222-222222222222": { relayFallback: true, punchMode: "tcp" } };
    const { wrapper } = await mountPage(Devices, state, "/devices");

    // 每行两开关（中继回退 + M7-05 优先打洞模式）：3 行 × 2 = 6
    const switches = wrapper.findAllComponents({ name: "ElSwitch" });
    expect(switches.length).toBe(6);
    await vi.waitFor(() => expect(switches[2].props("modelValue")).toBe(true)); // 现值异步拉取（第二行中继回退）

    await switches[0].vm.$emit("change", true); // 开启本机回退
    await flushPromises();
    expect(switches[0].props("modelValue")).toBe(true);
  });
});

// ── 设备页优先打洞模式 + 隧道类型列（M7-05，设计 12 §8.3/§7.3）─────

describe("Devices 优先打洞模式与隧道类型列（M7-05）", () => {
  it("punchMode 开关默认 TCP；切换 → PUT 单字段 + 成功提示（即时生效语义）", async () => {
    const state = createDefaultState();
    state.capability = "normal";
    const { wrapper } = await mountPage(Devices, state, "/devices");

    await vi.waitFor(() => {
      // punchMode 开关=每行第 2 个（索引 1/3/5）；无条目默认 false=TCP
      const modeSwitch = wrapper.findAllComponents({ name: "ElSwitch" })[1];
      expect(modeSwitch.props("modelValue")).toBe(false);
      expect(modeSwitch.props("inactiveText")).toBe("TCP"); // inline-prompt 文案
      expect(modeSwitch.props("activeText")).toBe("KCP");
    });

    // 切换 → PUT { punchMode } 单字段（mock 状态即回显：合并写不动 relayFallback）+ 消息提示
    await wrapper.findAllComponents({ name: "ElSwitch" })[1].vm.$emit("change", true);
    await flushPromises();
    expect(state.peers["11111111-1111-4111-8111-111111111111"]).toEqual({
      relayFallback: false, // 单字段 punchMode 写不动回退（部分更新合并，设计 12 §3.3）
      punchMode: "kcp",
    });
    await vi.waitFor(() => {
      expect([...document.querySelectorAll(".el-message")]
        .some((m) => m.textContent?.includes("优先打洞模式"))).toBe(true);
    });
  });

  it("隧道类型列四态 tag + 无隧道 —（数据源 tunnels.bearer）", async () => {
    const state = createDefaultState();
    state.capability = "normal";
    // 覆盖四态：办公室主机=relay、家里 NAS=raw-udp、本机=tcp（kcp 态换行验证）
    state.tunnels = [
      {
        peerDeviceId: "11111111-1111-4111-8111-111111111111",
        label: null, viaRelay: false, isInitiator: true, kcp: null, bearer: "tcp",
      },
      {
        peerDeviceId: "22222222-2222-4222-8222-222222222222",
        label: null, viaRelay: true, isInitiator: false, kcp: null, bearer: "relay",
      },
      {
        peerDeviceId: "33333333-3333-4333-8333-333333333333",
        label: null, viaRelay: false, isInitiator: true, kcp: null, bearer: "raw-udp",
      },
    ];
    const { wrapper } = await mountPage(Devices, state, "/devices");

    const tags = await vi.waitFor(() => {
      const t = wrapper.findAll('[data-testid="devices-tunnel-type"]');
      expect(t.length).toBe(3);
      return t;
    });
    expect(tags[0].text()).toBe("直连·TCP");
    expect(tags[1].text()).toBe("中继");
    expect(tags[2].text()).toBe("直连·UDP"); // 协商降级形态

    // tag 样式与降级 tooltip（组件 props/透传 attr 断言）
    const tagComps = wrapper.findAllComponents({ name: "ElTag" })
      .filter((c) => c.attributes("data-testid") === "devices-tunnel-type");
    expect(tagComps[1]!.props("type")).toBe("warning"); // 中继=warning
    expect(tagComps[0]!.props("type")).toBe("success"); // 直连系=success
    expect(tagComps[2]!.attributes("title")).toContain("已降级裸 UDP");

    // 无隧道 → —（清空 tunnels 再挂载）
    const empty = createDefaultState();
    empty.capability = "normal";
    empty.tunnels = [];
    const emptyPage = await mountPage(Devices, empty, "/devices");
    const emptyTags = await vi.waitFor(() => {
      const t = emptyPage.wrapper.findAll('[data-testid="devices-tunnel-type"]');
      expect(t.length).toBe(3);
      return t;
    });
    expect(emptyTags[0].text()).toBe("—");
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

  // ── M5-14/SEC-84（07 §14.4）：产物哈希按平台展示 + 屏蔽态 URL 空 ──

  it("artifactSha256 清单：Windows 取 windows-setup 项展示；未录入不展示", async () => {
    const originalAgent = window.navigator.userAgent;
    Object.defineProperty(window.navigator, "userAgent",
      { value: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36", configurable: true });
    try {
      const state = createDefaultState();
      state.upgrade = {
        latestVersion: "0.4.2",
        minProtocol: 1,
        maxProtocol: 2,
        upgradeUrl: "https://example.com/dl",
        notes: "n",
        artifactSha256: JSON.stringify({
          "windows-setup": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
          "linux-tar": "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90",
        }),
      };
      const { wrapper } = await mountPage(Upgrade, state, "/upgrade");
      expect(wrapper.find('[data-testid="upgrade-hash"]').exists()).toBe(true);
      expect(wrapper.find('[data-testid="upgrade-hash"]').text())
        .toContain("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
      // 未录入（空串）：哈希区不渲染
      const bare = createDefaultState();
      bare.upgrade = { ...bare.upgrade, artifactSha256: "" };
      const { wrapper: bareWrapper } = await mountPage(Upgrade, bare, "/upgrade");
      expect(bareWrapper.find('[data-testid="upgrade-hash"]').exists()).toBe(false);
    } finally {
      Object.defineProperty(window.navigator, "userAgent", { value: originalAgent, configurable: true });
    }
  });

  it("http 屏蔽态：upgradeUrl 空显示「未配置」，notes 含管理员提示", async () => {
    const state = createDefaultState();
    state.upgrade = {
      latestVersion: "0.4.2",
      minProtocol: 1,
      maxProtocol: 2,
      upgradeUrl: "",
      notes: "维护公告\n管理员未配置有效升级地址",
    };
    const { wrapper } = await mountPage(Upgrade, state, "/upgrade");
    expect(wrapper.find('[data-testid="upgrade-url"]').exists()).toBe(false); // 空地址不渲染链接
    expect(wrapper.find('[data-testid="upgrade-url-empty"]').text()).toContain("未配置");
    expect(wrapper.find('[data-testid="upgrade-notes"]').text()).toContain("管理员未配置有效升级地址");
  });
});

// ── 关于页（FR-C-812，2026-10-21）─────────────────────────────────

describe("About 走查（FR-C-812）", () => {
  it("关于卡：项目名/介绍（AI 重构口径）/仓库链接/原项目链接/QQ 群（projectInfo.ts 同源静态数据）", async () => {
    const { wrapper } = await mountPage(About, undefined, "/about");

    expect(wrapper.find('[data-testid="about-name"]').text()).toBe("P2PSocket_AI");
    const intro = wrapper.find('[data-testid="about-intro"]');
    expect(intro.text()).toContain("AI 重构");
    expect(intro.text()).toContain("内网穿透");
    expect(intro.text()).toContain("打洞直连隧道");
    const repo = wrapper.find('[data-testid="about-repo"]');
    expect(repo.attributes("href")).toBe("https://github.com/bobowire/P2PSocket_AI");
    const originRepo = wrapper.find('[data-testid="about-origin-repo"]');
    expect(originRepo.attributes("href")).toBe("https://github.com/bobowire/Wireboy.Socket.P2PSocket");
    expect(wrapper.find('[data-testid="about-qq"]').text()).toContain("417159195");
  });

  it("里程碑时间轴：M1~M5+维护期六节点，收官绿/进行蓝（用户批复汇总口径，M5 随立项补入）", async () => {
    const { wrapper } = await mountPage(About, undefined, "/about");

    const items = wrapper.findAll('[data-testid="about-milestone"]');
    expect(items.map((i) => i.find(".ms-title").text())).toEqual([
      "M1 · 核心链路可用",
      "M2 · 打洞完整与授权闭环",
      "M3 · 管理面与可观测",
      "M4 · 交付打磨",
      "维护期 · 持续交付",
      "M5 · 安全加固",
    ]);
    // done=success（M1/M4）、active=primary（M2/M3/维护期/M5）——节点色 class 区分
    expect(wrapper.findAll('[data-testid="about-milestone"] .el-timeline-item__node--success'))
      .toHaveLength(2);
    expect(wrapper.findAll('[data-testid="about-milestone"] .el-timeline-item__node--primary'))
      .toHaveLength(4);
    expect(wrapper.find('[data-testid="about-timeline"]').text()).toContain("2026-09-12");
    expect(wrapper.find('[data-testid="about-timeline"]').text()).toContain("2026-10-11");
    expect(wrapper.find('[data-testid="about-timeline"]').text()).toContain("安全加固");
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
    const [oldPw, newPw, confirmPw] = [...dialog.querySelectorAll("input[type=password]")] as HTMLInputElement[];
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
      version: "",
      commit: "",
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

  it("设备改名：初值加载 → 改名按钮 → mock 侧本机名更新；同名/空名不可点（v1.58）", async () => {
    const state = createDefaultState();
    const { wrapper } = await mountPage(Settings, state, "/settings");

    // 初值 = GET /api/device 的 deviceName（mock 本机行）
    const input = wrapper.find('[data-testid="device-name-input"]');
    expect((input.element as HTMLInputElement).value).toBe("本机（web-dev）");
    // 初值同名 → 改名按钮禁用
    expect(wrapper.find('[data-testid="device-rename"]').attributes("disabled")).toBeDefined();

    // 改名 → mock 侧 me.deviceName 更新 + 成功提示
    await input.setValue("新设备名-改名");
    await flushPromises();
    await wrapper.find('[data-testid="device-rename"]').trigger("click");
    await flushPromises();
    expect(state.devices.find((d) => d.deviceId === state.deviceId)?.deviceName)
      .toBe("新设备名-改名");
    expect(
      [...document.querySelectorAll(".el-message")].some((m) => m.textContent?.includes("设备名已更新")),
    ).toBe(true);

    // 空名 → 按钮恢复禁用（trim 后 1~64）
    await input.setValue("   ");
    await flushPromises();
    expect(wrapper.find('[data-testid="device-rename"]').attributes("disabled")).toBeDefined();
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

  it("M4-01 关于区：版本/构建提交/协议版本三行（state.version+commit）", async () => {
    const { wrapper } = await mountPage(Settings, undefined, "/settings");

    const about = wrapper.find('[data-testid="about-box"]');
    expect(about.exists()).toBe(true);
    expect(wrapper.find('[data-testid="about-version"]').text()).toBe("v1.0.0");
    expect(about.text()).toContain("m401mock"); // 构建提交（mock 固定值）
    expect(about.text()).toContain("协议版本");
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

  it("M3-14 服务端连通性：混合候选结果渲染（可达/不可达+详情）", async () => {
    const { wrapper } = await mountPage(Settings, undefined, "/settings");

    await wrapper.find('[data-testid="svtest-run"]').trigger("click");
    await flushPromises();

    const rows = wrapper.findAll('[data-testid="svtest-result"] .el-table__row');
    expect(rows).toHaveLength(2);
    expect(rows[0]!.text()).toContain("127.0.0.1:7101");
    expect(rows[0]!.text()).toContain("可达");
    expect(rows[0]!.find(".el-tag--success").exists()).toBe(true);
    expect(rows[1]!.text()).toContain("203.0.113.1:7000");
    expect(rows[1]!.text()).toContain("不可达");
    expect(rows[1]!.text()).toContain("连接超时");
    expect(rows[1]!.find(".el-tag--danger").exists()).toBe(true);
  });

  it("M3-14 打洞队列：onMounted 拉取深度+当前目标；空闲态显示（空闲）", async () => {
    const busy = await mountPage(Settings, undefined, "/settings");
    const card = await vi.waitFor(() => {
      const c = busy.wrapper.find('[data-testid="punch-result"]');
      expect(c.exists()).toBe(true);
      return c;
    });
    expect(card.text()).toContain("1"); // 队列深度
    expect(card.text()).toContain("33333333-3333-4333-8333-333333333333");

    // 空闲形态：深度 0 + 无当前目标
    const idle = createDefaultState();
    idle.diagnostics = { punchQueueDepth: 0, currentPunchPeer: null };
    const idlePage = await mountPage(Settings, idle, "/settings");
    const idleCard = await vi.waitFor(() => {
      const c = idlePage.wrapper.find('[data-testid="punch-result"]');
      expect(c.exists()).toBe(true);
      return c;
    });
    expect(idleCard.text()).toContain("（空闲）");
  });

  it("M3-14 隧道与 REKEY：列表（标签/短码回退/承载/角色）+触发反馈+failed 详情+空态", async () => {
    const state = createDefaultState();
    const { wrapper } = await mountPage(Settings, state, "/settings");

    const table = await vi.waitFor(() => {
      const t = wrapper.find('[data-testid="tunnels-table"]');
      expect(t.exists()).toBe(true);
      return t;
    });
    const rows = table.findAll(".el-table__row");
    expect(rows).toHaveLength(2);
    expect(rows[0]!.text()).toContain("d4e5f6"); // Label=映射目标远程码
    expect(rows[0]!.text()).toContain("直连·TCP"); // M7-04 承载细化：mock 行 bearer=tcp（旧"直连"二分）
    expect(rows[0]!.text()).toContain("发起方");
    expect(rows[1]!.text()).toContain("33333333"); // 无映射指向：peerId 短码回退
    expect(rows[1]!.text()).toContain("中继");
    expect(rows[1]!.text()).toContain("响应方");

    // 发起方触发 → ok 反馈
    await wrapper.find('[data-testid="rekey-22222222-2222-4222-8222-222222222222"]').trigger("click");
    await vi.waitFor(() => {
      expect([...document.querySelectorAll(".el-message")]
        .some((m) => m.textContent?.includes("轮换成功"))).toBe(true);
    });

    // 响应方触发 → not_initiator 信息提示（密钥由对端轮换）
    state.rekeyOutcome = "not_initiator";
    await wrapper.find('[data-testid="rekey-33333333-3333-4333-8333-333333333333"]').trigger("click");
    await vi.waitFor(() => {
      expect([...document.querySelectorAll(".el-message")]
        .some((m) => m.textContent?.includes("本端为响应方"))).toBe(true);
    });

    // 空态：无活动隧道 → 空提示，无表格无按钮（清单"无隧道禁用"）
    const empty = createDefaultState();
    empty.tunnels = [];
    const emptyPage = await mountPage(Settings, empty, "/settings");
    await vi.waitFor(() => {
      expect(emptyPage.wrapper.find('[data-testid="tunnels-empty"]').exists()).toBe(true);
    });
    expect(emptyPage.wrapper.find('[data-testid="tunnels-table"]').exists()).toBe(false);
  });

  it("M3-14 REKEY failed 形态：错误消息携带后端原因", async () => {
    const state = createDefaultState();
    state.rekeyOutcome = "failed";
    state.rekeyDetail = "会话已关闭";
    const { wrapper } = await mountPage(Settings, state, "/settings");

    await vi.waitFor(() => {
      expect(wrapper.find('[data-testid="rekey-22222222-2222-4222-8222-222222222222"]').exists())
        .toBe(true);
    });
    await wrapper.find('[data-testid="rekey-22222222-2222-4222-8222-222222222222"]').trigger("click");
    await vi.waitFor(() => {
      expect([...document.querySelectorAll(".el-message")]
        .some((m) => m.textContent?.includes("轮换失败：会话已关闭"))).toBe(true);
    });
  });
});

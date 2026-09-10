// M1-31 组件测试（06 §1）：StatusTag 状态映射 / CodeText 复制 / DeviceCard 字段 /
// AdminLayout 布局与菜单（RouterLink 以替身挂载）。
import { afterEach, describe, expect, it, vi } from "vitest";
import { mount, RouterLinkStub } from "@vue/test-utils";
import AdminLayout from "../layouts/AdminLayout.vue";
import CodeText from "./CodeText.vue";
import DeviceCard from "./DeviceCard.vue";
import StatusTag from "./StatusTag.vue";

afterEach(() => vi.restoreAllMocks());

describe("StatusTag（映射状态机 02 §4.5）", () => {
  it.each([
    ["disabled", "已禁用", "info"],
    ["punching", "打洞中", "warning"],
    ["direct", "直达", "success"],
    ["relay", "中继", "primary"],
    ["failed", "失败", "danger"],
  ])("状态 %s → 文案/样式", (state, text, kind) => {
    const w = mount(StatusTag, { props: { state } });
    expect(w.text()).toBe(text);
    expect(w.find("span").attributes("data-kind")).toBe(kind);
    expect(w.find("span").attributes("data-state")).toBe(state);
  });

  it("未知状态回退原文 + info", () => {
    const w = mount(StatusTag, { props: { state: "weird" } });
    expect(w.text()).toBe("weird");
    expect(w.find("span").attributes("data-kind")).toBe("info");
  });
});

describe("CodeText（远程码复制）", () => {
  it("点击写入剪贴板并短暂显示已复制", async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal("navigator", { clipboard: { writeText } });
    const w = mount(CodeText, { props: { value: "0a1b2c", label: "远程码" } });
    expect(w.find("code").text()).toBe("0a1b2c");
    await w.find("button").trigger("click");
    expect(writeText).toHaveBeenCalledWith("0a1b2c");
    expect(w.find(".hint").text()).toBe("已复制");
  });

  it("剪贴板失败静默（原文仍展示）", async () => {
    vi.stubGlobal("navigator", { clipboard: { writeText: vi.fn().mockRejectedValue(new Error("denied")) } });
    const w = mount(CodeText, { props: { value: "0a1b2c" } });
    await w.find("button").trigger("click");
    expect(w.find("code").text()).toBe("0a1b2c");
    expect(w.find(".hint").text()).toBe("复制");
  });
});

describe("DeviceCard", () => {
  const device = {
    deviceId: "d1",
    deviceName: "办公室主机",
    remoteCode: "0a1b2c",
    online: true,
    groupName: "默认分组",
  };

  it("渲染设备名/分组/远程码/在线态（快照）", () => {
    const w = mount(DeviceCard, { props: { device } });
    expect(w.find(".name").text()).toBe("办公室主机");
    expect(w.find(".group").text()).toBe("默认分组");
    expect(w.find(".dot").attributes("data-online")).toBe("true");
    expect(w.text()).toContain("0a1b2c");
    expect(w.element).toMatchSnapshot();
  });

  it("离线态与操作插槽", () => {
    const w = mount(DeviceCard, {
      props: { device: { ...device, online: false, groupName: null } },
      slots: { actions: "<button>建映射</button>" },
    });
    expect(w.find(".dot").attributes("data-online")).toBe("false");
    expect(w.text()).toContain("离线");
    expect(w.find(".actions button").text()).toBe("建映射");
  });
});

describe("AdminLayout", () => {
  const menu = [
    { path: "/", label: "仪表盘" },
    { path: "/mappings", label: "端口映射" },
  ];

  it("侧边栏菜单 + 顶栏 + 主区插槽（快照）", () => {
    const w = mount(AdminLayout, {
      props: { title: "P2P 客户端", menu },
      slots: { default: "<p>main</p>", header: "<span>user</span>" },
      global: { stubs: { RouterLink: RouterLinkStub } },
    });
    expect(w.find(".brand").text()).toBe("P2P 客户端");
    const items = w.findAll(".menu-item");
    expect(items.map((i) => i.text())).toEqual(["仪表盘", "端口映射"]);
    expect(w.findAllComponents(RouterLinkStub).map((l) => l.props("to")))
      .toEqual(["/", "/mappings"]);
    expect(w.text()).toContain("main");
    expect(w.find(".actions").text()).toBe("user");
    expect(w.element).toMatchSnapshot();
  });

  it("alert 未设置时不可见，设置后显示", async () => {
    const w = mount(AdminLayout, {
      props: { title: "t", menu, alert: undefined },
      global: { stubs: { RouterLink: RouterLinkStub } },
    });
    expect(w.find(".alert").attributes("class")).not.toContain("visible");
    await w.setProps({ alert: "本地服务不可达" });
    expect(w.find(".alert").text()).toBe("本地服务不可达");
    expect(w.find(".alert").attributes("class")).toContain("visible");
  });
});

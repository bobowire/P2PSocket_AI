// M1-31 useWs 测试（TD-16 事件分级 / 100ms debounce / resync 时机 / 指数退避重连）。
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent } from "vue";
import { mount } from "@vue/test-utils";
import { REFETCH_DEBOUNCE_MS, RESYNC_RESOURCES, useWs, type RefetchResource, type WebSocketLike } from "./useWs";

/** WebSocket 替身：手动驱动 open/close/message（连接即"成功"，无真网络）。 */
class FakeWs implements WebSocketLike {
  static instances: FakeWs[] = [];
  onopen: (() => void) | null = null;
  onclose: (() => void) | null = null;
  onerror: (() => void) | null = null;
  onmessage: ((ev: { data: string }) => void) | null = null;
  closed = false;

  constructor(public readonly url: string) {
    FakeWs.instances.push(this);
  }

  send(): void { /* M1 客户端 WS 无上行 */ }
  close(): void { this.closed = true; }

  serverOpen() { this.onopen?.(); }
  serverClose() { this.onclose?.(); }
  serverEvent(evt: object) { this.onmessage?.({ data: JSON.stringify(evt) }); }
}

function setup(options?: Partial<Parameters<typeof useWs>[0]>) {
  const refetched: RefetchResource[] = [];
  const stats: object[] = [];
  const resyncs: number[] = [];
  const api = useWs({
    url: "ws://127.0.0.1:7100/ws/status",
    wsFactory: (url) => new FakeWs(url),
    onStats: (e) => stats.push(e),
    refetch: (r) => refetched.push(r),
    onResync: () => resyncs.push(1),
    ...options,
  });
  return { api, refetched, stats, resyncs, current: () => FakeWs.instances.at(-1)! };
}

/** 在组件 setup 内执行（onScopeDispose 需活跃作用域；卸载触发 close）。 */
function setupInComponent(fn: () => ReturnType<typeof setup>) {
  let handle!: ReturnType<typeof setup>;
  const wrapper = mount(defineComponent({
    setup() {
      handle = fn();
      return () => null;
    },
  }));
  return { wrapper, ...handle };
}

beforeEach(() => {
  FakeWs.instances = [];
  vi.useFakeTimers();
});

afterEach(() => {
  vi.useRealTimers();
});

describe("useWs TD-16 事件分级", () => {
  it("onOpen 全量 resync（system/mappings/devices/auth + onResync）", () => {
    const t = setup();
    t.current().serverOpen();
    expect(t.refetched).toEqual(RESYNC_RESOURCES);
    expect(t.resyncs).toHaveLength(1);
  });

  it("mapping_stats 数值类直写：即回调、不触发 refetch", () => {
    const t = setup();
    t.current().serverOpen();
    t.refetched.length = 0;
    t.current().serverEvent({ ev: "mapping_stats", id: "m1", rateUp: 1024, rateDown: 512, path: "direct" });
    expect(t.stats).toEqual([{ id: "m1", rateUp: 1024, rateDown: 512, path: "direct" }]);
    vi.advanceTimersByTime(REFETCH_DEBOUNCE_MS * 2);
    expect(t.refetched).toEqual([]); // 数值类不 refetch
  });

  it("mapping_state 状态类 → 仅 refetch(mappings)，100ms 窗口合并突发", () => {
    const t = setup();
    t.current().serverOpen();
    t.refetched.length = 0;
    t.current().serverEvent({ ev: "mapping_state", id: "m1", state: "punching", reason: "" });
    t.current().serverEvent({ ev: "mapping_state", id: "m1", state: "direct", reason: "" });
    expect(t.refetched).toEqual([]); // debounce 窗口内不立即触发
    vi.advanceTimersByTime(REFETCH_DEBOUNCE_MS + 10);
    expect(t.refetched).toEqual(["mappings"]); // 两事件合并为一次
  });

  it("login_state / device_list → 各自资源 refetch（device_list 属 M2 规则先行）", () => {
    const t = setup();
    t.current().serverOpen();
    t.refetched.length = 0;
    t.current().serverEvent({ ev: "login_state", mode: "passive" });
    t.current().serverEvent({ ev: "device_list" });
    vi.advanceTimersByTime(REFETCH_DEBOUNCE_MS + 10);
    expect(t.refetched).toEqual(["auth", "devices"]);
  });

  it("非 JSON 帧丢弃（提示通道容错）", () => {
    const t = setup();
    t.current().serverOpen();
    t.refetched.length = 0;
    t.current().onmessage?.({ data: "not-json" });
    vi.advanceTimersByTime(REFETCH_DEBOUNCE_MS + 10);
    expect(t.refetched).toEqual([]);
  });
});

describe("useWs resync 时机与生命周期", () => {
  it("visibilitychange → visible 触发全量 resync", () => {
    const t = setup();
    t.current().serverOpen();
    t.refetched.length = 0;
    vi.spyOn(document, "visibilityState", "get").mockReturnValue("visible");
    document.dispatchEvent(new Event("visibilitychange"));
    expect(t.refetched).toEqual(RESYNC_RESOURCES);
    expect(t.resyncs).toHaveLength(2); // onOpen 1 次 + visibility 1 次
  });

  it("断线指数退避（未成功重连 1s→2s→4s），重连成功归零重来", () => {
    const t = setup();
    const len = () => FakeWs.instances.length;
    t.current().serverOpen();
    expect(t.api.connected.value).toBe(true);

    t.current().serverClose(); // 退避 1s（retries=1）
    expect(t.api.connected.value).toBe(false);
    vi.advanceTimersByTime(999);
    expect(len()).toBe(1);
    vi.advanceTimersByTime(1);
    expect(len()).toBe(2); // 1s 档重连（连接失败即 close，未 open）
    t.current().serverClose(); // 退避 2s（retries=2）
    vi.advanceTimersByTime(1999);
    expect(len()).toBe(2);
    vi.advanceTimersByTime(1);
    expect(len()).toBe(3); // 2s 档
    t.current().serverOpen(); // 本次成功 → 退避归零
    t.current().serverClose(); // 退避又从 1s 起
    vi.advanceTimersByTime(1000);
    expect(len()).toBe(4); // 1s 档（归零验证：非 2s/4s）
  });

  it("退避上限 30s（连续失败不无限增长）", () => {
    const t = setup({ reconnect: { minMs: 1000, maxMs: 3000 } });
    t.current().serverOpen();
    t.current().serverClose();
    // 逐次失败推进：1s、2s、3s、3s——第 4 次起恒为上限档
    const schedule = [1000, 2000, 3000, 3000];
    for (const delay of schedule) {
      vi.advanceTimersByTime(delay);
      t.current().serverClose();
    }
    expect(FakeWs.instances).toHaveLength(1 + schedule.length);
    vi.advanceTimersByTime(2999);
    expect(FakeWs.instances).toHaveLength(5);
    vi.advanceTimersByTime(1);
    expect(FakeWs.instances).toHaveLength(6); // 第 5 次仍 3s 档
  });

  it("close 后不再重连（停机幂等）", () => {
    const t = setup();
    t.current().serverOpen();
    t.current().serverClose();
    t.api.close();
    vi.advanceTimersByTime(60_000);
    expect(FakeWs.instances).toHaveLength(1);
  });

  it("组件卸载自动关闭（onScopeDispose）", () => {
    const t = setupInComponent(() => setup());
    t.current().serverOpen();
    t.wrapper.unmount();
    expect(t.api.connected.value).toBe(false);
    vi.advanceTimersByTime(60_000);
    expect(FakeWs.instances).toHaveLength(1); // 卸载后无重连
  });
});

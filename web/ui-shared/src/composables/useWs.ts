// M1-31 useWs（06 §4、TD-16"REST 是真相、WS 只是提示"）：
// - 事件分级：mapping_stats（1s 速率，数值类）→ onStats 直写；mapping_state/login_state/device_list
//   （状态类）→ 不直接改 store，仅触发对应资源 refetch（100ms debounce 合并突发）；
// - resync 兜底：onOpen（含断线重连成功）与 visibilitychange→visible 各触发一次全量 refetch；
// - 重连：指数退避 1s 起 ×2、上限 30s，连接成功归零；
// - connected 供全局"服务不可达"条（网络断连提示，06 §4）。
// 注：M1 客户端 WS 无上行（04 §2.8），不提供 send；device_list 属 M2，分级规则先行就位。
import { getCurrentScope, onScopeDispose, ref, type Ref } from "vue";

/** 状态类事件触发的 refetch 资源（对应 Pinia store，06 §4）。 */
export type RefetchResource = "system" | "mappings" | "devices" | "auth";

export interface WsOptions {
  url: string;
  /** 数值类直写（mapping_stats：下秒覆盖，丢失/陈旧无害）。 */
  onStats?: (e: { id: string; rateUp: number; rateDown: number; path: "direct" | "relay" }) => void;
  /** 状态类 → 资源 refetch（100ms debounce；TD-16）。 */
  refetch?: (resource: RefetchResource) => void;
  /** 全量 resync（onOpen/visibilitychange；丢失事件兜底）。 */
  onResync?: () => void;
  /** 测试注入替身 WebSocket（默认原生）。 */
  wsFactory?: (url: string) => WebSocketLike;
  /** 退避参数（默认 1s→30s，08 §5.2 reconnect 节）。 */
  reconnect?: { minMs: number; maxMs: number };
}

/** 最小 WebSocket 形状（测试替身契约；浏览器原生自动满足）。 */
export interface WebSocketLike {
  send(data: string): void;
  close(): void;
  onopen: (() => void) | null;
  onclose: (() => void) | null;
  onerror: (() => void) | null;
  onmessage: ((ev: { data: string }) => void) | null;
}

/** 状态类事件 → 资源映射（04 §2.8 事件语义）。 */
const STATE_EVENT_RESOURCES: Record<string, RefetchResource> = {
  mapping_state: "mappings",
  login_state: "auth",
  device_list: "devices",
};

export const RESYNC_RESOURCES: RefetchResource[] = ["system", "mappings", "devices", "auth"];

/** 状态类事件 debounce 窗口（TD-16：100ms 合并突发）。 */
export const REFETCH_DEBOUNCE_MS = 100;

export function useWs(options: WsOptions) {
  const connected: Ref<boolean> = ref(false);
  const wsFactory = options.wsFactory ?? ((url: string) => new WebSocket(url));
  const minMs = options.reconnect?.minMs ?? 1000;
  const maxMs = options.reconnect?.maxMs ?? 30_000;

  let ws: WebSocketLike | null = null;
  let retries = 0;
  let closed = false;
  let reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  let debounceTimer: ReturnType<typeof setTimeout> | null = null;
  let pendingResources = new Set<RefetchResource>();

  const flushRefetch = () => {
    debounceTimer = null;
    const resources = pendingResources;
    pendingResources = new Set();
    for (const r of resources) options.refetch?.(r);
  };

  /** 状态类 → 去抖合并（同一窗口内多事件/多资源合并为一批 refetch）。 */
  const scheduleRefetch = (resource: RefetchResource) => {
    pendingResources.add(resource);
    if (debounceTimer === null)
      debounceTimer = setTimeout(flushRefetch, REFETCH_DEBOUNCE_MS);
  };

  /** 全量 resync（onOpen / 页面可见性恢复；TD-16 丢失事件兜底）。 */
  const resync = () => {
    for (const r of RESYNC_RESOURCES) options.refetch?.(r);
    options.onResync?.();
  };

  const onVisibility = () => {
    if (typeof document !== "undefined" && document.visibilityState === "visible" && !closed)
      resync();
  };

  const scheduleReconnect = () => {
    if (closed || reconnectTimer !== null) return;
    const delay = Math.min(minMs * 2 ** retries, maxMs);
    retries += 1;
    reconnectTimer = setTimeout(() => {
      reconnectTimer = null;
      open();
    }, delay);
  };

  const handleMessage = (data: string) => {
    let evt: { ev?: string };
    try {
      evt = JSON.parse(data);
    } catch {
      return; // 非 JSON 帧丢弃（提示通道，真相在 REST）
    }
    const resource = STATE_EVENT_RESOURCES[evt.ev ?? ""];
    if (resource !== undefined) scheduleRefetch(resource);
    else if (evt.ev === "mapping_stats") {
      const m = evt as { id: string; rateUp: number; rateDown: number; path: "direct" | "relay" };
      options.onStats?.({ id: m.id, rateUp: m.rateUp, rateDown: m.rateDown, path: m.path });
    }
  };

  const open = () => {
    if (closed) return;
    const socket = wsFactory(options.url);
    ws = socket;
    socket.onopen = () => {
      connected.value = true;
      retries = 0; // 连接成功归零（06 §4）
      resync();
    };
    socket.onmessage = (ev) => handleMessage(ev.data);
    socket.onclose = () => {
      connected.value = false;
      ws = null;
      scheduleReconnect();
    };
    socket.onerror = () => socket.close();
  };

  if (typeof document !== "undefined")
    document.addEventListener("visibilitychange", onVisibility);
  open();

  /** 停止重连并释放（组件卸载自动触发）。 */
  const close = () => {
    if (closed) return;
    closed = true;
    if (typeof document !== "undefined")
      document.removeEventListener("visibilitychange", onVisibility);
    if (reconnectTimer !== null) clearTimeout(reconnectTimer);
    if (debounceTimer !== null) clearTimeout(debounceTimer);
    ws?.close();
    ws = null;
    connected.value = false;
  };

  // 在 setup/活跃作用域内使用时随组件卸载自动关闭（store 等作用域外使用须手动 close）
  if (getCurrentScope()) onScopeDispose(close);

  return { connected, close };
}

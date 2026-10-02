// M1-32 系统与设备自身状态 store（06 §4）：/api/system/state + /api/device，10s 轮询兜底；
// WS onOpen/visibilitychange 全量 resync 的主目标（TD-16：REST 是真相）。
import { ref } from "vue";
import { defineStore } from "pinia";
import type { CapabilityMode, SubnetConflictState, SystemPhase } from "@p2p/ui-shared";
import { ApiPaths } from "@p2p/ui-shared";
import { api } from "../api";

export interface SystemStateView {
  phase: SystemPhase;
  serverReachable: boolean;
  protocolVersion: number;
  conflict: SubnetConflictState | null; // M3-13：虚拟网段冲突（FR-C-204；null=无）
}

export interface DeviceView {
  deviceId: string | null;
  remoteCode: string | null;
  virtualIp: string | null;
  username: string | null;
  capability: CapabilityMode;
}

const POLL_MS = 10_000;

export const useSystemStore = defineStore("system", () => {
  const state = ref<SystemStateView | null>(null);
  const device = ref<DeviceView | null>(null);
  let timer: ReturnType<typeof setInterval> | null = null;

  async function refresh() {
    // 并行拉取：任一失败保留旧值（服务不可达由 useWs 全局条提示，不清空页面）
    const [s, d] = await Promise.allSettled([
      api.get<SystemStateView>(ApiPaths.SystemState),
      api.get<DeviceView>(ApiPaths.Device),
    ]);
    if (s.status === "fulfilled") state.value = s.value;
    if (d.status === "fulfilled") device.value = d.value;
  }

  function startPolling() {
    if (timer !== null) return;
    void refresh();
    timer = setInterval(() => void refresh(), POLL_MS);
  }

  function stopPolling() {
    if (timer !== null) clearInterval(timer);
    timer = null;
  }

  return { state, device, refresh, startPolling, stopPolling };
});

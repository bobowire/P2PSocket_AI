// M3-09 仪表盘 store（06 §3、FR-S-810）：GET /api/dashboard 六指标组 + 10s 定时刷新。
import { ref } from "vue";
import { defineStore } from "pinia";
import type { DashboardView } from "@p2p/ui-shared";
import { ServerApiPaths } from "@p2p/ui-shared";
import { api } from "../api";

export const DASHBOARD_REFRESH_MS = 10_000;

export const useDashboardStore = defineStore("server-dashboard", () => {
  const data = ref<DashboardView | null>(null);
  const loaded = ref(false);
  let timer: ReturnType<typeof setInterval> | null = null;

  async function refresh() {
    try {
      data.value = await api.get<DashboardView>(ServerApiPaths.Dashboard);
      loaded.value = true;
    } catch {
      // 401 已由拦截器跳登录；其余（瞬时不可达）保留下次轮询
    }
  }

  function startPolling() {
    if (timer === null) timer = setInterval(() => void refresh(), DASHBOARD_REFRESH_MS);
  }

  function stopPolling() {
    if (timer !== null) clearInterval(timer);
    timer = null;
  }

  return { data, loaded, refresh, startPolling, stopPolling };
});

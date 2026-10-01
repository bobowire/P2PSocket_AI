// M3-11 中继运维 store（06 §3、FR-S-824、TD-23）：配置现值（开关/限速）+会话表快照。
// PUT 即时生效双路径（M3-07）：relay_enabled 0x74 现读、rate_limit 进程内直调 UpdateRate；
// 会话表 10s 轮询观察（同 dashboard 口径）。
import { ref } from "vue";
import { defineStore } from "pinia";
import type { RelayConfigView, RelaySessionsView } from "@p2p/ui-shared";
import { ServerApiPaths } from "@p2p/ui-shared";
import { api } from "../api";

export const RELAY_POLL_MS = 10_000;

export const useRelayStore = defineStore("server-relay", () => {
  const config = ref<RelayConfigView | null>(null);
  const sessions = ref<RelaySessionsView | null>(null);

  async function refreshConfig() {
    config.value = await api.get<RelayConfigView>(ServerApiPaths.RelayConfig);
  }

  async function refreshSessions() {
    sessions.value = await api.get<RelaySessionsView>(ServerApiPaths.RelaySessions);
  }

  /// 部分更新（至少一项）；响应=服务端回读现值
  async function saveConfig(patch: { relayEnabled?: boolean; rateLimitBytes?: number }) {
    config.value = await api.put<RelayConfigView>(ServerApiPaths.RelayConfig, patch);
  }

  return { config, sessions, refreshConfig, refreshSessions, saveConfig };
});

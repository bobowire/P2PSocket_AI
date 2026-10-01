// M3-11 系统配置与审计 store（06 §3、FR-S-825、NFR-54）：server_config 白名单 18 键现值+
// 行级部分更新（PUT 单键，服务端全量校验整单拒绝）+审计 newest-first 过滤分页。
import { ref } from "vue";
import { defineStore } from "pinia";
import type { AuditLogListView, ConfigListView } from "@p2p/ui-shared";
import { ServerApiPaths } from "@p2p/ui-shared";
import { api } from "../api";

export const AUDIT_PAGE_SIZE = 20;

export const useSystemStore = defineStore("server-system", () => {
  const config = ref<ConfigListView | null>(null);
  const audits = ref<AuditLogListView | null>(null);
  const auditPage = ref(1);
  const auditEvent = ref(""); // 空=全量

  async function refreshConfig() {
    config.value = await api.get<ConfigListView>(ServerApiPaths.SystemConfig);
  }

  /// 行级保存：PUT {key: value}（部分更新）；响应=全集回读
  async function saveKey(key: string, value: string) {
    config.value = await api.put<ConfigListView>(ServerApiPaths.SystemConfig, { [key]: value });
  }

  async function refreshAudits() {
    const q = new URLSearchParams({ page: String(auditPage.value), pageSize: String(AUDIT_PAGE_SIZE) });
    if (auditEvent.value) q.set("event", auditEvent.value);
    audits.value = await api.get<AuditLogListView>(`${ServerApiPaths.AuditLogs}?${q}`);
  }

  async function setAuditPage(p: number) {
    auditPage.value = p;
    await refreshAudits();
  }

  /// 事件过滤（翻回第 1 页再查）
  async function setAuditEvent(event: string) {
    auditEvent.value = event;
    auditPage.value = 1;
    await refreshAudits();
  }

  return { config, audits, auditPage, auditEvent, refreshConfig, saveKey, refreshAudits, setAuditPage, setAuditEvent };
});

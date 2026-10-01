// M3-10 映射页 store（06 §3、FR-S-823）：全量只读 + 按归属设备/投影状态过滤（服务端 ?deviceId=/?status=）。
import { computed, ref } from "vue";
import { defineStore } from "pinia";
import type { MappingListView, MappingStatusProjection } from "@p2p/ui-shared";
import { ServerApiPaths } from "@p2p/ui-shared";
import { api } from "../api";

export const MAPPING_STATUS_OPTIONS: MappingStatusProjection[] =
  ["direct", "relay", "failed", "invalid", "unknown"];

export const useMappingsStore = defineStore("server-mappings", () => {
  const list = ref<MappingListView | null>(null);
  const deviceFilter = ref(""); // ""=全部（归属设备维度）
  const statusFilter = ref(""); // ""=全部（TD-22 投影态）

  /// 当前列表状态统计（过滤后 items 聚合——清单行"状态 Tag 统计"）
  const statusCounts = computed(() => {
    const counts: Record<string, number> = {};
    for (const m of list.value?.items ?? []) counts[m.status] = (counts[m.status] ?? 0) + 1;
    return counts;
  });

  async function refresh() {
    const q = new URLSearchParams();
    if (deviceFilter.value) q.set("deviceId", deviceFilter.value);
    if (statusFilter.value) q.set("status", statusFilter.value);
    const qs = q.toString();
    list.value = await api.get<MappingListView>(
      `${ServerApiPaths.Mappings}${qs ? `?${qs}` : ""}`);
  }

  async function setDeviceFilter(id: string) {
    deviceFilter.value = id;
    await refresh();
  }

  async function setStatusFilter(status: string) {
    statusFilter.value = status;
    await refresh();
  }

  return { list, deviceFilter, statusFilter, statusCounts, refresh, setDeviceFilter, setStatusFilter };
});

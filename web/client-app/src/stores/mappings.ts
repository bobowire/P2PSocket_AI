// M1-32 映射 store（06 §4）：列表与状态经 REST；mapping_stats（1s 速率）直写行内数据——
// 下秒覆盖、丢失无害（TD-16 高频数值类）。
import { computed, reactive, ref } from "vue";
import { defineStore } from "pinia";
import type { MappingState, MappingStatsEvent, MappingView } from "@p2p/ui-shared";
import { ApiPaths, apiPath } from "@p2p/ui-shared";
import { api } from "../api";

export interface MappingRates {
  rateUp: number;
  rateDown: number;
  path: string;
}

export interface MappingFormInput {
  id: string | null;
  name: string;
  localPort: number;
  proto: string;
  targetRemoteCode: string;
  targetPort: number;
}

export const useMappingStore = defineStore("mappings", () => {
  const items = ref<MappingView[]>([]);
  const total = ref(0);
  const loaded = ref(false);
  /** mapping_id → 实时速率（WS 直写；缺项视为 0） */
  const rates = reactive(new Map<string, MappingRates>());

  const overview = computed(() => {
    const count = (s: MappingState) => items.value.filter((m) => m.state === s).length;
    return {
      total: items.value.length,
      direct: count("direct"),
      punching: count("punching"),
      failed: count("failed"),
      disabled: count("disabled"),
    };
  });

  async function refresh() {
    try {
      const page = await api.get<{ items: MappingView[]; total: number }>(ApiPaths.Mappings, {
        params: { page: 1, pageSize: 100 },
      });
      items.value = page.items;
      total.value = page.total;
      // 已删除映射的速率残项清场
      const live = new Set(page.items.map((m) => m.mappingId));
      for (const id of [...rates.keys()]) if (!live.has(id)) rates.delete(id);
    } catch {
      /* 保留旧列表（服务不可达由全局条提示） */
    } finally {
      loaded.value = true;
    }
  }

  /** WS mapping_stats 直写（04 §2.8 高频数值类）。 */
  function applyStats(ev: Pick<MappingStatsEvent, "id" | "rateUp" | "rateDown" | "path">) {
    rates.set(ev.id, { rateUp: ev.rateUp, rateDown: ev.rateDown, path: ev.path });
  }

  async function create(input: MappingFormInput) {
    await api.post(ApiPaths.Mappings, {
      name: input.name,
      localPort: input.localPort,
      proto: input.proto,
      targetRemoteCode: input.targetRemoteCode,
      targetPort: input.targetPort,
    });
    await refresh();
  }

  async function update(input: MappingFormInput) {
    await api.put(apiPath(ApiPaths.MappingsById, { id: input.id! }), {
      name: input.name,
      localPort: input.localPort,
      proto: input.proto,
      targetRemoteCode: input.targetRemoteCode,
      targetPort: input.targetPort,
    });
    await refresh();
  }

  async function enable(id: string) {
    await api.post(apiPath(ApiPaths.MappingsByIdEnable, { id }));
    await refresh();
  }

  async function disable(id: string) {
    await api.post(apiPath(ApiPaths.MappingsByIdDisable, { id }));
    await refresh();
  }

  async function retry(id: string) {
    await api.post(apiPath(ApiPaths.MappingsByIdRetry, { id }));
    await refresh();
  }

  async function remove(id: string) {
    await api.del(apiPath(ApiPaths.MappingsById, { id }));
    await refresh();
  }

  return {
    items,
    total,
    loaded,
    rates,
    overview,
    refresh,
    applyStats,
    create,
    update,
    enable,
    disable,
    retry,
    remove,
  };
});

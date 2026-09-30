// M2-28 开放内网段 store（06 §2/§4、FR-C-806）：/api/lan-segments 镜像即数据源
// （0x63 本机管理类——passive 亦允许）；增删后本地过滤即时生效，移除段触发服务端
// 0x75 失效推送（前端经 WS mapping_state → mappings refetch 置灰）。
import { ref } from "vue";
import { defineStore } from "pinia";
import type { LanSegmentView } from "@p2p/ui-shared";
import { ApiPaths, apiPath } from "@p2p/ui-shared";
import { api } from "../api";

export const useSegmentStore = defineStore("segments", () => {
  const items = ref<LanSegmentView[]>([]);
  const loaded = ref(false);

  async function refresh() {
    try {
      items.value = await api.get<LanSegmentView[]>(ApiPaths.LanSegments);
    } catch {
      /* 不可达保留旧列表（全局条另提示） */
    } finally {
      loaded.value = true;
    }
  }

  /** 新增（0x63 新建须 Enabled=true；裸 IP 由服务端规范化 /32 或 /128）。 */
  async function add(cidr: string) {
    const seg = await api.post<LanSegmentView>(ApiPaths.LanSegments, { cidr });
    await refresh();
    return seg;
  }

  /** 移除（0x63 enabled=false；段内存量映射将收 0x75 invalid）。 */
  async function remove(segmentId: string) {
    await api.del(apiPath(ApiPaths.LanSegmentsById, { id: segmentId }));
    await refresh();
  }

  return { items, loaded, refresh, add, remove };
});

// M1-32 设备发现 store（06 §4）：/api/devices（0x40 分页循环由本地 API 侧完成，前端单次拉全量）。
// device_list WS 事件属 M2；M1 以 10s 轮询 refetch 兜底。
import { ref } from "vue";
import { defineStore } from "pinia";
import { ApiPaths } from "@p2p/ui-shared";
import { api } from "../api";

/** /api/devices 列表项（04 §2.4；生成物未含匿名投影——按端点契约手写镜像，集成测试锁定形状）。 */
export interface DeviceItem {
  deviceId: string;
  deviceName: string;
  remoteCode: string;
  virtualIp: string;
  online: boolean;
  groups: string[];
  lanSegments: string[];
}

const POLL_MS = 10_000;

export const useDeviceStore = defineStore("devices", () => {
  const items = ref<DeviceItem[]>([]);
  const loaded = ref(false);
  let timer: ReturnType<typeof setInterval> | null = null;

  async function refresh() {
    try {
      items.value = await api.get<DeviceItem[]>(ApiPaths.Devices);
    } catch {
      /* passive（2002）/不可达：保留旧列表，页面按能力模式禁写 */
    } finally {
      loaded.value = true;
    }
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

  return { items, loaded, refresh, startPolling, stopPolling };
});

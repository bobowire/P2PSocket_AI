// M1-32 设备发现 store（06 §4）；M2-28：10s 轮询移除——device_list WS 事件驱动
// refetch（0x41 服务端推送链 M2-10；onOpen/visibilitychange 全量 resync 兜底，TD-16）；
// 附设备级中继回退开关状态（/api/peers 本地持久化，不经控制协议——passive 亦可用）。
import { ref } from "vue";
import { defineStore } from "pinia";
import { ApiPaths, apiPath } from "@p2p/ui-shared";
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

export const useDeviceStore = defineStore("devices", () => {
  const items = ref<DeviceItem[]>([]);
  const loaded = ref(false);
  /** deviceId → relayFallback（D3 v0.4：目标设备级配置，本地 peers.json 真相） */
  const peers = ref<Record<string, boolean>>({});

  async function refresh() {
    try {
      items.value = await api.get<DeviceItem[]>(ApiPaths.Devices);
      void refreshPeers(); // 回退开关随列表刷新（失败静默——旧值保留）
    } catch {
      /* passive（2002）/不可达：保留旧列表，页面按能力模式禁写 */
    } finally {
      loaded.value = true;
    }
  }

  /** 并行拉取各设备回退开关现值（GET 幂等只读）。 */
  async function refreshPeers() {
    const results = await Promise.allSettled(
      items.value.map(async (d) => ({
        deviceId: d.deviceId,
        relayFallback: (await api.get<{ relayFallback: boolean }>(
          apiPath(ApiPaths.PeersByDeviceid, { deviceId: d.deviceId }),
        )).relayFallback,
      })),
    );
    for (const r of results)
      if (r.status === "fulfilled") peers.value[r.value.deviceId] = r.value.relayFallback;
  }

  /** 切换中继回退（PUT /api/peers/{deviceId}；本地即时生效）。 */
  async function setPeer(deviceId: string, relayFallback: boolean) {
    await api.put(apiPath(ApiPaths.PeersByDeviceid, { deviceId }), { relayFallback });
    peers.value = { ...peers.value, [deviceId]: relayFallback };
  }

  return { items, loaded, peers, refresh, refreshPeers, setPeer };
});

// M1-32 设备发现 store（06 §4）；M2-28：10s 轮询移除——device_list WS 事件驱动
// refetch（0x41 服务端推送链 M2-10；onOpen/visibilitychange 全量 resync 兜底，TD-16）；
// 附设备级中继回退开关状态（/api/peers 本地持久化，不经控制协议——passive 亦可用）。
// M7-05（设计 12 §8.2）：peers 扩双字段 { relayFallback, punchMode }（设备级打洞优先模式）+
// setPunchMode（PUT 单字段合并写）+ tunnelBearers（隧道类型列数据源——随 refresh 拉 tunnels，
// 复用 GET /api/diagnostics/tunnels，passive 亦可达）。
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

/** /api/peers/{deviceId} 响应（M7-01 双字段；匿名投影手写镜像——设计 12 §8.4）。 */
export interface PeerConfig {
  relayFallback: boolean;
  punchMode: string; // "tcp" | "kcp"（配置层口径，默认 tcp）
}

export const useDeviceStore = defineStore("devices", () => {
  const items = ref<DeviceItem[]>([]);
  const loaded = ref(false);
  /** deviceId → 设备级配置（D3 v0.4 回退 + M7 决策①打洞优先模式；本地 peers.json 真相） */
  const peers = ref<Record<string, PeerConfig>>({});
  /** deviceId → 隧道承载形态（M7-05 隧道类型列：relay/kcp/raw-udp/tcp；无隧道无键） */
  const tunnelBearers = ref<Record<string, string>>({});

  async function refresh() {
    try {
      items.value = await api.get<DeviceItem[]>(ApiPaths.Devices);
      void refreshPeers(); // 设备级配置随列表刷新（失败静默——旧值保留）
      void refreshTunnelBearers(); // 隧道类型列同刷（失败静默）
    } catch {
      /* passive（2002）/不可达：保留旧列表，页面按能力模式禁写 */
    } finally {
      loaded.value = true;
    }
  }

  /** 并行拉取各设备配置现值（GET 幂等只读）。 */
  async function refreshPeers() {
    const results = await Promise.allSettled(
      items.value.map(async (d) => ({
        deviceId: d.deviceId,
        config: await api.get<PeerConfig>(
          apiPath(ApiPaths.PeersByDeviceid, { deviceId: d.deviceId }),
        ),
      })),
    );
    for (const r of results)
      if (r.status === "fulfilled")
        peers.value = { ...peers.value, [r.value.deviceId]: r.value.config };
  }

  /** 隧道承载形态表（GET /api/diagnostics/tunnels：peerDeviceId → bearer；设计 12 §7.3）。 */
  async function refreshTunnelBearers() {
    try {
      const resp = await api.get<{ items: { peerDeviceId: string; bearer: string }[] }>(
        ApiPaths.DiagnosticsTunnels,
      );
      const map: Record<string, string> = {};
      for (const t of resp.items) map[t.peerDeviceId] = t.bearer;
      tunnelBearers.value = map;
    } catch {
      /* passive/不可达：保留旧值（无隧道列显示 —） */
    }
  }

  /** 切换中继回退（PUT /api/peers/{deviceId} 单字段；本地即时生效——服务端合并写不动 punchMode）。 */
  async function setPeer(deviceId: string, relayFallback: boolean) {
    await api.put(apiPath(ApiPaths.PeersByDeviceid, { deviceId }), { relayFallback });
    peers.value = {
      ...peers.value,
      [deviceId]: { ...peers.value[deviceId], relayFallback },
    };
  }

  /** 切换打洞优先模式（M7-05，设计 12 §8.2：PUT 单字段 punchMode——发送时点取现值，
   * 对后续打洞会话即时生效，同 KcpBearer kill-switch 口径）。 */
  async function setPunchMode(deviceId: string, punchMode: "tcp" | "kcp") {
    await api.put(apiPath(ApiPaths.PeersByDeviceid, { deviceId }), { punchMode });
    peers.value = {
      ...peers.value,
      [deviceId]: { ...peers.value[deviceId], punchMode },
    };
  }

  return {
    items, loaded, peers, tunnelBearers,
    refresh, refreshPeers, refreshTunnelBearers, setPeer, setPunchMode,
  };
});

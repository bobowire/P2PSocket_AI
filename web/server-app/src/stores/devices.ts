// M3-10 设备页 store（06 §3、FR-S-821）：全量列表 + 禁用/解绑/重置远程码（AdminService 转调端点）。
// mappings 页按设备筛选复用本列表（deviceId 选项数据源）。
import { ref } from "vue";
import { defineStore } from "pinia";
import type { DeviceListView, RemoteCodeResult } from "@p2p/ui-shared";
import { ServerApiPaths, apiPath } from "@p2p/ui-shared";
import { api } from "../api";

export const useDevicesStore = defineStore("server-devices", () => {
  const list = ref<DeviceListView | null>(null);

  async function refresh() {
    list.value = await api.get<DeviceListView>(ServerApiPaths.Devices);
  }

  async function disable(id: string) {
    await api.post(apiPath(ServerApiPaths.DevicesByIdDisable, { id }));
    await refresh();
  }

  async function enable(id: string) {
    await api.post(apiPath(ServerApiPaths.DevicesByIdEnable, { id }));
    await refresh();
  }

  async function unbind(id: string) {
    await api.post(apiPath(ServerApiPaths.DevicesByIdUnbind, { id }));
    await refresh();
  }

  /// 重置成功返回新远程码（响应即唯一明文出口，页面 alert 一次性展示）
  async function resetRemoteCode(id: string): Promise<string> {
    const r = await api.post<RemoteCodeResult>(apiPath(ServerApiPaths.DevicesByIdResetRemoteCode, { id }));
    await refresh();
    return r.remoteCode;
  }

  return { list, refresh, disable, enable, unbind, resetRemoteCode };
});

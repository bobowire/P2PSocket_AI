// M3-10 用户页 store（06 §3、FR-S-820）：分页列表 + 禁用/启用 + 密码重置（临时密码一次性返回）+
// 注册开关（registration_open 键经 system/config 端点，M3-10 任务行"端点不加法"）。
import { ref } from "vue";
import { defineStore } from "pinia";
import type { ConfigListView, TempPasswordResult, UserListView } from "@p2p/ui-shared";
import { ServerApiPaths, apiPath } from "@p2p/ui-shared";
import { api } from "../api";

export const USER_PAGE_SIZE = 20;

export const useUsersStore = defineStore("server-users", () => {
  const list = ref<UserListView | null>(null);
  const page = ref(1);
  const registrationOpen = ref<boolean | null>(null); // null=未加载（GET 后定值）

  async function refresh() {
    list.value = await api.get<UserListView>(
      `${ServerApiPaths.Users}?page=${page.value}&pageSize=${USER_PAGE_SIZE}`,
    );
  }

  /// system/config 的 registration_open 键（"0"|"1"）——用户页开关数据源
  async function loadRegistration() {
    const cfg = await api.get<ConfigListView>(ServerApiPaths.SystemConfig);
    const v = cfg.items.find((i) => i.key === "registration_open")?.value;
    registrationOpen.value = v === "1";
  }

  async function setRegistration(open: boolean) {
    // PUT 部分更新（单键）；服务端白名单校验+审计（M3-08），registration_open 运行期现读即时生效
    await api.put(ServerApiPaths.SystemConfig, { registration_open: open ? "1" : "0" });
    registrationOpen.value = open;
  }

  async function setPage(p: number) {
    page.value = p;
    await refresh();
  }

  async function disable(id: string) {
    await api.post(apiPath(ServerApiPaths.UsersByIdDisable, { id }));
    await refresh();
  }

  async function enable(id: string) {
    await api.post(apiPath(ServerApiPaths.UsersByIdEnable, { id }));
    await refresh();
  }

  /// 重置成功返回临时密码（响应即唯一明文出口，页面 alert 一次性展示）
  async function resetPassword(id: string): Promise<string> {
    const r = await api.put<TempPasswordResult>(apiPath(ServerApiPaths.UsersByIdPasswordReset, { id }));
    return r.tempPassword;
  }

  return {
    list, page, registrationOpen,
    refresh, setPage, loadRegistration, setRegistration, disable, enable, resetPassword,
  };
});

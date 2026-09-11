// M1-32 登录态 store（06 §4）：login_state 事件不直接改 store，仅触发 refresh（TD-16）。
// 0x21 登录属主动类：passive 会话本地拒发 2002——重新登录须先重连控制通道（02 §2.5）。
import { ref } from "vue";
import { defineStore } from "pinia";
import type { CapabilityMode } from "@p2p/ui-shared";
import { ApiPaths } from "@p2p/ui-shared";
import { api } from "../api";

export interface MeView {
  username: string | null;
  mode: CapabilityMode;
}

export const useAuthStore = defineStore("auth", () => {
  const me = ref<MeView | null>(null);

  async function refresh() {
    try {
      me.value = await api.get<MeView>(ApiPaths.AuthMe);
    } catch {
      me.value = null; // 本地服务不可达：视为未知态（全局条另提示）
    }
  }

  async function login(username: string, password: string) {
    await api.post<{ username: string; mode: CapabilityMode }>(ApiPaths.AuthLogin, {
      username,
      password,
    });
    await refresh();
  }

  async function register(username: string, password: string) {
    await api.post(ApiPaths.AuthRegister, { username, password });
  }

  async function logout() {
    await api.post(ApiPaths.AuthLogout);
    await refresh();
  }

  return { me, refresh, login, register, logout };
});

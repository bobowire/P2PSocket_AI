// M3-09 管理员会话 store（06 §3、FR-S-203）：login 携 mustChangePassword 首登判定；
// 刷新后守卫判据=AUTH_KEY 标记（服务端无 /me 端点——Cookie 8h 会话本体在服务端，
// 失效由 API 面 401 拦截器清除标记并跳登录）。改密成功不裁会话（04 §3.1）——mustChange 归零继续。
import { computed, ref } from "vue";
import { defineStore } from "pinia";
import type { LoginResult } from "@p2p/ui-shared";
import { ServerApiPaths } from "@p2p/ui-shared";
import { AUTH_KEY, api, setUnauthorizedHook } from "../api";

export const useAuthStore = defineStore("server-auth", () => {
  const loggedIn = ref(false);
  const mustChange = ref(false);
  // 刷新存活标记的响应式镜像：sessionStorage 非响应源，computed 直接读会在短路缓存里吞掉
  // 标记翻转——刷新态用户（loggedIn 恒 false）登出时 loggedIn false→false 不触发重算，
  // authed 恒真 → Login.onMounted 回跳后台死循环（M3-09 走查测试抓出）。
  const markerAlive = ref(sessionStorage.getItem(AUTH_KEY) === "1");

  /** 守卫判据：store 记忆 或 刷新存活标记。 */
  const authed = computed(() => loggedIn.value || markerAlive.value);

  async function login(username: string, password: string) {
    const r = await api.post<LoginResult>(ServerApiPaths.AuthLogin, { username, password });
    loggedIn.value = true;
    mustChange.value = r.mustChangePassword;
    sessionStorage.setItem(AUTH_KEY, "1");
    markerAlive.value = true;
  }

  async function changePassword(oldPassword: string, newPassword: string) {
    await api.post(ServerApiPaths.AuthChangePassword, { oldPassword, newPassword });
    mustChange.value = false; // 会话保持（04 §3.1 改密不裁会话）
  }

  async function logout() {
    await api.post(ServerApiPaths.AuthLogout).catch(() => undefined);
    expire();
  }

  /** 会话终结（登出/401 失效）：记忆与刷新标记一并清——否则 Login.onMounted 以旧 authed 回跳后台。 */
  function expire() {
    loggedIn.value = false;
    mustChange.value = false;
    sessionStorage.removeItem(AUTH_KEY);
    markerAlive.value = false;
  }

  return { loggedIn, mustChange, authed, login, changePassword, logout, expire };
});

// 401 拦截器联动（api.ts 注入点）：清本 store 记忆——否则跳到 /login 后 Login.onMounted
// 以旧 authed 回跳 /dashboard → 再 401 → ping-pong 死循环。回调内解析 active pinia（导入期无实例）。
setUnauthorizedHook(() => useAuthStore().expire());

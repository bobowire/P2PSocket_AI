// M3-09 API 单例（06 §5）：同源部署（宿主 wwwroot + hash 路由 base './'），baseURL 留空走相对
// 路径（Cookie 会话同源携带）；测试经 VITE_API_BASE 指向 MSW 拦截源。
// 路径常量一律取 api-server 生成物（禁止手写字符串路径，06 §5）。
import { ApiError, ServerApiPaths, useApi } from "@p2p/ui-shared";

export const AUTH_KEY = "p2p-admin-auth"; // 刷新后守卫判据（会话本体在服务端 Cookie，8h）

export const api = useApi(import.meta.env.VITE_API_BASE ?? "");

// 401/会话失效全局跳登录（管理面无 WS，API 面即唯一探测面）：清标记 + hash 跳转
// （useApi 外观在错误链尾端已映射为 ApiError：HTTP 401 → -401、HTTP 200+code 2001 → 2001）
// store 记忆联动清理经注入回调（api ← stores/auth 单向依赖，避免循环导入；模块期无 active
// pinia——回调内延迟解析）。不清记忆则 Login.onMounted 以旧 authed 回跳 /dashboard → 再 401 死循环。
let onUnauthorized: (() => void) | null = null;
export function setUnauthorizedHook(fn: () => void) {
  onUnauthorized = fn;
}

// 认证端点自身的 401 是凭据错误（登录错/改密旧密码错=HTTP 401{2001}，AdminAuthApi 口径），
// 与"会话失效"同码——须豁免全局会话处理，否则改密输错一次旧密码即被误判掉线（04 §3.1：会话仍在）。
const CREDENTIAL_401_PATHS = new Set([ServerApiPaths.AuthLogin, ServerApiPaths.AuthChangePassword]);

api.http.interceptors.response.use(
  (r) => r,
  (e) => {
    if (
      e instanceof ApiError &&
      (e.code === -401 || e.code === 2001) &&
      !CREDENTIAL_401_PATHS.has(e.url ?? "")
    ) {
      sessionStorage.removeItem(AUTH_KEY);
      onUnauthorized?.();
      if (!location.hash.startsWith("#/login")) location.hash = "#/login";
    }
    throw e;
  },
);

// M1-31 useApi（06 §1/§4）：axios 实例 + 错误码拦截映射文案（04 §5 码表）。
// envelope：HTTP 200 + code!=0 → ApiError（映射文案优先，服务端 msg 兜底）；
// 网络层/非 200 → 统一 5003 语义"本地服务不可达"（本地回环，进程存活检测见 useWs 全局条）。
import axios, { type AxiosInstance, type AxiosRequestConfig } from "axios";
import type { ApiEnvelope } from "../types/api";

/** 错误码 → 用户文案（04 §5 错误码表；顺序同表）。 */
export const ERROR_TEXT: Record<number, string> = {
  0: "成功",
  1001: "参数或格式错误",
  1002: "资源不存在",
  1003: "操作冲突（端口占用或重名）",
  2001: "未登录或凭据失效",
  2002: "哑节点模式不允许此操作",
  2003: "没有权限执行此操作",
  2004: "服务端已关闭注册",
  3001: "邀请码无效或已撤销",
  3002: "已提交申请，等待分组所有者审批",
  4001: "目标设备不在授权范围（需同分组或同账号）",
  4002: "目标地址不在对端开放网段",
  4003: "远程码不存在或已被重置",
  4004: "设备在线无法重注册，请联系管理员解绑",
  4005: "目标设备离线",
  5001: "打洞失败",
  5002: "服务端中继已关闭",
  5003: "本地服务与控制服务端断开",
  5004: "协议版本不兼容，请升级客户端",
  5005: "与服务器时间偏差过大，请校准系统时间",
};

/** 业务/传输错误（code 为 04 §5 业务码，网络层错误 code = -HTTP 状态码 / 0=无响应）。 */
export class ApiError extends Error {
  constructor(
    public readonly code: number,
    message: string,
  ) {
    super(message);
    this.name = "ApiError";
  }
}

/** 生成的路径模板填参（{id} 占位；前端禁止手写字符串路径，06 §5）。 */
export function apiPath(template: string, vars: Record<string, string>): string {
  return template.replace(/\{(\w+)\}/g, (_, k: string) => vars[k] ?? `{${k}}`);
}

/** 拦截器装配的 axios 实例（envelope 解包为 data；失败抛 ApiError）。 */
export function createApiClient(baseURL = ""): AxiosInstance {
  const http = axios.create({ baseURL, timeout: 10_000 });
  http.interceptors.response.use(
    (resp) => {
      const env = resp.data as ApiEnvelope<unknown>;
      if (env && typeof env.code === "number" && env.code !== 0)
        throw new ApiError(env.code, ERROR_TEXT[env.code] ?? env.msg ?? `错误 ${env.code}`);
      return resp;
    },
    (error) => {
      if (axios.isAxiosError(error)) {
        const status = error.response?.status ?? 0;
        throw new ApiError(status === 0 ? 0 : -status, status === 0
          ? "无法连接本地服务（进程可能未运行）"
          : `本地服务响应异常（HTTP ${status}）`);
      }
      throw error;
    },
  );
  return http;
}

/** 组合式入口（06 §1）：get/post/put/del 直接返回 envelope.data。 */
export function useApi(baseURL = "") {
  const http = createApiClient(baseURL);
  const unwrap = <T>(p: Promise<{ data: ApiEnvelope<T> }>) => p.then((r) => r.data.data);
  return {
    http,
    get: <T>(url: string, config?: AxiosRequestConfig) => unwrap<T>(http.get(url, config)),
    post: <T>(url: string, body?: unknown, config?: AxiosRequestConfig) =>
      unwrap<T>(http.post(url, body, config)),
    put: <T>(url: string, body?: unknown, config?: AxiosRequestConfig) =>
      unwrap<T>(http.put(url, body, config)),
    del: <T>(url: string, config?: AxiosRequestConfig) => unwrap<T>(http.delete(url, config)),
  };
}

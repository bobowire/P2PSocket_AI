// axios 实例 + 错误码拦截映射（06 §4；M1-31 完成完整实现）
export function useApi() {
  return {
    async get<T>(url: string): Promise<T> {
      const { default: axios } = await import("axios");
      const resp = await axios.get(url);
      return resp.data.data as T;
    },
  };
}

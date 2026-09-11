// M1-32 API 单例（06 §1/§5）：同源部署（宿主 wwwroot 嵌入 + hash 路由 base './'），
// baseURL 留空走相对路径；测试经 VITE_API_BASE 指向 MSW 拦截源（node 下 axios 需绝对地址）。
// 路径常量一律取生成物（禁止手写字符串路径，06 §5）。
import { useApi } from "@p2p/ui-shared";

export const api = useApi(import.meta.env.VITE_API_BASE ?? "");

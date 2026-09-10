// M1-31 useApi 测试（MSW 模拟本地 Web，04 §0 envelope / §5 码表映射）。
import { afterAll, afterEach, beforeAll, describe, expect, it } from "vitest";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import { ApiError, apiPath, createApiClient, ERROR_TEXT, useApi } from "./useApi";

const envelope = (code: number, msg = "ok", data: unknown = null) =>
  HttpResponse.json({ code, msg, data });

const server = setupServer(
  http.get("http://localhost/api/ok", () => envelope(0, "ok", { value: 42 })),
  http.get("http://localhost/api/biz", () => envelope(4001, "target_not_authorized", null)),
  http.get("http://localhost/api/unknown-code", () => envelope(9999, "custom_msg", null)),
  http.get("http://localhost/api/http500", () => new HttpResponse(null, { status: 500 })),
);

beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

describe("useApi envelope 与错误码拦截", () => {
  it("code=0 解包 data", async () => {
    const api = useApi("http://localhost");
    expect(await api.get<{ value: number }>("/api/ok")).toEqual({ value: 42 });
  });

  it("code!=0 抛 ApiError 且文案取码表映射（4001）", async () => {
    const api = useApi("http://localhost");
    const err = await api.get("/api/biz").catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).code).toBe(4001);
    expect((err as ApiError).message).toBe(ERROR_TEXT[4001]);
  });

  it("码表外 code 回退服务端 msg", async () => {
    const api = useApi("http://localhost");
    const err = await api.get("/api/unknown-code").catch((e: unknown) => e);
    expect((err as ApiError).code).toBe(9999);
    expect((err as ApiError).message).toBe("custom_msg");
  });

  it("非 200 响应映射为 HTTP 层 ApiError（负状态码）", async () => {
    const api = useApi("http://localhost");
    const err = await api.get("/api/http500").catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).code).toBe(-500);
  });

  it("网络不通（连接拒绝）→ code 0 + 本地服务不可达文案", async () => {
    // 无人监听端口：MSW 拦截不到原生 net 层错误——直接用拒绝实例验证映射
    const http2 = createApiClient("http://127.0.0.1:1");
    const err = await http2.get("/x").catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).code).toBe(0);
    expect((err as ApiError).message).toContain("无法连接本地服务");
  });

  it("apiPath 填参（生成路径模板 {id}）", () => {
    expect(apiPath("/api/mappings/{id}", { id: "abc" })).toBe("/api/mappings/abc");
    expect(apiPath("/api/mappings/{id}/enable", { id: "abc" })).toBe("/api/mappings/abc/enable");
  });
});

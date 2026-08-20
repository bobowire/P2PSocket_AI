import { http, HttpResponse } from "msw";

// MSW mock 处理器（04 §2 契约的镜像；M1-31/32 随页面补全全量端点）
export const handlers = [
  http.get("/api/system/state", () =>
    HttpResponse.json({ code: 0, msg: "ok", data: { phase: "unregistered", serverReachable: false, protocolVersion: 1 } }),
  ),
  http.get("/api/device", () =>
    HttpResponse.json({ code: 0, msg: "ok", data: { deviceId: null, remoteCode: null, virtualIp: null, username: null, capability: "passive" } }),
  ),
];

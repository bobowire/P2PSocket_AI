// M1-32 测试装配（每个测试文件加载）：
// - ResizeObserver 桩：Element Plus el-table/el-slider 布局探测依赖（happy-dom 未实现）；
// - 全局怪异 WebSocket 不在此处理——App.vue（唯一 WS 接线点）不进单测（TD-16 逻辑在 ui-shared useWs 已测）。
class ResizeObserverStub implements ResizeObserver {
  observe(): void { /* no-op */ }
  unobserve(): void { /* no-op */ }
  disconnect(): void { /* no-op */ }
}

globalThis.ResizeObserver ??= ResizeObserverStub as unknown as typeof ResizeObserver;

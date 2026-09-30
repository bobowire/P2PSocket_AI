// M3-09 测试装配（client-app 同款）：ResizeObserver 桩（Element Plus 布局探测，happy-dom 未实现）。
class ResizeObserverStub implements ResizeObserver {
  observe(): void { /* no-op */ }
  unobserve(): void { /* no-op */ }
  disconnect(): void { /* no-op */ }
}

globalThis.ResizeObserver ??= ResizeObserverStub as unknown as typeof ResizeObserver;

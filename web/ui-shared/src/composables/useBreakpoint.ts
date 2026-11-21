// M8-04 小屏断点基础设施（设计 13 §7，FR-C-814 前置）：matchMedia <768px——浏览器/WebView
// 均原生支持（SSR 无关）；视口变化监听保持响应式（旋转/分屏/PC 窗口拖拽即时切换）。
// 断点常量集中此处——全站首个媒体断点（V-2 评估欠账，本里程碑补齐）。
import { onBeforeUnmount, onMounted, ref, type Ref } from "vue";

/** 移动断点（px，设计 13 §7：<768px 为小屏——手机/平板竖屏/PC 窄窗口同受益）。 */
export const MOBILE_BREAKPOINT = 768;

/** 小屏断点响应式：isMobile=matchMedia(max-width:767px) 现值；width=视口宽（诊断/测试断言用）。
 * matchMedia 不可用环境（SSR）恒 false——宿主均为浏览器/WebView，仅防御。 */
export function useBreakpoint(): { isMobile: Ref<boolean>; width: Ref<number> } {
  const isMobile = ref(false);
  const width = ref(0);
  let mql: MediaQueryList | null = null;
  const supported = typeof window !== "undefined" && typeof window.matchMedia === "function";

  const sync = () => {
    isMobile.value = mql?.matches ?? false;
    width.value = window.innerWidth;
  };

  onMounted(() => {
    if (!supported) return;
    mql = window.matchMedia(`(max-width: ${MOBILE_BREAKPOINT - 1}px)`);
    sync();
    mql.addEventListener("change", sync);
  });
  onBeforeUnmount(() => mql?.removeEventListener("change", sync));

  return { isMobile, width };
}

/** 弹窗宽度（设计 13 §7：小屏 94vw 封顶、桌面原宽）——CSS min() 一处表达，桌面路径零变化。 */
export function dialogWidth(base: string): string {
  return `min(${base}, 94vw)`;
}

// 简易版科技感主题类集中管理（2026-11-21 移动端走查修复）：原 SimpleLayout 挂载/卸载钩子
// 加减 html.dark+simple-tech 在真机（Android WebView）上存在卸载清理不生效的路径——残留
// simple-tech 而 dark 缺失即"黑底白卡黑字"（高级版整页不可读）。改为路由派生值 watch +
// immediate 幂等切换：任何路由变化都重算一次，无时序竞态，手改类后路由一变即自愈。
// 独立成模块以供测试直挂（simple.test.ts 主题用例与 App.vue 同走此实现）。
import { computed, watch } from "vue";
import { useRoute } from "vue-router";

/** 按当前路由幂等维护 <html> 的 dark+simple-tech 类（/simple* 挂、其余摘）。 */
export function useSimpleThemeClass(): void {
  const route = useRoute();
  const simpleMode = computed(() => route.path.startsWith("/simple"));
  watch(
    simpleMode,
    (simple) => {
      const cls = document.documentElement.classList;
      cls.toggle("dark", simple);
      cls.toggle("simple-tech", simple);
    },
    { immediate: true },
  );
}

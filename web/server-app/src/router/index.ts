import { createRouter, createWebHashHistory, type RouteRecordRaw } from "vue-router";
import { useAuthStore } from "../stores/auth";

// 06 §3 八路由：/login 与 /dashboard 为 M3-09 交付，其余六页 M3-10/11 落地（当前占位）。
// 守卫：未登录（无 store 记忆亦无刷新标记）一律去 /login；API 面 401 由拦截器兜底跳转。
// routes 独立导出 + createAppRouter 工厂——测试构建隔离路由实例复用同表同守卫（守卫在
// 工厂内注册，否则测试实例"无守卫"走查失真；client-app 无守卫故无此约束）。
export const routes: RouteRecordRaw[] = [
  { path: "/", redirect: "/dashboard" },
  { path: "/login", component: () => import("../pages/Login.vue") },
  { path: "/dashboard", component: () => import("../pages/Dashboard.vue") },
  { path: "/users", component: () => import("../pages/Users.vue") },
  { path: "/devices", component: () => import("../pages/Devices.vue") },
  { path: "/groups", component: () => import("../pages/Groups.vue") },
  { path: "/mappings", component: () => import("../pages/Mappings.vue") },
  { path: "/relay", component: () => import("../pages/Relay.vue") },
  { path: "/system", component: () => import("../pages/System.vue") },
];

export function createAppRouter() {
  const router = createRouter({ history: createWebHashHistory(), routes });
  router.beforeEach((to) => {
    if (to.path !== "/login" && !useAuthStore().authed) return "/login";
  });
  return router;
}

export const router = createAppRouter();

import { createRouter, createWebHashHistory, type RouteRecordRaw } from "vue-router";

// 路由表（06 §2）：六页面 M1-32 落地；/groups 全量替换占位 + /segments M2-28；
// /logs /upgrade → M2-29（FR-C-807/904）。routes 独立导出——测试构建隔离路由实例复用同表。
export const routes: RouteRecordRaw[] = [
  { path: "/", component: () => import("../pages/Dashboard.vue") },
  { path: "/wizard", component: () => import("../pages/Wizard.vue") },
  { path: "/login", component: () => import("../pages/Login.vue") },
  { path: "/devices", component: () => import("../pages/Devices.vue") },
  { path: "/groups", component: () => import("../pages/Groups.vue") },
  { path: "/segments", component: () => import("../pages/Segments.vue") },
  { path: "/mappings", component: () => import("../pages/Mappings.vue") },
  { path: "/settings", component: () => import("../pages/Settings.vue") },
];

export const router = createRouter({
  history: createWebHashHistory(),
  routes,
});

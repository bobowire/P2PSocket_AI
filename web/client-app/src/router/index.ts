import { createRouter, createWebHashHistory, type RouteRecordRaw } from "vue-router";

// 路由表（06 §2）：六页面 M1-32 落地；/groups 占位 M1-33（分组管理本体 → M2）；
// /segments /logs → M2+（FR-C-806/807）。routes 独立导出——测试构建隔离路由实例复用同表。
export const routes: RouteRecordRaw[] = [
  { path: "/", component: () => import("../pages/Dashboard.vue") },
  { path: "/wizard", component: () => import("../pages/Wizard.vue") },
  { path: "/login", component: () => import("../pages/Login.vue") },
  { path: "/devices", component: () => import("../pages/Devices.vue") },
  { path: "/groups", component: () => import("../pages/Groups.vue") },
  { path: "/mappings", component: () => import("../pages/Mappings.vue") },
  { path: "/settings", component: () => import("../pages/Settings.vue") },
];

export const router = createRouter({
  history: createWebHashHistory(),
  routes,
});

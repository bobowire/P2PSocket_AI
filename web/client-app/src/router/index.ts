import { createRouter, createWebHashHistory } from "vue-router";

// 路由表（06 §2；页面实现随 M1-32 落地，当前为骨架占位页）
export const router = createRouter({
  history: createWebHashHistory(),
  routes: [
    { path: "/", component: () => import("../pages/Dashboard.vue") },
    { path: "/wizard", component: () => import("../pages/Wizard.vue") },
    { path: "/login", component: () => import("../pages/Login.vue") },
    { path: "/devices", component: () => import("../pages/Devices.vue") },
    { path: "/mappings", component: () => import("../pages/Mappings.vue") },
    { path: "/settings", component: () => import("../pages/Settings.vue") },
  ],
});

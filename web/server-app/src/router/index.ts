import { createRouter, createWebHashHistory } from "vue-router";

// 06 §3：服务端后台路由骨架（页面 M3 交付）
export const router = createRouter({
  history: createWebHashHistory(),
  routes: [
    { path: "/", redirect: "/dashboard" },
    { path: "/dashboard", component: () => import("../pages/Dashboard.vue") },
    { path: "/login", component: () => import("../pages/Login.vue") },
  ],
});

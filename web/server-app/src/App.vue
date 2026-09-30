<script setup lang="ts">
// M3-09 根组件（06 §3）：AdminLayout 八路由菜单（ui-shared 复用）；/login 免布局（独立登录页）。
// 登录态=Cookie 会话（服务端 8h）+ 刷新标记（AUTH_KEY）；挂载时标记存在则探测一次
// （401 由 api.ts 拦截器清标记跳登录——守卫在下次导航生效，拦截器即时生效）。
import { computed, onMounted, onUnmounted } from "vue";
import { useRoute, useRouter } from "vue-router";
import { AdminLayout } from "@p2p/ui-shared";
import { useAuthStore } from "./stores/auth";
import { useDashboardStore } from "./stores/dashboard";

const MENU = [
  { path: "/dashboard", label: "仪表盘" },
  { path: "/users", label: "用户" },
  { path: "/devices", label: "设备" },
  { path: "/groups", label: "分组" },
  { path: "/mappings", label: "映射" },
  { path: "/relay", label: "中继" },
  { path: "/system", label: "系统" },
];

const auth = useAuthStore();
const dashboard = useDashboardStore();
const route = useRoute();
const router = useRouter();

const bare = computed(() => route.path === "/login");

async function logout() {
  await auth.logout();
  void router.replace("/login");
}

onMounted(() => {
  if (auth.authed) {
    void dashboard.refresh(); // 刷新后探测：会话失效 → 401 拦截器跳登录
    dashboard.startPolling();
  }
});
onUnmounted(() => dashboard.stopPolling());
</script>

<template>
  <RouterView v-if="bare" />
  <AdminLayout
    v-else
    title="P2P 管理后台"
    :menu="MENU"
  >
    <template #header>
      <el-button
        size="small"
        data-testid="app-logout"
        @click="logout"
      >
        登出
      </el-button>
    </template>
    <RouterView />
  </AdminLayout>
</template>

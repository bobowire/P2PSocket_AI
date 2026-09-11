<script setup lang="ts">
// M1-32 根组件（06 §2/§4）：AdminLayout 布局 + 全局 WS 接线（唯一 /ws/status 连接）+
// 未注册守卫（phase=unregistered → /wizard；向导完成回 /）。
// useWs 分级：stats→store 直写；状态类→refetch（100ms debounce）；断连→顶栏"服务不可达"。
import { onMounted, onUnmounted, computed, watch } from "vue";
import { useRoute, useRouter } from "vue-router";
import { AdminLayout, useWs, ApiPaths } from "@p2p/ui-shared";
import { useSystemStore } from "./stores/system";
import { useAuthStore } from "./stores/auth";
import { useMappingStore } from "./stores/mappings";
import { useDeviceStore } from "./stores/devices";

const MENU = [
  { path: "/", label: "仪表盘" },
  { path: "/devices", label: "设备发现" },
  { path: "/groups", label: "分组" },
  { path: "/mappings", label: "端口映射" },
  { path: "/login", label: "账号" },
  { path: "/settings", label: "设置" },
];

const system = useSystemStore();
const auth = useAuthStore();
const mappings = useMappingStore();
const devices = useDeviceStore();
const route = useRoute();
const router = useRouter();

const wsUrl = `${location.protocol === "https:" ? "wss" : "ws"}://${location.host}${ApiPaths.WsStatus}`;

const { connected } = useWs({
  url: wsUrl,
  onStats: (ev) => mappings.applyStats(ev),
  refetch: {
    system: () => void system.refresh(),
    mappings: () => void mappings.refresh(),
    auth: () => void auth.refresh(),
    devices: () => void devices.refresh(),
  },
});

const alert = computed(() => (connected.value ? undefined : "本地服务不可达"));
const headerText = computed(() =>
  auth.me?.username
    ? `${auth.me.username}（${auth.me.mode === "normal" ? "已登录" : "哑节点"}）`
    : "未登录",
);

// 守卫（单向）：一旦检出未注册一律去向导；反向（注册完成回主界面）由向导页
// 自主处置——结果展示（远程码/虚拟 IP）须先呈现，用户点"进入控制台"再离开（06 §2）
watch(
  () => system.state?.phase,
  (phase) => {
    if (phase === "unregistered" && route.path !== "/wizard") void router.replace("/wizard");
  },
);

onMounted(() => {
  void system.refresh();
  void auth.refresh();
  void mappings.refresh();
  system.startPolling();
  devices.startPolling();
});

onUnmounted(() => {
  system.stopPolling();
  devices.stopPolling();
});
</script>

<template>
  <AdminLayout
    title="P2P 客户端"
    :menu="MENU"
    :alert="alert"
  >
    <template #header>
      <span class="auth-state">{{ headerText }}</span>
    </template>
    <RouterView />
  </AdminLayout>
</template>

<style scoped>
.auth-state {
  font-size: 13px;
  color: var(--el-text-color-secondary);
}
</style>

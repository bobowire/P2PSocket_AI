<script setup lang="ts">
// M1-32 根组件（06 §2/§4）：AdminLayout 布局 + 全局 WS 接线（唯一 /ws/status 连接）+
// 未注册守卫（phase=unregistered → /wizard；向导完成回 /）。
// useWs 分级：stats→store 直写；状态类→refetch（100ms debounce）；断连→顶栏"服务不可达"。
// M2-28：设备 10s 轮询移除（device_list 事件驱动 + onOpen/visibility resync 兜底，TD-16）；
// mapping_state invalid 旁路 → 全局失效提示（0x75 链，列表置灰由 refetch 完成）。
// M3-13：顶部网段冲突持续告警条（FR-C-204：subnet_conflict 事件 → system refetch 驱动显隐）。
// FR-C-810 双模式（2026-10-20）：默认入口 /simple（SimpleLayout 自带顶栏 tab，不经
// AdminLayout）；WS 接线/守卫/初始 refresh 留 App 级对两模式同权生效；断连告警经
// provide("simpleAlert") 传简易版顶栏。
import { onMounted, onUnmounted, computed, provide, watch } from "vue";
import { useRoute, useRouter } from "vue-router";
import { ElMessage } from "element-plus";
import { Odometer, Search, Share, Connection, Link, Document, User, Setting, Upload, InfoFilled } from "@element-plus/icons-vue";
import { AdminLayout, useWs, ApiPaths } from "@p2p/ui-shared";
import { buildWsUrl } from "./api";
import { useSystemStore } from "./stores/system";
import { useAuthStore } from "./stores/auth";
import { useMappingStore } from "./stores/mappings";
import { useDeviceStore } from "./stores/devices";
import SubnetConflictBanner from "./components/SubnetConflictBanner.vue";

// 2026-10-20 评估批次 A/C：登录入口"账号"→"账号登录"（U-6）；菜单配图标（V-4）；
// 仪表盘随双模式挪 /dashboard（原 / 由 /simple 接管默认入口）
// FR-C-812（2026-10-21）：末位增「关于」页（项目介绍/仓库/QQ 群+里程碑时间轴）
const MENU = [
  { path: "/dashboard", label: "仪表盘", icon: Odometer },
  { path: "/devices", label: "设备发现", icon: Search },
  { path: "/groups", label: "分组", icon: Share },
  { path: "/segments", label: "网段", icon: Connection },
  { path: "/mappings", label: "端口映射", icon: Link },
  { path: "/logs", label: "日志", icon: Document },
  { path: "/login", label: "账号登录", icon: User },
  { path: "/settings", label: "设置", icon: Setting },
  { path: "/upgrade", label: "升级", icon: Upload },
  { path: "/about", label: "关于", icon: InfoFilled },
];

const system = useSystemStore();
const auth = useAuthStore();
const mappings = useMappingStore();
const devices = useDeviceStore();
const route = useRoute();
const router = useRouter();

const wsUrl = buildWsUrl(ApiPaths.WsStatus); // M5-12：query token 注入（07 §14.2）

const { connected } = useWs({
  url: wsUrl,
  onStats: (ev) => mappings.applyStats(ev),
  // refetch 为函数形态（WsOptions 契约）：按事件资源名分派 store 刷新。M7-05 顺手修复存量
  // 形态错配——原对象形态使 flushRefetch 的 refetch?.(resource) 调用抛 TypeError（setTimeout
  // 回调内静默），WS 事件驱动/onOpen·visibility resync 的 refetch 实际全部失效
  refetch: (resource) => {
    const refetchers: Record<string, () => void> = {
      system: () => void system.refresh(),
      mappings: () => void mappings.refresh(),
      auth: () => void auth.refresh(),
      devices: () => void devices.refresh(),
    };
    refetchers[resource]?.();
  },
  onState: (ev) => {
    // 0x75 失效推送（网段移除/远程码重置/禁用等）→ 全局提示；原因见 05 §5
    if (ev.ev === "mapping_state" && ev.state === "invalid")
      ElMessage.warning(`映射已失效：${typeof ev.reason === "string" && ev.reason ? ev.reason : "授权变更"}`);
  },
});

const alert = computed(() => (connected.value ? undefined : "本地服务不可达"));
const headerText = computed(() =>
  auth.me?.username
    ? `${auth.me.username}（${auth.me.mode === "normal" ? "已登录" : "哑节点"}）`
    : "未登录",
);

// 未登录访问 /login → 免布局渲染（2026-10-20 评估 V-7：全屏居中登录页，沿 server-app bare 模式）；
// 已登录的账号管理（/login）仍嵌布局
const bare = computed(() => route.path === "/login" && !auth.me?.username);

// 简易版（FR-C-810）：/simple* 免 AdminLayout 渲染（SimpleLayout 内含子 RouterView）；
// 断连告警经 provide 传简易版顶栏（ref 直传保持响应式）
const simpleMode = computed(() => route.path.startsWith("/simple"));
provide("simpleAlert", alert);

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
  void devices.refresh();
  system.startPolling();
});

onUnmounted(() => {
  system.stopPolling();
});
</script>

<template>
  <RouterView v-if="bare || simpleMode" />
  <AdminLayout
    v-else
    title="P2PSocket 客户端"
    :menu="MENU"
    :alert="alert"
  >
    <template #header>
      <span class="auth-state">{{ headerText }}</span>
      <RouterLink
        to="/simple"
        class="simple-link"
      >
        简易版
      </RouterLink>
    </template>
    <SubnetConflictBanner />
    <RouterView />
  </AdminLayout>
</template>

<style scoped>
.auth-state {
  font-size: 13px;
  color: var(--el-text-color-secondary);
  margin-right: 12px;
}
.simple-link {
  font-size: 13px;
  color: var(--el-color-primary);
  text-decoration: none;
  border: 1px solid var(--el-color-primary-light-5);
  border-radius: var(--el-border-radius-base);
  padding: 2px 10px;
}
.simple-link:hover { background: var(--el-color-primary-light-9); }
</style>

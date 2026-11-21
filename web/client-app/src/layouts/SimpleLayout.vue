<script setup lang="ts">
// 简易版布局（FR-C-810，2026-10-20 双模式批复）：默认入口 /simple——品牌位 +
// 「概览|端口映射|分组」顶部 tab + 右侧在线态/登录态/「高级版」切换；
// 内容区 960px 水平居中（用户指令"页面水平居中"）。
// App 级 useWs 断连告警经 provide("simpleAlert") 注入（简易版不经 AdminLayout 的 alert 条）；
// 网段冲突横幅与高级版同源（system store 驱动，FR-C-204）。
// 科技感主题（用户指令 2026-10-20）：挂载期给 <html> 加 dark（EP 官方暗色变量，全局
// 生效含 teleport 弹层）+ simple-tech（simple-tech.css 皮肤），卸载期摘除——高级版/
// 登录页/向导页保持浅色出厂态，切换即还原。
import { computed, inject, type Ref } from "vue";
import { useSystemStore } from "../stores/system";
import { useAuthStore } from "../stores/auth";
import { useBreakpoint } from "@p2p/ui-shared";
import SubnetConflictBanner from "../components/SubnetConflictBanner.vue";
import "./simple-tech.css";

const TABS = [
  { path: "/simple", label: "概览" },
  { path: "/simple/mappings", label: "端口映射" },
  { path: "/simple/groups", label: "分组" },
];

const system = useSystemStore();
const auth = useAuthStore();
const wsAlert = inject<Ref<string | undefined> | undefined>("simpleAlert", undefined);
const { isMobile } = useBreakpoint(); // M8-04：小屏 tab 溢出滚动 + 身份文本让位（设计 13 §7）

const online = computed(() => system.state?.serverReachable ?? false);
const headerText = computed(() =>
  auth.me?.username
    ? `${auth.me.username}（${auth.me.mode === "normal" ? "已登录" : "哑节点"}）`
    : "未登录",
);

// 主题类（html.dark+simple-tech）挂/摘已上收 App.vue 路由级集中管理（2026-11-21 移动端
// 走查：卸载钩子在真机 WebView 存在清理不生效路径致"黑底白卡"，集中式幂等切换根治）——
// 此处不再操作 <html>，仅保留 simple-tech.css 样式引入。
</script>

<template>
  <div
    class="simple-layout"
    :class="{ mobile: isMobile }"
  >
    <header class="simple-header">
      <div class="header-inner">
        <RouterLink
          to="/simple"
          class="brand"
        >
          P2PSocket 客户端
        </RouterLink>
        <nav class="tabs">
          <RouterLink
            v-for="t in TABS"
            :key="t.path"
            :to="t.path"
            class="tab"
          >
            {{ t.label }}
          </RouterLink>
        </nav>
        <div class="header-right">
          <!-- 离线=异常语义红点（评估 V-3 离线色统一口径） -->
          <span
            :class="['dot', online ? 'on' : 'danger']"
            role="img"
            :aria-label="online ? '在线' : '离线'"
          />
          <span class="online-text">{{ online ? "在线" : "离线" }}</span>
          <span class="who">{{ headerText }}</span>
          <RouterLink
            to="/dashboard"
            class="advanced"
            data-testid="simple-advanced-link"
          >
            高级版
          </RouterLink>
        </div>
      </div>
    </header>
    <div
      v-if="wsAlert"
      class="ws-alert"
      role="alert"
      data-testid="simple-ws-alert"
    >
      {{ wsAlert }}
    </div>
    <main class="simple-main">
      <SubnetConflictBanner />
      <RouterView />
    </main>
  </div>
</template>

<style scoped>
/* 本布局只在简易版渲染——scoped 样式直接用深色字面值（科技感，simple-tech.css 负责全局层） */
.simple-layout { min-height: 100vh; display: flex; flex-direction: column; }
.simple-header {
  position: sticky;
  top: 0;
  z-index: 10;
  background: rgba(7, 11, 20, 0.78);
  backdrop-filter: blur(14px);
  -webkit-backdrop-filter: blur(14px);
}
/* 底部霓虹渐变分割线 */
.simple-header::after {
  content: "";
  position: absolute;
  left: 0;
  right: 0;
  bottom: -1px;
  height: 1px;
  background: linear-gradient(90deg, transparent 4%, rgba(34, 211, 238, 0.55) 50%, transparent 96%);
}
.header-inner {
  max-width: 960px; /* 内容水平居中（用户指令） */
  margin: 0 auto;
  padding: 0 16px;
  height: 56px;
  display: flex;
  align-items: center;
  gap: 24px;
}
/* 品牌渐变文字 + 光标方块 */
.brand {
  font-weight: 800;
  font-size: 15px;
  letter-spacing: 0.05em;
  text-decoration: none;
  background: linear-gradient(120deg, #7dd3fc, #22d3ee 45%, #3b82f6);
  -webkit-background-clip: text;
  background-clip: text;
  color: transparent;
  -webkit-text-fill-color: transparent;
}
.brand::before {
  content: "";
  display: inline-block;
  width: 10px;
  height: 10px;
  margin-right: 8px;
  border-radius: 3px;
  background: linear-gradient(135deg, #22d3ee, #3b82f6);
  box-shadow: 0 0 10px rgba(34, 211, 238, 0.8);
}
.tabs { display: flex; gap: 6px; flex: 1; }
.tab {
  padding: 6px 14px;
  border-radius: 8px;
  border: 1px solid transparent;
  color: #8aa0bf;
  text-decoration: none;
  font-size: 14px;
  transition: color 0.2s, box-shadow 0.2s, background-color 0.2s;
}
.tab:hover { color: #cfe3ff; }
/* 前缀匹配会把 /simple 也激活在子路由——精确激活才高亮（青色描边+内发光下划线） */
.tab.router-link-exact-active {
  color: #67e8f9;
  font-weight: 600;
  border-color: rgba(34, 211, 238, 0.35);
  background: rgba(34, 211, 238, 0.08);
  box-shadow: inset 0 -2px 0 #22d3ee, 0 6px 16px -10px rgba(34, 211, 238, 0.6);
}
.header-right { display: flex; align-items: center; gap: 10px; }
/* .dot/.dot.on/.dot.danger 由 shared.css 定义、simple-tech.css 加霓虹——此处不再重复 */
.online-text { font-size: 13px; color: #cbd5e1; }
.who { font-size: 13px; color: #8aa0bf; }
.advanced {
  font-size: 13px;
  color: #67e8f9;
  text-decoration: none;
  border: 1px solid rgba(34, 211, 238, 0.45);
  border-radius: 8px;
  padding: 3px 10px;
  background: rgba(34, 211, 238, 0.06);
  transition: background-color 0.2s, box-shadow 0.2s;
}
.advanced:hover {
  background: rgba(34, 211, 238, 0.15);
  box-shadow: 0 0 12px rgba(34, 211, 238, 0.25);
}
.ws-alert {
  padding: 6px 16px;
  font-size: 13px;
  color: #fca5a5;
  background: rgba(127, 29, 29, 0.4);
  border-bottom: 1px solid rgba(248, 113, 113, 0.35);
  text-align: center;
}
.simple-main {
  flex: 1;
  width: 100%;
  max-width: 960px;
  margin: 0 auto;
  padding: 20px 16px 40px;
  box-sizing: border-box;
}

/* ── M8-04 小屏（设计 13 §7）：tab 溢出横向滚动、身份文本让位、内边距收窄 ── */
.mobile .header-inner { gap: 10px; padding: 0 10px; }
.mobile .tabs { overflow-x: auto; scrollbar-width: none; }
.mobile .tabs::-webkit-scrollbar { display: none; }
.mobile .tab { padding: 6px 10px; white-space: nowrap; }
.mobile .who { display: none; }
.mobile .header-right { gap: 6px; }
.mobile .simple-main { padding: 14px 10px 32px; }
</style>

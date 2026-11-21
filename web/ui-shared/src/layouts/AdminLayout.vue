<script setup lang="ts">
// M1-31 AdminLayout（06 §1）：侧边栏菜单 + 顶栏 + 主区插槽。
// 菜单项走 RouterLink（宿主 app 注册路由与激活态）；顶栏右侧插槽放全局态（登录态/服务不可达条）。
// M8-04 移动适配（设计 13 §7）：小屏（<768px）侧边栏 → 汉堡按钮 + el-drawer 临时抽屉导航，
// 桌面路径 DOM 不变（v-if 分支）——PC 窄窗口同受益。
import { ref } from "vue";
import type { Component } from "vue";
import { useBreakpoint } from "../composables/useBreakpoint";

export interface LayoutMenuItem {
  path: string;
  label: string;
  /** 侧边栏图标（可选，@element-plus/icons-vue 组件由宿主 app 传入——2026-10-20 评估批次 C）。 */
  icon?: Component;
}

defineProps<{
  title: string;
  menu: LayoutMenuItem[];
  /** 顶栏警示文案（如 useWs.connected=false → "本地服务不可达"，06 §4）；空则不渲染。 */
  alert?: string;
}>();

const { isMobile } = useBreakpoint();
const drawerOpen = ref(false);
</script>

<template>
  <div class="admin-layout">
    <aside
      v-if="!isMobile"
      class="sidebar"
    >
      <div class="brand">
        {{ title }}
      </div>
      <nav class="menu">
        <RouterLink
          v-for="item in menu"
          :key="item.path"
          :to="item.path"
          class="menu-item"
        >
          <el-icon
            v-if="item.icon"
            class="menu-icon"
          >
            <component :is="item.icon" />
          </el-icon>
          <span>{{ item.label }}</span>
        </RouterLink>
      </nav>
    </aside>
    <!-- 小屏临时抽屉导航（路由不变；点选即收） -->
    <el-drawer
      v-else
      v-model="drawerOpen"
      direction="ltr"
      size="230px"
      :with-header="false"
      data-testid="mobile-nav-drawer"
    >
      <div class="drawer-body">
        <div class="brand">
          {{ title }}
        </div>
        <nav class="menu">
          <RouterLink
            v-for="item in menu"
            :key="item.path"
            :to="item.path"
            class="menu-item"
            @click="drawerOpen = false"
          >
            <el-icon
              v-if="item.icon"
              class="menu-icon"
            >
              <component :is="item.icon" />
            </el-icon>
            <span>{{ item.label }}</span>
          </RouterLink>
        </nav>
      </div>
    </el-drawer>
    <div class="main">
      <header class="topbar">
        <div class="topbar-left">
          <button
            v-if="isMobile"
            class="hamburger"
            type="button"
            aria-label="打开导航菜单"
            data-testid="mobile-menu-btn"
            @click="drawerOpen = true"
          >
            <svg
              viewBox="0 0 24 24"
              width="20"
              height="20"
              aria-hidden="true"
            >
              <path
                d="M3 6h18M3 12h18M3 18h18"
                stroke="currentColor"
                stroke-width="2"
                stroke-linecap="round"
              />
            </svg>
          </button>
          <span
            v-if="isMobile"
            class="brand-inline"
          >{{ title }}</span>
          <div
            class="alert"
            :class="{ visible: !!alert }"
            role="alert"
          >
            {{ alert ?? "" }}
          </div>
        </div>
        <div class="actions">
          <slot name="header" />
        </div>
      </header>
      <main class="content">
        <slot />
      </main>
    </div>
  </div>
</template>

<style scoped>
.admin-layout { display: flex; min-height: 100vh; }
.sidebar {
  width: 200px;
  flex-shrink: 0;
  border-right: 1px solid var(--el-border-color-light);
  background: var(--el-bg-color);
  display: flex;
  flex-direction: column;
}
.brand {
  padding: 16px;
  font-weight: 700;
  border-bottom: 1px solid var(--el-border-color-light);
}
.menu { display: flex; flex-direction: column; padding: 8px; gap: 2px; }
.menu-item {
  padding: 8px 12px;
  border-radius: var(--el-border-radius-base);
  color: var(--el-text-color-primary);
  text-decoration: none;
  font-size: 14px;
}
.menu-item { display: flex; align-items: center; }
.menu-icon { margin-right: 8px; font-size: 16px; }
.menu-item:hover { background: var(--el-fill-color-light); }
.menu-item.router-link-active { background: var(--el-color-primary-light-9); color: var(--el-color-primary); }
.main { flex: 1; display: flex; flex-direction: column; min-width: 0; }
.topbar {
  height: 48px;
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0 16px;
  border-bottom: 1px solid var(--el-border-color-light);
}
.topbar-left { display: flex; align-items: center; gap: 10px; min-width: 0; }
/* 小屏顶栏防溢出（2026-11-21 真机走查：登录态+右侧按钮两行溢出裁切）——
   右侧插槽区可收缩、内容不换行，配合宿主 auth-state 截断 ellipsis */
.actions { display: flex; align-items: center; gap: 8px; min-width: 0; max-width: 100%; }
/* 小屏汉堡（M8-04）：行内 SVG 零图标依赖 */
.hamburger {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 34px;
  height: 34px;
  padding: 0;
  border: 1px solid var(--el-border-color);
  border-radius: var(--el-border-radius-base);
  background: var(--el-bg-color);
  color: var(--el-text-color-primary);
  cursor: pointer;
}
.brand-inline { font-weight: 700; font-size: 15px; white-space: nowrap; }
.drawer-body { display: flex; flex-direction: column; height: 100%; }
.alert {
  font-size: 13px;
  color: var(--el-color-danger);
  visibility: hidden;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.alert.visible { visibility: visible; }
.content { flex: 1; padding: 16px; }
/* 小屏主区内边距收窄（设计 13 §7 表单/卡片化配套） */
@media (max-width: 767px) {
  .content { padding: 10px; }
}
</style>

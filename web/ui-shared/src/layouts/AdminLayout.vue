<script setup lang="ts">
// M1-31 AdminLayout（06 §1）：侧边栏菜单 + 顶栏 + 主区插槽。
// 菜单项走 RouterLink（宿主 app 注册路由与激活态）；顶栏右侧插槽放全局态（登录态/服务不可达条）。
export interface LayoutMenuItem {
  path: string;
  label: string;
}

defineProps<{
  title: string;
  menu: LayoutMenuItem[];
  /** 顶栏警示文案（如 useWs.connected=false → "本地服务不可达"，06 §4）；空则不渲染。 */
  alert?: string;
}>();
</script>

<template>
  <div class="admin-layout">
    <aside class="sidebar">
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
          {{ item.label }}
        </RouterLink>
      </nav>
    </aside>
    <div class="main">
      <header class="topbar">
        <div
          class="alert"
          :class="{ visible: !!alert }"
          role="alert"
        >
          {{ alert ?? "" }}
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
.alert {
  font-size: 13px;
  color: var(--el-color-danger);
  visibility: hidden;
}
.alert.visible { visibility: visible; }
.content { flex: 1; padding: 16px; }
</style>

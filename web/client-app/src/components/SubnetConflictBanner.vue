<script setup lang="ts">
// M3-13 虚拟网段冲突持续告警条（FR-C-204、05 §1.4）：App 顶部常驻（RouterView 之前），
// 数据源=system store 的 /api/system/state.conflict（WS subnet_conflict 提示 → refetch，
// TD-16 状态类不改 store）；消失条件=冲突解除（服务端调整 virtual_subnet 或本机网络变化
// 后现场重算返回 null）。纯展示组件，无请求。
import { computed } from "vue";
import { useSystemStore } from "../stores/system";

const system = useSystemStore();
const conflict = computed(() => system.state?.conflict ?? null);

const detail = computed(() =>
  conflict.value
    ? conflict.value.items.map((i) => `${i.kind === "address" ? "地址" : "路由"} ${i.value}（${i.interface}）`).join("；")
    : "");
</script>

<template>
  <el-alert
    v-if="conflict"
    type="error"
    show-icon
    :closable="false"
    data-testid="conflict-banner"
    title="虚拟网段冲突：本机网络与虚拟网段重叠，映射通信可能异常"
  >
    <div class="conflict-body">
      <p>虚拟网段 {{ conflict.subnet }} 与本机网络重叠：{{ detail }}</p>
      <p class="hint">
        建议管理员在服务端调整网段（virtual_subnet）后重新注册组网；冲突解除后本提示自动消失。
      </p>
    </div>
  </el-alert>
</template>

<style scoped>
.conflict-body p { margin: 4px 0; }
.hint { color: var(--el-text-color-secondary); font-size: 13px; }
</style>

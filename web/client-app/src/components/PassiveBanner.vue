<script setup lang="ts">
// M1-33 哑节点能力横幅（05 §8、FR-C-602、06 §2 注）：未登录（或被管理员禁用 0x75）时
// 能力降为 passive——仅维持心跳/被动响应，主动类操作（设备列表/分组/映射写操作）置灰。
// 挂在 /devices /groups /mappings 顶部；能力经 login_state 事件 → /api/device refetch
// 即时翻转（TD-16），本组件随 system store 响应式显隐。
import { computed } from "vue";
import { useSystemStore } from "../stores/system";

const system = useSystemStore();
const isPassive = computed(() => system.device?.capability === "passive");
</script>

<template>
  <el-alert
    v-if="isPassive"
    type="warning"
    :closable="false"
    show-icon
    data-testid="passive-banner"
    title="哑节点模式：主动操作不可用"
  >
    <div class="passive-detail">
      当前未登录或账号被禁用，仅维持心跳与被动响应；设备发现、分组、映射的写操作已停用。
      <RouterLink to="/login">
        去登录
      </RouterLink>恢复完整功能。
    </div>
  </el-alert>
</template>

<style scoped>
.passive-detail { font-size: 13px; }
.passive-detail a { margin-left: 4px; }
</style>

<script setup lang="ts">
// 映射状态标签（06 §1 组件清单；M1-31 完成完整实现）
defineProps<{ state: string }>();
</script>

<template>
  <span class="status-tag">{{ state }}</span>
</template>

<style scoped>
.status-tag { border: 1px solid var(--el-border-color); border-radius: 4px; padding: 0 8px; }
</style>

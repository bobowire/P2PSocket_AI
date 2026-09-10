<script setup lang="ts">
// M1-31 远程码等价文本（06 §1 CodeText：等宽展示 + 一键复制）。
// 复制用 navigator.clipboard（本地 Web 全程 127.0.0.1，安全上下文可用；失败退回选中文案提示）。
import { ref } from "vue";

const props = defineProps<{ value: string; label?: string }>();
const copied = ref(false);

async function copy() {
  try {
    await navigator.clipboard.writeText(props.value);
    copied.value = true;
    setTimeout(() => (copied.value = false), 1500);
  } catch {
    /* 无剪贴板权限（非安全上下文/无焦点）：保持原文展示，用户手动选择 */
  }
}
</script>

<template>
  <button
    type="button"
    class="code-text"
    :title="`复制 ${label ?? '值'}：${value}`"
    @click="copy"
  >
    <code>{{ value }}</code>
    <span class="hint">{{ copied ? "已复制" : "复制" }}</span>
  </button>
</template>

<style scoped>
.code-text {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  border: 1px solid var(--el-border-color);
  border-radius: var(--el-border-radius-base);
  background: var(--el-fill-color-light);
  padding: 2px 8px;
  cursor: pointer;
  font-size: 13px;
}
.code-text:hover { border-color: var(--el-color-primary-light-5); }
.code-text code {
  font-family: var(--el-font-family-mono, ui-monospace, monospace);
  letter-spacing: 1px;
}
.hint { color: var(--el-text-color-secondary); font-size: 12px; }
</style>

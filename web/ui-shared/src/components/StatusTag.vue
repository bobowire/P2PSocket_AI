<script setup lang="ts">
// M1-31 映射状态标签（06 §1 组件清单）：状态机 02 §4.5（disabled→punching→direct/failed；
// relay/invalid M2 预留）。样式走 Element Plus CSS 变量，无组件依赖（可独立测试）。
import { computed } from "vue";

const props = defineProps<{ state: string }>();

interface TagStyle { text: string; kind: "info" | "warning" | "success" | "primary" | "danger" }

const TAGS: Record<string, TagStyle> = {
  disabled: { text: "已禁用", kind: "info" },
  punching: { text: "打洞中", kind: "warning" },
  direct: { text: "直达", kind: "success" },
  relay: { text: "中继", kind: "primary" },
  failed: { text: "失败", kind: "danger" },
  invalid: { text: "失效", kind: "danger" },
};

const tag = computed<TagStyle>(() => TAGS[props.state] ?? { text: props.state, kind: "info" });
</script>

<template>
  <span
    class="status-tag"
    :data-kind="tag.kind"
    :data-state="state"
  >{{ tag.text }}</span>
</template>

<style scoped>
.status-tag {
  display: inline-block;
  border: 1px solid var(--el-border-color);
  border-radius: var(--el-border-radius-base);
  padding: 0 8px;
  font-size: 12px;
  line-height: 22px;
  white-space: nowrap;
}
.status-tag[data-kind="info"] { color: var(--el-text-color-secondary); }
.status-tag[data-kind="warning"] { color: var(--el-color-warning); border-color: var(--el-color-warning-light-5); }
.status-tag[data-kind="success"] { color: var(--el-color-success); border-color: var(--el-color-success-light-5); }
.status-tag[data-kind="primary"] { color: var(--el-color-primary); border-color: var(--el-color-primary-light-5); }
.status-tag[data-kind="danger"] { color: var(--el-color-danger); border-color: var(--el-color-danger-light-5); }
</style>

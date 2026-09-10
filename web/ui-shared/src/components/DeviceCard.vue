<script setup lang="ts">
// M1-31 设备卡片（06 §1）：设备名 + 在线点 + 远程码复制（CodeText）+ 操作插槽
// （/devices 页"建映射"快捷按钮经插槽注入，带入远程码——FR-C-803）。
import CodeText from "./CodeText.vue";

export interface DeviceCardModel {
  deviceId: string;
  deviceName: string;
  remoteCode: string;
  online: boolean;
  groupName?: string | null;
}

defineProps<{ device: DeviceCardModel }>();
</script>

<template>
  <div
    class="device-card"
    :data-online="device.online"
  >
    <div class="row head">
      <span
        class="dot"
        :data-online="device.online"
      />
      <span class="name">{{ device.deviceName }}</span>
      <span
        v-if="device.groupName"
        class="group"
      >{{ device.groupName }}</span>
    </div>
    <div class="row meta">
      <CodeText
        :value="device.remoteCode"
        label="远程码"
      />
      <span class="online-text">{{ device.online ? "在线" : "离线" }}</span>
    </div>
    <div
      v-if="$slots.actions"
      class="row actions"
    >
      <slot
        name="actions"
        :device="device"
      />
    </div>
  </div>
</template>

<style scoped>
.device-card {
  border: 1px solid var(--el-border-color-light);
  border-radius: var(--el-border-radius-base);
  padding: 12px 16px;
  display: flex;
  flex-direction: column;
  gap: 8px;
  background: var(--el-bg-color);
}
.row { display: flex; align-items: center; gap: 8px; }
.row.head .name { font-weight: 600; }
.row.head .group { color: var(--el-text-color-secondary); font-size: 12px; }
.row.meta { justify-content: space-between; }
.online-text { font-size: 12px; color: var(--el-text-color-secondary); }
.dot {
  width: 8px; height: 8px; border-radius: 50%;
  background: var(--el-color-danger-light-3);
}
.dot[data-online="true"] { background: var(--el-color-success); }
</style>

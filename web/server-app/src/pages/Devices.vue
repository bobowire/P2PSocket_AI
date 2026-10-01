<script setup lang="ts">
// M3-10 设备管理页（06 §3、FR-S-821）：全量列表（在线 tag=连接级真相源/分组 tags/远程码复制）+
// 禁用/解绑/重置码 confirm。解绑=清理全集+同 MAC 重注册全新身份（FR-S-103）——警示文案必须明确。
import { onMounted } from "vue";
import { ElMessage, ElMessageBox } from "element-plus";
import type { DeviceView } from "@p2p/ui-shared";
import { useDevicesStore } from "../stores/devices";

const devices = useDevicesStore();

function fmtTime(iso: string): string {
  return iso.replace("T", " ").replace(/(\.\d+|Z).*$/, "");
}

async function copyCode(code: string) {
  try {
    await navigator.clipboard.writeText(code);
    ElMessage.success(`已复制远程码 ${code}`);
  } catch {
    ElMessage.warning(`远程码：${code}`);
  }
}

async function toggleDisabled(row: DeviceView) {
  if (row.disabled) {
    await devices.enable(row.deviceId);
    ElMessage.success(`已启用 ${row.deviceName}`);
    return;
  }
  await ElMessageBox.confirm(
    `确定禁用设备"${row.deviceName}"？在线连接将被断开，引用其映射立即失效。`,
    "禁用确认",
    { type: "warning" },
  );
  await devices.disable(row.deviceId);
  ElMessage.success(`已禁用 ${row.deviceName}`);
}

async function unbind(row: DeviceView) {
  await ElMessageBox.confirm(
    `确定解绑设备"${row.deviceName}"？其全部映射、分组关联与统计数据将被清除，` +
      "同 MAC 重新注册将获得全新身份（新设备 ID/远程码），不可恢复。",
    "解绑确认",
    { type: "warning", confirmButtonText: "解绑" },
  );
  await devices.unbind(row.deviceId);
  ElMessage.success(`已解绑 ${row.deviceName}`);
}

async function resetCode(row: DeviceView) {
  await ElMessageBox.confirm(
    `确定重置设备"${row.deviceName}"的远程码？旧码立即失效，引用旧码的映射将置为失效。`,
    "重置远程码",
    { type: "warning" },
  );
  const code = await devices.resetRemoteCode(row.deviceId);
  await ElMessageBox.alert(code, `${row.deviceName} 的新远程码（仅显示一次）`, {
    confirmButtonText: "我已保存",
  });
}

onMounted(() => void devices.refresh());
</script>

<template>
  <main class="page">
    <h2>设备</h2>
    <el-table
      :data="devices.list?.items ?? []"
      data-testid="devices-table"
    >
      <el-table-column
        prop="deviceName"
        label="设备名"
        min-width="130"
      />
      <el-table-column
        label="在线"
        width="80"
      >
        <template #default="{ row }">
          <el-tag
            :type="row.online ? 'success' : 'info'"
            data-testid="device-online"
          >
            {{ row.online ? "在线" : "离线" }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column
        label="远程码"
        width="150"
      >
        <template #default="{ row }">
          <el-link
            type="primary"
            :underline="false"
            data-testid="device-copy-code"
            @click="copyCode(row.remoteCode)"
          >
            {{ row.remoteCode }}
          </el-link>
        </template>
      </el-table-column>
      <el-table-column
        prop="virtualIp"
        label="虚拟 IP"
        width="120"
      >
        <template #default="{ row }">
          {{ row.virtualIp ?? "—" }}
        </template>
      </el-table-column>
      <el-table-column
        prop="os"
        label="系统"
        width="90"
      />
      <el-table-column
        label="归属用户"
        width="110"
      >
        <template #default="{ row }">
          {{ row.ownerUsername ?? "—" }}
        </template>
      </el-table-column>
      <el-table-column
        label="分组"
        min-width="140"
      >
        <template #default="{ row }">
          <el-tag
            v-for="g in row.groups"
            :key="g"
            size="small"
            class="group-tag"
          >
            {{ g }}
          </el-tag>
          <span v-if="row.groups.length === 0">—</span>
        </template>
      </el-table-column>
      <el-table-column
        label="状态"
        width="90"
      >
        <template #default="{ row }">
          <el-tag :type="row.disabled ? 'danger' : 'info'">
            {{ row.disabled ? "已禁用" : "正常" }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column
        label="创建时间"
        min-width="150"
      >
        <template #default="{ row }">
          {{ fmtTime(row.createdAt) }}
        </template>
      </el-table-column>
      <el-table-column
        label="操作"
        width="230"
        fixed="right"
      >
        <template #default="{ row }">
          <el-button
            size="small"
            :type="row.disabled ? 'success' : 'danger'"
            plain
            :data-testid="`device-toggle-${row.deviceName}`"
            @click="toggleDisabled(row)"
          >
            {{ row.disabled ? "启用" : "禁用" }}
          </el-button>
          <el-button
            size="small"
            type="warning"
            plain
            :data-testid="`device-reset-${row.deviceName}`"
            @click="resetCode(row)"
          >
            重置码
          </el-button>
          <el-button
            size="small"
            type="danger"
            plain
            :data-testid="`device-unbind-${row.deviceName}`"
            @click="unbind(row)"
          >
            解绑
          </el-button>
        </template>
      </el-table-column>
    </el-table>
  </main>
</template>

<style scoped>
.page {
  padding: 24px;
}
.group-tag {
  margin-right: 4px;
}
</style>

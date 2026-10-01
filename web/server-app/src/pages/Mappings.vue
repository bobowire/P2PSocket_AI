<script setup lang="ts">
// M3-10 映射数据页（06 §3、FR-S-823）：全量只读（无写操作）——TD-22 最后已知状态投影 tag +
// mapping_stats 三向流量列 + 按归属设备/状态过滤联动 + 状态统计。设备筛选选项复用设备列表。
import { onMounted } from "vue";
import { ElMessage } from "element-plus";
import { useDevicesStore } from "../stores/devices";
import { MAPPING_STATUS_OPTIONS, useMappingsStore } from "../stores/mappings";
import { humanBytes } from "./punchChart";

const mappings = useMappingsStore();
const devices = useDevicesStore();

const STATUS_LABELS: Record<string, string> = {
  direct: "直达",
  relay: "中继",
  failed: "失败",
  invalid: "失效",
  unknown: "未知",
};
const STATUS_TAG: Record<string, "success" | "warning" | "danger" | "info"> = {
  direct: "success",
  relay: "warning",
  failed: "danger",
  invalid: "info",
  unknown: "info",
};

function targetText(m: { targetAddr: string; targetPort: number; targetDeviceName: string }): string {
  const addr = m.targetAddr === "self" ? "self" : `${m.targetAddr}:${m.targetPort}`;
  return `${m.targetDeviceName} · ${addr}`;
}

async function onDeviceChange(id: string) {
  try {
    await mappings.setDeviceFilter(id);
  } catch {
    ElMessage.error("筛选失败");
  }
}

async function onStatusChange(status: string) {
  try {
    await mappings.setStatusFilter(status);
  } catch {
    ElMessage.error("筛选失败");
  }
}

onMounted(() => {
  void devices.refresh(); // 筛选选项（独立于映射数据加载）
  void mappings.refresh();
});
</script>

<template>
  <main class="page">
    <h2>映射（只读）</h2>

    <div
      class="filters"
      data-testid="mappings-filters"
    >
      <el-select
        :model-value="mappings.deviceFilter"
        clearable
        placeholder="按归属设备筛选"
        style="width: 200px"
        data-testid="mappings-device-filter"
        @change="(v: string) => void onDeviceChange(v ?? '')"
      >
        <el-option
          v-for="d in devices.list?.items ?? []"
          :key="d.deviceId"
          :label="d.deviceName"
          :value="d.deviceId"
        />
      </el-select>
      <el-select
        :model-value="mappings.statusFilter"
        clearable
        placeholder="按状态筛选"
        style="width: 150px"
        data-testid="mappings-status-filter"
        @change="(v: string) => void onStatusChange(v ?? '')"
      >
        <el-option
          v-for="s in MAPPING_STATUS_OPTIONS"
          :key="s"
          :label="STATUS_LABELS[s]"
          :value="s"
        />
      </el-select>
      <div
        class="stats"
        data-testid="mappings-status-stats"
      >
        <el-tag
          v-for="(label, s) in STATUS_LABELS"
          :key="s"
          :type="STATUS_TAG[s]"
          size="small"
          class="stat-tag"
        >
          {{ label }} {{ mappings.statusCounts[s] ?? 0 }}
        </el-tag>
      </div>
    </div>

    <el-table
      :data="mappings.list?.items ?? []"
      data-testid="mappings-table"
    >
      <el-table-column
        prop="name"
        label="名称"
        min-width="120"
      />
      <el-table-column
        label="本地端口"
        width="100"
      >
        <template #default="{ row }">
          {{ row.localPort }}/{{ row.proto }}
        </template>
      </el-table-column>
      <el-table-column
        label="目标"
        min-width="180"
      >
        <template #default="{ row }">
          {{ targetText(row) }}
        </template>
      </el-table-column>
      <el-table-column
        label="归属设备"
        min-width="150"
      >
        <template #default="{ row }">
          {{ row.ownerDeviceName }}（{{ row.ownerRemoteCode }}）
        </template>
      </el-table-column>
      <el-table-column
        label="流量 ↑/↓（中继）"
        min-width="190"
      >
        <template #default="{ row }">
          <span :data-testid="`mapping-bytes-${row.name}`">
            {{ humanBytes(row.bytes.up) }} / {{ humanBytes(row.bytes.down) }}（{{ humanBytes(row.bytes.relay) }}）
          </span>
        </template>
      </el-table-column>
      <el-table-column
        label="状态"
        width="90"
      >
        <template #default="{ row }">
          <el-tag
            :type="STATUS_TAG[row.status] ?? 'info'"
            size="small"
          >
            {{ STATUS_LABELS[row.status] ?? row.status }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column
        label="启用"
        width="70"
      >
        <template #default="{ row }">
          <el-tag
            :type="row.enabled ? 'success' : 'info'"
            size="small"
          >
            {{ row.enabled ? "是" : "否" }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column
        label="统计更新"
        min-width="150"
      >
        <template #default="{ row }">
          {{ row.statsUpdatedAt ? row.statsUpdatedAt.replace("T", " ").replace(/(\.\d+|Z).*$/, "") : "—" }}
        </template>
      </el-table-column>
    </el-table>
  </main>
</template>

<style scoped>
.page {
  padding: 24px;
}
.filters {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-bottom: 16px;
}
.stats {
  display: flex;
  gap: 6px;
}
.stat-tag {
  margin-right: 0;
}
</style>

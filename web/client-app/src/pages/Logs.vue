<script setup lang="ts">
// M2-29 日志页（06 §2、FR-C-807）：GET /api/logs?level=&page=（服务端 newest-first、
// page 1 起、pageSize 200 固定）；级别精确类别过滤（04 §2.6 token：debug/info/warning/
// error/fatal）；导出=同源新窗口直下（text/plain 附件，服务端已带 content-disposition）。
import { onMounted, ref, watch } from "vue";
import { ApiPaths } from "@p2p/ui-shared";
import type { LogPageView } from "@p2p/ui-shared";
import { api } from "../api";

const LEVELS = [
  { value: "", label: "全部级别" },
  { value: "debug", label: "调试" },
  { value: "info", label: "信息" },
  { value: "warning", label: "警告" },
  { value: "error", label: "错误" },
  { value: "fatal", label: "致命" },
];

const LEVEL_TAG: Record<string, "info" | "success" | "warning" | "danger"> = {
  DBG: "info",
  INF: "success",
  WRN: "warning",
  ERR: "danger",
  FTL: "danger",
};

const level = ref("");
const page = ref(1);
const data = ref<LogPageView | null>(null);
const busy = ref(false);

async function refresh() {
  busy.value = true;
  try {
    const q = new URLSearchParams({ page: String(page.value) });
    if (level.value) q.set("level", level.value);
    data.value = await api.get<LogPageView>(`${ApiPaths.Logs}?${q}`);
  } catch {
    data.value = null; // 拉取失败清空（全局条另有提示）
  } finally {
    busy.value = false;
  }
}

watch(level, () => {
  // 切级别回首页；已在首页时 watch(page) 不触发，须自刷
  if (page.value === 1) void refresh();
  else page.value = 1;
});
watch(page, () => void refresh());

function exportUrl(): string {
  return level.value ? `${ApiPaths.LogsExport}?level=${level.value}` : ApiPaths.LogsExport;
}

function exportLogs() {
  window.open(exportUrl(), "_blank");
}

onMounted(() => void refresh());
</script>

<template>
  <section class="logs">
    <div class="toolbar">
      <h2>日志</h2>
      <el-select
        v-model="level"
        class="level"
        data-testid="logs-level"
      >
        <el-option
          v-for="l in LEVELS"
          :key="l.value"
          :value="l.value"
          :label="l.label"
        />
      </el-select>
      <el-button
        data-testid="logs-refresh"
        :loading="busy"
        @click="refresh()"
      >
        刷新
      </el-button>
      <el-button
        type="primary"
        plain
        data-testid="logs-export"
        @click="exportLogs"
      >
        导出下载
      </el-button>
    </div>

    <el-table
      :data="data?.items ?? []"
      data-testid="logs-table"
      size="small"
      empty-text="暂无日志（或按级别过滤后为空）"
    >
      <el-table-column
        prop="ts"
        label="时间"
        width="230"
      />
      <el-table-column
        label="级别"
        width="80"
      >
        <template #default="{ row }">
          <el-tag
            :type="LEVEL_TAG[row.level] ?? 'info'"
            size="small"
          >
            {{ row.level }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column
        prop="message"
        label="消息"
        min-width="420"
      />
    </el-table>

    <el-pagination
      class="pager"
      data-testid="logs-pagination"
      :total="data?.total ?? 0"
      :page-size="200"
      :current-page="page"
      layout="total, prev, pager, next"
      @current-change="(p: number) => (page = p)"
    />
  </section>
</template>

<style scoped>
.toolbar { display: flex; align-items: center; gap: 12px; margin-bottom: 12px; }
.toolbar h2 { margin: 0; font-size: 16px; }
.level { width: 130px; }
.pager { margin-top: 12px; justify-content: flex-end; }
</style>

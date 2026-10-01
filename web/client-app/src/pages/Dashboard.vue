<script setup lang="ts">
// M1-32 仪表盘（06 §2、FR-C-801）：本机信息卡（虚拟 IP/远程码/在线/登录态）+
// 隧道概览（按状态计数）+ 总速率图。
// M2-28：重置远程码入口（0x14 确认弹窗 + 新码展示；本机管理类 passive 亦允许）。
// M3-12：流量汇总卡（FR-C-1002：按设备维+总计，本地引擎累计口径）+ CSV 导出入口
// （同源新窗口直下，/logs 导出同模式）。
import { computed, onMounted, ref } from "vue";
import { ElMessage, ElMessageBox } from "element-plus";
import { ApiError, ApiPaths, CodeText, StatusTag } from "@p2p/ui-shared";
import type { StatsSummaryView } from "@p2p/ui-shared";
import { useSystemStore } from "../stores/system";
import { useMappingStore } from "../stores/mappings";
import { api } from "../api";
import RateChart from "../components/RateChart.vue";

const system = useSystemStore();
const mappings = useMappingStore();

const device = computed(() => system.device);
const overview = computed(() => mappings.overview);
const resetBusy = ref(false);
const summary = ref<StatsSummaryView | null>(null);

/** 字节人性化（与 server-app punchChart 同口径：二进制分档）。 */
function humanBytes(n: number): string {
  if (n < 1024) return `${n} B`;
  const units = ["KiB", "MiB", "GiB", "TiB"];
  let v = n;
  let i = -1;
  do {
    v /= 1024;
    i += 1;
  } while (v >= 1024 && i < units.length - 1);
  return `${v >= 100 ? v.toFixed(0) : v.toFixed(1)} ${units[i]}`;
}

/** 导出流量统计 CSV（GET /api/stats/export?format=csv 附件直下）。 */
function exportStats() {
  window.open(`${ApiPaths.StatsExport}?format=csv`, "_blank");
}

/** 重置远程码（POST /api/device/reset-remote-code → 0x14）：旧码立即失效（4003）。 */
async function resetRemoteCode() {
  try {
    await ElMessageBox.confirm(
      "重置后旧远程码立即失效：引用旧码的映射与打洞请求将被拒绝（4003），对端会收到失效提示并需改用新码。确定重置？",
      "重置远程码",
      { type: "warning", confirmButtonText: "重置", cancelButtonText: "取消" },
    );
  } catch {
    return; // 用户取消
  }
  resetBusy.value = true;
  try {
    const r = await api.post<{ remoteCode: string }>(ApiPaths.DeviceResetRemoteCode);
    await system.refresh(); // 新码回写信息卡（本地 WS device_list 提示链同源）
    await ElMessageBox.alert(`新远程码：${r.remoteCode}`, "重置成功", {
      confirmButtonText: "我已记下",
      type: "success",
    });
  } catch (e) {
    ElMessage.error(e instanceof ApiError ? e.message : "请求失败");
  } finally {
    resetBusy.value = false;
  }
}

onMounted(() => {
  void system.refresh();
  void mappings.refresh();
  void api
    .get<StatsSummaryView>(ApiPaths.StatsSummary)
    .then((r) => (summary.value = r))
    .catch(() => {}); // 汇总卡缺省隐藏（本地服务不可达时其余卡仍可用）
});
</script>

<template>
  <section class="dashboard">
    <div class="cards">
      <!-- 本机信息卡 -->
      <el-card
        class="info"
        data-testid="dash-info"
      >
        <template #header>
          本机信息
        </template>
        <dl v-if="device?.deviceId">
          <div class="row">
            <dt>虚拟 IP</dt>
            <dd>{{ device.virtualIp ?? "—" }}</dd>
          </div>
          <div class="row">
            <dt>远程码</dt>
            <dd>
              <CodeText
                v-if="device.remoteCode"
                :value="device.remoteCode"
                label="远程码"
              />
              <span v-else>—</span>
            </dd>
          </div>
          <div class="row">
            <dt>控制通道</dt>
            <dd>
              <span :class="['dot', system.state?.serverReachable ? 'on' : 'off']" />
              {{ system.state?.serverReachable ? "在线" : "离线" }}
            </dd>
          </div>
          <div class="row">
            <dt>登录态</dt>
            <dd>{{ device.username ? `已登录：${device.username}` : "未登录（哑节点）" }}</dd>
          </div>
          <div class="row actions">
            <el-button
              size="small"
              :loading="resetBusy"
              data-testid="dash-reset-code"
              @click="resetRemoteCode"
            >
              重置远程码
            </el-button>
          </div>
        </dl>
        <el-skeleton
          v-else
          :rows="3"
          animated
        />
      </el-card>

      <!-- 隧道概览 -->
      <el-card
        class="overview"
        data-testid="dash-overview"
      >
        <template #header>
          隧道概览
        </template>
        <ul class="tunnel-stats">
          <li>
            <StatusTag state="direct" />
            <b>{{ overview.direct }}</b>
          </li>
          <li>
            <StatusTag state="punching" />
            <b>{{ overview.punching }}</b>
          </li>
          <li>
            <StatusTag state="failed" />
            <b>{{ overview.failed }}</b>
          </li>
          <li>
            <StatusTag state="disabled" />
            <b>{{ overview.disabled }}</b>
          </li>
        </ul>
        <p class="muted">
          共 {{ overview.total }} 条映射，切到"端口映射"页可管理
        </p>
      </el-card>
    </div>

    <!-- 流量汇总（FR-C-1002）：本地引擎累计口径（进程重启清零），按设备维聚合 -->
    <el-card
      v-if="summary"
      class="stats"
      data-testid="dash-stats"
    >
      <template #header>
        流量汇总（本次运行累计）
      </template>
      <div class="stats-flex">
        <ul class="totals">
          <li>
            <span>上行</span>
            <b data-testid="dash-stats-up">{{ humanBytes(summary.totalBytesUp) }}</b>
          </li>
          <li>
            <span>下行</span>
            <b>{{ humanBytes(summary.totalBytesDown) }}</b>
          </li>
          <li>
            <span class="muted">其中中继</span>
            <b class="muted">{{ humanBytes(summary.totalRelayBytes) }}</b>
          </li>
        </ul>
        <el-table
          :data="summary.byDevices"
          size="small"
          data-testid="dash-stats-devices"
        >
          <el-table-column
            prop="targetRemoteCode"
            label="目标远程码"
            min-width="110"
          />
          <el-table-column
            prop="mappings"
            label="映射数"
            width="80"
          />
          <el-table-column
            label="上行 / 下行 / 中继"
            min-width="200"
          >
            <template #default="{ row }">
              {{ humanBytes(row.bytesUp) }} / {{ humanBytes(row.bytesDown) }} / {{ humanBytes(row.relayBytes) }}
            </template>
          </el-table-column>
        </el-table>
        <el-button
          type="primary"
          plain
          data-testid="dash-stats-export"
          @click="exportStats"
        >
          导出 CSV
        </el-button>
      </div>
      <p class="muted">
        按映射明细见导出文件（CSV 两段：映射明细+设备汇总）；本机视角累计，进程重启后清零
      </p>
    </el-card>

    <el-card data-testid="dash-rate">
      <template #header>
        实时速率
      </template>
      <RateChart />
    </el-card>
  </section>
</template>

<style scoped>
.cards {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 16px;
  margin-bottom: 16px;
}
.info dl { display: grid; gap: 10px; margin: 0; }
.row { display: flex; gap: 12px; align-items: center; }
.row dt { width: 72px; color: var(--el-text-color-secondary); font-size: 13px; }
.actions { margin-top: 4px; }
.tunnel-stats {
  list-style: none;
  display: grid;
  gap: 8px;
  margin: 0;
  padding: 0;
}
.tunnel-stats li { display: flex; align-items: center; gap: 12px; }
.tunnel-stats b { margin-left: auto; }
.muted { color: var(--el-text-color-secondary); font-size: 13px; margin: 12px 0 0; }
.stats { margin-bottom: 16px; }
.stats-flex { display: flex; align-items: center; gap: 24px; flex-wrap: wrap; }
.stats-flex .el-table { flex: 1; min-width: 360px; }
.totals { list-style: none; display: grid; gap: 8px; margin: 0; padding: 0; }
.totals li { display: flex; gap: 12px; align-items: baseline; }
.totals li span { color: var(--el-text-color-secondary); font-size: 13px; width: 60px; }
.dot {
  display: inline-block;
  width: 8px;
  height: 8px;
  border-radius: 50%;
  margin-right: 6px;
}
.dot.on { background: var(--el-color-success); }
.dot.off { background: var(--el-color-danger); }
</style>

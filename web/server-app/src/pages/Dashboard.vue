<script setup lang="ts">
// M3-09 仪表盘（06 §3、FR-S-810）：六指标卡（在线设备/分组/映射与 TD-22 分布/中继/STUN/
// 打洞成功率）+ 近 24h 成功率折线（纯 SVG，数据变换 punchChart.ts）；10s 定时刷新。
import { computed, onMounted, onUnmounted } from "vue";
import { useDashboardStore } from "../stores/dashboard";
import { humanBytes } from "./punchChart";
import PunchChart from "../components/PunchChart.vue";

const dashboard = useDashboardStore();
const data = computed(() => dashboard.data);

const rateText = computed(() => {
  const r = data.value?.punch.successRate24h;
  return r === null || r === undefined ? "—" : `${(r * 100).toFixed(1)}%`;
});

function fmtUptime(sec: number): string {
  if (sec < 60) return `${Math.floor(sec)}s`;
  if (sec < 3600) return `${Math.floor(sec / 60)}m${Math.floor(sec % 60)}s`;
  return `${Math.floor(sec / 3600)}h${Math.floor((sec % 3600) / 60)}m`;
}

const STATUS_LABELS: Record<string, string> = {
  direct: "直达",
  relay: "中继",
  failed: "失败",
  invalid: "失效",
  unknown: "未知",
};

onMounted(() => {
  void dashboard.refresh();
  dashboard.startPolling();
});
onUnmounted(() => dashboard.stopPolling());
</script>

<template>
  <section
    v-if="data"
    class="dashboard"
  >
    <div class="cards">
      <el-card data-testid="dash-online">
        <template #header>
          在线设备
        </template>
        <p class="metric">
          {{ data.onlineDevices }}
        </p>
      </el-card>
      <el-card data-testid="dash-groups">
        <template #header>
          分组
        </template>
        <p class="metric">
          {{ data.groups }}
        </p>
      </el-card>
      <el-card data-testid="dash-mappings">
        <template #header>
          活跃映射
        </template>
        <p class="metric">
          {{ data.mappings.enabled }} / {{ data.mappings.total }}
        </p>
        <ul class="sub">
          <li
            v-for="(count, state) in data.mappings.byStatus"
            :key="state"
          >
            {{ STATUS_LABELS[state] ?? state }}：{{ count }}
          </li>
        </ul>
      </el-card>
      <el-card data-testid="dash-relay">
        <template #header>
          中继流量
        </template>
        <p class="metric">
          {{ humanBytes(data.relay.bytesForwarded) }}
        </p>
        <p class="sub-line">
          会话 {{ data.relay.sessions }} · 运行 {{ fmtUptime(data.relay.uptimeSec) }}
        </p>
      </el-card>
      <el-card data-testid="dash-stun">
        <template #header>
          STUN
        </template>
        <p class="metric">
          {{ data.stun.qps.toFixed(1) }} QPS
        </p>
        <p class="sub-line">
          丢弃：限速 {{ data.stun.dropped.rate }} / 认证 {{ data.stun.dropped.auth }} / 断路 {{ data.stun.dropped.circuit }}
        </p>
      </el-card>
      <el-card data-testid="dash-punch">
        <template #header>
          打洞成功率（近 24h）
        </template>
        <p class="metric">
          {{ rateText }}
        </p>
        <p class="sub-line">
          {{ data.punch.total24h }} 次：直达 {{ data.punch.direct24h }} · 中继 {{ data.punch.relay24h }} · 失败 {{ data.punch.failed24h }}
        </p>
      </el-card>
    </div>

    <el-card data-testid="dash-chart">
      <template #header>
        打洞成功率
      </template>
      <PunchChart :hourly="data.punch.hourly" />
    </el-card>
  </section>
  <el-skeleton
    v-else
    :rows="6"
    animated
  />
</template>

<style scoped>
.cards {
  display: grid;
  grid-template-columns: repeat(3, 1fr);
  gap: 16px;
  margin-bottom: 16px;
}
.metric {
  font-size: 26px;
  font-weight: 700;
  margin: 0;
}
.sub {
  list-style: none;
  display: flex;
  flex-wrap: wrap;
  gap: 8px 16px;
  margin: 8px 0 0;
  padding: 0;
  font-size: 13px;
  color: var(--el-text-color-secondary);
}
.sub-line {
  font-size: 13px;
  color: var(--el-text-color-secondary);
  margin: 6px 0 0;
}
</style>

<script setup lang="ts">
// M3-09 打洞成功率折线（06 §3 仪表盘，FR-S-810）：纯 SVG 同 client-app RateChart 模式
// （不引 echarts——M1 先例：图形简单且 happy-dom 可测）；数据变换在 punchChart.ts 纯函数。
import { computed } from "vue";
import type { HourlyBucketView } from "@p2p/ui-shared";
import { successChartGeom } from "../pages/punchChart";

const props = withDefaults(
  defineProps<{ hourly: HourlyBucketView[]; title?: string }>(),
  { title: "打洞成功率（近 24h，按小时）" },
);

const WIDTH = 560;
const HEIGHT = 120;

const geom = computed(() => successChartGeom(props.hourly, WIDTH, HEIGHT));
</script>

<template>
  <figure
    class="punch-chart"
    data-testid="punch-chart"
  >
    <figcaption>{{ title }}</figcaption>
    <svg
      :viewBox="`0 0 ${WIDTH} ${HEIGHT}`"
      role="img"
      :aria-label="title"
      preserveAspectRatio="none"
    >
      <!-- 基线（0%）与满线（100%）参考 -->
      <line
        class="guide"
        x1="4"
        :y1="HEIGHT - 4"
        :x2="WIDTH - 4"
        :y2="HEIGHT - 4"
      />
      <line
        class="guide"
        x1="4"
        y1="4"
        :x2="WIDTH - 4"
        y2="4"
      />
      <polyline
        v-for="(pts, i) in geom.polylines"
        :key="i"
        class="line"
        :points="pts"
      />
      <circle
        v-for="(d, i) in geom.dots"
        :key="`d${i}`"
        class="dot"
        :cx="d.cx"
        :cy="d.cy"
        r="2"
      />
    </svg>
  </figure>
</template>

<style scoped>
.punch-chart {
  margin: 0;
  border: 1px solid var(--el-border-color-light);
  border-radius: var(--el-border-radius-base);
  padding: 8px 12px;
}
figcaption {
  font-size: 13px;
  color: var(--el-text-color-regular);
  margin-bottom: 4px;
}
svg {
  display: block;
  width: 100%;
  height: 120px;
  background: var(--el-fill-color-lighter);
}
.guide { stroke: var(--el-border-color-lighter); stroke-width: 1; }
.line { fill: none; stroke: var(--el-color-success); stroke-width: 1.5; }
.dot { fill: var(--el-color-success); }
</style>

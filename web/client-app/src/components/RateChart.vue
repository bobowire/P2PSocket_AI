<script setup lang="ts">
// M1-32 总速率图（06 §2 仪表盘）：纯 SVG 双折线（上行/下行，60 点环形缓冲，1s 采样）。
// 采样源为 mappings store 的 WS 直写速率（TD-16 高频数值类：丢失/陈旧无害，缺项计 0）。
// 不引 echarts——M1 图形简单且 happy-dom 可测；复杂图表（历史曲线等）M3 统计任务再议。
import { computed, onMounted, onUnmounted, ref } from "vue";
import { useMappingStore } from "../stores/mappings";

const SAMPLES = 60;
const INTERVAL_MS = 1000;
const WIDTH = 560;
const HEIGHT = 120;
const PAD = 4;

withDefaults(defineProps<{ title?: string }>(), { title: "总速率（近 60s）" });

const mappings = useMappingStore();
const up = ref<number[]>([]);
const down = ref<number[]>([]);

let timer: ReturnType<typeof setInterval> | null = null;

function sample() {
  let u = 0;
  let d = 0;
  for (const r of mappings.rates.values()) {
    u += r.rateUp;
    d += r.rateDown;
  }
  up.value = [...up.value.slice(-(SAMPLES - 1)), u];
  down.value = [...down.value.slice(-(SAMPLES - 1)), d];
}

onMounted(() => {
  sample();
  timer = setInterval(sample, INTERVAL_MS);
});
onUnmounted(() => {
  if (timer !== null) clearInterval(timer);
});

const peak = computed(() => Math.max(1, ...up.value, ...down.value));

function points(series: number[]): string {
  if (series.length === 0) return "";
  const step = (WIDTH - 2 * PAD) / (SAMPLES - 1);
  const offset = SAMPLES - series.length; // 左侧留白：数据不足 60 点时贴右对齐
  return series
    .map((v, i) => {
      const x = PAD + (offset + i) * step;
      const y = HEIGHT - PAD - ((HEIGHT - 2 * PAD) * v) / peak.value;
      return `${x.toFixed(1)},${y.toFixed(1)}`;
    })
    .join(" ");
}

const upPoints = computed(() => points(up.value));
const downPoints = computed(() => points(down.value));

function human(n: number): string {
  if (n >= 1024 * 1024) return `${(n / 1024 / 1024).toFixed(1)} MiB/s`;
  if (n >= 1024) return `${(n / 1024).toFixed(1)} KiB/s`;
  return `${n} B/s`;
}

const lastUp = computed(() => human(up.value.at(-1) ?? 0));
const lastDown = computed(() => human(down.value.at(-1) ?? 0));
</script>

<template>
  <figure
    class="rate-chart"
    data-testid="rate-chart"
  >
    <figcaption>
      {{ title }}
      <span class="peak">峰值 {{ human(peak) }}</span>
    </figcaption>
    <svg
      :viewBox="`0 0 ${WIDTH} ${HEIGHT}`"
      role="img"
      :aria-label="title"
      preserveAspectRatio="none"
    >
      <polyline
        class="line-up"
        :points="upPoints"
      />
      <polyline
        class="line-down"
        :points="downPoints"
      />
    </svg>
    <div class="legend">
      <span class="up">↑ 上行 {{ lastUp }}</span>
      <span class="down">↓ 下行 {{ lastDown }}</span>
    </div>
  </figure>
</template>

<style scoped>
.rate-chart {
  margin: 0;
  border: 1px solid var(--el-border-color-light);
  border-radius: var(--el-border-radius-base);
  padding: 8px 12px;
}
figcaption {
  display: flex;
  justify-content: space-between;
  font-size: 13px;
  color: var(--el-text-color-regular);
  margin-bottom: 4px;
}
.peak { color: var(--el-text-color-secondary); }
svg {
  display: block;
  width: 100%;
  height: 120px;
  background: var(--el-fill-color-lighter);
}
.line-up { fill: none; stroke: var(--el-color-success); stroke-width: 1.5; }
.line-down { fill: none; stroke: var(--el-color-primary); stroke-width: 1.5; }
.legend {
  display: flex;
  gap: 16px;
  font-size: 12px;
  color: var(--el-text-color-secondary);
  margin-top: 4px;
}
.legend .up::before { content: "—"; color: var(--el-color-success); margin-right: 4px; }
.legend .down::before { content: "—"; color: var(--el-color-primary); margin-right: 4px; }
</style>

<script setup lang="ts">
// 简易版·概览（FR-C-810）：本机卡（在线/设备名/远程码可复制）+ 实时速率卡（WS 速率总和）
// + 映射健康计数卡（失败标红可点 → /simple/mappings 处理）+ 新建映射 CTA。
// 监控集=用户批复"推荐集"：在线状态/远程码/映射健康计数/实时总速率；虚拟 IP 不在此卡
// ——以「连接地址 虚拟IP:本地端口」随映射卡呈现（简易版映射页，FR-C-810 调研定稿）。
// FR-C-812（2026-10-21）：底部增「关于项目」卡——项目介绍/仓库地址/QQ 群（projectInfo.ts
// 与高级版关于页同源）。
import { computed, onMounted } from "vue";
import { useRouter } from "vue-router";
import { CodeText } from "@p2p/ui-shared";
import { useSystemStore } from "../../stores/system";
import { useMappingStore } from "../../stores/mappings";
import { humanRate } from "../mappingDetail";
import { PROJECT_DESC, PROJECT_REPO, PROJECT_ORIGIN_REPO, PROJECT_QQ_GROUP } from "../projectInfo";

const system = useSystemStore();
const mappings = useMappingStore();
const router = useRouter();

const device = computed(() => system.device);
const online = computed(() => system.state?.serverReachable ?? false);
const overview = computed(() => mappings.overview);

/** 实时总速率（WS mapping_stats 直写行内求和；无数据时卡片示空态文案）。 */
const totalRate = computed(() => {
  let up = 0;
  let down = 0;
  for (const r of mappings.rates.values()) {
    up += r.rateUp;
    down += r.rateDown;
  }
  return { up, down };
});
const hasRate = computed(() => mappings.rates.size > 0);

/** CTA：去映射页并直接开引导式向导（?create=1）。 */
function goCreate() {
  void router.push("/simple/mappings?create=1");
}

onMounted(() => {
  void system.refresh();
  void mappings.refresh();
});
</script>

<template>
  <section class="overview">
    <div class="cards">
      <el-card
        class="card"
        data-testid="simple-overview-device"
      >
        <template #header>
          本机
        </template>
        <div class="device-line">
          <span
            :class="['dot', online ? 'on' : 'danger']"
            role="img"
            :aria-label="online ? '在线' : '离线'"
          />
          <span class="device-name">{{ device?.deviceName ?? "本机" }}</span>
          <span class="muted">{{ online ? "在线" : "离线" }}</span>
        </div>
        <div
          v-if="device?.remoteCode"
          class="code-line"
        >
          <span class="muted">远程码</span>
          <CodeText
            :value="device.remoteCode"
            label="远程码"
            data-testid="simple-overview-code"
          />
        </div>
      </el-card>

      <el-card
        class="card"
        data-testid="simple-overview-rate"
      >
        <template #header>
          实时速率
        </template>
        <div
          v-if="hasRate"
          class="rate-big"
        >
          <div class="rate-item">
            <span class="arrow up">↑</span>{{ humanRate(totalRate.up) }}
          </div>
          <div class="rate-item">
            <span class="arrow down">↓</span>{{ humanRate(totalRate.down) }}
          </div>
        </div>
        <p
          v-else
          class="muted"
        >
          暂无数据（映射有流量后显示）
        </p>
      </el-card>

      <el-card
        class="card"
        data-testid="simple-overview-mappings"
      >
        <template #header>
          映射
        </template>
        <div class="map-total">
          共 {{ overview.total }} 条
        </div>
        <div class="map-breakdown">
          <span>直连 {{ overview.direct }}</span>
          <span>中继 {{ overview.relay }}</span>
          <span>打洞中 {{ overview.punching }}</span>
          <span>停用 {{ overview.disabled }}</span>
          <RouterLink
            v-if="overview.failed > 0"
            to="/simple/mappings"
            class="failed-link"
            data-testid="simple-overview-failed"
          >
            失败 {{ overview.failed }}，点此处理
          </RouterLink>
          <span
            v-else
            class="ok"
          >失败 0</span>
        </div>
      </el-card>
    </div>

    <div class="cta">
      <el-button
        type="primary"
        size="large"
        data-testid="simple-overview-create"
        @click="goCreate"
      >
        新建映射
      </el-button>
      <p class="muted">
        一步步引导完成；高级功能（日志/网段/设置等）请切右上角「高级版」
      </p>
    </div>

    <el-card
      class="about-card"
      data-testid="simple-overview-about"
    >
      <template #header>
        关于项目
      </template>
      <p class="about-desc">
        {{ PROJECT_DESC }}
      </p>
      <div class="about-lines">
        <span class="muted">仓库地址</span>
        <el-link
          type="primary"
          :href="PROJECT_REPO"
          target="_blank"
          data-testid="simple-overview-repo"
        >
          {{ PROJECT_REPO }}
        </el-link>
      </div>
      <div class="about-lines">
        <span class="muted">原项目</span>
        <el-link
          type="primary"
          :href="PROJECT_ORIGIN_REPO"
          target="_blank"
          data-testid="simple-overview-origin-repo"
        >
          {{ PROJECT_ORIGIN_REPO }}
        </el-link>
      </div>
      <div class="about-lines">
        <span class="muted">QQ 群</span>
        <CodeText
          :value="PROJECT_QQ_GROUP"
          label="QQ 群号"
          data-testid="simple-overview-qq"
        />
      </div>
      <p class="muted about-more">
        更多信息与项目里程碑时间轴见
        <RouterLink
          to="/about"
          class="about-link"
        >
          高级版 · 关于页
        </RouterLink>
      </p>
    </el-card>
  </section>
</template>

<style scoped>
/* 简易版科技感（2026-10-20）：本页只在简易版渲染，字面值直接用深色霓虹口径；
 * 卡片玻璃底/描边/头部 kicker 由 simple-tech.css 全局层负责。 */
.cards {
  display: grid;
  grid-template-columns: repeat(3, 1fr);
  gap: 16px;
}
.device-line { display: flex; align-items: center; gap: 8px; }
.device-name { font-weight: 600; color: #e2e8f0; }
/* .dot/.dot.on/.dot.danger 由 shared.css+simple-tech.css 提供（霓虹+呼吸脉冲） */
.code-line { margin-top: 12px; display: flex; align-items: center; gap: 8px; }
.rate-big { display: flex; flex-direction: column; gap: 6px; }
/* 数据大字：等宽+青色辉光（科技感核心元素） */
.rate-item {
  font-family: ui-monospace, SFMono-Regular, "Cascadia Mono", Consolas, monospace;
  font-size: 26px;
  font-weight: 700;
  font-variant-numeric: tabular-nums;
  color: #a5f3fc;
  text-shadow: 0 0 16px rgba(34, 211, 238, 0.45);
}
.arrow { margin-right: 8px; font-size: 16px; }
.arrow.up { color: #34d399; }
.arrow.down { color: #38bdf8; }
.map-total {
  font-family: ui-monospace, SFMono-Regular, "Cascadia Mono", Consolas, monospace;
  font-size: 24px;
  font-weight: 700;
  font-variant-numeric: tabular-nums;
  color: #e2e8f0;
  text-shadow: 0 0 14px rgba(125, 211, 252, 0.3);
}
.map-breakdown {
  margin-top: 10px;
  display: flex;
  flex-wrap: wrap;
  gap: 4px 12px;
  font-size: 13px;
  color: #8aa0bf;
}
.failed-link { color: #f87171; text-decoration: none; font-weight: 600; text-shadow: 0 0 12px rgba(248, 113, 113, 0.5); }
.ok { color: #34d399; }
.cta { margin-top: 28px; text-align: center; }
.cta .muted { margin-top: 8px; }
/* 关于项目卡（FR-C-812）：深色字面值（本页只在简易版渲染）；玻璃卡底/描边归 simple-tech.css */
.about-card { margin-top: 28px; }
.about-desc { margin: 0; line-height: 1.8; color: #cbd5e1; font-size: 13px; }
.about-lines { margin-top: 12px; display: flex; align-items: center; gap: 10px; min-width: 0; }
.about-lines .muted { font-size: 13px; color: #8aa0bf; flex-shrink: 0; }
/* M8-04 小屏：长仓库 URL 换行收纳（el-link 白名单不换行会顶破 375px 卡片） */
.about-lines :deep(.el-link) { white-space: normal; word-break: break-all; line-height: 1.5; }
.about-more { margin: 14px 0 0; font-size: 12px; color: #8aa0bf; }
.about-link { color: #67e8f9; text-decoration: none; }
.about-link:hover { text-shadow: 0 0 10px rgba(34, 211, 238, 0.5); }

/* ── 小屏（2026-11-21 真机走查修复）：三卡并排每卡仅 ~110px——「远程码」label 一字一行、
   设备名/「共 N 条」硬断行、速率大字溢出 → 单列堆叠 + 数字降档 + 值行防断 ── */
@media (max-width: 767px) {
  .cards { grid-template-columns: 1fr; gap: 12px; }
  .device-line { flex-wrap: wrap; }
  .device-name { word-break: break-all; }
  .code-line { justify-content: space-between; }
  .rate-item { font-size: 20px; }
  .map-total { font-size: 20px; white-space: nowrap; }
  .map-breakdown { gap: 4px 16px; }
  .cta { margin-top: 20px; }
  .about-card { margin-top: 20px; }
}
</style>

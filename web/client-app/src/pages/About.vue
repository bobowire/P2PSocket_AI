<script setup lang="ts">
// 关于页（FR-C-812，2026-10-21）：项目介绍/仓库地址/QQ 群 + 项目里程碑时间轴。
// 纯静态展示页（数据源 projectInfo.ts，与简易版概览关于卡同源）——无 API 调用、
// 哑节点无写操作（同 /logs /upgrade 只读页，passive 亦可达）。时间轴粒度=用户批复
// 口径：每里程碑一条+维护期汇总一条（M1~M5+维护期共六节点，M5 随立项 2026-10-21 补入；
// done=success 绿收官 / active=primary 蓝进行）。
import { CodeText } from "@p2p/ui-shared";
import {
  PROJECT_NAME,
  PROJECT_DESC,
  PROJECT_REPO,
  PROJECT_ORIGIN_REPO,
  PROJECT_QQ_GROUP,
  MILESTONES,
} from "./projectInfo";
</script>

<template>
  <section class="about">
    <el-card
      class="card"
      data-testid="about-intro"
    >
      <template #header>
        关于项目
      </template>
      <h2
        class="name"
        data-testid="about-name"
      >
        {{ PROJECT_NAME }}
      </h2>
      <p class="desc">
        {{ PROJECT_DESC }}
      </p>
      <el-descriptions
        :column="1"
        border
        class="meta"
      >
        <el-descriptions-item label="仓库地址">
          <el-link
            type="primary"
            :href="PROJECT_REPO"
            target="_blank"
            data-testid="about-repo"
          >
            {{ PROJECT_REPO }}
          </el-link>
        </el-descriptions-item>
        <el-descriptions-item label="原项目（AI 重构基础）">
          <el-link
            type="primary"
            :href="PROJECT_ORIGIN_REPO"
            target="_blank"
            data-testid="about-origin-repo"
          >
            {{ PROJECT_ORIGIN_REPO }}
          </el-link>
        </el-descriptions-item>
        <el-descriptions-item label="QQ 群">
          <CodeText
            :value="PROJECT_QQ_GROUP"
            label="QQ 群号"
            data-testid="about-qq"
          />
        </el-descriptions-item>
      </el-descriptions>
    </el-card>

    <el-card
      class="card"
      data-testid="about-timeline"
    >
      <template #header>
        项目里程碑
      </template>
      <el-timeline>
        <el-timeline-item
          v-for="m in MILESTONES"
          :key="m.title"
          :type="m.status === 'done' ? 'success' : 'primary'"
          :timestamp="m.date"
          data-testid="about-milestone"
        >
          <span class="ms-title">{{ m.title }}</span>
          <p class="ms-note">
            {{ m.note }}
          </p>
        </el-timeline-item>
      </el-timeline>
    </el-card>
  </section>
</template>

<style scoped>
/* 高级版浅色出厂态：色值一律引用 --el-* 变量（06 §1.1 视觉规范） */
.about { max-width: 720px; }
.card + .card { margin-top: 16px; }
.name { margin: 0 0 8px; font-size: 20px; }
.desc { margin: 0 0 16px; line-height: 1.8; color: var(--el-text-color-regular); }
.meta { margin-top: 4px; }
.ms-title { font-weight: 600; }
.ms-note { margin: 4px 0 0; font-size: 12px; color: var(--el-text-color-secondary); }

/* ── 小屏（2026-11-21 真机走查修复）：descriptions 标签格被压至一字一行竖排、
   长 GitHub 链接溢出裁切 → 标签禁换行保持横排、链接允许断行收纳 ── */
@media (max-width: 767px) {
  .meta :deep(.el-descriptions__label) { white-space: nowrap; }
  .meta :deep(.el-descriptions__body) { font-size: 13px; }
  .meta :deep(.el-link) { white-space: normal; word-break: break-all; line-height: 1.5; }
}
</style>

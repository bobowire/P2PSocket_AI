<script setup lang="ts">
// M2-29 升级提示页（06 §2、FR-C-904、OQ-5 手动升级不自动拉包）：协议版本不符被服务端
// 拒答时客户端自动打开（?reason=version，M2-26 拒答窗口→浏览器引导）；GET /api/upgrade/info
// 由本地代理现取或回落拒答缓存（UpgradeApi），数据到达即渲染 UpdateInfo 全量字段。
import { onMounted, ref } from "vue";
import { useRoute } from "vue-router";
import { ApiPaths } from "@p2p/ui-shared";
import type { UpgradeInfoView } from "@p2p/ui-shared";
import { api } from "../api";

const route = useRoute();
const info = ref<UpgradeInfoView | null>(null);
const failed = ref(false);
const versionRejected = route.query.reason === "version";

onMounted(async () => {
  try {
    info.value = await api.get<UpgradeInfoView>(ApiPaths.UpgradeInfo);
  } catch {
    failed.value = true; // 本地服务不可达/未建立且无缓存
  }
});
</script>

<template>
  <section class="upgrade">
    <el-alert
      v-if="versionRejected"
      type="warning"
      :closable="false"
      show-icon
      data-testid="upgrade-reason-alert"
      title="协议版本不兼容"
      description="服务端已拒绝当前协议版本的连接（版本不符拒答窗口）。请下载并安装新版本，重启客户端后自动恢复。"
    />

    <el-card
      v-if="info"
      class="card"
      data-testid="upgrade-card"
    >
      <template #header>
        升级信息
      </template>
      <el-descriptions
        :column="1"
        border
      >
        <el-descriptions-item label="最新版本">
          <b data-testid="upgrade-version">{{ info.latestVersion }}</b>
        </el-descriptions-item>
        <el-descriptions-item label="协议兼容范围">
          <span data-testid="upgrade-protocol">{{ info.minProtocol }} ~ {{ info.maxProtocol }}</span>
          <span class="muted">（不兼容须升级，拒答窗口即由此触发）</span>
        </el-descriptions-item>
        <el-descriptions-item label="下载地址">
          <el-link
            type="primary"
            :href="info.upgradeUrl"
            target="_blank"
            data-testid="upgrade-url"
          >
            {{ info.upgradeUrl }}
          </el-link>
        </el-descriptions-item>
        <el-descriptions-item label="更新说明">
          <span
            class="notes"
            data-testid="upgrade-notes"
          >{{ info.notes }}</span>
        </el-descriptions-item>
      </el-descriptions>
      <p class="muted tip">
        手动升级：下载安装包后退出客户端（服务停止）再运行安装器；本页仅展示服务端下发的升级指引。
      </p>
    </el-card>

    <el-alert
      v-else-if="failed"
      type="error"
      :closable="false"
      show-icon
      title="暂无法获取升级信息"
      description="本地服务不可达，或控制通道未建立且无缓存（重启客户端后重试）。"
    />
  </section>
</template>

<style scoped>
.upgrade { max-width: 640px; }
.card { margin-top: 12px; }
.muted { color: var(--el-text-color-secondary); font-size: 12px; }
.notes { white-space: pre-line; }
.tip { margin: 12px 0 0; }
</style>

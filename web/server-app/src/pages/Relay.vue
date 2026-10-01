<script setup lang="ts">
// M3-11 中继运维页（06 §3、FR-S-824、TD-23）：全局开关（el-switch，关闭=拒新 0x74 保护存量）+
// 限速表单（0=不限；TD-23 令牌桶，改速进程内即时生效）+当前会话表（两端地址/承载/字节/活跃时间，
// 空态 el-empty）。会话表 10s 轮询观察。开关与 Users 注册开关同教训：config 未定值不渲染 el-switch。
import { onMounted, onUnmounted, ref } from "vue";
import { ElMessage, ElMessageBox } from "element-plus";
import { RELAY_POLL_MS, useRelayStore } from "../stores/relay";
import { humanBytes } from "./punchChart";

const relay = useRelayStore();
const rateDraft = ref<number | null>(null); // null=未跟随（首次加载后同步）
let timer: ReturnType<typeof setInterval> | undefined;

function fmtTime(iso: string): string {
  return iso.replace("T", " ").replace(/(\.\d+|Z|\+.*$)/, "").slice(0, 19);
}

function endText(e: { controlIp: string; carrier: string }): string {
  return `${e.controlIp}（${e.carrier}）`;
}

async function onSwitchChange(open: boolean) {
  try {
    await ElMessageBox.confirm(
      open
        ? "开启中继服务？新 0x74 分配将按限速余量放行。"
        : "关闭中继服务？新的中继分配将被拒绝（5002），存量会话不受影响继续转发。",
      "中继开关",
      { type: "warning" },
    );
    await relay.saveConfig({ relayEnabled: open });
    ElMessage.success(open ? "中继已开启" : "中继已关闭（存量会话保持）");
  } catch {
    await relay.refreshConfig(); // 取消/失败回读现值（防 UI 与服务端漂移）
  }
}

async function saveRate() {
  if (rateDraft.value === null || rateDraft.value < 0) return;
  try {
    await relay.saveConfig({ rateLimitBytes: rateDraft.value });
    rateDraft.value = relay.config?.rateLimitBytes ?? rateDraft.value;
    ElMessage.success("限速已更新（即时生效）");
  } catch {
    ElMessage.error("保存失败（0~2147483647，0=不限）");
  }
}

onMounted(async () => {
  await relay.refreshConfig();
  rateDraft.value = relay.config?.rateLimitBytes ?? 0;
  await relay.refreshSessions();
  timer = setInterval(() => void relay.refreshSessions(), RELAY_POLL_MS); // 会话表观察
});
onUnmounted(() => clearInterval(timer));
</script>

<template>
  <main class="page">
    <h2>中继</h2>

    <el-card
      class="cfg-card"
      data-testid="relay-config"
    >
      <template #header>
        全局开关与限速（TD-23 令牌桶）
      </template>
      <div
        v-if="relay.config"
        class="cfg-row"
      >
        <span class="cfg-label">中继服务</span>
        <!-- config 未定值不渲染（ElSwitch 对 null 自纠偏 emit 误触，Users.vue 同教训） -->
        <el-switch
          :model-value="relay.config.relayEnabled"
          data-testid="relay-switch"
          @change="onSwitchChange"
        />
        <span class="hint">{{ relay.config.relayEnabled ? "新分配按余量放行" : "新分配拒绝（5002），存量不受杀" }}</span>
      </div>
      <div
        v-if="relay.config"
        class="cfg-row"
      >
        <span class="cfg-label">限速（字节/秒）</span>
        <el-input-number
          v-model="rateDraft"
          :min="0"
          :max="2147483647"
          :step="1024"
          data-testid="relay-rate"
        />
        <el-button
          type="primary"
          plain
          data-testid="relay-rate-save"
          :disabled="rateDraft === relay.config.rateLimitBytes"
          @click="saveRate"
        >
          保存
        </el-button>
        <span class="hint">0=不限速；改速即时生效（存量会话按新速率扣桶）</span>
      </div>
    </el-card>

    <el-card data-testid="relay-sessions">
      <template #header>
        当前会话（{{ relay.sessions?.sessions.length ?? 0 }}，10s 自动刷新）
      </template>
      <el-empty
        v-if="(relay.sessions?.sessions.length ?? 0) === 0"
        description="暂无活跃中继会话"
        data-testid="relay-empty"
      />
      <el-table
        v-else
        :data="relay.sessions?.sessions ?? []"
        data-testid="relay-table"
      >
        <el-table-column
          label="会话 ID"
          min-width="160"
        >
          <template #default="{ row }">
            <span :title="row.sid">{{ row.sid.slice(0, 12) }}…</span>
          </template>
        </el-table-column>
        <el-table-column
          label="A 端（控制 IP·承载）"
          min-width="170"
        >
          <template #default="{ row }">
            {{ endText(row.a) }}
          </template>
        </el-table-column>
        <el-table-column
          label="B 端（控制 IP·承载）"
          min-width="170"
        >
          <template #default="{ row }">
            {{ endText(row.b) }}
          </template>
        </el-table-column>
        <el-table-column
          label="转发字节"
          width="110"
        >
          <template #default="{ row }">
            {{ humanBytes(row.bytesForwarded) }}
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
          label="最近活跃"
          min-width="150"
        >
          <template #default="{ row }">
            {{ fmtTime(row.lastActivity) }}
          </template>
        </el-table-column>
      </el-table>
    </el-card>
  </main>
</template>

<style scoped>
.page {
  padding: 24px;
}
.cfg-card {
  margin-bottom: 16px;
}
.cfg-row {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-bottom: 12px;
}
.cfg-row:last-child {
  margin-bottom: 0;
}
.cfg-label {
  width: 130px;
}
.hint {
  font-size: 13px;
  color: var(--el-text-color-secondary);
}
</style>

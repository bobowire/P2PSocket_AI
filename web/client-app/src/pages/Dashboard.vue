<script setup lang="ts">
// M1-32 仪表盘（06 §2、FR-C-801）：本机信息卡（虚拟 IP/远程码/在线/登录态）+
// 隧道概览（按状态计数）+ 总速率图；流量汇总导出（FR-C-1002）→ M3。
// M2-28：重置远程码入口（0x14 确认弹窗 + 新码展示；本机管理类 passive 亦允许）。
import { computed, onMounted, ref } from "vue";
import { ElMessage, ElMessageBox } from "element-plus";
import { ApiError, ApiPaths, CodeText, StatusTag } from "@p2p/ui-shared";
import { useSystemStore } from "../stores/system";
import { useMappingStore } from "../stores/mappings";
import { api } from "../api";
import RateChart from "../components/RateChart.vue";

const system = useSystemStore();
const mappings = useMappingStore();

const device = computed(() => system.device);
const overview = computed(() => mappings.overview);
const resetBusy = ref(false);

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

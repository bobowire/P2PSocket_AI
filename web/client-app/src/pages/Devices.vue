<script setup lang="ts">
// M1-32 设备发现（06 §2、FR-C-803）：可见设备表（在线/分组/远程码复制/开放网段）+
// "建映射"快捷按钮带入远程码跳转；按分组筛选。
// M2-28：设备级中继回退开关（/api/peers，D3 v0.4——目标设备层级，不经控制协议）；
// 列表刷新由 WS device_list 事件驱动（10s 轮询已移除，TD-16）。
// M7-05（设计 12 §8.3/§7.3）：两新列——"优先打洞模式"（TCP/KCP inline-prompt 切换，默认 TCP；
// 设备级 punchMode，发送时点取现值对后续打洞即时生效）与"隧道类型"（四态 el-tag：中继/
// 直连·KCP/直连·UDP/直连·TCP，无隧道 —；数据复用 GET /api/diagnostics/tunnels）。
import { computed, onMounted, ref } from "vue";
import { useRouter } from "vue-router";
import { ElMessage } from "element-plus";
import { ApiError, CodeText, useBreakpoint } from "@p2p/ui-shared";
import PassiveBanner from "../components/PassiveBanner.vue";
import { useDeviceStore, type DeviceItem } from "../stores/devices";
import { useSystemStore } from "../stores/system";

const devices = useDeviceStore();
const system = useSystemStore();
const router = useRouter();
const { isMobile } = useBreakpoint(); // M8-04：小屏卡片列表（设计 13 §7，桌面 DOM 不变）

const groupFilter = ref<string>("");

const groupOptions = computed(() => [
  ...new Set(devices.items.flatMap((d) => d.groups)),
].sort());

const filtered = computed(() =>
  groupFilter.value
    ? devices.items.filter((d) => d.groups.includes(groupFilter.value))
    : devices.items,
);

const isPassive = computed(() => system.device?.capability === "passive");

function createMapping(remoteCode: string) {
  void router.push({ path: "/mappings", query: { remoteCode } });
}

/** 切换中继回退（PUT /api/peers；打洞失败时是否允许经服务端中继兜底）。 */
async function toggleRelay(row: DeviceItem, value: string | number | boolean) {
  const on = value === true;
  try {
    await devices.setPeer(row.deviceId, on);
    ElMessage.success(`已${on ? "开启" : "关闭"}「${row.deviceName}」中继回退（对端打洞失败时${on ? "经中继转发" : "不再兜底"}）`);
  } catch (e) {
    ElMessage.error(e instanceof ApiError ? e.message : "请求失败");
  }
}

/** 切换打洞优先模式（M7-05，设计 12 决策①：设备级 punchMode——对后续打洞会话即时生效；
 * 失败回退中继后重试自动 TCP/KCP 交替，决策②）。 */
async function togglePunchMode(row: DeviceItem, value: string | number | boolean) {
  const mode = value === true ? "kcp" : "tcp";
  try {
    await devices.setPunchMode(row.deviceId, mode);
    ElMessage.success(`「${row.deviceName}」优先打洞模式已切换为 ${mode === "kcp" ? "KCP（UDP 打洞+KCP 可靠承载）" : "TCP"}（对后续打洞即时生效）`);
  } catch (e) {
    ElMessage.error(e instanceof ApiError ? e.message : "请求失败");
  }
}

/** 隧道类型列呈现（M7-05，设计 12 §7.3 表）：四态文本；无隧道 undefined。 */
function tunnelType(bearer: string | undefined): string | undefined {
  switch (bearer) {
    case "relay": return "中继";
    case "kcp": return "直连·KCP";
    case "raw-udp": return "直连·UDP";
    case "tcp": return "直连·TCP";
    default: return undefined;
  }
}

/** raw-udp 第四态说明（设计 12 §7.3）：承载协商降级形态。 */
const RAW_UDP_TIP = "对端未启用 KCP，已降级裸 UDP";

onMounted(() => void devices.refresh());
</script>

<template>
  <section class="devices">
    <PassiveBanner />
    <div class="toolbar">
      <h2>设备发现</h2>
      <el-select
        v-model="groupFilter"
        clearable
        placeholder="按分组筛选"
        class="group-filter"
        data-testid="devices-group-filter"
      >
        <el-option
          v-for="g in groupOptions"
          :key="g"
          :label="g"
          :value="g"
        />
      </el-select>
      <el-button @click="devices.refresh()">
        刷新
      </el-button>
    </div>
    <el-table
      v-if="!isMobile"
      :data="filtered"
      data-testid="devices-table"
      empty-text="暂无可见设备（需同账号或同分组）"
    >
      <el-table-column
        label="设备"
        min-width="160"
      >
        <template #default="{ row }">
          <span
            class="dot"
            :class="{ on: row.online }"
            role="img"
            :aria-label="row.online ? '在线' : '离线'"
          />
          {{ row.deviceName }}
        </template>
      </el-table-column>
      <el-table-column
        prop="virtualIp"
        label="虚拟 IP"
        width="120"
      />
      <el-table-column
        label="远程码"
        width="150"
      >
        <template #default="{ row }">
          <CodeText
            :value="row.remoteCode"
            label="远程码"
          />
        </template>
      </el-table-column>
      <el-table-column
        label="分组"
        min-width="140"
      >
        <template #default="{ row }">
          <el-tag
            v-for="g in row.groups"
            :key="g"
            size="small"
            class="tag"
          >
            {{ g }}
          </el-tag>
          <span v-if="row.groups.length === 0">—</span>
        </template>
      </el-table-column>
      <el-table-column
        label="开放网段"
        min-width="140"
      >
        <template #default="{ row }">
          <span v-if="row.lanSegments.length === 0">—</span>
          <code
            v-for="s in row.lanSegments"
            :key="s"
            class="code-pill"
          >{{ s }}</code>
        </template>
      </el-table-column>
      <el-table-column width="100">
        <template #header>
          <el-tooltip
            content="打洞失败时是否允许经服务端中继转发（目标设备级配置，D3）"
            placement="top"
          >
            中继回退
          </el-tooltip>
        </template>
        <template #default="{ row }">
          <el-switch
            :model-value="devices.peers[row.deviceId]?.relayFallback ?? false"
            data-testid="devices-relay-fallback"
            @change="(v: string | number | boolean) => toggleRelay(row, v)"
          />
        </template>
      </el-table-column>
      <el-table-column width="110">
        <template #header>
          <el-tooltip
            content="打洞优先使用的承载（目标设备级配置，默认 TCP；KCP=UDP 打洞+KCP 可靠承载；失败回退中继后重试自动 TCP/KCP 交替）"
            placement="top"
          >
            优先打洞模式
          </el-tooltip>
        </template>
        <template #default="{ row }">
          <el-switch
            :model-value="(devices.peers[row.deviceId]?.punchMode ?? 'tcp') === 'kcp'"
            inline-prompt
            inactive-text="TCP"
            active-text="KCP"
            data-testid="devices-punch-mode"
            @change="(v: string | number | boolean) => togglePunchMode(row, v)"
          />
        </template>
      </el-table-column>
      <el-table-column width="110">
        <template #header>
          <el-tooltip
            content="该设备当前隧道承载形态（随刷新更新；直连·UDP=对端未启用 KCP 已降级裸 UDP）"
            placement="top"
          >
            隧道类型
          </el-tooltip>
        </template>
        <template #default="{ row }">
          <el-tag
            v-if="tunnelType(devices.tunnelBearers[row.deviceId])"
            :type="devices.tunnelBearers[row.deviceId] === 'relay' ? 'warning' : 'success'"
            size="small"
            :title="devices.tunnelBearers[row.deviceId] === 'raw-udp' ? RAW_UDP_TIP : undefined"
            data-testid="devices-tunnel-type"
          >
            {{ tunnelType(devices.tunnelBearers[row.deviceId]) }}
          </el-tag>
          <span v-else data-testid="devices-tunnel-type">—</span>
        </template>
      </el-table-column>
      <el-table-column
        label="操作"
        width="120"
        fixed="right"
      >
        <template #default="{ row }">
          <el-button
            size="small"
            type="primary"
            plain
            :disabled="isPassive"
            title="带入远程码新建映射"
            data-testid="devices-create-mapping"
            @click="createMapping(row.remoteCode)"
          >
            建映射
          </el-button>
        </template>
      </el-table-column>
    </el-table>

    <!-- M8-04 小屏卡片列表（设计 13 §7：行数据纵排+关键操作保留；桌面 DOM 不变） -->
    <div
      v-else
      class="card-list"
      data-testid="devices-cards"
    >
      <el-card
        v-for="row in filtered"
        :key="row.deviceId"
        class="row-card"
        shadow="never"
      >
        <div class="card-head">
          <span class="card-title">
            <span
              class="dot"
              :class="{ on: row.online }"
              role="img"
              :aria-label="row.online ? '在线' : '离线'"
            />
            {{ row.deviceName }}
          </span>
          <el-tag
            v-if="tunnelType(devices.tunnelBearers[row.deviceId])"
            :type="devices.tunnelBearers[row.deviceId] === 'relay' ? 'warning' : 'success'"
            size="small"
          >
            {{ tunnelType(devices.tunnelBearers[row.deviceId]) }}
          </el-tag>
        </div>
        <div class="card-line">
          <span class="k">远程码</span>
          <CodeText
            :value="row.remoteCode"
            label="远程码"
          />
        </div>
        <div class="card-line">
          <span class="k">分组</span>
          <span>
            <el-tag
              v-for="g in row.groups"
              :key="g"
              size="small"
              class="tag"
            >
              {{ g }}
            </el-tag>
            <span v-if="row.groups.length === 0">—</span>
          </span>
        </div>
        <div class="card-line">
          <span class="k">开放网段</span>
          <span v-if="row.lanSegments.length === 0">—</span>
          <span v-else>
            <code
              v-for="s in row.lanSegments"
              :key="s"
              class="code-pill"
            >{{ s }}</code>
          </span>
        </div>
        <div class="card-line">
          <span class="k">中继回退</span>
          <el-switch
            :model-value="devices.peers[row.deviceId]?.relayFallback ?? false"
            data-testid="devices-relay-fallback"
            @change="(v: string | number | boolean) => toggleRelay(row, v)"
          />
        </div>
        <div class="card-line">
          <span class="k">优先打洞模式</span>
          <el-switch
            :model-value="(devices.peers[row.deviceId]?.punchMode ?? 'tcp') === 'kcp'"
            inline-prompt
            inactive-text="TCP"
            active-text="KCP"
            data-testid="devices-punch-mode"
            @change="(v: string | number | boolean) => togglePunchMode(row, v)"
          />
        </div>
        <div class="card-actions">
          <el-button
            size="small"
            type="primary"
            plain
            :disabled="isPassive"
            title="带入远程码新建映射"
            data-testid="devices-create-mapping"
            @click="createMapping(row.remoteCode)"
          >
            建映射
          </el-button>
        </div>
      </el-card>
      <el-empty
        v-if="filtered.length === 0"
        description="暂无可见设备（需同账号或同分组）"
      />
    </div>
  </section>
</template>

<style scoped>
/* .toolbar / .dot / .muted / .code-pill（原 .seg）上收 ui-shared/styles/shared.css
   （2026-10-20 评估批次 B：离线色统一灰） */
.group-filter { width: 180px; }
.tag { margin-right: 4px; }

/* ── M8-04 小屏卡片列表（设计 13 §7）── */
.card-list { display: flex; flex-direction: column; gap: 12px; }
.row-card :deep(.el-card__body) { padding: 12px 14px; }
.card-head { display: flex; align-items: center; justify-content: space-between; margin-bottom: 8px; }
.card-title { font-weight: 600; font-size: 15px; display: inline-flex; align-items: center; gap: 6px; }
.card-line { display: flex; gap: 10px; align-items: center; font-size: 13px; line-height: 2; word-break: break-all; }
.card-line .k { flex-shrink: 0; color: var(--el-text-color-secondary); }
.card-actions { display: flex; flex-wrap: wrap; gap: 4px; margin-top: 10px; }
</style>

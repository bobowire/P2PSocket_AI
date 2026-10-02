<script setup lang="ts">
// M1-32 设置页（06 §2、FR-C-808）：serverAddrs（主备多候选、可增删排序）+
// 打洞并发路数 N（1~5 滑块 + 说明"目标端口 = STUN 端口 + (N−1)"）+ 本地 Web 端口。
// PUT 部分修改；localWebPort 变更 → restartRequired 提示重启生效（04 §2.1）。
// M3-15 网络诊断卡（05 §7.2）：POST /api/diagnostics/stun-test 触发 RFC5780 子集判型，
// 结果卡渲染两桶族标签 + 降级提示 + 注记（FR-C-808 诊断面）。
// M3-16 ping-device（04 §2.6）：远程码→活隧道 PTP PING×4 测 RTT（min/avg/max）；
// 无活隧道 1002"须先启用一条到该设备的映射"如实透出。
// M3-14 诊断工具区收口（FR-C-808 剩余）：服务端连通性（settings 全候选逐个 TCP 探测）+
// 打洞队列（M1-29 punchQueueDepth/currentPunchPeer 展示化）+ 活隧道列表与手动 REKEY 触发
//（05 §2.3 M2-21 预留收口：发起方 Ok/响应方 NotInitiator/未完 Busy/失败 Failed 三态反馈）。
import { computed, onMounted, ref } from "vue";
import { ElMessage } from "element-plus";
import { ApiPaths, ApiError } from "@p2p/ui-shared";
import type { StunTestView, PingDeviceView, ServerTestView, TunnelView, TunnelListView, RekeyResultView } from "@p2p/ui-shared";
import { api } from "../api";
import { useSettingsStore } from "../stores/settings";

const settings = useSettingsStore();

const addrs = ref<string[]>([]);
const punchConcurrency = ref(3);
const localWebPort = ref(7100);
const busy = ref(false);
/** 提交后置位：localWebPort 变更须重启本地服务才生效（监听端口绑定时机） */
const restartHint = ref(false);

const ADDR_PATTERN = /^[\w.-]+:\d{1,5}$/;
const addrInvalid = computed(() => addrs.value.filter((a) => !ADDR_PATTERN.test(a)));
const canSave = computed(
  () => addrs.value.length > 0 && addrInvalid.value.length === 0 &&
    Number.isInteger(localWebPort.value) && localWebPort.value >= 1 && localWebPort.value <= 65535,
);

function addAddr() {
  addrs.value.push("");
}

function removeAddr(i: number) {
  addrs.value.splice(i, 1);
}

function move(i: number, delta: -1 | 1) {
  const j = i + delta;
  if (j < 0 || j >= addrs.value.length) return;
  [addrs.value[i], addrs.value[j]] = [addrs.value[j], addrs.value[i]];
}

async function save() {
  if (!canSave.value) {
    ElMessage.warning("存在非法配置项，请修正后保存");
    return;
  }
  busy.value = true;
  try {
    const result = await settings.save({
      serverAddrs: addrs.value,
      punchConcurrency: punchConcurrency.value,
      localWebPort: localWebPort.value,
    });
    restartHint.value = result.restartRequired;
    if (result.restartRequired) ElMessage.warning("本地 Web 端口已变更：重启客户端后生效");
    else ElMessage.success("设置已保存（即时生效）");
  } catch (e) {
    ElMessage.error(e instanceof ApiError ? e.message : "保存失败");
  } finally {
    busy.value = false;
  }
}

onMounted(async () => {
  await settings.refresh();
  const s = settings.settings;
  if (s) {
    addrs.value = [...s.serverAddrs];
    punchConcurrency.value = s.punchConcurrency;
    localWebPort.value = s.localWebPort;
  }
  void refreshPunchQueue(); // 诊断工具区初值（M3-14；失败静默，点刷新重试）
  void refreshTunnels();
});

// ── 网络诊断（M3-15 stun-test，05 §7.2）────────────────────────────

const stunBusy = ref(false);
const stunResult = ref<StunTestView | null>(null);

/** 判型两桶族标签（05 §7.2：本项目不细分 ADM/APDM 与 ADF/APDF——端口预测/入站过滤难度同域）。 */
const MAPPING_TEXT: Record<string, string> = {
  eim: "锥形（EIM）",
  adm_or_apdm: "对称（ADM/APDM）",
};
const FILTERING_TEXT: Record<string, string> = {
  eif: "全通（EIF）",
  adf_or_apdf: "受限（ADF/APDF）",
};

const mappingText = computed(() =>
  stunResult.value?.udpMapping ? (MAPPING_TEXT[stunResult.value.udpMapping] ?? stunResult.value.udpMapping) : "不可判");
const filteringText = computed(() =>
  stunResult.value?.udpFiltering ? (FILTERING_TEXT[stunResult.value.udpFiltering] ?? stunResult.value.udpFiltering) : "不可判");

async function runStunTest() {
  stunBusy.value = true;
  try {
    // 探测含超时判据（filtering 超时即 ADF/APDF），预算放宽到 30s（默认 10s 会截断慢 NAT 判定）
    stunResult.value = await api.post<StunTestView>(ApiPaths.DiagnosticsStunTest, undefined, {
      timeout: 30_000,
    });
  } catch (e) {
    stunResult.value = null;
    ElMessage.error(e instanceof ApiError ? e.message : "检测失败");
  } finally {
    stunBusy.value = false;
  }
}

// ── 设备连通性（M3-16 ping-device，04 §2.6）────────────────────────

const pingCode = ref("");
const pingBusy = ref(false);
const pingResult = ref<PingDeviceView | null>(null);

async function runPingDevice() {
  pingBusy.value = true;
  try {
    // 4 次 PING 各 2s 预算 + 列表解析往返，10s 默认预算可覆盖；超时样本计入丢失不出错
    pingResult.value = await api.post<PingDeviceView>(ApiPaths.DiagnosticsPingDevice, {
      remoteCode: pingCode.value.trim(),
    }, { timeout: 30_000 });
  } catch (e) {
    pingResult.value = null;
    ElMessage.error(e instanceof ApiError ? e.message : "检测失败");
  } finally {
    pingBusy.value = false;
  }
}

// ── 诊断工具区（M3-14，FR-C-808 收口）────────────────────────────

// 服务端连通性：对 settings 全部候选逐个 TCP 探测（wizard 预检同源逻辑的运行态入口）
const svtestBusy = ref(false);
const svtestResult = ref<ServerTestView | null>(null);

async function runServerTest() {
  svtestBusy.value = true;
  try {
    // 候选逐个探测：不可达候选 3s 超时串联，预算放宽（真实多候选全挂可达 9s+）
    svtestResult.value = await api.post<ServerTestView>(ApiPaths.DiagnosticsServerTest, undefined, {
      timeout: 30_000,
    });
  } catch (e) {
    svtestResult.value = null;
    ElMessage.error(e instanceof ApiError ? e.message : "检测失败");
  } finally {
    svtestBusy.value = false;
  }
}

// 打洞队列深度（M1-29 /api/diagnostics 数据展示化）
const punchDepth = ref<number | null>(null);
const punchCurrent = ref<string | null>(null);

async function refreshPunchQueue() {
  try {
    const d = await api.get<{ punchQueueDepth: number; currentPunchPeer: string | null }>(
      ApiPaths.Diagnostics);
    punchDepth.value = d.punchQueueDepth;
    punchCurrent.value = d.currentPunchPeer;
  } catch { /* 参考性数据：失败静默不弹错（onMounted 初拉/手动刷新共用） */ }
}

// 活隧道列表 + 手动 REKEY 触发（05 §2.3 M2-21 预留收口）
const tunnels = ref<TunnelView[]>([]);
const rekeyBusy = ref<string | null>(null); // peerDeviceId：同按钮防重入
const REKEY_TEXT: Record<string, string> = {
  ok: "轮换成功",
  not_initiator: "本端为响应方：密钥由对端轮换",
  busy: "上一轮轮换尚未完成，请稍后再试",
  failed: "轮换失败",
};

async function refreshTunnels() {
  try {
    tunnels.value = (await api.get<TunnelListView>(ApiPaths.DiagnosticsTunnels)).items;
  } catch {
    tunnels.value = []; // 无隧道态（后端未装配等）：空表呈现
  }
}

async function triggerRekey(t: TunnelView) {
  rekeyBusy.value = t.peerDeviceId;
  try {
    const r = await api.post<RekeyResultView>(ApiPaths.DiagnosticsRekey, {
      peerDeviceId: t.peerDeviceId,
    }, { timeout: 30_000 });
    const text = REKEY_TEXT[r.outcome] ?? r.outcome;
    if (r.outcome === "ok") ElMessage.success(text);
    else if (r.outcome === "failed") ElMessage.error(r.detail ? `${text}：${r.detail}` : text);
    else ElMessage.info(text);
  } catch (e) {
    ElMessage.error(e instanceof ApiError ? e.message : "轮换失败");
  } finally {
    rekeyBusy.value = null;
  }
}
</script>

<template>
  <section class="settings">
    <h2>设置</h2>

    <el-form label-width="140px">
      <!-- 服务端地址：主备候选，序即优先级 -->
      <el-form-item label="服务端地址">
        <div
          class="addrs"
          data-testid="settings-addrs"
        >
          <div
            v-for="(a, i) in addrs"
            :key="i"
            class="addr-row"
          >
            <el-input
              v-model="addrs[i]"
              :placeholder="`host:port（候选 ${i + 1}）`"
              :class="{ invalid: a !== '' && !ADDR_PATTERN.test(a) }"
            />
            <el-button
              size="small"
              :disabled="i === 0"
              title="上移（提高优先级）"
              @click="move(i, -1)"
            >
              ↑
            </el-button>
            <el-button
              size="small"
              :disabled="i === addrs.length - 1"
              title="下移"
              @click="move(i, 1)"
            >
              ↓
            </el-button>
            <el-button
              size="small"
              type="danger"
              plain
              @click="removeAddr(i)"
            >
              删除
            </el-button>
          </div>
          <el-button
            size="small"
            data-testid="settings-add-addr"
            @click="addAddr"
          >
            添加候选
          </el-button>
          <div class="hint">
            按序尝试连接；修改保存后控制通道立即换址重连（serverAddrs 即时生效）
          </div>
        </div>
      </el-form-item>

      <!-- 打洞并发 N：1~5 滑块（PRD OQ-11 参数） -->
      <el-form-item label="打洞并发路数">
        <div class="slider-block">
          <el-slider
            v-model="punchConcurrency"
            :min="1"
            :max="5"
            :step="1"
            show-stops
            style="width: 260px"
            data-testid="settings-punch"
          />
          <div class="hint">
            同时打洞的 socket 路数 N（1~5）：目标端口 = STUN 端口 + (N−1)，路数多命中率高但更易触发对端 NAT 限速
          </div>
        </div>
      </el-form-item>

      <!-- 本地 Web 端口 -->
      <el-form-item label="本地 Web 端口">
        <el-input-number
          v-model="localWebPort"
          :min="1"
          :max="65535"
          data-testid="settings-webport"
        />
        <div class="hint">
          本页面服务的监听端口；变更后须重启客户端生效
        </div>
      </el-form-item>

      <el-form-item>
        <el-button
          type="primary"
          :loading="busy"
          :disabled="!canSave"
          data-testid="settings-save"
          @click="save"
        >
          保存
        </el-button>
      </el-form-item>
    </el-form>

    <el-alert
      v-if="restartHint"
      type="warning"
      :closable="false"
      title="本地 Web 端口已变更——重启客户端后新端口生效（当前仍由旧端口服务）"
      data-testid="settings-restart-hint"
    />

    <!-- 只读参考：keepalive/重连退避（后端固化，M1 不开放编辑） -->
    <el-descriptions
      v-if="settings.settings"
      title="高级参数（只读）"
      :column="2"
      border
      class="readonly"
    >
      <el-descriptions-item label="隧道保活（秒）">
        {{ settings.settings.keepaliveSec }}
      </el-descriptions-item>
      <el-descriptions-item label="重连退避（秒）">
        {{ settings.settings.reconnect.minSec }} ~ {{ settings.settings.reconnect.maxSec }}
      </el-descriptions-item>
    </el-descriptions>

    <!-- 网络诊断（M3-15，FR-C-808）：RFC5780 子集判型——UDP 映射/过滤两维 + TCP 分配规律 -->
    <div class="stun-section">
      <h3>网络诊断（NAT 判型）</h3>
      <div class="stun-toolbar">
        <el-button
          type="primary"
          plain
          :loading="stunBusy"
          data-testid="stun-run"
          @click="runStunTest"
        >
          {{ stunBusy ? "探测中…" : "开始检测" }}
        </el-button>
        <span class="hint">
          经 STUN 多次 Binding 探测判型（RFC5780 子集）；受限型 NAT 需等超时判据，全程可数十秒
        </span>
      </div>

      <template v-if="stunResult">
        <el-descriptions
          :column="2"
          border
          class="stun-result"
          data-testid="stun-result"
        >
          <el-descriptions-item label="公网映射端点">
            {{ stunResult.publicEndpoint }}
          </el-descriptions-item>
          <el-descriptions-item label="UDP 映射类型">
            {{ mappingText }}
          </el-descriptions-item>
          <el-descriptions-item label="UDP 入站过滤">
            {{ filteringText }}
          </el-descriptions-item>
          <el-descriptions-item label="TCP 端口分配">
            {{ stunResult.tcpSequential === null
              ? "未测" : stunResult.tcpSequential ? "顺序递增（端口预测适用）" : "随机分配" }}
          </el-descriptions-item>
          <el-descriptions-item label="TCP 端口依赖">
            {{ stunResult.tcpPortDependent === null
              ? "未测" : stunResult.tcpPortDependent ? "端口依赖" : "端口无关" }}
          </el-descriptions-item>
          <el-descriptions-item label="耗时">
            {{ stunResult.durationMs }} ms
          </el-descriptions-item>
        </el-descriptions>

        <el-alert
          v-if="stunResult.downgraded"
          type="warning"
          :closable="false"
          data-testid="stun-downgraded"
          title="服务端未配置第二地址（stun_alt_addr）：UDP 映射/过滤两维不可判（单公网 IP 降级）"
        />

        <ul
          v-if="stunResult.notes.length > 0"
          class="hint stun-notes"
          data-testid="stun-notes"
        >
          <li
            v-for="(n, i) in stunResult.notes"
            :key="i"
          >
            {{ n }}
          </li>
        </ul>
      </template>
    </div>

    <!-- 设备连通性（M3-16，FR-C-808）：远程码 → 活隧道 PING×4 测 RTT -->
    <div class="stun-section">
      <h3>设备连通性（隧道 PING）</h3>
      <div class="stun-toolbar">
        <el-input
          v-model="pingCode"
          placeholder="目标设备远程码（6 位）"
          class="ping-code"
          data-testid="ping-code"
          :maxlength="8"
        />
        <el-button
          type="primary"
          plain
          :loading="pingBusy"
          :disabled="pingCode.trim() === ''"
          data-testid="ping-run"
          @click="runPingDevice"
        >
          {{ pingBusy ? "测速中…" : "开始 PING" }}
        </el-button>
        <span class="hint">
          在到目标设备的活动隧道上发 4 次 PING 测往返延迟；无活动隧道时请先启用一条到该设备的映射
        </span>
      </div>

      <el-descriptions
        v-if="pingResult"
        :column="3"
        border
        class="stun-result"
        data-testid="ping-result"
      >
        <el-descriptions-item label="目标设备">
          {{ pingResult.targetDevice }}（{{ pingResult.targetRemoteCode }}）
        </el-descriptions-item>
        <el-descriptions-item label="承载路径">
          {{ pingResult.viaRelay ? "中继" : "直连" }}
        </el-descriptions-item>
        <el-descriptions-item label="收到/发送">
          {{ pingResult.received }} / {{ pingResult.sent }}
        </el-descriptions-item>
        <el-descriptions-item label="最小 RTT">
          {{ pingResult.minMs === null ? "不可用" : `${pingResult.minMs} ms` }}
        </el-descriptions-item>
        <el-descriptions-item label="平均 RTT">
          {{ pingResult.avgMs === null ? "不可用" : `${pingResult.avgMs} ms` }}
        </el-descriptions-item>
        <el-descriptions-item label="最大 RTT">
          {{ pingResult.maxMs === null ? "不可用" : `${pingResult.maxMs} ms` }}
        </el-descriptions-item>
      </el-descriptions>
    </div>

    <!-- 服务端连通性（M3-14，FR-C-808）：settings 全候选逐个 TCP 探测 -->
    <div class="stun-section">
      <h3>服务端连通性</h3>
      <div class="stun-toolbar">
        <el-button
          type="primary"
          plain
          :loading="svtestBusy"
          data-testid="svtest-run"
          @click="runServerTest"
        >
          {{ svtestBusy ? "探测中…" : "测试连通性" }}
        </el-button>
        <span class="hint">
          对上方服务端地址全部候选逐个 TCP 探测（向导预检同源逻辑；不走控制通道，passive 亦可用）
        </span>
      </div>

      <el-table
        v-if="svtestResult"
        :data="svtestResult.items"
        size="small"
        class="stun-result"
        data-testid="svtest-result"
      >
        <el-table-column
          label="地址"
          prop="addr"
          min-width="180"
        />
        <el-table-column
          label="结果"
          width="100"
        >
          <template #default="{ row }">
            <el-tag
              :type="row.ok ? 'success' : 'danger'"
              size="small"
            >
              {{ row.ok ? "可达" : "不可达" }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column
          label="详情"
          prop="detail"
          min-width="220"
        />
      </el-table>
    </div>

    <!-- 打洞队列（M3-14：M1-29 诊断数据展示化） -->
    <div class="stun-section">
      <h3>打洞队列</h3>
      <div class="stun-toolbar">
        <el-button
          plain
          data-testid="punch-refresh"
          @click="refreshPunchQueue"
        >
          刷新
        </el-button>
        <span class="hint">
          打洞调度器实时状态：待打洞设备对深度与当前正在打洞的目标（空闲时无当前目标）
        </span>
      </div>

      <el-descriptions
        v-if="punchDepth !== null"
        :column="2"
        border
        class="stun-result"
        data-testid="punch-result"
      >
        <el-descriptions-item label="队列深度">
          {{ punchDepth }}
        </el-descriptions-item>
        <el-descriptions-item label="当前目标">
          {{ punchCurrent === null ? "（空闲）" : punchCurrent }}
        </el-descriptions-item>
      </el-descriptions>
    </div>

    <!-- 隧道与密钥轮换（M3-14：手动 REKEY 触发，05 §2.3 M2-21 预留收口） -->
    <div class="stun-section">
      <h3>隧道与密钥轮换（REKEY）</h3>
      <div class="stun-toolbar">
        <el-button
          plain
          data-testid="tunnels-refresh"
          @click="refreshTunnels"
        >
          刷新
        </el-button>
        <span class="hint">
          活动隧道列表（TunnelHost 本地表）：发起方角色可手动触发一次密钥轮换；响应方密钥由对端轮换
        </span>
      </div>

      <el-table
        v-if="tunnels.length > 0"
        :data="tunnels"
        size="small"
        class="stun-result"
        data-testid="tunnels-table"
      >
        <el-table-column
          label="目标设备"
          min-width="200"
        >
          <template #default="{ row }">
            {{ row.label ?? row.peerDeviceId.slice(0, 8) }}
          </template>
        </el-table-column>
        <el-table-column
          label="承载"
          width="90"
        >
          <template #default="{ row }">
            <el-tag
              :type="row.viaRelay ? 'warning' : 'success'"
              size="small"
            >
              {{ row.viaRelay ? "中继" : "直连" }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column
          label="本端角色"
          width="100"
        >
          <template #default="{ row }">
            {{ row.isInitiator ? "发起方" : "响应方" }}
          </template>
        </el-table-column>
        <el-table-column
          label="操作"
          width="120"
        >
          <template #default="{ row }">
            <el-button
              size="small"
              type="primary"
              plain
              :loading="rekeyBusy === row.peerDeviceId"
              :data-testid="`rekey-${row.peerDeviceId}`"
              @click="triggerRekey(row)"
            >
              轮换密钥
            </el-button>
          </template>
        </el-table-column>
      </el-table>

      <el-empty
        v-else
        description="无活动隧道（启用一条映射建立隧道后，可在此手动轮换密钥）"
        :image-size="48"
        data-testid="tunnels-empty"
      />
    </div>
  </section>
</template>

<style scoped>
.addrs { display: grid; gap: 8px; width: 100%; }
.addr-row { display: flex; gap: 8px; align-items: center; }
.addr-row .el-input { width: 280px; }
.addr-row .el-input.invalid :deep(input) { border-color: var(--el-color-danger); }
.slider-block { width: 100%; }
.hint { font-size: 12px; color: var(--el-text-color-secondary); margin-top: 4px; }
.readonly { margin-top: 24px; max-width: 640px; }
.stun-section { margin-top: 24px; max-width: 760px; }
.stun-section h3 { margin-bottom: 12px; }
.stun-toolbar { display: flex; gap: 12px; align-items: center; margin-bottom: 12px; }
.stun-result { margin-top: 8px; }
.stun-notes { margin-top: 8px; padding-left: 20px; }
.ping-code { width: 200px; }
</style>

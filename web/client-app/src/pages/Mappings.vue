<script setup lang="ts">
// M1-32 端口映射（06 §2、FR-C-804）：列表（状态 Tag、实时速率列、路径列）+
// 新建/编辑抽屉（端口 1~65535、远程码 6 位校验——04 §2.5 与服务端同口径）+ 启停/重试/删除。
// 实时速率来自 WS mapping_stats 直写 store（TD-16）；重试仅 failed 态提供（04 §2.5 /retry）。
// M2-28：UDP 协议单选启用（M2-20 引擎落地）；目标地址 self/IP 切换 + 开放网段提示
// （段外 4002 服务端 L3 白名单前置提示）；relay/invalid 状态 Tag（StatusTag 已含）。
import { computed, onMounted, reactive, ref } from "vue";
import { useRoute } from "vue-router";
import { ElMessage, ElMessageBox } from "element-plus";
import type { FormInstance } from "element-plus";
import { StatusTag, ApiError } from "@p2p/ui-shared";
import PassiveBanner from "../components/PassiveBanner.vue";
import { useMappingStore, type MappingFormInput } from "../stores/mappings";
import { useDeviceStore } from "../stores/devices";
import { useSystemStore } from "../stores/system";
import { makeMappingRules } from "./mappingFormRules";

const mappings = useMappingStore();
const system = useSystemStore();
const devices = useDeviceStore();
const route = useRoute();

const drawer = ref(false);
const busy = ref(false);
const formRef = ref<FormInstance>();
const form = reactive<MappingFormInput & { targetMode: "self" | "ip" }>({
  id: null,
  name: "",
  localPort: 8080,
  proto: "tcp",
  targetRemoteCode: "",
  targetAddr: "",
  targetMode: "self",
  targetPort: 80,
});

const isPassive = computed(() => system.device?.capability === "passive");
const editing = computed(() => form.id !== null);
/** targetAddr 必填性随模式联动（self 恒放行——服务端语义，04 §2.5）。 */
const rules = makeMappingRules(() => form.targetMode === "ip");

/** 目标设备现值（远程码匹配）——开放网段提示数据源（/api/devices，04 §2.4）。 */
const targetDevice = computed(() =>
  devices.items.find((d) => d.remoteCode === form.targetRemoteCode.trim()));

function humanRate(n: number): string {
  if (n >= 1024 * 1024) return `${(n / 1024 / 1024).toFixed(1)} MiB/s`;
  if (n >= 1024) return `${(n / 1024).toFixed(1)} KiB/s`;
  return `${n} B/s`;
}

function rate(id: string) {
  return mappings.rates.get(id);
}

function openCreate() {
  Object.assign(form, {
    id: null,
    name: "",
    localPort: 8080,
    proto: "tcp",
    targetRemoteCode: typeof route.query.remoteCode === "string" ? route.query.remoteCode : "",
    targetAddr: "",
    targetMode: "self",
    targetPort: 80,
  });
  drawer.value = true;
}

function openEdit(id: string) {
  const m = mappings.items.find((x) => x.mappingId === id);
  if (!m) return;
  Object.assign(form, {
    id: m.mappingId,
    name: m.name,
    localPort: m.localPort,
    proto: m.proto,
    targetRemoteCode: m.targetRemoteCode,
    targetAddr: m.targetAddr === "self" ? "" : m.targetAddr,
    targetMode: m.targetAddr === "self" ? "self" : "ip",
    targetPort: m.targetPort,
  });
  drawer.value = true;
}

async function save() {
  if (!(await formRef.value?.validate().catch(() => false))) return;
  busy.value = true;
  const payload: MappingFormInput = {
    ...form,
    targetAddr: form.targetMode === "ip" ? form.targetAddr.trim() : "self",
  };
  try {
    if (editing.value) await mappings.update(payload);
    else await mappings.create(payload);
    drawer.value = false;
    ElMessage.success(editing.value ? "已保存" : "已创建（默认停用，启用后开始打洞）");
  } catch (e) {
    ElMessage.error(e instanceof ApiError ? e.message : "请求失败");
  } finally {
    busy.value = false;
  }
}

async function toggle(id: string, enabled: boolean) {
  try {
    if (enabled) await mappings.enable(id);
    else await mappings.disable(id);
  } catch (e) {
    ElMessage.error(e instanceof ApiError ? e.message : "请求失败");
  }
}

async function retry(id: string) {
  try {
    await mappings.retry(id);
    ElMessage.info("已重新排队打洞");
  } catch (e) {
    ElMessage.error(e instanceof ApiError ? e.message : "请求失败");
  }
}

async function remove(id: string, name: string) {
  try {
    await ElMessageBox.confirm(`确定删除映射"${name}"？`, "删除确认", { type: "warning" });
  } catch {
    return; // 用户取消
  }
  try {
    await mappings.remove(id);
    ElMessage.success("已删除");
  } catch (e) {
    ElMessage.error(e instanceof ApiError ? e.message : "请求失败");
  }
}

onMounted(() => {
  void mappings.refresh();
  void devices.refresh(); // 目标设备开放网段提示（失败静默）
});
</script>

<template>
  <section class="mappings">
    <PassiveBanner />
    <div class="toolbar">
      <h2>端口映射</h2>
      <el-button
        type="primary"
        :disabled="isPassive"
        data-testid="mappings-new"
        @click="openCreate"
      >
        新建映射
      </el-button>
      <el-button
        data-testid="mappings-refresh"
        @click="mappings.refresh()"
      >
        刷新
      </el-button>
    </div>

    <el-table
      :data="mappings.items"
      data-testid="mappings-table"
      empty-text="暂无映射，点击右上角新建"
    >
      <el-table-column
        prop="name"
        label="名称"
        min-width="120"
      />
      <el-table-column
        label="本地监听"
        width="110"
      >
        <template #default="{ row }">
          {{ row.proto.toUpperCase() }} :{{ row.localPort }}
        </template>
      </el-table-column>
      <el-table-column
        label="目标"
        min-width="150"
      >
        <template #default="{ row }">
          <code>{{ row.targetRemoteCode }}</code> → {{ row.targetAddr }}:{{ row.targetPort }}
        </template>
      </el-table-column>
      <el-table-column
        label="状态"
        width="90"
      >
        <template #default="{ row }">
          <StatusTag
            :state="row.state"
            data-testid="mapping-state"
          />
          <el-tooltip
            v-if="row.detail"
            :content="row.detail"
            placement="top"
          >
            <span class="detail-mark">?</span>
          </el-tooltip>
        </template>
      </el-table-column>
      <el-table-column
        label="速率"
        width="170"
      >
        <template #default="{ row }">
          <span
            v-if="rate(row.mappingId)"
            class="rate"
            data-testid="mapping-rate"
          >
            ↑ {{ humanRate(rate(row.mappingId)!.rateUp) }} ↓ {{ humanRate(rate(row.mappingId)!.rateDown) }}
          </span>
          <span
            v-else
            class="muted"
          >—</span>
        </template>
      </el-table-column>
      <el-table-column
        label="路径"
        width="80"
      >
        <template #default="{ row }">
          {{ rate(row.mappingId)?.path ?? (row.state === "direct" ? "direct" : "—") }}
        </template>
      </el-table-column>
      <el-table-column
        label="操作"
        width="260"
        fixed="right"
      >
        <template #default="{ row }">
          <el-button
            v-if="!row.enabled"
            size="small"
            type="success"
            plain
            :disabled="isPassive"
            data-testid="mapping-enable"
            @click="toggle(row.mappingId, true)"
          >
            启用
          </el-button>
          <el-button
            v-else
            size="small"
            type="warning"
            plain
            :disabled="isPassive"
            data-testid="mapping-disable"
            @click="toggle(row.mappingId, false)"
          >
            停用
          </el-button>
          <el-button
            v-if="row.state === 'failed'"
            size="small"
            type="primary"
            plain
            :disabled="isPassive"
            data-testid="mapping-retry"
            @click="retry(row.mappingId)"
          >
            重试
          </el-button>
          <el-button
            size="small"
            :disabled="isPassive"
            data-testid="mapping-edit"
            @click="openEdit(row.mappingId)"
          >
            编辑
          </el-button>
          <el-button
            size="small"
            type="danger"
            plain
            :disabled="isPassive"
            data-testid="mapping-delete"
            @click="remove(row.mappingId, row.name)"
          >
            删除
          </el-button>
        </template>
      </el-table-column>
    </el-table>

    <!-- 新建/编辑抽屉 -->
    <el-drawer
      v-model="drawer"
      :title="editing ? '编辑映射' : '新建映射'"
      size="420px"
      data-testid="mapping-drawer"
    >
      <el-form
        ref="formRef"
        :model="form"
        :rules="rules"
        label-width="92px"
      >
        <el-form-item
          label="名称"
          prop="name"
        >
          <el-input
            v-model="form.name"
            data-testid="mapping-name"
          />
        </el-form-item>
        <el-form-item
          label="本地端口"
          prop="localPort"
        >
          <el-input-number
            v-model="form.localPort"
            :min="1"
            :max="65535"
            data-testid="mapping-localport"
          />
        </el-form-item>
        <el-form-item label="协议">
          <el-radio-group
            v-model="form.proto"
            data-testid="mapping-proto"
          >
            <el-radio value="tcp">
              TCP
            </el-radio>
            <el-radio value="udp">
              UDP
            </el-radio>
          </el-radio-group>
        </el-form-item>
        <el-form-item
          label="目标远程码"
          prop="targetRemoteCode"
        >
          <el-input
            v-model="form.targetRemoteCode"
            maxlength="6"
            data-testid="mapping-remote-code"
          />
        </el-form-item>
        <el-form-item label="目标地址">
          <el-radio-group
            v-model="form.targetMode"
            data-testid="mapping-target-mode"
          >
            <el-radio value="self">
              self（目标机本机）
            </el-radio>
            <el-radio value="ip">
              IP 地址
            </el-radio>
          </el-radio-group>
        </el-form-item>
        <el-form-item
          v-if="form.targetMode === 'ip'"
          label=" "
          prop="targetAddr"
        >
          <el-input
            v-model="form.targetAddr"
            placeholder="目标机可达的内网地址（如 192.168.1.50）"
            data-testid="mapping-target-addr"
          />
          <p
            v-if="targetDevice"
            class="muted seg-hint"
            data-testid="mapping-seg-hint"
          >
            对端「{{ targetDevice.deviceName }}」开放网段：{{
              targetDevice.lanSegments.length ? targetDevice.lanSegments.join("、") : "无（仅 self 放行）"
            }}——段外地址将被拒绝（4002）
          </p>
        </el-form-item>
        <el-form-item
          label="目标端口"
          prop="targetPort"
        >
          <el-input-number
            v-model="form.targetPort"
            :min="1"
            :max="65535"
            data-testid="mapping-targetport"
          />
        </el-form-item>
        <p class="muted">
          停用态才可改本地端口/协议（与服务端 1003 规则同口径）；创建后默认停用，启用即开始打洞
        </p>
        <el-form-item>
          <el-button
            type="primary"
            :loading="busy"
            data-testid="mapping-save"
            @click="save"
          >
            保存
          </el-button>
          <el-button @click="drawer = false">
            取消
          </el-button>
        </el-form-item>
      </el-form>
    </el-drawer>
  </section>
</template>

<style scoped>
.toolbar { display: flex; align-items: center; gap: 12px; margin-bottom: 12px; }
.toolbar h2 { margin: 0; font-size: 16px; }
.rate { font-size: 13px; white-space: nowrap; }
.muted { color: var(--el-text-color-secondary); font-size: 12px; }
.seg-hint { margin: 6px 0 0; width: 100%; }
.detail-mark {
  display: inline-block;
  margin-left: 4px;
  width: 14px;
  height: 14px;
  line-height: 14px;
  text-align: center;
  font-size: 10px;
  border: 1px solid var(--el-border-color);
  border-radius: 50%;
  color: var(--el-text-color-secondary);
  cursor: help;
}
</style>

<script setup lang="ts">
// M1-32 端口映射（06 §2、FR-C-804）：列表（状态 Tag、实时速率列、路径列）+
// 新建/编辑抽屉（端口 1~65535、远程码 6 位校验——04 §2.5 与服务端同口径）+ 启停/重试/删除。
// 实时速率来自 WS mapping_stats 直写 store（TD-16）；重试仅 failed 态提供（04 §2.5 /retry）。
import { computed, onMounted, reactive, ref } from "vue";
import { useRoute } from "vue-router";
import { ElMessage, ElMessageBox } from "element-plus";
import type { FormInstance } from "element-plus";
import { StatusTag, ApiError } from "@p2p/ui-shared";
import { useMappingStore, type MappingFormInput } from "../stores/mappings";
import { useSystemStore } from "../stores/system";
import { mappingFormRules as rules } from "./mappingFormRules";

const mappings = useMappingStore();
const system = useSystemStore();
const route = useRoute();

const drawer = ref(false);
const busy = ref(false);
const formRef = ref<FormInstance>();
const form = reactive<MappingFormInput>({
  id: null,
  name: "",
  localPort: 8080,
  proto: "tcp",
  targetRemoteCode: "",
  targetPort: 80,
});

const isPassive = computed(() => system.device?.capability === "passive");
const editing = computed(() => form.id !== null);

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
    targetPort: m.targetPort,
  });
  drawer.value = true;
}

async function save() {
  if (!(await formRef.value?.validate().catch(() => false))) return;
  busy.value = true;
  try {
    if (editing.value) await mappings.update(form);
    else await mappings.create(form);
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

onMounted(() => void mappings.refresh());
</script>

<template>
  <section class="mappings">
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
      <el-button @click="mappings.refresh()">
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
          <el-radio-group v-model="form.proto">
            <el-radio value="tcp">
              TCP
            </el-radio>
            <el-radio
              value="udp"
              disabled
              title="UDP 映射属 M2（TD-15）"
            >
              UDP（M2）
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
          <el-input
            model-value="self（目标机本机）"
            disabled
            title="M1 仅支持 self（目标机本机）；任意目标地址属 M2（4002 校验开放网段）"
          />
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

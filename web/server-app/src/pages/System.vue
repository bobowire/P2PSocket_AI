<script setup lang="ts">
// M3-11 系统配置与审计页（06 §3、FR-S-825、NFR-54）：server_config 白名单 18 键键值编辑表格
// （按类型控件：开关 bool/枚举 select/数值 input-number/文本——行级保存 PUT 单键，服务端
// 全量校验整单拒绝 400{1001} 前端显示错误）+重启生效提示 tag+审计日志（event 过滤+分页
// newest-first，detail 原始 JSON 文本截断透传）。
import { onMounted, ref } from "vue";
import { ElMessage } from "element-plus";
import { ApiError } from "@p2p/ui-shared"; // 值导入（instanceof 判定，type 导入会被 esbuild 剥掉）
import { AUDIT_PAGE_SIZE, useSystemStore } from "../stores/system";

const system = useSystemStore();
const drafts = ref<Record<string, string>>({}); // 行级编辑草稿（初始=现值）
const saving = ref("");

/// 控件类型映射（与服务端 AdminSystemApi.Validators 白名单同键集；值校验在服务端，
/// 前端控件只收敛输入形态）
const KEY_META: Record<string, { label: string; type: "bool" | "select" | "number" | "text"; options?: string[] }> = {
  registration_open: { label: "开放注册", type: "bool" },
  relay_enabled: { label: "中继服务", type: "bool" },
  relay_rate_limit: { label: "中继限速（B/s）", type: "number" },
  stun_auth: { label: "STUN 认证", type: "bool" },
  public_addr: { label: "公网通告地址", type: "text" },
  virtual_subnet: { label: "虚拟子网（CIDR）", type: "text" },
  default_join_policy: { label: "默认分组策略", type: "select", options: ["free", "approval"] },
  audit_retention_days: { label: "审计保留（天）", type: "number" },
  punch_retention_days: { label: "打洞统计保留（天）", type: "number" },
  max_devices: { label: "设备上限", type: "number" },
  stun_rate_per_ip: { label: "STUN 每 IP（pps）", type: "number" },
  stun_rate_per_device: { label: "STUN 每设备（QPS）", type: "number" },
  stun_circuit_pps: { label: "STUN 断路阈值（pps）", type: "number" },
  log_level: { label: "日志级别", type: "select", options: ["Trace", "Debug", "Information", "Warning", "Error", "Fatal"] },
  update_latest_version: { label: "升级-最新版本", type: "text" },
  update_min_protocol: { label: "升级-最低协议", type: "number" },
  update_url: { label: "升级-下载地址", type: "text" },
  update_notes: { label: "升级-说明", type: "text" },
};

const auditEventDraft = ref("");

function current(key: string): string {
  return system.config?.items.find((i) => i.key === key)?.value ?? "";
}

function draft(key: string): string {
  return drafts.value[key] ?? current(key);
}

function setDraft(key: string, v: string) {
  drafts.value[key] = v;
}

function dirty(key: string): boolean {
  return key in drafts.value && drafts.value[key] !== current(key);
}

async function save(key: string, value?: string) {
  saving.value = key;
  try {
    await system.saveKey(key, value ?? draft(key));
    delete drafts.value[key];
    ElMessage.success(`已保存 ${KEY_META[key]?.label ?? key}`);
  } catch (e) {
    // 服务端 400{1001} 整单拒绝：message 含键名与原因（前端如实显示）
    ElMessage.error(e instanceof ApiError ? e.message : "保存失败");
  } finally {
    saving.value = "";
  }
}

/// bool 键 el-switch：切换即存（无草稿态；与 Users 注册开关同交互）
async function onBoolChange(key: string, open: boolean) {
  await save(key, open ? "1" : "0");
}

function fmtTime(iso: string): string {
  return iso.replace("T", " ").replace(/(\.\d+|Z|\+.*$)/, "").slice(0, 19);
}

function shortDetail(d: string | null): string {
  return d && d.length > 80 ? `${d.slice(0, 80)}…` : (d ?? "—");
}

async function applyEventFilter() {
  try {
    await system.setAuditEvent(auditEventDraft.value.trim());
  } catch {
    ElMessage.error("查询失败");
  }
}

onMounted(async () => {
  await system.refreshConfig();
  await system.refreshAudits();
});
</script>

<template>
  <main class="page">
    <h2>系统</h2>

    <el-card
      class="cfg-card"
      data-testid="system-config"
    >
      <template #header>
        server_config（白名单 18 键；行级保存，服务端校验整单拒绝）
      </template>
      <el-table
        v-if="system.config"
        :data="system.config.items"
        data-testid="system-table"
      >
        <el-table-column
          label="配置项"
          min-width="170"
        >
          <template #default="{ row }">
            {{ KEY_META[row.key]?.label ?? row.key }}
          </template>
        </el-table-column>
        <el-table-column
          label="值"
          min-width="220"
        >
          <template #default="{ row }">
            <el-switch
              v-if="KEY_META[row.key]?.type === 'bool'"
              :model-value="draft(row.key) === '1'"
              :data-testid="`system-bool-${row.key}`"
              @change="(v: string | number | boolean) => void onBoolChange(row.key, v === true)"
            />
            <el-select
              v-else-if="KEY_META[row.key]?.type === 'select'"
              :model-value="draft(row.key)"
              style="width: 180px"
              :data-testid="`system-select-${row.key}`"
              @change="(v: string) => setDraft(row.key, v)"
            >
              <el-option
                v-for="o in KEY_META[row.key]?.options ?? []"
                :key="o"
                :label="o"
                :value="o"
              />
            </el-select>
            <el-input-number
              v-else-if="KEY_META[row.key]?.type === 'number'"
              :model-value="Number(draft(row.key))"
              :min="0"
              controls-position="right"
              style="width: 160px"
              :data-testid="`system-number-${row.key}`"
              @change="(v: number | undefined) => v !== undefined && setDraft(row.key, String(v))"
            />
            <el-input
              v-else
              :model-value="draft(row.key)"
              style="width: 220px"
              :data-testid="`system-text-${row.key}`"
              @input="(v: string) => setDraft(row.key, v)"
            />
          </template>
        </el-table-column>
        <el-table-column
          label="操作"
          width="90"
        >
          <template #default="{ row }">
            <el-button
              v-if="KEY_META[row.key]?.type !== 'bool'"
              size="small"
              type="primary"
              plain
              :disabled="!dirty(row.key)"
              :loading="saving === row.key"
              :data-testid="`system-save-${row.key}`"
              @click="save(row.key)"
            >
              保存
            </el-button>
            <span
              v-else
              class="muted"
            >即时</span>
          </template>
        </el-table-column>
        <el-table-column
          label="生效"
          width="100"
        >
          <template #default="{ row }">
            <el-tag
              v-if="row.restartRequired"
              type="warning"
              size="small"
            >
              重启生效
            </el-tag>
            <el-tag
              v-else
              type="success"
              size="small"
            >
              即时
            </el-tag>
          </template>
        </el-table-column>
      </el-table>
    </el-card>

    <el-card data-testid="system-audits">
      <template #header>
        审计日志（newest-first）
      </template>
      <div class="audit-filter">
        <el-input
          v-model="auditEventDraft"
          clearable
          placeholder="按事件过滤（如 group_join）"
          style="width: 260px"
          data-testid="audit-event-filter"
          @change="() => void applyEventFilter()"
          @clear="() => { auditEventDraft = ''; void applyEventFilter(); }"
        />
        <span class="muted">共 {{ system.audits?.total ?? 0 }} 条</span>
      </div>
      <el-table
        :data="system.audits?.items ?? []"
        data-testid="audit-table"
      >
        <el-table-column
          prop="id"
          label="#"
          width="70"
        />
        <el-table-column
          label="时间"
          min-width="150"
        >
          <template #default="{ row }">
            {{ fmtTime(row.ts) }}
          </template>
        </el-table-column>
        <el-table-column
          prop="event"
          label="事件"
          min-width="150"
        >
          <template #default="{ row }">
            <el-tag size="small">
              {{ row.event }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column
          label="详情"
          min-width="260"
        >
          <template #default="{ row }">
            <span :title="row.detail ?? ''">{{ shortDetail(row.detail) }}</span>
          </template>
        </el-table-column>
      </el-table>
      <el-pagination
        layout="total, prev, pager, next"
        :total="system.audits?.total ?? 0"
        :page-size="AUDIT_PAGE_SIZE"
        :current-page="system.auditPage"
        data-testid="audit-pagination"
        @current-change="(p: number) => void system.setAuditPage(p)"
      />
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
.audit-filter {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-bottom: 12px;
}
.muted {
  color: var(--el-text-color-secondary);
  font-size: 13px;
}
.el-pagination {
  margin-top: 16px;
  justify-content: flex-end;
}
</style>

<script setup lang="ts">
// M2-28 开放内网段页（06 §2、FR-C-806、SEC-52）：CIDR 表格增删 + 格式校验（与服务端
// NormalizeCidr 同口径：CIDR 或裸 IP——裸值按单地址 /32 或 /128 处理）+ 即时生效提示。
// 0x63 属本机管理类——passive 亦允许（无横幅/置灰）；移除段触发服务端 0x75：
// 段内存量映射 invalid（前端经 WS mapping_state → 列表置灰 + 全局失效提示）。
import { onMounted, reactive, ref } from "vue";
import { ElMessage, ElMessageBox } from "element-plus";
import type { FormInstance } from "element-plus";
import { ApiError } from "@p2p/ui-shared";
import { useSegmentStore } from "../stores/segments";
import { normalizeCidrPreview, segmentsFormRules } from "./segmentsRules";

const segments = useSegmentStore();
const formRef = ref<FormInstance>();
const form = reactive({ cidr: "" });
const busy = ref(false);

async function add() {
  if (!(await formRef.value?.validate().catch(() => false))) return;
  busy.value = true;
  try {
    await segments.add(form.cidr.trim());
    ElMessage.success("已添加并即时生效（本地目标过滤立即更新）");
    form.cidr = "";
    formRef.value?.clearValidate();
  } catch (e) {
    ElMessage.error(e instanceof ApiError ? e.message : "请求失败");
  } finally {
    busy.value = false;
  }
}

async function remove(segmentId: string, cidr: string) {
  try {
    await ElMessageBox.confirm(
      `确定移除网段 ${cidr}？段内存量映射将失效（对端收 0x75 提示）。`,
      "移除确认",
      { type: "warning" },
    );
  } catch {
    return; // 用户取消
  }
  try {
    await segments.remove(segmentId);
    ElMessage.success("已移除并即时生效");
  } catch (e) {
    ElMessage.error(e instanceof ApiError ? e.message : "请求失败");
  }
}

onMounted(() => void segments.refresh());
</script>

<template>
  <section class="segments">
    <div class="toolbar">
      <h2>开放内网段</h2>
      <el-button
        data-testid="segments-refresh"
        @click="segments.refresh()"
      >
        刷新
      </el-button>
    </div>

    <el-alert
      type="info"
      :closable="false"
      show-icon
      title="变更即时生效"
      class="hint"
    >
      开放网段是本机对外的目标地址白名单（self 恒放行）：段内地址可建映射，段外将被拒绝（4002）；
      移除网段会使段内存量映射失效并推送对端。全开放须显式 0.0.0.0/0 或 ::/0。
    </el-alert>

    <el-form
      ref="formRef"
      :model="form"
      :rules="segmentsFormRules"
      inline
      class="add-form"
    >
      <el-form-item prop="cidr">
        <el-input
          v-model="form.cidr"
          placeholder="如 192.168.1.0/24 或 10.0.0.5（单地址）"
          class="cidr-input"
          data-testid="segments-cidr"
          @keyup.enter="add"
        />
      </el-form-item>
      <el-form-item>
        <el-button
          type="primary"
          :loading="busy"
          data-testid="segments-add"
          @click="add"
        >
          添加
        </el-button>
      </el-form-item>
      <el-form-item v-if="form.cidr.trim() && !form.cidr.includes('/')">
        <span class="muted">将按单地址 {{ normalizeCidrPreview(form.cidr) }} 处理</span>
      </el-form-item>
    </el-form>

    <el-table
      :data="segments.items"
      data-testid="segments-table"
      empty-text="暂无开放网段（除 self 外全部目标地址将被拒绝）"
    >
      <el-table-column
        prop="cidr"
        label="网段"
        min-width="200"
      >
        <template #default="{ row }">
          <code class="cidr">{{ row.cidr }}</code>
        </template>
      </el-table-column>
      <el-table-column
        label="状态"
        width="100"
      >
        <template #default="{ row }">
          <el-tag
            :type="row.enabled ? 'success' : 'info'"
            size="small"
          >
            {{ row.enabled ? "生效中" : "已停用" }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column
        label="操作"
        width="100"
      >
        <template #default="{ row }">
          <el-button
            size="small"
            type="danger"
            plain
            data-testid="segments-delete"
            @click="remove(row.segmentId, row.cidr)"
          >
            移除
          </el-button>
        </template>
      </el-table-column>
    </el-table>
  </section>
</template>

<style scoped>
.toolbar { display: flex; align-items: center; gap: 12px; margin-bottom: 12px; }
.toolbar h2 { margin: 0; font-size: 16px; }
.hint { margin-bottom: 12px; }
.add-form { margin-bottom: 12px; }
.cidr-input { width: 320px; }
.cidr {
  font-size: 13px;
  background: var(--el-fill-color-light);
  border-radius: var(--el-border-radius-base);
  padding: 0 6px;
}
.muted { color: var(--el-text-color-secondary); font-size: 12px; }
</style>

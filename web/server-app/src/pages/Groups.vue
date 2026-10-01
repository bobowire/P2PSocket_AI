<script setup lang="ts">
// M3-10 分组管理页（06 §3、FR-S-822/305）：默认分组准入策略编辑（PUT 双写：键=下次建组+
// 默认组行=存量即时生效）+ 分组总览（成员数/待审批数）+ 跨分组审批队列（approve/reject 走
// GroupService 共享核——与客户端 0x53 所有者审批同一执行链，不双入组）。
import { onMounted, ref } from "vue";
import { ElMessage, ElMessageBox } from "element-plus";
import { useGroupsStore } from "../stores/groups";

const groups = useGroupsStore();
const policyDraft = ref(""); // 编辑草稿（保存后跟随 store 回读）

function fmtTime(iso: string): string {
  return iso.replace("T", " ").replace(/(\.\d+|Z).*$/, "");
}

async function savePolicy() {
  if (policyDraft.value === groups.defaultPolicy) return;
  await ElMessageBox.confirm(
    `默认分组准入策略改为"${policyDraft.value === "free" ? "自由加入" : "需审批"}"？` +
      "存量默认分组即时生效，其他新建分组从该默认取值。",
    "策略变更",
    { type: "warning" },
  );
  await groups.saveDefaultPolicy(policyDraft.value);
  policyDraft.value = groups.defaultPolicy;
  ElMessage.success("默认分组策略已更新");
}

async function decide(requestId: string, groupName: string, deviceName: string, approve: boolean) {
  await ElMessageBox.confirm(
    `确定${approve ? "批准" : "拒绝"}设备"${deviceName}"加入分组"${groupName}"的申请？`,
    approve ? "批准申请" : "拒绝申请",
    { type: approve ? "info" : "warning" },
  );
  if (approve) await groups.approve(requestId);
  else await groups.reject(requestId);
  ElMessage.success(approve ? "已批准入组" : "已拒绝申请");
}

function onRequestsLoaded() {
  // 队列与总览加载完成后同步草稿（首载）
  if (!policyDraft.value) policyDraft.value = groups.defaultPolicy;
}

async function loadAll() {
  await Promise.all([groups.refresh(), groups.refreshRequests()]);
  onRequestsLoaded();
}

onMounted(() => void loadAll());
</script>

<template>
  <main class="page">
    <h2>分组</h2>

    <el-card
      class="policy-card"
      data-testid="groups-policy"
    >
      <template #header>
        默认分组准入策略
      </template>
      <el-select
        v-model="policyDraft"
        data-testid="groups-policy-select"
        style="width: 160px"
      >
        <el-option
          label="自由加入（free）"
          value="free"
        />
        <el-option
          label="需审批（approval）"
          value="approval"
        />
      </el-select>
      <el-button
        type="primary"
        plain
        data-testid="groups-policy-save"
        :disabled="policyDraft === groups.defaultPolicy || !policyDraft"
        @click="savePolicy"
      >
        保存
      </el-button>
      <span class="hint">当前：{{ groups.defaultPolicy === "free" ? "自由加入" : "需审批" }}</span>
    </el-card>

    <el-card
      class="requests-card"
      data-testid="groups-requests"
    >
      <template #header>
        待审批申请（{{ groups.requests?.items.length ?? 0 }}）
      </template>
      <el-empty
        v-if="(groups.requests?.items.length ?? 0) === 0"
        description="暂无待审批申请"
      />
      <div
        v-for="r in groups.requests?.items ?? []"
        :key="r.requestId"
        class="request-row"
        :data-testid="`group-request-${r.deviceName}`"
      >
        <div class="request-info">
          <strong>{{ r.deviceName }}</strong> 申请加入 <strong>{{ r.groupName }}</strong>
          <span class="muted">（所有者 {{ r.ownerUsername }} · {{ fmtTime(r.createdAt) }}）</span>
        </div>
        <div>
          <el-button
            size="small"
            type="success"
            plain
            :data-testid="`request-approve-${r.deviceName}`"
            @click="decide(r.requestId, r.groupName, r.deviceName, true)"
          >
            批准
          </el-button>
          <el-button
            size="small"
            type="danger"
            plain
            :data-testid="`request-reject-${r.deviceName}`"
            @click="decide(r.requestId, r.groupName, r.deviceName, false)"
          >
            拒绝
          </el-button>
        </div>
      </div>
    </el-card>

    <el-table
      :data="groups.list?.items ?? []"
      data-testid="groups-table"
    >
      <el-table-column
        prop="name"
        label="分组"
        min-width="150"
      >
        <template #default="{ row }">
          <el-tag
            v-if="row.isDefault"
            type="warning"
            size="small"
            class="default-tag"
          >
            默认
          </el-tag>
          {{ row.name }}
        </template>
      </el-table-column>
      <el-table-column
        label="准入策略"
        width="110"
      >
        <template #default="{ row }">
          <el-tag
            :type="row.joinPolicy === 'free' ? 'success' : 'info'"
            size="small"
          >
            {{ row.joinPolicy === "free" ? "自由加入" : "需审批" }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column
        label="所有者"
        min-width="110"
      >
        <template #default="{ row }">
          {{ row.ownerUsername ?? "—" }}
        </template>
      </el-table-column>
      <el-table-column
        prop="memberCount"
        label="成员数"
        width="80"
      />
      <el-table-column
        prop="pendingCount"
        label="待审批"
        width="80"
      />
      <el-table-column
        label="创建时间"
        min-width="150"
      >
        <template #default="{ row }">
          {{ fmtTime(row.createdAt) }}
        </template>
      </el-table-column>
    </el-table>
  </main>
</template>

<style scoped>
.page {
  padding: 24px;
}
.policy-card {
  margin-bottom: 16px;
}
.requests-card {
  margin-bottom: 16px;
}
.request-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 8px 0;
  border-bottom: 1px solid var(--el-border-color-lighter);
}
.request-row:last-child {
  border-bottom: none;
}
.muted {
  color: var(--el-text-color-secondary);
  font-size: 13px;
}
.hint {
  margin-left: 12px;
  font-size: 13px;
  color: var(--el-text-color-secondary);
}
.default-tag {
  margin-right: 4px;
}
</style>

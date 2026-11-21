<script setup lang="ts">
// 简易版·分组（FR-C-810）：行式列表（组名·成员数·角色）+ 所有者待审入口（跳高级版审批）
// + 凭码入组（inviteCodeRule 同口径校验；3002 转待审批提示、3001 等错误内联）。
// 建组/改名/审批/邀请码管理等全量能力在高级版 /groups（简易版只留高频操作——调研定稿）。
import { computed, onMounted, reactive, ref } from "vue";
import { ElMessage } from "element-plus";
import type { FormInstance } from "element-plus";
import { ApiError, dialogWidth } from "@p2p/ui-shared";
import PassiveBanner from "../../components/PassiveBanner.vue";
import { useGroupStore } from "../../stores/groups";
import { useSystemStore } from "../../stores/system";
import { INVITE_CODE_MESSAGE, INVITE_CODE_PATTERN, normalizeInviteCode } from "../inviteCodeRule";

const groups = useGroupStore();
const system = useSystemStore();
const isPassive = computed(() => system.device?.capability === "passive");

/** 待审计数按组聚合（requests 与 items 同源 GET /api/groups，04 §2.4）。 */
const pendingByGroup = computed(() => {
  const map: Record<string, number> = {};
  for (const r of groups.requests) map[r.groupId] = (map[r.groupId] ?? 0) + 1;
  return map;
});

const joinVisible = ref(false);
const joinBusy = ref(false);
const joinFormRef = ref<FormInstance>();
const joinForm = reactive({ inviteCode: "" });
const joinError = ref("");
const joinRules = {
  inviteCode: [
    { required: true, message: "请输入邀请码", trigger: "blur" },
    { pattern: INVITE_CODE_PATTERN, message: INVITE_CODE_MESSAGE, trigger: "blur" },
  ],
};

function openJoin() {
  joinForm.inviteCode = "";
  joinError.value = "";
  joinVisible.value = true;
}

/** 凭码入组（0x51）：free 即入；3002=申请待审批（info 提示后关窗）；3001 等内联呈现。 */
async function submitJoin() {
  if (!(await joinFormRef.value?.validate().catch(() => false))) return;
  joinBusy.value = true;
  joinError.value = "";
  try {
    await groups.join(normalizeInviteCode(joinForm.inviteCode));
    ElMessage.success("已加入分组");
    joinVisible.value = false;
  } catch (e) {
    if (e instanceof ApiError && e.code === 3002) {
      ElMessage.info("已提交入组申请，等待所有者审批");
      joinVisible.value = false;
    } else {
      joinError.value = e instanceof ApiError ? e.message : "请求失败";
    }
  } finally {
    joinBusy.value = false;
  }
}

onMounted(() => {
  void groups.refresh();
  void system.refresh();
});
</script>

<template>
  <section class="simple-groups">
    <PassiveBanner />
    <div class="page-head">
      <h2>分组</h2>
      <el-button
        type="primary"
        :disabled="isPassive"
        data-testid="simple-groups-join"
        @click="openJoin"
      >
        凭码入组
      </el-button>
    </div>

    <div
      v-if="groups.items.length"
      class="group-rows"
    >
      <div
        v-for="g in groups.items"
        :key="g.groupId"
        class="group-row"
        :data-testid="`group-row-${g.groupId}`"
      >
        <span class="g-name">{{ g.groupName }}</span>
        <span class="muted">{{ g.memberCount }} 名成员</span>
        <el-tag
          v-if="g.isOwner"
          size="small"
        >
          所有者
        </el-tag>
        <el-tag
          v-else
          size="small"
          type="info"
        >
          成员
        </el-tag>
        <span class="spacer" />
        <RouterLink
          v-if="g.isOwner && pendingByGroup[g.groupId]"
          to="/groups"
          class="pending"
          data-testid="simple-groups-pending"
        >
          待审 {{ pendingByGroup[g.groupId] }}，去审批
        </RouterLink>
      </div>
    </div>
    <el-empty
      v-else
      description="还没有加入任何分组；可凭邀请码加入，或到高级版创建"
    />

    <p class="muted adv-hint">
      建组、审批成员、邀请码管理等更多操作在
      <RouterLink to="/groups">
        高级版·分组
      </RouterLink>
    </p>

    <el-dialog
      v-model="joinVisible"
      title="凭码入组"
      :width="dialogWidth('420px')"
      data-testid="simple-join-dialog"
    >
      <el-form
        ref="joinFormRef"
        :model="joinForm"
        :rules="joinRules"
        label-width="72px"
        @submit.prevent
      >
        <el-form-item
          label="邀请码"
          prop="inviteCode"
        >
          <el-input
            v-model="joinForm.inviteCode"
            maxlength="6"
            placeholder="6 位邀请码"
            data-testid="simple-join-code"
          />
        </el-form-item>
      </el-form>
      <p
        v-if="joinError"
        class="error"
        data-testid="simple-join-error"
      >
        {{ joinError }}
      </p>
      <template #footer>
        <el-button
          :loading="joinBusy"
          type="primary"
          data-testid="simple-join-submit"
          @click="submitJoin"
        >
          加入
        </el-button>
        <el-button @click="joinVisible = false">
          取消
        </el-button>
      </template>
    </el-dialog>
  </section>
</template>

<style scoped>
.page-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 16px;
}
.page-head h2 { margin: 0; }
/* 简易版科技感：分组行容器走青蓝暗框（与玻璃卡同族） */
.group-rows {
  display: flex;
  flex-direction: column;
  border: 1px solid rgba(56, 189, 248, 0.18);
  border-radius: 12px;
  background: rgba(10, 16, 30, 0.5);
  overflow: hidden;
}
.group-row {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 12px 14px;
  font-size: 14px;
}
.group-row + .group-row { border-top: 1px solid rgba(56, 189, 248, 0.1); }
.g-name { font-weight: 600; color: #e2e8f0; }
.spacer { flex: 1; }
.pending {
  color: #fbbf24;
  text-decoration: none;
  font-size: 13px;
  font-weight: 600;
  text-shadow: 0 0 10px rgba(251, 191, 36, 0.4);
}
.adv-hint { margin-top: 14px; }
.error { color: var(--el-color-danger); font-size: 13px; margin: 4px 0 0; }
</style>

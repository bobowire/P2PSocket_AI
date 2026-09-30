<script setup lang="ts">
// M2-28 分组全量页（06 §2、FR-C-805，替换 M1-33 占位）：已加入列表（策略/成员数/角色）+
// 凭码入组（0x51；approval 组 3002 → 待审批提示）+ 新建（准入策略单选，0x50）+
// 所有者操作：审批队列（0x53 Approve/Reject）、邀请码（0x54 覆盖式/撤销）、
// 成员管理（0x57 移出——成员取自可见设备同组过滤，0x40 维度）、编辑（0x55）、解散（0x56）；
// 成员自退（0x52）。分组族全属主动类——passive 置灰 + 横幅（05 §8）。
// 成员资格变更 → WS device_list → devices refetch（TD-16；服务端 0x41 推送链 M2-10）。
import { computed, onMounted, reactive, ref } from "vue";
import { ElMessage, ElMessageBox } from "element-plus";
import type { FormInstance, FormRules } from "element-plus";
import { ApiError, CodeText } from "@p2p/ui-shared";
import PassiveBanner from "../components/PassiveBanner.vue";
import { useGroupStore, type GroupFormInput } from "../stores/groups";
import { useDeviceStore } from "../stores/devices";
import { useSystemStore } from "../stores/system";

const groups = useGroupStore();
const devices = useDeviceStore();
const system = useSystemStore();

const isPassive = computed(() => system.device?.capability === "passive");

const nameRules: FormRules = {
  name: [
    { required: true, message: "请输入分组名", trigger: "blur" },
    { max: 64, message: "分组名最长 64 字符", trigger: "blur" },
  ],
};

// ── 凭码入组（0x51）──────────────────────────────────────────────
const joinVisible = ref(false);
const joinRef = ref<FormInstance>();
const joinForm = reactive({ inviteCode: "" });
const joinBusy = ref(false);
const joinRules: FormRules = {
  inviteCode: [
    { required: true, message: "请输入邀请码", trigger: "blur" },
    { pattern: /^[0-9A-Z]{6}$/, message: "邀请码为 6 位去混淆字符（大写字母数字）", trigger: "blur" },
  ],
};

function openJoin() {
  joinForm.inviteCode = "";
  joinVisible.value = true;
}

async function submitJoin() {
  if (!(await joinRef.value?.validate().catch(() => false))) return;
  joinBusy.value = true;
  try {
    await groups.join(joinForm.inviteCode.trim());
    ElMessage.success("已加入分组");
    joinVisible.value = false;
  } catch (e) {
    if (e instanceof ApiError && e.code === 3002) {
      // approval 准入：申请单已建（原码透传），引导等待所有者审批
      ElMessage.info("该分组需审批：已提交申请，等待所有者处理");
      joinVisible.value = false;
    } else {
      ElMessage.error(e instanceof ApiError ? e.message : "请求失败");
    }
  } finally {
    joinBusy.value = false;
  }
}

// ── 新建分组（0x50）──────────────────────────────────────────────
const createVisible = ref(false);
const createRef = ref<FormInstance>();
const createForm = reactive<GroupFormInput>({ name: "", joinPolicy: "free" });
const createBusy = ref(false);

function openCreate() {
  Object.assign(createForm, { name: "", joinPolicy: "free" });
  createVisible.value = true;
}

async function submitCreate() {
  if (!(await createRef.value?.validate().catch(() => false))) return;
  createBusy.value = true;
  try {
    await groups.create({ name: createForm.name.trim(), joinPolicy: createForm.joinPolicy });
    ElMessage.success("分组已创建（你是所有者）");
    createVisible.value = false;
  } catch (e) {
    ElMessage.error(e instanceof ApiError ? e.message : "请求失败");
  } finally {
    createBusy.value = false;
  }
}

// ── 编辑（0x55，所有者）──────────────────────────────────────────
const editVisible = ref(false);
const editRef = ref<FormInstance>();
const editForm = reactive<GroupFormInput & { id: string }>({ id: "", name: "", joinPolicy: "free" });
const editBusy = ref(false);

function openEdit(id: string) {
  const g = groups.items.find((x) => x.groupId === id);
  if (!g) return;
  Object.assign(editForm, { id, name: g.groupName, joinPolicy: g.policy === "approval" ? "approval" : "free" });
  editVisible.value = true;
}

async function submitEdit() {
  if (!(await editRef.value?.validate().catch(() => false))) return;
  editBusy.value = true;
  try {
    await groups.update(editForm.id, { name: editForm.name.trim(), joinPolicy: editForm.joinPolicy });
    ElMessage.success("已保存");
    editVisible.value = false;
  } catch (e) {
    ElMessage.error(e instanceof ApiError ? e.message : "请求失败");
  } finally {
    editBusy.value = false;
  }
}

// ── 邀请码（0x54 覆盖式：每分组至多一码）─────────────────────────
const inviteVisible = ref(false);
const invite = reactive({ groupId: "", groupName: "", code: null as string | null, busy: false });

async function openInvite(id: string) {
  const g = groups.items.find((x) => x.groupId === id);
  if (!g) return;
  invite.groupId = id;
  invite.groupName = g.groupName;
  invite.code = null;
  inviteVisible.value = true;
  try {
    invite.code = await groups.genInvite(id); // 打开即生成/取回（覆盖旧码）
  } catch (e) {
    ElMessage.error(e instanceof ApiError ? e.message : "请求失败");
  }
}

async function regenInvite(id: string) {
  invite.busy = true;
  try {
    invite.code = await groups.genInvite(id);
  } catch (e) {
    ElMessage.error(e instanceof ApiError ? e.message : "请求失败");
  } finally {
    invite.busy = false;
  }
}

async function revokeInvite(id: string) {
  invite.busy = true;
  try {
    await groups.revokeInvite(id);
    invite.code = null;
    ElMessage.success("已撤销（旧码立即失效）");
  } catch (e) {
    ElMessage.error(e instanceof ApiError ? e.message : "请求失败");
  } finally {
    invite.busy = false;
  }
}

// ── 成员管理（0x57 移出；成员取自可见设备同组过滤，0x40 维度）────
const membersVisible = ref(false);
const members = reactive({ groupId: "", groupName: "", items: [] as typeof devices.items });

function openMembers(id: string) {
  const g = groups.items.find((x) => x.groupId === id);
  if (!g) return;
  members.groupId = id;
  members.groupName = g.groupName;
  members.items = devices.items.filter(
    (d) => d.groups.includes(g.groupName) && d.deviceId !== system.device?.deviceId,
  );
  membersVisible.value = true;
}

async function kick(deviceId: string, deviceName: string) {
  try {
    await ElMessageBox.confirm(`确定将"${deviceName}"移出分组？其跨分组可见性即时回收。`, "移出确认", {
      type: "warning",
    });
  } catch {
    return; // 用户取消
  }
  try {
    await groups.kick(members.groupId, deviceId);
    members.items = members.items.filter((d) => d.deviceId !== deviceId);
    ElMessage.success("已移出");
  } catch (e) {
    ElMessage.error(e instanceof ApiError ? e.message : "请求失败");
  }
}

// ── 退组（0x52）/ 解散（0x56）────────────────────────────────────
async function leave(id: string, name: string) {
  try {
    await ElMessageBox.confirm(`确定退出分组"${name}"？`, "退组确认", { type: "warning" });
  } catch {
    return;
  }
  try {
    await groups.leave(id);
    ElMessage.success("已退出");
  } catch (e) {
    ElMessage.error(e instanceof ApiError ? e.message : "请求失败");
  }
}

async function dissolve(id: string, name: string) {
  try {
    await ElMessageBox.confirm(
      `确定解散分组"${name}"？组内全部成员的跨分组可见性即时回收，不可恢复。`,
      "解散确认",
      { type: "warning" },
    );
  } catch {
    return;
  }
  try {
    await groups.dissolve(id);
    ElMessage.success("已解散");
  } catch (e) {
    ElMessage.error(e instanceof ApiError ? e.message : "请求失败");
  }
}

// ── 审批（0x53 Approve/Reject，仅所有者）────────────────────────
const decideBusy = ref("");

async function approve(requestId: string) {
  decideBusy.value = requestId;
  try {
    await groups.approve(requestId);
    ElMessage.success("已批准入组");
  } catch (e) {
    ElMessage.error(e instanceof ApiError ? e.message : "请求失败");
  } finally {
    decideBusy.value = "";
  }
}

async function reject(requestId: string) {
  decideBusy.value = requestId;
  try {
    await groups.reject(requestId);
    ElMessage.info("已拒绝");
  } catch (e) {
    ElMessage.error(e instanceof ApiError ? e.message : "请求失败");
  } finally {
    decideBusy.value = "";
  }
}

const groupNameOf = (groupId: string) =>
  groups.items.find((g) => g.groupId === groupId)?.groupName ?? groupId;

function fmtTime(ms: number): string {
  return new Date(ms).toLocaleString();
}

onMounted(() => {
  void system.refresh(); // 成员管理需本机 deviceId（排除自身）+ passive 置灰随 capability
  void groups.refresh();
  void devices.refresh(); // 成员管理数据源（同组可见设备）
});
</script>

<template>
  <section class="groups">
    <PassiveBanner />
    <div class="toolbar">
      <h2>分组</h2>
      <el-button
        type="primary"
        plain
        :disabled="isPassive"
        data-testid="groups-join"
        @click="openJoin"
      >
        凭码入组
      </el-button>
      <el-button
        type="primary"
        :disabled="isPassive"
        data-testid="groups-create"
        @click="openCreate"
      >
        新建分组
      </el-button>
      <el-button
        data-testid="groups-refresh"
        @click="groups.refresh()"
      >
        刷新
      </el-button>
    </div>

    <el-table
      :data="groups.items"
      data-testid="groups-table"
      empty-text="暂未加入任何分组（凭码入组或新建）"
    >
      <el-table-column
        prop="groupName"
        label="分组名"
        min-width="160"
      />
      <el-table-column
        label="准入策略"
        width="110"
      >
        <template #default="{ row }">
          <el-tag
            :type="row.policy === 'approval' ? 'warning' : 'success'"
            size="small"
          >
            {{ row.policy === "approval" ? "需审批" : "自由加入" }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column
        prop="memberCount"
        label="成员数"
        width="90"
      />
      <el-table-column
        label="我的角色"
        width="100"
      >
        <template #default="{ row }">
          <el-tag
            :type="row.isOwner ? 'primary' : 'info'"
            size="small"
          >
            {{ row.isOwner ? "所有者" : "成员" }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column
        label="操作"
        width="300"
        fixed="right"
      >
        <template #default="{ row }">
          <template v-if="row.isOwner">
            <el-button
              size="small"
              :disabled="isPassive"
              data-testid="groups-invite"
              @click="openInvite(row.groupId)"
            >
              邀请码
            </el-button>
            <el-button
              size="small"
              :disabled="isPassive"
              data-testid="groups-members"
              @click="openMembers(row.groupId)"
            >
              成员
            </el-button>
            <el-button
              size="small"
              :disabled="isPassive"
              data-testid="groups-edit"
              @click="openEdit(row.groupId)"
            >
              编辑
            </el-button>
            <el-button
              size="small"
              type="danger"
              plain
              :disabled="isPassive"
              data-testid="groups-dissolve"
              @click="dissolve(row.groupId, row.groupName)"
            >
              解散
            </el-button>
          </template>
          <el-button
            v-else
            size="small"
            type="warning"
            plain
            :disabled="isPassive"
            data-testid="groups-leave"
            @click="leave(row.groupId, row.groupName)"
          >
            退组
          </el-button>
        </template>
      </el-table-column>
    </el-table>

    <!-- 审批队列（所有者侧聚合，FR-S-305） -->
    <el-card
      v-if="groups.requests.length > 0"
      class="requests"
      data-testid="groups-requests-card"
    >
      <template #header>
        待审批申请（{{ groups.requests.length }}）
      </template>
      <el-table
        :data="groups.requests"
        data-testid="groups-requests"
        size="small"
      >
        <el-table-column
          prop="deviceName"
          label="申请设备"
          min-width="140"
        />
        <el-table-column
          label="申请分组"
          min-width="140"
        >
          <template #default="{ row }">
            {{ groupNameOf(row.groupId) }}
          </template>
        </el-table-column>
        <el-table-column
          label="申请时间"
          width="170"
        >
          <template #default="{ row }">
            {{ fmtTime(row.createdAtMs) }}
          </template>
        </el-table-column>
        <el-table-column
          label="操作"
          width="140"
        >
          <template #default="{ row }">
            <el-button
              size="small"
              type="success"
              plain
              :disabled="isPassive || decideBusy !== ''"
              data-testid="groups-approve"
              @click="approve(row.requestId)"
            >
              批准
            </el-button>
            <el-button
              size="small"
              type="danger"
              plain
              :disabled="isPassive || decideBusy !== ''"
              data-testid="groups-reject"
              @click="reject(row.requestId)"
            >
              拒绝
            </el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-card>

    <!-- 凭码入组 -->
    <el-dialog
      v-model="joinVisible"
      title="凭码入组"
      width="380px"
      data-testid="groups-join-dialog"
    >
      <el-form
        ref="joinRef"
        :model="joinForm"
        :rules="joinRules"
        label-width="72px"
      >
        <el-form-item
          label="邀请码"
          prop="inviteCode"
        >
          <el-input
            v-model="joinForm.inviteCode"
            maxlength="6"
            data-testid="groups-join-code"
          />
        </el-form-item>
        <p class="muted">
          自由加入的分组即时入组；需审批的分组将提交申请（3002）
        </p>
        <el-form-item>
          <el-button
            type="primary"
            :loading="joinBusy"
            data-testid="groups-join-save"
            @click="submitJoin"
          >
            入组
          </el-button>
        </el-form-item>
      </el-form>
    </el-dialog>

    <!-- 新建分组 -->
    <el-dialog
      v-model="createVisible"
      title="新建分组"
      width="380px"
      data-testid="groups-create-dialog"
    >
      <el-form
        ref="createRef"
        :model="createForm"
        :rules="nameRules"
        label-width="72px"
      >
        <el-form-item
          label="分组名"
          prop="name"
        >
          <el-input
            v-model="createForm.name"
            maxlength="64"
            data-testid="groups-name"
          />
        </el-form-item>
        <el-form-item label="准入策略">
          <el-radio-group
            v-model="createForm.joinPolicy"
            data-testid="groups-policy"
          >
            <el-radio value="free">
              自由加入
            </el-radio>
            <el-radio value="approval">
              需审批
            </el-radio>
          </el-radio-group>
        </el-form-item>
        <el-form-item>
          <el-button
            type="primary"
            :loading="createBusy"
            data-testid="groups-create-save"
            @click="submitCreate"
          >
            创建
          </el-button>
        </el-form-item>
      </el-form>
    </el-dialog>

    <!-- 编辑分组（所有者） -->
    <el-dialog
      v-model="editVisible"
      title="编辑分组"
      width="380px"
      data-testid="groups-edit-dialog"
    >
      <el-form
        ref="editRef"
        :model="editForm"
        :rules="nameRules"
        label-width="72px"
      >
        <el-form-item
          label="分组名"
          prop="name"
        >
          <el-input
            v-model="editForm.name"
            maxlength="64"
            data-testid="groups-edit-name"
          />
        </el-form-item>
        <el-form-item label="准入策略">
          <el-radio-group v-model="editForm.joinPolicy">
            <el-radio value="free">
              自由加入
            </el-radio>
            <el-radio value="approval">
              需审批
            </el-radio>
          </el-radio-group>
        </el-form-item>
        <el-form-item>
          <el-button
            type="primary"
            :loading="editBusy"
            data-testid="groups-edit-save"
            @click="submitEdit"
          >
            保存
          </el-button>
        </el-form-item>
      </el-form>
    </el-dialog>

    <!-- 邀请码（所有者；覆盖式） -->
    <el-dialog
      v-model="inviteVisible"
      :title="`邀请码 · ${invite.groupName}`"
      width="400px"
      data-testid="groups-invite-dialog"
    >
      <div
        v-if="invite.code"
        class="invite-code"
        data-testid="groups-invite-code"
      >
        <CodeText
          :value="invite.code"
          label="邀请码"
        />
        <p class="muted">
          交给要加入的设备（凭码入组）；重新生成将覆盖旧码，撤销后旧码立即失效
        </p>
        <div class="row">
          <el-button
            size="small"
            :loading="invite.busy"
            data-testid="groups-invite-regen"
            @click="regenInvite(invite.groupId)"
          >
            重新生成
          </el-button>
          <el-button
            size="small"
            type="danger"
            plain
            :loading="invite.busy"
            data-testid="groups-invite-revoke"
            @click="revokeInvite(invite.groupId)"
          >
            撤销
          </el-button>
        </div>
      </div>
      <el-empty
        v-else
        description="当前无生效邀请码"
        :image-size="60"
      />
    </el-dialog>

    <!-- 成员管理（所有者；成员=可见设备同组过滤） -->
    <el-dialog
      v-model="membersVisible"
      :title="`成员管理 · ${members.groupName}`"
      width="520px"
      data-testid="groups-members-dialog"
    >
      <el-table
        :data="members.items"
        data-testid="groups-members-table"
        size="small"
        empty-text="暂无其他成员（同组可见设备将在此列出）"
      >
        <el-table-column
          label="设备"
          min-width="150"
        >
          <template #default="{ row }">
            <span
              class="dot"
              :class="row.online ? 'on' : 'off'"
            />
            {{ row.deviceName }}
          </template>
        </el-table-column>
        <el-table-column
          prop="remoteCode"
          label="远程码"
          width="120"
        />
        <el-table-column
          label="操作"
          width="100"
        >
          <template #default="{ row }">
            <el-button
              size="small"
              type="danger"
              plain
              :disabled="isPassive"
              data-testid="groups-kick"
              @click="kick(row.deviceId, row.deviceName)"
            >
              移出
            </el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-dialog>
  </section>
</template>

<style scoped>
.toolbar { display: flex; align-items: center; gap: 12px; margin-bottom: 12px; }
.toolbar h2 { margin: 0; font-size: 16px; }
.requests { margin-top: 16px; }
.muted { color: var(--el-text-color-secondary); font-size: 12px; }
.row { display: flex; gap: 8px; margin-top: 8px; }
.dot {
  display: inline-block;
  width: 8px;
  height: 8px;
  border-radius: 50%;
  margin-right: 6px;
}
.dot.on { background: var(--el-color-success); }
.dot.off { background: var(--el-text-color-disabled); }
</style>

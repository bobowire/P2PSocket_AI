<script setup lang="ts">
// M2-28 分组全量页（06 §2、FR-C-805，替换 M1-33 占位）：已加入列表（策略/成员数[所有者行附待审计数]/角色）+
// 凭码入组（0x51；approval 组 3002 → 待审批提示）+ 新建（准入策略单选，0x50）+
// 所有者操作：审批弹窗（0x53 List + 批量 Approve/Reject，10/页分页——2026-10-19 用户需求，
// 取代原顶部聚合审批卡）、邀请码（0x54 覆盖式/撤销）、
// 成员管理（0x57 移出——成员取自可见设备同组过滤，0x40 维度）、编辑（0x55）、解散（0x56）；
// 成员自退（0x52）。分组族全属主动类——passive 置灰 + 横幅（05 §8）。
// 成员资格变更 → WS device_list → devices refetch（TD-16；服务端 0x41 推送链 M2-10）。
import { computed, onMounted, reactive, ref } from "vue";
import { ElMessage, ElMessageBox } from "element-plus";
import type { FormInstance, FormRules } from "element-plus";
import { ApiError, CodeText, dialogWidth, useBreakpoint, type GroupRequestView } from "@p2p/ui-shared";
import PassiveBanner from "../components/PassiveBanner.vue";
import { INVITE_CODE_MESSAGE, INVITE_CODE_PATTERN, normalizeInviteCode } from "./inviteCodeRule";
import { useGroupStore, type GroupFormInput } from "../stores/groups";
import { useDeviceStore } from "../stores/devices";
import { useSystemStore } from "../stores/system";

const groups = useGroupStore();
const devices = useDeviceStore();
const system = useSystemStore();

const isPassive = computed(() => system.device?.capability === "passive");
const { isMobile } = useBreakpoint(); // 2026-11-21：小屏卡片列表（表格 860px 定宽手机横向溢出，对齐 Devices 模式）

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
    // 服务端生成口径=小写去混淆（InviteCharset）；大写抄写放行、提交归一
    { pattern: INVITE_CODE_PATTERN, message: INVITE_CODE_MESSAGE, trigger: "blur" },
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
    await groups.join(normalizeInviteCode(joinForm.inviteCode));
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

// ── 审批（0x53 List + 批量 Approve/Reject，仅所有者；FR-S-305 客户端载体）──
// 待审数据源=GET /api/groups 聚合 requests（自有组 0x53 List），成员数列同时按组计待审。
const approveVisible = ref(false);
const approve = reactive({ groupId: "", groupName: "", page: 1 });
const approveSelection = ref<GroupRequestView[]>([]);
const approveBusy = ref(false);
const ApprovePageSize = 10;

const approveItems = computed(() => groups.requests.filter((r) => r.groupId === approve.groupId));
const approvePaged = computed(() =>
  approveItems.value.slice((approve.page - 1) * ApprovePageSize, approve.page * ApprovePageSize));

function pendingOf(groupId: string): number {
  return groups.requests.filter((r) => r.groupId === groupId).length;
}

function openApprove(id: string) {
  const g = groups.items.find((x) => x.groupId === id);
  if (!g) return;
  approve.groupId = id;
  approve.groupName = g.groupName;
  approve.page = 1;
  approveSelection.value = [];
  approveVisible.value = true;
}

function onSelectionChange(rows: GroupRequestView[]) {
  approveSelection.value = rows;
}

/** 翻页清空勾选（勾选仅当页有效——分页切片换数据，旧勾选行不再渲染）。 */
function onApprovePageChange() {
  approveSelection.value = [];
}

/** 批量决议：逐条 0x53 Approve/Reject（每条独立 Ack；单条失败不中断后续），store 逐条刷新聚合。 */
async function decideBatch(approveThem: boolean) {
  if (approveSelection.value.length === 0 || approveBusy.value) return;
  approveBusy.value = true;
  let okCount = 0;
  let failCount = 0;
  for (const r of approveSelection.value) {
    try {
      await (approveThem ? groups.approve(r.requestId) : groups.reject(r.requestId));
      okCount++;
    } catch {
      failCount++; // 已被处理/网络失败：如实计数，继续后续条目
    }
  }
  approveBusy.value = false;
  approveSelection.value = [];
  approve.page = 1;
  if (okCount > 0)
    ElMessage.success(
      `${approveThem ? "已批准" : "已拒绝"} ${okCount} 条${failCount > 0 ? `（${failCount} 条失败）` : ""}`);
  else
    ElMessage.error(`操作失败 ${failCount} 条`);
  if (pendingOf(approve.groupId) === 0) approveVisible.value = false; // 该组待审清空→关弹窗
}

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
      v-if="!isMobile"
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
        label="成员数"
        width="140"
      >
        <template #default="{ row }">
          <!-- 所有者行附本组待审计数（聚合 requests 按组计）；非所有者仅成员数 -->
          {{ row.memberCount }}{{ row.isOwner ? `（待审：${pendingOf(row.groupId)}）` : "" }}
        </template>
      </el-table-column>
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
        v-if="!isMobile"
        label="操作"
        width="350"
        fixed="right"
      >
        <template #default="{ row }">
          <template v-if="row.isOwner">
            <el-button
              size="small"
              :disabled="isPassive"
              data-testid="groups-approve-entry"
              @click="openApprove(row.groupId)"
            >
              审批
            </el-button>
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

    <!-- 2026-11-21 小屏卡片列表（表格五列合计 860px 手机必横向溢出；行数据纵排+操作保留） -->
    <div
      v-else
      class="card-list"
      data-testid="groups-cards"
    >
      <el-card
        v-for="row in groups.items"
        :key="row.groupId"
        class="row-card"
        shadow="never"
      >
        <div class="card-head">
          <span class="card-title">{{ row.groupName }}</span>
          <el-tag
            :type="row.isOwner ? 'primary' : 'info'"
            size="small"
          >
            {{ row.isOwner ? "所有者" : "成员" }}
          </el-tag>
        </div>
        <div class="card-line">
          <span class="k">准入策略</span>
          <el-tag
            :type="row.policy === 'approval' ? 'warning' : 'success'"
            size="small"
          >
            {{ row.policy === "approval" ? "需审批" : "自由加入" }}
          </el-tag>
        </div>
        <div class="card-line">
          <span class="k">成员数</span>
          <span>{{ row.memberCount }}{{ row.isOwner ? `（待审：${pendingOf(row.groupId)}）` : "" }}</span>
        </div>
        <div class="card-actions">
          <template v-if="row.isOwner">
            <el-button
              size="small"
              :disabled="isPassive"
              data-testid="groups-approve-entry"
              @click="openApprove(row.groupId)"
            >
              审批
            </el-button>
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
        </div>
      </el-card>
      <el-empty
        v-if="groups.items.length === 0"
        description="暂未加入任何分组（凭码入组或新建）"
      />
    </div>

    <!-- 凭码入组 -->
    <el-dialog
      v-model="joinVisible"
      title="凭码入组"
      :width="dialogWidth('380px')"
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
          自由加入的分组即时入组；需审批的分组将提交申请，等待所有者处理
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
      :width="dialogWidth('380px')"
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
      :width="dialogWidth('380px')"
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
      :width="dialogWidth('400px')"
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

    <!-- 审批（所有者；0x53 List + 批量 Approve/Reject，10/页） -->
    <el-dialog
      v-model="approveVisible"
      :title="`审批 · ${approve.groupName}`"
      :width="dialogWidth('560px')"
      data-testid="groups-approve-dialog"
    >
      <template v-if="approveItems.length > 0">
        <el-table
          :data="approvePaged"
          size="small"
          data-testid="groups-approve-list"
          @selection-change="onSelectionChange"
        >
          <el-table-column
            type="selection"
            width="42"
          />
          <el-table-column
            prop="deviceName"
            label="申请设备"
            min-width="150"
          />
          <el-table-column
            label="申请时间"
            width="180"
          >
            <template #default="{ row }">
              {{ fmtTime(row.createdAtMs) }}
            </template>
          </el-table-column>
        </el-table>
        <div class="pager">
          <el-pagination
            v-model:current-page="approve.page"
            layout="total, prev, pager, next"
            :total="approveItems.length"
            :page-size="ApprovePageSize"
            data-testid="groups-approve-pager"
            @current-change="onApprovePageChange"
          />
        </div>
        <div class="row">
          <el-button
            type="success"
            :disabled="approveSelection.length === 0 || approveBusy"
            :loading="approveBusy"
            data-testid="groups-batch-approve"
            @click="decideBatch(true)"
          >
            批量同意（{{ approveSelection.length }}）
          </el-button>
          <el-button
            type="danger"
            plain
            :disabled="approveSelection.length === 0 || approveBusy"
            :loading="approveBusy"
            data-testid="groups-batch-reject"
            @click="decideBatch(false)"
          >
            批量拒绝（{{ approveSelection.length }}）
          </el-button>
        </div>
      </template>
      <el-empty
        v-else
        description="暂无待审批申请"
        :image-size="60"
      />
    </el-dialog>

    <!-- 成员管理（所有者；成员=可见设备同组过滤） -->
    <el-dialog
      v-model="membersVisible"
      :title="`成员管理 · ${members.groupName}`"
      :width="dialogWidth('520px')"
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
              :class="{ on: row.online }"
              role="img"
              :aria-label="row.online ? '在线' : '离线'"
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
/* .toolbar / .muted / .dot 上收 ui-shared/styles/shared.css（2026-10-20 评估批次 B） */
.row { display: flex; gap: 8px; margin-top: 8px; }
.pager { display: flex; justify-content: flex-end; margin-top: 8px; }

/* ── 2026-11-21 小屏卡片列表（对齐 Devices 模式）── */
.card-list { display: flex; flex-direction: column; gap: 12px; }
.row-card :deep(.el-card__body) { padding: 12px 14px; }
.card-head { display: flex; align-items: center; justify-content: space-between; margin-bottom: 8px; }
.card-title { font-weight: 600; font-size: 15px; word-break: break-all; }
.card-line { display: flex; gap: 10px; align-items: center; font-size: 13px; line-height: 2; word-break: break-all; }
.card-line .k { flex-shrink: 0; color: var(--el-text-color-secondary); }
.card-actions { display: flex; flex-wrap: wrap; gap: 4px; margin-top: 10px; }
</style>

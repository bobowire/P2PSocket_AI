<script setup lang="ts">
// M3-10 用户管理页（06 §3、FR-S-820）：列表+禁用/启用 confirm+重置密码（临时密码一次性展示）+
// 注册开关（registration_open 键经 system/config，FR-S-201）。禁用=AdminService 进程内直调：
// 即时降级哑节点+0x75（M3-03）。内置管理员禁用/重置按钮禁用（服务端 1003 前置 UX）。
import { onMounted } from "vue";
import { ElMessage, ElMessageBox } from "element-plus";
import type { UserView } from "@p2p/ui-shared";
import { USER_PAGE_SIZE, useUsersStore } from "../stores/users";

const users = useUsersStore();

function fmtTime(iso: string): string {
  return iso.replace("T", " ").replace(/(\.\d+|Z).*$/, "");
}

async function toggleDisabled(row: UserView) {
  if (row.disabled) {
    await users.enable(row.id);
    ElMessage.success(`已启用 ${row.username}`);
    return;
  }
  await ElMessageBox.confirm(
    `确定禁用用户"${row.username}"？其名下在线设备将即时降级为哑节点（passive）。`,
    "禁用确认",
    { type: "warning" },
  );
  await users.disable(row.id);
  ElMessage.success(`已禁用 ${row.username}（恢复须设备侧重连登录）`);
}

async function resetPassword(row: UserView) {
  await ElMessageBox.confirm(
    `确定重置用户"${row.username}"的密码？将生成临时密码（仅显示一次）。`,
    "重置密码",
    { type: "warning" },
  );
  const temp = await users.resetPassword(row.id);
  // 临时密码唯一明文出口（M3-03）：alert 一次性展示，关闭后不可再取
  await ElMessageBox.alert(temp, `${row.username} 的临时密码（仅显示一次）`, {
    confirmButtonText: "我已保存",
  });
}

async function onRegistrationChange(open: boolean) {
  try {
    await users.setRegistration(open);
    ElMessage.success(open ? "已开放注册" : "已关闭注册");
  } catch {
    await users.loadRegistration(); // 失败回读开关现值（防 UI 与服务端漂移）
  }
}

onMounted(() => {
  void users.refresh();
  void users.loadRegistration();
});
</script>

<template>
  <main class="page">
    <h2>用户</h2>

    <el-card
      class="reg-card"
      data-testid="users-registration"
    >
      <template #header>
        开放注册
      </template>
      <!-- null=首载窗口不渲染开关：ElSwitch 对非法 modelValue 会主动 emit change(false) 自纠偏，
           若 null 期挂载会误触 PUT registration_open=0（静默关注册）。加载定值后再渲染。 -->
      <el-switch
        v-if="users.registrationOpen !== null"
        :model-value="users.registrationOpen"
        data-testid="users-registration-switch"
        @change="onRegistrationChange"
      />
      <span
        v-else
        class="reg-hint"
      >加载中…</span>
      <span
        v-if="users.registrationOpen !== null"
        class="reg-hint"
      >
        {{ users.registrationOpen ? "允许新用户注册（0x20）" : "新用户注册被拒绝" }}
      </span>
    </el-card>

    <el-table
      :data="users.list?.items ?? []"
      data-testid="users-table"
    >
      <el-table-column
        prop="username"
        label="用户名"
        min-width="140"
      />
      <el-table-column
        label="状态"
        width="90"
      >
        <template #default="{ row }">
          <el-tag :type="row.disabled ? 'danger' : 'success'">
            {{ row.disabled ? "已禁用" : "正常" }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column
        label="角色"
        width="100"
      >
        <template #default="{ row }">
          <el-tag
            v-if="row.isAdmin"
            type="warning"
          >
            管理员
          </el-tag>
          <span v-else>—</span>
        </template>
      </el-table-column>
      <el-table-column
        prop="deviceCount"
        label="设备数"
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
      <el-table-column
        label="操作"
        width="200"
      >
        <template #default="{ row }">
          <el-button
            size="small"
            :type="row.disabled ? 'success' : 'danger'"
            plain
            :disabled="row.isAdmin"
            :data-testid="`users-toggle-${row.username}`"
            @click="toggleDisabled(row)"
          >
            {{ row.disabled ? "启用" : "禁用" }}
          </el-button>
          <el-button
            size="small"
            plain
            :disabled="row.isAdmin"
            :data-testid="`users-reset-${row.username}`"
            @click="resetPassword(row)"
          >
            重置密码
          </el-button>
        </template>
      </el-table-column>
    </el-table>

    <el-pagination
      layout="total, prev, pager, next"
      :total="users.list?.total ?? 0"
      :page-size="USER_PAGE_SIZE"
      :current-page="users.page"
      data-testid="users-pagination"
      @current-change="(p: number) => void users.setPage(p)"
    />
  </main>
</template>

<style scoped>
.page {
  padding: 24px;
}
.reg-card {
  margin-bottom: 16px;
}
.reg-hint {
  margin-left: 12px;
  font-size: 13px;
  color: var(--el-text-color-secondary);
}
.el-pagination {
  margin-top: 16px;
  justify-content: flex-end;
}
</style>

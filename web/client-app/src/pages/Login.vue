<script setup lang="ts">
// M1-32 账号页（06 §2、FR-C-802）：登录/注册表单；已登录态展示账号与登出。
// M2-29 修改密码（0x23）：已登录态弹窗改密，成功后主动登出引导重新登录（清单 M2-29 UX 口径；
// 协议侧 0x23 成功不裁会话——04 §2.3）；passive 会话登录 → 本地拒发 2002（02 §2.5）。
import { computed, onMounted, reactive, ref } from "vue";
import { ElMessage } from "element-plus";
import type { FormInstance, FormRules } from "element-plus";
import { ApiError, ApiPaths } from "@p2p/ui-shared";
import { api } from "../api";
import { useAuthStore } from "../stores/auth";
import { useSystemStore } from "../stores/system";

const auth = useAuthStore();
const system = useSystemStore();
const tab = ref<"login" | "register">("login");
const form = reactive({ username: "", password: "" });
const busy = ref(false);
const errorText = ref("");
const formRef = ref<FormInstance>();

const rules: FormRules = {
  username: [
    { required: true, message: "请输入用户名", trigger: "blur" },
    { min: 3, message: "用户名至少 3 个字符", trigger: "blur" },
  ],
  password: [
    { required: true, message: "请输入密码", trigger: "blur" },
    { min: 6, message: "密码至少 6 位", trigger: "blur" },
  ],
};

const loggedIn = computed(() => !!auth.me?.username);
const isPassive = computed(() => auth.me?.mode === "passive" || system.device?.capability === "passive");

async function submit() {
  if (!(await formRef.value?.validate().catch(() => false))) return;
  busy.value = true;
  errorText.value = "";
  try {
    if (tab.value === "login") await auth.login(form.username, form.password);
    else {
      await auth.register(form.username, form.password);
      ElMessage.success("注册成功，已自动登录");
      await auth.login(form.username, form.password);
    }
    form.password = "";
  } catch (e) {
    errorText.value = e instanceof ApiError ? e.message : "请求失败";
  } finally {
    busy.value = false;
  }
}

async function logout() {
  await auth.logout().catch(() => undefined);
  void system.refresh(); // capability → passive
}

// ── 修改密码（0x23，M2-29）───────────────────────────────────────
const changeVisible = ref(false);
const changeRef = ref<FormInstance>();
const changeForm = reactive({ oldPassword: "", newPassword: "", confirm: "" });
const changeBusy = ref(false);
const changeRules: FormRules = {
  oldPassword: [{ required: true, message: "请输入当前密码", trigger: "blur" }],
  newPassword: [
    { required: true, message: "请输入新密码", trigger: "blur" },
    { min: 6, message: "新密码至少 6 位", trigger: "blur" },
  ],
  confirm: [
    {
      validator: (_r, v: string, cb: (e?: Error) => void) =>
        v === changeForm.newPassword ? cb() : cb(new Error("两次输入的新密码不一致")),
      trigger: "blur",
    },
  ],
};

function openChange() {
  Object.assign(changeForm, { oldPassword: "", newPassword: "", confirm: "" });
  changeVisible.value = true;
}

async function submitChange() {
  if (!(await changeRef.value?.validate().catch(() => false))) return;
  changeBusy.value = true;
  try {
    await api.post(ApiPaths.AuthChangePassword, {
      oldPassword: changeForm.oldPassword,
      newPassword: changeForm.newPassword,
    });
    changeVisible.value = false;
    ElMessage.success("密码已修改，请用新密码重新登录");
    await auth.logout(); // 引导重新登录（清单 M2-29）；0x23 协议侧不裁会话
    void system.refresh();
  } catch (e) {
    if (e instanceof ApiError && e.code === 2001) ElMessage.error("当前密码不正确");
    else ElMessage.error(e instanceof ApiError ? e.message : "请求失败");
  } finally {
    changeBusy.value = false;
  }
}

onMounted(() => void auth.refresh());
</script>

<template>
  <section class="login-page">
    <el-card
      v-if="loggedIn"
      class="profile"
      data-testid="login-profile"
    >
      <template #header>
        账号
      </template>
      <p data-testid="login-username">
        已登录：<b>{{ auth.me?.username }}</b>
        <el-tag
          :type="auth.me?.mode === 'normal' ? 'success' : 'info'"
          size="small"
        >
          {{ auth.me?.mode === "normal" ? "主动模式" : "哑节点" }}
        </el-tag>
      </p>
      <p class="muted">
        登出后本机转为哑节点：仅响应既有隧道，不能主动发起映射（FR-C-603 不重启）
      </p>
      <div class="row">
        <el-button
          type="danger"
          plain
          data-testid="login-logout"
          @click="logout"
        >
          登出
        </el-button>
        <el-button
          data-testid="login-change-password"
          @click="openChange"
        >
          修改密码
        </el-button>
      </div>
    </el-card>

    <el-card
      v-else
      class="auth"
      data-testid="login-form"
    >
      <template #header>
        账号登录 / 注册
      </template>
      <el-tabs v-model="tab">
        <el-tab-pane
          label="登录"
          name="login"
        />
        <el-tab-pane
          label="注册"
          name="register"
        />
      </el-tabs>
      <p
        v-if="isPassive"
        class="passive-hint"
        data-testid="login-passive-hint"
      >
        当前为哑节点会话：登录须重建控制通道——请等待自动重连或重启客户端后重试（02 §2.5）
      </p>
      <el-form
        ref="formRef"
        :model="form"
        :rules="rules"
        label-width="72px"
        @submit.prevent="submit"
      >
        <el-form-item
          label="用户名"
          prop="username"
        >
          <el-input v-model="form.username" />
        </el-form-item>
        <el-form-item
          label="密码"
          prop="password"
        >
          <el-input
            v-model="form.password"
            type="password"
            show-password
            data-testid="login-password"
          />
        </el-form-item>
        <el-form-item>
          <el-button
            type="primary"
            :loading="busy"
            native-type="submit"
            data-testid="login-submit"
            @click="submit"
          >
            {{ tab === "login" ? "登录" : "注册并登录" }}
          </el-button>
        </el-form-item>
      </el-form>
      <p
        v-if="errorText"
        class="error"
        data-testid="login-error"
      >
        {{ errorText }}
      </p>
    </el-card>

    <!-- 修改密码（0x23）：成功后主动登出，引导用新密码重新登录 -->
    <el-dialog
      v-model="changeVisible"
      title="修改密码"
      width="400px"
      data-testid="login-change-dialog"
    >
      <el-form
        ref="changeRef"
        :model="changeForm"
        :rules="changeRules"
        label-width="88px"
      >
        <el-form-item
          label="当前密码"
          prop="oldPassword"
        >
          <el-input
            v-model="changeForm.oldPassword"
            type="password"
            show-password
            data-testid="login-change-old"
          />
        </el-form-item>
        <el-form-item
          label="新密码"
          prop="newPassword"
        >
          <el-input
            v-model="changeForm.newPassword"
            type="password"
            show-password
            data-testid="login-change-new"
          />
        </el-form-item>
        <el-form-item
          label="确认新密码"
          prop="confirm"
        >
          <el-input
            v-model="changeForm.confirm"
            type="password"
            show-password
            data-testid="login-change-confirm"
          />
        </el-form-item>
        <el-form-item>
          <el-button
            type="primary"
            :loading="changeBusy"
            data-testid="login-change-save"
            @click="submitChange"
          >
            保存
          </el-button>
        </el-form-item>
      </el-form>
    </el-dialog>
  </section>
</template>

<style scoped>
.login-page { max-width: 480px; }
.muted { color: var(--el-text-color-secondary); font-size: 13px; }
.row { display: flex; gap: 12px; }
.passive-hint {
  font-size: 13px;
  color: var(--el-color-warning);
  border: 1px solid var(--el-color-warning-light-5);
  border-radius: var(--el-border-radius-base);
  padding: 8px;
}
.error { color: var(--el-color-danger); font-size: 13px; }
</style>

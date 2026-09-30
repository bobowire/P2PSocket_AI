<script setup lang="ts">
// M3-09 管理员登录页（06 §3、FR-S-203 收口面）：admin/admin 首登 mustChangePassword 提示条
// + 改密弹窗（成功继续——04 §3.1 改密不裁会话）；提示不强制阻断（可稍后在系统页处理……
// 实际改密入口仅此弹窗——取消提示则驻留提示条，再次进入 /login 仍会提示）。
import { onMounted, reactive, ref } from "vue";
import { useRoute, useRouter } from "vue-router";
import { ElMessage } from "element-plus";
import type { FormInstance, FormRules } from "element-plus";
import { ApiError } from "@p2p/ui-shared";
import { useAuthStore } from "../stores/auth";

const auth = useAuthStore();
const router = useRouter();
const route = useRoute();

const form = reactive({ username: "", password: "" });
const busy = ref(false);
const errorText = ref("");
const formRef = ref<FormInstance>();

const rules: FormRules = {
  username: [{ required: true, message: "请输入用户名", trigger: "blur" }],
  password: [{ required: true, message: "请输入密码", trigger: "blur" }],
};

/** 登录后目标（守卫拦截时的原目标，默认仪表盘）。 */
const redirect = () => (typeof route.query.redirect === "string" ? route.query.redirect : "/dashboard");

async function submit() {
  if (!(await formRef.value?.validate().catch(() => false))) return;
  busy.value = true;
  errorText.value = "";
  try {
    await auth.login(form.username, form.password);
    form.password = "";
    if (auth.mustChange) changeVisible.value = true; // 首登：提示条 + 改密弹窗（成功继续）
    else void router.replace(redirect());
  } catch (e) {
    if (e instanceof ApiError && (e.code === -401 || e.code === 2001))
      errorText.value = "用户名或密码错误";
    else errorText.value = e instanceof ApiError ? e.message : "请求失败";
  } finally {
    busy.value = false;
  }
}

// ── 修改密码（04 §3.1：成功不裁会话——mustChange 归零继续进后台）──────────────
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

async function submitChange() {
  if (!(await changeRef.value?.validate().catch(() => false))) return;
  changeBusy.value = true;
  try {
    await auth.changePassword(changeForm.oldPassword, changeForm.newPassword);
    changeVisible.value = false;
    ElMessage.success("密码已修改");
    void router.replace(redirect()); // 会话保持（04 §3.1）——继续进后台
  } catch (e) {
    if (e instanceof ApiError && (e.code === -401 || e.code === 2001))
      ElMessage.error("当前密码不正确");
    else ElMessage.error(e instanceof ApiError ? e.message : "请求失败");
  } finally {
    changeBusy.value = false;
  }
}

onMounted(() => {
  // 刷新后已登录（非首登态）访问 /login → 直达后台；首登提示态留在本页完成改密
  if (auth.authed && !auth.mustChange) void router.replace(redirect());
});
</script>

<template>
  <main class="login-page">
    <el-card
      class="auth"
      data-testid="login-form"
    >
      <template #header>
        P2P 管理后台登录
      </template>
      <el-alert
        v-if="auth.mustChange"
        class="must-change"
        type="warning"
        :closable="false"
        data-testid="login-must-change"
        title="正在使用默认口令（admin/admin），请立即修改管理员密码"
      />
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
          <el-input
            v-model="form.username"
            data-testid="login-username"
          />
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
          >
            登录
          </el-button>
          <el-button
            v-if="auth.mustChange"
            data-testid="login-change-open"
            @click="changeVisible = true"
          >
            修改密码
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

    <el-dialog
      v-model="changeVisible"
      title="修改管理员密码"
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
            保存并继续
          </el-button>
        </el-form-item>
      </el-form>
    </el-dialog>
  </main>
</template>

<style scoped>
.login-page {
  min-height: 100vh;
  display: flex;
  align-items: center;
  justify-content: center;
  background: var(--el-fill-color-lighter);
}
.auth { width: 400px; }
.must-change { margin-bottom: 12px; }
.error { color: var(--el-color-danger); font-size: 13px; }
</style>

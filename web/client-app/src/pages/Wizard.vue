<script setup lang="ts">
// M1-32 首启向导（06 §2、FR-C-101/102/103）：三步——
// ① 服务端地址（连通性测试即时反馈）→ ② 分组方式（default/invite/account）→ ③ 确认注册；
// 完成后展示远程码/虚拟 IP（06 §2），点"进入控制台"回主界面。
import { computed, onMounted, ref } from "vue";
import { useRouter } from "vue-router";
import { ElMessage } from "element-plus";
import type { FormInstance, FormRules } from "element-plus";
import { CodeText, ApiPaths, ApiError } from "@p2p/ui-shared";
import { api } from "../api";
import { useSystemStore } from "../stores/system";
import { useSettingsStore } from "../stores/settings";
import type { RegistrationResult } from "@p2p/ui-shared";

type WizardMode = "default" | "invite" | "account";

const system = useSystemStore();
const settings = useSettingsStore();
const router = useRouter();

const step = ref(0);
const serverAddr = ref("");
const testing = ref(false);
const testResult = ref<{ ok: boolean; detail: string } | null>(null);
const mode = ref<WizardMode>("default");
const inviteCode = ref("");
const username = ref("");
const password = ref("");
const deviceName = ref("我的设备");
const submitting = ref(false);
const result = ref<RegistrationResult | null>(null);

const MODES: { value: WizardMode; label: string; hint: string }[] = [
  { value: "default", label: "默认分组", hint: "加入服务端默认分组（与同分组设备互通）" },
  { value: "invite", label: "邀请码入组", hint: "输入 6 位分组邀请码，入组后与组内设备互通" },
  { value: "account", label: "账号注册建组", hint: "新建账号并创建专属分组（我的分组）" },
];

const addrForm = ref<FormInstance>();
const groupForm = ref<FormInstance>();

const addrRules: FormRules = {
  serverAddr: [
    { required: true, message: "请输入服务端地址", trigger: "blur" },
    {
      pattern: /^[\w.-]+:\d{1,5}$/,
      message: "格式：host:port（如 203.0.113.10:7000）",
      trigger: "blur",
    },
  ],
};
const groupRules: FormRules = {
  inviteCode: [
    { required: true, message: "请输入邀请码", trigger: "blur" },
    { len: 6, message: "邀请码为 6 位", trigger: "blur" },
  ],
  username: [
    { required: true, message: "请输入用户名", trigger: "blur" },
    { min: 3, message: "用户名至少 3 个字符", trigger: "blur" },
  ],
  password: [
    { required: true, message: "请输入密码", trigger: "blur" },
    { min: 6, message: "密码至少 6 位", trigger: "blur" },
  ],
  deviceName: [{ required: true, message: "请输入设备名", trigger: "blur" }],
};

const groupError = ref("");

async function testServer() {
  if (!(await addrForm.value?.validate().catch(() => false))) return;
  testing.value = true;
  testResult.value = null;
  try {
    testResult.value = await api.post<{ ok: boolean; detail: string }>(
      ApiPaths.WizardServerTest,
      { serverAddr: serverAddr.value },
    );
  } catch (e) {
    testResult.value = { ok: false, detail: e instanceof ApiError ? e.message : "测试请求失败" };
  } finally {
    testing.value = false;
  }
}

const canNext = computed(() => {
  if (step.value === 0) return !!testResult.value?.ok;
  return true;
});

function next() {
  if (step.value === 0 && !canNext.value) {
    ElMessage.warning("请先完成连通性测试");
    return;
  }
  step.value += 1;
}

async function submit() {
  if (!(await groupForm.value?.validate().catch(() => false))) return;
  submitting.value = true;
  groupError.value = "";
  try {
    result.value = await api.post<RegistrationResult>(ApiPaths.WizardRegister, {
      serverAddr: serverAddr.value,
      mode: mode.value,
      inviteCode: mode.value === "invite" ? inviteCode.value : undefined,
      username: mode.value === "account" ? username.value : undefined,
      password: mode.value === "account" ? password.value : undefined,
      deviceName: deviceName.value || undefined,
    });
    step.value = 3;
    void system.refresh(); // phase → running（App 守卫自此放行全站）
  } catch (e) {
    groupError.value = e instanceof ApiError ? e.message : "注册失败，请重试";
  } finally {
    submitting.value = false;
  }
}

function enterConsole() {
  void router.replace("/");
}

onMounted(async () => {
  // 已注册直达主界面（06 §2：/wizard 仅未注册可见；守卫反向出口）
  await system.refresh();
  if (system.state && system.state.phase !== "unregistered") {
    void router.replace("/");
    return;
  }
  await settings.refresh();
  // 预填既有 serverAddrs 首选（换址重装/半途退出场景）
  if (settings.settings?.serverAddrs?.length) serverAddr.value = settings.settings.serverAddrs[0];
});
</script>

<template>
  <section class="wizard">
    <h2>首启向导</h2>
    <el-steps
      :active="step"
      finish-status="success"
      data-testid="wizard-steps"
    >
      <el-step title="服务端" />
      <el-step title="分组方式" />
      <el-step title="完成" />
    </el-steps>

    <!-- ① 服务端地址 + 连通性测试 -->
    <div
      v-if="step === 0"
      class="panel"
    >
      <el-form
        ref="addrForm"
        :model="{ serverAddr }"
        :rules="addrRules"
        label-width="100px"
      >
        <el-form-item
          label="服务端地址"
          prop="serverAddr"
        >
          <el-input
            v-model="serverAddr"
            placeholder="host:port，如 203.0.113.10:7000"
            data-testid="wizard-addr"
          />
        </el-form-item>
        <el-form-item>
          <el-button
            :loading="testing"
            data-testid="wizard-test"
            @click="testServer"
          >
            测试连接
          </el-button>
          <span
            v-if="testResult"
            class="test-result"
            :class="testResult.ok ? 'ok' : 'bad'"
            data-testid="wizard-test-result"
          >{{ testResult.ok ? "连接成功" : `连接失败：${testResult.detail}` }}</span>
        </el-form-item>
      </el-form>
      <div class="actions">
        <el-button
          type="primary"
          :disabled="!canNext"
          data-testid="wizard-next"
          @click="next"
        >
          下一步
        </el-button>
      </div>
    </div>

    <!-- ② 分组方式 -->
    <div
      v-if="step === 1"
      class="panel"
    >
      <el-form
        ref="groupForm"
        :model="{ inviteCode, username, password, deviceName }"
        :rules="groupRules"
        label-width="100px"
      >
        <el-form-item label="分组方式">
          <el-radio-group
            v-model="mode"
            data-testid="wizard-mode"
          >
            <el-radio
              v-for="m in MODES"
              :key="m.value"
              :value="m.value"
            >
              {{ m.label }}
            </el-radio>
          </el-radio-group>
          <div class="mode-hint">
            {{ MODES.find((m) => m.value === mode)?.hint }}
          </div>
        </el-form-item>
        <el-form-item
          v-if="mode === 'invite'"
          label="邀请码"
          prop="inviteCode"
        >
          <el-input
            v-model="inviteCode"
            maxlength="6"
            placeholder="6 位邀请码"
          />
        </el-form-item>
        <template v-if="mode === 'account'">
          <el-form-item
            label="用户名"
            prop="username"
          >
            <el-input v-model="username" />
          </el-form-item>
          <el-form-item
            label="密码"
            prop="password"
          >
            <el-input
              v-model="password"
              type="password"
              show-password
            />
          </el-form-item>
        </template>
        <el-form-item
          label="设备名"
          prop="deviceName"
        >
          <el-input v-model="deviceName" />
        </el-form-item>
      </el-form>
      <div class="actions">
        <el-button @click="step = 0">
          上一步
        </el-button>
        <el-button
          type="primary"
          :loading="submitting"
          data-testid="wizard-register"
          @click="submit"
        >
          注册
        </el-button>
        <div
          v-if="groupError"
          class="error"
          data-testid="wizard-error"
        >
          {{ groupError }}
        </div>
      </div>
    </div>

    <!-- ③ 完成：三要素展示（06 §2） -->
    <div
      v-if="step === 3 && result"
      class="panel done"
    >
      <el-result
        icon="success"
        title="注册完成"
        sub-title="以下为这台设备的接入要素，请妥善保管"
      />
      <dl class="facts">
        <div>
          <dt>远程码</dt>
          <dd>
            <CodeText
              :value="result.remoteCode"
              label="远程码"
            />
          </dd>
        </div>
        <div>
          <dt>虚拟 IP</dt>
          <dd>{{ result.virtualIp }}</dd>
        </div>
        <div>
          <dt>分组</dt>
          <dd>{{ result.groups.map((g) => g.groupName).join("、") || "无" }}</dd>
        </div>
      </dl>
      <el-button
        type="primary"
        data-testid="wizard-enter"
        @click="enterConsole"
      >
        进入控制台
      </el-button>
    </div>
  </section>
</template>

<style scoped>
.wizard { max-width: 640px; }
.panel { margin-top: 24px; }
.actions { display: flex; align-items: center; gap: 12px; margin-left: 100px; }
.test-result { margin-left: 12px; font-size: 13px; }
.test-result.ok { color: var(--el-color-success); }
.test-result.bad { color: var(--el-color-danger); }
.mode-hint { font-size: 12px; color: var(--el-text-color-secondary); width: 100%; }
.error { color: var(--el-color-danger); font-size: 13px; }
.done .facts {
  display: grid;
  gap: 12px;
  margin: 0 0 24px;
  padding: 0 48px;
}
.facts dt { font-size: 13px; color: var(--el-text-color-secondary); }
.facts dd { margin: 4px 0 0; }
</style>

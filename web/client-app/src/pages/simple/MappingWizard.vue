<script setup lang="ts">
// 简易版·引导式新建映射（FR-C-811）：4 步 el-steps——选设备 → 选服务 → 本地端口/名称
// → 确认启用。设备下拉 filterable（显示「设备名（远程码）」，离线设备禁选并标「离线」）；
// 协议+目标端口配快捷 chips（80 网页/22 SSH/3389 远程桌面/53 DNS→UDP）；本地端口默认
// 镜像目标端口、名称默认「设备名-端口」（均可改，改后不再跟随）；目标地址默认 self，
// 高级折叠内切 IP（网段提示与高级版同源）。
// 「完成并启用」= create 后立即 enable（用户批复口径，与高级版"创建默认停用"分流）；
// create/enable 失败均在确认步内联呈现（enable 失败不回滚——映射已建为停用态，可列表启用）。
import { computed, reactive, ref, watch } from "vue";
import { ElMessage } from "element-plus";
import { ApiError, dialogWidth } from "@p2p/ui-shared";
import { useDeviceStore } from "../../stores/devices";
import { useSystemStore } from "../../stores/system";
import { useMappingStore, type MappingFormInput } from "../../stores/mappings";
import { isValidIp } from "../mappingFormRules";

const props = defineProps<{ modelValue: boolean }>();
const emit = defineEmits<{ (e: "update:modelValue", v: boolean): void }>();

const devices = useDeviceStore();
const system = useSystemStore();
const mappings = useMappingStore();

/** 快捷服务（53 DNS 为 UDP，其余 TCP）。 */
const QUICK = [
  { port: 80, proto: "tcp", label: "80 网页" },
  { port: 22, proto: "tcp", label: "22 SSH" },
  { port: 3389, proto: "tcp", label: "3389 远程桌面" },
  { port: 53, proto: "udp", label: "53 DNS" },
];

const step = ref(0);
const busy = ref(false);
const errorText = ref("");
const form = reactive({
  targetRemoteCode: "",
  proto: "tcp",
  targetPort: 80,
  targetMode: "self" as "self" | "ip",
  targetAddr: "",
  localPort: 80,
  name: "",
});
/** 名称/本地端口"用户改过"标记——未改时随上游联动（设备/目标端口变化自动重算）。 */
const nameTouched = ref(false);
const localTouched = ref(false);

const open = computed({
  get: () => props.modelValue,
  set: (v: boolean) => emit("update:modelValue", v),
});

/** 目标设备现值（远程码匹配；离线项禁选但选后仍可提示）。 */
const targetDevice = computed(() =>
  devices.items.find((d) => d.remoteCode === form.targetRemoteCode));

/** 名称默认「设备名-端口」（FR-C-811：可改，改后不跟随）。 */
const autoName = computed(() => {
  const dn = targetDevice.value?.deviceName ?? form.targetRemoteCode;
  return dn ? `${dn}-${form.targetPort}` : "";
});

watch(
  () => form.targetPort,
  (p) => {
    if (!localTouched.value) form.localPort = p;
  },
);
watch(autoName, (n) => {
  if (!nameTouched.value) form.name = n;
});

const step1Ok = computed(() => !!form.targetRemoteCode && (targetDevice.value?.online ?? false));
const step2Ok = computed(
  () =>
    Number.isInteger(form.targetPort) &&
    form.targetPort >= 1 &&
    form.targetPort <= 65535 &&
    (form.targetMode === "self" || isValidIp(form.targetAddr)),
);
const step3Ok = computed(
  () =>
    Number.isInteger(form.localPort) &&
    form.localPort >= 1 &&
    form.localPort <= 65535 &&
    !!form.name.trim() &&
    form.name.trim().length <= 50,
);
const nextDisabled = computed(() =>
  step.value === 0 ? !step1Ok.value : step.value === 1 ? !step2Ok.value : !step3Ok.value,
);

/** 连接地址预览（映射监听绑虚拟 IP——与高级版部署形态一致）。 */
const connectAddr = computed(
  () => `${system.device?.virtualIp ?? "127.0.0.1"}:${form.localPort}`);

function applyQuick(q: (typeof QUICK)[number]) {
  form.targetPort = q.port;
  form.proto = q.proto;
}

function reset() {
  step.value = 0;
  busy.value = false;
  errorText.value = "";
  nameTouched.value = false;
  localTouched.value = false;
  Object.assign(form, {
    targetRemoteCode: "",
    proto: "tcp",
    targetPort: 80,
    targetMode: "self",
    targetAddr: "",
    localPort: 80,
    name: "",
  });
}

watch(
  open,
  (v) => {
    if (v) {
      reset();
      void devices.refresh(); // 开窗时刷新设备候选（离线态最新）
    }
  },
);

/** 完成并启用（FR-C-811 批复口径）：create → enable 两连调；enable 失败不回滚。 */
async function finish() {
  busy.value = true;
  errorText.value = "";
  const payload: MappingFormInput = {
    id: null,
    name: form.name.trim(),
    localPort: form.localPort,
    proto: form.proto,
    targetRemoteCode: form.targetRemoteCode,
    targetAddr: form.targetMode === "ip" ? form.targetAddr.trim() : "self",
    targetPort: form.targetPort,
  };
  try {
    const id = await mappings.create(payload);
    try {
      await mappings.enable(id);
      ElMessage.success("映射已创建并启用，正在打洞");
    } catch (e) {
      // 映射已建（停用态）：不回滚删除——用户可在列表手动启用或删除
      errorText.value = `已创建，但启用失败：${e instanceof ApiError ? e.message : "请求失败"}（关闭后可在列表中启用）`;
      return;
    }
    open.value = false;
  } catch (e) {
    // create 失败（如 1003 端口冲突）：留在确认步内联呈现
    errorText.value = e instanceof ApiError ? e.message : "请求失败";
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <el-dialog
    v-model="open"
    title="新建映射"
    :width="dialogWidth('560px')"
    :close-on-click-modal="false"
    data-testid="simple-wizard"
  >
    <el-steps
      :active="step"
      finish-status="success"
      simple
      data-testid="simple-wizard-steps"
    >
      <el-step title="选设备" />
      <el-step title="选服务" />
      <el-step title="本机端口" />
      <el-step title="确认" />
    </el-steps>

    <!-- 步 1：选设备（可搜索下拉；离线禁选） -->
    <div
      v-if="step === 0"
      class="step-body"
    >
      <p class="muted">
        选择要访问的目标设备（可输入名称或设备码搜索）
      </p>
      <el-select
        v-model="form.targetRemoteCode"
        filterable
        placeholder="搜索并选择设备"
        style="width: 100%"
        data-testid="simple-wizard-device"
      >
        <el-option
          v-for="d in devices.items"
          :key="d.deviceId"
          :value="d.remoteCode"
          :label="`${d.deviceName}（${d.remoteCode}）`"
          :disabled="!d.online"
        >
          <span>{{ d.deviceName }}（{{ d.remoteCode }}）</span>
          <el-tag
            v-if="!d.online"
            size="small"
            type="danger"
            class="off-tag"
          >
            离线
          </el-tag>
        </el-option>
      </el-select>
      <p
        v-if="targetDevice"
        class="muted meta"
      >
        虚拟 IP {{ targetDevice.virtualIp }}
        <template v-if="targetDevice.lanSegments.length">
          · 开放网段 {{ targetDevice.lanSegments.join("、") }}
        </template>
      </p>
    </div>

    <!-- 步 2：选服务（协议+目标端口+快捷；高级=目标地址） -->
    <div
      v-else-if="step === 1"
      class="step-body"
    >
      <p class="muted">
        要访问目标设备上的哪个服务？
      </p>
      <div class="field">
        <span class="field-label">协议</span>
        <el-radio-group
          v-model="form.proto"
          data-testid="simple-wizard-proto"
        >
          <el-radio value="tcp">
            TCP
          </el-radio>
          <el-radio value="udp">
            UDP
          </el-radio>
        </el-radio-group>
      </div>
      <div class="field">
        <span class="field-label">目标端口</span>
        <el-input-number
          v-model="form.targetPort"
          :min="1"
          :max="65535"
          data-testid="simple-wizard-targetport"
        />
      </div>
      <div class="chips">
        <el-button
          v-for="q in QUICK"
          :key="q.port"
          size="small"
          round
          :data-testid="`simple-wizard-quick-${q.port}`"
          @click="applyQuick(q)"
        >
          {{ q.label }}
        </el-button>
      </div>
      <el-collapse class="advanced">
        <el-collapse-item
          title="高级：目标地址（默认 self = 设备本机）"
          name="adv"
        >
          <el-radio-group
            v-model="form.targetMode"
            data-testid="simple-wizard-target-mode"
          >
            <el-radio value="self">
              self（设备本机）
            </el-radio>
            <el-radio value="ip">
              IP 地址
            </el-radio>
          </el-radio-group>
          <el-input
            v-if="form.targetMode === 'ip'"
            v-model="form.targetAddr"
            placeholder="如 192.168.1.50（设备可达的内网地址）"
            data-testid="simple-wizard-target-addr"
          />
          <p
            v-if="form.targetMode === 'ip' && targetDevice"
            class="muted"
          >
            对端「{{ targetDevice.deviceName }}」开放网段：{{
              targetDevice.lanSegments.length ? targetDevice.lanSegments.join("、") : "无（仅 self 放行）"
            }}——段外地址将被拒绝
          </p>
        </el-collapse-item>
      </el-collapse>
    </div>

    <!-- 步 3：本地端口与名称（默认联动，可改） -->
    <div
      v-else-if="step === 2"
      class="step-body"
    >
      <p class="muted">
        本机监听端口与名称——创建后本机经 {{ connectAddr }} 访问目标服务
      </p>
      <div class="field">
        <span class="field-label">本地端口</span>
        <el-input-number
          v-model="form.localPort"
          :min="1"
          :max="65535"
          data-testid="simple-wizard-localport"
          @change="localTouched = true"
        />
      </div>
      <div class="field">
        <span class="field-label">名称</span>
        <el-input
          v-model="form.name"
          maxlength="50"
          style="width: 220px"
          data-testid="simple-wizard-name"
          @input="nameTouched = true"
        />
      </div>
      <p class="muted">
        本地端口与名称已按目标服务预填，可直接下一步
      </p>
    </div>

    <!-- 步 4：确认并启用 -->
    <div
      v-else
      class="step-body"
    >
      <el-descriptions
        :column="1"
        border
        data-testid="simple-wizard-summary"
      >
        <el-descriptions-item label="目标设备">
          {{ targetDevice?.deviceName ?? form.targetRemoteCode }}（{{ form.targetRemoteCode }}）
        </el-descriptions-item>
        <el-descriptions-item label="目标服务">
          {{ form.proto.toUpperCase() }} :{{ form.targetPort }}<template v-if="form.targetMode === 'ip'">
            @ {{ form.targetAddr }}
          </template>
        </el-descriptions-item>
        <el-descriptions-item label="连接地址">
          {{ connectAddr }}
        </el-descriptions-item>
        <el-descriptions-item label="名称">
          {{ form.name }}
        </el-descriptions-item>
      </el-descriptions>
      <p class="muted">
        点「完成并启用」后立即开始打洞，成功后即可通过连接地址访问。
      </p>
      <p
        v-if="errorText"
        class="error"
        data-testid="simple-wizard-error"
      >
        {{ errorText }}
      </p>
    </div>

    <template #footer>
      <el-button
        v-if="step > 0"
        data-testid="simple-wizard-prev"
        @click="step -= 1"
      >
        上一步
      </el-button>
      <el-button
        v-if="step < 3"
        type="primary"
        :disabled="nextDisabled"
        data-testid="simple-wizard-next"
        @click="step += 1"
      >
        下一步
      </el-button>
      <el-button
        v-else
        type="primary"
        :loading="busy"
        data-testid="simple-wizard-finish"
        @click="finish"
      >
        完成并启用
      </el-button>
      <el-button @click="open = false">
        取消
      </el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.step-body { margin-top: 16px; }
.meta { margin-top: 10px; }
.field { display: flex; align-items: center; gap: 12px; margin: 12px 0; }
.field-label { width: 64px; color: var(--el-text-color-regular); font-size: 14px; flex-shrink: 0; }
.chips { display: flex; gap: 8px; flex-wrap: wrap; margin: 4px 0 12px 76px; }
.advanced { margin-left: 0; }
.off-tag { margin-left: 8px; }
.error { color: var(--el-color-danger); font-size: 13px; margin: 8px 0 0; }
</style>

<script setup lang="ts">
// M1-32 设置页（06 §2、FR-C-808）：serverAddrs（主备多候选、可增删排序）+
// 打洞并发路数 N（1~5 滑块 + 说明"目标端口 = STUN 端口 + (N−1)"）+ 本地 Web 端口。
// PUT 部分修改；localWebPort 变更 → restartRequired 提示重启生效（04 §2.1）。
import { computed, onMounted, ref } from "vue";
import { ElMessage } from "element-plus";
import { ApiError } from "@p2p/ui-shared";
import { useSettingsStore } from "../stores/settings";

const settings = useSettingsStore();

const addrs = ref<string[]>([]);
const punchConcurrency = ref(3);
const localWebPort = ref(7100);
const busy = ref(false);
/** 提交后置位：localWebPort 变更须重启本地服务才生效（监听端口绑定时机） */
const restartHint = ref(false);

const ADDR_PATTERN = /^[\w.-]+:\d{1,5}$/;
const addrInvalid = computed(() => addrs.value.filter((a) => !ADDR_PATTERN.test(a)));
const canSave = computed(
  () => addrs.value.length > 0 && addrInvalid.value.length === 0 &&
    Number.isInteger(localWebPort.value) && localWebPort.value >= 1 && localWebPort.value <= 65535,
);

function addAddr() {
  addrs.value.push("");
}

function removeAddr(i: number) {
  addrs.value.splice(i, 1);
}

function move(i: number, delta: -1 | 1) {
  const j = i + delta;
  if (j < 0 || j >= addrs.value.length) return;
  [addrs.value[i], addrs.value[j]] = [addrs.value[j], addrs.value[i]];
}

async function save() {
  if (!canSave.value) {
    ElMessage.warning("存在非法配置项，请修正后保存");
    return;
  }
  busy.value = true;
  try {
    const result = await settings.save({
      serverAddrs: addrs.value,
      punchConcurrency: punchConcurrency.value,
      localWebPort: localWebPort.value,
    });
    restartHint.value = result.restartRequired;
    if (result.restartRequired) ElMessage.warning("本地 Web 端口已变更：重启客户端后生效");
    else ElMessage.success("设置已保存（即时生效）");
  } catch (e) {
    ElMessage.error(e instanceof ApiError ? e.message : "保存失败");
  } finally {
    busy.value = false;
  }
}

onMounted(async () => {
  await settings.refresh();
  const s = settings.settings;
  if (s) {
    addrs.value = [...s.serverAddrs];
    punchConcurrency.value = s.punchConcurrency;
    localWebPort.value = s.localWebPort;
  }
});
</script>

<template>
  <section class="settings">
    <h2>设置</h2>

    <el-form label-width="140px">
      <!-- 服务端地址：主备候选，序即优先级 -->
      <el-form-item label="服务端地址">
        <div
          class="addrs"
          data-testid="settings-addrs"
        >
          <div
            v-for="(a, i) in addrs"
            :key="i"
            class="addr-row"
          >
            <el-input
              v-model="addrs[i]"
              :placeholder="`host:port（候选 ${i + 1}）`"
              :class="{ invalid: a !== '' && !ADDR_PATTERN.test(a) }"
            />
            <el-button
              size="small"
              :disabled="i === 0"
              title="上移（提高优先级）"
              @click="move(i, -1)"
            >
              ↑
            </el-button>
            <el-button
              size="small"
              :disabled="i === addrs.length - 1"
              title="下移"
              @click="move(i, 1)"
            >
              ↓
            </el-button>
            <el-button
              size="small"
              type="danger"
              plain
              @click="removeAddr(i)"
            >
              删除
            </el-button>
          </div>
          <el-button
            size="small"
            data-testid="settings-add-addr"
            @click="addAddr"
          >
            添加候选
          </el-button>
          <div class="hint">
            按序尝试连接；修改保存后控制通道立即换址重连（serverAddrs 即时生效）
          </div>
        </div>
      </el-form-item>

      <!-- 打洞并发 N：1~5 滑块（PRD OQ-11 参数） -->
      <el-form-item label="打洞并发路数">
        <div class="slider-block">
          <el-slider
            v-model="punchConcurrency"
            :min="1"
            :max="5"
            :step="1"
            show-stops
            style="width: 260px"
            data-testid="settings-punch"
          />
          <div class="hint">
            同时打洞的 socket 路数 N（1~5）：目标端口 = STUN 端口 + (N−1)，路数多命中率高但更易触发对端 NAT 限速
          </div>
        </div>
      </el-form-item>

      <!-- 本地 Web 端口 -->
      <el-form-item label="本地 Web 端口">
        <el-input-number
          v-model="localWebPort"
          :min="1"
          :max="65535"
          data-testid="settings-webport"
        />
        <div class="hint">
          本页面服务的监听端口；变更后须重启客户端生效
        </div>
      </el-form-item>

      <el-form-item>
        <el-button
          type="primary"
          :loading="busy"
          :disabled="!canSave"
          data-testid="settings-save"
          @click="save"
        >
          保存
        </el-button>
      </el-form-item>
    </el-form>

    <el-alert
      v-if="restartHint"
      type="warning"
      :closable="false"
      title="本地 Web 端口已变更——重启客户端后新端口生效（当前仍由旧端口服务）"
      data-testid="settings-restart-hint"
    />

    <!-- 只读参考：keepalive/重连退避（后端固化，M1 不开放编辑） -->
    <el-descriptions
      v-if="settings.settings"
      title="高级参数（只读）"
      :column="2"
      border
      class="readonly"
    >
      <el-descriptions-item label="隧道保活（秒）">
        {{ settings.settings.keepaliveSec }}
      </el-descriptions-item>
      <el-descriptions-item label="重连退避（秒）">
        {{ settings.settings.reconnect.minSec }} ~ {{ settings.settings.reconnect.maxSec }}
      </el-descriptions-item>
    </el-descriptions>
  </section>
</template>

<style scoped>
.addrs { display: grid; gap: 8px; width: 100%; }
.addr-row { display: flex; gap: 8px; align-items: center; }
.addr-row .el-input { width: 280px; }
.addr-row .el-input.invalid :deep(input) { border-color: var(--el-color-danger); }
.slider-block { width: 100%; }
.hint { font-size: 12px; color: var(--el-text-color-secondary); margin-top: 4px; }
.readonly { margin-top: 24px; max-width: 640px; }
</style>

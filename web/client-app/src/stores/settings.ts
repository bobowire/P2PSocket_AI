// M1-32 设置 store（06 §2 /settings）：GET 全量 + PUT 部分修改（缺省字段保持现值）。
// localWebPort 变更返回 restartRequired——监听端口重启后生效（04 §2.1）。
import { ref } from "vue";
import { defineStore } from "pinia";
import type { ClientSettings } from "@p2p/ui-shared";
import { ApiPaths } from "@p2p/ui-shared";
import { api } from "../api";

export interface SettingsSaveResult {
  restartRequired: boolean;
  serverAddrsChanged: boolean;
  settings: ClientSettings;
}

export const useSettingsStore = defineStore("settings", () => {
  const settings = ref<ClientSettings | null>(null);
  const loaded = ref(false);

  async function refresh() {
    try {
      settings.value = await api.get<ClientSettings>(ApiPaths.Settings);
    } catch {
      /* 保留旧值（服务不可达由全局条提示） */
    } finally {
      loaded.value = true;
    }
  }

  /** 部分修改：仅送变更字段（null=保持现值语义在服务端，04 §2.1）。 */
  async function save(patch: Partial<Pick<ClientSettings,
    "serverAddrs" | "localWebPort" | "punchConcurrency" | "keepaliveSec">>) {
    const result = await api.put<SettingsSaveResult>(ApiPaths.Settings, patch);
    settings.value = result.settings;
    return result;
  }

  return { settings, loaded, refresh, save };
});

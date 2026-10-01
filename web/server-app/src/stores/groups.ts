// M3-10 分组页 store（06 §3、FR-S-822/305）：总览 + 默认分组准入策略（双写端点）+ 跨分组审批队列。
// 默认分组策略显示来源=总览 isDefault 行的 joinPolicy（M3-05 双写后键/行一致）。
import { computed, ref } from "vue";
import { defineStore } from "pinia";
import type { GroupListView, GroupRequestListView, PolicyResult } from "@p2p/ui-shared";
import { ServerApiPaths, apiPath } from "@p2p/ui-shared";
import { api } from "../api";

export const useGroupsStore = defineStore("server-groups", () => {
  const list = ref<GroupListView | null>(null);
  const requests = ref<GroupRequestListView | null>(null);

  const defaultPolicy = computed(() =>
    list.value?.items.find((g) => g.isDefault)?.joinPolicy ?? "free");

  async function refresh() {
    list.value = await api.get<GroupListView>(ServerApiPaths.Groups);
  }

  async function refreshRequests() {
    requests.value = await api.get<GroupRequestListView>(
      `${ServerApiPaths.GroupRequests}?status=pending`);
  }

  /// PUT /api/groups/default 双写（键=下次建组口径 + 默认分组行=存量即时生效）；回读新值
  async function saveDefaultPolicy(policy: string) {
    const r = await api.put<PolicyResult>(ServerApiPaths.GroupsDefault, { policy });
    await refresh();
    return r.policy;
  }

  async function approve(requestId: string) {
    await api.post(apiPath(ServerApiPaths.GroupRequestsByIdApprove, { id: requestId }));
    await Promise.all([refreshRequests(), refresh()]);
  }

  async function reject(requestId: string) {
    await api.post(apiPath(ServerApiPaths.GroupRequestsByIdReject, { id: requestId }));
    await Promise.all([refreshRequests(), refresh()]);
  }

  return { list, requests, defaultPolicy, refresh, refreshRequests, saveDefaultPolicy, approve, reject };
});

// M2-28 分组 store（06 §2/§4）：列表与聚合审批队列经 REST（GET /api/groups 一次取
// items+requests）；成员资格变更后服务端经 WS device_list 提示 refetch（TD-16 状态类）。
// 0x51~0x57 全属主动类——passive 由本地 API 拒发 2002（refresh 静默保留旧列表，页面置灰）。
import { ref } from "vue";
import { defineStore } from "pinia";
import type { GroupRequestView, GroupView } from "@p2p/ui-shared";
import { ApiError, ApiPaths, apiPath } from "@p2p/ui-shared";
import { api } from "../api";

export interface GroupFormInput {
  name: string;
  joinPolicy: "free" | "approval";
}

export const useGroupStore = defineStore("groups", () => {
  const items = ref<GroupView[]>([]);
  const requests = ref<GroupRequestView[]>([]);
  const loaded = ref(false);

  async function refresh() {
    try {
      const page = await api.get<{ items: GroupView[]; requests: GroupRequestView[] }>(ApiPaths.Groups);
      items.value = page.items;
      requests.value = page.requests;
    } catch {
      /* passive（2002）/不可达：保留旧列表，页面按能力模式禁写 */
    } finally {
      loaded.value = true;
    }
  }

  /** 建组（0x50）：成功回新组 id；3002 等业务错原样抛（ApiError.code）由页面分流提示。 */
  async function create(input: GroupFormInput) {
    const r = await api.post<{ groupId: string }>(ApiPaths.Groups, {
      name: input.name,
      joinPolicy: input.joinPolicy,
    });
    await refresh();
    return r.groupId;
  }

  /** 凭码入组（0x51）：free 即入；approval 建申请单 → 服务端 3002（页面按待审批提示）。 */
  async function join(inviteCode: string) {
    try {
      const r = await api.post<{ groupId: string }>(ApiPaths.GroupsJoin, { inviteCode });
      await refresh();
      return r.groupId;
    } catch (e) {
      // 3002：申请单已在服务端建好 → 先同步聚合队列（供所有者侧审批），再原样抛给页面提示
      if (e instanceof ApiError && e.code === 3002) await refresh();
      throw e;
    }
  }

  async function leave(id: string) {
    await api.post(apiPath(ApiPaths.GroupsByIdLeave, { id }));
    await refresh();
  }

  async function update(id: string, input: Partial<GroupFormInput>) {
    await api.put(apiPath(ApiPaths.GroupsById, { id }), {
      name: input.name,
      joinPolicy: input.joinPolicy,
    });
    await refresh();
  }

  async function dissolve(id: string) {
    await api.del(apiPath(ApiPaths.GroupsById, { id }));
    await refresh();
  }

  async function kick(id: string, deviceId: string) {
    await api.post(apiPath(ApiPaths.GroupsByIdMembersByDeviceidKick, { id, deviceId }));
    await refresh();
  }

  /** 生成/取回邀请码（0x54 覆盖式：每分组至多一码，重新生成覆盖旧码）。 */
  async function genInvite(id: string) {
    const r = await api.get<{ inviteCode: string }>(apiPath(ApiPaths.GroupsByIdInvite, { id }));
    return r.inviteCode;
  }

  /** 撤销邀请码（0x54 Revoke：旧码立即失效）。 */
  async function revokeInvite(id: string) {
    await api.del(apiPath(ApiPaths.GroupsByIdInvite, { id }));
  }

  async function approve(requestId: string) {
    await api.post(apiPath(ApiPaths.GroupRequestsByIdApprove, { id: requestId }));
    await refresh();
  }

  async function reject(requestId: string) {
    await api.post(apiPath(ApiPaths.GroupRequestsByIdReject, { id: requestId }));
    await refresh();
  }

  return {
    items, requests, loaded, refresh,
    create, join, leave, update, dissolve, kick,
    genInvite, revokeInvite, approve, reject,
  };
});

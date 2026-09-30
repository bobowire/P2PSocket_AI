// M2-27 分组管理端点（04 §2.4，转发 0x42/0x50~0x57）：
// - GET /api/groups = 0x42 已加入分组 + 逐自有组 0x53 List 聚合待审批（组量级小，不改 0x53 语义）；
// - 建/入/退/编辑/解散/移出/邀请码/审批全为薄转发；主动类消息 passive 由 ControlClient 本地拒发 2002；
// - 成员资格变更后 hub 发 device_list 提示（0x40 摘要含 groups[]；服务端侧 0x41 推送由 M2-10 联动）；
// - 建组空名服务端回 GroupId=Empty（诚实 Ack 不误报）：本地前置校验 + Empty 判重防线。
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using P2P.Client.Control;
using P2P.Core.Protocol;

namespace P2P.Client.Web;

public static class GroupsApi
{
    public static IEndpointRouteBuilder MapGroupsApi(this IEndpointRouteBuilder app,
        ControlClient control, StatusHub hub)
    {
        // 已加入分组 + 待审批申请（04 §2.4：requests 仅所有者侧，聚合到自有组）
        app.MapGet("/api/groups", async (CancellationToken ct) =>
        {
            try
            {
                var resp = await control.SendRequestAsync<GroupListResponse>(new GroupListRequest(
                    control.NextSeq(), control.TimestampMs(), MsgType.GroupList), ct);
                var items = resp.Items
                    .Select(g => new GroupView(g.GroupId, g.GroupName, PolicyString(g.Policy),
                        g.IsOwner, g.MemberCount)).ToList();

                var requests = new List<GroupRequestView>();
                foreach (var owned in resp.Items.Where(g => g.IsOwner))
                {
                    var list = await control.SendRequestAsync<JoinRequestsResponse>(new JoinRequests(
                        control.NextSeq(), control.TimestampMs(), MsgType.JoinRequests,
                        JoinRequestAction.List, owned.GroupId, null), ct);
                    requests.AddRange(list.Items.Select(r =>
                        new GroupRequestView(r.RequestId, r.GroupId, r.DeviceId, r.DeviceName, r.CreatedAtMs)));
                }
                return Api.Ok(new GroupsView(items.ToArray(), requests.ToArray()));
            }
            catch (Exception e) { return Api.Fail(e); }
        });

        // 建组（FR-C-805）：{ name, joinPolicy } → 0x50 → { groupId }
        app.MapPost("/api/groups", async (GroupCreateRequest body, CancellationToken ct) =>
        {
            try
            {
                var (name, policy) = ValidateCreate(body);
                var ack = await control.SendRequestAsync<GroupCreateAck>(new GroupCreate(
                    control.NextSeq(), control.TimestampMs(), MsgType.GroupCreate, name, policy), ct);
                if (ack.GroupId == Guid.Empty)
                    throw new ApiException(ErrorCode.BadRequest, "分组创建被拒绝（名称不可用或未登录）");
                hub.Publish(new { ev = WsEventNames.DeviceList });
                return Api.Ok(new { groupId = ack.GroupId });
            }
            catch (Exception e) { return Api.Fail(e); }
        });

        // 凭码入组（04 §2.4 /api/groups/join）：free 即入回 groupId；approval 建
        // 申请单回 3002（Api.Fail 原码透传，前端按待审批提示）
        app.MapPost("/api/groups/join", async (GroupJoinRequest body, CancellationToken ct) =>
        {
            try
            {
                var code = body.InviteCode?.Trim() ?? "";
                if (code.Length == 0)
                    throw new ApiException(ErrorCode.BadRequest, "邀请码不能为空");
                var ack = await control.SendRequestAsync<GroupJoinAck>(new GroupJoin(
                    control.NextSeq(), control.TimestampMs(), MsgType.GroupJoin, code), ct);
                hub.Publish(new { ev = WsEventNames.DeviceList });
                return Api.Ok(new { groupId = ack.GroupId });
            }
            catch (Exception e) { return Api.Fail(e); }
        });

        // 自退（0x52；服务端联动 0x75 清理残余授权）
        app.MapPost("/api/groups/{id:guid}/leave", async (Guid id, CancellationToken ct) =>
        {
            try
            {
                var ack = await control.SendRequestAsync<GroupLeaveAck>(new GroupLeave(
                    control.NextSeq(), control.TimestampMs(), MsgType.GroupLeave, id), ct);
                if (!ack.Ok)
                    throw new ApiException(ErrorCode.BadRequest, "退出分组失败（未登录或不在组内）");
                hub.Publish(new { ev = WsEventNames.DeviceList });
                return Api.Ok(null);
            }
            catch (Exception e) { return Api.Fail(e); }
        });

        // 所有者编辑（0x55，FR-S-302）：null 字段=不改（02 §2.4）
        app.MapPut("/api/groups/{id:guid}", async (Guid id, GroupUpdateRequest body, CancellationToken ct) =>
        {
            try
            {
                var name = string.IsNullOrWhiteSpace(body.Name) ? null : body.Name.Trim();
                JoinPolicy? policy = body.JoinPolicy switch
                {
                    "free" => JoinPolicy.Free,
                    "approval" => JoinPolicy.Approval,
                    null => null,
                    _ => throw new ApiException(ErrorCode.BadRequest, "joinPolicy 须为 free 或 approval"),
                };
                if (name is null && policy is null)
                    throw new ApiException(ErrorCode.BadRequest, "未提供任何修改项（name/joinPolicy）");
                var ack = await control.SendRequestAsync<GroupUpdateAck>(new GroupUpdate(
                    control.NextSeq(), control.TimestampMs(), MsgType.GroupUpdate, id, name, policy), ct);
                if (!ack.Ok)
                    throw new ApiException(ErrorCode.BadRequest, "编辑分组失败（未登录或非所有者）");
                hub.Publish(new { ev = WsEventNames.DeviceList });
                return Api.Ok(null);
            }
            catch (Exception e) { return Api.Fail(e); }
        });

        // 所有者解散（0x56；组内在线成员收 0x75）
        app.MapDelete("/api/groups/{id:guid}", async (Guid id, CancellationToken ct) =>
        {
            try
            {
                var ack = await control.SendRequestAsync<GroupDissolveAck>(new GroupDissolve(
                    control.NextSeq(), control.TimestampMs(), MsgType.GroupDissolve, id), ct);
                if (!ack.Ok)
                    throw new ApiException(ErrorCode.BadRequest, "解散分组失败（未登录或非所有者）");
                hub.Publish(new { ev = WsEventNames.DeviceList });
                return Api.Ok(null);
            }
            catch (Exception e) { return Api.Fail(e); }
        });

        // 所有者移出成员（0x57，FR-S-307；被移出设备收 0x75）
        app.MapPost("/api/groups/{id:guid}/members/{deviceId:guid}/kick", async (Guid id, Guid deviceId,
            CancellationToken ct) =>
        {
            try
            {
                var ack = await control.SendRequestAsync<GroupRemoveMemberAck>(new GroupRemoveMember(
                    control.NextSeq(), control.TimestampMs(), MsgType.GroupRemoveMember, id, deviceId), ct);
                if (!ack.Ok)
                    throw new ApiException(ErrorCode.BadRequest, "移出成员失败（未登录或非所有者）");
                hub.Publish(new { ev = WsEventNames.DeviceList });
                return Api.Ok(null);
            }
            catch (Exception e) { return Api.Fail(e); }
        });

        // 生成邀请码（0x54 Revoke=false；覆盖式：每分组至多一码，OQ-17）
        app.MapGet("/api/groups/{id:guid}/invite", async (Guid id, CancellationToken ct) =>
        {
            try
            {
                var ack = await control.SendRequestAsync<GroupInviteGenAck>(new GroupInviteGen(
                    control.NextSeq(), control.TimestampMs(), MsgType.GroupInviteGen, id, Revoke: false), ct);
                if (!ack.Ok || string.IsNullOrEmpty(ack.InviteCode))
                    throw new ApiException(ErrorCode.BadRequest, "邀请码生成失败（未登录或非所有者）");
                return Api.Ok(new { inviteCode = ack.InviteCode });
            }
            catch (Exception e) { return Api.Fail(e); }
        });

        // 撤销邀请码（0x54 Revoke=true；撤销=置 NULL，旧码立即失效）
        app.MapDelete("/api/groups/{id:guid}/invite", async (Guid id, CancellationToken ct) =>
        {
            try
            {
                var ack = await control.SendRequestAsync<GroupInviteGenAck>(new GroupInviteGen(
                    control.NextSeq(), control.TimestampMs(), MsgType.GroupInviteGen, id, Revoke: true), ct);
                if (!ack.Ok)
                    throw new ApiException(ErrorCode.BadRequest, "邀请码撤销失败（未登录或非所有者）");
                return Api.Ok(null);
            }
            catch (Exception e) { return Api.Fail(e); }
        });

        // 审批队列（0x53 List；仅所有者，server 侧 4004/4003）
        app.MapGet("/api/groups/{id:guid}/requests", async (Guid id, CancellationToken ct) =>
        {
            try
            {
                var resp = await control.SendRequestAsync<JoinRequestsResponse>(new JoinRequests(
                    control.NextSeq(), control.TimestampMs(), MsgType.JoinRequests,
                    JoinRequestAction.List, id, null), ct);
                return Api.Ok(resp.Items.Select(r =>
                    new GroupRequestView(r.RequestId, r.GroupId, r.DeviceId, r.DeviceName, r.CreatedAtMs)));
            }
            catch (Exception e) { return Api.Fail(e); }
        });

        // 审批决定（0x53 Approve/Reject，FR-S-305；RequestId 定位，GroupId 免填）
        app.MapPost("/api/group-requests/{id:guid}/approve", (Guid id, CancellationToken ct) =>
            DecideAsync(control, hub, JoinRequestAction.Approve, id, ct));
        app.MapPost("/api/group-requests/{id:guid}/reject", (Guid id, CancellationToken ct) =>
            DecideAsync(control, hub, JoinRequestAction.Reject, id, ct));

        return app;
    }

    private static async Task<IResult> DecideAsync(ControlClient control, StatusHub hub,
        JoinRequestAction action, Guid requestId, CancellationToken ct)
    {
        try
        {
            var ack = await control.SendRequestAsync<JoinRequestsAck>(new JoinRequests(
                control.NextSeq(), control.TimestampMs(), MsgType.JoinRequests, action, null, requestId), ct);
            if (!ack.Ok)
                throw new ApiException(ErrorCode.BadRequest, "审批失败（申请已处理或非所有者）");
            hub.Publish(new { ev = WsEventNames.DeviceList });
            return Api.Ok(null);
        }
        catch (Exception e) { return Api.Fail(e); }
    }

    private static (string Name, JoinPolicy Policy) ValidateCreate(GroupCreateRequest body)
    {
        var name = body.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 64)
            throw new ApiException(ErrorCode.BadRequest, "分组名长度须为 1~64 字符");
        return body.JoinPolicy switch
        {
            "free" => (name, JoinPolicy.Free),
            "approval" => (name, JoinPolicy.Approval),
            _ => throw new ApiException(ErrorCode.BadRequest, "joinPolicy 须为 free 或 approval"),
        };
    }

    internal static string PolicyString(JoinPolicy policy) => policy == JoinPolicy.Approval ? "approval" : "free";
}

// ── 展示 DTO（export-ts 单一事实源；Guid→string、createdAtMs 为 Unix 毫秒）──

public sealed record GroupView(Guid GroupId, string GroupName, string Policy, bool IsOwner, uint MemberCount);

public sealed record GroupRequestView(
    Guid RequestId, Guid GroupId, Guid DeviceId, string DeviceName, ulong CreatedAtMs);

public sealed record GroupsView(GroupView[] Items, GroupRequestView[] Requests);

// ── 请求体（camelCase 绑定）──────────────────────────────────────

public sealed record GroupCreateRequest(string? Name, string? JoinPolicy);

public sealed record GroupJoinRequest(string? InviteCode);

public sealed record GroupUpdateRequest(string? Name, string? JoinPolicy);

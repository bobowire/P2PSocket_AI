using Microsoft.EntityFrameworkCore;
using P2P.Core.Protocol;
using P2P.Server.Data;

namespace P2P.Server.Services;

/// <summary>
/// 分组与设备列表（02 §2.4 0x50~0x57/0x40；FR-S-301~307、OQ-16/17）。
/// 0x40 可见性 = 本账号设备 ∪ 共同分组设备（与 L2 同口径，05 §5）。
/// M2-09：凭码入组/审批队列/邀请码/退组/移出成员全量；入组即跨账号共享
/// （成员是设备维度，joiner 所属账号与组所有者无关，05 §1）；0x75 联动触发点归 M2-12。
/// M2-10：成员变更后经 DeviceListPusher 推 0x41（提示帧，哑节点不下发）。
/// </summary>
public sealed class GroupService(
    IDbContextFactory<AppDbContext> dbFactory,
    DeviceRegistry registry,
    AuditLogger audit,
    DeviceListPusher? pusher = null,
    InvalidationPusher? invalidation = null,
    TimeProvider? time = null)
{
    /// <summary>0x40 分页上限（OQ-16：默认 100，钳制区间 1~200）。</summary>
    public const int DefaultLimit = 100;
    public const int MaxLimit = 200;

    /// <summary>邀请码 6 位去混淆字符集（03 §3：数字 2-9 + 小写去 o/i/l，OQ-17）。</summary>
    public const int InviteCodeLength = 6;
    internal const string InviteCharset = "23456789abcdefghjkmnpqrstuvwxyz";
    private const int InviteCodeMaxAttempts = 8;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    // ── 0x50 建组（登录态）────────────────────────────────────────────

    public async Task HandleCreateAsync(ControlSession session, GroupCreate msg)
    {
        if (session.OwnerUserId is not { } owner)
        {
            await session.SendErrorAsync(ErrorCode.Unauthorized, "login_required");
            return;
        }
        if (string.IsNullOrWhiteSpace(msg.Name))
        {
            await session.SendAsync(new GroupCreateAck(session.NextSeq(), session.ServerTimestamp(),
                MsgType.GroupCreate, Guid.Empty));
            return;
        }

        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = msg.Name.Trim(),
            OwnerUserId = owner,
            JoinPolicy = msg.Policy == JoinPolicy.Approval ? "approval" : "free",
            CreatedAt = _time.GetLocalNow().UtcDateTime,
        };
        await using var db = await dbFactory.CreateDbContextAsync();
        db.Groups.Add(group);
        // 创建设备即首成员（FR-S-306 组内互见：可见性=共同分组口径，
        // 创建者不入组则与凭码入组的跨账号设备互不可见）
        db.GroupMembers.Add(new GroupMember
        {
            Id = Guid.NewGuid(), GroupId = group.Id, DeviceId = session.DeviceId,
            Approved = true, JoinedAt = group.CreatedAt,
        });
        await db.SaveChangesAsync();
        await session.SendAsync(new GroupCreateAck(session.NextSeq(), session.ServerTimestamp(),
            MsgType.GroupCreate, group.Id));
    }

    // ── 0x51 凭码入组（登录态；free 即入 / approval 建申请单；FR-S-303/306）──

    public async Task HandleJoinAsync(ControlSession session, GroupJoin msg)
    {
        if (session.OwnerUserId is null)
        {
            await session.SendErrorAsync(ErrorCode.Unauthorized, "login_required");
            return;
        }
        var code = msg.InviteCode?.Trim();
        if (string.IsNullOrEmpty(code))
        {
            await session.SendErrorAsync(ErrorCode.BadRequest, "invite_code_required");
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        var group = await db.Groups.AsNoTracking().SingleOrDefaultAsync(g => g.InviteCode == code);
        if (group is null) // 无码匹配=无效或已撤销（撤销=置 NULL）
        {
            await session.SendErrorAsync(ErrorCode.GroupNotFound, "invite_invalid");
            await audit.WriteAsync("group_join_deny", session.DeviceId, userId: session.OwnerUserId,
                detail: new { Reason = "invite_invalid" });
            return;
        }

        // 已是成员：幂等 Ack（不重复入组、不重复审计）
        if (await db.GroupMembers.AnyAsync(m => m.GroupId == group.Id && m.DeviceId == session.DeviceId))
        {
            await session.SendAsync(new GroupJoinAck(session.NextSeq(), session.ServerTimestamp(),
                MsgType.GroupJoin, group.Id));
            return;
        }

        if (group.JoinPolicy != "approval")
        {
            db.GroupMembers.Add(new GroupMember
            {
                Id = Guid.NewGuid(), GroupId = group.Id, DeviceId = session.DeviceId,
                Approved = true, JoinedAt = _time.GetLocalNow().UtcDateTime,
            });
            // 直接入组后清理残留 pending 申请（策略曾由 approval 切换的边角）
            await db.JoinRequests.Where(r => r.GroupId == group.Id && r.DeviceId == session.DeviceId
                && r.Status == "pending").ExecuteDeleteAsync();
            await db.SaveChangesAsync();
            await session.SendAsync(new GroupJoinAck(session.NextSeq(), session.ServerTimestamp(),
                MsgType.GroupJoin, group.Id));
            await audit.WriteAsync("group_join", session.DeviceId, userId: session.OwnerUserId,
                detail: new { GroupId = group.Id, Policy = "free" });
            if (pusher is not null)
                await pusher.NotifyGroupMembersAsync(group.Id, session.DeviceId); // 0x41（M2-10）
            return;
        }

        // approval：建 pending 申请单（重复申请去重），回 3002 待审批
        if (!await db.JoinRequests.AnyAsync(r => r.GroupId == group.Id && r.DeviceId == session.DeviceId
                && r.Status == "pending"))
        {
            db.JoinRequests.Add(new JoinRequest
            {
                Id = Guid.NewGuid(), GroupId = group.Id, DeviceId = session.DeviceId,
                Status = "pending", CreatedAt = _time.GetLocalNow().UtcDateTime,
            });
            await db.SaveChangesAsync();
            await audit.WriteAsync("group_join_request", session.DeviceId, userId: session.OwnerUserId,
                detail: new { GroupId = group.Id });
        }
        await session.SendErrorAsync(ErrorCode.GroupNeedApproval, "group_need_approval");
    }

    // ── 0x52 设备自退（登录态；FR-S-307）─────────────────────────────

    public async Task HandleLeaveAsync(ControlSession session, GroupLeave msg)
    {
        if (session.OwnerUserId is null)
        {
            await session.SendErrorAsync(ErrorCode.Unauthorized, "login_required");
            return;
        }
        await using var db = await dbFactory.CreateDbContextAsync();
        var removed = await db.GroupMembers.Where(m =>
            m.GroupId == msg.GroupId && m.DeviceId == session.DeviceId).ExecuteDeleteAsync();
        await session.SendAsync(new GroupLeaveAck(session.NextSeq(), session.ServerTimestamp(),
            MsgType.GroupLeave, removed > 0));
        if (removed > 0)
        {
            await audit.WriteAsync("group_leave", session.DeviceId, userId: session.OwnerUserId,
                detail: new { msg.GroupId });
            if (pusher is not null)
                await pusher.NotifyGroupMembersAsync(msg.GroupId, session.DeviceId); // 0x41（M2-10）
            // 0x75 group_left（M2-12）：切断边映射失效（残余可见性复核——同账号/另共同组保留）
            if (invalidation is not null)
                await invalidation.PushMemberSeveredAsync(session.DeviceId, InvalidationReason.GroupLeft);
        }
    }

    // ── 0x53 审批队列 List/Approve/Reject（仅所有者；FR-S-305）────────

    public async Task HandleJoinRequestsAsync(ControlSession session, JoinRequests msg)
    {
        if (session.OwnerUserId is null)
        {
            await session.SendErrorAsync(ErrorCode.Unauthorized, "login_required");
            return;
        }
        switch (msg.Action)
        {
            case JoinRequestAction.List:
                await HandleJoinListAsync(session, msg);
                return;
            case JoinRequestAction.Approve or JoinRequestAction.Reject:
                await HandleJoinDecisionAsync(session, msg);
                return;
            default:
                await session.SendErrorAsync(ErrorCode.BadRequest, "bad_join_request_action");
                return;
        }
    }

    private async Task HandleJoinListAsync(ControlSession session, JoinRequests msg)
    {
        if (msg.GroupId is not { } groupId)
        {
            await session.SendErrorAsync(ErrorCode.BadRequest, "group_id_required");
            return;
        }
        var (forbidden, notFound) = await CheckOwnerAsync(session, groupId);
        if (notFound)
        {
            await session.SendErrorAsync(ErrorCode.GroupNotFound, "group_not_found");
            return;
        }
        if (forbidden)
        {
            await session.SendErrorAsync(ErrorCode.Forbidden, "not_group_owner");
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.JoinRequests.AsNoTracking()
            .Where(r => r.GroupId == groupId && r.Status == "pending")
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .ToListAsync();
        var names = await db.Devices.AsNoTracking()
            .Where(d => rows.Select(r => r.DeviceId).Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, d => d.DeviceName);
        var items = rows.Select(r => new JoinRequestItem(r.Id, r.GroupId, r.DeviceId,
            names.GetValueOrDefault(r.DeviceId, ""),
            (ulong)new DateTimeOffset(r.CreatedAt).ToUnixTimeMilliseconds())).ToArray();
        await session.SendAsync(new JoinRequestsResponse(session.NextSeq(), session.ServerTimestamp(),
            MsgType.JoinRequests, items));
    }

    private async Task HandleJoinDecisionAsync(ControlSession session, JoinRequests msg)
    {
        if (msg.RequestId is not { } requestId)
        {
            await session.SendErrorAsync(ErrorCode.BadRequest, "request_id_required");
            return;
        }
        await using var db = await dbFactory.CreateDbContextAsync();
        var request = await db.JoinRequests.AsNoTracking().SingleOrDefaultAsync(r =>
            r.Id == requestId && r.Status == "pending");
        if (request is null) // 不存在或已处理：诚实 Ok=false（防客户端无限等待）
        {
            await session.SendAsync(new JoinRequestsAck(session.NextSeq(), session.ServerTimestamp(),
                MsgType.JoinRequests, false));
            return;
        }
        var (forbidden, notFound) = await CheckOwnerAsync(session, request.GroupId);
        if (notFound)
        {
            await session.SendErrorAsync(ErrorCode.GroupNotFound, "group_not_found");
            return;
        }
        if (forbidden)
        {
            await session.SendErrorAsync(ErrorCode.Forbidden, "not_group_owner");
            return;
        }

        var decision = await ApplyJoinDecisionAsync(requestId, msg.Action == JoinRequestAction.Approve,
            session.OwnerUserId, session.DeviceId);
        // 帧序契约：Ack 先于 0x41 推送（客户端读 Ack 的 skipping 助手会消费掉先到的推送帧）
        await session.SendAsync(new JoinRequestsAck(session.NextSeq(), session.ServerTimestamp(),
            MsgType.JoinRequests, decision is not null));
        if (decision is not null && msg.Action == JoinRequestAction.Approve)
            await NotifyJoinApprovalAsync(decision);
    }

    /// <summary>审批决议结果（GroupId/DeviceId 供 0x41 推送侧使用）。</summary>
    private sealed record JoinDecision(Guid GroupId, Guid DeviceId);

    /// <summary>审批决议共享核（0x53 会话路径与 M3-05 Web 管理路径同语义；FR-S-305/822）：
    /// pending 定位 → 置态 → 批准入组（已成员不重复插行）→ 审计（actor=操作者，Web 侧 admin）。
    /// 返回 null=单不存在或已处理（诚实应答不抛错）。0x41 推送留在核外：0x53 路径须 Ack 先于推送。</summary>
    private async Task<JoinDecision?> ApplyJoinDecisionAsync(Guid requestId, bool approve,
        Guid? actorUserId = null, Guid? actorDeviceId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var request = await db.JoinRequests.SingleOrDefaultAsync(r =>
            r.Id == requestId && r.Status == "pending");
        if (request is null)
            return null;
        request.Status = approve ? "approved" : "rejected";
        request.HandledAt = _time.GetLocalNow().UtcDateTime;
        if (approve && !await db.GroupMembers.AnyAsync(m =>
                m.GroupId == request.GroupId && m.DeviceId == request.DeviceId))
            db.GroupMembers.Add(new GroupMember
            {
                Id = Guid.NewGuid(), GroupId = request.GroupId, DeviceId = request.DeviceId,
                Approved = true, JoinedAt = _time.GetLocalNow().UtcDateTime,
            });
        await db.SaveChangesAsync();
        await audit.WriteAsync(approve ? "group_join_approve" : "group_join_reject",
            actorDeviceId, userId: actorUserId,
            detail: new { request.GroupId, request.DeviceId, RequestId = request.Id });
        return new JoinDecision(request.GroupId, request.DeviceId);
    }

    /// <summary>批准决议的 0x41 组成员+申请人相关方推送（M2-10）。</summary>
    private async Task NotifyJoinApprovalAsync(JoinDecision decision)
    {
        if (pusher is not null)
            await pusher.NotifyGroupMembersAsync(decision.GroupId, decision.DeviceId);
    }

    /// <summary>Web 管理路径审批入口（M3-05）：决议共享核 + 批准即补 0x41 推送（无 Ack 帧）。
    /// 返回 false=单不存在或已处理。</summary>
    public async Task<bool> DecideJoinRequestAsync(Guid requestId, bool approve,
        Guid? actorUserId = null, Guid? actorDeviceId = null)
    {
        var decision = await ApplyJoinDecisionAsync(requestId, approve, actorUserId, actorDeviceId);
        if (decision is not null && approve)
            await NotifyJoinApprovalAsync(decision);
        return decision is not null;
    }

    // ── 0x54 邀请码生成/撤销（仅所有者；每分组至多一码=覆盖式；OQ-17）──

    public async Task HandleInviteGenAsync(ControlSession session, GroupInviteGen msg)
    {
        if (session.OwnerUserId is null)
        {
            await session.SendErrorAsync(ErrorCode.Unauthorized, "login_required");
            return;
        }
        var (forbidden, notFound) = await CheckOwnerAsync(session, msg.GroupId);
        if (notFound)
        {
            await session.SendErrorAsync(ErrorCode.GroupNotFound, "group_not_found");
            return;
        }
        if (forbidden)
        {
            await session.SendErrorAsync(ErrorCode.Forbidden, "not_group_owner");
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        var group = await db.Groups.SingleAsync(g => g.Id == msg.GroupId);
        if (msg.Revoke)
        {
            group.InviteCode = null;
            await db.SaveChangesAsync();
            await session.SendAsync(new GroupInviteGenAck(session.NextSeq(), session.ServerTimestamp(),
                MsgType.GroupInviteGen, true, null));
            await audit.WriteAsync("group_invite_revoke", session.DeviceId, userId: session.OwnerUserId,
                detail: new { msg.GroupId });
            return;
        }

        for (var attempt = 1; attempt <= InviteCodeMaxAttempts; attempt++)
        {
            var code = GenerateInviteCode();
            if (await db.Groups.AsNoTracking().AnyAsync(g => g.InviteCode == code))
                continue;
            group.InviteCode = code;
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex) && attempt >= InviteCodeMaxAttempts)
            {
                break; // 冲突重试耗尽（6 位 31 字符集 ≈ 8.9 亿空间，实际不可达）
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                continue; // 并发撞码：换码重试
            }
            await session.SendAsync(new GroupInviteGenAck(session.NextSeq(), session.ServerTimestamp(),
                MsgType.GroupInviteGen, true, code));
            // 审计不含码值本身（AI-17：准入凭据不入日志）
            await audit.WriteAsync("group_invite_gen", session.DeviceId, userId: session.OwnerUserId,
                detail: new { msg.GroupId });
            return;
        }
        await session.SendErrorAsync(ErrorCode.Conflict, "invite_code_exhausted");
    }

    // ── 0x55 编辑（所有者；Name/Policy null=不改）─────────────────────

    public async Task HandleUpdateAsync(ControlSession session, GroupUpdate msg)
    {
        if (session.OwnerUserId is null)
        {
            await session.SendErrorAsync(ErrorCode.Unauthorized, "login_required");
            return;
        }
        var (forbidden, notFound) = await CheckOwnerAsync(session, msg.GroupId);
        if (notFound)
        {
            await session.SendErrorAsync(ErrorCode.GroupNotFound, "group_not_found");
            return;
        }
        if (forbidden)
        {
            await session.SendErrorAsync(ErrorCode.Forbidden, "not_group_owner");
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        var query = db.Groups.Where(g => g.Id == msg.GroupId);
        if (msg.Name is not null)
            await query.ExecuteUpdateAsync(s => s.SetProperty(g => g.Name, msg.Name.Trim()));
        if (msg.Policy is not null)
            await query.ExecuteUpdateAsync(s => s.SetProperty(
                g => g.JoinPolicy, msg.Policy == JoinPolicy.Approval ? "approval" : "free"));
        await session.SendAsync(new GroupUpdateAck(session.NextSeq(), session.ServerTimestamp(),
            MsgType.GroupUpdate, true));
    }

    // ── 0x56 解散（所有者；默认分组不可解散；联动清理成员与申请）────────

    public async Task HandleDissolveAsync(ControlSession session, GroupDissolve msg)
    {
        if (session.OwnerUserId is null)
        {
            await session.SendErrorAsync(ErrorCode.Unauthorized, "login_required");
            return;
        }
        var (forbidden, notFound) = await CheckOwnerAsync(session, msg.GroupId);
        if (notFound)
        {
            await session.SendErrorAsync(ErrorCode.GroupNotFound, "group_not_found");
            return;
        }
        if (forbidden)
        {
            await session.SendErrorAsync(ErrorCode.Forbidden, "not_group_owner");
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        var group = await db.Groups.SingleAsync(g => g.Id == msg.GroupId);
        if (group.IsDefault)
        {
            await session.SendErrorAsync(ErrorCode.Conflict, "default_group_immutable");
            return;
        }
        // 成员清单先捕获（删除后无从查询，0x41 收件人与 0x75 反查候选来源）
        var members = pusher is null && invalidation is null ? [] : await db.GroupMembers.AsNoTracking()
            .Where(m => m.GroupId == group.Id).Select(m => m.DeviceId).ToListAsync();
        // 联动清理：成员与准入申请（映射授权为动态计算，可见性随之收缩，05 §5）
        await db.GroupMembers.Where(m => m.GroupId == group.Id).ExecuteDeleteAsync();
        await db.JoinRequests.Where(r => r.GroupId == group.Id).ExecuteDeleteAsync();
        await db.Groups.Where(g => g.Id == group.Id).ExecuteDeleteAsync();
        await session.SendAsync(new GroupDissolveAck(session.NextSeq(), session.ServerTimestamp(),
            MsgType.GroupDissolve, true));
        if (pusher is not null && members.Count > 0)
            await pusher.NotifyDevicesAsync(members); // 0x41（M2-10）
        // 0x75 group_dissolved（M2-12）：组内两两切断复核（同账号对保留）
        if (invalidation is not null && members.Count > 0)
            await invalidation.PushGroupDissolvedAsync(members);
    }

    // ── 0x57 所有者移出成员（FR-S-307）───────────────────────────────

    public async Task HandleRemoveMemberAsync(ControlSession session, GroupRemoveMember msg)
    {
        if (session.OwnerUserId is null)
        {
            await session.SendErrorAsync(ErrorCode.Unauthorized, "login_required");
            return;
        }
        var (forbidden, notFound) = await CheckOwnerAsync(session, msg.GroupId);
        if (notFound)
        {
            await session.SendErrorAsync(ErrorCode.GroupNotFound, "group_not_found");
            return;
        }
        if (forbidden)
        {
            await session.SendErrorAsync(ErrorCode.Forbidden, "not_group_owner");
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        var removed = await db.GroupMembers.Where(m =>
            m.GroupId == msg.GroupId && m.DeviceId == msg.MemberDeviceId).ExecuteDeleteAsync();
        // 移出后残留 pending 申请一并清理（该设备不再待审）
        await db.JoinRequests.Where(r => r.GroupId == msg.GroupId && r.DeviceId == msg.MemberDeviceId
            && r.Status == "pending").ExecuteDeleteAsync();
        await session.SendAsync(new GroupRemoveMemberAck(session.NextSeq(), session.ServerTimestamp(),
            MsgType.GroupRemoveMember, removed > 0));
        if (removed > 0)
        {
            await audit.WriteAsync("group_member_remove", session.DeviceId, userId: session.OwnerUserId,
                detail: new { msg.GroupId, msg.MemberDeviceId });
            if (pusher is not null)
                await pusher.NotifyGroupMembersAsync(msg.GroupId, msg.MemberDeviceId); // 0x41（M2-10）
            // 0x75 group_dissolved（M2-12，枚举口径：0x56 解散/0x57 移出同值——组关系终止）
            if (invalidation is not null)
                await invalidation.PushMemberSeveredAsync(msg.MemberDeviceId, InvalidationReason.GroupDissolved);
        }
    }

    // ── 0x40 设备列表分页（OQ-16）────────────────────────────────────

    public async Task HandleDeviceListAsync(ControlSession session, DeviceListRequest msg)
    {
        var offset = (int)Math.Min(msg.Offset, int.MaxValue);
        var limit = msg.Limit == 0 ? DefaultLimit : (int)Math.Min(msg.Limit, MaxLimit);

        await using var db = await dbFactory.CreateDbContextAsync();
        var visible = VisibleDevices(db, session);
        var total = await visible.CountAsync();

        var items = await visible
            .OrderBy(d => d.DeviceName).ThenBy(d => d.Id)
            .Skip(offset).Take(limit + 1) // 多取 1 判 hasMore
            .ToListAsync();
        var hasMore = items.Count > limit;
        if (hasMore) items.RemoveAt(items.Count - 1);

        // 批量摘要：分组与开放网段（列表项字段 PRD 06 §2）
        var pageIds = items.Select(d => d.Id).ToList();
        var groupsByDevice = (await db.GroupMembers.Where(m => pageIds.Contains(m.DeviceId))
                .Join(db.Groups, m => m.GroupId, g => g.Id, (m, g) => new { m.DeviceId, g.Name })
                .ToListAsync())
            .GroupBy(x => x.DeviceId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Name).Distinct().ToArray());
        var segmentsByDevice = (await db.LanSegments.Where(s => pageIds.Contains(s.DeviceId) && s.Enabled)
                .Select(s => new { s.DeviceId, s.Cidr }).ToListAsync())
            .GroupBy(x => x.DeviceId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Cidr).ToArray());

        var listItems = items.Select(d => new DeviceListItem(
                d.Id, d.DeviceName, d.RemoteCode, d.VirtualIp,
                registry.IsOnline(d.Id),
                groupsByDevice.GetValueOrDefault(d.Id, []),
                segmentsByDevice.GetValueOrDefault(d.Id, [])))
            .ToArray();

        await session.SendAsync(new DeviceListResponse(session.NextSeq(), session.ServerTimestamp(),
            MsgType.DeviceList, (uint)total, listItems, hasMore));
    }

    // ── 0x42 已加入分组列表（M2-27 定案，FR-C-805：本地 /api/groups 数据源）────

    /// <summary>本设备已加入分组全量（仅 approved 成员行）：groupId/名称/策略/所有者标志/成员数。
    /// 与 0x40 同口径——无需登录（成员资格是设备维度），passive 由主动类闸统一拒绝。</summary>
    public async Task HandleGroupListAsync(ControlSession session, GroupListRequest msg)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await (from m in db.GroupMembers
                          where m.DeviceId == session.DeviceId && m.Approved
                          join g in db.Groups on m.GroupId equals g.Id
                          orderby g.Name, g.Id
                          select new { g.Id, g.Name, g.JoinPolicy, g.OwnerUserId }).ToListAsync();
        var groupIds = rows.Select(r => r.Id).ToList();
        var counts = await db.GroupMembers
            .Where(m => groupIds.Contains(m.GroupId) && m.Approved)
            .GroupBy(m => m.GroupId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);

        await session.SendAsync(new GroupListResponse(session.NextSeq(), session.ServerTimestamp(),
            MsgType.GroupList, rows.Select(r => new GroupListItem(
                r.Id, r.Name,
                r.JoinPolicy == "approval" ? JoinPolicy.Approval : JoinPolicy.Free,
                session.OwnerUserId is not null && r.OwnerUserId == session.OwnerUserId,
                (uint)counts.GetValueOrDefault(r.Id))).ToArray()));
    }

    /// <summary>可见设备查询（与 Authorizer L2 同口径：本账号设备 ∪ 共同分组设备）。</summary>
    internal static IQueryable<Device> VisibleDevices(AppDbContext db, ControlSession session)
    {
        var myGroups = db.GroupMembers.Where(m => m.DeviceId == session.DeviceId).Select(m => m.GroupId);
        return db.Devices.Where(d =>
            (session.OwnerUserId != null && d.OwnerUserId == session.OwnerUserId)
            || db.GroupMembers.Any(m => m.DeviceId == d.Id && myGroups.Contains(m.GroupId)));
    }

    /// <summary>邀请码生成（加密随机；UNIQUE 冲突由调用方重试）。</summary>
    internal static string GenerateInviteCode()
    {
        Span<char> code = stackalloc char[InviteCodeLength];
        for (var i = 0; i < InviteCodeLength; i++)
            code[i] = InviteCharset[System.Security.Cryptography.RandomNumberGenerator.GetInt32(InviteCharset.Length)];
        return new string(code);
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 };

    private async Task<(bool Forbidden, bool NotFound)> CheckOwnerAsync(ControlSession session, Guid groupId)
    {
        var owner = session.OwnerUserId!.Value; // 调用方已做登录前置
        await using var db = await dbFactory.CreateDbContextAsync();
        var group = await db.Groups.AsNoTracking().SingleOrDefaultAsync(g => g.Id == groupId);
        return group is null ? (false, true) : (group.OwnerUserId != owner, false);
    }
}

using Microsoft.EntityFrameworkCore;
using P2P.Core.Protocol;
using P2P.Server.Data;

namespace P2P.Server.Services;

/// <summary>
/// 分组与设备列表（02 §2.4 0x50/0x55/0x56/0x40；FR-S-301/302/401、OQ-16）。
/// 0x40 可见性 = 本账号设备 ∪ 共同分组设备（与 L2 同口径，05 §5）。
/// 入组 0x51/邀请码/审批/移出成员（FR-S-303~307）→ M2。
/// </summary>
public sealed class GroupService(
    IDbContextFactory<AppDbContext> dbFactory,
    DeviceRegistry registry,
    TimeProvider? time = null)
{
    /// <summary>0x40 分页上限（OQ-16：默认 100，钳制区间 1~200）。</summary>
    public const int DefaultLimit = 100;
    public const int MaxLimit = 200;

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
        await db.SaveChangesAsync();
        await session.SendAsync(new GroupCreateAck(session.NextSeq(), session.ServerTimestamp(),
            MsgType.GroupCreate, group.Id));
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
        // 联动清理：成员与准入申请（映射授权为动态计算，可见性随之收缩，05 §5）
        await db.GroupMembers.Where(m => m.GroupId == group.Id).ExecuteDeleteAsync();
        await db.JoinRequests.Where(r => r.GroupId == group.Id).ExecuteDeleteAsync();
        await db.Groups.Where(g => g.Id == group.Id).ExecuteDeleteAsync();
        await session.SendAsync(new GroupDissolveAck(session.NextSeq(), session.ServerTimestamp(),
            MsgType.GroupDissolve, true));
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

    /// <summary>可见设备查询（与 Authorizer L2 同口径：本账号设备 ∪ 共同分组设备）。</summary>
    internal static IQueryable<Device> VisibleDevices(AppDbContext db, ControlSession session)
    {
        var myGroups = db.GroupMembers.Where(m => m.DeviceId == session.DeviceId).Select(m => m.GroupId);
        return db.Devices.Where(d =>
            (session.OwnerUserId != null && d.OwnerUserId == session.OwnerUserId)
            || db.GroupMembers.Any(m => m.DeviceId == d.Id && myGroups.Contains(m.GroupId)));
    }

    private async Task<(bool Forbidden, bool NotFound)> CheckOwnerAsync(ControlSession session, Guid groupId)
    {
        var owner = session.OwnerUserId!.Value; // 调用方已做登录前置
        await using var db = await dbFactory.CreateDbContextAsync();
        var group = await db.Groups.AsNoTracking().SingleOrDefaultAsync(g => g.Id == groupId);
        return group is null ? (false, true) : (group.OwnerUserId != owner, false);
    }
}

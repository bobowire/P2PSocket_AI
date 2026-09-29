using Microsoft.EntityFrameworkCore;
using P2P.Core.Protocol;
using P2P.Server.Data;

namespace P2P.Server.Services;

/// <summary>
/// 0x75 Invalidation 推送（M2-12，02 §2.4、PRD 05 §4/§5、FR-C-702）：
/// 按授权链反查受影响映射 → 按 owner 分组 → 在线推送（离线跳过——下次登录全量同步对齐；
/// 单收件人 try/catch 静默）。disabled 映射不入集（无转发可停；重启用 0x60/0x70 会再过授权链，
/// 届时拒绝即"提示重新配置"的自然执行点）。Ack 先于推送由调用方保证（M2-10 纪律）。
/// 触发点：logged_out←0x22、remote_code_reset←0x14、group_left←0x52、group_dissolved←0x56/0x57；
/// user_disabled/device_disabled←M2-13；lan_segment_removed←0x63（LanSegmentService 内联，口径一致）。
/// </summary>
public sealed class InvalidationPusher(
    IDbContextFactory<AppDbContext> dbFactory, DeviceRegistry registry)
{
    /// <summary>登出（L1 失效）：本人 enabled 映射全部失效（PRD 05 §4"任意一层失效"）。</summary>
    public async Task PushOwnedAsync(Guid ownerDeviceId, InvalidationReason reason)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var ids = await db.Mappings.AsNoTracking()
            .Where(m => m.OwnerDeviceId == ownerDeviceId && m.Enabled)
            .Select(m => m.Id)
            .ToArrayAsync();
        if (ids.Length == 0)
            return;
        await PushByOwnerAsync(new Dictionary<Guid, Guid[]> { [ownerDeviceId] = ids }, reason);
    }

    /// <summary>远程码重置（0x14，PRD 05 §5）：引用该设备的存量映射失效——码是定位别名（D7），
    /// 服务端以 TargetDeviceId 反查（无法也不必区分"经码创建"；保守全量失效+提示重新配置）。</summary>
    public async Task PushTargetingAsync(Guid targetDeviceId, InvalidationReason reason)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var byOwner = await db.Mappings.AsNoTracking()
            .Where(m => m.TargetDeviceId == targetDeviceId && m.Enabled)
            .GroupBy(m => m.OwnerDeviceId)
            .ToDictionaryAsync(g => g.Key, g => g.Select(m => m.Id).ToArray());
        await PushByOwnerAsync(byOwner, reason);
    }

    /// <summary>组关系单边切断（0x52 自退 / 0x57 移出）：候选=切断边任一端为离开者的映射，
    /// 复核切断后残余可见性——同账号或另共同组仍在则保留（只有真失效的入集）。</summary>
    public async Task PushMemberSeveredAsync(Guid leaverDeviceId, InvalidationReason reason)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var candidates = await db.Mappings.AsNoTracking()
            .Where(m => m.Enabled && m.OwnerDeviceId != m.TargetDeviceId
                && (m.OwnerDeviceId == leaverDeviceId || m.TargetDeviceId == leaverDeviceId))
            .Select(m => new { m.OwnerDeviceId, m.Id, m.TargetDeviceId })
            .ToListAsync();
        await PushRecheckedAsync(db, candidates.Select(m => (m.OwnerDeviceId, m.Id, m.TargetDeviceId)), reason);
    }

    /// <summary>解散（0x56）：候选=两端均为原成员的映射（成员清单由调用方在删行前捕获），
    /// 复核残余可见性（同账号对保留）。</summary>
    public async Task PushGroupDissolvedAsync(IReadOnlyCollection<Guid> memberIds)
    {
        if (memberIds.Count == 0)
            return;
        await using var db = await dbFactory.CreateDbContextAsync();
        var candidates = await db.Mappings.AsNoTracking()
            .Where(m => m.Enabled && m.OwnerDeviceId != m.TargetDeviceId
                && memberIds.Contains(m.OwnerDeviceId) && memberIds.Contains(m.TargetDeviceId))
            .Select(m => new { m.OwnerDeviceId, m.Id, m.TargetDeviceId })
            .ToListAsync();
        await PushRecheckedAsync(db, candidates.Select(m => (m.OwnerDeviceId, m.Id, m.TargetDeviceId)),
            InvalidationReason.GroupDissolved);
    }

    /// <summary>逐 owner 推送（已算好的集合）。</summary>
    public async Task PushByOwnerAsync(IReadOnlyDictionary<Guid, Guid[]> byOwner, InvalidationReason reason,
        CapabilityMode? newCapability = null)
    {
        foreach (var (ownerId, ids) in byOwner)
        {
            if (ids.Length == 0 || registry.TryGet(ownerId) is not { } session)
                continue; // 离线跳过（提示帧语义）
            try
            {
                await session.PushAsync(new Invalidation(session.NextSeq(), session.ServerTimestamp(),
                    MsgType.Invalidation, reason, ids, newCapability));
            }
            catch { /* 单收件人静默 */ }
        }
    }

    /// <summary>复核推送：切断后目标仍对 owner 可见的映射保留，其余按 owner 分组推送。</summary>
    private async Task PushRecheckedAsync(AppDbContext db,
        IEnumerable<(Guid OwnerDeviceId, Guid MappingId, Guid TargetDeviceId)> candidates,
        InvalidationReason reason)
    {
        var byOwner = new Dictionary<Guid, List<Guid>>();
        foreach (var (ownerId, mappingId, targetId) in candidates)
        {
            if (await IsStillVisibleAsync(db, ownerId, targetId))
                continue; // 残余可见性仍在（同账号/另共同组）：映射不受影响
            if (!byOwner.TryGetValue(ownerId, out var list))
                byOwner[ownerId] = list = [];
            list.Add(mappingId);
        }
        await PushByOwnerAsync(byOwner.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()), reason);
    }

    /// <summary>会话无关可见性复核（与 <see cref="GroupService.VisibleDevices"/> 同口径：
    /// 同账号 ∪ 共同分组）。调用时点=组关系变更已提交后。</summary>
    internal static async Task<bool> IsStillVisibleAsync(AppDbContext db, Guid ownerDeviceId, Guid targetDeviceId)
    {
        var ownerUserId = await db.Devices.AsNoTracking()
            .Where(d => d.Id == ownerDeviceId).Select(d => d.OwnerUserId).SingleOrDefaultAsync();
        if (ownerUserId is null)
            return false; // owner 行消失（解绑竞态）：保守失效
        var myGroups = db.GroupMembers.Where(m => m.DeviceId == ownerDeviceId).Select(m => m.GroupId);
        return await db.Devices.AnyAsync(d => d.Id == targetDeviceId
            && (d.OwnerUserId == ownerUserId
                || db.GroupMembers.Any(m => m.DeviceId == d.Id && myGroups.Contains(m.GroupId))));
    }
}

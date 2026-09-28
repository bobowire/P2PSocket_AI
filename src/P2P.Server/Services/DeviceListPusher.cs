using Microsoft.EntityFrameworkCore;
using P2P.Core.Protocol;
using P2P.Server.Data;

namespace P2P.Server.Services;

/// <summary>
/// 0x41 DeviceListUpdate 推送（02 §2.4、FR-S-403、TD-16：提示帧非真相，客户端据此 refetch 0x40）。
/// 触发点：分组成员变更（入组/批准/退组/移出/解散，GroupService 调用）与在线状态变更
/// （DeviceRegistry 事件订阅）；远程码重置触发归 M2-12（0x14 处理器调用）。
/// 收件人=变更波及的在线设备（同账号 ∪ 共同分组，与 0x40 可见性同口径）；哑节点不下发；
/// 单收件人推送失败静默忽略（提示帧语义，客户端周期轮询兜底）。
/// </summary>
public sealed class DeviceListPusher
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly DeviceRegistry _registry;

    public DeviceListPusher(IDbContextFactory<AppDbContext> dbFactory, DeviceRegistry registry)
    {
        _dbFactory = dbFactory;
        _registry = registry;
        _registry.DeviceOnline += OnPresenceChanged;   // 注册即订阅（单例生命周期，不退订）
        _registry.DeviceOffline += OnPresenceChanged;
    }

    private void OnPresenceChanged(Guid deviceId)
    {
        // fire-and-forget：事件源自会话读循环，推送不得阻塞在线判定
        _ = SafeNotifyAsync(() => NotifyPresenceAsync(deviceId));
    }

    private static async Task SafeNotifyAsync(Func<Task> notify)
    {
        try { await notify(); } catch { /* 推送失败可容忍（见类注记） */ }
    }

    /// <summary>设备在线/离线 → 通知能看见它的在线设备（同账号 ∪ 共同分组，不含自身）。</summary>
    public async Task NotifyPresenceAsync(Guid deviceId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var recipients = await ResolveAccountPeersAsync(db, deviceId);
        await PushAsync(recipients);
    }

    /// <summary>分组成员变更 → 通知组内在线成员 ∪ 变更涉及设备的同账号在线设备。</summary>
    public async Task NotifyGroupMembersAsync(Guid groupId, Guid changedDeviceId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var members = await db.GroupMembers.AsNoTracking()
            .Where(m => m.GroupId == groupId).Select(m => m.DeviceId).ToListAsync();
        members.AddRange(await ResolveAccountPeersAsync(db, changedDeviceId));
        await PushAsync(members);
    }

    /// <summary>指定设备集合的列表已变（解散等成员行已删的场景：调用方先捕获成员清单）。</summary>
    public Task NotifyDevicesAsync(IReadOnlyCollection<Guid> deviceIds) => PushAsync(deviceIds);

    /// <summary>同账号设备 ∪ 共同分组设备（≠ 自身）——与 VisibleDevices 同口径的逆向查询。</summary>
    internal static async Task<List<Guid>> ResolveAccountPeersAsync(AppDbContext db, Guid deviceId)
    {
        var owner = await db.Devices.AsNoTracking()
            .Where(d => d.Id == deviceId).Select(d => d.OwnerUserId).SingleOrDefaultAsync();
        var myGroups = db.GroupMembers.Where(m => m.DeviceId == deviceId).Select(m => m.GroupId);
        var peers = await db.GroupMembers.AsNoTracking()
            .Where(m => myGroups.Contains(m.GroupId) && m.DeviceId != deviceId)
            .Select(m => m.DeviceId).Distinct().ToListAsync();
        if (owner is { } userId)
            peers.AddRange(await db.Devices.AsNoTracking()
                .Where(d => d.OwnerUserId == userId && d.Id != deviceId)
                .Select(d => d.Id).ToListAsync());
        return peers.Distinct().ToList(); // 并集去重（同账号且同组设备双路出现）
    }

    private async Task PushAsync(IReadOnlyCollection<Guid> recipientIds)
    {
        foreach (var id in recipientIds.Distinct())
        {
            var session = _registry.TryGet(id);
            if (session is null || session.Capability == CapabilityMode.Passive)
                continue; // 离线跳过；哑节点不下发（02 §2.4）
            try
            {
                await session.PushAsync(new DeviceListUpdate(
                    session.NextSeq(), session.ServerTimestamp(), MsgType.DeviceListUpdate));
            }
            catch { /* 连接关闭竞态等：该收件人跳过 */ }
        }
    }
}

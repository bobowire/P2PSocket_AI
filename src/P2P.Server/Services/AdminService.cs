using Microsoft.EntityFrameworkCore;
using P2P.Core.Protocol;
using P2P.Server.Data;

namespace P2P.Server.Services;

/// <summary>
/// 管理操作统一执行点（M2-13，FR-S-105/204/103、05 §5）：CLI 启动参数（Program 壳）与测试直调共用；
/// Web 操作载体 M3 复用同机制（FR-S-821/823）。
/// 设备禁用：置位 → 引用方 0x75(device_disabled)（先推）→ 踢线（后断）；读侧拒绝=Hello/注册恢复/登录/STUN 四处查库即时生效。
/// 用户禁用：置位 → 名下在线设备即时降级 passive + 0x75(user_disabled, newCapability=passive)，不踢线（FR-S-204 哑节点）。
/// 解绑：对齐 0x12 自助解绑清理集合删行，同 MAC 重注册=全新身份（FR-S-103 收口：4004 在线拒绝后的管理员出口）。
/// CLI 为独立进程（触不到运行中服务器的内存注册表）：写库+审计即时生效，踢线/降级/0x75 由
/// PresenceMonitor 心跳兜底在 ~30s 窗口内补齐。
/// </summary>
public sealed class AdminService(
    IDbContextFactory<AppDbContext> dbFactory,
    DeviceRegistry registry,
    AuditLogger audit,
    InvalidationPusher? invalidation = null)
{
    /// <summary>禁用设备（macCode 定位）：置位 → 引用方 0x75 → 踢线 → 审计。已禁用幂等成功。</summary>
    public async Task<bool> DisableDeviceAsync(string macCode)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var device = await db.Devices.SingleOrDefaultAsync(d => d.MacCode == macCode);
        if (device is null)
            return false;
        if (device.Disabled)
            return true; // 幂等
        device.Disabled = true;
        await db.SaveChangesAsync();

        // 先推后断：引用方提示帧先入发送队列，连接随后关闭
        if (invalidation is not null)
            await invalidation.PushTargetingAsync(device.Id, InvalidationReason.DeviceDisabled);
        if (registry.TryGet(device.Id) is { } session)
            await session.CloseAsync("device_disabled");
        await audit.WriteAsync("device_disable", device.Id, detail: new { device.MacCode });
        return true;
    }

    /// <summary>启用设备：清位 + 审计（在线恢复靠设备重连 Hello，正常走 AwaitingProof）。</summary>
    public async Task<bool> EnableDeviceAsync(string macCode)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var device = await db.Devices.SingleOrDefaultAsync(d => d.MacCode == macCode);
        if (device is null)
            return false;
        device.Disabled = false;
        await db.SaveChangesAsync();
        await audit.WriteAsync("device_enable", device.Id, detail: new { device.MacCode });
        return true;
    }

    /// <summary>禁用用户（username 定位）：置位 → 名下在线设备降级 passive + 0x75(user_disabled) → 审计。
    /// 不踢线（哑节点继续被动同步；重新登录自然拒绝——登录读侧查 user.Disabled）。</summary>
    public async Task<bool> DisableUserAsync(string username)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Username == username);
        if (user is null)
            return false;
        if (!user.Disabled)
        {
            user.Disabled = true;
            await db.SaveChangesAsync();
        }

        var deviceIds = await db.Devices.AsNoTracking()
            .Where(d => d.OwnerUserId == user.Id)
            .Select(d => d.Id)
            .ToArrayAsync();
        foreach (var deviceId in deviceIds)
        {
            if (registry.TryGet(deviceId) is not { } session)
                continue; // 离线：下次登录读侧拒绝
            session.Capability = CapabilityMode.Passive;
            if (invalidation is not null)
                await invalidation.PushOwnedAsync(deviceId, InvalidationReason.UserDisabled,
                    newCapability: CapabilityMode.Passive);
        }
        await audit.WriteAsync("user_disable", userId: user.Id, detail: new { user.Username });
        return true;
    }

    /// <summary>启用用户：清位 + 审计。不自动恢复在线会话能力（被动态保持，设备重新登录即恢复 normal）。</summary>
    public async Task<bool> EnableUserAsync(string username)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Username == username);
        if (user is null)
            return false;
        user.Disabled = false;
        await db.SaveChangesAsync();
        await audit.WriteAsync("user_enable", userId: user.Id, detail: new { user.Username });
        return true;
    }

    /// <summary>管理员解绑（macCode 定位；FR-S-103 收口）：在线先踢 → 删除设备与关联行
    /// （清理集合对齐 0x12 自助解绑）→ 审计。同 MAC 重注册=全新身份（新 deviceId/远程码/凭据）。</summary>
    public async Task<bool> UnbindDeviceAsync(string macCode)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var device = await db.Devices.SingleOrDefaultAsync(d => d.MacCode == macCode);
        if (device is null)
            return false;
        var deviceId = device.Id;

        if (registry.TryGet(deviceId) is { } session)
            await session.CloseAsync("unbind_admin"); // 4004 挡自助路径后的强制出口：在线也解

        db.GroupMembers.RemoveRange(db.GroupMembers.Where(m => m.DeviceId == deviceId));
        db.LanSegments.RemoveRange(db.LanSegments.Where(s => s.DeviceId == deviceId));
        db.JoinRequests.RemoveRange(db.JoinRequests.Where(r => r.DeviceId == deviceId));
        var mappingIds = db.Mappings
            .Where(m => m.OwnerDeviceId == deviceId || m.TargetDeviceId == deviceId)
            .Select(m => m.Id).ToList();
        db.MappingStats.RemoveRange(db.MappingStats.Where(s => mappingIds.Contains(s.MappingId)));
        db.Mappings.RemoveRange(db.Mappings.Where(m => mappingIds.Contains(m.Id)));
        db.PunchStats.RemoveRange(db.PunchStats
            .Where(p => p.InitiatorId == deviceId || p.TargetId == deviceId));
        db.Devices.Remove(device);
        await db.SaveChangesAsync();
        await audit.WriteAsync("unbind_admin", deviceId, detail: new { device.MacCode });
        return true;
    }
}

using Microsoft.EntityFrameworkCore;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using P2P.Server.Data;

namespace P2P.Server.Services;

/// <summary>
/// 注册族处理（02 §2.4 0x10/0x12/0x14；FR-S-101/102/104/106/903）：
/// 新建设备（ECIES 下发 deviceSecret、固定 .2、默认分组入组）；
/// macCode 离线命中 → 覆盖式恢复（OQ-14：保留 deviceId/远程码/归属/分组，存量映射不失效）；
/// 在线命中 → 4004 DEVICE_ACTIVE；解绑删除设备并清理关联行；
/// 0x14 远程码重置（M2-12，passive 允许——本机管理类）：生成新码、旧码立即失效（PRD 05 §5），
/// 引用该设备的存量映射 0x75 失效提示重新配置，可见相关方 0x41 刷新列表（SEC-25）。
/// </summary>
public sealed class RegistrationService(
    IDbContextFactory<AppDbContext> dbFactory,
    DeviceRegistry registry,
    AuditLogger audit,
    TimeProvider? time = null,
    InvalidationPusher? invalidation = null,
    DeviceListPusher? listPusher = null)
{
    /// <summary>统一下发虚拟 IP 固定 .2（OQ-13：单机自连通场景无需按位分配）。</summary>
    public const string VirtualIp = "100.64.0.2";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task HandleRegisterAsync(ControlSession session, Register msg)
    {
        if (session.State != ControlSession.SessionState.NeedRegister)
        {
            // 已建立会话重复注册无意义（凭据已持有）
            await session.SendErrorAsync(ErrorCode.BadRequest, "register_in_wrong_state");
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        var now = _time.GetLocalNow().UtcDateTime;
        var existing = await db.Devices.AsNoTracking()
            .SingleOrDefaultAsync(d => d.MacCode == msg.MacCode);

        if (existing is not null)
        {
            if (registry.IsOnline(existing.Id))
            {
                // 防伪造 MAC 抢注（OQ-14）：在线记录拒绝，需管理员解绑
                await session.SendErrorAsync(ErrorCode.DeviceActive, "device_active");
                await session.CloseAsync("device_active");
                return;
            }

            // 覆盖式凭据恢复：重签 deviceSecret、更新 staticPubKey；保留 deviceId/远程码/归属/分组
            var recoveredSecret = RandomGenerator.Bytes(32);
            existing.DeviceSecret = recoveredSecret;
            existing.StaticPubKey = msg.StaticPubKey;
            existing.DeviceName = msg.Hostname;
            existing.Os = msg.Os;
            existing.ClientVersion = msg.ClientVersion;
            existing.LastSeenAt = now;
            db.Devices.Update(existing);
            await db.SaveChangesAsync();
            await audit.WriteAsync("register_recover", existing.Id,
                detail: new { existing.MacCode, existing.RemoteCode });

            await SendRegisterAckAsync(session, db, msg, existing, recoveredSecret);
            session.CompleteRegistration(existing.Id, recoveredSecret);
            return;
        }

        // 新建设备
        var device = new Device
        {
            Id = Guid.NewGuid(),
            DeviceName = msg.Hostname,
            Os = msg.Os,
            ClientVersion = msg.ClientVersion,
            MacCode = msg.MacCode,
            RemoteCode = await new RemoteCodeGenerator(db).GenerateAsync(),
            VirtualIp = VirtualIp,
            StaticPubKey = msg.StaticPubKey,
            DeviceSecret = RandomGenerator.Bytes(32),
            LastSeenAt = now,
            CreatedAt = now,
        };
        db.Devices.Add(device);

        // 默认分组入组（03 §6 种子保证存在；free 策略即入）
        var defaultGroup = await db.Groups.AsNoTracking().SingleAsync(g => g.IsDefault);
        db.GroupMembers.Add(new GroupMember
        {
            Id = Guid.NewGuid(),
            GroupId = defaultGroup.Id,
            DeviceId = device.Id,
            Approved = true,
            JoinedAt = now,
        });
        await db.SaveChangesAsync();
        await audit.WriteAsync("register", device.Id, detail: new { device.MacCode, device.RemoteCode });

        await SendRegisterAckAsync(session, db, msg, device, device.DeviceSecret);
        session.CompleteRegistration(device.Id, device.DeviceSecret);
    }

    /// <summary>0x11：ECIES(staticPubKey) 下发 deviceSecret + 设备信息 + 分组列表。</summary>
    private static async Task SendRegisterAckAsync(ControlSession session, AppDbContext db,
        Register msg, Device device, byte[] secret)
    {
        var groups = await db.GroupMembers.AsNoTracking()
            .Where(m => m.DeviceId == device.Id)
            .Join(db.Groups, m => m.GroupId, g => g.Id, (m, g) => new GroupInfo(g.Id, g.Name))
            .ToArrayAsync();

        var ack = new RegisterAck(session.NextSeq(), session.ServerTimestamp(), MsgType.RegisterAck,
            device.Id, Ecies.Encrypt(msg.StaticPubKey, secret),
            device.RemoteCode, device.VirtualIp, groups);
        await session.SendAsync(ack);
    }

    /// <summary>
    /// 0x12 解绑（本机重置；凭连接级设备身份认证）：删除设备及关联行（分组/网段/映射/统计），断连。
    /// </summary>
    public async Task HandleUnbindAsync(ControlSession session, UnbindMe msg)
    {
        if (!session.IsEstablished)
        {
            await session.SendErrorAsync(ErrorCode.Unauthorized, "unbind_requires_session");
            return;
        }
        var deviceId = session.DeviceId;

        await using var db = await dbFactory.CreateDbContextAsync();
        var device = await db.Devices.SingleOrDefaultAsync(d => d.Id == deviceId);
        if (device is null)
        {
            await session.CloseAsync("unbind_unknown_device");
            return;
        }

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
        await audit.WriteAsync("unbind", deviceId, detail: new { device.MacCode });

        await session.CloseAsync("unbound");
    }

    /// <summary>
    /// 0x14 远程码重置（M2-12，FR-S-903/SEC-25；passive 允许——本机管理类）：凭连接级设备身份，
    /// 无附加载荷。生成新码（OQ-8 生成器）、旧码立即失效（唯一索引换值即不可解析 → 4003）；
    /// Ack 后：引用该设备的存量映射 0x75(remote_code_reset) 提示重新配置 → 可见相关方
    /// 0x41 刷新设备列表（新码）。审计不含码值（AI-17 准入凭据不入日志）。
    /// </summary>
    public async Task HandleRemoteCodeResetAsync(ControlSession session, RemoteCodeReset msg)
    {
        if (!session.IsEstablished)
        {
            await session.SendErrorAsync(ErrorCode.Unauthorized, "reset_requires_session");
            return;
        }
        await using var db = await dbFactory.CreateDbContextAsync();
        var device = await db.Devices.SingleOrDefaultAsync(d => d.Id == session.DeviceId);
        if (device is null)
        {
            await session.CloseAsync("reset_unknown_device"); // 解绑竞态
            return;
        }

        var newCode = await new RemoteCodeGenerator(db).GenerateAsync();
        device.RemoteCode = newCode;
        await db.SaveChangesAsync();
        await audit.WriteAsync("remote_code_reset", session.DeviceId);
        await session.SendAsync(new RemoteCodeResetAck(session.NextSeq(), session.ServerTimestamp(),
            MsgType.RemoteCodeReset, true, newCode));

        // 0x75：引用本设备（旧码）的存量映射失效（PRD 05 §5）；0x41：可见相关方刷新列表
        if (invalidation is not null)
            await invalidation.PushTargetingAsync(session.DeviceId, InvalidationReason.RemoteCodeReset);
        if (listPusher is not null)
            await listPusher.NotifyDevicesAsync(
                await DeviceListPusher.ResolveAccountPeersAsync(db, session.DeviceId));
    }
}

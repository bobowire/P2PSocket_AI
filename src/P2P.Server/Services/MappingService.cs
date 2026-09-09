using Microsoft.EntityFrameworkCore;
using P2P.Core.Protocol;
using P2P.Server.Data;

namespace P2P.Server.Services;

/// <summary>
/// 映射 CRUD 同步（02 §2.4 0x60/0x61，FR-S-801 全量同步至服务端存储）：
/// 字段=PRD 06 §2；L2 授权校验（SEC-52——目标 ∈ 本账号 ∪ 共同分组，与 0x40/0x70 同口径，
/// 失败写 mapping_deny 审计）；唯一约束 owner+proto+localPort（03 §2.4）。
/// L3 targetAddr/lan_segments 白名单 → M2（白名单未开放）；0x62 状态上报 → M2（FR-C-404），
/// M1 映射状态经客户端本地 WS 展示。passive 拦截在路由入口（0x60 属主动类，02 §2.5）。
/// </summary>
public sealed class MappingService(IDbContextFactory<AppDbContext> dbFactory, AuditLogger audit)
{
    /// <summary>名称上限（PRD 06 §2 用户自定义未定长；取 64，客户端本地 API 同口径）。</summary>
    public const int NameMaxLength = 64;

    public async Task HandleUpsertAsync(ControlSession session, MappingUpsert msg)
    {
        var name = msg.Name?.Trim() ?? "";
        if (name.Length is < 1 or > NameMaxLength
            || msg.LocalPort is 0
            || msg.TargetPort is 0
            || (msg.Proto != "tcp" && msg.Proto != "udp") // D19
            || string.IsNullOrWhiteSpace(msg.TargetRemoteCode)
            || string.IsNullOrWhiteSpace(msg.TargetAddr))
        {
            await session.SendErrorAsync(ErrorCode.BadRequest, "bad_mapping_fields");
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync();

        // 目标解析：先判码存在性（4003）再判可见性（4001），错误语义分开
        var target = await db.Devices.FirstOrDefaultAsync(d => d.RemoteCode == msg.TargetRemoteCode);
        if (target is null)
        {
            await session.SendErrorAsync(ErrorCode.RemoteCodeInvalid, "remote_code_not_found");
            return;
        }
        var visible = await GroupService.VisibleDevices(db, session).AnyAsync(d => d.Id == target.Id);
        if (!visible)
        {
            // 先审计后回错（与 punch_deny 同口径：客户端收到 4xx 时记录必然已落库）
            await audit.WriteAsync("mapping_deny", session.DeviceId,
                detail: new { reason = "l2_not_visible", targetDeviceId = target.Id });
            await session.SendErrorAsync(ErrorCode.TargetNotAuthorized, "l2_not_visible");
            return;
        }

        Guid mappingId;
        if (msg.MappingId is { } editId)
        {
            var existing = await db.Mappings.FirstOrDefaultAsync(m => m.Id == editId);
            if (existing is null || existing.OwnerDeviceId != session.DeviceId)
            {
                await session.SendErrorAsync(ErrorCode.NotFound, "mapping_not_found");
                return;
            }
            // 04 §2.5：启用状态下不允许改本地端口/协议（与本地监听一致性；名称/目标可改）
            if (existing.Enabled
                && (existing.LocalPort != msg.LocalPort || existing.Proto != msg.Proto))
            {
                await session.SendErrorAsync(ErrorCode.Conflict, "port_change_requires_disabled");
                return;
            }
            if (await PortTakenAsync(db, session.DeviceId, msg.Proto, msg.LocalPort, editId))
            {
                await session.SendErrorAsync(ErrorCode.Conflict, "local_port_in_use");
                return;
            }
            existing.Name = name;
            existing.LocalPort = msg.LocalPort;
            existing.Proto = msg.Proto;
            existing.TargetDeviceId = target.Id;
            existing.TargetAddr = msg.TargetAddr.Trim();
            existing.TargetPort = msg.TargetPort;
            existing.Enabled = msg.Enabled;
            mappingId = editId;
        }
        else
        {
            if (await PortTakenAsync(db, session.DeviceId, msg.Proto, msg.LocalPort, null))
            {
                await session.SendErrorAsync(ErrorCode.Conflict, "local_port_in_use");
                return;
            }
            mappingId = Guid.NewGuid();
            db.Mappings.Add(new Mapping
            {
                Id = mappingId,
                OwnerDeviceId = session.DeviceId,
                Name = name,
                LocalPort = msg.LocalPort,
                Proto = msg.Proto,
                TargetDeviceId = target.Id,
                TargetAddr = msg.TargetAddr.Trim(),
                TargetPort = msg.TargetPort,
                Enabled = msg.Enabled,
                CreatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
        await session.SendAsync(new MappingUpsertAck(session.NextSeq(), session.ServerTimestamp(),
            MsgType.MappingUpsert, mappingId));
    }

    public async Task HandleDeleteAsync(ControlSession session, MappingDelete msg)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        // 仅本人映射可删；不存在 → Ack Ok=false（幂等删除语义，不发 Error）
        var existing = await db.Mappings.FirstOrDefaultAsync(m =>
            m.Id == msg.MappingId && m.OwnerDeviceId == session.DeviceId);
        var ok = existing is not null;
        if (ok)
        {
            db.Mappings.Remove(existing!); // MappingStats 级联（03 §2.6 ON DELETE CASCADE）
            await db.SaveChangesAsync();
        }
        await session.SendAsync(new MappingDeleteAck(session.NextSeq(), session.ServerTimestamp(),
            MsgType.MappingDelete, ok));
    }

    /// <summary>唯一约束 owner+proto+localPort（03 §2.4）应用层预判（excludedId=更新场景排除自身）。</summary>
    private static Task<bool> PortTakenAsync(AppDbContext db, Guid owner, string proto, int localPort, Guid? excludedId)
        => db.Mappings.AnyAsync(m => m.OwnerDeviceId == owner && m.Proto == proto
            && m.LocalPort == localPort && (excludedId == null || m.Id != excludedId));
}

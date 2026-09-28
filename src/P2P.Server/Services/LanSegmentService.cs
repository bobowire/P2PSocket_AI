using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using P2P.Core.Protocol;
using P2P.Server.Data;

namespace P2P.Server.Services;

/// <summary>
/// 0x63 内网段白名单（M2-11，FR-C-701/702、05 §2.5、SEC-53）：
/// - 三分支语义：SegmentId=null 新建（须 Enabled=true）；id+Enabled=true 更新 Cidr；id+Enabled=false 移除；
/// - CIDR 校验 .NET IPNetwork.TryParse，裸 IP 补 /32（IPv6 /128）规范化入库；
/// - passive 允许（本机管理类，02 §2.5 不在主动类清单）；
/// - 移除联动：算引用该段的存量 enabled 映射（TargetDeviceId=本机 && TargetAddr≠self && CIDR 覆盖，
///   SQLite 无法 SQL 内做 CIDR 数学 → 内存过滤，同 Authorizer.L3 口径）→ 删行 → 审计 → Ack →
///   在线 owner 推 0x75(lan_segment_removed, affectedMappingIds)（客户端置 invalid 停转发）。
/// 更新（扩大覆盖）不触发 0x75：覆盖面只增不减，存量映射天然仍被覆盖。
/// </summary>
public sealed class LanSegmentService(
    IDbContextFactory<AppDbContext> dbFactory, DeviceRegistry registry, AuditLogger audit)
{
    public async Task HandleUpsertAsync(ControlSession session, LanSegmentsUpsert msg)
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        // 移除语义：Enabled=false + SegmentId → 失效联动 + 删行（先算受影响映射再删）。
        // Cidr 字段本分支不消费（协议容错：移除请求无须回带原值）
        if (msg.SegmentId is { } removeId && !msg.Enabled)
        {
            var existing = await db.LanSegments.FirstOrDefaultAsync(s =>
                s.Id == removeId && s.DeviceId == session.DeviceId);
            if (existing is null)
            {
                await session.SendErrorAsync(ErrorCode.NotFound, "segment_not_found");
                return;
            }

            var affectedIds = await FindAffectedMappingsAsync(db, session.DeviceId, existing.Cidr);
            db.LanSegments.Remove(existing);
            await db.SaveChangesAsync();

            await audit.WriteAsync("lan_segment_remove", session.DeviceId,
                detail: new { segmentId = removeId, cidr = existing.Cidr, affectedMappings = affectedIds.Length });
            await session.SendAsync(new LanSegmentsUpsertAck(session.NextSeq(), session.ServerTimestamp(),
                MsgType.LanSegmentsUpsert, removeId));

            // 0x75 失效推送（FR-C-702）：Ack 先于推送（M2-10 纪律）；owner 即上报者本人，离线不可能
            // （正在处理其消息）——TryGet 兜底跳过；单收件人失败静默（提示帧语义，客户端可 0x63 重查）
            if (affectedIds.Length > 0 && registry.TryGet(session.DeviceId) is { } owner)
            {
                try
                {
                    await owner.PushAsync(new Invalidation(session.NextSeq(), session.ServerTimestamp(),
                        MsgType.Invalidation, InvalidationReason.LanSegmentRemoved, affectedIds, null));
                }
                catch { /* 推送失败不回滚删除：白名单已收口，下次登录全量同步对齐 */ }
            }
            return;
        }

        // 新建：SegmentId=null（须 Enabled=true——null+false 无对应操作语义，1001）
        if (msg.SegmentId is null)
        {
            if (!msg.Enabled)
            {
                await session.SendErrorAsync(ErrorCode.BadRequest, "bad_segment_fields");
                return;
            }
            var cidr = NormalizeCidr(msg.Cidr?.Trim() ?? "");
            if (cidr is null)
            {
                await session.SendErrorAsync(ErrorCode.BadRequest, "bad_cidr");
                return;
            }
            var segmentId = Guid.NewGuid();
            db.LanSegments.Add(new LanSegment
            {
                Id = segmentId,
                DeviceId = session.DeviceId,
                Cidr = cidr,
                Enabled = true,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            await audit.WriteAsync("lan_segment_upsert", session.DeviceId,
                detail: new { segmentId, cidr, action = "create" });
            await session.SendAsync(new LanSegmentsUpsertAck(session.NextSeq(), session.ServerTimestamp(),
                MsgType.LanSegmentsUpsert, segmentId));
            return;
        }

        // 更新：SegmentId + Enabled=true → 改 Cidr（归属本人）
        var target = await db.LanSegments.FirstOrDefaultAsync(s =>
            s.Id == msg.SegmentId && s.DeviceId == session.DeviceId);
        if (target is null)
        {
            await session.SendErrorAsync(ErrorCode.NotFound, "segment_not_found");
            return;
        }
        var normalized = NormalizeCidr(msg.Cidr?.Trim() ?? "");
        if (normalized is null)
        {
            await session.SendErrorAsync(ErrorCode.BadRequest, "bad_cidr");
            return;
        }
        target.Cidr = normalized;
        await db.SaveChangesAsync();
        await audit.WriteAsync("lan_segment_upsert", session.DeviceId,
            detail: new { segmentId = msg.SegmentId.Value, cidr = normalized, action = "update" });
        await session.SendAsync(new LanSegmentsUpsertAck(session.NextSeq(), session.ServerTimestamp(),
            MsgType.LanSegmentsUpsert, msg.SegmentId.Value));
    }

    /// <summary>引用段的存量映射（02 §2.4 0x75 联动口径）：目标=本机、enabled、非 self 且被段 CIDR 覆盖。
    /// disabled 映射不入集：重启用 0x60 会再过 L3 校验，届时无段自然拒绝（Fail 诚实回错）。</summary>
    private static async Task<Guid[]> FindAffectedMappingsAsync(AppDbContext db, Guid ownerDeviceId, string removedCidr)
    {
        if (!IPNetwork.TryParse(removedCidr, out var removedNet))
            return []; // 非规范段（手改库）：无可判定覆盖面，仅删行
        var candidates = await db.Mappings.AsNoTracking()
            .Where(m => m.TargetDeviceId == ownerDeviceId && m.TargetAddr != "self" && m.Enabled)
            .Select(m => new { m.Id, m.TargetAddr })
            .ToListAsync();
        return candidates
            .Where(m => IPAddress.TryParse(m.TargetAddr, out var addr) && removedNet.Contains(addr))
            .Select(m => m.Id)
            .ToArray();
    }

    /// <summary>CIDR 规范化：.NET IPNetwork.TryParse 语义——显式前缀原样、裸 IP 按主机地址补满前缀
    /// （IPv4 /32、IPv6 /128，"::" 同理为单地址；全开放须显式 "0.0.0.0/0"/"::/0"）；其余（主机名/
    /// 非法串/前缀越界）null → 拒绝。落库统一规范形态（IPv6 压缩小写）。</summary>
    internal static string? NormalizeCidr(string input)
    {
        if (IPNetwork.TryParse(input, out var net))
            return net.ToString();
        if (IPAddress.TryParse(input, out var bare))
            return new IPNetwork(bare, bare.AddressFamily == AddressFamily.InterNetwork ? 32 : 128).ToString();
        return null;
    }
}

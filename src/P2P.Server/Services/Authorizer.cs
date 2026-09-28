using System.Net;
using Microsoft.EntityFrameworkCore;
using P2P.Core.Protocol;
using P2P.Server.Data;

namespace P2P.Server.Services;

/// <summary>
/// 授权判定链（05 §5，PRD 05 §4 服务端执行点）：
/// L1 访问方 normal 模式；L2 目标 ∈ 访问方账号设备 ∪ 共同分组设备（SQL 一次查询）；
/// L3 targetAddr ≠ self → 须落在目标设备 enabled lan_segments CIDR 覆盖内（M2-11，SEC-52 第一道）。
/// 失败写 punch_deny/mapping_deny（SEC-51）。
/// </summary>
public sealed class Authorizer(IDbContextFactory<AppDbContext> dbFactory)
{
    public sealed record Verdict(bool Allowed, int Code, string Reason)
    {
        public static readonly Verdict OkResult = new(true, ErrorCode.Ok, "ok");
    }

    /// <summary>L1+L2+L3 打洞授权（0x70；passive 已在路由入口拦截，此处兜底）。
    /// mappingId 给定时（M2-11）：查无/非本人 → 1002（防伪造绕过 L3）；以映射现值 TargetAddr 过 L3。</summary>
    public async Task<Verdict> CheckPunchAsync(ControlSession session, Guid targetDeviceId, Guid? mappingId = null)
    {
        if (session.Capability != CapabilityMode.Normal) // L1（02 §2.5）
            return new Verdict(false, ErrorCode.ForbiddenPassive, "l1_passive");

        await using var db = await dbFactory.CreateDbContextAsync();
        // L2：目标 ∈ 可见设备（本账号 ∪ 共同分组，一次查询；与 0x40 同口径）
        var allowed = await GroupService.VisibleDevices(db, session)
            .AnyAsync(d => d.Id == targetDeviceId);
        if (!allowed)
            return new Verdict(false, ErrorCode.TargetNotAuthorized, "l2_not_visible");

        // L3：TriggerMappingId 关联映射现值校验（02 §2.4 双路径之二）
        if (mappingId is { } mid)
        {
            var mapping = await db.Mappings.AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == mid && m.OwnerDeviceId == session.DeviceId);
            if (mapping is null)
                return new Verdict(false, ErrorCode.NotFound, "mapping_not_found");
            if (!await IsTargetAddrAllowedAsync(db, targetDeviceId, mapping.TargetAddr))
                return new Verdict(false, ErrorCode.TargetAddrNotAllowed, "l3_segment_not_covered");
        }
        return Verdict.OkResult;
    }

    /// <summary>L3 白名单判定（05 §2.5）：self 恒放行；否则 targetAddr（IP 字面量）须落在
    /// 目标设备 enabled lan_segments 任一 CIDR 内。SQLite 无法 SQL 内做 CIDR 数学 → 段集内存过滤
    /// （每设备段数个位数量级，FR-C-701）。非法地址/段（手改库）fail closed。</summary>
    internal static async Task<bool> IsTargetAddrAllowedAsync(AppDbContext db, Guid targetDeviceId, string targetAddr)
    {
        if (targetAddr == "self")
            return true;
        if (!IPAddress.TryParse(targetAddr, out var addr))
            return false; // 非 IP 字面量（协议语义即 IP 或 self，SEC-52 保守拒绝）
        var cidrs = await db.LanSegments.AsNoTracking()
            .Where(s => s.DeviceId == targetDeviceId && s.Enabled)
            .Select(s => s.Cidr)
            .ToListAsync();
        foreach (var cidr in cidrs)
            if (IPNetwork.TryParse(cidr, out var net) && net.Contains(addr))
                return true;
        return false;
    }
}

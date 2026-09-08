using Microsoft.EntityFrameworkCore;
using P2P.Core.Protocol;
using P2P.Server.Data;

namespace P2P.Server.Services;

/// <summary>
/// 授权判定链（05 §5，PRD 05 §4 服务端执行点）：
/// L1 访问方 normal 模式；L2 目标 ∈ 访问方账号设备 ∪ 共同分组设备（SQL 一次查询）。
/// L3 targetAddr/lan_segments 校验 → M2（白名单未开放）。失败写 punch_deny/mapping_deny（SEC-51）。
/// </summary>
public sealed class Authorizer(IDbContextFactory<AppDbContext> dbFactory)
{
    public sealed record Verdict(bool Allowed, int Code, string Reason)
    {
        public static readonly Verdict OkResult = new(true, ErrorCode.Ok, "ok");
    }

    /// <summary>L1+L2 打洞授权（0x70；passive 已在路由入口拦截，此处兜底）。</summary>
    public async Task<Verdict> CheckPunchAsync(ControlSession session, Guid targetDeviceId)
    {
        if (session.Capability != CapabilityMode.Normal) // L1（02 §2.5）
            return new Verdict(false, ErrorCode.ForbiddenPassive, "l1_passive");

        await using var db = await dbFactory.CreateDbContextAsync();
        // L2：目标 ∈ 可见设备（本账号 ∪ 共同分组，一次查询；与 0x40 同口径）
        var allowed = await GroupService.VisibleDevices(db, session)
            .AnyAsync(d => d.Id == targetDeviceId);
        return allowed ? Verdict.OkResult
            : new Verdict(false, ErrorCode.TargetNotAuthorized, "l2_not_visible");
    }
}

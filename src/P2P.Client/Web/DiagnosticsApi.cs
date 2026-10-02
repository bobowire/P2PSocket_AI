// M1-29 诊断端点（04 §2.6 本地子集）：打洞队列深度/当前目标（05 §3.1 PunchScheduler 诊断口径）。
// M3-15 stun-test：POST /api/diagnostics/stun-test——RFC5780 子集判型（05 §7.2，FR-C-808），
// 纯本机 UDP/TCP 编排不走控制通道（passive 亦可达，本机诊断类）；ping-device → M3-16。
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using P2P.Client.Diagnostics;
using P2P.Client.Punch;
using P2P.Core.Protocol;

namespace P2P.Client.Web;

public static class DiagnosticsApi
{
    public static IEndpointRouteBuilder MapDiagnosticsApi(this IEndpointRouteBuilder app,
        PunchScheduler scheduler, StunTester? stunTester = null)
    {
        app.MapGet("/api/diagnostics", () => Api.Ok(new
        {
            punchQueueDepth = scheduler.QueueDepth,
            currentPunchPeer = scheduler.CurrentPeer,
        }));
        app.MapPost("/api/diagnostics/stun-test", async (CancellationToken ct) =>
        {
            try
            {
                if (stunTester is null)
                    throw new ApiException(ErrorCode.BadRequest, "StunTester 未装配（无可用服务器地址）");
                return Api.Ok(await stunTester.RunAsync(ct));
            }
            catch (Exception e) { return Api.Fail(e); }
        });
        return app;
    }
}

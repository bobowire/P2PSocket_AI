// M1-29 诊断端点（04 §2.6 本地子集）：打洞队列深度/当前目标（05 §3.1 PunchScheduler 诊断口径）。
// stun-test/ping-device → M1-3x 诊断任务。
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using P2P.Client.Punch;

namespace P2P.Client.Web;

public static class DiagnosticsApi
{
    public static IEndpointRouteBuilder MapDiagnosticsApi(this IEndpointRouteBuilder app,
        PunchScheduler scheduler)
    {
        app.MapGet("/api/diagnostics", () => Api.Ok(new
        {
            punchQueueDepth = scheduler.QueueDepth,
            currentPunchPeer = scheduler.CurrentPeer,
        }));
        return app;
    }
}

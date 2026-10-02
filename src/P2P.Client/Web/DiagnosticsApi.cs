// M1-29 诊断端点（04 §2.6 本地子集）：打洞队列深度/当前目标（05 §3.1 PunchScheduler 诊断口径）。
// M3-15 stun-test：POST /api/diagnostics/stun-test——RFC5780 子集判型（05 §7.2，FR-C-808），
// 纯本机 UDP/TCP 编排不走控制通道（passive 亦可达，本机诊断类）。
// M3-16 ping-device：POST /api/diagnostics/ping-device {remoteCode}——远程码→0x40 可见列表解析→
// 活隧道 PTP PING(0x06)×4 测 RTT；无活隧道 1002"须先启用一条到该设备的映射"（04 §2.6）。
//   列表解析走控制通道主动类（0x40）：passive 会话本地拒发 2002——ping-device 须 normal 模式
//   （目标不可见即无从解析，与映射创建同口径）。
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using P2P.Client.Diagnostics;
using P2P.Client.Punch;
using P2P.Core.Protocol;

namespace P2P.Client.Web;

public static class DiagnosticsApi
{
    /// <summary>ping-device 请求体（04 §2.6）。</summary>
    public sealed record PingDeviceRequest(string? RemoteCode);

    public static IEndpointRouteBuilder MapDiagnosticsApi(this IEndpointRouteBuilder app,
        PunchScheduler scheduler, StunTester? stunTester = null, DevicePinger? devicePinger = null)
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
        app.MapPost("/api/diagnostics/ping-device", async (PingDeviceRequest body, CancellationToken ct) =>
        {
            try
            {
                if (devicePinger is null)
                    throw new ApiException(ErrorCode.BadRequest, "DevicePinger 未装配");
                return Api.Ok(await devicePinger.RunAsync(body.RemoteCode, ct));
            }
            catch (Exception e) { return Api.Fail(e); }
        });
        return app;
    }
}


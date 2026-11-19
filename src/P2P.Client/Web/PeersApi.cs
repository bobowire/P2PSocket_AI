// M2-23 目标设备级配置端点（04 §2.4、FR-C-803/FR-S-702、D3 v0.4/OQ-10）：
// - GET /api/peers/{deviceId}：`{ relayFallback, punchMode }`（无条目=默认 false/tcp，PRD 06 §2/设计 12 决策①）；
// - PUT /api/peers/{deviceId}：`{ relayFallback?, punchMode? }` → PeersStore.SetAsync 合并写（null=保持现值，
//   部分更新语义同 /api/settings）；punchMode 非 tcp/kcp（含 "udp"）→ 1001（设计 12 §8.1）；
// - 读写仅本地 peers.json：不经控制协议、不同步服务端（OQ-10：回退/打洞模式均为访问方本地决策输入）；
// - 仅 normal 模式（04 §2.4 节口径；passive 无打洞/映射路径，配置无生效对象 → 2002）。
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using P2P.Client.Control;
using P2P.Client.Storage;
using P2P.Core.Protocol;

namespace P2P.Client.Web;

public static class PeersApi
{
    public static IEndpointRouteBuilder MapPeersApi(this IEndpointRouteBuilder app,
        ControlClient control, PeersStore peers)
    {
        app.MapGet("/api/peers/{deviceId:guid}", (Guid deviceId) =>
        {
            try
            {
                RequireNormal(control);
                return Api.Ok(new { relayFallback = peers.GetRelayFallback(deviceId), punchMode = peers.GetPunchMode(deviceId) });
            }
            catch (Exception e) { return Api.Fail(e); }
        });

        app.MapPut("/api/peers/{deviceId:guid}", async (Guid deviceId, PeerUpdateRequest body, CancellationToken ct) =>
        {
            try
            {
                RequireNormal(control);
                await peers.SetAsync(deviceId, body.RelayFallback, body.PunchMode, ct);
                return Api.Ok(new { relayFallback = peers.GetRelayFallback(deviceId), punchMode = peers.GetPunchMode(deviceId) });
            }
            catch (Exception e) { return Api.Fail(e); }
        });

        return app;
    }

    /// <summary>04 §2.4 节仅 normal 模式（本端点不经控制协议，无服务端侧 2002，本地显式拦截）。</summary>
    private static void RequireNormal(ControlClient control)
    {
        if (control.Capability != CapabilityMode.Normal)
            throw new PassiveModeException("目标设备级配置仅 normal 模式可用（04 §2.4）");
    }

    /// <summary>更新体（04 §2.4/设计 12 §8.1：双字段部分更新——缺省/null = 保持现值）。</summary>
    public sealed record PeerUpdateRequest(bool? RelayFallback, string? PunchMode);
}

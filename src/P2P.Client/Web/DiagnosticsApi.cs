// M1-29 诊断端点（04 §2.6 本地子集）：打洞队列深度/当前目标（05 §3.1 PunchScheduler 诊断口径）。
// M3-15 stun-test：POST /api/diagnostics/stun-test——RFC5780 子集判型（05 §7.2，FR-C-808），
// 纯本机 UDP/TCP 编排不走控制通道（passive 亦可达，本机诊断类）。
// M3-16 ping-device：POST /api/diagnostics/ping-device {remoteCode}——远程码→0x40 可见列表解析→
// 活隧道 PTP PING(0x06)×4 测 RTT；无活隧道 1002"须先启用一条到该设备的映射"（04 §2.6）。
//   列表解析走控制通道主动类（0x40）：passive 会话本地拒发 2002——ping-device 须 normal 模式
//   （目标不可见即无从解析，与映射创建同口径）。
// M3-14 诊断区收口（FR-C-808 剩余）：server-test（复用 wizard 连通性探测的运行态入口——对
//   settings 全部候选逐个 TCP 探测，纯 socket 不走控制会话，passive 亦可达）+ tunnels（活隧道
//   列表快照：TunnelHost 本地表）+ rekey（TriggerRekeyAsync 手动轮换，05 §2.3 M2-21 预留收口；
//   纯本地隧道操作 passive 亦可达）。队列深度展示化消费既有 GET /api/diagnostics。
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using P2P.Client.Diagnostics;
using P2P.Client.Punch;
using P2P.Client.Registration;
using P2P.Client.Storage;
using P2P.Client.Tunnel;
using P2P.Core.Protocol;

namespace P2P.Client.Web;

/// <summary>ping-device 请求体（04 §2.6）。</summary>
public sealed record PingDeviceRequest(string? RemoteCode);

/// <summary>rekey 请求体：目标设备对（隧道列表行 peerDeviceId）。</summary>
public sealed record RekeyRequest(Guid? PeerDeviceId);

/// <summary>server-test 单候选结果（M3-14，04 §2.6）。</summary>
public sealed record ServerTestResultView(string Addr, bool Ok, string Detail);

/// <summary>server-test 结果：全部候选逐个探测（短路语义仅探测可达性诊断，不做换址）。</summary>
public sealed record ServerTestView(ServerTestResultView[] Items);

/// <summary>活隧道行（M3-14）：Label=本地映射运行时反查的远程码（无映射指向时 null，显示短 id）。</summary>
public sealed record TunnelView(Guid PeerDeviceId, string? Label, bool ViaRelay, bool IsInitiator);

/// <summary>活隧道列表快照（TunnelHost 本地表，passive 亦可达）。</summary>
public sealed record TunnelListView(TunnelView[] Items);

/// <summary>手动 REKEY 结果（outcome：ok/not_initiator/busy/failed；05 §2.3）。</summary>
public sealed record RekeyResultView(Guid PeerDeviceId, string Outcome, string? Detail);

public static class DiagnosticsApi
{
    public static IEndpointRouteBuilder MapDiagnosticsApi(this IEndpointRouteBuilder app,
        PunchScheduler scheduler, StunTester? stunTester = null, DevicePinger? devicePinger = null,
        TunnelHost? tunnels = null, SettingsStore? settingsStore = null,
        Func<Guid, string?>? peerLabelLookup = null)
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

        // M3-14 服务端连通性测试（运行态入口，FR-C-808）：对 settings 全部候选逐个探测——
        // 复用 wizard TestConnectivityAsync（M1-24 版本协商预检同源 TCP 探测），单候选独立呈现。
        app.MapPost("/api/diagnostics/server-test", async (CancellationToken ct) =>
        {
            try
            {
                if (settingsStore is null)
                    throw new ApiException(ErrorCode.BadRequest, "SettingsStore 未装配");
                var addrs = settingsStore.Settings.ServerAddrs;
                if (addrs.Length == 0)
                    throw new ApiException(ErrorCode.BadRequest, "未配置服务端地址（settings.serverAddrs 为空）");
                var items = new List<ServerTestResultView>(addrs.Length);
                foreach (var addr in addrs)
                {
                    var (ok, detail) = await ClientRegistrationService.TestConnectivityAsync([addr], ct: ct);
                    items.Add(new ServerTestResultView(addr, ok, detail));
                }
                return Api.Ok(new ServerTestView(items.ToArray()));
            }
            catch (Exception e) { return Api.Fail(e); }
        });

        // M3-14 活隧道列表（passive 亦可达——TunnelHost 本地表，不经控制通道）。
        app.MapGet("/api/diagnostics/tunnels", () =>
        {
            try
            {
                if (tunnels is null)
                    throw new ApiException(ErrorCode.BadRequest, "TunnelHost 未装配");
                var items = tunnels.Sessions
                    .Where(s => !s.IsClosed)
                    .OrderBy(s => s.PeerDeviceId) // 确定性排序
                    .Select(s => new TunnelView(
                        s.PeerDeviceId, peerLabelLookup?.Invoke(s.PeerDeviceId), s.ViaRelay, s.IsInitiator))
                    .ToArray();
                return Api.Ok(new TunnelListView(items));
            }
            catch (Exception e) { return Api.Fail(e); }
        });

        // M3-14 手动 REKEY（05 §2.3 M2-21 预留收口）：发起方执行一次轮换并回传结果；
        // 响应方 NotInitiator 空操作；上一轮未完 Busy；无活隧道 1002。
        app.MapPost("/api/diagnostics/rekey", async (RekeyRequest body, CancellationToken ct) =>
        {
            try
            {
                if (tunnels is null)
                    throw new ApiException(ErrorCode.BadRequest, "TunnelHost 未装配");
                var peerId = body.PeerDeviceId ?? Guid.Empty;
                if (peerId == Guid.Empty)
                    throw new ApiException(ErrorCode.BadRequest, "peerDeviceId 不能为空");
                var session = tunnels.Get(peerId)
                    ?? throw new ApiException(ErrorCode.NotFound, "设备对无活动隧道");
                var result = await session.TriggerRekeyAsync();
                var outcome = result.Outcome switch // 契约口径 04 §2.6：枚举名 NotInitiator ≠ not_initiator
                {
                    RekeyOutcome.Ok => "ok",
                    RekeyOutcome.NotInitiator => "not_initiator",
                    RekeyOutcome.Busy => "busy",
                    _ => "failed",
                };
                return Api.Ok(new RekeyResultView(peerId, outcome, result.Detail));
            }
            catch (Exception e) { return Api.Fail(e); }
        });
        return app;
    }
}

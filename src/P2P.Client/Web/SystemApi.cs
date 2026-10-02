// M1-29 系统与设备端点（04 §2.1、FR-C-801/FR-S-106）：
// - GET /api/system/state：phase 机 = 未注册（wizard 进行中→wizard，否则 unregistered）/已注册
//   （Established→running，其余 degraded）+ 通道可达性 + 协议版本；
//   M3-13 增 conflict 字段（FR-C-204）：每请求现场重算（冲突解除无本机事件可听，惰性重算
//   才可靠）；值变化经 ConflictsChanged → WS subnet_conflict 提示（TD-16 REST 真相源）；
// - GET /api/device：state.json 三要素 + 登录账号 + 能力模式；
// - PUT /api/device：0x13 改名（空名 1001；passive 本地拒 2002）。
// reset-remote-code（0x14）→ DeviceApi（M2-15）；settings GET/PUT → 配置任务。
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using P2P.Client.Control;
using P2P.Client.Nic;
using P2P.Client.Storage;
using P2P.Core.Protocol;

namespace P2P.Client.Web;

public static class SystemApi
{
    public static IEndpointRouteBuilder MapSystemApi(this IEndpointRouteBuilder app,
        ControlClient control, StateStore store, LocalApiContext ctx,
        SubnetConflictDetector? subnetConflicts = null)
    {
        app.MapGet("/api/system/state", () =>
        {
            var registered = store.State.IsRegistered;
            var phase = !registered
                ? ctx.WizardInProgress ? "wizard" : "unregistered"
                : control.State == ControlClientState.Established ? "running" : "degraded";
            return Api.Ok(new { phase, serverReachable = control.IsReady,
                protocolVersion = ProtocolVersion.Current,
                conflict = subnetConflicts?.Check(store.State.VirtualIp) });
        });

        app.MapGet("/api/device", () => Api.Ok(new
        {
            deviceId = store.State.DeviceId,
            remoteCode = store.State.RemoteCode,
            virtualIp = store.State.VirtualIp,
            username = ctx.LoginUser,
            capability = control.Capability == CapabilityMode.Normal ? "normal" : "passive",
        }));

        app.MapPut("/api/device", async (DeviceNameRequest body, CancellationToken ct) =>
        {
            try
            {
                var name = body.DeviceName?.Trim() ?? "";
                if (name.Length is < 1 or > 64)
                    throw new ApiException(ErrorCode.BadRequest, "设备名长度须为 1~64 字符");
                var ack = await control.SendRequestAsync<DeviceUpdateAck>(new DeviceUpdate(
                    control.NextSeq(), control.TimestampMs(), MsgType.DeviceUpdate, name), ct);
                if (!ack.Ok) throw new ApiException(ErrorCode.BadRequest, "服务端拒绝改名");
                return Api.Ok(new { deviceName = name });
            }
            catch (Exception e) { return Api.Fail(e); }
        });

        return app;
    }

    public sealed record DeviceNameRequest(string? DeviceName);
}

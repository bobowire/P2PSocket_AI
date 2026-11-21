// M1-29 系统与设备端点（04 §2.1、FR-C-801/FR-S-106）：
// - GET /api/system/state：phase 机 = 未注册（wizard 进行中→wizard，否则 unregistered）/已注册
//   （Established→running，其余 degraded）+ 通道可达性 + 协议版本；
//   M3-13 增 conflict 字段（FR-C-204）：每请求现场重算（冲突解除无本机事件可听，惰性重算
//   才可靠）；值变化经 ConflictsChanged → WS subnet_conflict 提示（TD-16 REST 真相源）；
// - GET /api/device：state.json 三要素 + 登录账号 + 能力模式 + 设备名（v1.58）；
// - PUT /api/device：0x13 改名（空名 1001；passive 本地拒 2002）；成功后 state.json 持久化
//   + WS device_list 提示（本机列表行即时刷新；服务端 0x41 推同账号/同组对端，v1.58）。
// reset-remote-code（0x14）→ DeviceApi（M2-15）；settings GET/PUT → 配置任务。
// M4-01 增 version/commit（08 §1：Directory.Build.props 注入，Settings 关于区展示）。
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using P2P.Client.Control;
using P2P.Client.Nic;
using P2P.Client.Storage;
using P2P.Core;
using P2P.Core.Protocol;

namespace P2P.Client.Web;

public static class SystemApi
{
    public static IEndpointRouteBuilder MapSystemApi(this IEndpointRouteBuilder app,
        ControlClient control, StateStore store, LocalApiContext ctx, StatusHub hub,
        SubnetConflictDetector? subnetConflicts = null, Func<string>? effectiveVirtualIp = null)
    {
        app.MapGet("/api/system/state", () =>
        {
            var registered = store.State.IsRegistered;
            var phase = !registered
                ? ctx.WizardInProgress ? "wizard" : "unregistered"
                : control.State == ControlClientState.Established ? "running" : "degraded";
            return Api.Ok(new { phase, serverReachable = control.IsReady,
                protocolVersion = ProtocolVersion.Current,
                // M8-01 起冲突检测按生效绑定地址推导网段（无卡=127.0.0.0/24 无误报，设计 13 §2.5）
                conflict = subnetConflicts?.Check(effectiveVirtualIp?.Invoke() ?? store.State.VirtualIp,
                    trigger: "api/system/state"),
                version = AppVersion.Version, commit = AppVersion.Commit });
        });

        app.MapGet("/api/device", () => Api.Ok(new
        {
            deviceId = store.State.DeviceId,
            // 未改名（含存量升级 state.json 无此字段）回落主机名=注册默认名，仅展示；改名后为持久真值（v1.58）
            deviceName = store.State.DeviceName ?? Environment.MachineName,
            remoteCode = store.State.RemoteCode,
            // M8-01 展示归真（设计 13 §4.3）：无卡模式=生效绑定地址（127.0.0.1，真实可达）；
            // 桌面（缝 null）=state.VirtualIp 现状。/api/devices 列表为服务端他设备记录（远端真相），
            // 非本机连接地址，不随无卡模式改写
            virtualIp = effectiveVirtualIp?.Invoke() ?? store.State.VirtualIp,
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
                var ack = await control.SendRequestAsync<DeviceUpdateAck>(seq => new DeviceUpdate(
                    seq, control.TimestampMs(), MsgType.DeviceUpdate, name), ct);
                if (!ack.Ok) throw new ApiException(ErrorCode.BadRequest, "服务端拒绝改名");
                store.State.DeviceName = name; // 改名持久化（v1.58：凭据恢复重注册沿用，防洗回 MachineName）
                await store.SaveAsync(ct);
                hub.Publish(new { ev = WsEventNames.DeviceList }); // 本机名已变：前端刷新设备视图（TD-16，与 0x14 同模式）
                return Api.Ok(new { deviceName = name });
            }
            catch (Exception e) { return Api.Fail(e); }
        });

        return app;
    }

    public sealed record DeviceNameRequest(string? DeviceName);
}

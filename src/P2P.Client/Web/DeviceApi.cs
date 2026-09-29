// M1-32 设备发现端点（04 §2.4、FR-C-803）：
// GET /api/devices —— 0x40 分页循环拉全量（OQ-16）转发服务端可见列表（本账号 ∪ 共同分组）。
// passive 会话 0x40 属主动类：本地拒发 2002（02 §2.5）；未注册/断线 → 5003。
// groups/peers 族端点属 M2（设备级回退开关 D3 v0.4 → M2）。
// POST /api/device/reset-remote-code（M2-15，04 §2.1、FR-S-903）：0x14 本机管理类（passive 允许），
// Ack 新码落 state.json + WS device_list 提示（服务端侧旧码引用方 0x75/0x41 由 M2-12 推送）。
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using P2P.Client.Control;
using P2P.Client.Storage;
using P2P.Core.Protocol;

namespace P2P.Client.Web;

public static class DeviceApi
{
    /// <summary>0x40 分页上限（服务端钳制 1~200，OQ-16）。</summary>
    private const uint PageSize = 100;

    public static IEndpointRouteBuilder MapDeviceApi(this IEndpointRouteBuilder app,
        ControlClient control, StateStore state, StatusHub hub)
    {
        app.MapGet("/api/devices", async (CancellationToken ct) =>
        {
            try
            {
                var items = new List<DeviceListItem>();
                uint offset = 0;
                while (true)
                {
                    var resp = await control.SendRequestAsync<DeviceListResponse>(new DeviceListRequest(
                        control.NextSeq(), control.TimestampMs(), MsgType.DeviceList, offset, PageSize), ct);
                    items.AddRange(resp.Items);
                    if (!resp.HasMore) break;
                    offset += (uint)resp.Items.Length;
                }
                return Api.Ok(items.Select(d => new
                {
                    deviceId = d.DeviceId,
                    deviceName = d.DeviceName,
                    remoteCode = d.RemoteCode,
                    virtualIp = d.VirtualIp,
                    online = d.Online,
                    groups = d.Groups,
                    lanSegments = d.LanSegments,
                }));
            }
            catch (Exception e) { return Api.Fail(e); }
        });

        // 0x14 远程码重置（FR-S-903/SEC-25）：凭连接级设备身份（已注册即已建立会话），passive 允许
        app.MapPost("/api/device/reset-remote-code", async (CancellationToken ct) =>
        {
            try
            {
                var ack = await control.SendRequestAsync<RemoteCodeResetAck>(new RemoteCodeReset(
                    control.NextSeq(), control.TimestampMs(), MsgType.RemoteCodeReset), ct);
                if (!ack.Ok || string.IsNullOrEmpty(ack.NewRemoteCode))
                    throw new ApiException(ErrorCode.BadRequest, "远程码重置被服务端拒绝");
                state.State.RemoteCode = ack.NewRemoteCode; // 新码持久化（03 §5 state.json）
                await state.SaveAsync(ct);
                hub.Publish(new { ev = WsEventNames.DeviceList }); // 本机码已变：前端刷新设备视图（TD-16）
                return Api.Ok(new { remoteCode = ack.NewRemoteCode });
            }
            catch (Exception e) { return Api.Fail(e); }
        });

        return app;
    }
}

// M1-32 设备发现端点（04 §2.4、FR-C-803）：
// GET /api/devices —— 0x40 分页循环拉全量（OQ-16）转发服务端可见列表（本账号 ∪ 共同分组）。
// passive 会话 0x40 属主动类：本地拒发 2002（02 §2.5）；未注册/断线 → 5003。
// groups/peers 族端点属 M2（设备级回退开关 D3 v0.4 → M2）。
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using P2P.Client.Control;
using P2P.Core.Protocol;

namespace P2P.Client.Web;

public static class DeviceApi
{
    /// <summary>0x40 分页上限（服务端钳制 1~200，OQ-16）。</summary>
    private const uint PageSize = 100;

    public static IEndpointRouteBuilder MapDeviceApi(this IEndpointRouteBuilder app, ControlClient control)
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

        return app;
    }
}

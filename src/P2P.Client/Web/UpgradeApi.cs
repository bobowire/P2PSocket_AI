// M2-26 升级引导端点（04 §2.7、FR-C-904、OQ-5）：
// - GET /api/upgrade/info：已建立会话 → signed 0x03 现取（服务端两窗口共用 SendUpdateInfoAsync）；
//   未建立 → 回落版本拒答窗口缓存 LastUpgradeInfo——版本不符恰是通道不可建立态，正是升级页数据源；
// - UpgradeInfoView：展示 DTO（export-ts 同源导出前端类型，页面渲染归 M2-29）；
// - 拒答窗口自动开浏览器 /upgrade?reason=version 的接线在 ClientRuntime（沿 M1-24 机制）。
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using P2P.Client.Control;
using P2P.Core.Protocol;

namespace P2P.Client.Web;

public static class UpgradeApi
{
    public static IEndpointRouteBuilder MapUpgradeApi(this IEndpointRouteBuilder app, ControlClient control)
    {
        app.MapGet("/api/upgrade/info", async (CancellationToken ct) =>
        {
            try
            {
                // 已建立会话现取（0x03 非主动类，passive 亦允许——02 §2.5 清单不含）
                if (control.State == ControlClientState.Established)
                {
                    var fresh = await control.SendRequestAsync<UpdateInfoResponse>(new UpdateInfoRequest(
                        control.NextSeq(), control.TimestampMs(), MsgType.UpdateInfo), ct);
                    return Api.Ok(ToView(fresh));
                }
                // 版本不符/通道未建立：回落拒答窗口缓存（无缓存=尚未发生版本拒答，无可引导）
                if (control.LastUpgradeInfo is { } cached)
                    return Api.Ok(ToView(cached));
                throw new ApiException(ErrorCode.ServerUnreachable,
                    "升级信息不可用（控制通道未建立且尚无版本拒答记录）");
            }
            catch (Exception e) { return Api.Fail(e); }
        });
        return app;
    }

    private static UpgradeInfoView ToView(UpdateInfoResponse info) =>
        new(info.LatestVersion, info.MinProtocol, info.MaxProtocol, info.UpgradeUrl, info.Notes);
}

/// <summary>升级信息展示 DTO（04 §2.7：版本/兼容范围/指引；export-ts 导出）。</summary>
public sealed record UpgradeInfoView(
    string LatestVersion,
    ushort MinProtocol,
    ushort MaxProtocol,
    string UpgradeUrl,
    string Notes);

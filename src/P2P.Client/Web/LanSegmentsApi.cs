// M2-27 开放内网段白名单端点（04 §2.5、FR-C-701/702）：
// - GET 读本地镜像（M2-11：镜像即 UI 数据源——0x63 无 list 消息，每变更经 Ack 后同步落盘）；
// - POST/DELETE 薄转发 0x63（SegmentId=null 新建；Enabled=false 为移除语义，02 §2.4），
//   Ack 后镜像 ReplaceAll 收口——读路径零协议往返；
// - 0x63 属本机管理类：passive 允许（02 §2.5 不入主动类清单）；
// - 变更后 hub 发 device_list 提示（0x40 列表项 lanSegments[] 摘要随之变化）。
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using P2P.Client.Control;
using P2P.Client.Storage;
using P2P.Core.Protocol;

namespace P2P.Client.Web;

public static class LanSegmentsApi
{
    public static IEndpointRouteBuilder MapLanSegmentsApi(this IEndpointRouteBuilder app,
        ControlClient control, LanSegmentsStore store, StatusHub hub)
    {
        // 全量（本地镜像快照；无分页——段量级小）
        app.MapGet("/api/lan-segments", () =>
        {
            try
            {
                return Api.Ok(store.Entries().Select(e => new LanSegmentView(e.SegmentId, e.Cidr, e.Enabled)).ToList());
            }
            catch (Exception e) { return Api.Fail(e); }
        });

        // 新建：{ cidr } → 0x63 → Ack.SegmentId → 镜像追加
        app.MapPost("/api/lan-segments", async (LanSegmentCreateRequest body, CancellationToken ct) =>
        {
            try
            {
                var cidr = (body.Cidr ?? "").Trim();
                // 规范化与服务端 LanSegmentService.NormalizeCidr 同口径（裸 IP 补 /32）——镜像与服务端一致
                var normalized = NormalizeCidr(cidr)
                    ?? throw new ApiException(ErrorCode.BadRequest, "cidr 须为合法网段（如 192.168.1.0/24）");
                cidr = normalized;
                var ack = await control.SendRequestAsync<LanSegmentsUpsertAck>(new LanSegmentsUpsert(
                    control.NextSeq(), control.TimestampMs(), MsgType.LanSegmentsUpsert,
                    SegmentId: null, Cidr: cidr, Enabled: true), ct);
                var next = store.Entries().Where(e => e.Cidr != cidr).ToList(); // 同段覆盖
                next.Add(new LanSegmentsStore.LanSegmentEntry(ack.SegmentId, cidr, true));
                await store.ReplaceAllAsync(next, ct);
                hub.Publish(new { ev = WsEventNames.DeviceList });
                return Api.Ok(new LanSegmentView(ack.SegmentId, cidr, true));
            }
            catch (Exception e) { return Api.Fail(e); }
        });

        // 移除（幂等：镜像无此段直接 Ok——服务端 Enabled=false 同义移除，不留悬挂条目）
        app.MapDelete("/api/lan-segments/{id:guid}", async (Guid id, CancellationToken ct) =>
        {
            try
            {
                if (store.Entries().FirstOrDefault(e => e.SegmentId == id) is { } entry)
                {
                    await control.SendRequestAsync<LanSegmentsUpsertAck>(new LanSegmentsUpsert(
                        control.NextSeq(), control.TimestampMs(), MsgType.LanSegmentsUpsert,
                        id, entry.Cidr, Enabled: false), ct);
                    await store.ReplaceAllAsync(
                        store.Entries().Where(e => e.SegmentId != id).ToList(), ct);
                    hub.Publish(new { ev = WsEventNames.DeviceList });
                }
                return Api.Ok(null);
            }
            catch (Exception e) { return Api.Fail(e); }
        });

        return app;
    }

    /// <summary>CIDR 规范化（镜像服务端 LanSegmentService.NormalizeCidr 同口径：
    /// IPNetwork 语义优先，裸 IP 补满前缀 IPv4 /32、IPv6 /128；其余 null → 本地拒绝）。</summary>
    private static string? NormalizeCidr(string input)
    {
        if (IPNetwork.TryParse(input, out var net))
            return net.ToString();
        if (IPAddress.TryParse(input, out var bare))
            return new IPNetwork(bare, bare.AddressFamily == AddressFamily.InterNetwork ? 32 : 128).ToString();
        return null;
    }
}

/// <summary>展示 DTO（export-ts；条目与 0x63 字段一一对应）。</summary>
public sealed record LanSegmentView(Guid SegmentId, string Cidr, bool Enabled);

public sealed record LanSegmentCreateRequest(string? Cidr);

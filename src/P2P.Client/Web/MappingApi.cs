// M1-28 映射本地 API（04 §2.5、TD-08：业务数据经本地 API → 控制协议转发，不经 HTTP 直连服务端）：
// - 响应包裹 { code, msg, data }（04 §1）；业务错误 HTTP 仍 200，code 表 §5；
// - 列表 { items, total } 分页 page/pageSize 默认 1/20（04 §1）；
// - 宿主（M1-29）挂载于 127.0.0.1:7100（TD-12 无鉴权）；本模块只做端点→MappingSyncService 转发与错误映射。
// - retry/stats 端点 → M1-29+（任务清单 M1-28 范围：列表/创建/编辑/启停/删除）。
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using P2P.Client.Control;
using P2P.Client.Mapping;
using P2P.Core.Protocol;

namespace P2P.Client.Web;

/// <summary>映射端点（扩展方法挂载，闭包持有 <see cref="MappingSyncService"/>——单例宿主内无 DI 需求）。</summary>
public static class MappingApi
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>映射 CRUD 请求体（04 §2.5；proto/targetAddr 缺省 tcp/self——PRD 06 §2 默认值）。</summary>
    public sealed record MappingRequest(
        string? Name,
        ushort LocalPort,
        string? Proto,
        string? TargetRemoteCode,
        string? TargetAddr,
        ushort TargetPort);

    public static IEndpointRouteBuilder MapMappingApi(this IEndpointRouteBuilder app,
        MappingSyncService sync)
    {
        var group = app.MapGroup("/api/mappings");

        group.MapGet("/", (HttpRequest req) =>
        {
            var page = Math.Max(1, QueryInt(req, "page", 1));
            var pageSize = Math.Clamp(QueryInt(req, "pageSize", 20), 1, 100);
            var all = sync.List();
            var items = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            return Ok(new { items, total = all.Count });
        });

        group.MapPost("/", async (MappingRequest body, CancellationToken ct) =>
        {
            try
            {
                var m = await sync.CreateAsync(body.Name ?? "", body.LocalPort,
                    body.TargetRemoteCode ?? "", body.TargetPort,
                    body.Proto ?? "tcp", body.TargetAddr ?? "self", ct);
                return Ok(View(sync, m.MappingId));
            }
            catch (Exception e) { return Fail(e); }
        });

        group.MapPut("/{id:guid}", async (Guid id, MappingRequest body, CancellationToken ct) =>
        {
            try
            {
                var m = await sync.UpdateAsync(id, body.Name ?? "", body.LocalPort,
                    body.TargetRemoteCode ?? "", body.TargetPort,
                    body.Proto ?? "tcp", body.TargetAddr ?? "self", ct);
                return Ok(View(sync, m.MappingId));
            }
            catch (Exception e) { return Fail(e); }
        });

        group.MapDelete("/{id:guid}", async (Guid id, CancellationToken ct) =>
        {
            try { await sync.DeleteAsync(id, ct); return Ok(null); }
            catch (Exception e) { return Fail(e); }
        });

        group.MapPost("/{id:guid}/enable", async (Guid id, CancellationToken ct) =>
        {
            try { await sync.EnableAsync(id, ct); return Ok(View(sync, id)); }
            catch (Exception e) { return Fail(e); }
        });

        group.MapPost("/{id:guid}/disable", async (Guid id, CancellationToken ct) =>
        {
            try { await sync.DisableAsync(id, ct); return Ok(View(sync, id)); }
            catch (Exception e) { return Fail(e); }
        });

        return app;
    }

    // ── 包裹与错误映射 ────────────────────────────────────────────────

    private static IResult Ok(object? data) => Results.Json(
        new { code = ErrorCode.Ok, msg = "ok", data }, JsonOptions);

    /// <summary>异常 → envelope code（04 §5）：业务校验/服务端拒绝原码透传，
    /// passive 2002，通道断 5003，其余按 1001 兜底（不向 HTTP 层泄漏堆栈）。</summary>
    private static IResult Fail(Exception e) => Results.Json(new
    {
        code = e switch
        {
            MappingException me => me.Code,
            ControlErrorException ce => ce.Code,
            PassiveModeException => ErrorCode.ForbiddenPassive,
            ControlClientException => ErrorCode.ServerUnreachable,
            _ => ErrorCode.BadRequest,
        },
        msg = e.Message,
        data = (object?)null,
    }, JsonOptions);

    private static MappingView? View(MappingSyncService sync, Guid id)
        => sync.List().FirstOrDefault(m => m.MappingId == id);

    private static int QueryInt(HttpRequest req, string key, int fallback)
        => int.TryParse(req.Query[key].ToString(), out var v) ? v : fallback;
}

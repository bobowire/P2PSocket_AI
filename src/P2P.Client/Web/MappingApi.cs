// M1-28 映射本地 API（04 §2.5、TD-08：业务数据经本地 API → 控制协议转发，不经 HTTP 直连服务端）：
// - 响应包裹 { code, msg, data }（04 §1）；业务错误 HTTP 仍 200，code 表 §5（共享 <see cref="Api"/>）；
// - 列表 { items, total } 分页 page/pageSize 默认 1/20（04 §1）；
// - 宿主（M1-30）经 <see cref="LocalWebApi"/> 挂载于 127.0.0.1:7100（TD-12 无鉴权）。
// - retry 端点（04 §2.5）→ M1-32+（前端用例落地时）；stats 族 → FR-C-1001/1002 统计任务。
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using P2P.Client.Mapping;

namespace P2P.Client.Web;

/// <summary>映射端点（扩展方法挂载，闭包持有 <see cref="MappingSyncService"/>——单例宿主内无 DI 需求）。</summary>
public static class MappingApi
{
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
            return Api.Ok(new { items, total = all.Count });
        });

        group.MapPost("/", async (MappingRequest body, CancellationToken ct) =>
        {
            try
            {
                var m = await sync.CreateAsync(body.Name ?? "", body.LocalPort,
                    body.TargetRemoteCode ?? "", body.TargetPort,
                    body.Proto ?? "tcp", body.TargetAddr ?? "self", ct);
                return Api.Ok(View(sync, m.MappingId));
            }
            catch (Exception e) { return Api.Fail(e); }
        });

        group.MapPut("/{id:guid}", async (Guid id, MappingRequest body, CancellationToken ct) =>
        {
            try
            {
                var m = await sync.UpdateAsync(id, body.Name ?? "", body.LocalPort,
                    body.TargetRemoteCode ?? "", body.TargetPort,
                    body.Proto ?? "tcp", body.TargetAddr ?? "self", ct);
                return Api.Ok(View(sync, m.MappingId));
            }
            catch (Exception e) { return Api.Fail(e); }
        });

        group.MapDelete("/{id:guid}", async (Guid id, CancellationToken ct) =>
        {
            try { await sync.DeleteAsync(id, ct); return Api.Ok(null); }
            catch (Exception e) { return Api.Fail(e); }
        });

        group.MapPost("/{id:guid}/enable", async (Guid id, CancellationToken ct) =>
        {
            try { await sync.EnableAsync(id, ct); return Api.Ok(View(sync, id)); }
            catch (Exception e) { return Api.Fail(e); }
        });

        group.MapPost("/{id:guid}/disable", async (Guid id, CancellationToken ct) =>
        {
            try { await sync.DisableAsync(id, ct); return Api.Ok(View(sync, id)); }
            catch (Exception e) { return Api.Fail(e); }
        });

        group.MapPost("/{id:guid}/retry", async (Guid id, CancellationToken ct) =>
        {
            try { await sync.RetryAsync(id, ct); return Api.Ok(View(sync, id)); }
            catch (Exception e) { return Api.Fail(e); }
        });

        return app;
    }

    private static MappingView? View(MappingSyncService sync, Guid id)
        => sync.List().FirstOrDefault(m => m.MappingId == id);

    private static int QueryInt(HttpRequest req, string key, int fallback)
        => int.TryParse(req.Query[key].ToString(), out var v) ? v : fallback;
}

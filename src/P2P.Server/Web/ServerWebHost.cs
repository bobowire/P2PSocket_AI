// M3-01 服务端 Web 宿主（04 §3、08 §5.1、D11、NFR-31/35）：
// Generic Host 内嵌 ASP.NET Core——复刻客户端本地 Web（M1-22）已验证模式：独立 WebApplication
// 容器 + 主容器单例实例注入，Kestrel 监听 listen.webBind:listen.web（默认 127.0.0.1:7500，仅本机，D11）。
// 停机序：hosted services 按注册序启动、逆序停止——本服务在 Program 注册于 ServerHostService 之后
// ⇒ Web 先停（在途管理请求排水），控制通道/STUN/中继随后。
// 静态托管：物理 wwwroot（程序目录旁路分发，与客户端部署生态同构）+ hash 路由 fallback；
// 目录不存在（测试宿主/裸 API 部署）则跳过，仅 API——不因缺目录拒启。
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using P2P.Server.Data;
using P2P.Server.Services;

namespace P2P.Server.Web;

/// <summary>Web 宿主装配与生命周期（IHostedService 载体）。管理端点组随任务渐次挂载（M3-02 认证起）。
/// relay/stun 可选尾参：生产 DI 注入已注册单例（中继/STUN 运行统计直读）；测试裸 API 形态可缺省
/// （仪表盘该节呈零值快照）——沿 AdminService 可选尾参先例（MS DI 对已注册服务仍注入、未注册才取默认）。</summary>
public sealed class ServerWebHostService(
    ServerOptions options,
    TimeProvider time,
    AdminSessionStore sessions,
    IDbContextFactory<AppDbContext> dbFactory,
    AuditLogger audit,
    AdminService admin,
    DeviceRegistry registry,
    GroupService groups,
    RelayService? relay = null,
    StunService? stun = null) : IHostedService
{
    private WebApplication? _app;

    /// <summary>测试缝：静态根目录（缺省=程序目录/wwwroot）。</summary>
    public string? WebRootOverride { get; init; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _app = Build(options, time, sessions, dbFactory, audit, admin, registry, groups, relay, stun,
            WebRootOverride);
        await _app.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is not null) await _app.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>装配（internal 供集成测试直调复刻生产拓扑）。</summary>
    internal static WebApplication Build(ServerOptions options, TimeProvider time,
        AdminSessionStore sessions, IDbContextFactory<AppDbContext> dbFactory, AuditLogger audit,
        AdminService admin, DeviceRegistry registry, GroupService groups,
        RelayService? relay = null, StunService? stun = null, string? webRootOverride = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://{options.Listen.WebBind}:{options.Listen.Web}");
        builder.Logging.ClearProviders(); // Serilog 在主 host；Web 容器不重复配（访问日志无需求）
        // 主容器单例实例注入（生命周期跟随主 host；管理端点组按需扩）
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(time);
        builder.Services.AddSingleton(sessions);
        var app = builder.Build();

        // M3-02 管理员会话与账号（认证中间件白名单 /api/auth/login 的端点本体在此挂载）
        new AdminAuthApi(dbFactory, audit).Map(app, sessions);
        // M3-03 用户管理（进程内直调 AdminService：踢线/降级/0x75 即时生效）
        new AdminUsersApi(dbFactory, admin, audit).Map(app);
        // M3-04 设备管理（列表在线态取 registry；动作全转调 AdminService）
        new AdminDevicesApi(dbFactory, registry, admin).Map(app);
        // M3-05 分组与审批（审批走 GroupService 共享核=与 0x53 同一执行链）
        new AdminGroupsApi(dbFactory, groups, audit).Map(app);
        // M3-06 仪表盘（在线数/分组数/映射与 TD-22 状态分布/中继统计/STUN 分桶/打洞成功率时序）
        new AdminDashboardApi(dbFactory, registry, relay, stun).Map(app);

        // 认证骨架（04 §3.1/§3.2、07 §8）：/api/* 须携带有效会话 Cookie，否则 401 {code:2001}；
        // /api/auth/login 白名单（登录端点 M3-02 挂载）。静态页（SPA 外壳）不经认证——前端路由接管。
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api")
                && !ctx.Request.Path.StartsWithSegments("/api/auth/login")
                && !sessions.IsValid(ctx.Request.Cookies[AdminSessionStore.CookieName]))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new { code = 2001, message = "未登录或会话失效" });
                return;
            }
            await next();
        });

        var webRoot = webRootOverride ?? Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (Directory.Exists(webRoot))
        {
            app.Environment.WebRootFileProvider = new PhysicalFileProvider(webRoot);
            app.UseDefaultFiles();
            app.UseStaticFiles();
            // SPA hash 路由兜底：未知路径回 index.html 由前端路由接管（不遮蔽 /api/*——endpoint 匹配在中间件后）
            app.MapFallbackToFile("index.html");
        }

        return app;
    }
}

// M3-04 设备管理 API（FR-S-821/103/903、04 §3.2、M2-13/12）：全量列表与四个管理动作。
// 动作全部转调 AdminService（进程内直调：禁用=即时踢线+引用方 0x75；解绑=清理全集+同 MAC 重注册
// 全新身份；重置码=RemoteCodeGenerator 换值[旧码 4003]+0x75+0x41，复用 0x14 自助路径同一执行链）。
// 在线态取 DeviceRegistry（连接级真相源，与库行禁用态正交）。
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using P2P.Server.Data;
using P2P.Server.Services;

namespace P2P.Server.Web;

/// <summary>设备管理端点组（挂 ServerWebHost 装配的 WebApplication，认证中间件之后）。</summary>
public sealed class AdminDevicesApi(
    IDbContextFactory<AppDbContext> dbFactory,
    DeviceRegistry registry,
    AdminService admin)
{
    public void Map(WebApplication app)
    {
        app.MapGet("/api/devices", async ctx =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ctx.RequestAborted);
            var rows = await db.Devices.AsNoTracking()
                .OrderBy(d => d.CreatedAt).ThenBy(d => d.DeviceName)
                .Select(d => new
                {
                    d.Id,
                    d.DeviceName,
                    d.Os,
                    d.RemoteCode,
                    d.VirtualIp,
                    d.Disabled,
                    d.CreatedAt,
                    OwnerUsername = db.Users.Where(u => u.Id == d.OwnerUserId)
                        .Select(u => u.Username).FirstOrDefault(),
                    Groups = db.GroupMembers.Where(m => m.DeviceId == d.Id)
                        .Join(db.Groups, m => m.GroupId, g => g.Id, (m, g) => g.Name).ToArray(),
                })
                .ToListAsync(ctx.RequestAborted);
            // 在线=连接级真相源（registry），与库行禁用态正交：禁用未踢线的离线行两者可同时为真
            var items = rows.Select(d => new
            {
                deviceId = d.Id,
                d.DeviceName,
                d.Os,
                d.RemoteCode,
                d.VirtualIp,
                d.OwnerUsername,
                d.Groups,
                online = registry.IsOnline(d.Id),
                d.Disabled,
                d.CreatedAt,
            });
            await WriteAsync(ctx, StatusCodes.Status200OK, 0, "ok", new { items });
        });

        app.MapPost("/api/devices/{id}/disable", async ctx =>
        {
            var device = await FindAsync(ctx);
            if (device is null) return;
            if (!await admin.DisableDeviceAsync(device.MacCode)) // 即时踢线+0x75（进程内直调）
            {
                await WriteAsync(ctx, StatusCodes.Status404NotFound, 1002, "设备不存在");
                return;
            }
            await WriteAsync(ctx, StatusCodes.Status200OK, 0, "ok");
        });

        app.MapPost("/api/devices/{id}/enable", async ctx =>
        {
            var device = await FindAsync(ctx);
            if (device is null) return;
            if (!await admin.EnableDeviceAsync(device.MacCode))
            {
                await WriteAsync(ctx, StatusCodes.Status404NotFound, 1002, "设备不存在");
                return;
            }
            await WriteAsync(ctx, StatusCodes.Status200OK, 0, "ok");
        });

        app.MapPost("/api/devices/{id}/unbind", async ctx =>
        {
            var device = await FindAsync(ctx);
            if (device is null) return;
            if (!await admin.UnbindDeviceAsync(device.MacCode)) // 在线也解+清理全集（FR-S-103 收口）
            {
                await WriteAsync(ctx, StatusCodes.Status404NotFound, 1002, "设备不存在");
                return;
            }
            await WriteAsync(ctx, StatusCodes.Status200OK, 0, "ok");
        });

        app.MapPost("/api/devices/{id}/reset-remote-code", async ctx =>
        {
            var device = await FindAsync(ctx);
            if (device is null) return;
            var remoteCode = await admin.ResetDeviceRemoteCodeAsync(device.Id);
            if (remoteCode is null)
            {
                await WriteAsync(ctx, StatusCodes.Status404NotFound, 1002, "设备不存在");
                return;
            }
            await WriteAsync(ctx, StatusCodes.Status200OK, 0, "ok", new { remoteCode }); // 新码一次返回
        });
    }

    /// <summary>按路由 id 查行（Guid 非法 400/无行 404，口径同 AdminUsersApi）。</summary>
    private async Task<Device?> FindAsync(HttpContext ctx)
    {
        if (!Guid.TryParse(ctx.Request.RouteValues["id"]?.ToString(), out var deviceId))
        {
            await WriteAsync(ctx, StatusCodes.Status400BadRequest, 1001, "参数错误");
            return null;
        }
        await using var db = await dbFactory.CreateDbContextAsync(ctx.RequestAborted);
        var device = await db.Devices.AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == deviceId, ctx.RequestAborted);
        if (device is null)
            await WriteAsync(ctx, StatusCodes.Status404NotFound, 1002, "设备不存在");
        return device;
    }

    private static async Task WriteAsync(HttpContext ctx, int status, int code, string message, object? data = null)
    {
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(new { code, message, data });
    }
}

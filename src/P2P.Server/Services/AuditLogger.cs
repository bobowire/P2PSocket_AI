using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using P2P.Server.Data;

namespace P2P.Server.Services;

/// <summary>
/// 审计事件写入（NFR-54、SEC-51）：register|register_recover|unbind|login|…。
/// detail 为 JSON 摘要（AI-17：不含密钥/凭据材料）。
/// </summary>
public sealed class AuditLogger(IDbContextFactory<AppDbContext> dbFactory, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task WriteAsync(string @event, Guid? deviceId = null, Guid? userId = null,
        object? detail = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.AuditLogs.Add(new AuditLog
        {
            Ts = _time.GetLocalNow().UtcDateTime,
            Event = @event,
            DeviceId = deviceId,
            UserId = userId,
            Detail = detail is null ? null : JsonSerializer.Serialize(detail),
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

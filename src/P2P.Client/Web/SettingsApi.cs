// M1-32 设置端点（04 §2.1、FR-C-808、OQ-1）：
// - GET /api/settings：settings.json 全量（无机密字段）；
// - PUT /api/settings：部分修改（缺省字段保持现值）→ SettingsStore 校验后原子落盘（非法值不写盘，
//   NFR-35 同口径）→ serverAddrs 即时生效（控制通道换址重连）；localWebPort 变更返回 restartRequired
//   （监听端口重启后生效）；punchConcurrency/keepaliveSec 落盘，对后续打洞会话生效。
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using P2P.Client.Control;
using P2P.Client.Storage;

namespace P2P.Client.Web;

public static class SettingsApi
{
    public static IEndpointRouteBuilder MapSettingsApi(this IEndpointRouteBuilder app,
        ControlClient control, SettingsStore settings)
    {
        app.MapGet("/api/settings", () => Api.Ok(View(settings.Settings)));

        app.MapPut("/api/settings", async (SettingsUpdateRequest body, CancellationToken ct) =>
        {
            try
            {
                var current = settings.Settings;
                var merged = new ClientSettings
                {
                    ServerAddrs = body.ServerAddrs ?? current.ServerAddrs,
                    LocalWebPort = body.LocalWebPort ?? current.LocalWebPort,
                    PunchConcurrency = body.PunchConcurrency ?? current.PunchConcurrency,
                    KeepaliveSec = body.KeepaliveSec ?? current.KeepaliveSec,
                    Reconnect = current.Reconnect,
                };
                await settings.SaveAsync(merged, ct); // 校验失败抛 ConfigValidationException → 1001 带字段明细

                var serverAddrsChanged = !merged.ServerAddrs.SequenceEqual(current.ServerAddrs);
                if (serverAddrsChanged)
                    control.UpdateServerAddrs(merged.ServerAddrs); // 即时生效：换表断连重连（M1-30 退避唤醒）

                return Api.Ok(new
                {
                    restartRequired = merged.LocalWebPort != current.LocalWebPort,
                    serverAddrsChanged,
                    settings = View(merged),
                });
            }
            catch (Exception e) { return Api.Fail(e); }
        });

        return app;
    }

    private static object View(ClientSettings s) => new
    {
        s.ServerAddrs,
        s.LocalWebPort,
        s.PunchConcurrency,
        s.KeepaliveSec,
        s.Reconnect,
    };

    /// <summary>部分更新体（null=保持现值；04 §2.1）。</summary>
    public sealed record SettingsUpdateRequest(
        string[]? ServerAddrs, int? LocalWebPort, int? PunchConcurrency, int? KeepaliveSec);
}

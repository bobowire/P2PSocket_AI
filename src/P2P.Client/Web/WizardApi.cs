// M1-29 首启向导端点（04 §2.2、FR-C-101~104）：
// - server-test：TCP 连通性探测（复用 M1-24 后端）；
// - register：三模式 default（仅注册设备）|account（注册+建号+登录+建组）|invite（M2）；
//   换址路径：settings 落盘 → ControlClient 换表断连重连（主候选不同才断）→ 等 NeedRegister →
//   0x10 注册（deviceName 进 0x10）→ account 续 0x20/0x21/0x50；
// - result：最近注册结果缓存（LocalApiContext，进程内展示态）。
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using P2P.Client.Control;
using P2P.Client.Registration;
using P2P.Client.Storage;
using P2P.Core.Protocol;

namespace P2P.Client.Web;

public static class WizardApi
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(15);

    public static IEndpointRouteBuilder MapWizardApi(this IEndpointRouteBuilder app,
        ControlClient control, ClientRegistrationService registration,
        StateStore store, SettingsStore settings, LocalApiContext ctx)
    {
        app.MapPost("/api/wizard/server-test", async (ServerTestRequest body, CancellationToken ct) =>
        {
            try
            {
                var addr = body.ServerAddr?.Trim() ?? "";
                if (addr.Length == 0) throw new ApiException(ErrorCode.BadRequest, "serverAddr 不可为空");
                var (ok, detail) = await ClientRegistrationService.TestConnectivityAsync([addr], ct: ct);
                return Api.Ok(new { ok, detail });
            }
            catch (Exception e) { return Api.Fail(e); }
        });

        app.MapPost("/api/wizard/register", async (WizardRegisterRequest body, CancellationToken ct) =>
        {
            try
            {
                if (store.State.IsRegistered)
                    throw new ApiException(ErrorCode.Conflict, "设备已注册（如需重新注册请先清除本机状态）");
                var serverAddr = body.ServerAddr?.Trim() ?? "";
                if (serverAddr.Length == 0)
                    throw new ApiException(ErrorCode.BadRequest, "serverAddr 不可为空");
                var mode = body.Mode ?? "default";
                if (mode != "default" && mode != "account" && mode != "invite")
                    throw new ApiException(ErrorCode.BadRequest, "mode 须为 default|invite|account");
                if (mode == "invite")
                    throw new ApiException(ErrorCode.BadRequest, "邀请码注册（FR-C-102 路径 b）M2 提供");
                var username = body.Username?.Trim() ?? "";
                var password = body.Password ?? "";
                if (mode == "account" && (username.Length is < 1 or > 64 || password.Length < 6))
                    throw new ApiException(ErrorCode.BadRequest, "account 模式须提供用户名与至少 6 字符密码");

                // 地址选定即生效：settings 落盘（重启沿用）+ 控制通道换表（主候选不同才断连重连）
                await settings.SaveAsync(new ClientSettings
                {
                    ServerAddrs = [serverAddr],
                    LocalWebPort = settings.Settings.LocalWebPort,
                    PunchConcurrency = settings.Settings.PunchConcurrency,
                    KeepaliveSec = settings.Settings.KeepaliveSec,
                    Reconnect = settings.Settings.Reconnect,
                }, ct);
                if (control.ServerAddrs.FirstOrDefault() != serverAddr)
                    control.UpdateServerAddrs([serverAddr]);

                // 等握手停在 NeedRegister（未注册设备唯一停留态，02 §2.2）
                var deadline = Environment.TickCount64 + (long)ReadyTimeout.TotalMilliseconds;
                while (control.State != ControlClientState.NeedRegister)
                {
                    if (Environment.TickCount64 >= deadline || ct.IsCancellationRequested)
                        throw new ApiException(ErrorCode.ServerUnreachable, "等待服务端就绪超时");
                    await Task.Delay(200, ct);
                }

                ctx.WizardInProgress = true;
                try
                {
                    var result = await registration.RegisterAsync(null, body.DeviceName, ct);
                    if (mode == "account")
                    {
                        var created = await registration.CreateUserAsync(username, password, ct);
                        if (!created.Ok) throw new ApiException(ErrorCode.BadRequest, "用户名已存在或不可用");
                        var login = await registration.LoginBindAsync(username, password, ct);
                        if (!login.Ok) throw new ApiException(ErrorCode.Unauthorized, "注册后自动登录失败");
                        await registration.CreateGroupAsync("我的分组", JoinPolicy.Free, ct);
                        ctx.LoginUser = username;
                    }
                    ctx.LastRegistration = result;
                    return Api.Ok(ResultView(result));
                }
                finally { ctx.WizardInProgress = false; }
            }
            catch (Exception e) { return Api.Fail(e); }
        });

        app.MapGet("/api/wizard/result", () =>
        {
            try
            {
                var result = ctx.LastRegistration
                    ?? throw new ApiException(ErrorCode.NotFound, "尚无注册结果（未完成向导）");
                return Api.Ok(ResultView(result));
            }
            catch (Exception e) { return Api.Fail(e); }
        });

        return app;
    }

    private static object ResultView(RegistrationResult r) => new
    {
        deviceId = r.DeviceId,
        remoteCode = r.RemoteCode,
        virtualIp = r.VirtualIp,
        groups = r.Groups.Select(g => new { groupId = g.GroupId, groupName = g.GroupName }),
    };

    public sealed record ServerTestRequest(string? ServerAddr);

    public sealed record WizardRegisterRequest(
        string? ServerAddr, string? Mode, string? InviteCode,
        string? Username, string? Password, string? DeviceName);
}

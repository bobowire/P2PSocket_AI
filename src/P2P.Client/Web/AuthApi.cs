// M1-29 账号端点（04 §2.3，转发 0x20~0x22）：
// - register/login/logout/me；登录成功切能力模式（ControlClient 镜像维护，WS login_state 事件源）；
// - login Ack.Ok=false → 2001（服务端不降级、客户端亦不切模式，02 §2.5）；
// - logout → passive（FR-C-603 不重启）。
// change-password（0x23）→ M2。
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using P2P.Client.Control;
using P2P.Client.Registration;
using P2P.Core.Protocol;

namespace P2P.Client.Web;

public static class AuthApi
{
    public static IEndpointRouteBuilder MapAuthApi(this IEndpointRouteBuilder app,
        ControlClient control, ClientRegistrationService registration, LocalApiContext ctx)
    {
        app.MapPost("/api/auth/register", async (CredentialsRequest body, CancellationToken ct) =>
        {
            try
            {
                var (username, password) = ValidateCredentials(body);
                var ack = await registration.CreateUserAsync(username, password, ct);
                if (!ack.Ok) throw new ApiException(ErrorCode.BadRequest, "用户名已存在或不可用");
                return Api.Ok(new { username });
            }
            catch (Exception e) { return Api.Fail(e); }
        });

        app.MapPost("/api/auth/login", async (CredentialsRequest body, CancellationToken ct) =>
        {
            try
            {
                var (username, password) = ValidateCredentials(body);
                var ack = await registration.LoginBindAsync(username, password, ct);
                if (!ack.Ok) throw new ApiException(ErrorCode.Unauthorized, "账号或密码错误");
                ctx.LoginUser = username; // ControlClient 已切模式 → CapabilityChanged 推 login_state
                return Api.Ok(new { username, mode = ack.Mode == CapabilityMode.Normal ? "normal" : "passive" });
            }
            catch (Exception e) { return Api.Fail(e); }
        });

        app.MapPost("/api/auth/logout", async (CancellationToken ct) =>
        {
            try
            {
                var ack = await control.SendRequestAsync<UserLogoutAck>(new UserLogout(
                    control.NextSeq(), control.TimestampMs(), MsgType.UserLogout), ct);
                if (!ack.Ok) throw new ApiException(ErrorCode.BadRequest, "登出失败");
                ctx.LoginUser = null;
                return Api.Ok(null);
            }
            catch (Exception e) { return Api.Fail(e); }
        });

        app.MapGet("/api/auth/me", () => Api.Ok(new
        {
            username = ctx.LoginUser,
            mode = control.Capability == CapabilityMode.Normal ? "normal" : "passive",
        }));

        return app;
    }

    /// <summary>凭据校验（05 §3：密码 ≥6 字符）。</summary>
    private static (string Username, string Password) ValidateCredentials(CredentialsRequest body)
    {
        var username = body.Username?.Trim() ?? "";
        var password = body.Password ?? "";
        if (username.Length is < 1 or > 64)
            throw new ApiException(ErrorCode.BadRequest, "用户名长度须为 1~64 字符");
        if (password.Length < 6)
            throw new ApiException(ErrorCode.BadRequest, "密码至少 6 字符");
        return (username, password);
    }

    public sealed record CredentialsRequest(string? Username, string? Password);
}

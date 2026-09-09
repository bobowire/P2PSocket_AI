// M1-29 本地 API 公共设施（04 §1）：envelope 包裹与异常→业务码映射。
// 各端点模块（System/Auth/Wizard/Mapping/Diagnostics）共用；业务错误 HTTP 恒 200，code 表 04 §5。
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using P2P.Client.Control;
using P2P.Client.Mapping;
using P2P.Core.Protocol;

namespace P2P.Client.Web;

/// <summary>本地 API 业务异常（envelope code 携带者；码表=04 §5）。</summary>
public sealed class ApiException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

internal static class Api
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // 不忽略 null：契约字段恒在（04 §2 响应形状稳定，username/currentPunchPeer 等空值显式 null）
    };

    public static IResult Ok(object? data) => Results.Json(
        new { code = ErrorCode.Ok, msg = "ok", data }, Json);

    /// <summary>异常 → envelope code（04 §5）：本地业务/服务端拒绝原码透传，passive 2002，
    /// 通道断 5003，其余按 1001 兜底（不向 HTTP 层泄漏堆栈）。</summary>
    public static IResult Fail(Exception e) => Results.Json(new
    {
        code = e switch
        {
            ApiException ae => ae.Code,
            MappingException me => me.Code,
            ControlErrorException ce => ce.Code,
            PassiveModeException => ErrorCode.ForbiddenPassive,
            ControlClientException => ErrorCode.ServerUnreachable,
            _ => ErrorCode.BadRequest,
        },
        msg = e.Message,
        data = (object?)null,
    }, Json);
}

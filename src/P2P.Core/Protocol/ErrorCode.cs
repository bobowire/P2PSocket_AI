namespace P2P.Core.Protocol;

/// <summary>错误码常量（04 §5 总表，唯一来源；只增码不改义，AI-14）。</summary>
public static class ErrorCode
{
    public const int Ok = 0;
    public const int BadRequest = 1001;
    public const int NotFound = 1002;
    public const int Conflict = 1003;
    public const int Unauthorized = 2001;
    public const int ForbiddenPassive = 2002;   // 哑节点发起主动类操作（SEC-51，写审计）
    public const int Forbidden = 2003;
    public const int RegistrationClosed = 2004;
    public const int GroupNotFound = 3001;      // 兼 INVITE_INVALID
    public const int GroupNeedApproval = 3002;
    public const int TargetNotAuthorized = 4001; // L2 失败
    public const int TargetAddrNotAllowed = 4002; // L3 失败
    public const int RemoteCodeInvalid = 4003;
    public const int DeviceActive = 4004;        // OQ-14：同 macCode 重注册且原设备在线
    public const int TargetOffline = 4005;       // OQ-18/TD-19：打洞目标离线/邀请不可达
    public const int PunchFailed = 5001;
    public const int RelayDisabled = 5002;
    public const int ServerUnreachable = 5003;
    public const int VersionNotSupported = 5004;
    public const int TimeSkew = 5005;            // OQ-12：ts 超 ±120s 窗口，附 serverTs

    /// <summary>错误码 → 标准文案（0x7E Error.httpLikeMsg 与 HTTP 响应共用语义，04 §5）。</summary>
    public static string Describe(int code) => code switch
    {
        Ok => "ok",
        BadRequest => "bad_request",
        NotFound => "not_found",
        Conflict => "conflict",
        Unauthorized => "unauthorized",
        ForbiddenPassive => "forbidden_passive",
        Forbidden => "forbidden",
        RegistrationClosed => "registration_closed",
        GroupNotFound => "group_not_found",
        GroupNeedApproval => "group_need_approval",
        TargetNotAuthorized => "target_not_authorized",
        TargetAddrNotAllowed => "target_addr_not_allowed",
        RemoteCodeInvalid => "remote_code_invalid",
        DeviceActive => "device_active",
        TargetOffline => "target_offline",
        PunchFailed => "punch_failed",
        RelayDisabled => "relay_disabled",
        ServerUnreachable => "server_unreachable",
        VersionNotSupported => "version_not_supported",
        TimeSkew => "time_skew",
        _ => $"unknown_{code}",
    };
}

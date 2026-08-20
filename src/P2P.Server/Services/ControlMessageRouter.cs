using P2P.Core.Protocol;

namespace P2P.Server.Services;

/// <summary>
/// 控制消息分发器（05 §5）：ControlSession 完成帧校验后按 msgType 路由到处理器。
/// M1-14 挂载注册族；用户/分组/映射/信令在 M1-15~17 逐任务挂载。
/// </summary>
public sealed class ControlMessageRouter
{
    private readonly RegistrationService _registration;

    public ControlMessageRouter(RegistrationService registration) => _registration = registration;

    public async Task DispatchAsync(ControlSession session, IPcpMessage message)
    {
        switch (message)
        {
            case Register reg:
                await _registration.HandleRegisterAsync(session, reg);
                break;
            case UnbindMe unbind:
                await _registration.HandleUnbindAsync(session, unbind);
                break;
            default:
                // 已登记但处理器未挂载：诚实拒绝（避免客户端无限等待）
                await session.SendErrorAsync(ErrorCode.BadRequest, "not_implemented");
                break;
        }
    }
}

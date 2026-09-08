using P2P.Core.Protocol;

namespace P2P.Server.Services;

/// <summary>
/// 控制消息分发器（05 §5）：ControlSession 完成帧校验后按 msgType 路由到处理器。
/// passive 主动类拦截（02 §2.5，SEC-51）在路由入口统一执行。
/// M1-14/15 挂载注册/用户族；分组/映射/信令在 M1-16~17 逐任务挂载。
/// </summary>
public sealed class ControlMessageRouter
{
    private readonly RegistrationService _registration;
    private readonly UserService _user;
    private readonly GroupService _group;
    private readonly AuditLogger _audit;

    public ControlMessageRouter(RegistrationService registration, UserService user,
        GroupService group, AuditLogger audit)
    {
        _registration = registration;
        _user = user;
        _group = group;
        _audit = audit;
    }

    public async Task DispatchAsync(ControlSession session, IPcpMessage message)
    {
        // 主动类 passive 拒绝（02 §2.5 清单：0x20/0x21/0x23/0x40/0x50~0x57/0x60/0x70）
        if (session.Capability == CapabilityMode.Passive && IsActiveClass(message.MsgType))
        {
            await session.SendErrorAsync(ErrorCode.ForbiddenPassive, "forbidden_passive");
            await _audit.WriteAsync("passive_deny", session.DeviceId, detail: new { msgType = message.MsgType });
            return;
        }

        switch (message)
        {
            case Register reg:
                await _registration.HandleRegisterAsync(session, reg);
                break;
            case UnbindMe unbind:
                await _registration.HandleUnbindAsync(session, unbind);
                break;
            case UserRegister userRegister:
                await _user.HandleUserRegisterAsync(session, userRegister);
                break;
            case UserLogin login:
                await _user.HandleLoginAsync(session, login);
                break;
            case UserLogout logout:
                await _user.HandleLogoutAsync(session, logout);
                break;
            case DeviceUpdate deviceUpdate:
                await _user.HandleDeviceUpdateAsync(session, deviceUpdate);
                break;
            case GroupCreate groupCreate:
                await _group.HandleCreateAsync(session, groupCreate);
                break;
            case GroupUpdate groupUpdate:
                await _group.HandleUpdateAsync(session, groupUpdate);
                break;
            case GroupDissolve groupDissolve:
                await _group.HandleDissolveAsync(session, groupDissolve);
                break;
            case DeviceListRequest deviceList:
                await _group.HandleDeviceListAsync(session, deviceList);
                break;
            default:
                // 已登记但处理器未挂载：诚实拒绝（避免客户端无限等待）
                await session.SendErrorAsync(ErrorCode.BadRequest, "not_implemented");
                break;
        }
    }

    /// <summary>02 §2.5 主动类消息清单（服务端拒绝点；0x22 登出为降级操作不在此列）。</summary>
    internal static bool IsActiveClass(byte msgType) => msgType is
        MsgType.UserRegister or MsgType.UserLogin or MsgType.UserChangePassword
        or MsgType.DeviceList
        or MsgType.GroupCreate or MsgType.GroupJoin or MsgType.GroupLeave
        or MsgType.JoinRequests or MsgType.GroupInviteGen or MsgType.GroupUpdate
        or MsgType.GroupDissolve or MsgType.GroupRemoveMember
        or MsgType.MappingUpsert or MsgType.MappingDelete
        or MsgType.PunchRequest;
}

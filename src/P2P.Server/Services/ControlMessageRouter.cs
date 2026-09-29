using P2P.Core.Protocol;

namespace P2P.Server.Services;

/// <summary>
/// 控制消息分发器（05 §5）：ControlSession 完成帧校验后按 msgType 路由到处理器。
/// passive 主动类拦截（02 §2.5，SEC-51）在路由入口统一执行。
/// M1-14~17 挂载注册/用户/分组/信令族；M1-28 挂载映射 0x60/0x61；
/// M2-07 挂载中继 0x74 RelayAllocate；M2-19 挂载回切 0x73 PunchRetry；
/// M2-08 挂载上报族 0x62/0x64/0x72（审计/流量累计/打洞结果）；
/// M2-09 挂载分组全量 0x51/0x52/0x53/0x54/0x57（凭码入组/自退/审批/邀请码/移出）；
/// M2-11 挂载白名单 0x63 LanSegmentsUpsert（passive 允许，本机管理类）；
/// M2-12 挂载 0x14 RemoteCodeReset（远程码重置；passive 允许，本机管理类）。
/// </summary>
public sealed class ControlMessageRouter
{
    private readonly RegistrationService _registration;
    private readonly UserService _user;
    private readonly GroupService _group;
    private readonly SignalingCoordinator _signaling;
    private readonly MappingService _mappings;
    private readonly LanSegmentService? _lanSegments; // 可选：既有测试夹具不发 0x63 时可省
    private readonly RelayService _relay;
    private readonly StatsService _stats;
    private readonly AuditLogger _audit;

    public ControlMessageRouter(RegistrationService registration, UserService user,
        GroupService group, SignalingCoordinator signaling, MappingService mappings,
        RelayService relay, StatsService stats, AuditLogger audit,
        LanSegmentService? lanSegments = null)
    {
        _registration = registration;
        _user = user;
        _group = group;
        _signaling = signaling;
        _mappings = mappings;
        _lanSegments = lanSegments;
        _relay = relay;
        _stats = stats;
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
            case RemoteCodeReset codeReset: // 0x14 远程码重置（M2-12，FR-S-903；passive 允许，不入主动类清单）
                await _registration.HandleRemoteCodeResetAsync(session, codeReset);
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
            case GroupJoin groupJoin: // 0x51 凭码入组（M2-09，FR-S-303/306）
                await _group.HandleJoinAsync(session, groupJoin);
                break;
            case GroupLeave groupLeave: // 0x52 设备自退（M2-09，FR-S-307）
                await _group.HandleLeaveAsync(session, groupLeave);
                break;
            case JoinRequests joinRequests: // 0x53 审批队列 List/Approve/Reject（M2-09，FR-S-305）
                await _group.HandleJoinRequestsAsync(session, joinRequests);
                break;
            case GroupInviteGen groupInviteGen: // 0x54 邀请码生成/撤销（M2-09，OQ-17）
                await _group.HandleInviteGenAsync(session, groupInviteGen);
                break;
            case GroupRemoveMember groupRemoveMember: // 0x57 所有者移出成员（M2-09，FR-S-307）
                await _group.HandleRemoveMemberAsync(session, groupRemoveMember);
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
            case PunchRequest punchRequest:
                await _signaling.HandlePunchRequestAsync(session, punchRequest);
                break;
            case PunchEndpoint punchEndpoint:
                await _signaling.HandlePunchEndpointAsync(session, punchEndpoint);
                break;
            case PunchRetry punchRetry: // 0x73 中继回切协调（M2-19，02 §6.2/OQ-7）
                await _signaling.HandlePunchRetryAsync(session, punchRetry);
                break;
            case RelayAllocate relayAllocate:
                await _relay.HandleAllocateAsync(session, relayAllocate);
                break;
            case PunchResult punchResult: // 0x72 打洞结果 → punch_stats（M2-08，03 §2.9）
                await _signaling.HandlePunchResultAsync(session, punchResult);
                break;
            case MappingStatus mappingStatus: // 0x62 映射状态 → 审计流水（M2-08）
                await _stats.HandleMappingStatusAsync(session, mappingStatus);
                break;
            case StatsReport statsReport: // 0x64 流量累计 → mapping_stats 覆盖式 upsert（M2-08）
                await _stats.HandleStatsReportAsync(session, statsReport);
                break;
            case MappingUpsert mappingUpsert:
                await _mappings.HandleUpsertAsync(session, mappingUpsert);
                break;
            case MappingDelete mappingDelete:
                await _mappings.HandleDeleteAsync(session, mappingDelete);
                break;
            case LanSegmentsUpsert lanSegmentsUpsert: // 0x63 白名单（M2-11；passive 允许，不入主动类清单）
                if (_lanSegments is null)
                    goto default; // 已登记未挂载：诚实拒绝（避免客户端无限等待）
                await _lanSegments.HandleUpsertAsync(session, lanSegmentsUpsert);
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

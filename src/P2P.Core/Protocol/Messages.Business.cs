using MessagePack;

namespace P2P.Core.Protocol;

// ── 共享载荷（02 §2.4 多消息复用）───────────────────────────────────────

/// <summary>公网端点（STUN 探测所得；host 为 IP 字面量）。</summary>
[MessagePackObject]
public sealed record Endpoint(
    [property: Key(0)] string Host,
    [property: Key(1)] ushort Port);

/// <summary>端点组 {udp?, tcp?}——两段式打洞载荷（OQ-18/TD-19）：探测得哪个带哪个。</summary>
[MessagePackObject]
public sealed record EndpointPair(
    [property: Key(0)] Endpoint? Udp,
    [property: Key(1)] Endpoint? Tcp);

/// <summary>对端设备信息（0x70 Ack 与 0x71 的 peer 载荷；staticPubKey 供 PTP 双重 ECDH）。</summary>
[MessagePackObject]
public sealed record PeerInfo(
    [property: Key(0)] Guid DeviceId,
    [property: Key(1)] string DeviceName,
    [property: Key(2)] string RemoteCode,
    [property: Key(3)] byte[] StaticPubKey);

// ── 0x03 升级信息（FR-S-804/OQ-5：稳定接口永不变更，02 §7 冻结字段）────────

/// <summary>0x03 C→S：请求升级信息——无附加载荷（版本协商失败后仍可发送的唯一消息，02 §2.3）。</summary>
[MessagePackObject]
public sealed record UpdateInfoRequest(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType) : IPcpMessage;

/// <summary>0x03 S→C：最新版本号 + 协议兼容范围 [min, max] + 升级指引 URL/文本。
/// 尾部可选字段 artifactSha256（v1.79/M5-14 落地，SEC-84/07 §14.4）：产物 SHA-256 清单
/// JSON 字符串（{产物名→小写hex64}），升级页按平台取项展示；缺省/旧服务端=null=无清单。</summary>
[MessagePackObject]
public sealed record UpdateInfoResponse(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] string LatestVersion,
    [property: Key(4)] ushort MinProtocol,
    [property: Key(5)] ushort MaxProtocol,
    [property: Key(6)] string UpgradeUrl,
    [property: Key(7)] string Notes,
    [property: Key(8)] string? ArtifactSha256 = null) : IPcpMessage;

// ── 0x13 设备改名（02 §2.4：本机管理类，passive 允许）────────────────────

[MessagePackObject]
public sealed record DeviceUpdate(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] string DeviceName) : IPcpMessage;

[MessagePackObject]
public sealed record DeviceUpdateAck(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] bool Ok) : IPcpMessage;

// ── 0x14 远程码重置（FR-S-903/SEC-25：本机管理类，passive 允许）────────────

/// <summary>0x14 C→S：请求重置本机远程码（凭连接级设备身份认证，无附加载荷）。</summary>
[MessagePackObject]
public sealed record RemoteCodeReset(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType) : IPcpMessage;

/// <summary>0x14 S→C：成功时 NewRemoteCode 非空（旧码立即失效，相关方 0x75 通知）。</summary>
[MessagePackObject]
public sealed record RemoteCodeResetAck(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] bool Ok,
    [property: Key(4)] string? NewRemoteCode) : IPcpMessage;

// ── 0x20~0x23 用户族（FR-S-201~205）────────────────────────────────────

/// <summary>0x20 C→S。v1.67/M5（SEC-26/07 §10.2）：密码经 PasswordBox（ECIES 密文，info="pcp-login-v1"）；
/// PasswordBox 缺失或明文 Password 非空 → 5004（OQ-21① 硬切，无明文过渡双轨）。</summary>
[MessagePackObject]
public sealed record UserRegister(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] string Username,
    [property: Key(4)] string Password,
    [property: Key(5)] byte[]? PasswordBox = null) : IPcpMessage;

[MessagePackObject]
public sealed record UserRegisterAck(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] bool Ok) : IPcpMessage;

/// <summary>0x21 C→S（同 0x20 封装口径，SEC-26/07 §10.2；记住我重放每次现加密非密文重放）。</summary>
[MessagePackObject]
public sealed record UserLogin(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] string Username,
    [property: Key(4)] string Password,
    [property: Key(5)] byte[]? PasswordBox = null) : IPcpMessage;

/// <summary>能力模式（PRD 05 §3）：normal=主动+被动；passive=哑节点。</summary>
public enum CapabilityMode : byte
{
    Normal = 0,
    Passive = 1,
}

/// <summary>登录 Ack：能力模式 + token（本地 Web 展示登录态用，02 §2.4）。</summary>
[MessagePackObject]
public sealed record UserLoginAck(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] bool Ok,
    [property: Key(4)] CapabilityMode Mode,
    [property: Key(5)] string Token) : IPcpMessage;

[MessagePackObject]
public sealed record UserLogout(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType) : IPcpMessage;

[MessagePackObject]
public sealed record UserLogoutAck(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] bool Ok) : IPcpMessage;

/// <summary>0x23 C→S：用户修改自己密码（FR-S-205；主动类——passive 拒绝 2002）。
/// v1.67/M5（SEC-26/07 §10.2）：old/new 密码经 OldPasswordBox/NewPasswordBox（ECIES 密文，
/// info="pcp-login-v1"）；任一缺失或明文字段非空 → 5004（OQ-21① 硬切）。</summary>
[MessagePackObject]
public sealed record UserChangePassword(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] string OldPassword,
    [property: Key(4)] string NewPassword,
    [property: Key(5)] byte[]? OldPasswordBox = null,
    [property: Key(6)] byte[]? NewPasswordBox = null) : IPcpMessage;

[MessagePackObject]
public sealed record UserChangePasswordAck(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] bool Ok) : IPcpMessage;

// ── 0x40/0x41 设备列表（OQ-16 应用层分页；0x41 变更推送）──────────────────

[MessagePackObject]
public sealed record DeviceListRequest(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] uint Offset,
    [property: Key(4)] uint Limit) : IPcpMessage;

/// <summary>列表项：deviceId/deviceName/remoteCode/virtualIp/在线/分组/开放网段摘要。</summary>
[MessagePackObject]
public sealed record DeviceListItem(
    [property: Key(0)] Guid DeviceId,
    [property: Key(1)] string DeviceName,
    [property: Key(2)] string RemoteCode,
    [property: Key(3)] string VirtualIp,
    [property: Key(4)] bool Online,
    [property: Key(5)] string[] Groups,
    [property: Key(6)] string[] LanSegments);

[MessagePackObject]
public sealed record DeviceListResponse(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] uint Total,
    [property: Key(4)] DeviceListItem[] Items,
    [property: Key(5)] bool HasMore) : IPcpMessage;

/// <summary>0x41 S→C：列表变更提示帧（无附加载荷）——客户端据此 refetch 0x40（TD-16：提示非真相）。</summary>
[MessagePackObject]
public sealed record DeviceListUpdate(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType) : IPcpMessage;

// ── 0x42 分组列表（M2-27 定案：本地 /api/groups 的协议侧缺口——0x40 列表项仅回组名无
//    groupId/owner，分组页离开/编辑/解散/审批等 0x52~0x57 操作均需 groupId；加法演进 02 §7）──

/// <summary>0x42 C→S：请求分组列表——本设备已加入 ∪（已登录）账号自有分组（2026-11-22
/// 口径扩展，PRD 05 §1 v0.20；无附加载荷；主动类——passive 拒 2002）。</summary>
[MessagePackObject]
public sealed record GroupListRequest(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType) : IPcpMessage;

/// <summary>列表项：分组最小展示集（isOwner 驱动所有者操作可见性，FR-C-805）。</summary>
[MessagePackObject]
public sealed record GroupListItem(
    [property: Key(0)] Guid GroupId,
    [property: Key(1)] string GroupName,
    [property: Key(2)] JoinPolicy Policy,
    [property: Key(3)] bool IsOwner,
    [property: Key(4)] uint MemberCount);

[MessagePackObject]
public sealed record GroupListResponse(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] GroupListItem[] Items) : IPcpMessage;

// ── 0x50~0x57 分组管理（M2-03 补全 0x51~0x54/0x57 全族）──────────────────

/// <summary>准入策略（D9）：free=即入；approval=审批。</summary>
public enum JoinPolicy : byte
{
    Free = 0,
    Approval = 1,
}

[MessagePackObject]
public sealed record GroupCreate(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] string Name,
    [property: Key(4)] JoinPolicy Policy) : IPcpMessage;

[MessagePackObject]
public sealed record GroupCreateAck(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] Guid GroupId) : IPcpMessage;

/// <summary>0x51 C→S：凭码入组——free 即入 → Ack{groupId}；approval 建申请单 → Error 3002；码无效/撤销 → 3001。</summary>
[MessagePackObject]
public sealed record GroupJoin(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] string InviteCode) : IPcpMessage;

[MessagePackObject]
public sealed record GroupJoinAck(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] Guid GroupId) : IPcpMessage;

/// <summary>0x52 C→S：设备自行退出分组（登录态；服务端联动 0x75）。</summary>
[MessagePackObject]
public sealed record GroupLeave(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] Guid GroupId) : IPcpMessage;

[MessagePackObject]
public sealed record GroupLeaveAck(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] bool Ok) : IPcpMessage;

// ── 0x53 审批队列（FR-S-305：所有者侧 List/Approve/Reject 共用一个 msgType）──

/// <summary>审批队列操作判别。</summary>
public enum JoinRequestAction : byte
{
    List = 0,
    Approve = 1,
    Reject = 2,
}

/// <summary>待审批申请项（join_requests 表最小展示集，03 §2.3）。</summary>
[MessagePackObject]
public sealed record JoinRequestItem(
    [property: Key(0)] Guid RequestId,
    [property: Key(1)] Guid GroupId,
    [property: Key(2)] Guid DeviceId,
    [property: Key(3)] string DeviceName,
    [property: Key(4)] ulong CreatedAtMs);

/// <summary>0x53 C→S：List 携带 groupId；Approve/Reject 携带 requestId。</summary>
[MessagePackObject]
public sealed record JoinRequests(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] JoinRequestAction Action,
    [property: Key(4)] Guid? GroupId,
    [property: Key(5)] Guid? RequestId) : IPcpMessage;

/// <summary>0x53 S→C：队列列表（List 的应答）。</summary>
[MessagePackObject]
public sealed record JoinRequestsResponse(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] JoinRequestItem[] Items) : IPcpMessage;

/// <summary>0x53 S→C：Approve/Reject 操作确认。</summary>
[MessagePackObject]
public sealed record JoinRequestsAck(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] bool Ok) : IPcpMessage;

/// <summary>0x54 C→S：生成或撤销邀请码——Revoke=false 生成（Ack 携带新码），true 撤销（置 NULL）。</summary>
[MessagePackObject]
public sealed record GroupInviteGen(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] Guid GroupId,
    [property: Key(4)] bool Revoke) : IPcpMessage;

[MessagePackObject]
public sealed record GroupInviteGenAck(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] bool Ok,
    [property: Key(4)] string? InviteCode) : IPcpMessage;

/// <summary>所有者编辑分组：Name/Policy 为 null 表示不改（02 §2.4）。</summary>
[MessagePackObject]
public sealed record GroupUpdate(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] Guid GroupId,
    [property: Key(4)] string? Name,
    [property: Key(5)] JoinPolicy? Policy) : IPcpMessage;

[MessagePackObject]
public sealed record GroupUpdateAck(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] bool Ok) : IPcpMessage;

[MessagePackObject]
public sealed record GroupDissolve(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] Guid GroupId) : IPcpMessage;

[MessagePackObject]
public sealed record GroupDissolveAck(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] bool Ok) : IPcpMessage;

/// <summary>0x57 C→S：所有者将设备移出分组（FR-S-307；服务端联动 0x75）。</summary>
[MessagePackObject]
public sealed record GroupRemoveMember(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] Guid GroupId,
    [property: Key(4)] Guid MemberDeviceId) : IPcpMessage;

[MessagePackObject]
public sealed record GroupRemoveMemberAck(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] bool Ok) : IPcpMessage;

// ── 0x60~0x64 映射与统计（字段=PRD 06 §2 表）────────────────────────────

/// <summary>映射 Upsert：MappingId=null 新建，否则更新；TargetAddr="self" 或开放网段内 IP。</summary>
[MessagePackObject]
public sealed record MappingUpsert(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] Guid? MappingId,
    [property: Key(4)] string Name,
    [property: Key(5)] ushort LocalPort,
    [property: Key(6)] string Proto,          // "tcp" | "udp"（D19）
    [property: Key(7)] string TargetRemoteCode,
    [property: Key(8)] string TargetAddr,
    [property: Key(9)] ushort TargetPort,
    [property: Key(10)] bool Enabled) : IPcpMessage;

[MessagePackObject]
public sealed record MappingUpsertAck(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] Guid MappingId) : IPcpMessage;

[MessagePackObject]
public sealed record MappingDelete(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] Guid MappingId) : IPcpMessage;

[MessagePackObject]
public sealed record MappingDeleteAck(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] bool Ok) : IPcpMessage;

/// <summary>0x62 状态上报——M1 仅定义编解码，处理逻辑属 FR-C-404 → M2。</summary>
[MessagePackObject]
public sealed record MappingStatus(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] Guid MappingId,
    [property: Key(4)] string State,
    [property: Key(5)] string? Detail) : IPcpMessage;

/// <summary>0x63 C→S：开放内网段白名单——SegmentId=null 新建否则更新；Enabled=false 为移除语义（服务端联动 0x75；本机管理类 passive 允许）。</summary>
[MessagePackObject]
public sealed record LanSegmentsUpsert(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] Guid? SegmentId,
    [property: Key(4)] string Cidr,
    [property: Key(5)] bool Enabled) : IPcpMessage;

[MessagePackObject]
public sealed record LanSegmentsUpsertAck(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] Guid SegmentId) : IPcpMessage;

/// <summary>映射流量累计项（mapping_stats 表字段，03 §2.6；值 = 客户端本地累计）。</summary>
[MessagePackObject]
public sealed record StatsEntry(
    [property: Key(0)] Guid MappingId,
    [property: Key(1)] ulong BytesUp,
    [property: Key(2)] ulong BytesDown,
    [property: Key(3)] ulong RelayBytes);

/// <summary>0x64 C→S：批量上报（30s 周期 + 优雅停机补报；被动同步类 passive 允许，02 §2.5）。</summary>
[MessagePackObject]
public sealed record StatsReport(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] StatsEntry[] Entries) : IPcpMessage;

// ── 0x70~0x76 打洞与中继信令（OQ-18/TD-19 两段式；M2-03 补 0x73~0x75）────

/// <summary>
/// 0x70 C→S：请求与目标建立隧道；requesterEndpoints=发起方出队后即时 STUN 探测所得。
/// Ack 由服务端**延后**至被邀请方 0x76 端点就绪才下发（§5.1④）。
/// punchConcurrency=发起方本地配置 N（1~5 缺省 3，OQ-19/TD-20）——服务端校验后经
/// 0x71/0x70 Ack 的 PunchCount 统一回填（旧端不携带 → 尾部缺省容忍，02 §7）。
/// localCandidates=发起方本机直连候选（网卡 IPv4 × 打洞监听口，§5.4 直连优先；
/// 服务端经 0x71 透传给对端——同网段/对端公网可直达时先直连，旧端不携带同容忍）。
/// bearerCaps=发起方承载能力位图（M6-04，NET-77，设计 11 §8.1：服务端纯存转经 0x71
/// PeerBearerCaps 透传给对端；旧端不携带同容忍——缺省=0=不支持，混布自动降级裸 UDP）。
/// </summary>
[MessagePackObject]
public sealed record PunchRequest(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] Guid TargetDeviceId,
    [property: Key(4)] Guid? TriggerMappingId,
    [property: Key(5)] string Proto,
    [property: Key(6)] EndpointPair? RequesterEndpoints,
    [property: Key(7)] byte? PunchConcurrency,
    [property: Key(8)] Endpoint[]? LocalCandidates = null,
    [property: Key(9)] uint? BearerCaps = null) : IPcpMessage;

/// <summary>0x70 S→C（延后 Ack）：被邀请方端点就绪后携带对端信息与端点；
/// peerLocalCandidates=被邀请方 0x76 上报的本机直连候选透传（§5.4，旧端缺省容忍）；
/// peerBearerCaps=被邀请方 0x76 上报的承载能力位图透传（M6-04，设计 11 §8.1）。</summary>
[MessagePackObject]
public sealed record PunchRequestAck(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] Guid SessionId,
    [property: Key(4)] PeerInfo Peer,
    [property: Key(5)] EndpointPair PeerEndpoints,
    [property: Key(6)] byte PunchCount,
    [property: Key(7)] bool RelayAllowed,
    [property: Key(8)] Endpoint[]? PeerLocalCandidates = null,
    [property: Key(9)] uint? PeerBearerCaps = null) : IPcpMessage;

/// <summary>0x71 S→C（发往被邀请方）：发起方设备信息+端点；本端需监听的并发路数 N；
/// localCandidates=发起方 0x70 上报的本机直连候选透传（§5.4，旧端缺省容忍）；
/// peerBearerCaps=发起方 0x70 上报的承载能力位图透传（M6-04，设计 11 §8.1——被邀请方
/// 开火前的对端 caps 输入）。</summary>
[MessagePackObject]
public sealed record PunchInvite(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] Guid SessionId,
    [property: Key(4)] PeerInfo Peer,
    [property: Key(5)] EndpointPair PeerEndpoints,
    [property: Key(6)] byte PunchCount,
    [property: Key(7)] bool RelayAllowed,
    [property: Key(8)] Endpoint[]? LocalCandidates = null,
    [property: Key(9)] uint? PeerBearerCaps = null) : IPcpMessage;

/// <summary>0x72 打洞结果上报（M2-22 落库 punch_stats）。
/// bearer=直连承载类型（M6-08，NET-77 L2 包络验证：仅 Ok 直连成功携带——0=裸 UDP 承载、
/// 1=KCP 承载；中继/失败/旧端不携带 null）。服务端按此列统计 KCP 采用率与按承载分组的
/// 成功率/时长包络（基准报告 L1 丢包曲线的生产对照面）。</summary>
[MessagePackObject]
public sealed record PunchResult(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] Guid SessionId,
    [property: Key(4)] bool Ok,
    [property: Key(5)] Endpoint? Endpoint,
    [property: Key(6)] string? FailReason,
    [property: Key(7)] byte? Bearer = null) : IPcpMessage;

/// <summary>0x76 C→S：被邀请方回传自己的公网端点（收到 0x71 后即时 STUN 探测）；
/// localCandidates=本机直连候选（网卡 IPv4 × 打洞监听口，§5.4——经 0x70 Ack 透传给发起方）；
/// bearerCaps=被邀请方承载能力位图（M6-04，设计 11 §8.1——经 0x70 Ack PeerBearerCaps
/// 透传给发起方，服务端纯存转）。</summary>
[MessagePackObject]
public sealed record PunchEndpoint(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] Guid SessionId,
    [property: Key(4)] EndpointPair Endpoints,
    [property: Key(5)] Endpoint[]? LocalCandidates = null,
    [property: Key(6)] uint? BearerCaps = null) : IPcpMessage;

/// <summary>0x73 PunchRetry：中继回切直连（OQ-7）——C→S 访问方 60s 请求协调新打洞会话 / S→C 服务端通知双端重打；载荷均为原 sessionId。</summary>
[MessagePackObject]
public sealed record PunchRetry(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] Guid SessionId) : IPcpMessage;

/// <summary>0x77 S→C（发往被邀请方）：齐射锚点——服务端在向 A 下发 0x70 延后 Ack 的**同一时刻**
/// 推送给 B（02 §5.2⑤ 同锚点倒计时）；B 收到后与 A 各自从消息到达起倒数同一 Δ
///（<see cref="PunchPolicy.TcpVolleyCountdown"/>，双端共享常量）到点齐射——两消息同刻出发，
/// 开火时刻差≈两条控制连接的投递时差（远小于对端 RTT），SYN 在空中交叉，规避
/// 「B 结构性早打 A 空门」竞态。旧服务端不推送 → B 侧等待器超时兜底立即开火（行为同旧版，
/// 由波次补射覆盖时差）；旧客户端未知类型容忍降级（02 §7）。</summary>
[MessagePackObject]
public sealed record PunchFire(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] Guid SessionId) : IPcpMessage;

/// <summary>0x78 C→S：晚探测起跑申请（02 §5.2⑥，2026-10-24）——**不携带端点**（对端端点经
/// STUN-TCP 交换事务晚收集，探测贴近开火、基准映射被探针连接钉活）。服务端受理即同刻向双端
/// 下发 PunchStartGo（A=请求应答、B=推送），双端并行起 STUN-TCP 交换连接。主动类（passive 拒绝）。
/// 旧服务端未知 0x78 静默丢弃（02 §7）→ 客户端 2s 无应答即降级旧 0x70 流程（混布自由升级序）。</summary>
[MessagePackObject]
public sealed record PunchStart(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] Guid TargetDeviceId,
    [property: Key(4)] Guid? TriggerMappingId,
    [property: Key(5)] string Proto,
    [property: Key(6)] byte? PunchConcurrency = null) : IPcpMessage;

/// <summary>0x78 S→C（双端）：起跑指令——A 侧为 0x78 请求的即时应答（同族配对）、B 侧为推送；
/// 载荷={sessionId, 对端设备信息(含 staticPubKey), N, relayAllowed}。收到的端立即开 STUN-TCP
/// 交换连接（请求带 sessionId+本机候选，响应带对端映射端点+对端候选=开火锚点）。</summary>
[MessagePackObject]
public sealed record PunchStartGo(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] Guid SessionId,
    [property: Key(4)] PeerInfo Peer,
    [property: Key(5)] byte PunchCount,
    [property: Key(6)] bool RelayAllowed) : IPcpMessage;

/// <summary>0x74 C→S：申请中继会话（打洞失败且目标设备回退开启，OQ-10）。</summary>
[MessagePackObject]
public sealed record RelayAllocate(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] Guid SessionId) : IPcpMessage;

/// <summary>0x74 S→C：中继授权——relaySessionId 为 RLP 外层 8B 会话头标识（02 §6/TD-11）；Endpoints 双承载按需取用。</summary>
[MessagePackObject]
public sealed record RelayGrant(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] ulong RelaySessionId,
    [property: Key(4)] EndpointPair RelayEndpoints) : IPcpMessage;

// ── 0x75 失效推送（PRD 05 §4 全集；02 §2.4）──────────────────────────────

/// <summary>授权/状态失效原因（七值全枚举，02 §2.4 0x75）。</summary>
public enum InvalidationReason : byte
{
    /// <summary>← 0x22 用户登出。</summary>
    LoggedOut = 0,

    /// <summary>← 用户禁用（FR-S-204，携 newCapability 降级）。</summary>
    UserDisabled = 1,

    /// <summary>← 设备禁用（FR-S-105）。</summary>
    DeviceDisabled = 2,

    /// <summary>← 0x14 远程码重置（FR-S-903/SEC-25）。</summary>
    RemoteCodeReset = 3,

    /// <summary>← 0x63 白名单移除（FR-C-702）。</summary>
    LanSegmentRemoved = 4,

    /// <summary>← 0x52 退组。</summary>
    GroupLeft = 5,

    /// <summary>← 0x56 解散 / 0x57 被移出（组关系终止）。</summary>
    GroupDissolved = 6,
}

/// <summary>0x75 S→C：失效推送——客户端将受影响映射置 invalid 并停止转发；NewCapability 仅能力变化时携带（用户禁用降级）。</summary>
[MessagePackObject]
public sealed record Invalidation(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] InvalidationReason Reason,
    [property: Key(4)] Guid[] AffectedMappingIds,
    [property: Key(5)] CapabilityMode? NewCapability) : IPcpMessage;

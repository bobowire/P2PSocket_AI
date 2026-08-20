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

// ── 0x20~0x22 用户族（FR-S-201~205）────────────────────────────────────

[MessagePackObject]
public sealed record UserRegister(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] string Username,
    [property: Key(4)] string Password) : IPcpMessage;

[MessagePackObject]
public sealed record UserRegisterAck(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] bool Ok) : IPcpMessage;

[MessagePackObject]
public sealed record UserLogin(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] string Username,
    [property: Key(4)] string Password) : IPcpMessage;

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

// ── 0x40 设备列表（OQ-16 应用层分页）───────────────────────────────────

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

// ── 0x50/0x55/0x56 分组管理（M1 范围：建组/编辑/解散）──────────────────

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

// ── 0x60/0x61 映射 CRUD 同步（字段=PRD 06 §2 表）───────────────────────

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

// ── 0x70/0x71/0x76 两段式打洞信令（OQ-18/TD-19）─────────────────────────

/// <summary>
/// 0x70 C→S：请求与目标建立隧道；requesterEndpoints=发起方出队后即时 STUN 探测所得。
/// Ack 由服务端**延后**至被邀请方 0x76 端点就绪才下发（§5.1④）。
/// </summary>
[MessagePackObject]
public sealed record PunchRequest(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] Guid TargetDeviceId,
    [property: Key(4)] Guid? TriggerMappingId,
    [property: Key(5)] string Proto,
    [property: Key(6)] EndpointPair? RequesterEndpoints) : IPcpMessage;

/// <summary>0x70 S→C（延后 Ack）：被邀请方端点就绪后携带对端信息与端点。</summary>
[MessagePackObject]
public sealed record PunchRequestAck(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] Guid SessionId,
    [property: Key(4)] PeerInfo Peer,
    [property: Key(5)] EndpointPair PeerEndpoints,
    [property: Key(6)] byte PunchCount,
    [property: Key(7)] bool RelayAllowed) : IPcpMessage;

/// <summary>0x71 S→C（发往被邀请方）：发起方设备信息+端点；本端需监听的并发路数 N。</summary>
[MessagePackObject]
public sealed record PunchInvite(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] Guid SessionId,
    [property: Key(4)] PeerInfo Peer,
    [property: Key(5)] EndpointPair PeerEndpoints,
    [property: Key(6)] byte PunchCount,
    [property: Key(7)] bool RelayAllowed) : IPcpMessage;

/// <summary>0x72 打洞结果上报——M1 仅定义编解码，处理属 FR-C-404 → M2。</summary>
[MessagePackObject]
public sealed record PunchResult(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] Guid SessionId,
    [property: Key(4)] bool Ok,
    [property: Key(5)] Endpoint? Endpoint,
    [property: Key(6)] string? FailReason) : IPcpMessage;

/// <summary>0x76 C→S：被邀请方回传自己的公网端点（收到 0x71 后即时 STUN 探测）。</summary>
[MessagePackObject]
public sealed record PunchEndpoint(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] Guid SessionId,
    [property: Key(4)] EndpointPair Endpoints) : IPcpMessage;

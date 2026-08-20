using MessagePack;

namespace P2P.Core.Protocol;

// ── 0x10~0x12 注册族（02 §2.4；凭据恢复 OQ-14 / deviceSecret ECIES 下发 OQ-15）──────

/// <summary>0x10 C→S：macCode 命中离线记录 → 覆盖式凭据恢复；在线 → Error 4004（服务端语义，M1-14）。</summary>
[MessagePackObject]
public sealed record Register(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] string MacCode,
    [property: Key(4)] string Hostname,
    [property: Key(5)] string Os,
    [property: Key(6)] string ClientVersion,
    [property: Key(7)] byte[] StaticPubKey,
    [property: Key(8)] string? InviteCode,
    [property: Key(9)] string? Username,
    [property: Key(10)] string? Password) : IPcpMessage;

/// <summary>分组信息（0x11 groupInfo[] 载荷；字段=groups 表最小展示集）。</summary>
[MessagePackObject]
public sealed record GroupInfo(
    [property: Key(0)] Guid GroupId,
    [property: Key(1)] string GroupName);

/// <summary>
/// 0x11 S→C：deviceSecret = ECIES 密文（[ephPub65][nonce12][ct+tag]，OQ-15），
/// 以 Register.staticPubKey 加密——未注册设备首个 Register 无连接密钥，此为唯一机密性保护。
/// virtualIp 为统一下发固定 .2（OQ-13）。
/// </summary>
[MessagePackObject]
public sealed record RegisterAck(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] Guid DeviceId,
    [property: Key(4)] byte[] DeviceSecretBox,
    [property: Key(5)] string RemoteCode,
    [property: Key(6)] string VirtualIp,
    [property: Key(7)] GroupInfo[] Groups) : IPcpMessage;

/// <summary>0x12 C→S：请求解绑（本机重置；凭连接级设备身份认证）。</summary>
[MessagePackObject]
public sealed record UnbindMe(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType) : IPcpMessage;

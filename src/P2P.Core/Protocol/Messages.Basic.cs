using MessagePack;

namespace P2P.Core.Protocol;

// ── 0x01 连接握手（02 §2.3）────────────────────────────────────────────
// 注：单主构造函数（MessagePack 绑定无歧义）；MsgType 由调用方传常量，PcpCodec 编解码时校验。

public enum HelloStatus : byte
{
    Ok = 0,
    NeedRegister = 1,
    VersionNotSupported = 2, // 触发升级引导（FR-C-904，M2）
}

/// <summary>0x01 C→S：不校 ts（尚无共享时间基准，OQ-12）；deviceId 仅注册过设备携带。</summary>
[MessagePackObject]
public sealed record Hello(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] ushort ProtocolVersion,
    [property: Key(4)] Guid? DeviceId,
    [property: Key(5)] byte[] NonceC) : IPcpMessage;

/// <summary>0x01 S→C：serverTs 供 RTT/2 补偿计算 offset（OQ-12）。</summary>
[MessagePackObject]
public sealed record HelloAck(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] string ServerVersion,
    [property: Key(4)] ushort ProtocolVersion,
    [property: Key(5)] byte[] NonceS,
    [property: Key(6)] HelloStatus Status,
    [property: Key(7)] ulong ServerTs) : IPcpMessage;

// ── 0x02 连接级身份证明（02 §2.3）─────────────────────────────────────

/// <summary>0x02 C→S：hmac = HMAC(deviceSecret, nonceC|nonceS)。</summary>
[MessagePackObject]
public sealed record Proof(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] byte[] Hmac) : IPcpMessage;

/// <summary>0x02 S→C。</summary>
[MessagePackObject]
public sealed record ProofAck(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] bool Ok) : IPcpMessage;

// ── 0x30 心跳（02 §2.4：30s 周期；Ack 带服务器时间持续重校准）──────────

[MessagePackObject]
public sealed record Heartbeat(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType) : IPcpMessage;

[MessagePackObject]
public sealed record HeartbeatAck(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] ulong ServerTs) : IPcpMessage;

// ── 0x7E 错误（02 §2.4；错误码见 ErrorCode，04 §5）────────────────────

[MessagePackObject]
public sealed record ErrorMessage(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType,
    [property: Key(3)] int Code,
    [property: Key(4)] string HttpLikeMsg) : IPcpMessage;

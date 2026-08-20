using MessagePack;

namespace P2P.Core.Protocol;

/// <summary>
/// PCP 消息公共契约（02 §2.2）：每条消息前三个字段固定 seq / timestampMs / msgType（数组格式键 0/1/2）。
/// timestampMs 为校准后时钟（±120s 窗口，OQ-12）。
/// </summary>
public interface IPcpMessage
{
    uint Seq { get; }
    ulong TimestampMs { get; }
    byte MsgType { get; }
}

/// <summary>仅头三字段的最小视图：解码分發前探测 seq/ts/msgType；数组格式容忍尾部未知字段（消息演进加法兼容，02 §7）。</summary>
[MessagePackObject]
public sealed record PcpHeader(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType) : IPcpMessage;

/// <summary>未知 msgType 的容忍表示（02 §7 演进约定：新增 msgType 不使旧端崩溃）。</summary>
[MessagePackObject]
public sealed record UnknownMessage(
    [property: Key(0)] uint Seq,
    [property: Key(1)] ulong TimestampMs,
    [property: Key(2)] byte MsgType) : IPcpMessage;

/// <summary>协议版本（Hello.protocolVersion，v1；字段语义永久冻结，02 §7）。</summary>
public static class ProtocolVersion
{
    public const ushort Current = 1;
}

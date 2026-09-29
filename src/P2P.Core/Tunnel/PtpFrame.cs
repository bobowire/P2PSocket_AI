using MessagePack;

namespace P2P.Core.Tunnel;

/// <summary>PTP 帧 type（02 §4.2 表；0x11~0x13 为 M1 自选握手帧值——业务帧 0x01~0x0A 均密文，握手在会话密钥产生前故明文）。</summary>
public static class PtpFrameType
{
    public const byte Open = 0x01;        // {targetProto, targetAddr, targetPort}
    public const byte OpenResult = 0x02;  // OPEN_OK / OPEN_FAIL（payload 首字节区分，FAIL 附原因）
    public const byte Data = 0x03;
    public const byte Close = 0x04;
    public const byte Keepalive = 0x05;
    public const byte Ping = 0x06;
    public const byte Pong = 0x07;
    public const byte Rekey = 0x08;       // SEC-14 → M2
    public const byte UdpDgram = 0x09;    // UDP 映射数据报（M2-20，FR-C-303；channelId 在帧头）
    public const byte Window = 0x0A;      // M2
    public const byte Frag = 0x0B;        // UDP_DGRAM 分片（M2-20 定案 0x0B，02 §4.2 回填）
    public const byte RekeyAck = 0x0C;    // REKEY 应答（M2-21 定案 0x0C，02 §4.2 回填；OPEN/OPEN_RESULT、PING/PONG 同款分立惯例）

    // ── 握手帧（明文传输；counter=0、channelId=0，不进防重放窗口）────────
    public const byte THello1 = 0x11;     // A→B {sessionId, ephA, nonceA}
    public const byte THello2 = 0x12;     // B→A {ephB, nonceB, mac}
    public const byte TConfirm = 0x13;    // A→B {mac}

    /// <summary>握手帧集合（接收侧识别：这些帧不带 AEAD）。</summary>
    public static bool IsHandshake(byte type) => type is THello1 or THello2 or TConfirm;
}

/// <summary>PTP 帧头（明文 16B：ver|type|channelId|counter|u16len，小端；作为 AEAD 的 AAD）。</summary>
public readonly record struct PtpHeader(byte Ver, byte Type, uint ChannelId, ulong Counter, ushort CipherLen)
{
    public const int WireLen = 1 + 1 + 4 + 8 + 2;
    public const byte CurrentVer = 1;
}

/// <summary>OPEN 载荷（0x01）：访问方申请对端打开目标连接。</summary>
[MessagePackObject]
public sealed record OpenPayload(
    [property: Key(0)] string TargetProto,   // "tcp" | "udp"
    [property: Key(1)] string TargetAddr,    // "self" 或绝对 IP（M2 白名单）
    [property: Key(2)] ushort TargetPort);

/// <summary>OPEN 结果载荷（0x02）：首字节语义由 Ok 决定；FAIL 携带原因。</summary>
[MessagePackObject]
public sealed record OpenResultPayload(
    [property: Key(0)] bool Ok,
    [property: Key(1)] string? FailReason);

/// <summary>WINDOW 信用回报载荷（0x0A，05 §2.3）：channelId 在帧头，载荷仅已消费字节数——接收方据此回报、发送方 Grant 恢复读。</summary>
[MessagePackObject]
public sealed record WindowCreditPayload(
    [property: Key(0)] uint CreditBytes);

/// <summary>FRAG 载荷（0x0B，M2-20）：UDP_DGRAM 超单帧上限（明文 1368B）时分片逐帧承载。
/// channelId 在帧头；DgramId 区分同 channel 相邻数据报的片（UDP 承载乱序容忍）；
/// More=false 为末片（Index 从 0 起 → 总片数 = Index+1）；接收侧按 channelId+DgramId 攒齐重组。</summary>
[MessagePackObject]
public sealed record FragPayload(
    [property: Key(0)] uint DgramId,
    [property: Key(1)] ushort Index,
    [property: Key(2)] bool More,
    [property: Key(3)] byte[] Chunk);

using System.Buffers.Binary;
using P2P.Core.Crypto;
using P2P.Core.Protocol;

namespace P2P.Core.Tunnel;

/// <summary>
/// PTP 帧编解码（02 §4.2）：
/// [ver u8=1][type u8][channelId u32][counter u64][u16len][AEAD 密文(含 16B tag)]
/// AEAD = AES-256-GCM(方向密钥, nonce=counter 12B 小端, aad=帧头 16B 明文)。
/// 握手帧（0x11~0x13）无会话密钥：密文段为明文 payload，counter/channelId 恒 0。
/// </summary>
public static class PtpFrameCodec
{
    /// <summary>TCP 承载 DATA 上限 16 KiB（02 §4.3）；UDP 承载整帧 ≤1400B 由发送侧保证。</summary>
    public const int MaxDataPayload = 16 * 1024;

    /// <summary>UDP_DGRAM 单帧明文上限（M2-20，02 §4.3：UDP 承载整帧 ≤1400 = 16 头 + 明文 + 16 tag）。
    /// 与承载类型解耦：超出即走 FRAG 分片（TCP 承载同口径，02 §6.2"UDP_DGRAM 语义不变"）。</summary>
    public const int MaxUdpDgramPlain = 1400 - PtpHeader.WireLen - Aead.TagLen; // 1368

    /// <summary>FRAG 单片 chunk 上限：1368 减最坏 MessagePack 载荷头（fixarray+uint32+uint16+bool+bin32 ≈ 15B，保守 16）。</summary>
    public const int MaxFragChunk = MaxUdpDgramPlain - 16; // 1352

    /// <summary>组装密文业务帧。</summary>
    public static byte[] Seal(byte type, uint channelId, ulong counter, ReadOnlySpan<byte> plain, ReadOnlySpan<byte> directionKey)
    {
        if (plain.Length + Aead.TagLen > ushort.MaxValue)
            throw new ProtocolException($"PTP 帧密文超长：{plain.Length + Aead.TagLen} > {ushort.MaxValue}");
        var wire = new byte[PtpHeader.WireLen + plain.Length + Aead.TagLen];
        WriteHeader(wire, type, channelId, counter, (ushort)(plain.Length + Aead.TagLen));
        var nonce = NonceFromCounter(counter);
        var cipher = Aead.Seal(directionKey, nonce, wire.AsSpan(0, PtpHeader.WireLen), plain);
        cipher.CopyTo(wire.AsSpan(PtpHeader.WireLen));
        return wire;
    }

    /// <summary>组装明文握手帧（仅 0x11~0x13；其余 type 拒绝）。</summary>
    public static byte[] BuildHandshake(byte type, ReadOnlySpan<byte> payload)
    {
        if (!PtpFrameType.IsHandshake(type))
            throw new ProtocolException($"0x{type:X2} 不是握手帧类型");
        var wire = new byte[PtpHeader.WireLen + payload.Length];
        WriteHeader(wire, type, 0, 0, (ushort)payload.Length);
        payload.CopyTo(wire.AsSpan(PtpHeader.WireLen));
        return wire;
    }

    /// <summary>解析帧头（不解密；接收侧按 type 分发握手/业务帧）。</summary>
    public static PtpHeader ParseHeader(ReadOnlySpan<byte> wire)
    {
        if (wire.Length < PtpHeader.WireLen)
            throw new ProtocolException("PTP 帧不足 16B 头");
        var ver = wire[0];
        if (ver != PtpHeader.CurrentVer)
            throw new ProtocolException($"PTP 版本不支持：{ver}");
        var type = wire[1];
        if (!PtpFrameType.IsHandshake(type) && type is < PtpFrameType.Open or > PtpFrameType.Frag)
            throw new ProtocolException($"未知 PTP 帧 type：0x{type:X2}");
        return new PtpHeader(ver, type,
            BinaryPrimitives.ReadUInt32LittleEndian(wire.Slice(2, 4)),
            BinaryPrimitives.ReadUInt64LittleEndian(wire.Slice(6, 8)),
            BinaryPrimitives.ReadUInt16LittleEndian(wire.Slice(14, 2)));
    }

    /// <summary>解密业务帧：AAD/nonce 校验失败抛 <see cref="System.Security.Cryptography.CryptographicException"/>。</summary>
    public static byte[] Open(ReadOnlySpan<byte> wire, ReadOnlySpan<byte> directionKey)
    {
        var header = ParseHeader(wire);
        if (PtpFrameType.IsHandshake(header.Type))
            throw new ProtocolException("握手帧无密文，不能 Open");
        if (wire.Length != PtpHeader.WireLen + header.CipherLen)
            throw new ProtocolException($"PTP 帧长度不符：头声明 {header.CipherLen}，实得 {wire.Length - PtpHeader.WireLen}");
        var nonce = NonceFromCounter(header.Counter);
        return Aead.Open(directionKey, nonce, wire.Slice(0, PtpHeader.WireLen), wire.Slice(PtpHeader.WireLen));
    }

    /// <summary>提取明文握手帧 payload。</summary>
    public static byte[] ReadHandshakePayload(ReadOnlySpan<byte> wire)
    {
        var header = ParseHeader(wire);
        if (!PtpFrameType.IsHandshake(header.Type))
            throw new ProtocolException($"0x{header.Type:X2} 不是握手帧类型");
        if (header.Counter != 0 || header.ChannelId != 0)
            throw new ProtocolException("握手帧 counter/channelId 须为 0");
        return wire[PtpHeader.WireLen..].ToArray();
    }

    internal static void WriteHeader(Span<byte> dst, byte type, uint channelId, ulong counter, ushort cipherLen)
    {
        dst[0] = PtpHeader.CurrentVer;
        dst[1] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(2, 4), channelId);
        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(6, 8), counter);
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(14, 2), cipherLen);
    }

    private static byte[] NonceFromCounter(ulong counter)
    {
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce.AsSpan(0, 8), counter);
        return nonce;
    }
}

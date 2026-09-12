using System.Buffers.Binary;

namespace P2P.Core.Stun;

/// <summary>
/// STUN over TCP 定界（RFC 5389 §7.2.2，02 §3.3）：流承载上消息以自身头部的 msgLen 定界
/// （头 20B 含 msgLen u16 大端），无额外长度前缀。客户端单事务（写一读一即关）与
/// 服务端 3478/TCP 短事务（M2-06，收 Binding Request 即回即关）共用本封装。
/// </summary>
public static class StunTcpFraming
{
    /// <summary>
    /// 消息属性区上限：本子集 Binding ≤ ~120B（DEVICE-AUTH 72B），M3 RFC5780 判型属性扩展后仍远小于 512；
    /// 超限即协议错断连（无放大面的防御性边界，02 §3.2 精神）。
    /// </summary>
    public const int MaxAttrLen = 512;

    /// <summary>向流写一条完整 STUN 消息（一致性校验：头部 msgLen 与实际长度不符拒绝）。</summary>
    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> message, CancellationToken ct = default)
    {
        if (message.Length < StunCodec.HeaderLen
            || message.Length > StunCodec.HeaderLen + MaxAttrLen
            || BinaryPrimitives.ReadUInt16BigEndian(message.Span.Slice(2, 2)) != message.Length - StunCodec.HeaderLen)
            throw new ArgumentException($"STUN-TCP 消息长度非法：{message.Length}B（头 msgLen 不符或越界）", nameof(message));
        await stream.WriteAsync(message, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 从流读一条完整 STUN 消息。
    /// 返回 null = 对端在消息边界处正常关闭（无半帧）；msgLen 超限或流中断于消息中间抛
    /// <see cref="InvalidDataException"/>（协议错，调用方断连，AI-19）。
    /// </summary>
    public static async Task<byte[]?> TryReadAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[StunCodec.HeaderLen];
        var got = await ReadWithEofAsync(stream, header, ct).ConfigureAwait(false);
        if (got == 0) return null;
        if (got < StunCodec.HeaderLen)
            throw new InvalidDataException($"STUN-TCP 流在头中间断开（{got}/{StunCodec.HeaderLen}B）");

        var attrLen = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));
        if (attrLen > MaxAttrLen)
            throw new InvalidDataException($"STUN-TCP msgLen {attrLen} 超上限 {MaxAttrLen}B");

        var message = new byte[StunCodec.HeaderLen + attrLen];
        header.AsSpan().CopyTo(message);
        if (attrLen > 0)
        {
            got = await ReadWithEofAsync(stream, message.AsMemory(StunCodec.HeaderLen), ct).ConfigureAwait(false);
            if (got < attrLen)
                throw new InvalidDataException($"STUN-TCP 流在消息中间断开（缺 {attrLen - got}B）");
        }
        return message;
    }

    /// <summary>读满或至 EOF：返回 EOF 前读到的字节数（区分"边界处关闭"与"中途断开"由调用方判定）。</summary>
    private static async Task<int> ReadWithEofAsync(Stream stream, Memory<byte> buf, CancellationToken ct)
    {
        var read = 0;
        while (read < buf.Length)
        {
            var n = await stream.ReadAsync(buf[read..], ct).ConfigureAwait(false);
            if (n == 0) break;
            read += n;
        }
        return read;
    }
}

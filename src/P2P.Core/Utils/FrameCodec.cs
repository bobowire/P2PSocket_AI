using System.Buffers;
using System.IO.Pipelines;

namespace P2P.Core.Utils;

/// <summary>
/// PCP 分帧编解码（02 §2.1）：u16 bodyLen（小端）+ body；bodyLen ≤ 64KiB。
/// 帧上限的物理边界即 u16 本身，故 MaxBodyLen = 65535；写入侧对超限 body 拒绝分帧。
/// </summary>
public static class FrameCodec
{
    public const int MaxBodyLen = ushort.MaxValue; // 65535 ≈ 64KiB（参数登记表：控制帧 body ≤ 64KiB）
    public const int HeaderLen = 2;

    /// <summary>向 PipeWriter 写一帧（u16 长度前缀 + body）。超限 body 抛 <see cref="ArgumentException"/>。</summary>
    public static ValueTask<FlushResult> WriteFrameAsync(PipeWriter writer, ReadOnlyMemory<byte> body, CancellationToken ct = default)
    {
        if (body.Length > MaxBodyLen)
            throw new ArgumentException($"帧体 {body.Length}B 超过上限 {MaxBodyLen}B（02 §2.1）", nameof(body));
        var span = writer.GetSpan(HeaderLen + body.Length);
        span[0] = (byte)(body.Length & 0xFF);
        span[1] = (byte)(body.Length >> 8);
        body.Span.CopyTo(span[HeaderLen..]);
        writer.Advance(HeaderLen + body.Length);
        return writer.FlushAsync(ct);
    }

    /// <summary>
    /// 从 PipeReader 读一帧完整 body。
    /// 返回 false = 对端正常关闭且无半帧；流中断于帧中间抛 <see cref="InvalidDataException"/>（协议错，AI-19 断连）。
    /// </summary>
    public static async ValueTask<byte[]?> ReadFrameAsync(PipeReader reader, CancellationToken ct = default)
    {
        while (true)
        {
            var result = await reader.ReadAsync(ct);
            var buffer = result.Buffer;
            if (buffer.Length == 0 && result.IsCompleted)
            {
                reader.AdvanceTo(buffer.Start);
                return null;
            }
            if (TryParseFrame(ref buffer, out var body))
            {
                reader.AdvanceTo(buffer.Start);
                return body;
            }
            // 不完整帧：若流已结束则为协议错
            if (result.IsCompleted || result.IsCanceled)
                throw new InvalidDataException("连接在帧中间断开（不完整帧）");
            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    private static bool TryParseFrame(ref ReadOnlySequence<byte> buffer, out byte[] body)
    {
        body = [];
        if (buffer.Length < HeaderLen) return false;
        Span<byte> lenBuf = stackalloc byte[HeaderLen];
        buffer.Slice(0, HeaderLen).CopyTo(lenBuf);
        var len = lenBuf[0] | (lenBuf[1] << 8); // 小端
        if (buffer.Length < HeaderLen + len) return false;
        body = buffer.Slice(HeaderLen, len).ToArray();
        buffer = buffer.Slice(HeaderLen + len);
        return true;
    }
}

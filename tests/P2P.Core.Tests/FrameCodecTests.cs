using System.Buffers;
using System.IO.Pipelines;
using P2P.Core.Utils;
using Xunit;

namespace P2P.Core.Tests;

public class FrameCodecTests
{
    [Fact]
    public async Task ReadFrame_EmptyBody_ReturnsEmptyArray()
    {
        var (reader, writer) = SetupPipe([0x00, 0x00]);
        var body = await FrameCodec.ReadFrameAsync(reader);
        Assert.NotNull(body);
        Assert.Empty(body);
    }

    [Fact]
    public async Task ReadWrite_RoundTrip_MaxBody_Preserved()
    {
        var body = new byte[FrameCodec.MaxBodyLen];
        Random.Shared.NextBytes(body);
        // 关闭写暂停阈值：64KiB 帧超过 Pipe 默认 64KiB 暂停线，无读端并发时会背压死锁
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0));
        await FrameCodec.WriteFrameAsync(pipe.Writer, body);
        await pipe.Writer.CompleteAsync();
        var read = await FrameCodec.ReadFrameAsync(pipe.Reader);
        Assert.Equal(body, read);
    }

    [Fact]
    public async Task WriteFrame_OverLimit_Rejected()
    {
        var pipe = new Pipe();
        await Assert.ThrowsAsync<ArgumentException>(
            () => FrameCodec.WriteFrameAsync(pipe.Writer, new byte[FrameCodec.MaxBodyLen + 1]).AsTask());
    }

    [Fact]
    public async Task ReadFrame_TruncatedMiddle_Throws()
    {
        // 声明 5B body 只给 2B，随后流完成 → 协议错
        var (reader, writer) = SetupPipe([0x05, 0x00, 0x01, 0x02]);
        await writer.CompleteAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => FrameCodec.ReadFrameAsync(reader).AsTask());
    }

    [Fact]
    public async Task ReadFrame_MultipleFrames_SeriallyDelivered()
    {
        var pipe = new Pipe();
        await FrameCodec.WriteFrameAsync(pipe.Writer, new byte[] { 1 });
        await FrameCodec.WriteFrameAsync(pipe.Writer, new byte[] { 2, 3 });
        await pipe.Writer.CompleteAsync();
        Assert.Equal(new byte[] { 1 }, await FrameCodec.ReadFrameAsync(pipe.Reader));
        Assert.Equal(new byte[] { 2, 3 }, await FrameCodec.ReadFrameAsync(pipe.Reader));
        Assert.Null(await FrameCodec.ReadFrameAsync(pipe.Reader)); // 流结束
    }

    [Fact]
    public async Task ReadFrame_LittleEndianLength_Parsed()
    {
        // 长度 258 = 0x0102 → 小端字节 02 01
        var payload = new byte[258];
        payload[257] = 0xAB;
        var (reader, writer) = SetupPipe([0x02, 0x01, .. payload]);
        await writer.CompleteAsync();
        var body = await FrameCodec.ReadFrameAsync(reader);
        Assert.Equal(258, body!.Length);
        Assert.Equal(0xAB, body[257]);
    }

    private static (PipeReader, PipeWriter) SetupPipe(byte[] bytes)
    {
        var pipe = new Pipe();
        pipe.Writer.Write(bytes);
        pipe.Writer.FlushAsync().AsTask().GetAwaiter().GetResult();
        return (pipe.Reader, pipe.Writer);
    }
}

public class BackoffPolicyTests
{
    [Fact]
    public void ComputeDelay_ReconnectSequence_CappedAt30s()
    {
        var p = BackoffPolicy.ReconnectDefault;
        Assert.Equal(TimeSpan.FromSeconds(1), p.ComputeDelay(0));
        Assert.Equal(TimeSpan.FromSeconds(2), p.ComputeDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(4), p.ComputeDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(8), p.ComputeDelay(3));
        Assert.Equal(TimeSpan.FromSeconds(16), p.ComputeDelay(4));
        Assert.Equal(TimeSpan.FromSeconds(30), p.ComputeDelay(5));
        Assert.Equal(TimeSpan.FromSeconds(30), p.ComputeDelay(50)); // 上界（08 §5.2 maxSec=30）
    }

    [Fact]
    public void ComputeDelay_NegativeAttempt_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BackoffPolicy.ReconnectDefault.ComputeDelay(-1));
    }
}

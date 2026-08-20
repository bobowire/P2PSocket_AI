using P2P.Core.Protocol;
using Xunit;

namespace P2P.Core.Tests;

/// <summary>
/// 消息相等断言：record 含 byte[] 成员时默认引用相等不可用，
/// 改用确定性重编码做字节级比较（内容等 ⇒ 编码等）。
/// </summary>
public static class MsgAssert
{
    public static void Equal<T>(T expected, T actual) where T : class, IPcpMessage
    {
        Assert.NotNull(actual);
        var eb = PcpCodec.Encode(expected);
        var ab = PcpCodec.Encode(actual);
        Assert.True(eb.AsSpan().SequenceEqual(ab),
            $"消息内容不一致：\n期望 {Convert.ToHexString(eb)}\n实得 {Convert.ToHexString(ab)}");
    }
}

using P2P.Core.Crypto;
using Xunit;

namespace P2P.Core.Tests;

/// <summary>M1-24 静态私钥持久化支撑：导出/重建往返（注册后随 state 落盘，重启加载）。</summary>
public sealed class EcKeyPairTests
{
    [Fact]
    public void 私钥导出重建_公钥一致_共享密钥一致()
    {
        using var a = EcKeyPair.Generate();
        using var b = EcKeyPair.Generate();
        var d = a.ExportPrivateKey();
        Assert.Equal(32, d.Length);

        using var restored = EcKeyPair.FromPrivateKey(d);
        Assert.Equal(a.ExportPublicKey(), restored.ExportPublicKey());

        var shared1 = a.DeriveSharedKey(b.ExportPublicKey());
        var shared2 = restored.DeriveSharedKey(b.ExportPublicKey());
        Assert.Equal(shared1, shared2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void 私钥长度非法_拒绝(int len)
        => Assert.Throws<ArgumentException>(() => EcKeyPair.FromPrivateKey(new byte[len]));
}

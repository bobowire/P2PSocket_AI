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

    /// <summary>Q=d×G 托管标量乘正确性（M8-07 Android 修复）：NIST/SEC 2 公开测试向量锁死——
    /// 往返用例在忽略 Q-D 关系的平台上抓不住标量乘数学错误，须以已知答案锁定。</summary>
    [Fact]
    public void 已知私钥_公钥点等于NIST向量()
    {
        // RFC 6979 A.2.5 / NIST P-256 标准向量
        var d = Hex("C9AFA9D845BA75166B5C215767B1D6934E50C3DB36E89B127B8A622B120F6721");
        using var kp = EcKeyPair.FromPrivateKey(d);
        var pub = kp.ExportPublicKey();
        Assert.Equal(
            "04" + "60FED4BA255A9D31C961EB74C6356D68C049B8923B61FA6CE669622E60F29FB6"
                 + "7903FE1008B8BC99A41AE9E95628BC64F2F1B20C2D7E9F5177A3C294D4462299",
            Convert.ToHexString(pub));
    }

    [Fact]
    public void 双侧私钥导入_ECDH共享密钥一致()
    {
        // Android 场景等价：两端均走 D+Q 成对导入（ImportParameters 全参数路径）
        using var a = EcKeyPair.Generate();
        using var b = EcKeyPair.Generate();
        using var a2 = EcKeyPair.FromPrivateKey(a.ExportPrivateKey());
        using var b2 = EcKeyPair.FromPrivateKey(b.ExportPrivateKey());
        Assert.Equal(a2.DeriveSharedKey(b.ExportPublicKey()), b2.DeriveSharedKey(a.ExportPublicKey()));
    }

    [Fact]
    public void 私钥标量越界_零与n拒绝()
    {
        Assert.Throws<ArgumentException>(() => EcKeyPair.FromPrivateKey(new byte[32])); // d=0
        var n = Hex("FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551"); // =n 越界
        Assert.Throws<ArgumentException>(() => EcKeyPair.FromPrivateKey(n));
    }

    private static byte[] Hex(string s)
        => Convert.FromHexString(s);
}

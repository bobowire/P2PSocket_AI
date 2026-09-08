// M1-22 机密保护器测试：Windows DPAPI 实机回环（机器范围）+ 工厂分发。Linux 明文路径由
// StateStoreTests 的反转替身覆盖语义，此处仅验证平台选择。
using System.Security.Cryptography;
using System.Runtime.Versioning;
using P2P.Client.Storage;
using Xunit;

namespace P2P.Client.Tests;

public sealed class SecretProtectorTests
{
    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public void WindowsDpapi_RoundTrip_Succeeds()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI 仅 Windows（07 §4）");

        var protector = new WindowsDpapiSecretProtector();
        var secret = RandomNumberGenerator.GetBytes(32);

        var boxed = protector.Protect(secret);
        Assert.NotEqual(secret, boxed); // 确实加了密
        Assert.Equal(secret, protector.Unprotect(boxed)); // 回环还原
    }

    [Fact]
    public void Factory_PlainProtector_RoundTripIdentity()
    {
        var protector = new PlainSecretProtector();
        var secret = new byte[] { 1, 2, 3 };
        Assert.Equal(secret, protector.Unprotect(protector.Protect(secret)));
    }

    [Fact]
    public void Factory_OnNonWindows_SelectsPlain()
    {
        if (OperatingSystem.IsWindows()) return; // Windows 分支由 DPAPI 测试覆盖
        Assert.IsType<PlainSecretProtector>(SecretProtectorFactory.Create());
    }
}

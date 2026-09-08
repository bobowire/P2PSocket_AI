// M1-22 机密落盘保护（07 §4/SEC-23）：
// Windows=DPAPI 机器范围加密 deviceSecret 与静态私钥（07 §4 表：写入 state.json 前加密）；
// Linux=明文承载，由文件权限保护（state.json 0600、目录 0700）。
// 测试与降级场景注入 PlainSecretProtector/自定义实现。
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace P2P.Client.Storage;

/// <summary>机密字段落盘保护器：Protect 后的字节方可写文件，Unprotect 在读取边界还原。</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] boxed);
}

public static class SecretProtectorFactory
{
    /// <summary>按平台创建默认保护器（Windows=DPAPI 机器范围；Linux=明文+文件权限，07 §4）。</summary>
    public static ISecretProtector Create() =>
        OperatingSystem.IsWindows() ? new WindowsDpapiSecretProtector() : new PlainSecretProtector();
}

/// <summary>明文保护器（Linux 路径与测试）：保护语义由 0600 文件权限承载（07 §4）。</summary>
public sealed class PlainSecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] plaintext) => plaintext;
    public byte[] Unprotect(byte[] boxed) => boxed;
}

/// <summary>DPAPI 机器范围保护（Windows；07 §4「机器范围」）。附加熵绑定用途，防同机其他软件解密。</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("P2P.Client.state.v1");

    public byte[] Protect(byte[] plaintext)
        => ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.LocalMachine);

    public byte[] Unprotect(byte[] boxed)
        => ProtectedData.Unprotect(boxed, Entropy, DataProtectionScope.LocalMachine);
}

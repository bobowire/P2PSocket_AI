using System.Security.Cryptography;

namespace P2P.Core.Crypto;

/// <summary>
/// PTP 会话密钥派生（02 §4.1 双重 ECDH；07 §3）：
/// k_sess = HKDF-SHA256(ikm = ECDH(ephA,ephB) || k_static, salt = sessionId|nonceA|nonceB, info="ptp-v1", 64)。
/// 前 32B = A→B 发送密钥，后 32B = B→A 发送密钥（双方向密钥独立性）。
/// </summary>
public static class TunnelKeyDerivation
{
    public const int SessionKeyLen = 64; // 双方向各 32B
    public const int DirectionKeyLen = 32;

    public static (byte[] KeyAtoB, byte[] KeyBtoA) DeriveSessionKeys(
        ReadOnlySpan<byte> ephShared, ReadOnlySpan<byte> staticShared,
        ReadOnlySpan<byte> sessionId, ReadOnlySpan<byte> nonceA, ReadOnlySpan<byte> nonceB)
    {
        var ikm = new byte[ephShared.Length + staticShared.Length];
        ephShared.CopyTo(ikm.AsSpan(0, ephShared.Length));
        staticShared.CopyTo(ikm.AsSpan(ephShared.Length));

        var salt = new byte[sessionId.Length + nonceA.Length + nonceB.Length];
        var off = 0;
        sessionId.CopyTo(salt.AsSpan(off));
        off += sessionId.Length;
        nonceA.CopyTo(salt.AsSpan(off));
        off += nonceA.Length;
        nonceB.CopyTo(salt.AsSpan(off));

        var okm = Hkdf.Derive(ikm, salt, "ptp-v1"u8, SessionKeyLen);
        CryptoUtil.Zero(ikm);
        var aToB = okm[..DirectionKeyLen];
        var bToA = okm[DirectionKeyLen..];
        return (aToB, bToA);
    }

    /// <summary>
    /// 握手认证密钥（07 §3：以 static-static 共享密钥为 IKM 派生的握手密钥）。
    /// 方向绑定 + 会话绑定：kA 供 A 计算 TConfirm / B 校验，kB 供 B 计算 THello2 mac / A 校验。
    /// </summary>
    public static (byte[] KeyA, byte[] KeyB) DeriveHandshakeAuthKeys(
        ReadOnlySpan<byte> staticShared, ReadOnlySpan<byte> sessionId)
        => (Hkdf.Derive(staticShared, sessionId, "ptp-auth-a"u8, 32),
            Hkdf.Derive(staticShared, sessionId, "ptp-auth-b"u8, 32));
}

/// <summary>PBKDF2-SHA256 口令哈希（03 §2.1 users 表：100k 迭代、16B 盐）。</summary>
public static class PasswordHasher
{
    public const int Iterations = 100_000;
    public const int SaltLen = 16;
    private const int HashLen = 32;
    // 自包含存储格式：$PBKDF2-SHA256$<迭代>$<saltB64>$<hashB64>（盐与参数随哈希同行，校验无需外部假设）
    private const string Prefix = "$PBKDF2-SHA256";

    public static string Hash(string password)
    {
        var salt = RandomGenerator.Bytes(SaltLen);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashLen);
        return $"{Prefix}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 5 || parts[1] != "PBKDF2-SHA256") return false;
        if (!int.TryParse(parts[2], out var iterations)) return false;
        var salt = Convert.FromBase64String(parts[3]);
        var expected = Convert.FromBase64String(parts[4]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return Mac.Verify(expected, actual);
    }
}

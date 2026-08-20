using System.Security.Cryptography;

namespace P2P.Core.Crypto;

/// <summary>CSPRNG 随机源（07 §1：RandomNumberGenerator）。</summary>
public static class RandomGenerator
{
    public static byte[] Bytes(int length)
    {
        var buf = new byte[length];
        RandomNumberGenerator.Fill(buf);
        return buf;
    }
}

/// <summary>HMAC-SHA256 与固定时间比较（07 §1；AI-17：密钥与摘要不落日志）。</summary>
public static class Mac
{
    public const int HashLen = 32;

    public static byte[] HmacSha256(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        var result = new byte[HashLen];
        HMACSHA256.HashData(key, data, result);
        return result;
    }

    /// <summary>恒定时间校验，防时序侧信道。</summary>
    public static bool Verify(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
        => expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
}

/// <summary>HKDF-SHA256（RFC 5869；07 §1 统一派生函数）。</summary>
public static class Hkdf
{
    public static byte[] Derive(ReadOnlySpan<byte> ikm, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> info, int length)
    {
        var okm = new byte[length];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, okm, salt, info);
        return okm;
    }
}

/// <summary>AES-256-GCM AEAD（07 §2：tag 16B；密钥清零由调用方经 <see cref="CryptoUtil.Zero"/> 处理）。</summary>
public static class Aead
{
    public const int NonceLen = 12;
    public const int TagLen = 16;

    public static byte[] Seal(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> aad, ReadOnlySpan<byte> plaintext)
    {
        var ct = new byte[plaintext.Length + TagLen];
        using (var aes = new AesGcm(key, TagLen))
            aes.Encrypt(nonce, plaintext, ct.AsSpan(0, plaintext.Length), ct.AsSpan(plaintext.Length), aad);
        return ct;
    }

    /// <summary>解密失败（篡改/错钥）抛 <see cref="CryptographicException"/>——协议路径由调用方转错误码。</summary>
    public static byte[] Open(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> aad, ReadOnlySpan<byte> ciphertext)
    {
        var pt = new byte[ciphertext.Length - TagLen];
        using (var aes = new AesGcm(key, TagLen))
            aes.Decrypt(nonce, ciphertext[..pt.Length], ciphertext[pt.Length..], pt, aad);
        return pt;
    }
}

/// <summary>密钥敏感数据清零（M1-05：含密钥清零）。</summary>
public static class CryptoUtil
{
    public static void Zero(byte[]? data)
    {
        if (data is not null) CryptographicOperations.ZeroMemory(data.AsSpan());
    }
}

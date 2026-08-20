using System.Security.Cryptography;
using P2P.Core.Crypto;
using Xunit;

namespace P2P.Core.Tests;

public class CryptoTests
{
    [Fact]
    public void Aead_SealOpen_RoundTrip_Succeeds()
    {
        var key = RandomGenerator.Bytes(32);
        var nonce = RandomGenerator.Bytes(Aead.NonceLen);
        var aad = RandomGenerator.Bytes(8);
        var plain = RandomGenerator.Bytes(123);

        var ct = Aead.Seal(key, nonce, aad, plain);
        Assert.Equal(plain.Length + Aead.TagLen, ct.Length);
        Assert.Equal(plain, Aead.Open(key, nonce, aad, ct));
    }

    [Fact]
    public void Aead_TamperedCiphertext_Rejected()
    {
        var key = RandomGenerator.Bytes(32);
        var nonce = RandomGenerator.Bytes(Aead.NonceLen);
        var ct = Aead.Seal(key, nonce, [], [1, 2, 3]);
        ct[^1] ^= 0xFF; // 篡改末字节（含 tag 区）
        Assert.ThrowsAny<CryptographicException>(() => Aead.Open(key, nonce, [], ct));
    }

    [Fact]
    public void Aead_WrongAad_Rejected()
    {
        var key = RandomGenerator.Bytes(32);
        var nonce = RandomGenerator.Bytes(Aead.NonceLen);
        var ct = Aead.Seal(key, nonce, [1], [9]);
        Assert.ThrowsAny<CryptographicException>(() => Aead.Open(key, nonce, [2], ct));
    }

    [Fact]
    public void EcDh_BothSides_DeriveSameKey()
    {
        using var a = EcKeyPair.Generate();
        using var b = EcKeyPair.Generate();
        var kab = a.DeriveSharedKey(b.ExportPublicKey());
        var kba = b.DeriveSharedKey(a.ExportPublicKey());
        Assert.Equal(kab, kba);
        Assert.Equal(32, kab.Length);
    }

    [Fact]
    public void Ecies_RoundTrip_BothEndsConsistent()
    {
        using var clientKey = EcKeyPair.Generate(); // 客户端 static 密钥对
        var secret = RandomGenerator.Bytes(32);     // deviceSecret

        var boxed = Ecies.Encrypt(clientKey.ExportPublicKey(), secret);
        Assert.True(boxed.Length > 65 + 12 + 16);
        Assert.Equal(secret, Ecies.Decrypt(clientKey, boxed));
    }

    [Fact]
    public void Ecies_TamperedBox_Rejected()
    {
        using var clientKey = EcKeyPair.Generate();
        var boxed = Ecies.Encrypt(clientKey.ExportPublicKey(), [1, 2, 3, 4]);
        boxed[^2] ^= 0x01;
        Assert.ThrowsAny<CryptographicException>(() => Ecies.Decrypt(clientKey, boxed));
    }

    [Fact]
    public void Ecies_WrongKey_Rejected()
    {
        using var right = EcKeyPair.Generate();
        using var wrong = EcKeyPair.Generate();
        var boxed = Ecies.Encrypt(right.ExportPublicKey(), [7]);
        Assert.ThrowsAny<CryptographicException>(() => Ecies.Decrypt(wrong, boxed));
    }

    [Fact]
    public void TunnelKeyDerivation_DirectionKeys_IndependentAndSymmetric()
    {
        using var ephA = EcKeyPair.Generate();
        using var ephB = EcKeyPair.Generate();
        using var stA = EcKeyPair.Generate();
        using var stB = EcKeyPair.Generate();
        var sessionId = RandomGenerator.Bytes(16);
        var nonceA = RandomGenerator.Bytes(16);
        var nonceB = RandomGenerator.Bytes(16);

        var (aToB, bToA) = TunnelKeyDerivation.DeriveSessionKeys(
            ephA.DeriveSharedKey(ephB.ExportPublicKey()), stA.DeriveSharedKey(stB.ExportPublicKey()),
            sessionId, nonceA, nonceB);
        // B 侧以相反顺序计算 eph 共享（对称），static 共享相同 → 同一密钥
        var (aToB2, bToA2) = TunnelKeyDerivation.DeriveSessionKeys(
            ephB.DeriveSharedKey(ephA.ExportPublicKey()), stB.DeriveSharedKey(stA.ExportPublicKey()),
            sessionId, nonceA, nonceB);

        Assert.Equal(aToB, aToB2);
        Assert.Equal(bToA, bToA2);
        Assert.NotEqual(aToB, bToA); // 双方向密钥独立（02 §4.1）
        Assert.Equal(32, aToB.Length);
    }

    [Fact]
    public void PasswordHasher_RoundTrip_AndWrongPassword()
    {
        var hash = PasswordHasher.Hash("s3cret-Pass");
        Assert.StartsWith("$PBKDF2-SHA256$100000$", hash);
        Assert.True(PasswordHasher.Verify("s3cret-Pass", hash));
        Assert.False(PasswordHasher.Verify("wrong", hash));
    }

    [Fact]
    public void Mac_HmacSha256_KnownVector()
    {
        // RFC 4231 Test Case 2：key="Jefe"，data="what do ya want for nothing?"
        var key = "Jefe"u8.ToArray();
        var data = "what do ya want for nothing?"u8.ToArray();
        // 期望值经 Node crypto 独立实现交叉验证（AI-03：不凭记忆写测试向量）
        var expected = Convert.FromHexString("5BDCC146BF60754E6A042426089575C75A003F089D2739839DEC58B964EC3843");
        Assert.Equal(expected, Mac.HmacSha256(key, data));
    }

    [Fact]
    public void Hkdf_Rfc5869_TestCase1()
    {
        var ikm = new byte[22]; Array.Fill(ikm, (byte)0x0b);
        var salt = Convert.FromHexString("000102030405060708090a0b0c");
        var info = Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9");
        var okm = Hkdf.Derive(ikm, salt, info, 42);
        Assert.Equal(
            "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865",
            Convert.ToHexString(okm).ToLowerInvariant());
    }
}

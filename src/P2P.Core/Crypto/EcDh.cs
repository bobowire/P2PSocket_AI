using System.Security.Cryptography;

namespace P2P.Core.Crypto;

/// <summary>ECDH P-256 密钥对封装（07 §1）。公钥 65B 未压缩（devices 表 static_pub_key 口径）。</summary>
public sealed class EcKeyPair : IDisposable
{
    private readonly ECDiffieHellman _dh;

    private EcKeyPair(ECDiffieHellman dh) => _dh = dh;

    public static EcKeyPair Generate()
        => new(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>65B 未压缩公钥（0x04 || X || Y）。</summary>
    public byte[] ExportPublicKey()
    {
        var p = _dh.ExportParameters(false);
        var pub = new byte[65];
        pub[0] = 0x04;
        p.Q.X!.CopyTo(pub.AsSpan(1, 32));
        p.Q.Y!.CopyTo(pub.AsSpan(33, 32));
        return pub;
    }

    public void Dispose() => _dh.Dispose();

    /// <summary>
    /// 计算与本端私钥、对端 65B 公钥的共享密钥（32B）。
    /// 采用 DeriveKeyFromHash(SHA-256)：跨平台确定一致（CNG/OpenSSL 原始 X 坐标口径不一，统一走哈希）。
    /// </summary>
    public byte[] DeriveSharedKey(ReadOnlySpan<byte> peerPub65)
    {
        var peer = ImportPublicKey(peerPub65);
        return _dh.DeriveKeyFromHash(peer, HashAlgorithmName.SHA256);
    }

    /// <summary>ECIES 解密入口：以本端私钥开箱（见 <see cref="Ecies"/>）。</summary>
    internal ECDiffieHellman Inner => _dh;

    internal static ECDiffieHellmanPublicKey ImportPublicKey(ReadOnlySpan<byte> pub65)
    {
        if (pub65.Length != 65 || pub65[0] != 0x04)
            throw new FormatException("公钥须为 65B 未压缩 P-256 格式");
        var x = pub65.Slice(1, 32).ToArray();
        var y = pub65.Slice(33, 32).ToArray();
        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = x, Y = y },
        }).PublicKey;
    }


}

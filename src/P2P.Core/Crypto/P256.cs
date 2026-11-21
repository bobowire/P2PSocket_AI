using System.Globalization;
using System.Numerics;

namespace P2P.Core.Crypto;

/// <summary>
/// P-256 托管标量乘（仅 <see cref="EcKeyPair.FromPrivateKey"/> 公钥点推导使用）。
/// 背景：Android 的 ECDiffieHellman 实现（BouncyCastle 系）ImportParameters 要求 D 与 Q
/// 成对提供，仅 D 时抛 CryptographicException（Windows CNG 才支持仅 D 自动推导 Q——
/// M8-07 真机实测 2026-11-21：AttachPuncher 启动即崩）。本类以仿射双加算 Q=d×G 补齐。
/// 启动路径一次调用（~256 次点运算，毫秒级），性能不敏感。
/// </summary>
internal static class P256
{
    // NIST SP 800-186 / SEC 2 P-256 域参数（前导 00 保 BigInteger 正号）
    private static readonly BigInteger P =
        Parse("00FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF");
    private static readonly BigInteger A = P - 3;
    private static readonly BigInteger N =
        Parse("00FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551");
    private static readonly BigInteger Gx =
        Parse("006B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296");
    private static readonly BigInteger Gy =
        Parse("004FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5");

    /// <summary>Q = d×G（32B 大端私钥标量 → 32B×2 仿射坐标）。d 须 ∈ [1, n)。</summary>
    public static (byte[] X, byte[] Y) MultiplyGenerator(ReadOnlySpan<byte> d32)
    {
        if (d32.Length != 32) throw new ArgumentException("私钥须为 32B P-256 标量", nameof(d32));
        var d = BeToBigInteger(d32);
        if (d.IsZero || d >= N) throw new ArgumentException("私钥标量越界（须 ∈ [1, n)）", nameof(d32));

        // 双加（从最高有效位；无穷远点以 isInfinity 记）
        BigInteger qx = 0, qy = 0;
        var isInfinity = true;
        for (var i = (int)d.GetBitLength() - 1; i >= 0; i--)
        {
            if (!isInfinity) (qx, qy) = Double(qx, qy);
            if (((d >> i) & BigInteger.One).IsOne)
            {
                if (isInfinity)
                {
                    (qx, qy) = (Gx, Gy);
                    isInfinity = false;
                }
                else (qx, qy) = Add(qx, qy, Gx, Gy);
            }
        }

        return (ToBe32(qx), ToBe32(qy));
    }

    /// <summary>仿射点加（P+P 调用方已分流至 <see cref="Double"/>；x 同且 y 异=P+(-P)=无穷远，不出现）。</summary>
    private static (BigInteger x, BigInteger y) Add(BigInteger x1, BigInteger y1, BigInteger x2, BigInteger y2)
    {
        if (x1 == x2) return Double(x1, y1); // 剩余场景即 P+P（y1==y2）
        var lam = (y2 - y1) * ModInverse(x2 - x1) % P;
        var x3 = (lam * lam - x1 - x2) % P;
        return (x3, Mod(lam * (x1 - x3) - y1));
    }

    private static (BigInteger x, BigInteger y) Double(BigInteger x, BigInteger y)
    {
        if (y.IsZero) throw new InvalidOperationException("P-256 点倍遇 y=0（非法输入）");
        var lam = (3 * x * x + A) * ModInverse(2 * y) % P;
        var x3 = (lam * lam - 2 * x) % P;
        return (x3, Mod(lam * (x - x3) - y));
    }

    private static BigInteger Mod(BigInteger v) => (v % P + P) % P;

    private static BigInteger ModInverse(BigInteger v) => BigInteger.ModPow(Mod(v), P - 2, P);

    private static BigInteger BeToBigInteger(ReadOnlySpan<byte> be)
    {
        var bi = new BigInteger(be, isUnsigned: true, isBigEndian: true);
        return bi;
    }

    private static byte[] ToBe32(BigInteger v)
    {
        var le = v.ToByteArray(); // 小端+可能带符号位
        var be = new byte[32];
        for (var i = 0; i < le.Length && i < 32; i++)
            be[31 - i] = le[i];
        return be;
    }

    private static BigInteger Parse(string hex)
        => BigInteger.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}

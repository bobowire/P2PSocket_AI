using Microsoft.EntityFrameworkCore;
using P2P.Core.Crypto;
using P2P.Server.Data;

namespace P2P.Server.Services;

/// <summary>
/// 远程码生成（03 §3 / D18 / FR-S-901 / OQ-8）：
/// 6 位，字符集 0-9 + a/b/c；先耗尽纯数字空间（10⁶），再扩展 13⁶ ≈ 483 万；
/// 随机生成 + 查重（UNIQUE 约束兜底并发）。邀请码生成属 FR-S-303 → M2。
/// </summary>
public sealed class RemoteCodeGenerator(AppDbContext db)
{
    public const int Length = 6;
    public const string Digits = "0123456789";
    public const string FullCharset = "0123456789abc";

    /// <summary>纯数字空间连续撞码次数阈值——超过视为接近耗尽，切全字符集。</summary>
    private const int DigitsAttempts = 10;

    public async Task<string> GenerateAsync(CancellationToken ct = default)
    {
        // 纯数字优先
        for (var i = 0; i < DigitsAttempts; i++)
        {
            var code = RandomCode(Digits);
            if (!await ExistsAsync(code, ct)) return code;
        }
        // 全字符集兜底（含唯一索引冲突时的最终防线由调用方 SaveChanges 兜住）
        for (var i = 0; i < 100; i++)
        {
            var code = RandomCode(FullCharset);
            if (!await ExistsAsync(code, ct)) return code;
        }
        throw new InvalidOperationException("远程码空间耗尽");
    }

    private Task<bool> ExistsAsync(string code, CancellationToken ct)
        => db.Devices.AsNoTracking().AnyAsync(d => d.RemoteCode == code, ct);

    private static string RandomCode(string charset)
    {
        var bytes = RandomGenerator.Bytes(Length);
        var chars = new char[Length];
        for (var i = 0; i < Length; i++)
            chars[i] = charset[bytes[i] % charset.Length];
        return new string(chars);
    }
}

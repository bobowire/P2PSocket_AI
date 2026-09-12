namespace P2P.Core.Protocol;

/// <summary>
/// 打洞并发路数 N 的协议级约束（OQ-1/OQ-19/TD-20：1~5，缺省 3）。
/// 客户端 settings 校验与服务端 0x70 校验回填共用同一口径（AI-11 单一事实来源）。
/// </summary>
public static class PunchPolicy
{
    public const byte MinConcurrency = 1;
    public const byte MaxConcurrency = 5;
    public const byte DefaultConcurrency = 3;

    /// <summary>0x70.punchConcurrency 合法化：null（未携带/旧端）或越界 → 缺省 3；1~5 原样采纳。</summary>
    public static byte Normalize(byte? requested)
        => requested is >= MinConcurrency and <= MaxConcurrency ? requested.Value : DefaultConcurrency;
}

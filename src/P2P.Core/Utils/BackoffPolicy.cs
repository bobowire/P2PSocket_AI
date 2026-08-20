namespace P2P.Core.Utils;

/// <summary>
/// 指数退避策略（settings.json reconnect：minSec=1 起指数增长，上限 maxSec=30，08 §5.2）。
/// 纯函数、无随机抖动——确定性序列便于测试与 NAT 侧行为可预期。
/// </summary>
public sealed class BackoffPolicy(TimeSpan min, TimeSpan max)
{
    public static BackoffPolicy ReconnectDefault { get; } = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));

    public TimeSpan Min { get; } = min;
    public TimeSpan Max { get; } = max;

    /// <summary>第 attempt 次失败后的等待时长（attempt 从 0 起）：min * 2^attempt，封顶 max。</summary>
    public TimeSpan ComputeDelay(int attempt)
    {
        if (attempt < 0) throw new ArgumentOutOfRangeException(nameof(attempt));
        // 用乘法逐步封顶，防大 attempt 溢出
        var delay = Min;
        for (var i = 0; i < attempt && delay < Max; i++)
        {
            var next = delay + delay;
            delay = next > Max || next < delay ? Max : next;
        }
        return delay < Min ? Min : delay;
    }
}

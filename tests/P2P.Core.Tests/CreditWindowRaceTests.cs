using P2P.Core.Tunnel;
using Xunit;

namespace P2P.Core.Tests;

/// <summary>
/// M2_38 根因实证：CreditWindow 账本跨线程原子性。生产线程拓扑（M2-21 实际集成形态）：
/// TryConsume 跑在每 channel splice 发送循环（SpliceOutAsync→SendDataAsync→CreditGate.AcquireAsync），
/// Grant 跑在会话接收循环（WINDOW 帧分支）——同一 _available 字段被两线程非原子读-改-写，
/// 交错时 Grant 的写入可被并发 TryConsume 的旧值写入覆盖=永久丢失信用（发送侧在差 k×chunk 字节处
/// 永久挂起、会话存活零日志=中继满载间歇停滞签名）。测试以单消费者+守门生产者对打复现：
/// 守门（已消费领先已授信 ≥1 chunk 才授信；单生产者使守门精确）保证 Grant 永不触达 capacity 封顶，
/// 末态账目恒等式 avail == capacity + Σgranted − Σconsumed 可作精确判据；任何丢失更新表现为
/// ①消费侧在预算内永久停滞（watchdog 超时检出——丢失 1×chunk 即差 1368B 不够最后一扣）或
/// ②恒等式破缺。
/// </summary>
public class CreditWindowRaceTests
{
    private const int Chunk = 1368; // 生产 ChunkSize（MappingEngine.ChunkSize = PtpFrameCodec.MaxUdpDgramPlain）

    [Fact]
    public void CreditWindow_ConcurrentTryConsumeAndGrant_NoLostUpdate()
    {
        const int rounds = 8;
        const int grantsPerRound = 4000;
        for (var round = 0; round < rounds; round++)
            RunRound(CreditWindow.DefaultCapacity, grantsPerRound, round);
    }

    private static void RunRound(int capacity, int grants, int round)
    {
        var window = new CreditWindow(capacity);
        var chunksPerWindow = capacity / Chunk;
        var targetConsumed = chunksPerWindow + grants; // 总预算 = 初始窗口 + 全部授信
        var consumed = 0;
        var granted = 0;
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var consumer = Task.Run(() =>
        {
            while (Volatile.Read(ref consumed) < targetConsumed && !watchdog.IsCancellationRequested)
            {
                if (window.TryConsume(Chunk)) Interlocked.Increment(ref consumed);
                else Thread.SpinWait(4); // 耗尽自旋候授信——纯自旋最大化 TryConsume/Grant 内存交错密度
            }
        }, watchdog.Token);

        var producer = Task.Run(() =>
        {
            while (Volatile.Read(ref granted) < grants && !watchdog.IsCancellationRequested)
            {
                // 守门：已消费领先已授信 ≥1 chunk（avail ≤ capacity − chunk → Grant 永不封顶吸收，
                // 恒等式保持精确）；单生产者线程使 granted 读数即自身计数，判定无竞态
                if (Volatile.Read(ref consumed) - Volatile.Read(ref granted) >= 1)
                {
                    window.Grant(Chunk);
                    Interlocked.Increment(ref granted);
                }
                else Thread.SpinWait(4);
            }
        }, watchdog.Token);

        var allDone = Task.WaitAll([consumer, producer], TimeSpan.FromSeconds(15));
        Assert.True(allDone, $"第 {round} 轮：消费侧在预算内永久停滞（丢失授信的账本黑洞）——" +
            $"consumed={Volatile.Read(ref consumed)}/{targetConsumed}，granted={Volatile.Read(ref granted)}/{grants}");

        // 账目恒等式：终态可用 == 初始容量 + Σ授信 − Σ消费（精确相等；任何偏差=丢失更新）
        var expectedAvail = capacity + Chunk * grants - Chunk * targetConsumed;
        Assert.True(Volatile.Read(ref consumed) == targetConsumed,
            $"第 {round} 轮：消费未达目标 consumed={consumed}/{targetConsumed}");
        Assert.Equal(expectedAvail, window.Available);
    }
}

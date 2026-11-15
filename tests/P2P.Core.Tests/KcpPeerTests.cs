// M6-01 KCP 协议核属性单测（设计 11 §11、M6-01 完成判定）：
// 受控丢包 0~50% × 乱序 × 重复注入下可靠有序性质（零缺段/零重复交付/零死锁、闭合窗收敛、
// ACK 全确认）+ 对拍参考行为关键场景（RTO 退避序列 nodelay ×1.5 / 快重传触发点先于 RTO）+
// WINS 裁剪的窗口重开死锁补偿。介质=脚本桩（确定性：计数取模丢包/相邻交换/复制），时钟注入。
using P2P.Core.Transport.Kcp;
using Xunit;

namespace P2P.Core.Tests;

public class KcpPeerTests
{
    private const uint Conv = 0x13572468u;

    /// <summary>脚本介质：捕获双端 flush 产物（待投递队列），Exchange 按确定性策略投递。</summary>
    private sealed class Wire
    {
        public long Clock;
        public KcpPeer A = null!, B = null!;
        public readonly List<byte[]> PendingAtoB = [];
        public readonly List<byte[]> PendingBtoA = [];

        public int LossEvery;           // 每 N 个投递包丢 1（0=不丢；序号%N==0 丢）
        public bool ReorderPairs;       // 相邻交换
        public bool Duplicate;          // 每包复制一份
        public bool BlackholeAtoB;      // A→B 全丢（黑洞）
        public Func<int, bool>? DropAtoBAt; // 按投递序号定点丢（快重传用例）

        public long DeliveriesDropped;
        private int _seqA, _seqB;

        public IKcpDatagramSink SinkA { get; }
        public IKcpDatagramSink SinkB { get; }

        public Wire()
        {
            SinkA = new SinkImpl(PendingAtoB);
            SinkB = new SinkImpl(PendingBtoA);
        }

        private sealed class SinkImpl(List<byte[]> target) : IKcpDatagramSink
        {
            public void Send(ReadOnlySpan<byte> datagram) => target.Add(datagram.ToArray());
        }

        /// <summary>双向投递一跳：A→B 与 B→A 各按策略变换后注入对端（注入后即时 flush，壳层同款）。</summary>
        public void Exchange()
        {
            Deliver(PendingAtoB, B, ref _seqA, BlackholeAtoB, DropAtoBAt);
            Deliver(PendingBtoA, A, ref _seqB, false, null);
        }

        private void Deliver(List<byte[]> pending, KcpPeer to, ref int seq, bool blackhole,
            Func<int, bool>? dropAt)
        {
            if (pending.Count == 0) return;
            var batch = ReorderPairs ? SwapPairs(pending) : pending;
            foreach (var dgram in batch)
            {
                seq++;
                if (blackhole || (LossEvery > 0 && seq % LossEvery == 0) || (dropAt?.Invoke(seq) ?? false))
                {
                    DeliveriesDropped++;
                    continue;
                }
                if (Duplicate)
                    to.Input(dgram);
                to.Input(dgram);
                to.Flush(); // 壳层即时 flush（ACK/快重传即时回流）
            }
            pending.Clear();
        }

        private static List<byte[]> SwapPairs(List<byte[]> list)
        {
            var result = new List<byte[]>(list);
            for (var i = 0; i + 1 < result.Count; i += 2)
                (result[i], result[i + 1]) = (result[i + 1], result[i]);
            return result;
        }
    }

    private static (Wire Wire, KcpPeer A, KcpPeer B) CreatePair(
        Func<KcpPeerOptions, KcpPeerOptions>? tune = null, uint convA = Conv, uint convB = Conv)
    {
        var wire = new Wire();
        var opts = new KcpPeerOptions { ClockMs = () => wire.Clock };
        opts = tune?.Invoke(opts) ?? opts;
        var a = new KcpPeer(convA, wire.SinkA, opts);
        var b = new KcpPeer(convB, wire.SinkB, opts with { });
        wire.A = a;
        wire.B = b;
        a.Update();
        b.Update();
        return (wire, a, b);
    }

    /// <summary>时钟步进泵：每 tick 双端 Update（RTO/节奏 flush）+ 交换一跳；直至 done 或超预算。</summary>
    private static void Pump(Wire wire, KcpPeer a, KcpPeer b, Func<bool> done, int maxTicks = 6000)
    {
        for (var i = 0; i < maxTicks && !done(); i++)
        {
            wire.Clock += 10;
            a.Update();
            b.Update();
            wire.Exchange();
        }
    }

    private static byte[] Drain(KcpPeer peer)
    {
        var total = new List<byte>();
        while (peer.TryRead(out var seg))
            total.AddRange(seg);
        return [.. total];
    }

    private static byte[] Payload(int len, byte seed)
    {
        var data = new byte[len];
        for (var i = 0; i < len; i++)
            data[i] = (byte)(seed + i * 7);
        return data;
    }

    // ── 性质：可靠有序（丢包 × 乱序 × 重复注入矩阵）──────────────────

    [Theory]
    [InlineData(0, false, false)]   // 0% 丢包
    [InlineData(2, false, false)]   // 50% 丢包（每 2 丢 1）——极端上界
    [InlineData(3, false, false)]   // ~33%
    [InlineData(10, false, false)]  // 10%
    [InlineData(10, true, false)]   // 10% + 乱序
    [InlineData(10, true, true)]    // 10% + 乱序 + 重复
    [InlineData(4, true, true)]     // 25% + 乱序 + 重复
    public void 受控扰动下_可靠有序交付_闭合窗收敛(int lossEvery, bool reorder, bool dup)
    {
        var (wire, a, b) = CreatePair();
        wire.LossEvery = lossEvery;
        wire.ReorderPairs = reorder;
        wire.Duplicate = dup;

        var stream = new List<byte>();
        // 混合尺寸写入：满 MSS 分帧 + 跨 MSS 分帧 + 尾段追加 + 微帧
        foreach (var (len, seed) in new[] { (1376, 1), (1376, 2), (5000, 3), (1, 4), (1376, 5), (300, 6), (1376, 7) })
        {
            var chunk = Payload(len, (byte)seed);
            Assert.Equal(len, a.Send(chunk));
            a.Flush(); // 壳层即时派发
            stream.AddRange(chunk);
        }
        Assert.False(a.Input([])); // 空包拒绝

        var received = new List<byte>();
        Pump(wire, a, b, () =>
        {
            received.AddRange(Drain(b));
            return received.Count >= stream.Count && a.WaitSnd == 0;
        });

        Assert.True(received.Count >= stream.Count,
            $"欠投 {received.Count}/{stream.Count}（丢 {wire.DeliveriesDropped} 包后 {a.Retransmissions} 次重传）");
        Assert.Equal(stream.Count, received.Count); // 零缺段且零重复（计数恰等）
        Assert.True(stream.ToArray().AsSpan().SequenceEqual(received.ToArray()),
            "流内容不一致（缺段/重复/乱序交付）");
        Assert.Equal(0, a.WaitSnd);                 // 闭合窗收敛：对端全部确认
        Assert.Equal(0UL, b.OutOfWindowDrops);
    }

    [Fact]
    public void 零丢包_单帧往返_零重传()
    {
        var (wire, a, b) = CreatePair();
        a.Send(Payload(100, 1));
        a.Flush();
        wire.Exchange();
        var got = Drain(b);
        Assert.Equal(100, got.Length);
        Assert.Equal(0UL, a.Retransmissions);
        Assert.Equal(0UL, a.FastRetransmissions);
    }

    // ── 对拍参考行为：RTO 退避序列（nodelay ×1.5）───────────────────

    [Fact]
    public void 黑洞下_RTO退避序列_200_500_950_1625()
    {
        // 初始 RTO=200（RtoDef，无 ACK 永不更新）；nodelay=1 → 每次退避 rto += rto/2：
        // 重传点 200 → rto=300 → rto=450 → rto=675…即触发时刻 200/500/950/1625ms（对照 ikcp.c 数学）
        var (wire, a, b) = CreatePair();
        wire.BlackholeAtoB = true;
        a.Send(Payload(1376, 1));
        a.Flush();

        void TickTo(long t)
        {
            while (wire.Clock < t)
            {
                wire.Clock += 10;
                a.Update();
                b.Update();
                wire.Exchange(); // A→B 全丢；B 侧无输入无输出
            }
        }

        TickTo(190);
        Assert.Equal(0UL, a.Retransmissions); // 首发不算重传
        TickTo(200);
        Assert.Equal(1UL, a.Retransmissions);
        TickTo(490);
        Assert.Equal(1UL, a.Retransmissions);
        TickTo(500);
        Assert.Equal(2UL, a.Retransmissions);
        TickTo(940);
        Assert.Equal(2UL, a.Retransmissions);
        TickTo(950);
        Assert.Equal(3UL, a.Retransmissions);
        TickTo(1620);
        Assert.Equal(3UL, a.Retransmissions);
        TickTo(1630);
        Assert.Equal(4UL, a.Retransmissions);
        // 裁剪③：永不放弃（dead_link 不实现）——持续重传不断链
        TickTo(60_000);
        Assert.True(a.Retransmissions > 10, $"重传应持续（实际 {a.Retransmissions}）");
    }

    // ── 对拍参考行为：快重传先于 RTO ────────────────────────────────

    [Fact]
    public void 丢中间段_快重传触发_先于RTO_恢复有序交付()
    {
        var (wire, a, b) = CreatePair();
        wire.DropAtoBAt = seq => seq == 2; // 定点丢第 2 个 A→B 包（sn=1 所在数据报）

        var stream = new List<byte>();
        for (var i = 0; i < 4; i++) // 4 个满 MSS 段（各占独立数据报，sn=0..3）
        {
            var chunk = Payload(1376, (byte)(i + 1));
            Assert.Equal(1376, a.Send(chunk));
            a.Flush();
            stream.AddRange(chunk);
        }
        Assert.Equal(4, wire.PendingAtoB.Count); // 满段一包一段

        // 时钟不动（远小于 RTO 200ms），一轮交换：B 收 0/2/3 → ACK 回流 → A 即时快重传 sn=1
        wire.Exchange();

        Assert.Equal(0UL, a.Retransmissions);      // 未走 RTO 路径
        Assert.True(a.FastRetransmissions >= 1, "快重传须在 RTO 前触发");
        Assert.True(wire.Clock < 200);

        // 续泵：重传段送达 → 全量有序交付
        var received = new List<byte>();
        Pump(wire, a, b, () =>
        {
            received.AddRange(Drain(b));
            return received.Count >= stream.Count && a.WaitSnd == 0;
        });
        Assert.Equal(stream.Count, received.Count);
        Assert.True(stream.ToArray().AsSpan().SequenceEqual(received.ToArray()), "快重传后交付须有序完整");
    }

    // ── WINS 裁剪补偿：窗口重开通告防死锁 ──────────────────────────

    [Fact]
    public void 接收窗满载重开_窗口通告ACK_续传不死锁()
    {
        // RcvWnd=2：发送 5 段 → 对端收 2 段窗满（ACK 通告 wnd=0）→ 发送侧 snd_buf 排空后
        // 若无窗口通告将永久停摆（WINS 裁剪死锁场景）；接收侧读出后须补发窗口通告恢复续传
        var (wire, a, b) = CreatePair(o => o with { RcvWnd = 2 });
        var stream = new List<byte>();
        for (var i = 0; i < 5; i++)
        {
            var chunk = Payload(1376, (byte)(i + 1));
            a.Send(chunk);
            a.Flush();
            stream.AddRange(chunk);
        }

        var received = new List<byte>();
        Pump(wire, a, b, () =>
        {
            received.AddRange(Drain(b)); // 持续读出（窗口重开触发点）
            return received.Count >= stream.Count && a.WaitSnd == 0;
        });

        Assert.Equal(stream.Count, received.Count);
        Assert.True(stream.ToArray().AsSpan().SequenceEqual(received.ToArray()), "窗口收窄下仍须完整有序");
        Assert.True(b.WindowUpdates >= 1, "窗口满载重开须产生窗口通告 ACK");
        Assert.Equal(0, a.WaitSnd);
    }

    // ── 防线：conv 不符 / 畸形包 / 幂等 ────────────────────────────

    [Fact]
    public void conv不符_整包拒绝_零状态影响()
    {
        var (wire, a, b) = CreatePair(convB: Conv + 1);
        a.Send(Payload(100, 1));
        a.Flush();
        Assert.Single(wire.PendingAtoB);
        Assert.False(b.Input(wire.PendingAtoB[0])); // conv 不符（B 的 conv=Conv+1）
        Assert.Equal(0UL, b.SegmentsReceived);
        Assert.Equal(0UL, b.AcksSent);
    }

    [Fact]
    public void 畸形包_拒绝_不抛异常()
    {
        var (_, _, b) = CreatePair();
        Assert.False(b.Input(new byte[10]));   // 短于段头
        Assert.False(b.Input(new byte[24]));   // 头全零（cmd=0 非法）
        var bad = new byte[28];                // len 超 remain
        bad[4] = 81;                           // cmd=PUSH
        bad[20] = 200;                         // len=200 > 4B 余量
        Assert.False(b.Input(bad));
        Assert.Equal(0UL, b.SegmentsReceived);
    }

    [Fact]
    public void 同毫秒重复Flush_幂等_无重复发送()
    {
        var (wire, a, b) = CreatePair();
        a.Send(Payload(1376, 1));
        a.Flush();
        var sentAfterFirst = a.SegmentsSent;
        a.Flush();
        a.Flush();
        Assert.Equal(sentAfterFirst, a.SegmentsSent); // 幂等：同刻重复 flush 不重发
        wire.Exchange();
        var got = Drain(b);
        Assert.Equal(1376, got.Length); // 恰一段、无重复字节
    }
}

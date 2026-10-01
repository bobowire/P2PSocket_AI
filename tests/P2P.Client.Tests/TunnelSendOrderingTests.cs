using System.Collections.Concurrent;
using System.Threading.Channels;
using P2P.Client.Tunnel;
using P2P.Core.Crypto;
using P2P.Core.Tunnel;
using Xunit;

namespace P2P.Client.Tests;

/// <summary>
/// M2_38 根因回归（发送侧乱序）：SendChannelFrameAsync 须在 _sendGate 内完成 counter 分配+密封+写传输。
/// 修复前写传输在闸外——多发送方并发（8 并发 splice DATA/WINDOW 回报/keepalive）释放闸后的线程调度
/// 可倒置线上到达顺序，接收侧反重放（SEC-12 counter 严格递增）判"回退"整帧丢弃 → 字节流静默缺段 +
/// 该帧信用永不回报 = 永久停滞（中继满载间歇复现的第二个根因，与 CreditWindow 竞态独立）。
/// 本测试以随机抖动传输复现"闸外写"的调度倒置窗口，断言全帧到达且每 channel 字节序=发送序；
/// 数据量刻意超 64KiB 信用窗（逼出 WINDOW 回报循环，顺带回归信用闭环）。
/// </summary>
public sealed class TunnelSendOrderingTests
{
    private const int PayloadLen = 1368; // 生产 ChunkSize（MappingEngine.ChunkSize）
    private static readonly TimeSpan Idle = TimeSpan.FromHours(1); // 关心跳防干扰

    /// <summary>抖动传输对：多数帧 0~1ms、偶发（1/50）30~80ms 长尾——模拟满载下 TCP 写阻塞：
    /// 一帧落线被压后期间其余 channel 持续过闸泵帧，倒置深度须能超 ReplayWindow 容差 64
    /// （窗口内小倒置被滑动窗按序外到达接受，不构成缺陷；≥64 才丢帧=字节缺段）。
    /// 随机源用 Random.Shared（线程安全；共享 Random 实例并发调用会状态损坏恒返 0=抖动静默失效，首版实证）。</summary>
    private sealed class JitterTransport(Channel<byte[]> inbox, Channel<byte[]> outbox) : ITunnelTransport
    {
        public static (JitterTransport A, JitterTransport B) CreatePair()
        {
            var ab = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
            var ba = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
            return (new JitterTransport(ba, ab), new JitterTransport(ab, ba));
        }

        public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
        {
            var copy = frame.ToArray();
            var delay = Random.Shared.Next(150) == 0 ? Random.Shared.Next(250, 350) : 0;
            if (delay > 0) await Task.Delay(delay, ct).ConfigureAwait(false);
            await outbox.Writer.WriteAsync(copy, ct).ConfigureAwait(false);
        }

        public async ValueTask<byte[]?> ReceiveAsync(CancellationToken ct = default)
        {
            try { return await inbox.Reader.ReadAsync(ct).ConfigureAwait(false); }
            catch (ChannelClosedException) { return null; }
        }

        public ValueTask DisposeAsync()
        {
            inbox.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>目标侧记录 handler：按 channel 记载荷序号（前 4 字节）并回报信用（复刻 SpliceIn 消费回报）。</summary>
    private sealed class RecordingCreditHandler : ITunnelChannelHandler
    {
        public ConcurrentDictionary<uint, List<int>> Seen { get; } = [];

        public void OnOpen(TunnelSession session, uint channelId, OpenPayload open) { }
        public void OnOpenResult(TunnelSession session, uint channelId, OpenResultPayload result) { }

        public void OnData(TunnelSession session, uint channelId, ReadOnlyMemory<byte> data)
        {
            var list = Seen.GetOrAdd(channelId, _ => []);
            lock (list) list.Add(BitConverter.ToInt32(data.Span));
            _ = session.SendWindowCreditAsync(channelId, data.Length); // 消费即回报（fire-and-forget，接收环不阻塞）
        }

        public void OnUdpDgram(TunnelSession session, uint channelId, ReadOnlyMemory<byte> datagram) { }
        public void OnClose(TunnelSession session, uint channelId) { }
    }

    private sealed class NopHandler : ITunnelChannelHandler
    {
        public void OnOpen(TunnelSession session, uint channelId, OpenPayload open) { }
        public void OnOpenResult(TunnelSession session, uint channelId, OpenResultPayload result) { }
        public void OnData(TunnelSession session, uint channelId, ReadOnlyMemory<byte> data) { }
        public void OnUdpDgram(TunnelSession session, uint channelId, ReadOnlyMemory<byte> datagram) { }
        public void OnClose(TunnelSession session, uint channelId) { }
    }

    private static async Task<(TunnelSession A, TunnelSession B, RecordingCreditHandler Hb)> EstablishAsync()
    {
        var staticA = EcKeyPair.Generate();
        var staticB = EcKeyPair.Generate();
        var (ta, tb) = JitterTransport.CreatePair();
        var hb = new RecordingCreditHandler();
        var sessionId = Guid.NewGuid();
        var peerA = Guid.NewGuid();
        var peerB = Guid.NewGuid();
        var options = new TunnelSessionOptions { KeepaliveInterval = Idle };

        var connectTask = TunnelSession.ConnectAsync(sessionId, peerB, staticA,
            staticB.ExportPublicKey(), ta, new NopHandler(), options);
        var t1 = await tb.ReceiveAsync() ?? throw new IOException("无 THello1");
        var acceptTask = TunnelSession.AcceptAsync(peerA, t1, staticB,
            staticA.ExportPublicKey(), tb, hb, options);
        return (await connectTask, await acceptTask, hb);
    }

    private static async Task UntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = Environment.TickCount64 + (long)(timeout ?? TimeSpan.FromSeconds(5)).TotalMilliseconds;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException($"等待超时：{what}");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task 并发多channel发送_抖动传输_全帧到达且序号不丢不乱()
    {
        const int channels = 6;
        const int framesPerChannel = 150; // 每 channel ~205KB > 64KiB 信用窗——逼出 WINDOW 回报循环
        var (a, b, hb) = await EstablishAsync();
        await using (a)
        await using (b)
        {
            // 每 channel 串行（生产语义：一条 splice 循环）、channel 间并发——counter 倒置只可能来自发送侧
            var sends = Enumerable.Range(0, channels).Select(async c =>
            {
                var ch = a.AllocateChannelId();
                var payload = new byte[PayloadLen];
                for (var seq = 0; seq < framesPerChannel; seq++)
                {
                    BitConverter.GetBytes(seq).CopyTo(payload, 0);
                    await a.SendDataAsync(ch, payload);
                }
            });
            // 丢帧时该 channel 信用永不回报 → 发送侧窗口耗尽挂起：15s 护栏把挂起转成明确失败
            await Task.WhenAll(sends).WaitAsync(TimeSpan.FromSeconds(15));

            var total = channels * framesPerChannel;
            await UntilAsync(() => hb.Seen.Values.Sum(l => { lock (l) return l.Count; }) >= total,
                $"全帧到达（{total} 帧）", TimeSpan.FromSeconds(10));

            Assert.Equal(channels, hb.Seen.Count);
            foreach (var (_, list) in hb.Seen)
            {
                lock (list)
                    Assert.Equal(Enumerable.Range(0, framesPerChannel).ToList(), list); // 不丢（反重放误杀）不乱（字节序）
            }
        }
    }
}

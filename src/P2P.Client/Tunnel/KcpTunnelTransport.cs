// M6-02 KcpTunnelTransport（设计 11 §5 全文，M6 清单 M6-02）：UDP 直连 KCP 多 conv 承载。
// - 收包泵：魔数 0x4B → conv 表命中 → KcpPeer.Input + 即时提取/flush；未命中丢弃+计数
//   （未授权 conv 防线——无 lazy accept，设计 11 §4.3）。非魔数 → 白名单 {UDP_DGRAM, FRAG}
//   → 裸旁路队列（有界 DropOldest）；其余（迟到握手帧/迟到 STUN 应答/垃圾）丢弃+计数——
//   顺带修复现状潜伏缺陷：迟到 STUN 重传应答在裸承载上以形似 OPEN 垃圾帧进入会话接收循环
//   致整隧道误杀（设计 11 §5.1）。
// - 发包：明文帧头路由（OPEN/KEEPALIVE/PING/PONG/REKEY 族→conv 0；OPEN_RESULT/DATA/CLOSE/
//   WINDOW→conv=channelId（未登记/超上限→降级 conv 0）；UDP_DGRAM/FRAG→裸 SendToAsync 钉定）。
// - 更新泵：单 PeriodicTimer 驱动全 conv Update（RTO/窗口通告）；发送侧单任务串行 SendToAsync。
// - 背压：真实在途上界=应用层 WINDOW 信用（64KiB/channel，设计 11 §5.6）——KCP 层 rcv_wnd
//   不因消费停滞收敛（ExtractFrames 在泵上即时提取排空 rcv_queue，审查修正口径：原「应用
//   不读→窗收敛」不成立；_rxQueue 无界，堆积上界=信用窗×ConvCap+低速率控制帧）；裸队列
//   DropOldest（丢一包=UDP_DGRAM 语义内损失，绝不阻塞收包泵——泵阻塞拖累全部 conv 的
//   ACK 时效，设计 11 §5.1）。
// - Adopt：打洞 socket 所有权移交（UdpPunchTransport.ReleaseSocket 后由本类独占；Dispose 关闭）。
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using P2P.Core.Protocol;
using P2P.Core.Transport.Kcp;
using P2P.Core.Tunnel;

namespace P2P.Client.Tunnel;

/// <summary>KCP 承载参数（设计 11 §5.4 默认表；M6-07 基准调参后回填终值）。</summary>
public sealed record KcpTunnelTransportOptions
{
    /// <summary>更新泵周期（驱动全 conv Update/Flush——重传与窗口通告）。</summary>
    public TimeSpan UpdateInterval { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <summary>KCP 算法参数组（Nodelay/Resend/Nc/SndWnd/RcvWnd/Mtu，§5.4 表）。</summary>
    public KcpPeerOptions Peer { get; init; } = new();

    /// <summary>demux 魔数（KCP 包首字节前缀；02 §4.3 混淆扩展点预留衔接位）。</summary>
    public byte Magic { get; init; } = 0x4B;

    /// <summary>channel conv 上限/会话（超限 channel 全帧降级 conv 0——保正确性牺牲隔离）。</summary>
    public int ConvCap { get; init; } = 256;

    /// <summary>裸旁路接收队列容量（DropOldest）。</summary>
    public int RawQueueCap { get; init; } = 256;
}

/// <summary>
/// UDP 直连 KCP 多 conv 承载（单 socket：conv 0 全局控制流 + channel conv ×N + 裸 UDP 旁路）。
/// 非线程安全面仅 SendAsync/EnsureChannel/RemoveChannel/ReceiveAsync 契约（同 UdpPunchTransport）；
/// conv 表为共享可变状态白名单豁免点（ConcurrentDictionary，设计 11 §5.3）。
/// </summary>
public sealed class KcpTunnelTransport : ITunnelTransport, IKcpConvControl
{
    /// <summary>整帧上限（同 <see cref="Punch.UdpPunchTransport.MaxUdpFrame"/>；02 §4.3 发送侧保证）。</summary>
    public const int MaxUdpFrame = Punch.UdpPunchTransport.MaxUdpFrame;

    /// <summary>KCP 线上包上限（含 1B 魔数前缀；随 Mtu 参数）。</summary>
    private int MaxWireDatagram => 1 + _options.Peer.Mtu;

    private sealed class ConvEntry
    {
        public required KcpPeer Peer;
        public required object Gate; // 该 conv 的算法层串行锁（Send/Input/Update 三面并发）
        public byte[] Stream = new byte[4096]; // KCP 流缓冲（PTP 帧自分界累积）
        public int StreamLen;
        public bool Draining; // 排空后销毁（RemoveChannel 语义）
    }

    private readonly Socket _socket;
    private readonly IPEndPoint _peer;
    private readonly KcpTunnelTransportOptions _options;
    private readonly ConcurrentDictionary<uint, ConvEntry> _convs = new();
    private readonly ConcurrentDictionary<uint, byte> _degraded = new(); // ConvCap 超限降级集合
    private readonly Channel<(byte[] Buf, int Len)> _outbox =
        Channel.CreateUnbounded<(byte[], int)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<byte[]> _rxQueue = Channel.CreateUnbounded<byte[]>(); // KCP 帧+裸帧汇流
    private readonly Channel<byte[]> _rawQueue; // 裸旁路（有界 DropOldest+丢弃计数，泵绝不阻塞）
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _recvPump;
    private readonly Task _updatePump;
    private readonly Task _sendLoop;
    private readonly Task _rawMerger;
    private readonly IKcpDatagramSink _sink;
    private int _channelConvCount;
    private int _disposed;
    private int _faulted;

    // ── 诊断计数（M6-08 可选接入本地诊断页）────────────────────────
    public long UnknownConvDropped { get; private set; }
    public long MalformedDropped { get; private set; }
    public long NonWhitelistDropped { get; private set; } // 迟到 STUN/握手帧/垃圾（白名单闭包）
    public long RawQueueDropped { get; private set; }
    public long DegradedFrameCount { get; private set; } // 降级 conv 0 承载的 channel 帧数
    public long UpdatePumpErrors { get; private set; } // 更新泵单 conv 异常（审查修复：原计入 MalformedDropped 会污染畸形丢弃读数）
    public int ConvCount => Volatile.Read(ref _channelConvCount);

    /// <summary>socket 所有权移交构造（设计 11 §5.4/§7.3）：调用方（UdpPunchTransport）须先
    /// ReleaseSocket 放弃关闭权；本类 Dispose 时关闭 socket（承载关闭=会话销毁契约）。</summary>
    private KcpTunnelTransport(Socket socket, IPEndPoint pinnedEndpoint, KcpTunnelTransportOptions? options)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(pinnedEndpoint);
        _socket = socket;
        _peer = pinnedEndpoint;
        _options = options ?? new KcpTunnelTransportOptions();
        _rawQueue = CreateRawQueue(_options.RawQueueCap, this);
        _sink = new OutboxSink(this);
        _ = _convs.GetOrAdd(0, _ => CreateEntry(0)); // conv 0：全局控制流（会话构造即存在）
        _recvPump = ReceivePumpAsync(_cts.Token);
        _updatePump = UpdatePumpAsync(_cts.Token);
        _sendLoop = SendLoopAsync(_cts.Token);
        _rawMerger = RawMergerAsync(_cts.Token);
    }

    private static Channel<byte[]> CreateRawQueue(int cap, KcpTunnelTransport owner)
    {
        var n = Math.Max(1, cap);
        return Channel.CreateBounded<byte[]>(new BoundedChannelOptions(n)
        {
            FullMode = BoundedChannelFullMode.DropOldest, // UDP_DGRAM 丢一包=语义内损失
            SingleReader = true,
        }, _ => owner.RawQueueDropped++);
    }

    private ConvEntry CreateEntry(uint conv)
    {
        var entry = new ConvEntry
        {
            Peer = new KcpPeer(conv, _sink, _options.Peer), // options 为只读复制入算法层，无须克隆（审查修复：原 with {} 空克隆）
            Gate = new object(),
        };
        entry.Peer.Update(); // 初始化（算法层 updated 标志，参考 ikcp 首调语义）
        return entry;
    }

    /// <summary>打洞 socket 所有权移交（设计 11 §7.2 承载切换）：握手成功瞬间由 Puncher 调用。</summary>
    public static KcpTunnelTransport Adopt(Socket socket, IPEndPoint pinnedEndpoint,
        KcpTunnelTransportOptions? options = null)
        => new(socket, pinnedEndpoint, options);

    /// <summary>钉定对端端点（Adopt 所携——握手期首包钉定语义的延续）。</summary>
    public IPEndPoint Peer => _peer;

    // ── IKcpConvControl（conv 生命周期，设计 11 §4.2/§4.3）──────────

    public void EnsureChannel(uint channelId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (channelId == 0) return; // conv 0 生而存在
        if (_convs.ContainsKey(channelId)) return;
        var count = Interlocked.Increment(ref _channelConvCount);
        if (count > _options.ConvCap)
        {
            Interlocked.Decrement(ref _channelConvCount);
            _degraded[channelId] = 1; // 超上限：全帧降级 conv 0（保正确性牺牲隔离，§4.3）
            return;
        }
        var entry = CreateEntry(channelId);
        if (!_convs.TryAdd(channelId, entry))
        {
            Interlocked.Decrement(ref _channelConvCount); // 并发同 id 竞败：仅胜者计数
        }
    }

    public void RemoveChannel(uint channelId)
    {
        if (channelId == 0) return; // conv 0 全局控制流永存（机制化 TryReap 注记的不变量，审查修复）
        _degraded.TryRemove(channelId, out _); // 超限降级项随 channel 销毁清理（审查修复：原无清理路径，
        // 超限 id 从不入 _convs 故 TryReap 够不着——channel 高频开关的长会话下单调增长）
        if (!_convs.TryGetValue(channelId, out var entry)) return;
        entry.Draining = true; // 排空后销毁：在途段重传至确认（CLOSE 可靠到达）——更新泵收尸
    }

    /// <summary>排空判定+销毁（更新泵调用；-1 保持 conv 0 永存）。</summary>
    private bool TryReap(ConvEntry entry, uint conv)
    {
        lock (entry.Gate)
        {
            if (!entry.Draining || entry.Peer.WaitSnd > 0) return false;
        }
        if (_convs.TryRemove(conv, out _))
        {
            Interlocked.Decrement(ref _channelConvCount);
            _degraded.TryRemove(conv, out _);
        }
        return true;
    }

    // ── ITunnelTransport ────────────────────────────────────────────

    public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        var span = frame.Span;
        if (span.Length is < PtpHeader.WireLen or > MaxUdpFrame)
            throw new ProtocolException($"KCP 承载 PTP 帧 {span.Length}B 非法（16~{MaxUdpFrame}B，02 §4.3）");
        var type = span[1];
        if (span[0] != PtpHeader.CurrentVer)
            throw new ProtocolException($"PTP 帧版本 {span[0]} 非法");

        if (type is PtpFrameType.UdpDgram or PtpFrameType.Frag)
        {
            // 裸旁路：丢包容忍语义保持（不经可靠层；钉定端点直发）。全程同步完成（审查去 async：
            // 原方法无 await 的状态机包装无意义）；TryWrite 失败=停机竞态，池缓冲须归还（审查修复：
            // 原漏还——channel 已完成则发送任务不会取走）
            var buf = ArrayPool<byte>.Shared.Rent(span.Length);
            span.CopyTo(buf);
            if (!_outbox.Writer.TryWrite((buf, span.Length)))
                ArrayPool<byte>.Shared.Return(buf);
            return ValueTask.CompletedTask;
        }

        var conv = ResolveConv(type, BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(2)));
        if (_convs.TryGetValue(conv, out var entry))
        {
            lock (entry.Gate)
            {
                if (!entry.Draining)
                {
                    entry.Peer.Send(span);
                    entry.Peer.Flush(); // 壳层即时派发（首发出窗即上 wire）
                    return ValueTask.CompletedTask;
                }
            }
        }
        // conv 未登记（发送侧异常时序——EnsureChannel 四触发点之外的残余）或排空中：
        // 降级 conv 0 承载（可靠有序不破坏，正确性优先；计数观测）
        DegradedFrameCount++;
        if (_convs.TryGetValue(0u, out var conv0))
        {
            lock (conv0.Gate)
            {
                conv0.Peer.Send(span);
                conv0.Peer.Flush();
            }
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>路由表（设计 11 §4.1：明文帧头判定，不解密）。</summary>
    private uint ResolveConv(byte type, uint channelId) => type switch
    {
        PtpFrameType.Open or PtpFrameType.Keepalive or PtpFrameType.Ping or PtpFrameType.Pong
            or PtpFrameType.Rekey or PtpFrameType.RekeyAck => 0u,
        PtpFrameType.OpenResult or PtpFrameType.Data or PtpFrameType.Close or PtpFrameType.Window
            => _convs.ContainsKey(channelId) || _degraded.ContainsKey(channelId) ? channelId : 0u,
        _ => throw new ProtocolException($"帧 type 0x{type:X2} 不在 KCP 承载路由域"),
    };

    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken ct = default)
    {
        while (await _rxQueue.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            if (_rxQueue.Reader.TryRead(out var frame))
                return frame;
        }
        return null; // 承载关闭（契约：null=触发会话销毁）
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _outbox.Writer.TryComplete();
        _rawQueue.Writer.TryComplete();
        _rxQueue.Writer.TryComplete();
        try { _cts.Cancel(); } catch { /* 停机 */ }
        await Task.WhenAll(SafeAwait(_recvPump), SafeAwait(_updatePump), SafeAwait(_sendLoop),
            SafeAwait(_rawMerger));
        _socket.Dispose(); // 承载关闭（所有权：Adopt 后独占）
        _cts.Dispose();

        static async Task SafeAwait(Task t)
        {
            try { await t.ConfigureAwait(false); }
            catch { /* 泵收尾异常不再上抛（含取消路径） */ }
        }
    }

    // ── 收包泵（单 socket Actor，设计 11 §5.1）─────────────────────

    private async Task ReceivePumpAsync(CancellationToken ct)
    {
        var buf = new byte[65_535];
        var anyEp = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult res;
            try
            {
                res = await _socket.ReceiveFromAsync(buf, SocketFlags.None, anyEp, ct);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                Fault("receive_closed");
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var n = res.ReceivedBytes;
            try
            {
                if (n >= 1 + KcpPeer.Overhead && n <= MaxWireDatagram && buf[0] == _options.Magic)
                {
                    HandleKcpDatagram(buf.AsSpan(1, n - 1));
                }
                else if (n >= PtpHeader.WireLen && n <= MaxUdpFrame && buf[0] == PtpHeader.CurrentVer)
                {
                    if (buf[1] is PtpFrameType.UdpDgram or PtpFrameType.Frag)
                        _rawQueue.Writer.TryWrite(buf.AsMemory(0, n).ToArray()); // DropOldest 自理
                    else
                        NonWhitelistDropped++; // 迟到 KEEPALIVE/OPEN 形似帧等：白名单闭包外
                }
                else
                {
                    // 迟到 STUN 应答（首字节 0x01 与 PTP ver 同值曾致误杀）/短包/垃圾
                    NonWhitelistDropped++;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                MalformedDropped++; // 单包异常不击杀泵（7×24 收口口径）；流破坏另行 Fault
            }
        }
    }

    private void HandleKcpDatagram(ReadOnlySpan<byte> kcpPacket)
    {
        var conv = BinaryPrimitives.ReadUInt32LittleEndian(kcpPacket);
        if (!_convs.TryGetValue(conv, out var entry))
        {
            UnknownConvDropped++; // 未授权 conv 防线（§4.3：无 lazy accept）
            return;
        }
        lock (entry.Gate)
        {
            if (!entry.Peer.Input(kcpPacket))
            {
                MalformedDropped++; // 畸形 KCP 包（conv 不符/段头越界）——丢弃不杀会话
                return;
            }
            ExtractFrames(entry);
            entry.Peer.Flush(); // 即时 ACK（kcp2k 同款壳层时机）
        }
    }

    /// <summary>KCP 流 → PTP 帧自分界（帧头 16B 明文含 u16 CipherLen，设计 11 §5.5）。
    /// 流被破坏（头非法）=承载不可信 → Fault 断链（SEC-12 同口径：AEAD 面前置的形态校验）。</summary>
    private void ExtractFrames(ConvEntry entry)
    {
        while (entry.Peer.TryRead(out var seg))
        {
            if (entry.StreamLen + seg.Length > entry.Stream.Length)
            {
                var newSize = Math.Max(Math.Max(entry.StreamLen + seg.Length, entry.Stream.Length * 2), 4096);
                Array.Resize(ref entry.Stream, newSize);
            }
            seg.CopyTo(entry.Stream, entry.StreamLen);
            entry.StreamLen += seg.Length;
        }

        var off = 0;
        while (entry.StreamLen - off >= PtpHeader.WireLen)
        {
            var ver = entry.Stream[off];
            var type = entry.Stream[off + 1];
            if (ver != PtpHeader.CurrentVer ||
                !(PtpFrameType.IsHandshake(type) || (type >= PtpFrameType.Open && type <= PtpFrameType.RekeyAck)))
            {
                Fault("kcp_stream_corrupted");
                return;
            }
            var total = PtpHeader.WireLen +
                        BinaryPrimitives.ReadUInt16LittleEndian(entry.Stream.AsSpan(off + 14));
            if (entry.StreamLen - off < total) break; // 未满一帧：留待后续段
            var frame = new byte[total];
            Buffer.BlockCopy(entry.Stream, off, frame, 0, total);
            _rxQueue.Writer.TryWrite(frame);
            off += total;
        }
        if (off > 0)
        {
            var leftover = entry.StreamLen - off;
            if (leftover > 0)
                Buffer.BlockCopy(entry.Stream, off, entry.Stream, 0, leftover);
            entry.StreamLen = leftover;
        }
    }

    // ── 更新泵（单 PeriodicTimer 驱动全 conv，设计 11 §5.3）────────

    private async Task UpdatePumpAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.UpdateInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                foreach (var (conv, entry) in _convs)
                {
                    try
                    {
                        lock (entry.Gate)
                        {
                            entry.Peer.Update();
                            ExtractFrames(entry); // 窗口通告等 Update 路径产出顺带提取
                        }
                        TryReap(entry, conv); // 排空收尸（conv 0 永不在 _convs 移除路径——Draining 恒 false）
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        UpdatePumpErrors++; // 单 conv 异常不杀泵（审查修复：独立计数，不再计入 MalformedDropped）
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* 停机 */ }
    }

    // ── 发送侧（单任务串行 socket 写；KCP flush 输出+裸旁路共用）────

    private async Task SendLoopAsync(CancellationToken ct)
    {
        await foreach (var (buf, len) in _outbox.Reader.ReadAllAsync(ct))
        {
            try
            {
                await _socket.SendToAsync(buf.AsMemory(0, len), SocketFlags.None, _peer, ct);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                Fault("send_failed");
                return;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }
    }

    /// <summary>裸旁路 → 汇流（设计 11 §5.1：conv 帧与裸帧汇入单一接收队列）。</summary>
    private async Task RawMergerAsync(CancellationToken ct)
    {
        await foreach (var frame in _rawQueue.Reader.ReadAllAsync(ct))
            _rxQueue.Writer.TryWrite(frame);
    }

    private void Fault(string reason)
    {
        if (Interlocked.Exchange(ref _faulted, 1) == 1) return;
        _ = DisposeAsync(); // 关泵+关 socket；_rxQueue 完成 → ReceiveAsync null → 会话断链
    }

    /// <summary>KCP flush 输出 → outbox（前缀魔数；ArrayPool 缓冲，发送任务归还）。</summary>
    private sealed class OutboxSink(KcpTunnelTransport owner) : IKcpDatagramSink
    {
        public void Send(ReadOnlySpan<byte> datagram)
        {
            var buf = ArrayPool<byte>.Shared.Rent(1 + datagram.Length);
            buf[0] = owner._options.Magic;
            datagram.CopyTo(buf.AsSpan(1));
            if (!owner._outbox.Writer.TryWrite((buf, 1 + datagram.Length)))
                ArrayPool<byte>.Shared.Return(buf); // 停机竞态写不进则归还（审查修复：原漏还）
        }
    }

    // ── 测试缝（InternalsVisibleTo P2P.Client.Tests）───────────────

    internal bool ConvExists(uint conv) => _convs.ContainsKey(conv);

    internal long PeerSegmentsReceived(uint conv)
        => _convs.TryGetValue(conv, out var e) ? (long)e.Peer.SegmentsReceived : -1;

    /// <summary>诊断计数快照（M6-08 生产观察：本地诊断页/0x72 包络统计的数据源）——
    /// 全 conv 算法层计数聚合（单线程写、聚合读容忍良性竞态）。</summary>
    public KcpBearerStatsSnapshot Snapshot()
    {
        ulong segmentsSent = 0, segmentsReceived = 0, retrans = 0, fastRetrans = 0,
              acksSent = 0, acksReceived = 0, windowUpdates = 0;
        foreach (var entry in _convs.Values)
        {
            var p = entry.Peer;
            segmentsSent += p.SegmentsSent;
            segmentsReceived += p.SegmentsReceived;
            retrans += p.Retransmissions;
            fastRetrans += p.FastRetransmissions;
            acksSent += p.AcksSent;
            acksReceived += p.AcksReceived;
            windowUpdates += p.WindowUpdates;
        }
        return new KcpBearerStatsSnapshot(
            ConvCount, UnknownConvDropped, MalformedDropped, NonWhitelistDropped,
            RawQueueDropped, DegradedFrameCount,
            segmentsSent, segmentsReceived, retrans, fastRetrans, acksSent, acksReceived, windowUpdates);
    }
}

/// <summary>KCP 承载诊断快照（M6-08）：承载层计数 + 全 conv 算法层聚合计数。</summary>
public sealed record KcpBearerStatsSnapshot(
    int ConvCount,
    long UnknownConvDropped,
    long MalformedDropped,
    long NonWhitelistDropped,
    long RawQueueDropped,
    long DegradedFrameCount,
    ulong SegmentsSent,
    ulong SegmentsReceived,
    ulong Retransmissions,
    ulong FastRetransmissions,
    ulong AcksSent,
    ulong AcksReceived,
    ulong WindowUpdates);

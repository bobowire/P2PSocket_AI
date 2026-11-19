// M6-01 KCP 协议核（设计 11 §2.3/§5.5，决议④自研移植）。
// 协议数学对照参考实现 1:1 移植（RTO 估计/快重传/nodelay 收紧曲线/拥塞窗/ACK-una 滑动/
// flush 攒包），只换壳不发明数学。算法层约束（设计 11 §2.3）：不认 Socket 类型、无 static
// 全局态、多实例并存（≤257 conv/会话 × 多会话）；非线程安全——调用方（壳层）串行化访问。
//
// MIT attribution（决议④，四参考出处与许可注记）：
// - skywind3000/kcp（https://github.com/skywind3000/kcp，MIT）：协议行为基准真值——段头
//   格式/flush 时序/重传与窗口公式；本文件数学逐函数对照其 ikcp.c 移植（对照时点 2026-11）。
// - kcp2k Kcp.cs（https://github.com/vis2k/kcp2k，MIT）：C# 段队列写法与生产验证过的边界处理。
// - Cysharp/KcpTransport（https://github.com/Cysharp/KcpTransport，MIT）：现代 API 形态
//   （Span 收发、会话端点接口——KcpPeer 公开 API 对标）。
// - limpo1989/kcp-csharp（https://github.com/limpo1989/kcp-csharp，MIT）：zero-GC 技巧
//   （struct 段、flush 攒包缓冲复用——本实现的分配纪律）。
//
// 本项目裁剪定案（设计 11 §2.3/§5.5，2026-11-14 评审批复）：
// ① 只做流模式——PTP 帧自带 u16 长度自分界；frg 恒 0（字节占位保留，wire 布局与参考一致）；
// ② WASK/WINS 窗口探测裁剪——补偿机制见 TryRead 窗口重开通告（_tellWindow）注记；
// ③ dead_link 放弃计数不实现——重传到会话消亡为止、永不主动放弃，活性判定归上层
//   KEEPALIVE 3×20s 断链（高丢包下零挂起前提，设计 11 §2.3）；
// ④ ikcp_wndsize 的 rcv_wnd ≥128 下限不移植（该下限为消息模式分片安全不变量；流模式无此
//   约束，且设计 11 §5.4 参数表定 RcvWnd=64 对齐单 channel 信用窗帧数 48+余量）。
using System.Buffers.Binary;

namespace P2P.Core.Transport.Kcp;

/// <summary>KCP 段（wire 头 24B：conv|cmd|frg|wnd|ts|sn|una|len，小端）＋发送侧簿记。
/// struct 存储（池化纪律：段体入 List 无逐段对象分配，数据缓冲按需分配；flush 攒包缓冲复用）。</summary>
internal struct KcpSegment
{
    public uint Conv;
    public byte Cmd;
    public byte Frg;   // 流模式恒 0（裁剪①；字节保留与参考 wire 布局一致）
    public ushort Wnd;
    public uint Ts;
    public uint Sn;
    public uint Una;
    public uint Len;
    public uint ResendTs;
    public uint Rto;
    public uint Fastack;
    public uint Xmit;
    public byte[] Data = []; // Len 个有效字节

    public KcpSegment() { } // 字段初始值设定项要求显式构造（CS8983）
}

/// <summary>
/// 单 conv KCP 端点（参考 ikcpcb 1:1）。壳层职责：<see cref="Update"/> 周期驱动（重传/超时）、
/// Send 后与 <see cref="Input"/> 后即时 <see cref="Flush"/>（压低首发/ACK 延迟——数学不变，
/// 仅 flush 触发时机，kcp2k 同款壳层形态）；时钟可注入（确定性单测）。
/// </summary>
public sealed class KcpPeer : IKcpDatagramSource
{
    internal const uint RtoNdl = 30;      // nodelay 下限 RTO（参考 IKCP_RTO_NDL）
    internal const uint RtoMin = 100;     // 常规下限 RTO（参考 IKCP_RTO_MIN）
    internal const uint RtoDef = 200;     // 初始 RTO（参考 IKCP_RTO_DEF）
    internal const uint RtoMax = 60000;   // 上限（参考 IKCP_RTO_MAX）
    internal const byte CmdPush = 81;     // 参考 IKCP_CMD_PUSH
    internal const byte CmdAck = 82;      // 参考 IKCP_CMD_ACK
    public const int Overhead = 24;       // 段头长（参考 IKCP_OVERHEAD；壳层包长校验用）
    internal const uint ThreshInit = 2;   // 参考 IKCP_THRESH_INIT/MIN
    internal const uint FastAckLimit = 5; // 参考 IKCP_FASTACK_LIMIT（快重传最大触发次数）

    private readonly IKcpDatagramSink _sink;
    private readonly Func<long> _clock;
    private readonly int _mtu;
    private readonly int _mss;
    private readonly uint _intervalMs;
    private readonly bool _noDelay;
    private readonly int _fastResend;
    private readonly bool _noCwnd;
    private readonly uint _sndWnd;
    private readonly uint _rcvWnd;
    private readonly byte[] _buffer; // flush 攒包缓冲（复用，参考 kcp->buffer）

    // 发送/接收簿记（参考 ikcpcb 字段一一对应；List+头游标替代侵入式双向链表）
    private readonly List<KcpSegment> _sndQueue = [];
    private int _sndQueueHead;
    private readonly List<KcpSegment> _sndBuf = [];
    private readonly List<KcpSegment> _rcvBuf = [];
    private readonly List<KcpSegment> _rcvQueue = [];
    private int _rcvQueueHead;
    private readonly List<(uint Sn, uint Ts)> _ackList = [];

    private uint _sndUna, _sndNxt, _rcvNxt;
    private uint _rto = RtoDef, _rxSrtt, _rxRttval, _rxMinrto = RtoMin;
    private uint _rmtWnd;           // 对端通告接收窗（初始=RcvWnd，参考 kcp->rmt_wnd=IKCP_WND_RCV 同意图）
    private uint _cwnd, _incr;      // 拥塞窗（nc=0 对照组用；nc=1 不参与发送窗）
    private uint _ssthresh = ThreshInit;
    private uint _current;          // uint32 毫秒时钟（回绕安全减法 _itimediff 同参考）
    private bool _updated;
    private uint _tsFlush;
    private bool _tellWindow;       // 窗口重开通告待发（WINS 裁剪补偿，见 TryRead）
    private uint _lastPushTs;       // 最近收到 PUSH 的 ts（窗口通告 ACK 回显，避免对端 RTT 采样失真）

    public uint Conv { get; }

    // ── 诊断计数（M6-08 诊断页可选接入；单线程写、诊断读容忍良性竞态）────────
    public ulong SegmentsSent { get; private set; }
    public ulong SegmentsReceived { get; private set; }
    public ulong Retransmissions { get; private set; }
    public ulong FastRetransmissions { get; private set; }
    public ulong AcksSent { get; private set; }
    public ulong AcksReceived { get; private set; }
    public ulong WindowUpdates { get; private set; }
    public ulong OutOfWindowDrops { get; private set; }
    public ulong DuplicateDrops { get; private set; }

    /// <summary>发送待办（snd_buf+snd_queue 段数，参考 ikcp_waitsnd）——排空=对端全部确认。</summary>
    public int WaitSnd => _sndBuf.Count + (_sndQueue.Count - _sndQueueHead);

    private int RcvQueCount => _rcvQueue.Count - _rcvQueueHead;

    public KcpPeer(uint conv, IKcpDatagramSink sink, KcpPeerOptions? options = null)
    {
        Conv = conv;
        _sink = sink;
        var o = options ?? new KcpPeerOptions();
        if (o.Mtu < 50 || o.Mtu <= Overhead || o.Mtu > 65_506)
            throw new ArgumentOutOfRangeException(nameof(options),
                $"MTU {o.Mtu} 非法（参考 ikcp_setmtu 下限；上界 65506=UDP 数据报上限 65507 含 1B 魔数，审查补界）");
        _mtu = o.Mtu;
        _mss = _mtu - Overhead;
        _intervalMs = (uint)Math.Clamp(o.IntervalMs, 10, 5000); // 参考 ikcp_interval 夹取
        _noDelay = o.NoDelay;
        _fastResend = o.Resend;
        _noCwnd = o.NoCongestionWindow;
        // 审查补界：窗口夹取到 ushort 上限——flush 模板 (ushort)WndUnused() 通告位宽 16bit，超限配置原会静默截断
        _sndWnd = (uint)Math.Clamp(o.SndWnd, 1, ushort.MaxValue);
        _rcvWnd = (uint)Math.Clamp(o.RcvWnd, 1, ushort.MaxValue);
        _rmtWnd = _rcvWnd;
        if (_noDelay) _rxMinrto = RtoNdl; // 参考 ikcp_nodelay：nodelay=1 → rx_minrto=30
        _clock = o.ClockMs ?? (() => Environment.TickCount64);
        _buffer = new byte[_mtu];
    }

    private uint Now => (uint)_clock();

    /// <summary>流式写入（参考 ikcp_send stream 模式）：入 snd_queue 即返（无界入队、永不部分
    /// 写——在途量由上层 WINDOW 信用与 snd_wnd 界定，设计 11 §5.6）；尾段未满追加复用同款。</summary>
    public int Send(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return 0;
        var offset = 0;
        var remaining = data.Length;
        if (_sndQueue.Count > _sndQueueHead)
        {
            // 追加到上一段未满空间（参考 ikcp_send 流模式尾段扩展）
            var idx = _sndQueue.Count - 1;
            var old = _sndQueue[idx];
            if (old.Len < _mss)
            {
                var extend = Math.Min(remaining, _mss - (int)old.Len);
                var grown = new byte[old.Len + extend];
                old.Data.AsSpan(0, (int)old.Len).CopyTo(grown);
                data.Slice(0, extend).CopyTo(grown.AsSpan((int)old.Len));
                old.Data = grown;
                old.Len += (uint)extend;
                _sndQueue[idx] = old;
                offset += extend;
                remaining -= extend;
            }
        }
        while (remaining > 0)
        {
            var take = Math.Min(remaining, _mss);
            _sndQueue.Add(new KcpSegment
            {
                Data = data.Slice(offset, take).ToArray(),
                Len = (uint)take,
                Frg = 0, // 裁剪①：流模式无分片计数
            });
            offset += take;
            remaining -= take;
        }
        return data.Length;
    }

    /// <summary>流式读出一段（参考 ikcp_recv 流模式逐段返回语义；缓冲所有权转移给调用方）。
    /// 读出后驱动 rcv_buf→rcv_queue 搬移；接收窗从满载重开时置窗口通告（WINS 裁剪补偿）。</summary>
    public bool TryRead(out byte[] data)
    {
        data = [];
        if (_rcvQueueHead >= _rcvQueue.Count) return false;
        // recover 判定须在摘除前（参考 ikcp_recv：nrcv_que >= rcv_wnd 即满载过）
        var recover = RcvQueCount >= _rcvWnd;
        data = _rcvQueue[_rcvQueueHead].Data;
        _rcvQueueHead++;
        if (_rcvQueueHead >= 64 && _rcvQueueHead * 2 >= _rcvQueue.Count) CompactRcvQueue();
        MoveRcvBufToQueue();
        // WINS 裁剪补偿（设计 11 §2.3 裁剪②的安全前提）：参考此处置 ASK_TELL→flush 发
        // IKCP_CMD_WINS 告知对端窗口重开。裁剪 WINS 后若不补偿：对端 rmt_wnd=0 且 snd_buf
        // 空（无未确认段可重传触发 ACK 回流）时永久死锁。补偿形态=补发一条 ACK（sn=最后
        // 已收段、ts=最近 PUSH 的 ts 回显——对端 RTT 采样基于自身时钟回显，失真限于一次
        // 满载事件，EWMA 自愈），携带最新 wnd。窗满才触发，频次受 rcv_wnd 限制。
        if (recover && RcvQueCount < _rcvWnd)
            _tellWindow = true;
        return true;
    }

    /// <summary>注入收到的 KCP 包（一段或多段聚合；参考 ikcp_input）。畸形/conv 不符返回
    /// false（已处理的前段副作用保留——参考同款中途返回语义），由调用方丢弃计数。</summary>
    public bool Input(ReadOnlySpan<byte> datagram)
    {
        _current = Now; // 时钟刷新点（参考仅 update 置 current；壳层即时 flush 下刷新避免陈旧 RTT 采样）
        var size = datagram.Length;
        if (size < Overhead) return false;
        var offset = 0;
        var maxack = 0u;
        var latestTs = 0u;
        var flag = false;
        var prevUna = _sndUna;

        while (size - offset >= Overhead)
        {
            var conv = BinaryPrimitives.ReadUInt32LittleEndian(datagram.Slice(offset));
            if (conv != Conv) return false;
            var cmd = datagram[offset + 4];
            var frg = datagram[offset + 5]; // 流模式恒 0，不校验（对端缺陷等价丢弃）
            var wnd = BinaryPrimitives.ReadUInt16LittleEndian(datagram.Slice(offset + 6));
            var ts = BinaryPrimitives.ReadUInt32LittleEndian(datagram.Slice(offset + 8));
            var sn = BinaryPrimitives.ReadUInt32LittleEndian(datagram.Slice(offset + 12));
            var una = BinaryPrimitives.ReadUInt32LittleEndian(datagram.Slice(offset + 16));
            var len = BinaryPrimitives.ReadUInt32LittleEndian(datagram.Slice(offset + 20));
            offset += Overhead;
            if (len > (uint)(size - offset)) return false;
            if (cmd is not (CmdPush or CmdAck))
                return false; // WASK(83)/WINS(84) 裁剪②：非 PUSH/ACK 一律不识别（对端同版本不会发）

            _rmtWnd = wnd;
            ParseUna(una);
            ShrinkBuf();

            if (cmd == CmdAck)
            {
                AcksReceived++;
                if ((int)(_current - ts) >= 0)
                    UpdateAck((int)(_current - ts));
                ParseAck(sn);
                ShrinkBuf();
                if (!flag)
                {
                    flag = true;
                    maxack = sn;
                    latestTs = ts;
                }
                // 参考 IKCP_FASTACK_CONSERVE（master 定义）：仅更新时间戳更新的 ack。
                // 审查注记：master 此处为严格 >（CONSERVE 防跨数据报陈旧 ACK 虚增 maxack）；本实现
                // 取 >=（kcp2k 口径，见文件头出处②）——同毫秒段批是 interval=10 下的常态（发送侧
                // MTU 攒包多段同 ts、接收侧 ACK 同报回涌），严格 > 会令同报内 maxack 不推进、快重传
                // 推迟一个窗口；CONSERVE 的防陈旧意图不受损（陈旧 ACK 到达于不同数据报，其 ts 落后
                // 于本报 latestTs，两口径同样拒绝）。红绿验证：两口径下本套件 14 例均绿（差异仅在
                // 单报多 ACK 场景判别），保留已基准定型的 >=。
                else if ((int)(sn - maxack) > 0 && (int)(ts - latestTs) >= 0)
                {
                    maxack = sn;
                    latestTs = ts;
                }
            }
            else // PUSH
            {
                SegmentsReceived++;
                if ((int)(sn - (_rcvNxt + _rcvWnd)) < 0)
                {
                    _ackList.Add((sn, ts)); // 窗内即回 ACK（乱序/重复同样回——对端快重传依据）
                    _lastPushTs = ts;
                    if ((int)(sn - _rcvNxt) >= 0)
                    {
                        ParseData(new KcpSegment
                        {
                            Conv = conv, Cmd = cmd, Frg = frg, Wnd = wnd, Ts = ts, Sn = sn, Una = una, Len = len,
                            Data = len > 0 ? datagram.Slice(offset, (int)len).ToArray() : [],
                        });
                    }
                }
            }
            offset += (int)len;
        }

        if (flag)
            ParseFastack(maxack, latestTs);
        if ((int)(_sndUna - prevUna) > 0 && !_noCwnd)
            GrowCwndOnAck(); // 拥塞窗增长（参考 ikcp_input 尾段经典路径；nc=1 不用）
        return true;
    }

    /// <summary>周期驱动（参考 ikcp_update：ts_flush 节奏 + ±10s 漂移重同步）。壳层泵每 tick 调。</summary>
    public void Update()
    {
        var current = Now;
        _current = current;
        if (!_updated)
        {
            _updated = true;
            _tsFlush = current;
        }
        var slap = (int)(current - _tsFlush);
        if (slap >= 10000 || slap < -10000)
        {
            _tsFlush = current;
            slap = 0;
        }
        if (slap >= 0)
        {
            _tsFlush += _intervalMs;
            if ((int)(current - _tsFlush) >= 0)
                _tsFlush = current + _intervalMs;
            FlushInternal();
        }
    }

    /// <summary>即时派发（参考 ikcp_flush 直调；壳层 Send/收包后调用压低延迟——不改 ts_flush 节奏，
    /// 同毫秒重复调用幂等无重发副作用：needsend 仅首发/超时/快重传三态成立）。</summary>
    public void Flush()
    {
        _current = Now;
        FlushInternal();
    }

    private void FlushInternal()
    {
        if (!_updated) return; // 参考：未 Update 过不 flush（壳层构造后先 Update 初始化）
        var current = _current;
        var ptr = 0;
        var span = _buffer.AsSpan();
        var change = 0;
        var lost = false;
        var priorCwnd = _cwnd;

        // ACK 模板段（wnd/una 每段携带——对端 rmt_wnd/snd_una 的持续来源）
        var seg = new KcpSegment
        {
            Conv = Conv, Cmd = CmdAck, Frg = 0, Wnd = (ushort)WndUnused(), Una = _rcvNxt, Sn = 0, Ts = 0, Len = 0,
        };

        // ① flush acknowledges（参考逐条 ACK + MTU 攒包输出）
        foreach (var (sn, ts) in _ackList)
        {
            if (ptr + Overhead > _mtu)
            {
                Output(span[..ptr]);
                ptr = 0;
            }
            seg.Sn = sn;
            seg.Ts = ts;
            ptr = EncodeSeg(span, ptr, seg);
            AcksSent++;
        }
        _ackList.Clear();

        // ①′ 窗口重开通告（WINS 裁剪补偿，见 TryRead 注记）
        if (_tellWindow)
        {
            _tellWindow = false;
            if (_rcvNxt > 0)
            {
                if (ptr + Overhead > _mtu)
                {
                    Output(span[..ptr]);
                    ptr = 0;
                }
                seg.Sn = _rcvNxt - 1;
                seg.Ts = _lastPushTs;
                ptr = EncodeSeg(span, ptr, seg);
                WindowUpdates++;
            }
        }

        // ② 发送窗（参考：min(snd_wnd, rmt_wnd)；nc=0 再与 cwnd 取小）
        var cwnd = Math.Min(_sndWnd, _rmtWnd);
        if (!_noCwnd)
            cwnd = Math.Min(_cwnd, cwnd);

        // ③ snd_queue → snd_buf（窗内搬移并赋 sn/首发簿记）
        while ((int)(_sndNxt - (_sndUna + cwnd)) < 0)
        {
            if (_sndQueueHead >= _sndQueue.Count) break;
            var newSeg = _sndQueue[_sndQueueHead];
            _sndQueueHead++;
            newSeg.Conv = Conv;
            newSeg.Cmd = CmdPush;
            newSeg.Wnd = seg.Wnd;
            newSeg.Ts = current;
            newSeg.Sn = _sndNxt++;
            newSeg.Una = _rcvNxt;
            newSeg.ResendTs = current;
            newSeg.Rto = _rto;
            newSeg.Fastack = 0;
            newSeg.Xmit = 0;
            _sndBuf.Add(newSeg);
        }
        CompactSndQueue();

        // ④ 重传判定参数（参考：fastresend=0 关快重传；nodelay 无 rtomin 提前量）
        var resent = _fastResend > 0 ? (uint)_fastResend : uint.MaxValue;
        var rtomin = _noDelay ? 0 : _rto >> 3;

        // ⑤ flush data segments（首发 / RTO 超时退避重传 / 快重传）
        for (var i = 0; i < _sndBuf.Count; i++)
        {
            var segment = _sndBuf[i];
            var needsend = false;
            if (segment.Xmit == 0)
            {
                needsend = true;
                segment.Xmit++;
                segment.Rto = _rto;
                segment.ResendTs = current + segment.Rto + rtomin;
            }
            else if ((int)(current - segment.ResendTs) >= 0)
            {
                needsend = true;
                segment.Xmit++;
                Retransmissions++;
                if (!_noDelay)
                    segment.Rto += Math.Max(segment.Rto, _rto); // 常规：rto += max(rto, rx_rto)
                else
                    segment.Rto += segment.Rto / 2;             // nodelay=1：×1.5（参考 nodelay<2 分支）
                segment.ResendTs = current + segment.Rto;
                lost = true;
            }
            else if (segment.Fastack >= resent)
            {
                if (segment.Xmit <= FastAckLimit || FastAckLimit <= 0)
                {
                    needsend = true;
                    segment.Xmit++;
                    FastRetransmissions++;
                    segment.Fastack = 0;
                    segment.ResendTs = current + segment.Rto;
                    change++;
                }
            }

            if (!needsend)
            {
                _sndBuf[i] = segment;
                continue;
            }
            segment.Ts = current;
            segment.Wnd = seg.Wnd;
            segment.Una = _rcvNxt;
            _sndBuf[i] = segment;
            SegmentsSent++;

            var need = Overhead + (int)segment.Len;
            if (ptr + need > _mtu)
            {
                Output(span[..ptr]);
                ptr = 0;
            }
            ptr = EncodeSeg(span, ptr, segment);
            segment.Data.AsSpan(0, (int)segment.Len).CopyTo(span.Slice(ptr));
            ptr += (int)segment.Len;
            // 裁剪③：参考此处 dead_link 判定（xmit≥20 → state=-1）不移植——永不放弃
        }

        if (ptr > 0)
            Output(span[..ptr]);

        // ⑥ 拥塞窗收缩（参考经典路径，nc=0 对照组用；uint 回绕与 C 同型）
        if (change > 0)
        {
            var inflight = _sndNxt - _sndUna;
            _ssthresh = Math.Max(inflight / 2, ThreshInit);
            _cwnd = _ssthresh + resent;
            _incr = _cwnd * (uint)_mss;
        }
        if (lost)
        {
            _ssthresh = Math.Max(priorCwnd / 2, ThreshInit);
            _cwnd = 1;
            _incr = (uint)_mss;
        }
        if (_cwnd < 1)
        {
            _cwnd = 1;
            _incr = (uint)_mss;
        }
    }

    // ── 内部：参考同名函数 1:1 ─────────────────────────────────────────

    private void UpdateAck(int rtt)
    {
        if (_rxSrtt == 0)
        {
            _rxSrtt = (uint)rtt;
            _rxRttval = (uint)Math.Max(0, rtt / 2);
        }
        else
        {
            var delta = Math.Abs(rtt - (int)_rxSrtt);
            _rxRttval = (3 * _rxRttval + (uint)delta) / 4;
            _rxSrtt = (7 * _rxSrtt + (uint)rtt) / 8;
            if (_rxSrtt < 1) _rxSrtt = 1;
        }
        var rto = _rxSrtt + Math.Max(_intervalMs, 4 * _rxRttval);
        _rto = Math.Clamp(rto, _rxMinrto, RtoMax);
    }

    private void ShrinkBuf() => _sndUna = _sndBuf.Count > 0 ? _sndBuf[0].Sn : _sndNxt;

    private void ParseUna(uint una)
    {
        for (var i = 0; i < _sndBuf.Count;)
        {
            if ((int)(una - _sndBuf[i].Sn) > 0)
                _sndBuf.RemoveAt(i);
            else
                break; // 按 sn 有序，前方无更小者
        }
    }

    private void ParseAck(uint sn)
    {
        if ((int)(sn - _sndUna) < 0 || (int)(sn - _sndNxt) >= 0) return;
        for (var i = 0; i < _sndBuf.Count; i++)
        {
            var seg = _sndBuf[i];
            if (sn == seg.Sn)
            {
                _sndBuf.RemoveAt(i);
                return;
            }
            if ((int)(sn - seg.Sn) < 0) return;
        }
    }

    private void ParseFastack(uint sn, uint ts)
    {
        if ((int)(sn - _sndUna) < 0 || (int)(sn - _sndNxt) >= 0) return;
        for (var i = 0; i < _sndBuf.Count; i++)
        {
            var seg = _sndBuf[i];
            if ((int)(sn - seg.Sn) < 0) break;
            if (sn != seg.Sn && (int)(ts - seg.Ts) >= 0) // FASTACK_CONSERVE（master 语义）
            {
                seg.Fastack++;
                _sndBuf[i] = seg;
            }
        }
    }

    private void ParseData(KcpSegment newSeg)
    {
        var sn = newSeg.Sn;
        if ((int)(sn - (_rcvNxt + _rcvWnd)) >= 0 || (int)(sn - _rcvNxt) < 0)
        {
            OutOfWindowDrops++; // 出窗丢弃（发送侧以对端窗约束，此为异常/攻击包）
            return;
        }
        var repeat = false;
        var insertAt = 0; // 未遇更小 sn → 插头（参考插链头）
        for (var i = _rcvBuf.Count - 1; i >= 0; i--) // 自尾向前（参考 rcv_buf.prev 起遍历）
        {
            var seg = _rcvBuf[i];
            if (seg.Sn == sn)
            {
                repeat = true;
                break;
            }
            if ((int)(sn - seg.Sn) > 0)
            {
                insertAt = i + 1;
                break;
            }
        }
        if (repeat)
            DuplicateDrops++;
        else
            _rcvBuf.Insert(insertAt, newSeg);
        MoveRcvBufToQueue();
    }

    private void MoveRcvBufToQueue()
    {
        while (_rcvBuf.Count > 0)
        {
            var seg = _rcvBuf[0];
            if (seg.Sn == _rcvNxt && RcvQueCount < _rcvWnd)
            {
                _rcvBuf.RemoveAt(0);
                _rcvQueue.Add(seg);
                _rcvNxt++;
            }
            else
                break;
        }
    }

    private void GrowCwndOnAck()
    {
        // 参考 ikcp_input 尾段经典拥塞窗增长（ccops==NULL 路径）
        if (_cwnd >= _rmtWnd) return;
        uint mss = (uint)_mss;
        if (_cwnd < _ssthresh)
        {
            _cwnd++;
            _incr += mss;
        }
        else
        {
            if (_incr < mss) _incr = mss;
            _incr += mss * mss / _incr + mss / 16;
            if ((_cwnd + 1) * mss <= _incr)
                _cwnd = (_incr + mss - 1) / (mss > 0 ? mss : 1);
        }
        if (_cwnd > _rmtWnd)
        {
            _cwnd = _rmtWnd;
            _incr = _rmtWnd * mss;
        }
    }

    private uint WndUnused() => (uint)Math.Max(0, (int)_rcvWnd - RcvQueCount);

    private void Output(ReadOnlySpan<byte> datagram)
    {
        if (datagram.IsEmpty) return;
        _sink.Send(datagram);
    }

    private static int EncodeSeg(Span<byte> dst, int ptr, in KcpSegment seg)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(ptr), seg.Conv);
        dst[ptr + 4] = seg.Cmd;
        dst[ptr + 5] = seg.Frg;
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(ptr + 6), seg.Wnd);
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(ptr + 8), seg.Ts);
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(ptr + 12), seg.Sn);
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(ptr + 16), seg.Una);
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(ptr + 20), seg.Len);
        return ptr + Overhead;
    }

    private void CompactSndQueue()
    {
        if (_sndQueueHead == 0 || _sndQueueHead < 64) return;
        if (_sndQueueHead * 2 >= _sndQueue.Count)
        {
            _sndQueue.RemoveRange(0, _sndQueueHead);
            _sndQueueHead = 0;
        }
    }

    private void CompactRcvQueue()
    {
        if (_rcvQueueHead == 0) return;
        _rcvQueue.RemoveRange(0, _rcvQueueHead);
        _rcvQueueHead = 0;
    }
}

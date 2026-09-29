// M1-25 TunnelSession（02 §4.2/§4.5、05 §4、NET-72、SEC-12）：
// - 双方向密钥（A→B / B→A 各 32B）+ counter 单调（发送侧递增、接收侧滑动窗口防重放）；
// - 接收循环：解帧 → 防重放 → AEAD 解密 → KEEPALIVE/PING 内联 → channel 分发；
// - KEEPALIVE 20s 周期（NET-72 ≤25s）+ 3 次未响应判定断链 → 销毁 → 事件（状态机回 punching 由引擎订阅）；
// - REKEY 轮换（M2-21，SEC-14/02 §4.4）：TTL 定时（仅发起方）+手动触发；REKEY(0x08)/REKEY_ACK(0x0C)
//   承载于现会话密钥 AEAD 内，先排水后切换（旧钥 2s 窗口，NET-75）——存量 channel 不中断；
// - WINDOW 信用背压（M2-21，05 §2.3）：每 channel 64KiB 发送信用（DATA 专用；UDP_DGRAM 丢包容忍不设信用），
//   对端本地消费后回 WINDOW(0x0A)，信用耗尽则调用方 splice 循环阻塞于 SendDataAsync=暂停读本地 socket。
using System.Collections.Concurrent;
using System.Threading.Channels;
using MessagePack;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using P2P.Core.Tunnel;

namespace P2P.Client.Tunnel;

/// <summary>可调参数（默认 20s/3 次；测试注入小间隔）。</summary>
public sealed record TunnelSessionOptions
{
    public TimeSpan KeepaliveInterval { get; init; } = TimeSpan.FromSeconds(20);
    public int KeepaliveMissLimit { get; init; } = 3;
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>THello1 连发次数（打洞 10ms×20，02 §5.1；默认 1=不重发）。</summary>
    public int HandshakeResendCount { get; init; } = 1;
    public TimeSpan HandshakeResendInterval { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <summary>REKEY 周期（02 §4.4 TTL 默认 3600s；仅会话发起方起定时器。≤0=禁用；测试注入缩短）。</summary>
    public TimeSpan RekeyInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>REKEY_ACK 等待超时（超时本轮放弃、下轮重试；不断链——会话健康由 KEEPALIVE 兜底）。</summary>
    public TimeSpan RekeyAckTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>一条已建立的设备对隧道（会话密钥就绪后构造即进入收发循环）。</summary>
public sealed class TunnelSession : IAsyncDisposable
{
    private readonly ITunnelTransport _transport;
    private readonly ITunnelChannelHandler _handler;
    private readonly TunnelSessionOptions _options;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1); // counter 递增与传输写入串行
    private readonly ReplayWindow _replay = new();
    private byte[] _sendKey; // REKEY 轮换交换（_sendGate 内；旧钥置零）
    private readonly PtpReceiveKeyRing _recvRing; // 现钥+旧钥 2s 排水窗（M2-21）
    private readonly EcKeyPair? _staticKey; // REKEY 派生用（宿主所有，会话不释放）；null=REKEY 不可用
    private readonly byte[]? _peerStaticPub; // 对端静态公钥副本（REKEY 派生用）
    private readonly Task _recvLoop;
    private readonly Task _keepaliveLoop;
    private readonly Task? _rekeyLoop; // 仅发起方且密钥上下文齐备
    private readonly List<IDisposable> _owned = [];
    private ulong _sendCounter;
    private int _misses;
    private int _closed;
    private uint _nextChannelId;
    private readonly ConcurrentDictionary<uint, CreditGate> _credits = new(); // 每 channel 发送信用（DATA 专用）
    private TaskCompletionSource<byte[]?>? _pendingRekeyAck; // 进行中轮换的 ACK 交接（接收循环→发起方任务）
    private PtpRekey.Initiator? _pendingRekeyInitiator; // 与 _pendingRekeyAck 成对：接收循环内联 HandleAck 用

    public Guid SessionId { get; }
    public Guid PeerDeviceId { get; }
    public bool IsInitiator { get; }

    /// <summary>承载路径标记（02 §4.5，M2-18）：绑定 <see cref="RelayTransport"/> 即中继路径——
    /// 映射状态机 relay 态判定与隧道复用检查的依据（加密与路径解耦：同一会话类型，帧逻辑不变）。</summary>
    public bool ViaRelay => _transport is RelayTransport;

    /// <summary>已完成的密钥轮换代际（接收侧密钥环；诊断/测试观察）。</summary>
    public int RekeyGeneration => _recvRing.Generation;

    public DateTimeOffset EstablishedAt { get; }
    public ulong SentFrames { get; private set; }
    public ulong ReceivedFrames { get; private set; }
    public ulong ReplayDropped { get; private set; }
    public bool IsClosed => Volatile.Read(ref _closed) == 1;

    /// <summary>断链（KEEPALIVE 3 次未响应/传输故障/AEAD 失败）——TunnelHost 摘表并转发（映射状态机回 punching）。</summary>
    public event Action<TunnelSession, string>? Disconnected;

    /// <summary>诊断日志（宿主接 Serilog）。</summary>
    public event Action<string>? Log;

    private TunnelSession(Guid sessionId, Guid peerDeviceId, bool isInitiator,
        PtpSessionKeys keys, ITunnelTransport transport, ITunnelChannelHandler handler,
        TunnelSessionOptions? options, TimeProvider? time,
        EcKeyPair? staticKey = null, byte[]? peerStaticPub = null)
    {
        SessionId = sessionId;
        PeerDeviceId = peerDeviceId;
        IsInitiator = isInitiator;
        _sendKey = keys.SendKey(isInitiator);
        _recvRing = new PtpReceiveKeyRing(keys.ReceiveKey(isInitiator), time);
        _staticKey = staticKey; // 引用共享：密钥对宿主所有（随 state 落盘重建）
        _peerStaticPub = peerStaticPub?.ToArray();
        _transport = transport;
        _handler = handler;
        _options = options ?? new TunnelSessionOptions();
        _time = time ?? TimeProvider.System;
        EstablishedAt = _time.GetLocalNow();
        // channelId 32bit 随机起点避碰撞（02 §4.3）；跳过 0 与握手保留值
        _nextChannelId = BitConverter.ToUInt32(RandomGenerator.Bytes(4)) | 1u;
        _recvLoop = ReceiveLoopAsync(_cts.Token);
        _keepaliveLoop = KeepaliveLoopAsync(_cts.Token);
        if (isInitiator && _options.RekeyInterval > TimeSpan.Zero && staticKey is not null && peerStaticPub is not null)
            _rekeyLoop = RekeyLoopAsync(_cts.Token); // TTL 定时轮换仅发起方（02 §4.4）
    }

    // ── 握手驱动（M1-26 打洞成功后调用；02 §4.1 时序）──────────────

    /// <summary>发起方握手：发 THello1 → 收 THello2 → 发 TConfirm → 建立会话。
    /// 期间收到的非 THello2 帧丢弃（打洞窗口期可能收到对端探测包）。</summary>
    public static async Task<TunnelSession> ConnectAsync(
        Guid sessionId, Guid peerDeviceId, EcKeyPair staticKey, byte[] peerStaticPub,
        ITunnelTransport transport, ITunnelChannelHandler handler,
        TunnelSessionOptions? options = null, TimeProvider? time = null,
        IReadOnlyList<IDisposable>? keepAlive = null)
    {
        using var initiator = PtpHandshake.StartInitiator(sessionId, staticKey, peerStaticPub);
        var opts = options ?? new TunnelSessionOptions();
        using var timeoutCts = new CancellationTokenSource(opts.HandshakeTimeout);
        await transport.SendAsync(initiator.THello1Wire, timeoutCts.Token);

        // 打洞窗口连发（02 §5.1：NAT 打开期丢包容忍；收到 THello2 即停）
        using var resendCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);
        var resend = ResendHandshakeAsync(transport, initiator.THello1Wire, opts, time, resendCts.Token);
        byte[]? tHello2;
        try
        {
            // 收帧直到 THello2（超时抛 OperationCanceledException 由调用方判打洞失败）
            while (true)
            {
                tHello2 = await transport.ReceiveAsync(timeoutCts.Token)
                    ?? throw new IOException("承载在握手期间关闭");
                if (PtpFrameCodec.ParseHeader(tHello2).Type == PtpFrameType.THello2) break;
            }
        }
        finally
        {
            resendCts.Cancel();
            try { await resend; } catch { /* 尽力而为重发 */ }
        }
        var (tConfirm, keys) = initiator.HandleTHello2(tHello2);
        await transport.SendAsync(tConfirm, timeoutCts.Token);
        return Create(sessionId, peerDeviceId, true, keys, transport, handler, options, time, keepAlive,
            staticKey, peerStaticPub);
    }

    /// <summary>响应方握手：已收到 THello1 → 回 THello2 → 收 TConfirm → 建立会话。</summary>
    public static async Task<TunnelSession> AcceptAsync(
        Guid peerDeviceId, byte[] tHello1Wire, EcKeyPair staticKey, byte[] peerStaticPubA,
        ITunnelTransport transport, ITunnelChannelHandler handler,
        TunnelSessionOptions? options = null, TimeProvider? time = null,
        IReadOnlyList<IDisposable>? keepAlive = null)
    {
        using var responder = PtpHandshake.AcceptTHello1(tHello1Wire, staticKey, peerStaticPubA);
        using var timeoutCts = new CancellationTokenSource((options ?? new TunnelSessionOptions()).HandshakeTimeout);
        await transport.SendAsync(responder.THello2Wire, timeoutCts.Token);

        byte[] confirmWire;
        while (true)
        {
            var confirm = await transport.ReceiveAsync(timeoutCts.Token)
                ?? throw new IOException("承载在握手期间关闭");
            if (PtpFrameCodec.ParseHeader(confirm).Type == PtpFrameType.TConfirm)
            {
                confirmWire = confirm;
                break;
            }
        }
        var keys = responder.VerifyTConfirm(confirmWire);
        return Create(keys.SessionId, peerDeviceId, false, keys, transport, handler, options, time, keepAlive,
            staticKey, peerStaticPubA);
    }

    /// <summary>
    /// 外部驱动握手完成后的建立入口（M2-16 TCP 打洞）：THello1 须在多条候选连接上扇出、
    /// 由首条 THello2/THello1 到达的连接胜出——握手时序在 Puncher 侧驱动（02 §5.2③④），
    /// 此处仅以既得会话密钥启动收发循环（内部同 <see cref="ConnectAsync"/> 收尾）。
    /// 传 staticKey/peerStaticPub 使 REKEY 可用（M2-21；缺省 null=该会话不轮换）。
    /// </summary>
    public static TunnelSession FromEstablishedKeys(Guid sessionId, Guid peerDeviceId, bool isInitiator,
        PtpSessionKeys keys, ITunnelTransport transport, ITunnelChannelHandler handler,
        TunnelSessionOptions? options = null, TimeProvider? time = null,
        IReadOnlyList<IDisposable>? keepAlive = null,
        EcKeyPair? staticKey = null, byte[]? peerStaticPub = null)
        => Create(sessionId, peerDeviceId, isInitiator, keys, transport, handler, options, time, keepAlive,
            staticKey, peerStaticPub);

    /// <summary>按周期重发同一握手帧（打洞连发；首发已由调用方发出，此处补发 N-1 次）。</summary>
    private static async Task ResendHandshakeAsync(ITunnelTransport transport, byte[] wire,
        TunnelSessionOptions options, TimeProvider? time, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(options.HandshakeResendInterval, time ?? TimeProvider.System);
            for (var i = 1; i < options.HandshakeResendCount; i++)
            {
                if (!await timer.WaitForNextTickAsync(ct)) return;
                await transport.SendAsync(wire, ct);
            }
        }
        catch (OperationCanceledException) { /* 收到应答/超时：停止连发 */ }
        catch (Exception) { /* 重发尽力而为，失败不阻断握手等待 */ }
    }

    private static TunnelSession Create(Guid sessionId, Guid peerDeviceId, bool isInitiator,
        PtpSessionKeys keys, ITunnelTransport transport, ITunnelChannelHandler handler,
        TunnelSessionOptions? options, TimeProvider? time, IReadOnlyList<IDisposable>? keepAlive,
        EcKeyPair? staticKey = null, byte[]? peerStaticPub = null)
    {
        var session = new TunnelSession(sessionId, peerDeviceId, isInitiator,
            keys, transport, handler, options, time, staticKey, peerStaticPub);
        if (keepAlive is not null) session._owned.AddRange(keepAlive);
        return session;
    }

    // ── channel 发送 API（M1-27 MappingEngine 使用）─────────────────

    /// <summary>分配新 channelId（32bit 随机起点递增，02 §4.3；两侧独立分配靠随机起点避碰撞）。</summary>
    public uint AllocateChannelId() => _nextChannelId++;

    /// <summary>发送 OPEN（访问侧：请求对端打开目标连接）。</summary>
    public ValueTask SendOpenAsync(uint channelId, OpenPayload open, CancellationToken ct = default)
        => SendChannelFrameAsync(PtpFrameType.Open, channelId,
            MessagePackSerializer.Serialize(open), ct);

    /// <summary>发送 OPEN_OK / OPEN_FAIL（目标侧）。</summary>
    public ValueTask SendOpenResultAsync(uint channelId, OpenResultPayload result, CancellationToken ct = default)
        => SendChannelFrameAsync(PtpFrameType.OpenResult, channelId,
            MessagePackSerializer.Serialize(result), ct);

    /// <summary>发送 DATA（明文入参，组帧时 AEAD 加密；≤16KiB，02 §4.3）。
    /// M2-21 信用背压：每 channel 64KiB 发送信用，不足不部分扣 → 挂起等待对端 WINDOW 回报
    /// （调用方 splice 循环阻塞于此 = 暂停读本地 socket，05 §2.3）。</summary>
    public async ValueTask SendDataAsync(uint channelId, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (data.Length > PtpFrameCodec.MaxDataPayload)
            throw new ArgumentOutOfRangeException(nameof(data),
                $"DATA 载荷 {data.Length}B 超过 {PtpFrameCodec.MaxDataPayload}B（02 §4.3 TCP 承载上限）");
        var gate = _credits.GetOrAdd(channelId, static _ => new CreditGate());
        await gate.AcquireAsync(data.Length, ct).ConfigureAwait(false);
        await SendChannelFrameAsync(PtpFrameType.Data, channelId, data, ct).ConfigureAwait(false);
    }

    /// <summary>消费回报（M2-21，05 §2.3）：本地应用已消费 n 字节 → WINDOW(0x0A) 恢复对端信用。</summary>
    public ValueTask SendWindowCreditAsync(uint channelId, int consumedBytes, CancellationToken ct = default)
        => SendChannelFrameAsync(PtpFrameType.Window, channelId,
            MessagePackSerializer.Serialize(new WindowCreditPayload((uint)consumedBytes)), ct);

    /// <summary>channel 终结时摘除信用账本（幂等；CLOSE 双侧与本地关闭路径均调用）。</summary>
    public void RemoveChannelCredit(uint channelId) => _credits.TryRemove(channelId, out _);

    /// <summary>发送 CLOSE。</summary>
    public ValueTask SendCloseAsync(uint channelId, CancellationToken ct = default)
        => SendChannelFrameAsync(PtpFrameType.Close, channelId, Array.Empty<byte>(), ct);

    /// <summary>发送 UDP_DGRAM（M2-20，05 §2.4）：≤1368B 单帧直发；超出按 1352B 分片为 FRAG 序列
    /// （同一数据报的片共用 DgramId，More=false 为末片；分片阈值与承载类型解耦——TCP 承载同口径，
    /// 02 §6.2"UDP_DGRAM 语义不变"）。调用方串行化同 channel 数据报（保证 DgramId 单调映射到发送序）。</summary>
    public async ValueTask SendUdpDgramAsync(uint channelId, ReadOnlyMemory<byte> datagram, CancellationToken ct = default)
    {
        if (datagram.Length <= PtpFrameCodec.MaxUdpDgramPlain)
        {
            await SendChannelFrameAsync(PtpFrameType.UdpDgram, channelId, datagram, ct).ConfigureAwait(false);
            return;
        }
        var dgramId = (uint)Interlocked.Increment(ref _nextDgramId);
        for (ushort index = 0; ; index++)
        {
            var take = Math.Min(PtpFrameCodec.MaxFragChunk, datagram.Length - (int)index * PtpFrameCodec.MaxFragChunk);
            var more = (int)index * PtpFrameCodec.MaxFragChunk + take < datagram.Length;
            var chunk = datagram.Slice((int)index * PtpFrameCodec.MaxFragChunk, take).ToArray();
            await SendChannelFrameAsync(PtpFrameType.Frag, channelId,
                MessagePackSerializer.Serialize(new FragPayload(dgramId, index, more, chunk)), ct).ConfigureAwait(false);
            if (!more) return;
        }
    }

    private int _nextDgramId;

    /// <summary>诊断 RTT 测量（02 §4.2 0x06；本地 Web 诊断页用）。单飞行。</summary>
    public async Task<TimeSpan> PingAsync(CancellationToken ct = default)
    {
        var payload = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var tcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pingTcs = tcs;
        try
        {
            await SendChannelFrameAsync(PtpFrameType.Ping, 0, payload, ct);
            var rtt = await tcs.Task.WaitAsync(_options.KeepaliveInterval * 3, ct);
            return TimeSpan.FromMilliseconds(rtt);
        }
        catch (TimeoutException)
        {
            throw new IOException("PONG 超时");
        }
    }

    private TaskCompletionSource<long>? _pingTcs;

    private async ValueTask SendChannelFrameAsync(byte type, uint channelId, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        byte[] wire;
        await _sendGate.WaitAsync(ct);
        try
        {
            var counter = ++_sendCounter; // 严格递增，从 1 起（0 保留给握手帧）
            wire = PtpFrameCodec.Seal(type, channelId, counter, payload.Span, _sendKey); // _sendKey 交换同在闸内（REKEY）
            SentFrames++;
        }
        finally { _sendGate.Release(); }
        await _transport.SendAsync(wire, ct);
    }

    // ── 接收循环 ─────────────────────────────────────────────────────

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            byte[]? frame;
            try
            {
                frame = await _transport.ReceiveAsync(ct);
                if (frame is null)
                {
                    Disconnect("transport_closed");
                    return;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log?.Invoke($"接收异常：{e.Message}");
                Disconnect("receive_failed");
                return;
            }

            PtpHeader header;
            byte[] plain;
            try
            {
                header = PtpFrameCodec.ParseHeader(frame);
                if (PtpFrameType.IsHandshake(header.Type)) continue; // 会话后迟到握手帧：忽略（重建期旧包）
                if (!_replay.Accept(header.Counter))
                {
                    ReplayDropped++; // 丢弃+告警（SEC-12），不断链
                    Log?.Invoke($"重放/回退 counter={header.Counter} 已丢弃");
                    continue;
                }
                // 两代接收密钥（REKEY 排水期）依次尝试；均不可解=篡改/错包 → 断链（SEC-12）
                if (!_recvRing.TryOpen(frame, out plain))
                    throw new ProtocolException("两代接收密钥均不可解");
            }
            catch (Exception e) when (e is ProtocolException or System.Security.Cryptography.CryptographicException)
            {
                // AEAD 失败=密钥不一致/帧被篡改（SEC-12）：视为承载被破坏，断链重建
                Disconnect($"frame_invalid: {e.Message}");
                return;
            }
            ReceivedFrames++;

            switch (header.Type)
            {
                case PtpFrameType.Keepalive:
                    Volatile.Write(ref _misses, 0); // 对端存活证明（响应帧或对端主动心跳均算）
                    break;
                case PtpFrameType.Ping:
                    _ = TrySendPongAsync(header.ChannelId, plain);
                    break;
                case PtpFrameType.Pong:
                    if (_pingTcs is { } tcs && plain.Length == 8)
                    {
                        var sent = BitConverter.ToInt64(plain);
                        tcs.TrySetResult(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - sent);
                    }
                    break;
                case PtpFrameType.Open:
                    _handler.OnOpen(this, header.ChannelId, MessagePackSerializer.Deserialize<OpenPayload>(plain));
                    break;
                case PtpFrameType.OpenResult:
                    _handler.OnOpenResult(this, header.ChannelId, MessagePackSerializer.Deserialize<OpenResultPayload>(plain));
                    break;
                case PtpFrameType.Data:
                    _handler.OnData(this, header.ChannelId, plain);
                    break;
                case PtpFrameType.UdpDgram:
                    _handler.OnUdpDgram(this, header.ChannelId, plain);
                    break;
                case PtpFrameType.Frag:
                    HandleFrag(header.ChannelId, plain);
                    break;
                case PtpFrameType.Rekey:
                    if (IsInitiator) Log?.Invoke("REKEY 到达但本端为发起方（不期望），丢弃");
                    else HandleRekeyRequest(plain);
                    break;
                case PtpFrameType.RekeyAck:
                {
                    // 内联完成轮换（发起方）：HandleAck+双钥切换在本循环内原子完成——响应方发 ACK
                    // 即换发送钥，紧跟 ACK 的新代帧到达时本端环已就位（消除"唤醒异步任务再轮换"竞态窗）
                    var initiator = Volatile.Read(ref _pendingRekeyInitiator);
                    var pending = Volatile.Read(ref _pendingRekeyAck);
                    if (initiator is null || pending is null)
                    {
                        Log?.Invoke("REKEY_ACK 无进行中轮换，丢弃");
                        break;
                    }
                    try
                    {
                        await RotateKeysAsync(initiator.HandleAck(plain)).ConfigureAwait(false);
                        Log?.Invoke($"REKEY 完成（发起方，代际 {_recvRing.Generation}，sessionId 不变）");
                        pending.TrySetResult(plain);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        pending.TrySetException(e); // 发起方任务（PerformRekeyAsync）记失败并日志
                    }
                    break;
                }
                case PtpFrameType.Window:
                    if (MessagePackSerializer.Deserialize<WindowCreditPayload>(plain) is { CreditBytes: > 0 } credit
                        && _credits.TryGetValue(header.ChannelId, out var gate))
                        gate.Grant((int)Math.Min(credit.CreditBytes, (uint)int.MaxValue));
                    break;
                case PtpFrameType.Close:
                    _handler.OnClose(this, header.ChannelId);
                    RemoveChannelCredit(header.ChannelId);
                    break;
                default:
                    Log?.Invoke($"未知帧 type 0x{header.Type:X2} 容忍丢弃（02 §7）");
                    break;
            }
        }
    }

    private async Task TrySendPongAsync(uint channelId, byte[] payload)
    {
        try { await SendChannelFrameAsync(PtpFrameType.Pong, channelId, payload, _cts.Token); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log?.Invoke($"PONG 发送失败：{e.Message}");
        }
    }

    // ── UDP_DGRAM 分片重组（M2-20，02 §4.2 FRAG）────────────────────

    /// <summary>in-flight 重组缓冲上限（per channel）：超出丢弃全部未齐片（防慢对端内存放大）。</summary>
    private const int ReasmInFlightLimit = 8;

    /// <summary>未齐分片驻留上限：超时丢弃（对端放弃/丢片兜底）。</summary>
    private static readonly TimeSpan ReasmStaleAge = TimeSpan.FromSeconds(10);

    private sealed class PendingDgram
    {
        public required long FirstSeenMs;
        public Dictionary<ushort, byte[]> Parts = [];
        public int Total; // More=false 到达时 = Index+1（0=总数未知）
    }

    private readonly ConcurrentDictionary<uint /*channelId*/, Dictionary<uint /*dgramId*/, PendingDgram>> _reasm = new();

    /// <summary>FRAG 重组：按 channelId+DgramId 攒片，末片定总数，攒齐合并为完整数据报回调
    /// <see cref="ITunnelChannelHandler.OnUdpDgram"/>（乱序容忍；未齐超时/超限丢弃+日志）。</summary>
    private void HandleFrag(uint channelId, byte[] plain)
    {
        FragPayload frag;
        try { frag = MessagePackSerializer.Deserialize<FragPayload>(plain); }
        catch (MessagePack.MessagePackSerializationException)
        {
            Log?.Invoke($"channel {channelId} FRAG 载荷非法，丢弃");
            return;
        }
        var map = _reasm.GetOrAdd(channelId, _ => []);
        byte[]? assembled = null;
        lock (map)
        {
            foreach (var stale in map.Where(kv =>
                        Environment.TickCount64 - kv.Value.FirstSeenMs > ReasmStaleAge.TotalMilliseconds)
                    .Select(kv => kv.Key).ToList())
                map.Remove(stale); // 未齐超时（对端放弃/丢片）
            if (!map.TryGetValue(frag.DgramId, out var pend))
            {
                if (map.Count >= ReasmInFlightLimit)
                {
                    map.Clear();
                    Log?.Invoke($"channel {channelId} 重组缓冲超 {ReasmInFlightLimit} 条，丢弃未齐分片");
                }
                pend = new PendingDgram { FirstSeenMs = Environment.TickCount64 };
                map[frag.DgramId] = pend;
            }
            if (!frag.More) pend.Total = frag.Index + 1;
            pend.Parts[frag.Index] = frag.Chunk;
            if (pend.Total > 0 && pend.Parts.Count == pend.Total)
            {
                assembled = new byte[pend.Parts.Values.Sum(p => p.Length)];
                var offset = 0;
                for (var i = 0; i < pend.Total; i++)
                {
                    var part = pend.Parts[(ushort)i];
                    part.CopyTo(assembled, offset);
                    offset += part.Length;
                }
                map.Remove(frag.DgramId);
            }
            if (map.Count == 0) _reasm.TryRemove(channelId, out _); // 空表摘除防增长
        }
        if (assembled is not null)
            _handler.OnUdpDgram(this, channelId, assembled);
    }

    // ── WINDOW 信用（M2-21，05 §2.3）────────────────────────────────

    /// <summary>每 channel 发送信用闸：CreditWindow 记账（不足不部分扣 → 挂起）+ 授信唤醒。
    /// 单等待者（每 channel 一条 splice 发送循环）、授信者=接收循环；TCS 交换实现无锁唤醒。</summary>
    private sealed class CreditGate
    {
        private readonly CreditWindow _window = new();
        private TaskCompletionSource _granted = NewSignal(); // 生而 pending：等待者要么扣到信用要么真挂起（无自旋）

        private static TaskCompletionSource NewSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task AcquireAsync(int bytes, CancellationToken ct)
        {
            while (!_window.TryConsume(bytes))
            {
                var signal = Volatile.Read(ref _granted); // 捕获当前信号
                if (_window.TryConsume(bytes)) return;    // 捕获后复查（Grant 落在两检之间的竞态窗口）
                await signal.Task.WaitAsync(ct).ConfigureAwait(false); // 信号仅由 Grant 置位——不足则等下一次授信
            }
        }

        public void Grant(int bytes)
        {
            _window.Grant(bytes);
            // 唤醒旧等待者，换入的新信号保持 pending：等待者醒来重试，仍不足则等下一轮授信（零忙等）
            Interlocked.Exchange(ref _granted, NewSignal()).TrySetResult();
        }
    }

    // ── REKEY 密钥轮换（M2-21，02 §4.4/SEC-14）──────────────────────

    /// <summary>手动触发一次密钥轮换（TTL 之外的诊断入口，M3 设置页接线；响应方调用为空操作）。</summary>
    public Task TriggerRekeyAsync() => IsInitiator ? PerformRekeyAsync(_cts.Token) : Task.CompletedTask;

    private async Task RekeyLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.RekeyInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                await PerformRekeyAsync(ct);
        }
        catch (OperationCanceledException) { /* 会话关闭 */ }
    }

    /// <summary>一次轮换（发起方）：REKEY(0x08) → 等待接收循环内联完成（REKEY_ACK case 内
    /// HandleAck+双钥切换，见上）→ 本任务仅观测结果。失败仅日志（下轮重试），不断链——
    /// 会话健康由 KEEPALIVE 兜底。</summary>
    private async Task PerformRekeyAsync(CancellationToken ct)
    {
        if (_staticKey is null || _peerStaticPub is null) return; // 密钥上下文缺失（构造时已保证，防御）
        var ackTcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _pendingRekeyAck, ackTcs, null) is not null)
            return; // 上一轮未完（手动+定时并发）：跳过
        using var initiator = PtpRekey.Start(SessionId, _staticKey, _peerStaticPub);
        Volatile.Write(ref _pendingRekeyInitiator, initiator);
        try
        {
            await SendChannelFrameAsync(PtpFrameType.Rekey, 0, initiator.RekeyPayload, ct).ConfigureAwait(false);
            await ackTcs.Task.WaitAsync(_options.RekeyAckTimeout, ct).ConfigureAwait(false); // 载荷/派生校验在接收循环内联抛出
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log?.Invoke($"REKEY 失败（下轮重试）：{e.Message}");
        }
        finally
        {
            Volatile.Write(ref _pendingRekeyInitiator, null); // 先摘发起器引用再置空交接（迟到 ACK 丢弃）
            Volatile.Write(ref _pendingRekeyAck, null);
        }
    }

    /// <summary>响应侧：解析 REKEY → 派生新钥即编排轮换（接收环先行，见 <see cref="SendRekeyAckThenRotateAsync"/>）。
    /// 双端旧钥均在各自 2s 排水窗内可解切换期乱序尾帧（NET-75；承载乱序容忍同 KEEPALIVE 量级）。</summary>
    private void HandleRekeyRequest(byte[] plain)
    {
        if (_staticKey is null || _peerStaticPub is null)
        {
            Log?.Invoke("REKEY 到达但本端无静态密钥上下文，丢弃");
            return;
        }
        try
        {
            using var responder = PtpRekey.Accept(plain, SessionId, _staticKey, _peerStaticPub);
            _ = SendRekeyAckThenRotateAsync(responder.AckPayload, responder.NewKeys);
        }
        catch (Exception e) // ProtocolException（载荷非法）等
        {
            Log?.Invoke($"REKEY 载荷处理失败：{e.Message}");
        }
    }

    /// <summary>响应方轮换编排（次序保证接收侧零竞态窗）：①接收环先切换——发起方切换后发出的新代帧
    /// 即刻可解，其切换前的在途旧代帧走 2s 排水窗；②以旧发送钥回 ACK（发起方尚未切换）；
    /// ③发送钥在闸内交换（此后新代帧以新钥封印）。</summary>
    private async Task SendRekeyAckThenRotateAsync(byte[] ackPayload, PtpSessionKeys newKeys)
    {
        try
        {
            _recvRing.Rotate(newKeys.ReceiveKey(IsInitiator));
            await SendChannelFrameAsync(PtpFrameType.RekeyAck, 0, ackPayload, _cts.Token).ConfigureAwait(false);
            await SwapSendKeyAsync(newKeys.SendKey(IsInitiator)).ConfigureAwait(false);
            Log?.Invoke($"REKEY 完成（响应方，代际 {_recvRing.Generation}）");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log?.Invoke($"REKEY ACK 发送失败：{e.Message}"); // 发送失败≈会话将断链，KEEPALIVE 兜底
        }
    }

    /// <summary>发起方内联轮换（接收循环 RekeyAck case 调用）：接收环先行——本循环处理下一帧前
    /// 新代接收钥已就位（响应方发 ACK 即换钥，紧跟的新代帧零竞态窗）；旧代在途帧走排水窗。</summary>
    private async Task RotateKeysAsync(PtpSessionKeys newKeys)
    {
        _recvRing.Rotate(newKeys.ReceiveKey(IsInitiator));
        await SwapSendKeyAsync(newKeys.SendKey(IsInitiator)).ConfigureAwait(false);
    }

    /// <summary>发送钥在 _sendGate 内交换（与 Seal 串行，无半换态帧），旧钥置零。</summary>
    private async Task SwapSendKeyAsync(byte[] newSendKey)
    {
        await _sendGate.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            var old = _sendKey;
            _sendKey = newSendKey;
            CryptoUtil.Zero(old);
        }
        finally { _sendGate.Release(); }
    }

    // ── KEEPALIVE（NET-72：20s 周期；3 次未响应断链）────────────────

    private async Task KeepaliveLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.KeepaliveInterval, _time);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try { await SendChannelFrameAsync(PtpFrameType.Keepalive, 0, Array.Empty<byte>(), ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Disconnect($"keepalive_send_failed: {e.Message}");
                return;
            }
            // 判定时机：发出本周期心跳后检查上一周期是否得到响应
            if (Interlocked.Increment(ref _misses) >= _options.KeepaliveMissLimit)
            {
                Disconnect($"keepalive_timeout: 连续 {_options.KeepaliveMissLimit} 次未响应");
                return;
            }
        }
    }

    // ── 生命周期 ─────────────────────────────────────────────────────

    private void Disconnect(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        Log?.Invoke($"隧道断链（{reason}）");
        try { _cts.Cancel(); } catch { }
        _ = _transport.DisposeAsync();
        Disconnected?.Invoke(this, reason);
    }

    /// <summary>本端主动关闭（空闲回收/宿主停机；正常排水语义，NET-75 中继排水属 M2）。</summary>
    public void Close(string reason) => Disconnect($"local_close: {reason}");

    public async ValueTask DisposeAsync()
    {
        Disconnect("disposed");
        try { await _recvLoop; } catch { /* 取消路径 */ }
        try { await _keepaliveLoop; } catch { /* 取消路径 */ }
        if (_rekeyLoop is not null) { try { await _rekeyLoop; } catch { /* 取消路径 */ } }
        foreach (var d in _owned) d.Dispose();
        CryptoUtil.Zero(_sendKey);
        _recvRing.Dispose(); // 现钥+旧钥置零
        _cts.Dispose();
        _sendGate.Dispose();
    }
}

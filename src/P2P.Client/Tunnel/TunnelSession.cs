// M1-25 TunnelSession（02 §4.2/§4.5、05 §4、NET-72、SEC-12）：
// - 双方向密钥（A→B / B→A 各 32B）+ counter 单调（发送侧递增、接收侧滑动窗口防重放）；
// - 接收循环：解帧 → 防重放 → AEAD 解密 → KEEPALIVE/PING 内联 → channel 分发；
// - KEEPALIVE 20s 周期（NET-72 ≤25s）+ 3 次未响应判定断链 → 销毁 → 事件（状态机回 punching 由引擎订阅）；
// - REKEY 轮换属 SEC-14/FR-C-502 → M2，M1 会话密钥随隧道生命周期存续（任务清单 M1-25 注）。
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
    private readonly byte[] _sendKey;
    private readonly byte[] _recvKey;
    private readonly Task _recvLoop;
    private readonly Task _keepaliveLoop;
    private readonly List<IDisposable> _owned = [];
    private ulong _sendCounter;
    private int _misses;
    private int _closed;
    private uint _nextChannelId;

    public Guid SessionId { get; }
    public Guid PeerDeviceId { get; }
    public bool IsInitiator { get; }

    /// <summary>承载路径标记（02 §4.5，M2-18）：绑定 <see cref="RelayTransport"/> 即中继路径——
    /// 映射状态机 relay 态判定与隧道复用检查的依据（加密与路径解耦：同一会话类型，帧逻辑不变）。</summary>
    public bool ViaRelay => _transport is RelayTransport;

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
        TunnelSessionOptions? options, TimeProvider? time)
    {
        SessionId = sessionId;
        PeerDeviceId = peerDeviceId;
        IsInitiator = isInitiator;
        _sendKey = keys.SendKey(isInitiator);
        _recvKey = keys.ReceiveKey(isInitiator);
        _transport = transport;
        _handler = handler;
        _options = options ?? new TunnelSessionOptions();
        _time = time ?? TimeProvider.System;
        EstablishedAt = _time.GetLocalNow();
        // channelId 32bit 随机起点避碰撞（02 §4.3）；跳过 0 与握手保留值
        _nextChannelId = BitConverter.ToUInt32(RandomGenerator.Bytes(4)) | 1u;
        _recvLoop = ReceiveLoopAsync(_cts.Token);
        _keepaliveLoop = KeepaliveLoopAsync(_cts.Token);
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
        return Create(sessionId, peerDeviceId, true, keys, transport, handler, options, time, keepAlive);
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
        return Create(keys.SessionId, peerDeviceId, false, keys, transport, handler, options, time, keepAlive);
    }

    /// <summary>
    /// 外部驱动握手完成后的建立入口（M2-16 TCP 打洞）：THello1 须在多条候选连接上扇出、
    /// 由首条 THello2/THello1 到达的连接胜出——握手时序在 Puncher 侧驱动（02 §5.2③④），
    /// 此处仅以既得会话密钥启动收发循环（内部同 <see cref="ConnectAsync"/> 收尾）。
    /// </summary>
    public static TunnelSession FromEstablishedKeys(Guid sessionId, Guid peerDeviceId, bool isInitiator,
        PtpSessionKeys keys, ITunnelTransport transport, ITunnelChannelHandler handler,
        TunnelSessionOptions? options = null, TimeProvider? time = null,
        IReadOnlyList<IDisposable>? keepAlive = null)
        => Create(sessionId, peerDeviceId, isInitiator, keys, transport, handler, options, time, keepAlive);

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
        TunnelSessionOptions? options, TimeProvider? time, IReadOnlyList<IDisposable>? keepAlive)
    {
        var session = new TunnelSession(sessionId, peerDeviceId, isInitiator,
            keys, transport, handler, options, time);
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

    /// <summary>发送 DATA（明文入参，组帧时 AEAD 加密；≤16KiB，02 §4.3）。</summary>
    public ValueTask SendDataAsync(uint channelId, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (data.Length > PtpFrameCodec.MaxDataPayload)
            throw new ArgumentOutOfRangeException(nameof(data),
                $"DATA 载荷 {data.Length}B 超过 {PtpFrameCodec.MaxDataPayload}B（02 §4.3 TCP 承载上限）");
        return SendChannelFrameAsync(PtpFrameType.Data, channelId, data, ct);
    }

    /// <summary>发送 CLOSE。</summary>
    public ValueTask SendCloseAsync(uint channelId, CancellationToken ct = default)
        => SendChannelFrameAsync(PtpFrameType.Close, channelId, Array.Empty<byte>(), ct);

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
            wire = PtpFrameCodec.Seal(type, channelId, counter, payload.Span, _sendKey);
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
                plain = PtpFrameCodec.Open(frame, _recvKey);
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
                case PtpFrameType.Close:
                    _handler.OnClose(this, header.ChannelId);
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
        foreach (var d in _owned) d.Dispose();
        CryptoUtil.Zero(_sendKey);
        CryptoUtil.Zero(_recvKey);
        _cts.Dispose();
        _sendGate.Dispose();
    }
}

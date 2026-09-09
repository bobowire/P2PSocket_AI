// M1-23 控制通道客户端（02 §2.3、01 §3.2、OQ-12）：
// - 连接握手：Hello（不校 ts）→ HelloAck{serverTs} RTT/2 补偿校准 offset → Proof（HMAC(deviceSecret,
//   nonceC|nonceS)）→ ProofAck（ok 时服务端已启用签名，帧体为 [msgpack][hmac]）→ connMacKey 启用签名；
// - 0x30 心跳：30s 周期（02 §2.4），Ack.serverTs 持续重校 offset 跟踪漂移（OQ-12）；
// - 5005 TIME_SKEW：Established 后签名消息 ts 超窗，服务端附 serverTs；客户端重校准并换新 seq/ts
//   重试一次（02 §2.3；服务端先记 seq 再校 ts，同 seq 重发会判 seq_regression 断连，故必须重建）；
// - 断线重连：指数退避 1s→30s（08 §5.2 reconnect），serverAddrs[] 顺序尝试（FR-C-604）；
// - 能力模式：镜像服务端会话语义（02 §2.5）——新会话初始 Normal，登出降 Passive，登录失败不降级；
//   passive 下主动类消息本地拒绝（省一次往返，服务端同样会拒）；
// - 未注册设备：握手停在 NeedRegister 待 0x10（M1-24 向导后端），CompleteRegistration 转建立。
using System.IO.Pipelines;
using System.Net.Sockets;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using P2P.Core.Utils;

namespace P2P.Client.Control;

public enum ControlClientState
{
    Idle,
    Connecting,
    Handshaking,
    NeedRegister,   // 服务端要求注册（首启向导入口）
    Established,
    BackoffWait,    // 断线退避等待
    Stopped,
}

/// <summary>可调参数（默认值依据 02 §2.4 与 08 §5.2；测试可缩小心跳间隔）。</summary>
public sealed record ControlClientOptions
{
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10); // M1-26 打洞 Ack 延后，届时调大
}

public sealed class ControlClientException(string message) : Exception(message);

/// <summary>服务端 0x7E Error 应答（请求方 await 时收到）。</summary>
public sealed class ControlErrorException(int code, string httpLikeMsg)
    : Exception($"服务端错误 {code}：{httpLikeMsg}")
{
    public int Code { get; } = code;
    public string HttpLikeMsg { get; } = httpLikeMsg;
}

/// <summary>passive 模式下发送主动类消息被本地拒绝（02 §2.5；服务端同样会拒）。</summary>
public sealed class PassiveModeException(string message) : Exception(message);

public sealed class ControlClient : IAsyncDisposable
{
    private readonly ControlClientOptions _options;
    private readonly BackoffPolicy _backoff;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);   // PipeWriter 单写者（心跳与请求并发）
    private readonly SemaphoreSlim _requestGate = new(1, 1); // 单飞行请求（单连接串行，02 §2.1）
    private readonly Task _runLoop;

    private TcpClient? _tcp;
    private Stream? _stream;
    private PipeReader? _reader;
    private PipeWriter? _writer;
    private byte[] _connMacKey = [];
    private byte[] _deviceSecret = [];
    private byte[] _nonceC = [];
    private byte[] _nonceS = [];
    private uint _lastPeerSeq;      // 服务端方向 seq 严格递增（防重放，镜像服务端校验）
    private int _selfSeq;           // 出站 seq 原子递增
    private bool _hmacEnabled;
    private long _lastHeartbeatSentAt;
    private PendingAck? _pending;
    private int _state = (int)ControlClientState.Idle;
    private CapabilityMode _capability = CapabilityMode.Normal;
    private bool _stopped;
    private IReadOnlyList<(string Host, int Port)> _serverAddrs; // 可换（向导选定地址，M1-29）
    private CancellationTokenSource? _backoffWake; // 退避等待可被换址唤醒（M1-30：向导选定地址即重连，不等满退避期）

    /// <summary>状态变迁通知（UI/宿主展示连接性）。</summary>
    public event Action<ControlClientState>? StateChanged;

    /// <summary>服务器推送（无对应 pending 请求的消息：0x71 PunchInvite 等，M1-24+ 挂载处理）。</summary>
    public event Action<IPcpMessage>? ServerPush;

    /// <summary>能力模式变迁（登录/登出/重连驱动，02 §2.5）。</summary>
    public event Action<CapabilityMode>? CapabilityChanged;

    /// <summary>诊断日志（宿主 M1-30 接 Serilog）。</summary>
    public event Action<string>? Log;

    public ControlClientState State => (ControlClientState)Volatile.Read(ref _state);
    public bool IsReady => State is ControlClientState.Established or ControlClientState.NeedRegister;
    public CapabilityMode Capability => _capability;
    public Guid? DeviceId { get; private set; }

    /// <summary>时钟对齐（OQ-12）：offset 供 timestampMs 与 STUN DEVICE-AUTH 共用（M1-26）。</summary>
    public ClockSync Clock { get; }

    public ControlClient(IEnumerable<string> serverAddrs, ControlClientOptions? options = null,
        BackoffPolicy? backoff = null, TimeProvider? time = null, ClockSync? clock = null,
        Guid? deviceId = null, byte[]? deviceSecret = null)
    {
        var addrs = serverAddrs.Select(ParseHostPort).ToList();
        if (addrs.Count == 0)
            throw new ArgumentException("serverAddrs 不能为空（08 §5.2）", nameof(serverAddrs));
        _serverAddrs = addrs;
        _options = options ?? new ControlClientOptions();
        _backoff = backoff ?? BackoffPolicy.ReconnectDefault;
        _time = time ?? TimeProvider.System;
        Clock = clock ?? new ClockSync();
        DeviceId = deviceId;
        _deviceSecret = deviceSecret ?? [];
        _runLoop = RunLoopAsync(_cts.Token);
    }

    // ── 请求发送 ─────────────────────────────────────────────────────

    /// <summary>发送请求并等待 Ack（族类型配对：请求与 Ack 共用 msgType，Register 族 0x10→0x11 例外）。
    /// 5005 TIME_SKEW 自动重校准并换新 seq/ts 重试一次（02 §2.3）。</summary>
    public async Task<TAck> SendRequestAsync<TAck>(IPcpMessage request, CancellationToken ct = default)
        where TAck : class, IPcpMessage
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfPassiveBlocked(request.MsgType);
        if (!IsReady)
            throw new ControlClientException($"控制连接未就绪（{State}），无法发送 0x{request.MsgType:X2}");

        await _requestGate.WaitAsync(ct);
        try
        {
            var pending = new PendingAck(request);
            _pending = pending;
            try
            {
                await SendWireAsync(EncodeOutbound(request), ct);
                return await AwaitAckAsync<TAck>(pending, ct);
            }
            finally { _pending = null; }
        }
        finally { _requestGate.Release(); }
    }

    /// <summary>单向发送（0x76 PunchEndpoint 等无 Ack 上报，M1-26）。</summary>
    public async Task SendAsync(IPcpMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfPassiveBlocked(message.MsgType);
        if (!IsReady)
            throw new ControlClientException($"控制连接未就绪（{State}），无法发送 0x{message.MsgType:X2}");
        await SendWireAsync(EncodeOutbound(message), ct);
    }

    /// <summary>出站 seq（心跳与请求并发，原子递增）。</summary>
    public uint NextSeq() => (uint)Interlocked.Increment(ref _selfSeq);

    /// <summary>校准后时间戳（OQ-12：全部出站消息 timestampMs 用此时钟）。</summary>
    public ulong TimestampMs() => Clock.NowRemoteMs(_time);

    /// <summary>注册完成（M1-24 向导后端在收到未签名 RegisterAck 并解出 deviceSecret 后调用；
    /// 镜像服务端 ControlSession.CompleteRegistration 的密钥派生与状态迁移）。</summary>
    public void CompleteRegistration(Guid deviceId, byte[] deviceSecret)
    {
        ArgumentNullException.ThrowIfNull(deviceSecret);
        DeviceId = deviceId;
        _deviceSecret = (byte[])deviceSecret.Clone();
        _connMacKey = Hkdf.Derive(_deviceSecret, NonceInput(), "pcp-mac"u8, 32);
        _hmacEnabled = true;
        SetCapability(CapabilityMode.Normal); // 镜像服务端：新建立会话初始 Normal（02 §2.5）
        SetState(ControlClientState.Established);
    }

    /// <summary>凭据更新（解绑/覆盖式恢复后重连前；OQ-14）。</summary>
    public void UpdateCredentials(Guid? deviceId, byte[]? deviceSecret)
    {
        DeviceId = deviceId;
        _deviceSecret = (byte[]?)deviceSecret?.Clone() ?? [];
    }

    /// <summary>当前生效地址表（host:port；向导换址判断/诊断展示用）。</summary>
    public IReadOnlyList<string> ServerAddrs => _serverAddrs.Select(a => $"{a.Host}:{a.Port}").ToList();

    /// <summary>更换服务端地址表（向导第一步选定后即时生效，04 §2.2 / 08 §5.2）：
    /// 断开当前连接，主循环下一轮退避后即用新地址重连。已建立的请求会以"连接已断开"失败。</summary>
    public void UpdateServerAddrs(IEnumerable<string> serverAddrs)
    {
        var addrs = serverAddrs.Select(ParseHostPort).ToList();
        if (addrs.Count == 0)
            throw new ArgumentException("serverAddrs 不能为空（08 §5.2）", nameof(serverAddrs));
        _serverAddrs = addrs;
        try { Volatile.Read(ref _backoffWake)?.Cancel(); } // 唤醒退避等待（换了地址就不必等满旧退避期）
        catch (ObjectDisposedException) { /* 唤醒窗口竞态：等待已自然结束 */ }
        try { _tcp?.Dispose(); } catch { /* 已关 */ } // 杀当前连接 → 读循环退出 → 下一轮新表
    }

    // ── 连接生命周期 ─────────────────────────────────────────────────

    /// <summary>等待首个就绪（已注册设备 = Established；未注册 = NeedRegister）。</summary>
    public async Task WaitReadyAsync(CancellationToken ct = default)
    {
        while (!IsReady && State != ControlClientState.Stopped)
            await Task.Delay(50, ct);
        if (!IsReady)
            throw new ControlClientException($"控制连接未就绪：{State}");
    }

    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;
        _cts.Cancel();
        try { await _runLoop; } catch { /* 取消路径 */ }
        await CleanupConnectionAsync();
        SetState(ControlClientState.Stopped);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Dispose();
        _sendGate.Dispose();
        _requestGate.Dispose();
    }

    // ── 主循环：连接 → 会话 → 断线退避重连 ────────────────────────────

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            var connected = false;
            foreach (var (host, port) in _serverAddrs) // FR-C-604 依次尝试
            {
                if (ct.IsCancellationRequested) break;
                SetState(ControlClientState.Connecting);
                try
                {
                    await ConnectAndHandshakeAsync(host, port, ct);
                    connected = true;
                    attempt = 0; // 任一地址成功即清零退避
                    break;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Log?.Invoke($"连接 {host}:{port} 失败：{e.Message}");
                    await CleanupConnectionAsync();
                }
            }

            if (connected && !ct.IsCancellationRequested)
            {
                try { await RunSessionAsync(ct); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Log?.Invoke($"会话中断：{e.Message}");
                }
                attempt++;
                await CleanupConnectionAsync();
            }

            if (ct.IsCancellationRequested) break;
            var delay = _backoff.ComputeDelay(attempt); // 1s→30s 指数退避（08 §5.2）
            SetState(ControlClientState.BackoffWait);
            Log?.Invoke($"退避 {delay.TotalSeconds:0.#}s 后重连");
            using (var wake = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                Volatile.Write(ref _backoffWake, wake);
                try { await Task.Delay(delay, wake.Token); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (OperationCanceledException) { /* 换址唤醒：立即用新表重连 */ }
                finally { Volatile.Write(ref _backoffWake, null); }
            }
        }
    }

    private async Task ConnectAndHandshakeAsync(string host, int port, CancellationToken ct)
    {
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(_options.ConnectTimeout);
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(host, port, connectCts.Token);
        _tcp.NoDelay = true; // 02 §2.1
        _stream = _tcp.GetStream();
        _reader = PipeReader.Create(_stream);
        _writer = PipeWriter.Create(_stream);
        _lastPeerSeq = 0;
        _hmacEnabled = false;
        SetState(ControlClientState.Handshaking);

        // Hello：ts 不校（OQ-12：尚无共享时间基准）；deviceId 仅注册过设备携带
        _nonceC = RandomGenerator.Bytes(16);
        var sentAt = LocalNowMs;
        await SendWireAsync(PcpCodec.Encode(new Hello(NextSeq(), (ulong)sentAt, MsgType.Hello,
            ProtocolVersion.Current, DeviceId, _nonceC)), ct);

        var helloAck = PcpCodec.Decode<HelloAck>(await ReadHandshakeFrameAsync(ct));
        // 时钟对齐（OQ-12）：RTT/2 补偿。此后全部出站 ts 用校准后时钟
        Clock.Calibrate(helloAck.ServerTs, sentAt, LocalNowMs);
        _nonceS = helloAck.NonceS;

        switch (helloAck.Status)
        {
            case HelloStatus.VersionNotSupported:
                throw new ControlClientException(
                    $"服务端协议版本 {helloAck.ProtocolVersion} 不支持本机 {ProtocolVersion.Current}（升级引导 FR-C-904 属 M2）");
            case HelloStatus.NeedRegister:
                SetState(ControlClientState.NeedRegister); // M1-24 向导在此发 0x10（未签名）
                return;
        }

        // Proof：HMAC(deviceSecret, nonceC|nonceS)（02 §2.3；服务端握手期不校 ts）
        await SendWireAsync(PcpCodec.Encode(new Proof(NextSeq(), TimestampMs(), MsgType.Proof,
            Mac.HmacSha256(_deviceSecret, NonceInput()))), ct);

        // ProofAck(ok) 时服务端已启用签名（先派生 connMacKey 再发送）；ProofAck(false)/Error 为未签名。
        // 客户端可自行派生同一 connMacKey，先按签名帧解，失败回退未签名。
        var candidateKey = Hkdf.Derive(_deviceSecret, NonceInput(), "pcp-mac"u8, 32);
        var proofFrame = await ReadHandshakeFrameAsync(ct);
        byte[] proofBody;
        try { proofBody = PcpCodec.DecodeSigned(proofFrame, candidateKey); }
        catch (ProtocolException) { proofBody = proofFrame; }
        var proofHeader = PcpCodec.Peek(proofBody);
        if (proofHeader.MsgType == MsgType.Error)
            throw ControlErrorExceptionFrom(PcpCodec.Decode<ErrorMessage>(proofBody));
        var proofAck = PcpCodec.Decode<ProofAck>(proofBody);
        if (!proofAck.Ok)
            throw new ControlClientException("身份证明被拒：deviceSecret 已失效或设备被禁用（请重新注册）");
        _connMacKey = candidateKey;
        _hmacEnabled = true;
        SetCapability(CapabilityMode.Normal); // 镜像服务端：新会话初始 Normal（02 §2.5）
        SetState(ControlClientState.Established);
    }

    private async Task RunSessionAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = State == ControlClientState.Established
            ? HeartbeatLoopAsync(linked.Token)
            : Task.CompletedTask;
        try { await ReadLoopAsync(linked.Token); }
        finally
        {
            await linked.CancelAsync();
            try { await heartbeat; } catch { /* 取消路径 */ }
        }
    }

    // ── 读循环与会话内消息处理 ────────────────────────────────────────

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        while (true)
        {
            byte[]? frame;
            try
            {
                frame = await FrameCodec.ReadFrameAsync(_reader!, ct);
                if (frame is null) break; // 服务端正常关闭
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log?.Invoke($"读取中断：{e.Message}");
                break;
            }

            try { HandleFrame(frame); }
            catch (Exception e) when (e is ProtocolException or MessagePack.MessagePackSerializationException)
            {
                // HMAC/seq/ts 校验失败 → 断连重连（镜像服务端 07 §4；重连即重新握手重新校准，OQ-12）
                Log?.Invoke($"协议校验失败，断连重连：{e.Message}");
                break;
            }
        }
        FailPending(new ControlClientException("连接已断开"));
    }

    private void HandleFrame(byte[] frame)
    {
        var msgpack = _hmacEnabled ? PcpCodec.DecodeSigned(frame, _connMacKey) : frame;
        var header = PcpCodec.Peek(msgpack);
        if (_hmacEnabled)
        {
            // 入站校验：HMAC（SEC-22）+ seq 严格递增（防重放）。ts 窗口不校验服务端方向——
            // 02 §2.2 的 ±120s 窗口是服务端对客户端消息的校验（超窗回 5005 附 serverTs）；
            // 客户端本机时钟漂移期间入站帧 ts 必然"超窗"，若本地强校验会吞掉 5005 重校准自愈路径（OQ-12）。
            if (header.Seq <= _lastPeerSeq)
                throw new ProtocolException($"服务端 seq 回退（{_lastPeerSeq} → {header.Seq}）");
            _lastPeerSeq = header.Seq;
        }

        switch (header.MsgType)
        {
            case MsgType.Heartbeat: // 同族：Heartbeat/HeartbeatAck 均为 0x30（02 §2.4）
                var hbAck = PcpCodec.Decode<HeartbeatAck>(msgpack);
                // 周期重校准（OQ-12）：以心跳发出时刻为 RTT 起点
                Clock.Calibrate(hbAck.ServerTs, Volatile.Read(ref _lastHeartbeatSentAt), LocalNowMs);
                return;
            case MsgType.Error:
                HandleError(PcpCodec.Decode<ErrorMessage>(msgpack));
                return;
            case MsgType.UserLogin:
                var loginAck = PcpCodec.Decode<UserLoginAck>(msgpack);
                // 登录成功才切模式；失败时会话能力不变（服务端不降级，Ack 中 Passive 仅为提示）
                if (loginAck.Ok) SetCapability(loginAck.Mode);
                DeliverAckOrPush(loginAck);
                return;
            case MsgType.UserLogout:
                var logoutAck = PcpCodec.Decode<UserLogoutAck>(msgpack);
                if (logoutAck.Ok) SetCapability(CapabilityMode.Passive); // 登出降级（FR-C-603）
                DeliverAckOrPush(logoutAck);
                return;
            default:
                DeliverAckOrPush(DecodeKnown(msgpack, header.MsgType));
                return;
        }
    }

    /// <summary>已知类型解码；未知 msgType 容忍降级为仅头三字段（02 §7 加法演进）。</summary>
    private static IPcpMessage DecodeKnown(byte[] msgpack, byte msgType) => msgType switch
    {
        MsgType.RegisterAck => PcpCodec.Decode<RegisterAck>(msgpack),
        MsgType.DeviceUpdate => PcpCodec.Decode<DeviceUpdateAck>(msgpack),
        MsgType.UserRegister => PcpCodec.Decode<UserRegisterAck>(msgpack),
        MsgType.DeviceList => PcpCodec.Decode<DeviceListResponse>(msgpack),
        MsgType.GroupCreate => PcpCodec.Decode<GroupCreateAck>(msgpack),
        MsgType.GroupUpdate => PcpCodec.Decode<GroupUpdateAck>(msgpack),
        MsgType.GroupDissolve => PcpCodec.Decode<GroupDissolveAck>(msgpack),
        MsgType.MappingUpsert => PcpCodec.Decode<MappingUpsertAck>(msgpack),
        MsgType.MappingDelete => PcpCodec.Decode<MappingDeleteAck>(msgpack),
        MsgType.PunchRequest => PcpCodec.Decode<PunchRequestAck>(msgpack),
        MsgType.PunchInvite => PcpCodec.Decode<PunchInvite>(msgpack), // S→C 推送（M1-26 挂载处理）
        _ => PcpCodec.DecodeLoose(msgpack),
    };

    private void DeliverAckOrPush(IPcpMessage message)
    {
        var pending = _pending;
        if (pending is not null && AckTypeFor(pending.RequestType) == message.MsgType)
            pending.TryComplete(message);
        else
            ServerPush?.Invoke(message);
    }

    /// <summary>请求 → Ack 的族类型配对：除 Register 族（0x10→0x11）外，请求与 Ack 共用 msgType。
    /// 服务端 Ack 用自身 NextSeq() 不回显请求 seq，故按族类型配对（02 §2.4 消息表）。</summary>
    internal static byte AckTypeFor(byte requestType)
        => requestType == MsgType.Register ? MsgType.RegisterAck : requestType;

    private void HandleError(ErrorMessage err)
    {
        if (err.Code == ErrorCode.TimeSkew)
        {
            // 5005 重校准 + 重试一次（02 §2.3；仅一次防风暴）
            var pending = _pending;
            if (pending is not null && !pending.Retried && TryParseServerTs(err.HttpLikeMsg, out var serverTs))
            {
                Clock.Calibrate(serverTs, LocalNowMs, LocalNowMs); // RTT 未知按 0（保守）
                pending.Retried = true;
                _ = ResendPendingAsync(pending); // 新 seq/ts 重建（服务端已记旧 seq，同 seq 重发会判回退断连）
                return;
            }
            if (TryParseServerTs(err.HttpLikeMsg, out var bareTs))
                Clock.Calibrate(bareTs, LocalNowMs, LocalNowMs); // 无 pending 请求也重校准（后续消息即恢复）
        }
        _pending?.TryFail(ControlErrorExceptionFrom(err));
    }

    private static ControlErrorException ControlErrorExceptionFrom(ErrorMessage err)
        => new(err.Code, err.HttpLikeMsg);

    private async Task ResendPendingAsync(PendingAck pending)
    {
        try
        {
            var retry = MessageRebuilder.WithHeader(pending.Request, NextSeq(), TimestampMs());
            await SendWireAsync(EncodeOutbound(retry), _cts.Token);
        }
        catch (Exception e)
        {
            pending.TryFail(new ControlClientException($"重试发送失败：{e.Message}"));
        }
    }

    private async Task<byte[]> ReadHandshakeFrameAsync(CancellationToken ct)
        => await FrameCodec.ReadFrameAsync(_reader!, ct)
           ?? throw new ControlClientException("连接在握手期间被服务端关闭");

    // ── 心跳（0x30，02 §2.4：30s 周期；Ack 重校 offset）──────────────

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.HeartbeatInterval, _time);
        while (await timer.WaitForNextTickAsync(ct))
        {
            Volatile.Write(ref _lastHeartbeatSentAt, LocalNowMs);
            try { await SendWireAsync(PcpCodec.EncodeSigned(
                new Heartbeat(NextSeq(), TimestampMs(), MsgType.Heartbeat), _connMacKey), ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log?.Invoke($"心跳发送失败：{e.Message}");
                return; // 读循环将随断流退出，触发重连
            }
        }
    }

    // ── 出站（单写者门卫：心跳/请求/重试并发）─────────────────────────

    private byte[] EncodeOutbound(IPcpMessage message)
        => _hmacEnabled ? PcpCodec.EncodeSignedObject(message, _connMacKey) : PcpCodec.EncodeObject(message);

    private async Task SendWireAsync(byte[] wire, CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct);
        try
        {
            var writer = _writer ?? throw new ControlClientException("连接未建立");
            await FrameCodec.WriteFrameAsync(writer, wire, ct);
        }
        finally { _sendGate.Release(); }
    }

    private async Task<TAck> AwaitAckAsync<TAck>(PendingAck pending, CancellationToken ct) where TAck : class, IPcpMessage
    {
        try
        {
            var message = await pending.Task.WaitAsync(_options.RequestTimeout, ct);
            return (TAck)message;
        }
        catch (TimeoutException)
        {
            throw new ControlClientException($"等待 0x{pending.RequestType:X2} Ack 超时（{_options.RequestTimeout.TotalSeconds:0.#}s）");
        }
    }

    // ── 状态与能力 ───────────────────────────────────────────────────

    private void SetState(ControlClientState state)
    {
        Interlocked.Exchange(ref _state, (int)state);
        StateChanged?.Invoke(state);
    }

    private void SetCapability(CapabilityMode mode)
    {
        if (_capability == mode) return;
        _capability = mode;
        CapabilityChanged?.Invoke(mode);
    }

    private void ThrowIfPassiveBlocked(byte msgType)
    {
        if (_capability == CapabilityMode.Passive && IsActiveClass(msgType))
            throw new PassiveModeException(
                $"passive 模式禁止主动类消息 0x{msgType:X2}（02 §2.5；重新登录须重连后再发）");
    }

    /// <summary>02 §2.5 主动类清单（镜像服务端 ControlMessageRouter.IsActiveClass）。</summary>
    internal static bool IsActiveClass(byte msgType) => msgType is
        MsgType.UserRegister or MsgType.UserLogin or MsgType.UserChangePassword
        or MsgType.DeviceList
        or MsgType.GroupCreate or MsgType.GroupJoin or MsgType.GroupLeave
        or MsgType.JoinRequests or MsgType.GroupInviteGen or MsgType.GroupUpdate
        or MsgType.GroupDissolve or MsgType.GroupRemoveMember
        or MsgType.MappingUpsert or MsgType.MappingDelete
        or MsgType.PunchRequest;

    private void FailPending(Exception error) => _pending?.TryFail(error);

    private byte[] NonceInput()
    {
        var input = new byte[_nonceC.Length + _nonceS.Length];
        _nonceC.AsSpan().CopyTo(input);
        _nonceS.AsSpan().CopyTo(input.AsSpan(_nonceC.Length));
        return input;
    }

    private long LocalNowMs => _time.GetLocalNow().ToUnixTimeMilliseconds();

    private async Task CleanupConnectionAsync()
    {
        FailPending(new ControlClientException("连接已断开"));
        if (_tcp is not null) try { _tcp.Dispose(); } catch { }
        _tcp = null;
        _stream = null;
        try { _reader?.Complete(); } catch { }
        _reader = null;
        try { _writer?.Complete(); } catch { }
        _writer = null;
        _hmacEnabled = false;
        CryptoUtil.Zero(_connMacKey);
        _connMacKey = [];
    }

    private static (string Host, int Port) ParseHostPort(string addr)
    {
        // settings 已校验格式（M1-22），此处仅拆分；方括号 IPv6 兼容
        if (addr.StartsWith('['))
        {
            var close = addr.IndexOf(']');
            return (addr[1..close], int.Parse(addr[(close + 2)..]));
        }
        var colon = addr.LastIndexOf(':');
        return (addr[..colon], int.Parse(addr[(colon + 1)..]));
    }

    /// <summary>服务端 5005 附带 serverTs（格式 "time_skew:{serverTs}"，ControlSession 行为）。</summary>
    private static bool TryParseServerTs(string httpLikeMsg, out ulong serverTs)
    {
        serverTs = 0;
        var idx = httpLikeMsg.IndexOf("time_skew:", StringComparison.Ordinal);
        if (idx < 0) return false;
        return ulong.TryParse(httpLikeMsg[(idx + "time_skew:".Length)..], out serverTs);
    }

    // ── pending 请求槽（单飞行）──────────────────────────────────────

    private sealed class PendingAck(IPcpMessage request)
    {
        private readonly TaskCompletionSource<IPcpMessage> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IPcpMessage Request { get; } = request;
        public byte RequestType => Request.MsgType;
        public bool Retried { get; set; }
        public Task<IPcpMessage> Task => _tcs.Task;

        public void TryComplete(IPcpMessage message) => _tcs.TrySetResult(message);
        public void TryFail(Exception error) => _tcs.TrySetException(error);
    }
}

/// <summary>
/// 5005 重试的消息重建：全部 PCP 消息为 positional record（Key(0)=Seq、Key(1)=TimestampMs），
/// 经主构造函数按参数名回填（Seq/TimestampMs 换新值，其余取现值）。低频路径，反射开销可忽略。
/// </summary>
internal static class MessageRebuilder
{
    public static IPcpMessage WithHeader(IPcpMessage message, uint seq, ulong timestampMs)
    {
        var type = message.GetType();
        var ctor = type.GetConstructors().Single();
        var parameters = ctor.GetParameters();
        var args = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var name = parameters[i].Name;
            args[i] = name switch
            {
                "Seq" => seq,
                "TimestampMs" => timestampMs,
                _ => type.GetProperty(name!)?.GetValue(message),
            };
        }
        return (IPcpMessage)ctor.Invoke(args);
    }
}

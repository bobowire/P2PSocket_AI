using System.IO.Pipelines;
using System.Net;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using P2P.Core.Utils;
using P2P.Server.Data;

namespace P2P.Server.Services;

/// <summary>会话可调参数（02 §2.2/§2.4：±120s 窗口；30s 心跳→空闲 75s 判死）。</summary>
public sealed record ControlSessionOptions
{
    public TimeSpan TsWindow { get; init; } = TimeSpan.FromSeconds(120);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(75);
    public string ServerVersion { get; init; } = "0.1.0";
}

/// <summary>
/// 单连接控制会话（02 §2.3，05 §5）：Pipelines 读循环 → 分发器（seq/ts 校验 → HMAC 验证 → 处理器）。
/// 每连接串行处理（02 §2.1）；HMAC 失败即断连（07 §4）。
/// 状态机：AwaitingHello →（deviceId 命中）AwaitingProof → Established；未注册设备 → NeedRegister 后待 0x10。
/// </summary>
public sealed class ControlSession : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly DeviceRegistry _registry;
    private readonly ControlSessionOptions _options;
    private readonly TimeProvider _time;
    private readonly Func<ControlSession, IPcpMessage, Task> _dispatcher; // 业务路由（M1-14+ 注册）
    private readonly Action? _onHmacFailure; // 断连计数（M1-13 完成判定）

    private readonly Channel<(byte[] Body, TaskCompletionSource Tcs)> _sendQueue =
        Channel.CreateBounded<(byte[], TaskCompletionSource)>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.Wait });

    private SessionState _state = SessionState.AwaitingHello;
    private byte[] _connMacKey = [];
    private byte[] _deviceSecret = [];
    private byte[] _nonceC = [];
    private byte[] _nonceS = [];
    private uint _lastPeerSeq;       // 对端 seq 严格递增
    private int _selfSeqInt;         // 出站 seq（原子递增）
    private Guid _deviceId;
    private bool _hmacEnabled;       // Established 后除 Register（NeedRegister 阶段）外全部签名
    private int _closed;
    private CancellationTokenSource _cts = new();
    private readonly Task _runLoop;
    private readonly Task _sendLoop;

    public ControlSession(Stream stream, IDbContextFactory<AppDbContext> dbFactory, DeviceRegistry registry,
        Func<ControlSession, IPcpMessage, Task> dispatcher, ControlSessionOptions? options = null,
        TimeProvider? time = null, Action? onHmacFailure = null,
        IPEndPoint? remoteEndPoint = null, IPEndPoint? localEndPoint = null)
    {
        _stream = stream;
        _reader = PipeReader.Create(stream);
        _writer = PipeWriter.Create(stream);
        _dbFactory = dbFactory;
        _registry = registry;
        _dispatcher = dispatcher;
        _options = options ?? new ControlSessionOptions();
        _time = time ?? TimeProvider.System;
        _onHmacFailure = onHmacFailure;
        RemoteEndPoint = remoteEndPoint;
        LocalEndPoint = localEndPoint;
        LastSeen = _time.GetLocalNow();
        _runLoop = RunAsync(_cts.Token);
        _sendLoop = SendLoopAsync(_cts.Token);
    }

    public enum SessionState { AwaitingHello, AwaitingProof, NeedRegister, Established, Closed }

    public SessionState State => _state;
    public Guid DeviceId => _deviceId;
    public bool IsEstablished => _state == SessionState.Established;

    /// <summary>读循环终止异常（协议错/IO 错；诊断观测用）。</summary>
    public Exception? Fault { get; private set; }

    /// <summary>读循环完成 Task（含收尾注销；监听器据此回收跟踪）。</summary>
    public Task Completion => _runLoop;

    /// <summary>最近一次心跳/握手时刻（PresenceMonitor 离线判定依据，FR-S-104）。</summary>
    public DateTimeOffset LastSeen { get; private set; }

    /// <summary>能力模式（02 §2.5）：会话级；登录→normal、登出→passive；初始 normal（未降级）。</summary>
    public CapabilityMode Capability { get; set; } = CapabilityMode.Normal;

    /// <summary>会话登录态用户（未登录/登出为 null；设备 owner 绑定持久在库）。</summary>
    public Guid? OwnerUserId { get; set; }

    /// <summary>连接对端端点（RelayService 端槽认领的源 IP 偏好，M2-07）。</summary>
    public IPEndPoint? RemoteEndPoint { get; }

    /// <summary>连接本地侧端点（中继端点派生：客户端经哪个接口到达即回哪个地址，M2-07）。</summary>
    public IPEndPoint? LocalEndPoint { get; }

    /// <summary>下一出站 seq（处理器构造回复消息头用；信令推送与读循环并发，原子递增）。</summary>
    public uint NextSeq() => (uint)Interlocked.Increment(ref _selfSeqInt);

    /// <summary>当前服务器时间戳（处理器构造回复消息头用）。</summary>
    public ulong ServerTimestamp() => NowMs64;

    private long NowMs => _time.GetLocalNow().ToUnixTimeMilliseconds();

    // ── 主循环（串行：02 §2.1）──────────────────────────────────────────

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idleCts.CancelAfter(_options.IdleTimeout); // 每轮读前重置：空闲判定按帧间隔
            while (_state != SessionState.Closed)
            {
                idleCts.CancelAfter(_options.IdleTimeout);
                var body = await FrameCodec.ReadFrameAsync(_reader, idleCts.Token).ConfigureAwait(false);
                if (body is null) break; // 对端正常关闭

                if (_state is SessionState.AwaitingHello or SessionState.NeedRegister || !_hmacEnabled)
                    await HandleFrameUnsignedAsync(body, ct).ConfigureAwait(false);
                else
                    await HandleFrameSignedAsync(body, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* 空闲超时/停机 */ }
        catch (Exception ex)
        {
            // 协议错/IO 错：断连（AI-19）——不记录秘密
            Fault = ex; // 诊断观测（测试/排障；异常消息不含敏感材料）
        }
        finally
        {
            await CloseAsync(null).ConfigureAwait(false);
        }
    }

    private async Task HandleFrameUnsignedAsync(byte[] body, CancellationToken ct)
    {
        var header = PcpCodec.Peek(body);
        switch (_state)
        {
            case SessionState.AwaitingHello when header.MsgType == MsgType.Hello:
                await HandleHelloAsync(body, ct).ConfigureAwait(false);
                return;
            case SessionState.NeedRegister when header.MsgType == MsgType.Register:
                // 未注册设备首个 Register 例外不签名（02 §2.2）
                await DispatchAsync(PcpCodec.Decode<Register>(body), ct).ConfigureAwait(false);
                return;
            case SessionState.AwaitingProof when header.MsgType == MsgType.Proof:
                await HandleProofAsync(body, ct).ConfigureAwait(false);
                return;
            default:
                await SendErrorAsync(ErrorCode.BadRequest, "unexpected_message", ct).ConfigureAwait(false);
                await CloseAsync("unexpected_message").ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleFrameSignedAsync(byte[] body, CancellationToken ct)
    {
        // 1) HMAC（先于一切语义处理，07 §4）
        byte[] msgpack;
        try
        {
            msgpack = PcpCodec.DecodeSigned(body, _connMacKey);
        }
        catch (ProtocolException)
        {
            _onHmacFailure?.Invoke();
            await CloseAsync("hmac_failed").ConfigureAwait(false); // 断连（SEC-22）
            return;
        }

        // 2) seq 严格递增 + ts 窗口（±120s，OQ-12）
        var header = PcpCodec.Peek(msgpack);
        if (header.Seq <= _lastPeerSeq)
        {
            _onHmacFailure?.Invoke(); // 计入安全计数（防重放拒绝）
            await CloseAsync("seq_regression").ConfigureAwait(false);
            return;
        }
        _lastPeerSeq = header.Seq;

        var skew = NowMs - (long)header.TimestampMs;
        if (Math.Abs(skew) > _options.TsWindow.TotalMilliseconds)
        {
            // 5005 TIME_SKEW：附 serverTs 供客户端重算 offset；客户端重试一次（02 §2.3）
            await SendErrorAsync(ErrorCode.TimeSkew, $"time_skew:{NowMs}", ct).ConfigureAwait(false);
            return;
        }

        // 3) 心跳内联处理（高频，不走分发器）；刷新在线时刻（FR-S-104）
        if (header.MsgType == MsgType.Heartbeat)
        {
            LastSeen = _time.GetLocalNow();
            await SendAsync(new HeartbeatAck(NextSeq(), NowMs64, MsgType.Heartbeat, (ulong)NowMs), ct).ConfigureAwait(false);
            return;
        }

        // 4) 业务分发（未知 msgType 容忍丢弃：02 §7 演进约定）
        if (!ExpectedTypeKnown(header.MsgType))
            return;
        await DispatchAsync(DecodeTyped(msgpack, header.MsgType), ct).ConfigureAwait(false);
    }

    private static bool ExpectedTypeKnown(byte msgType)
        => msgType is MsgType.Hello or MsgType.Proof or MsgType.Register or MsgType.RegisterAck
            or MsgType.UnbindMe or MsgType.DeviceUpdate or MsgType.UserRegister or MsgType.UserLogin
            or MsgType.UserLogout or MsgType.Heartbeat or MsgType.DeviceList or MsgType.GroupCreate
            or MsgType.GroupUpdate or MsgType.GroupDissolve or MsgType.MappingUpsert or MsgType.MappingDelete
            or MsgType.MappingStatus or MsgType.PunchRequest or MsgType.PunchInvite or MsgType.PunchResult
            or MsgType.PunchEndpoint or MsgType.PunchRetry or MsgType.RelayAllocate or MsgType.Error;

    private static IPcpMessage DecodeTyped(byte[] msgpack, byte msgType) => msgType switch
    {
        MsgType.DeviceUpdate => PcpCodec.Decode<DeviceUpdate>(msgpack),
        MsgType.UserRegister => PcpCodec.Decode<UserRegister>(msgpack),
        MsgType.UserLogin => PcpCodec.Decode<UserLogin>(msgpack),
        MsgType.UserLogout => PcpCodec.Decode<UserLogout>(msgpack),
        MsgType.Register => PcpCodec.Decode<Register>(msgpack),
        MsgType.UnbindMe => PcpCodec.Decode<UnbindMe>(msgpack),
        MsgType.DeviceList => PcpCodec.Decode<DeviceListRequest>(msgpack),
        MsgType.GroupCreate => PcpCodec.Decode<GroupCreate>(msgpack),
        MsgType.GroupUpdate => PcpCodec.Decode<GroupUpdate>(msgpack),
        MsgType.GroupDissolve => PcpCodec.Decode<GroupDissolve>(msgpack),
        MsgType.MappingUpsert => PcpCodec.Decode<MappingUpsert>(msgpack),
        MsgType.MappingDelete => PcpCodec.Decode<MappingDelete>(msgpack),
        MsgType.MappingStatus => PcpCodec.Decode<MappingStatus>(msgpack),
        MsgType.PunchRequest => PcpCodec.Decode<PunchRequest>(msgpack),
        MsgType.PunchEndpoint => PcpCodec.Decode<PunchEndpoint>(msgpack),
        MsgType.PunchRetry => PcpCodec.Decode<PunchRetry>(msgpack), // 0x73 回切协调（M2-19）
        MsgType.RelayAllocate => PcpCodec.Decode<RelayAllocate>(msgpack),
        MsgType.Error => PcpCodec.Decode<ErrorMessage>(msgpack),
        _ => PcpCodec.DecodeLoose(msgpack),
    };

    // ── 握手 ────────────────────────────────────────────────────────────

    private async Task HandleHelloAsync(byte[] body, CancellationToken ct)
    {
        var hello = PcpCodec.Decode<Hello>(body); // ts 不校（OQ-12）
        _nonceC = hello.NonceC;

        if (hello.ProtocolVersion != ProtocolVersion.Current)
        {
            await SendAsync(new HelloAck(NextSeq(), NowMs64, MsgType.Hello, _options.ServerVersion,
                ProtocolVersion.Current, RandomGenerator.Bytes(16), HelloStatus.VersionNotSupported, (ulong)NowMs), ct).ConfigureAwait(false);
            await CloseAsync("version_not_supported").ConfigureAwait(false); // 0x03 UpdateInfo 属 M2
            return;
        }

        // 已注册设备：查库定状态；未注册：NeedRegister
        if (hello.DeviceId is { } devId)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(d => d.Id == devId, ct).ConfigureAwait(false);
            if (device is { Disabled: false })
            {
                _deviceId = devId;
                _deviceSecret = device.DeviceSecret;
                _nonceS = RandomGenerator.Bytes(16);
                await SendAsync(new HelloAck(NextSeq(), NowMs64, MsgType.Hello, _options.ServerVersion,
                    ProtocolVersion.Current, _nonceS, HelloStatus.Ok, (ulong)NowMs), ct).ConfigureAwait(false);
                _state = SessionState.AwaitingProof;
                return;
            }
        }
        _nonceS = RandomGenerator.Bytes(16);
        await SendAsync(new HelloAck(NextSeq(), NowMs64, MsgType.Hello, _options.ServerVersion,
            ProtocolVersion.Current, _nonceS, HelloStatus.NeedRegister, (ulong)NowMs), ct).ConfigureAwait(false);
        _state = SessionState.NeedRegister;
    }

    private async Task HandleProofAsync(byte[] body, CancellationToken ct)
    {
        var proof = PcpCodec.Decode<Proof>(body);
        var input = new byte[_nonceC.Length + _nonceS.Length];
        _nonceC.AsSpan().CopyTo(input.AsSpan());
        _nonceS.AsSpan().CopyTo(input.AsSpan(_nonceC.Length));
        var expected = Mac.HmacSha256(_deviceSecret, input);

        if (!Mac.Verify(proof.Hmac, expected))
        {
            await SendAsync(new ProofAck(NextSeq(), NowMs64, MsgType.Proof, false), ct).ConfigureAwait(false);
            await CloseAsync("proof_failed").ConfigureAwait(false); // 身份证明失败断连
            return;
        }

        // connMacKey = HKDF-SHA256(deviceSecret, nonceC|nonceS|"pcp-mac")（02 §2.3）
        _connMacKey = Hkdf.Derive(_deviceSecret, input, "pcp-mac"u8, 32);
        _hmacEnabled = true;
        _state = SessionState.Established;
        LastSeen = _time.GetLocalNow();
        _registry.Register(this);
        await SendAsync(new ProofAck(NextSeq(), NowMs64, MsgType.Proof, true), ct).ConfigureAwait(false);
    }

    // ── 发送（写队列串行化；Established 后签名）──────────────────────────

    private ulong NowMs64 => (ulong)NowMs;

    public async Task SendAsync<T>(T message, CancellationToken ct = default) where T : class, IPcpMessage
    {
        var body = _hmacEnabled
            ? PcpCodec.EncodeSigned(message, _connMacKey)
            : PcpCodec.Encode(message);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _sendQueue.Writer.WriteAsync((body, tcs), ct).ConfigureAwait(false);
        await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>服务端主动推送（M1-17 信令两段式）。</summary>
    public Task PushAsync<T>(T message, CancellationToken ct = default) where T : class, IPcpMessage
        => SendAsync(message, ct);

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var (body, tcs) in _sendQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await FrameCodec.WriteFrameAsync(_writer, body, ct).ConfigureAwait(false);
                tcs.TrySetResult();
            }
        }
        catch (Exception)
        {
            _sendQueue.Writer.TryComplete();
        }
        finally
        {
            // 队列中未写出的项立即失败，避免等待方（dispatcher 内 SendAsync）悬挂
            while (_sendQueue.Reader.TryRead(out var pending))
                pending.Tcs.TrySetException(new ObjectDisposedException(nameof(ControlSession), "发送队列已关闭"));
        }
    }

    /// <summary>回错误消息（处理器与会话内部共用；0x7E）。</summary>
    public Task SendErrorAsync(int code, string msg, CancellationToken ct = default)
        => SendAsync(new ErrorMessage(NextSeq(), NowMs64, MsgType.Error, code, msg), ct);

    // ── 分发与关闭 ─────────────────────────────────────────────────────

    private async Task DispatchAsync(IPcpMessage message, CancellationToken ct)
    {
        try
        {
            await _dispatcher(this, message).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await SendErrorAsync(ErrorCode.BadRequest, "handler_error", ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 注册处理完成（M1-14 调用）：NeedRegister 会话携带新凭据进入已建立态。
    /// connMacKey 以注册时下发的 deviceSecret 派生（nonceC|nonceS 本会话已知）。
    /// </summary>
    public void CompleteRegistration(Guid deviceId, byte[] deviceSecret)
    {
        if (_state != SessionState.NeedRegister)
            throw new InvalidOperationException($"状态 {_state} 不允许完成注册");
        _deviceId = deviceId;
        _deviceSecret = deviceSecret;
        var input = new byte[_nonceC.Length + _nonceS.Length];
        _nonceC.AsSpan().CopyTo(input.AsSpan());
        _nonceS.AsSpan().CopyTo(input.AsSpan(_nonceC.Length));
        _connMacKey = Hkdf.Derive(_deviceSecret, input, "pcp-mac"u8, 32);
        _hmacEnabled = true;
        _state = SessionState.Established;
        _registry.Register(this);
    }

    public async Task CloseAsync(string? reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        var wasEstablished = _state == SessionState.Established;
        _state = SessionState.Closed;
        if (wasEstablished)
        {
            _registry.Unregister(this);
            await PersistLastSeenAsync().ConfigureAwait(false); // 05 §5：断连即 last_seen_at 落库
        }
        try { _cts.Cancel(); } catch { }
        try { await _stream.DisposeAsync(); } catch { }
        _reader.Complete();
        _writer.Complete();
        _sendQueue.Writer.TryComplete();
        CryptoUtil.Zero(_connMacKey);
        CryptoUtil.Zero(_deviceSecret);
    }

    /// <summary>断连落库（FR-S-104）：在线判定在内存，超时/断连时刻持久化。</summary>
    private async Task PersistLastSeenAsync()
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
            var stamp = _time.GetLocalNow().UtcDateTime;
            await db.Devices.Where(d => d.Id == _deviceId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSeenAt, stamp))
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 断连路径不因落库失败抛出（AI-19：观测数据可丢，连接清理优先）
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync("disposed").ConfigureAwait(false);
        try { await _runLoop; } catch { }
        try { await _sendLoop; } catch { }
        _cts.Dispose();
    }
}

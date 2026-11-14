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
// - 升级引导（M2-26，FR-C-904/02 §2.3）：Hello 5004 版本不符 → 受理窗内发无签名 0x03 →
//   UpdateInfoResponse 缓存（LastUpgradeInfo）+ UpgradeRequired 事件（宿主接 WS/浏览器引导）
//   → 仍抛出保持退避重连（每轮重取，升级信息随服务端配置演进）；已建立会话 0x03 同族应答照常。
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net.Sockets;
using MessagePack;
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

    /// <summary>Hello 协议版本覆盖缝（M2-26 测试注入）：生产 null=ProtocolVersion.Current；
    /// 集成测注入越界值驱动服务端 5004 拒答，验证升级引导数据链（FR-C-904）。</summary>
    public ushort? ProtocolVersionOverride { get; init; }
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

/// <summary>登录族发送本地拒绝（M5-06/SEC-26，07 §10.3）：本连接 HelloAck 未携带 loginEphPub
///（旧服务端）——不回退明文（防主动 MITM 剥字段降级）；API 层映射 5004 引导升级服务端。</summary>
public sealed class LoginEncryptionUnavailableException(string message) : Exception(message);

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
    private byte[]? _loginEphPub; // 本连接登录加密公钥（M5-06/SEC-26，07 §10.2；旧服务端=缺失）
    private long _lastHeartbeatSentAt;
    private volatile UpdateInfoResponse? _lastUpgradeInfo; // 最近版本拒答窗口取回的升级信息（M2-26）
    private PendingAck? _pending;
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> _punchFires = new(); // 0x77 等待器（每打洞会话至多一个）
    private int _state = (int)ControlClientState.Idle;
    private CapabilityMode _capability = CapabilityMode.Normal;
    private bool _stopped;
    private IReadOnlyList<(string Host, int Port)> _serverAddrs; // 可换（向导选定地址，M1-29）
    private CancellationTokenSource? _backoffWake; // 退避等待可被换址唤醒（M1-30：向导选定地址即重连，不等满退避期）
    private int _sessionGen; // 会话代数：每条新连接 Established 递增（ReconnectAsync 区分新旧会话）

    /// <summary>状态变迁通知（UI/宿主展示连接性）。</summary>
    public event Action<ControlClientState>? StateChanged;

    /// <summary>服务器推送（无对应 pending 请求的消息：0x71 PunchInvite 等，M1-24+ 挂载处理）。</summary>
    public event Action<IPcpMessage>? ServerPush;

    /// <summary>能力模式变迁（登录/登出/重连驱动，02 §2.5）。</summary>
    public event Action<CapabilityMode>? CapabilityChanged;

    /// <summary>会话重建（缺陷批次 2 修 A）：非首条连接重回到 Established（断线退避重连/
    /// 强制重连后新会话就绪）。首条连接与 NeedRegister→注册完成的同连接迁移不触发。
    /// 宿主据此重放登录（P2 方案 a 的重连对等修复）与 failed 映射兜底。</summary>
    public event Action? SessionReestablished;

    /// <summary>版本拒答取回升级信息（M2-26，FR-C-904）：每轮 5004 拒答均触发
    ///（宿主接 WS upgrade_required 广播与浏览器引导去重）；载荷=0x03 应答原文。</summary>
    public event Action<UpdateInfoResponse>? UpgradeRequired;

    /// <summary>诊断日志（宿主 M1-30 接 Serilog）。</summary>
    public event Action<string>? Log;

    public ControlClientState State => (ControlClientState)Volatile.Read(ref _state);
    public bool IsReady => State is ControlClientState.Established or ControlClientState.NeedRegister;
    public CapabilityMode Capability => _capability;
    public Guid? DeviceId { get; private set; }

    /// <summary>最近一次版本拒答窗口取回的升级信息（M2-26）：本地 /api/upgrade/info
    /// 在通道不可建立时（版本不符恰是此态）的回落数据源。</summary>
    public UpdateInfoResponse? LastUpgradeInfo => _lastUpgradeInfo;

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
    /// 5005 TIME_SKEW 自动重校准并换新 seq/ts 重试一次（02 §2.3）。
    /// 工厂重载为唯一并发安全路径：消息在发送门内组包（seq 分配序≡字节写入序，02 §2.4 严格递增）。</summary>
    public async Task<TAck> SendRequestAsync<TAck>(Func<uint, IPcpMessage> factory, CancellationToken ct = default)
        where TAck : class, IPcpMessage
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (!IsReady)
            throw new ControlClientException($"控制连接未就绪（{State}），无法发送请求");

        await _requestGate.WaitAsync(ct);
        try
        {
            PendingAck pending;
            // 门内原子：seq 分配→组包→pending 注册→写线（pending 先于写线=Ack 到达不丢；
            // seq 分配与写入同临界区=多线程发送不再乱序 → 服务端 seq_regression 断连根治）
            await _sendGate.WaitAsync(ct);
            try
            {
                var request = factory(NextSeq());
                ThrowIfPassiveBlocked(request.MsgType);
                pending = new PendingAck(request);
                _pending = pending;
                var writer = _writer ?? throw new ControlClientException("连接未建立");
                await FrameCodec.WriteFrameAsync(writer, EncodeOutbound(request), ct);
            }
            finally { _sendGate.Release(); }
            try { return await AwaitAckAsync<TAck>(pending, ct); }
            finally { _pending = null; }
        }
        finally { _requestGate.Release(); }
    }

    /// <summary>已组包消息直发（单线程/测试场景保留：seq 已在消息内固化——调用方自行保证与
    /// 其他发送线程的顺序；多线程并发发送须用工厂重载）。</summary>
    public Task<TAck> SendRequestAsync<TAck>(IPcpMessage request, CancellationToken ct = default)
        where TAck : class, IPcpMessage
        => SendRequestAsync<TAck>(_ => request, ct);

    /// <summary>单向发送（0x76 PunchEndpoint 等无 Ack 上报，M1-26）：门内原子组包（并发安全路径）。</summary>
    public async Task SendAsync(Func<uint, IPcpMessage> factory, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (!IsReady)
            throw new ControlClientException($"控制连接未就绪（{State}），无法发送消息");
        await SendWithSeqAsync(factory, ct);
    }

    /// <summary>已组包消息单向直发（单线程/测试场景保留；多线程并发须用工厂重载）。</summary>
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

    /// <summary>等待 0x77 PunchFire 齐射锚点（02 §5.2⑤，被邀请方 Puncher 经缝调用）：
    /// 会话锚点已到/到达即返 true；超 <paramref name="timeout"/> 未到（旧服务端不推送/丢失）返 false，
    /// 调用方兜底立即开火。等待先于推送注册也安全（GetOrAdd 同键汇合）；断线重连期间等待照常走超时。</summary>
    public async Task<bool> WaitForPunchFireAsync(Guid sessionId, TimeSpan timeout, CancellationToken ct = default)
    {
        var tcs = _punchFires.GetOrAdd(sessionId,
            _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            await tcs.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally
        {
            _punchFires.TryRemove(sessionId, out _); // 每会话单等待器：出表防累积（晚到锚点对已出表会话无消费者）
        }
    }

    /// <summary>校准后时间戳（OQ-12：全部出站消息 timestampMs 用此时钟）。</summary>
    public ulong TimestampMs() => Clock.NowRemoteMs(_time);

    /// <summary>登录族密码 ECIES 封装（M5-06/SEC-26，07 §10.1）：info="pcp-login-v1" 域分离、
    /// 每次现加密（记住我重放非密文重放）。本连接未收到 loginEphPub（旧服务端）→ 本地拒绝
    /// <see cref="LoginEncryptionUnavailableException"/>，不回退明文（OQ-21① 硬切）。</summary>
    public byte[] BoxLoginSecret(string secret)
    {
        var pub = _loginEphPub;
        if (pub is not { Length: 65 })
            throw new LoginEncryptionUnavailableException(
                "服务端版本过旧，不支持加密登录，请先升级服务端（password_ecies_required）");
        return Ecies.Encrypt(pub, System.Text.Encoding.UTF8.GetBytes(secret), "pcp-login-v1"u8);
    }

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

    /// <summary>强制重连并等待新会话就绪（02 §2.5"重新登录须重连后再发"的落地路径，P1 修复）：
    /// 杀当前连接（同 <see cref="UpdateServerAddrs"/> 手法）→ 主循环退避后重建 → 以会话代数
    /// 区分新旧连接，等到<b>新代数</b>的 Established 才返回（新会话初始 Normal，能力复位）。
    /// 15s 预算覆盖杀连接后首退避（1s）+ 握手；超时/停机抛 <see cref="ControlClientException"/>。</summary>
    public async Task ReconnectAsync(CancellationToken ct = default)
    {
        if (_stopped) throw new ControlClientException("控制通道已停止，无法重连");
        var gen = Volatile.Read(ref _sessionGen);
        Log?.Invoke("强制重连：passive 会话复位 Normal（02 §2.5）");
        try { Volatile.Read(ref _backoffWake)?.Cancel(); } // 唤醒退避等待（不必等满旧退避期）
        catch (ObjectDisposedException) { /* 唤醒窗口竞态：等待已自然结束 */ }
        try { _tcp?.Dispose(); } catch { /* 已关 */ } // 杀当前连接 → 读循环退出 → 下一轮重连
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            if (_stopped) throw new ControlClientException("重连期间通道已停止");
            if (State == ControlClientState.Established && Volatile.Read(ref _sessionGen) != gen)
                return;
            try { await Task.Delay(50, timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ControlClientException($"强制重连超时（当前状态 {State}）");
            }
        }
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
            try
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
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } // 停机：交外层退出
                    catch (Exception e) // 含非停机 OCE（连接超时等）：一律视为本地址失败换下一个
                    {
                        Log?.Invoke($"连接 {host}:{port} 失败：{e.Message}");
                        await CleanupConnectionAsync();
                    }
                }

                if (connected && !ct.IsCancellationRequested)
                {
                    try { await RunSessionAsync(ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception e)
                    {
                        Log?.Invoke($"会话中断：{e.Message}");
                    }
                    attempt++;
                    await CleanupConnectionAsync();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; } // 唯一正常出口：停机
            catch (Exception e)
            {
                // 7×24 兜底（2026-11-12 断网不恢复根因家族）：任何未预期异常不得杀死重连循环——
                // 循环死亡=状态永久滞留（Connecting/Handshaking），此后无人再发起连接，只能重启进程
                Log?.Invoke($"重连循环异常（已忽略，继续退避重试）：{e.Message}");
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

    /// <summary>连接+握手（自超时 OCE → 普通异常转换层）。</summary>
    private async Task ConnectAndHandshakeAsync(string host, int port, CancellationToken ct)
    {
        // 全程有界（2026-11-12 断网不恢复根因修复）：连接段 ConnectTimeout、握手段 RequestTimeout
        // 分阶段起表（CancelAfter 重定表）。此前两处无界/裸传 ct：
        // ①黑洞路由（路由器断电但网卡仍在）SYN 无响应 → ConnectAsync 超时抛 OCE，而 RunLoop
        //   旧过滤条件放行一切 OCE → 异常穿透整个重连循环（循环死亡、状态永久滞留 Connecting，
        //   此后无人再连接，只能重启进程——日志 13:26:40 后 [control] 再无任何输出即此）；
        // ②TCP 连上但对端/中间盒不答（挂起型故障）→ HelloAck 读取无超时 → 永久滞留 Handshaking。
        // 自超时 OCE 在此转为普通异常上抛（RunLoop 视为本地址失败换下一个）；仅停机（ct）OCE 原样传播。
        try
        {
            await ConnectAndHandshakeCoreAsync(host, port, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ControlClientException(
                $"连接/握手 {host}:{port} 超时（连接 {_options.ConnectTimeout.TotalSeconds:0.#}s"
                + $" + 握手 {_options.RequestTimeout.TotalSeconds:0.#}s）");
        }
    }

    private async Task ConnectAndHandshakeCoreAsync(string host, int port, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_options.ConnectTimeout);
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(host, port, deadline.Token);
        _tcp.NoDelay = true; // 02 §2.1
        _stream = _tcp.GetStream();
        _reader = PipeReader.Create(_stream);
        _writer = PipeWriter.Create(_stream);
        _lastPeerSeq = 0;
        _hmacEnabled = false;
        SetState(ControlClientState.Handshaking);
        deadline.CancelAfter(_options.RequestTimeout); // 连接段通过：重定表为握手段预算

        // Hello：ts 不校（OQ-12：尚无共享时间基准）；deviceId 仅注册过设备携带
        _nonceC = RandomGenerator.Bytes(16);
        var sentAt = LocalNowMs;
        await SendWireAsync(PcpCodec.Encode(new Hello(NextSeq(), (ulong)sentAt, MsgType.Hello,
            _options.ProtocolVersionOverride ?? ProtocolVersion.Current, DeviceId, _nonceC)), deadline.Token);

        var helloAck = PcpCodec.Decode<HelloAck>(await ReadHandshakeFrameAsync(deadline.Token));
        // 时钟对齐（OQ-12）：RTT/2 补偿。此后全部出站 ts 用校准后时钟
        Clock.Calibrate(helloAck.ServerTs, sentAt, LocalNowMs);
        _nonceS = helloAck.NonceS;
        _loginEphPub = helloAck.LoginEphPub; // M5-06/SEC-26：登录族加密公钥（缺失=旧服务端，发送前本地拒）

        switch (helloAck.Status)
        {
            case HelloStatus.VersionNotSupported:
                await HandleVersionRejectedAsync(helloAck, deadline.Token);
                return; // 不可达：HandleVersionRejectedAsync 恒抛（保持退避重连语义）
            case HelloStatus.NeedRegister:
                SetState(ControlClientState.NeedRegister); // M1-24 向导在此发 0x10（未签名）
                return;
        }

        // Proof：HMAC(deviceSecret, nonceC|nonceS)（02 §2.3；服务端握手期不校 ts）
        await SendWireAsync(PcpCodec.Encode(new Proof(NextSeq(), TimestampMs(), MsgType.Proof,
            Mac.HmacSha256(_deviceSecret, NonceInput()))), deadline.Token);

        // ProofAck(ok) 时服务端已启用签名（先派生 connMacKey 再发送）；ProofAck(false)/Error 为未签名。
        // 客户端可自行派生同一 connMacKey，先按签名帧解，失败回退未签名。
        var candidateKey = Hkdf.Derive(_deviceSecret, NonceInput(), "pcp-mac"u8, 32);
        var proofFrame = await ReadHandshakeFrameAsync(deadline.Token);
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
        if (Interlocked.Increment(ref _sessionGen) > 1) // 新连接就绪（CompleteRegistration 是同连接迁移，不计）
            SessionReestablished?.Invoke(); // 重连沿：宿主重放登录+failed 映射兜底（首连由注册后路径处理）
    }

    /// <summary>版本拒答处理（M2-26，FR-C-904/02 §2.3）：5004 后服务端受理窗内 0x03 是唯一可发消息
    ///（无签名）→ 读 UpdateInfoResponse → 缓存 + 事件 → 仍抛出令连接循环进入退避重连
    ///（每轮重连重取一次，服务端 update_* 配置演进可跟进）。取回失败不吞异常路径，仅告警。</summary>
    private async Task HandleVersionRejectedAsync(HelloAck helloAck, CancellationToken ct)
    {
        UpdateInfoResponse? info = null;
        try
        {
            await SendWireAsync(PcpCodec.EncodeObject(new UpdateInfoRequest(
                NextSeq(), TimestampMs(), MsgType.UpdateInfo)), ct);
            info = PcpCodec.Decode<UpdateInfoResponse>(await ReadHandshakeFrameAsync(ct)); // 受理窗应答未签名
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log?.Invoke($"升级信息获取失败：{e.Message}（重连后重取）");
        }
        if (info is not null)
        {
            _lastUpgradeInfo = info; // volatile 字段：引用写原子+可见性有序
            UpgradeRequired?.Invoke(info);
        }
        throw new ControlClientException(
            $"服务端协议版本 {helloAck.ProtocolVersion} 不支持本机 {ProtocolVersion.Current}（升级引导 /upgrade?reason=version，FR-C-904）");
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
            case MsgType.Invalidation:
                var inv = PcpCodec.Decode<Invalidation>(msgpack);
                // newCapability 即时降级（M2-15，02 §2.5）：能力是连接级状态在此层维护（镜像 UserLogout 分支）；
                // 受影响映射置 invalid 由宿主 OnServerPush 消费（05 §4）；0x75 恒为推送无 pending 配对
                if (inv.NewCapability is { } cap) SetCapability(cap);
                ServerPush?.Invoke(inv);
                return;
            case MsgType.PunchFire:
                var fire = PcpCodec.Decode<PunchFire>(msgpack);
                // 0x77 齐射锚点（02 §5.2⑤）：纯推送无 pending 配对，唯一消费者=WaitForPunchFireAsync 等待器
                _punchFires.GetOrAdd(fire.SessionId,
                    _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously))
                    .TrySetResult(true);
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
        MsgType.RemoteCodeReset => PcpCodec.Decode<RemoteCodeResetAck>(msgpack), // 0x14 同族 Ack（M2-15 端点持久化）
        MsgType.UserRegister => PcpCodec.Decode<UserRegisterAck>(msgpack),
        MsgType.UserChangePassword => PcpCodec.Decode<UserChangePasswordAck>(msgpack), // 0x23 同族（M2-27 /api/auth/password）
        MsgType.DeviceList => PcpCodec.Decode<DeviceListResponse>(msgpack),
        MsgType.DeviceListUpdate => PcpCodec.Decode<DeviceListUpdate>(msgpack), // 0x41 提示帧（M2-15 → WS device_list）
        MsgType.GroupList => PcpCodec.Decode<GroupListResponse>(msgpack),      // 0x42 已加入分组（M2-27 /api/groups）
        MsgType.LanSegmentsUpsert => PcpCodec.Decode<LanSegmentsUpsertAck>(msgpack), // 0x63 同族（上报接线 M2-27）
        MsgType.GroupCreate => PcpCodec.Decode<GroupCreateAck>(msgpack),
        MsgType.GroupJoin => PcpCodec.Decode<GroupJoinAck>(msgpack),           // 0x51 凭码入组（M2-27 /api/groups join）
        MsgType.GroupLeave => PcpCodec.Decode<GroupLeaveAck>(msgpack),         // 0x52 自退（M2-27 /api/groups leave）
        MsgType.JoinRequests => DecodeJoinRequests(msgpack),                   // 0x53 双形态同族（M2-27）
        MsgType.GroupInviteGen => PcpCodec.Decode<GroupInviteGenAck>(msgpack), // 0x54 邀请码（M2-27）
        MsgType.GroupRemoveMember => PcpCodec.Decode<GroupRemoveMemberAck>(msgpack), // 0x57 移出（M2-27）
        MsgType.GroupUpdate => PcpCodec.Decode<GroupUpdateAck>(msgpack),
        MsgType.GroupDissolve => PcpCodec.Decode<GroupDissolveAck>(msgpack),
        MsgType.MappingUpsert => PcpCodec.Decode<MappingUpsertAck>(msgpack),
        MsgType.MappingDelete => PcpCodec.Decode<MappingDeleteAck>(msgpack),
        MsgType.PunchRequest => PcpCodec.Decode<PunchRequestAck>(msgpack),
        MsgType.PunchInvite => PcpCodec.Decode<PunchInvite>(msgpack), // S→C 推送（M1-26 挂载处理）
        MsgType.PunchStart => PcpCodec.Decode<PunchStartGo>(msgpack), // 0x78 S→C 恒为 Go（请求应答/被邀请方推送，02 §5.2⑥）
        MsgType.PunchRetry => PcpCodec.Decode<PunchRetry>(msgpack),  // 0x73 请求-应答同族 + 被邀请方推送（M2-19）
        MsgType.UpdateInfo => PcpCodec.Decode<UpdateInfoResponse>(msgpack), // 0x03 已建立会话同族应答（M2-26）
        // S→C 方向 0x74 恒为 Grant（M2-17 分配应答/对端侧推送接续，02 §6.1②；事件面接线 M2-15）
        MsgType.RelayAllocate => PcpCodec.Decode<RelayGrant>(msgpack),
        _ => PcpCodec.DecodeLoose(msgpack),
    };

    /// <summary>0x53 双形态同 msgType（02 §2.4）：List 应答 Key(3)=JoinRequestItem[]，
    /// Approve/Reject 确认 Key(3)=bool——bool 与数组互不可解，try-decode 判别安全。</summary>
    private static IPcpMessage DecodeJoinRequests(byte[] msgpack)
    {
        try { return PcpCodec.Decode<JoinRequestsResponse>(msgpack); }
        catch (MessagePackSerializationException) { return PcpCodec.Decode<JoinRequestsAck>(msgpack); }
    }

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
            // 门内原子重发：重试也与心跳/新请求并发，seq 换新须与写线同临界区
            await SendWithSeqAsync(
                seq => MessageRebuilder.WithHeader(pending.Request, seq, TimestampMs()), _cts.Token);
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
            try
            {
                // 门内原子（此前组包在门外：心跳与请求并发时 seq 分配序≠写线序 → 服务端
                // seq_regression 断连——M4-05 基准 8 映射并发场景稳定复现的根因）
                await SendWithSeqAsync(
                    seq => new Heartbeat(seq, TimestampMs(), MsgType.Heartbeat), ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log?.Invoke($"心跳发送失败：{e.Message}");
                return; // 读循环将随断流退出，触发重连
            }
        }
    }

    // ── 出站（单写者门卫：心跳/请求/重试并发）─────────────────────────

    /// <summary>门内原子原语：seq 分配→组包→passive 校验→编码→写线。
    /// seq 分配序≡字节写入序（02 §2.4 严格递增）——此前消息在调用线程组包（seq 构造时刻固化）、
    /// 写线在门内，多线程并发时分配序≠写线序 → 服务端 seq_regression 断连（M4-05 基准暴露）。
    /// 无就绪检查（调用方自备）；passive 在门内对已组包消息校验（心跳 0x30 非主动类恒通过）。</summary>
    private async Task SendWithSeqAsync(Func<uint, IPcpMessage> factory, CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct);
        try
        {
            var message = factory(NextSeq());
            ThrowIfPassiveBlocked(message.MsgType);
            var writer = _writer ?? throw new ControlClientException("连接未建立");
            await FrameCodec.WriteFrameAsync(writer, EncodeOutbound(message), ct);
        }
        finally { _sendGate.Release(); }
    }

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
        or MsgType.DeviceList or MsgType.GroupList
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

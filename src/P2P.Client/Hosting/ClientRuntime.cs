// M1-30 客户端运行时装配（01 §4.1 冷启动序列、01 §3.2 进程模型）：
// 读状态 → [未注册：仅本地 Web 向导模式，等 RegistrationCompleted] /
// [已注册：Nic 应用虚拟 IP（失败降级告警不阻断）→ 通道 Established → 恢复启用中映射（启用即打洞）]。
// Puncher 委托缝在此接 ControlClient（0x70/0x76/STUN，M1-26 注）；0x71 PunchInvite 推送接调度器；
// ClientReporter 上报三消息（M2-22：0x72/0x62/0x64，停机序先于引擎/control）。
// Program.cs（宿主入口）经 ClientHostService 驱动；测试直接构造（M1-35 同法注入替身）。
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using P2P.Client.Control;
using P2P.Client.Mapping;
using P2P.Client.Punch;
using P2P.Client.Registration;
using P2P.Client.Storage;
using P2P.Client.Tunnel;
using P2P.Client.Web;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using P2P.Core.Stun;
using P2P.Core.Tunnel;
using P2P.Nic;
using P2P.Nic.Linux;
using P2P.Nic.Windows;

namespace P2P.Client.Hosting;

/// <summary>运行时可调参数（默认值取自本机默认布局；测试注入临时目录与替身）。</summary>
public sealed record ClientRuntimeOptions
{
    /// <summary>状态/配置基目录（03 §5：Windows %ProgramData%\P2PClient、Linux /etc/p2p-client）。</summary>
    public string BaseDir { get; init; } = ClientPaths.DefaultBaseDir;

    /// <summary>交互模式（--console）：向导 URL 尝试打开浏览器（服务模式仅日志，01 §3.2）。</summary>
    public bool Interactive { get; init; }

    /// <summary>网卡替身缝（M1-35 双实例冒烟同法注入）。</summary>
    public INicManager? NicOverride { get; init; }

    /// <summary>打洞 socket 绑定地址覆盖缝（M1-35：NatSimulator 按源 IP 识别客户端 NAT，
    /// 双客户端打洞 socket 须各自绑定内网回环别名；生产 null=Any 全接口）。</summary>
    public IPAddress? PunchBindOverride { get; init; }

    /// <summary>中继回切重试周期覆盖缝（M2-19）：生产 null=60s（PRD 06 §6/OQ-7 定值，
    /// 非用户可调）；集成测试注入秒级缩短以验证周期触发与回切全链。</summary>
    public TimeSpan? RelayRetryIntervalOverride { get; init; }

    /// <summary>0x64 流量上报周期覆盖缝（M2-22）：生产 null=30s（02 §2.4 定值）；
    /// 集成测试注入亚秒级以验证周期落库与停机补报。</summary>
    public TimeSpan? StatsIntervalOverride { get; init; }
}

/// <summary>客户端全组件生命周期（单一属主；启动序=01 §4.1，停机序为其逆序）。</summary>
public sealed class ClientRuntime : IAsyncDisposable
{
    private readonly ClientRuntimeOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private StateStore _state = null!;
    private SettingsStore _settings = null!;
    private PeersStore _peers = null!;
    private LanSegmentsStore _lanSegments = null!;
    private ControlClient _control = null!;
    private INicManager _nic = null!;
    private TunnelHost _host = null!;
    private LazyPuncher _puncher = null!;
    private PunchScheduler _scheduler = null!;
    private MappingEngine _engine = null!;
    private ClientReporter _reporter = null!;
    private MappingSyncService _sync = null!;
    private ClientRegistrationService _wizard = null!;
    private LocalApiServices _api = null!;
    private WebApplication _app = null!;
    private Task? _relayRetryLoop;
    private int _registeredPathStarted;
    private int _disposed;

    /// <summary>诊断日志（Program 接 Serilog）。</summary>
    public event Action<string>? Log;

    /// <summary>本地 API 服务束（启动后可用；测试探查/宿主诊断）。</summary>
    public LocalApiServices Api => _api;

    /// <summary>本地 Web 实际监听端口（settings.localWebPort）。</summary>
    public int WebPort { get; private set; }

    public ClientRuntime(ClientRuntimeOptions? options = null) => _options = options ?? new ClientRuntimeOptions();

    /// <summary>冷启动（01 §4.1）。未注册时启动即返回（向导模式），注册完成事件续跑注册后路径。</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        // ① 读本地状态与配置（NFR-35：settings 非法直接抛出，由入口拒启）
        ClientPaths.EnsureBaseDir(_options.BaseDir);
        _settings = new SettingsStore(_options.BaseDir);
        _settings.Load();
        _state = new StateStore(_options.BaseDir);
        _state.Load();
        _peers = new PeersStore(_options.BaseDir); // M2-23 目标设备级回退配置（损坏自愈，不拒启）
        _peers.Load();
        _peers.Recovered += m => Log?.Invoke($"[peers] {m}");
        _lanSegments = new LanSegmentsStore(_options.BaseDir); // M2-11 内网段白名单（损坏自愈，不拒启）
        _lanSegments.Load();
        _lanSegments.Recovered += m => Log?.Invoke($"[lansegments] {m}");

        var addrs = _settings.Settings.ServerAddrs;
        if (addrs.Length == 0)
        {
            if (_state.State.IsRegistered)
                throw new ConfigValidationException(
                    "已注册设备但 serverAddrs 为空：状态与配置不一致（请参照 08 §5.2 修正 settings.json 后重启）",
                    ["serverAddrs：已注册设备不可为空"]);
            addrs = ["127.0.0.1:1"]; // 占位（永不可达）：向导第一步换址即接替（04 §2.2）
        }

        // ② 组件装配（控制通道自 ctor 起后台连接循环）
        _control = new ControlClient(addrs, new ControlClientOptions(),
            deviceId: _state.State.DeviceId, deviceSecret: _state.State.DeviceSecret);
        _control.Log += m => Log?.Invoke($"[control] {m}");
        _nic = _options.NicOverride ?? CreateNic();
        _nic.Degraded += m => Log?.Invoke($"[nic] 降级告警：{m}");
        _host = new TunnelHost();
        _puncher = new LazyPuncher();
        _scheduler = new PunchScheduler(_puncher);
        var bindIp = IPAddress.TryParse(_state.State.VirtualIp, out var vip) ? vip : IPAddress.Loopback;
        _engine = new MappingEngine(_host, _scheduler, bindIp,
            enabledCidrsProvider: _lanSegments.EnabledCidrs); // 监听绑虚拟 IP（01 §3.2）+ L3 白名单（M2-11）
        _engine.Log += m => Log?.Invoke($"[engine] {m}");
        // 上报三消息（M2-22，FR-C-404/1002）：0x72 打洞结果 / 0x62 映射状态 / 0x64 流量统计
        _reporter = new ClientReporter(_control, _scheduler, _engine, new ClientReporterOptions
        {
            StatsInterval = _options.StatsIntervalOverride ?? TimeSpan.FromSeconds(30),
        });
        _reporter.Log += m => Log?.Invoke($"[report] {m}");
        _sync = new MappingSyncService(_control, _engine, _state);
        _wizard = new ClientRegistrationService(_control, _state, _nic);
        _api = new LocalApiServices(_control, _state, _settings, _peers, _wizard, _sync, _scheduler);
        _control.ServerPush += OnServerPush; // 0x71 PunchInvite → 被邀请方打洞（02 §5.1③）

        // ③ 本地 Web（两种分支都启：向导也经它完成注册）
        WebPort = _settings.Settings.LocalWebPort;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{WebPort}"); // TD-12 仅本机回环
        _app = builder.Build();
        _app.UseWebSockets();
        _app.MapLocalApi(_api);
        // SPA 同端口静态托管（04 §0「SPA 由同端口静态托管」、08 §2 NFR-31）：wwwroot 经
        // MapStaticAssets 服务（读 staticwebassets.endpoints.json，含 .br/.gz 预压缩直发）；
        // MapFallbackToFile 兜底 SPA 路由（/wizard、/login 等均回 index.html 由前端路由接管）。
        // 挂载顺序：API 端点在前，fallback 最后兜底，不遮蔽 /api/*。
        // 清单随发布产物存在才挂——集成测试宿主（testhost）无清单会抛
        // InvalidOperationException（MapStaticAssets 按 {宿主程序集}.staticwebassets 解析），跳过仅 API。
        var spaManifest = Path.Combine(AppContext.BaseDirectory,
            $"{typeof(ClientRuntime).Assembly.GetName().Name}.staticwebassets.endpoints.json");
        if (File.Exists(spaManifest))
        {
            _app.MapStaticAssets(spaManifest);
            _app.MapFallbackToFile("index.html");
        }
        await _app.StartAsync(ct);

        // ④ 分支：未注册 → 向导模式等待；已注册 → 注册后路径（Nic→通道→映射恢复）
        if (!_state.State.IsRegistered)
        {
            var url = ClientRegistrationService.WizardUrl(WebPort);
            Log?.Invoke($"未注册：向导模式已就绪 {url}（等待用户完成注册，FR-C-101）");
            if (_options.Interactive) TryOpenBrowser(url);
            _wizard.RegistrationCompleted += result =>
            {
                _ = RunRegisteredPathAsync(_cts.Token);
            };
        }
        else
        {
            AttachPuncher();
            await RunRegisteredPathAsync(ct);
        }
    }

    /// <summary>注册后路径（01 §4.1：Nic → Established → 恢复启用映射）。幂等（向导完成事件与已注册启动共用）。</summary>
    private async Task RunRegisteredPathAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _registeredPathStarted, 1) == 1) return;
        try
        {
            // 虚拟 IP 应用（FR-C-201）：失败降级告警不阻断（05 §1.1；自愈 FR-C-202 属 M2）
            if (IPAddress.TryParse(_state.State.VirtualIp, out var vip))
            {
                // 监听绑定地址切换（01 §3.2）：向导路径冷启动时 VirtualIp 尚空、引擎初值是 Loopback
                // 兜底——不切换则注册后映射监听落 127.0.0.1，与本地同端口服务相撞（A-4 场景）。
                // 先于 Nic 应用：网卡降级不回退绑定（虚拟 IP 已落盘即为本机监听地址）。
                _engine.UpdateVirtualIp(vip);
                try { await _nic.EnsureAsync(vip, CancellationToken.None); }
                catch (Exception e) { Log?.Invoke($"[nic] 虚拟网卡降级运行：{e.Message}（本地 Web 告警展示）"); }
            }
            else Log?.Invoke($"[nic] 虚拟 IP 非法（{_state.State.VirtualIp}），网卡未应用");

            AttachPuncher();

            // 控制通道建立（已注册设备唯一就绪态 = Established；NeedRegister 意味着服务端侧设备已删，交由重连循环）
            while (_control.State != ControlClientState.Established && !ct.IsCancellationRequested)
                await Task.Delay(50, ct);

            // 恢复启用中的映射：启用即打洞（01 §4.1/FR-C-306）；单条失败不连坐其余
            foreach (var m in _state.State.Mappings.Where(m => m.Enabled).ToList())
            {
                try { await _sync.EnableAsync(m.MappingId, CancellationToken.None); }
                catch (Exception e) { Log?.Invoke($"[restore] 映射 {m.Name} 恢复失败：{e.Message}"); }
            }

            // 中继回切周期循环（M2-19，02 §6.2/OQ-7）：relay 态设备对每 60s 重试直连
            _relayRetryLoop = RelayRetryLoopAsync(_cts.Token);

            Log?.Invoke($"注册后路径完成：网卡已应用，映射恢复 {_state.State.Mappings.Count(m => m.Enabled)} 条");
        }
        catch (OperationCanceledException) { /* 停机 */ }
        catch (Exception e) { Log?.Invoke($"[restore] 注册后路径异常：{e.Message}"); }
    }

    /// <summary>真打洞器接线（静态键须已落盘；未注册阶段由占位打洞器兜住，注册完成即接替）。</summary>
    private void AttachPuncher()
    {
        if (_puncher.Attached) return;
        var privateKey = _state.State.StaticPrivateKey;
        if (privateKey is null) return; // 未注册：无静态键（向导完成后事件会再进入）
        var deviceId = _state.State.DeviceId ?? Guid.Empty;
        var deviceSecret = _state.State.DeviceSecret ?? [];
        var stunEp = ResolveStunEndpoint(_control.ServerAddrs.FirstOrDefault());

        _puncher.Attach(new Puncher(
            // OQ-19/TD-20 punchConcurrency 通路（M2-16 打通）：settings → 0x70 上送（发送时点取当前值，
            // 设置页修改对后续打洞会话生效）；服务端 PunchPolicy.Normalize 校验后经 0x71/0x70 Ack 回填
            (targetId, trigger, proto, endpoints, ct) => _control.SendRequestAsync<PunchRequestAck>(new PunchRequest(
                _control.NextSeq(), _control.TimestampMs(), MsgType.PunchRequest,
                targetId, trigger, proto, endpoints,
                PunchConcurrency: (byte?)_settings.Settings.PunchConcurrency), ct),
            (sessionId, endpoints, ct) => _control.SendAsync(new PunchEndpoint(
                _control.NextSeq(), _control.TimestampMs(), MsgType.PunchEndpoint,
                sessionId, endpoints), ct),
            (socket, ct) => stunEp is null
                ? throw new IOException("STUN 地址解析失败（服务端主机名不可解析，TD-07 :3478）")
                : StunProber.ProbeAsync(socket, stunEp, deviceId, deviceSecret, _control.Clock, ct: ct),
            EcKeyPair.FromPrivateKey(privateKey),
            _engine,
            new PunchOptions
            {
                KeepaliveSec = _settings.Settings.KeepaliveSec,
                BindAddress = _options.PunchBindOverride,
            },
            // STUN-TCP 探测缝（M2-04 StunTcpProber，TCP 打洞 M2-16；单事务即关、端口 L 由调用方复用 listen）
            (socket, ct) => stunEp is null
                ? throw new IOException("STUN 地址解析失败（服务端主机名不可解析，TD-07 :3478）")
                : StunTcpProber.ProbeAsync(socket, stunEp, deviceId, deviceSecret, _control.Clock, ct: ct),
            // 本地设备级回退配置缝（M2-23，05 §3.1）：出队执行时与 Ack.relayAllowed 合成（M2-18 消费）
            relayFallbackLookup: _peers.GetRelayFallback,
            // 0x74 中继分配缝（M2-18，02 §6.1①）：打洞 Ack 后失败且回退资格真 → 分配 → JOIN → 中继握手
            relayAllocator: (punchSessionId, token) =>
                RelayClient.AllocateAsync(_control, punchSessionId, token)));
        Log?.Invoke($"打洞器已接线（STUN={stunEp?.ToString() ?? "解析失败"}，派生自控制地址 :3478，TD-07）");
    }

    /// <summary>0x71 PunchInvite：被邀请方即时响应（不进本地队列，02 §5.1③）；成功会话入宿主表（02 §4.5）。
    /// 0x74 RelayGrant（M2-18，02 §6.1② 双方下发）：被邀请侧加入中继并应答 PTP 握手（承载绑定回退路径）。</summary>
    private void OnServerPush(IPcpMessage message)
    {
        switch (message)
        {
            case PunchInvite invite:
                _ = HandleInviteAsync(invite);
                break;
            case RelayGrant grant:
                _ = HandleRelayGrantAsync(grant);
                break;
            case PunchRetry retry: // S→C 0x73（M2-19）：访问方发起侧由请求-应答通道消费不走此路径，
                // 到达此处的必为被邀请方重打预备通知——实际端点交换由随后的 0x71 邀请驱动
                Log?.Invoke($"[punch] 服务端回切通知（session={retry.SessionId}）：预备重打洞（02 §6.2）");
                break;
        }
    }

    private async Task HandleInviteAsync(PunchInvite invite)
    {
        // 服务端合成 relayAllowed（M2-07）为真才可能跟来 0x74——预记邀请上下文供中继关联
        //（Grant 与 RespondAsync 并发到达：先记后应答，成功应答即撤销，M2-18）
        if (invite.RelayAllowed) RememberInvite(invite);
        try
        {
            var outcome = await _scheduler.RespondAsync(invite, _cts.Token);
            if (outcome.Ok && outcome.Session is { IsClosed: false })
            {
                _host.Attach(outcome.Session);
                ForgetInvite(invite.SessionId); // 握手互证成功：发起方不会再走中继
            }
        }
        catch (Exception e) { Log?.Invoke($"[punch] 0x71 处理失败：{e.Message}"); }
    }

    // ── 被邀请侧中继回退（M2-18，02 §4.5/§6.1）──────────────────────

    /// <summary>邀请上下文 TTL：对齐服务端打洞会话台账保留窗（RetainForRelay 120s，M2-07）。</summary>
    private static readonly TimeSpan RelayInviteTtl = TimeSpan.FromSeconds(120);

    /// <summary>等待 A 侧中继 THello1 的预算：覆盖其对端打洞超时 + 承载兜底 + 握手重发窗。</summary>
    private static readonly TimeSpan RelayWaitTimeout = TimeSpan.FromSeconds(45);

    private sealed record RelayInviteContext(Guid PeerDeviceId, byte[] PeerStaticPubKey);

    private readonly Dictionary<Guid, (DateTimeOffset ExpiresAt, RelayInviteContext Ctx)> _relayInvites = new();

    private void RememberInvite(PunchInvite invite)
    {
        var expiresAt = DateTimeOffset.UtcNow + RelayInviteTtl;
        lock (_relayInvites)
        {
            PruneInvitesLocked();
            _relayInvites[invite.SessionId] =
                (expiresAt, new RelayInviteContext(invite.Peer.DeviceId, invite.Peer.StaticPubKey));
        }
    }

    private void ForgetInvite(Guid punchSessionId)
    {
        lock (_relayInvites) _relayInvites.Remove(punchSessionId);
    }

    /// <summary>取出并消费邀请上下文（握手单次语义：重复 THello1 不复用）。</summary>
    private RelayInviteContext? TakeInviteContext(Guid punchSessionId)
    {
        lock (_relayInvites)
        {
            PruneInvitesLocked();
            return _relayInvites.Remove(punchSessionId, out var entry) ? entry.Ctx : null;
        }
    }

    private void PruneInvitesLocked()
    {
        // 常规量级（设备对级）全量清理即可
        foreach (var stale in _relayInvites.Where(kv => kv.Value.ExpiresAt < DateTimeOffset.UtcNow).ToList())
            _relayInvites.Remove(stale.Key);
    }

    /// <summary>被邀请侧 0x74 RelayGrant：JOIN（先 UDP 后 TCP）→ 等 A 的 THello1 → 按 THello1 携带的
    /// 打洞 sessionId 关联邀请上下文（对端身份+静态公钥——Grant 本身不含对端信息，02 §6.1②）→
    /// PTP 握手应答 → 中继承载会话入宿主（映射态 relay 由对端 PunchCompleted 驱动，本端复用检查同样生效）。</summary>
    private async Task HandleRelayGrantAsync(RelayGrant grant)
    {
        var privateKey = _state.State.StaticPrivateKey;
        if (privateKey is null) return; // 未注册无静态键（不会收到，防御）
        RelayTransport? transport = null;
        try
        {
            using var key = EcKeyPair.FromPrivateKey(privateKey);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            cts.CancelAfter(RelayWaitTimeout);
            transport = await RelayClient.JoinWithCarrierFallbackAsync(grant, ct: cts.Token);
            while (true)
            {
                var frame = await transport.ReceiveAsync(cts.Token);
                if (frame is null) return; // relay 会话回收/断连（承载关闭契约）
                if (PtpFrameCodec.ParseHeader(frame).Type != PtpFrameType.THello1) continue; // 迟到无关帧
                RelayInviteContext? ctx;
                try
                {
                    var payload = PtpFrameCodec.ReadHandshakePayload(frame); // THello1 = sessionId(16)|ephA|nonceA
                    ctx = payload.Length < 16 ? null : TakeInviteContext(new Guid(payload.AsSpan(0, 16)));
                }
                catch (ProtocolException) { continue; } // 非法握手帧：跳过继续等
                if (ctx is null)
                {
                    Log?.Invoke("[relay] THello1 打洞会话无邀请上下文（台账 120s 口径外），忽略");
                    continue;
                }
                var session = await TunnelSession.AcceptAsync(ctx.PeerDeviceId, frame, key,
                    ctx.PeerStaticPubKey, transport, _engine,
                    new TunnelSessionOptions
                    {
                        KeepaliveInterval = TimeSpan.FromSeconds(_settings.Settings.KeepaliveSec),
                    });
                transport = null; // 会话接管承载（Disconnect 时释放）
                if (!session.IsClosed)
                {
                    _host.Attach(session);
                    Log?.Invoke($"中继承载建立（被动侧）peer={ctx.PeerDeviceId}");
                }
                return;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !_cts.IsCancellationRequested)
        {
            Log?.Invoke($"[relay] Grant 处理失败：{e.Message}");
        }
        finally
        {
            if (transport is not null) await transport.DisposeAsync(); // 握手未成：释放承载
        }
    }

    // ── 中继回切直连（M2-19，02 §6.2/OQ-7/NET-75）────────────────────

    /// <summary>回切重试周期（PRD 06 §6 定值 60s；测试缝 RelayRetryIntervalOverride）。</summary>
    private static readonly TimeSpan RelayRetryInterval = TimeSpan.FromSeconds(60);

    /// <summary>relay 态设备对周期重试直连：访问方（会话发起侧，IsInitiator）每周期发 0x73 请求协调
    /// （服务端台账/活中继解析 → 双端 0x73 通知；被邀请侧非发起方不触发，防双端同时重打）→
    /// 随后入队全新 0x70 打洞（端点须新鲜，两段式不变）。成功 → 直连会话替换中继（TunnelHost
    /// 排水窗，NET-75）→ 映射 relay→direct；失败且回退资格真 → 再走中继回退（会话对整体替换）。</summary>
    private async Task RelayRetryLoopAsync(CancellationToken ct)
    {
        try
        {
            var interval = _options.RelayRetryIntervalOverride ?? RelayRetryInterval;
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(ct))
            {
                foreach (var session in _host.Sessions.Where(s => s.ViaRelay && !s.IsClosed && s.IsInitiator))
                {
                    // proto 取该设备对 relay 态映射（隧道为设备对级资源，02 §4.5）；无映射兜底 udp
                    var proto = _engine.Snapshots.FirstOrDefault(m =>
                            m.Config.PeerDeviceId == session.PeerDeviceId && m.State == MappingState.Relay)
                        ?.Config.Proto ?? "udp";
                    try
                    {
                        await _control.SendRequestAsync<PunchRetry>(new PunchRetry(
                            _control.NextSeq(), _control.TimestampMs(), MsgType.PunchRetry,
                            session.SessionId), ct);
                    }
                    catch (ControlErrorException e)
                    {
                        // 台账/活中继均已不在册（1001 等）：退化为直接全新 0x70（等效回切路径）
                        Log?.Invoke($"[relay] 回切协调被拒（{e.Code} {e.HttpLikeMsg}）：改走全新打洞");
                    }
                    catch (Exception e)
                    {
                        Log?.Invoke($"[relay] PunchRetry 发送失败：{e.Message}（下周期重试）");
                    }
                    _scheduler.Enqueue(session.PeerDeviceId, null, proto);
                }
            }
        }
        catch (OperationCanceledException) { /* 停机 */ }
    }

    /// <summary>STUN 端点派生：控制地址主机 + 3478（STUN 与控制同宿主，01 §5 TD-07）。</summary>
    private static IPEndPoint? ResolveStunEndpoint(string? hostPort)
    {
        if (string.IsNullOrEmpty(hostPort)) return null;
        var host = ExtractHost(hostPort);
        try
        {
            if (IPAddress.TryParse(host, out var ip)) return new IPEndPoint(ip, 3478);
            var resolved = Dns.GetHostAddressesAsync(host).GetAwaiter().GetResult()
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            return resolved is null ? null : new IPEndPoint(resolved, 3478);
        }
        catch { return null; }
    }

    /// <summary>host:port 取主机（IPv6 方括号形式与 ControlClient.ServerAddrs 同口径）。</summary>
    private static string ExtractHost(string hostPort)
    {
        if (hostPort.StartsWith('['))
        {
            var close = hostPort.IndexOf(']');
            return close > 0 ? hostPort[1..close] : hostPort;
        }
        var last = hostPort.LastIndexOf(':');
        return last >= 0 ? hostPort[..last] : hostPort;
    }

    private static INicManager CreateNic() => OperatingSystem.IsWindows()
        ? new WintunNicManager()
        : OperatingSystem.IsLinux()
            ? new LinuxTunNicManager()
            : throw new PlatformNotSupportedException("虚拟网卡仅支持 Windows/Linux（05 §1）");

    /// <summary>向导引导（FR-C-101）：Windows 开浏览器；Linux 有 DISPLAY 时 xdg-open，否则仅日志。</summary>
    private static void TryOpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            else if (Environment.GetEnvironmentVariable("DISPLAY") is not null)
                System.Diagnostics.Process.Start("xdg-open", url);
        }
        catch { /* 无桌面/无默认浏览器：URL 已入日志 */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _control.ServerPush -= OnServerPush;
        try { await _cts.CancelAsync(); } catch { /* 已取消 */ }
        try { await _app.DisposeAsync(); } catch { /* Kestrel 收尾 */ }
        await _api.DisposeAsync();   // Hub 停推
        await _reporter.DisposeAsync(); // 0x64 停机补报（须先于引擎/控制通道：读快照、走连接）
        await _engine.DisposeAsync(); // 拆监听/关 channel
        await _scheduler.DisposeAsync();
        _puncher.Dispose();
        await _host.DisposeAsync();
        await _control.DisposeAsync();
        // 网卡不随停机拆除：Ensure 幂等（FR-C-201），服务重启场景保留适配器更稳（M1 决策）
        _cts.Dispose();
    }

    /// <summary>打洞器占位（未注册无静态键）：注册完成后 Attach 真件；此前出队即失败兜底。</summary>
    private sealed class LazyPuncher : IPuncher, IDisposable
    {
        private IPuncher? _inner;

        public bool Attached => Volatile.Read(ref _inner) is not null;

        public void Attach(IPuncher puncher) => Volatile.Write(ref _inner, puncher);

        public Task<PunchOutcome> InitiateAsync(Guid targetDeviceId, Guid? triggerMappingId, string proto,
            CancellationToken ct = default)
            => (_inner ?? NotReadyPuncher.Instance).InitiateAsync(targetDeviceId, triggerMappingId, proto, ct);

        public Task<PunchOutcome> RespondAsync(PunchInvite invite, CancellationToken ct = default)
            => (_inner ?? NotReadyPuncher.Instance).RespondAsync(invite, ct);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _inner, null) is Puncher real) real.Dispose();
        }
    }

    private sealed class NotReadyPuncher : IPuncher
    {
        public static readonly NotReadyPuncher Instance = new();

        public Task<PunchOutcome> InitiateAsync(Guid targetDeviceId, Guid? triggerMappingId, string proto,
            CancellationToken ct = default)
            => Task.FromResult(PunchOutcome.Failure(targetDeviceId, "puncher_not_ready"));

        public Task<PunchOutcome> RespondAsync(PunchInvite invite, CancellationToken ct = default)
            => Task.FromResult(PunchOutcome.Failure(invite.Peer.DeviceId, "puncher_not_ready"));
    }
}

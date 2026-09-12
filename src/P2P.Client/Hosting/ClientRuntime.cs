// M1-30 客户端运行时装配（01 §4.1 冷启动序列、01 §3.2 进程模型）：
// 读状态 → [未注册：仅本地 Web 向导模式，等 RegistrationCompleted] /
// [已注册：Nic 应用虚拟 IP（失败降级告警不阻断）→ 通道 Established → 恢复启用中映射（启用即打洞）]。
// Puncher 委托缝在此接 ControlClient（0x70/0x76/STUN，M1-26 注）；0x71 PunchInvite 推送接调度器。
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
}

/// <summary>客户端全组件生命周期（单一属主；启动序=01 §4.1，停机序为其逆序）。</summary>
public sealed class ClientRuntime : IAsyncDisposable
{
    private readonly ClientRuntimeOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private StateStore _state = null!;
    private SettingsStore _settings = null!;
    private ControlClient _control = null!;
    private INicManager _nic = null!;
    private TunnelHost _host = null!;
    private LazyPuncher _puncher = null!;
    private PunchScheduler _scheduler = null!;
    private MappingEngine _engine = null!;
    private MappingSyncService _sync = null!;
    private ClientRegistrationService _wizard = null!;
    private LocalApiServices _api = null!;
    private WebApplication _app = null!;
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
        _engine = new MappingEngine(_host, _scheduler, bindIp); // 监听绑虚拟 IP（01 §3.2）
        _engine.Log += m => Log?.Invoke($"[engine] {m}");
        _sync = new MappingSyncService(_control, _engine, _state);
        _wizard = new ClientRegistrationService(_control, _state, _nic);
        _api = new LocalApiServices(_control, _state, _settings, _wizard, _sync, _scheduler);
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
            // punchConcurrency 通路 M2-16 打通（settings → 0x70 上送）；当前不携带 → 服务端取缺省 3
            (targetId, trigger, proto, endpoints, ct) => _control.SendRequestAsync<PunchRequestAck>(new PunchRequest(
                _control.NextSeq(), _control.TimestampMs(), MsgType.PunchRequest,
                targetId, trigger, proto, endpoints, PunchConcurrency: null), ct),
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
            }));
        Log?.Invoke($"打洞器已接线（STUN={stunEp?.ToString() ?? "解析失败"}，派生自控制地址 :3478，TD-07）");
    }

    /// <summary>0x71 PunchInvite：被邀请方即时响应（不进本地队列，02 §5.1③）；成功会话入宿主表（02 §4.5）。</summary>
    private void OnServerPush(IPcpMessage message)
    {
        if (message is not PunchInvite invite) return;
        _ = HandleInviteAsync(invite);
    }

    private async Task HandleInviteAsync(PunchInvite invite)
    {
        try
        {
            var outcome = await _scheduler.RespondAsync(invite, _cts.Token);
            if (outcome.Ok && outcome.Session is { IsClosed: false })
                _host.Attach(outcome.Session);
        }
        catch (Exception e) { Log?.Invoke($"[punch] 0x71 处理失败：{e.Message}"); }
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

        public Task<PunchOutcome> InitiateAsync(Guid targetDeviceId, Guid? triggerMappingId,
            CancellationToken ct = default)
            => (_inner ?? NotReadyPuncher.Instance).InitiateAsync(targetDeviceId, triggerMappingId, ct);

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

        public Task<PunchOutcome> InitiateAsync(Guid targetDeviceId, Guid? triggerMappingId,
            CancellationToken ct = default)
            => Task.FromResult(PunchOutcome.Failure(targetDeviceId, "puncher_not_ready"));

        public Task<PunchOutcome> RespondAsync(PunchInvite invite, CancellationToken ct = default)
            => Task.FromResult(PunchOutcome.Failure(invite.Peer.DeviceId, "puncher_not_ready"));
    }
}

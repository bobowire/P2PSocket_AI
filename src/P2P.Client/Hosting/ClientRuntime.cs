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
using P2P.Client.Diagnostics;
using P2P.Client.Mapping;
using P2P.Client.Nic;
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

    /// <summary>本机直连候选枚举覆盖缝（§5.4，2026-10-21）：生产 null=Puncher 内置真实枚举
    /// （SubnetSurvey IPv4 滤虚拟网段）；集成测试注入空表保持「跨 NAT 不可直达」前提
    /// （回环别名 fabric 全互通，不注入则必败场景被候选直连击穿）、注入别名本身驱动同网段用例。</summary>
    public Func<IPAddress?, IReadOnlyList<IPAddress>>? LocalIpEnumeratorOverride { get; init; }

    /// <summary>中继回切重试周期覆盖缝（M2-19）：生产 null=60s（PRD 06 §6/OQ-7 定值，
    /// 非用户可调）；集成测试注入秒级缩短以验证周期触发与回切全链。</summary>
    public TimeSpan? RelayRetryIntervalOverride { get; init; }

    /// <summary>0x64 流量上报周期覆盖缝（M2-22）：生产 null=30s（02 §2.4 定值）；
    /// 集成测试注入亚秒级以验证周期落库与停机补报。</summary>
    public TimeSpan? StatsIntervalOverride { get; init; }

    /// <summary>网卡健康探测周期覆盖缝（M2-24）：生产 null=30s（05 §1.2 定值，FR-C-202）；
    /// 集成测试注入亚秒级以验证替身移除→自动重建→恢复沿。</summary>
    public TimeSpan? NicProbeIntervalOverride { get; init; }

    /// <summary>Hello 协议版本覆盖缝（M2-26）：生产 null=当前协议版本；集成测注入越界值
    /// 驱动服务端 5004 拒答，验证升级引导数据链（FR-C-904）。</summary>
    public ushort? ControlProtocolVersionOverride { get; init; }

    /// <summary>网络面枚举源覆盖缝（M3-13，FR-C-204）：生产 null=SubnetSurvey.Snapshot
    ///（真实地址/路由表）；集成测注入可变替身驱动冲突命中→API/WS 可见→解除消失全链。</summary>
    public SubnetConflictDetector.SurveySource? SubnetSurveyOverride { get; init; }

    /// <summary>禁用 0x78 晚探测流程（§5.2⑥ 测试缝）：true=仅旧 0x70 流程（A-3~A-6 场景
    /// 断言旧流程行为——STUN 替身无交换语义）；生产 false=新流程优先、旧流程兜底。</summary>
    public bool DisableLatePunch { get; init; }

    /// <summary>禁用 KCP 承载（M6-04 测试缝，DisableLatePunch 同款；设计 11 §8.3）：true=
    /// Puncher 的 localBearerCaps 缝不装配 → 不上送位、判定恒 false（两态回归用：kcp on/off）；
    /// 生产 false=随 settings.KcpBearer（用户 kill-switch 双层合成）。</summary>
    public bool DisableKcpBearer { get; init; }

    /// <summary>无卡端口映射模式（M8-01，设计 13 §4.1/TD-32）：true=映射监听绑 127.0.0.1——
    /// 生效点两处（引擎构造初值与注册后 <see cref="MappingEngine.UpdateVirtualIp"/> 调用点均传
    /// Loopback），state.VirtualIp 保持服务端下发值不篡改（本地真相与服务端记录一致，未来
    /// VpnService 透明接入可零迁移复用）；展示经 <see cref="MappingEngine.EffectiveBindIp"/> 归真。
    /// 默认 false=桌面行为零变化。Android 壳必开。</summary>
    public bool LoopbackMappingMode { get; init; }

    /// <summary>机器指纹注入缝（M8-01，设计 13 §4.4/TD-34）：Android 6+ 系统 MAC 全设备统一
    /// 02:00:00:00:00:00，默认采集会顶号（OQ-14 覆盖式恢复按 macCode 判身份）；壳注入
    /// ANDROID_ID 派生指纹，经 ClientRegistrationService 流至 MacCodeProvider.FromFingerprint
    ///（注入**优先**——回退位序会被全设备同值 MAC 击穿）。默认 null=平台采集（05 §10 现状）。</summary>
    public Func<string?>? MachineFingerprintOverride { get; init; }

    /// <summary>SPA 物理目录托管缝（M8-02，设计 13 §5.1/TD-35）：null=staticwebassets 现状
    ///（清单探测 + MapStaticAssets，桌面零变化）；非 null=PhysicalFileProvider+UseStaticFiles+
    /// MapFallbackToFile（Android 打包布局不产出清单；目录不存在则告警跳过，仅 API）。</summary>
    public string? SpaRootOverride { get; init; }
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
    private SubnetConflictDetector _subnetConflicts = null!;
    private StunTester _stunTester = null!;
    private DevicePinger _devicePinger = null!;
    private LocalApiServices _api = null!;
    private WebApplication? _app; // M8-02 起 nullable：Web 宿主启动失败降级（null=页面/API 不可用，数据面照常）
    private WebToken _webToken = null!; // M5-12：本地 Web token（07 §14.2，随启动轮换）
    private NicHealthMonitor? _nicMonitor;
    private Task? _punchRetryLoop; // M7-03 更名（原 _relayRetryLoop）：中继回切+Failed 重试统一周期循环
    private readonly SemaphoreSlim _restoreMutex = new(1, 1); // 启用映射恢复重入闸（启动与登录补发并发防扰）
    private int _registeredPathStarted;
    private int _disposed;
    private int _upgradeBrowserOpened; // 版本拒答浏览器引导去重（每进程一次，M2-26）

    /// <summary>诊断日志（Program 接 Serilog）。</summary>
    public event Action<string>? Log;

    /// <summary>升级引导触发（M2-26，FR-C-904）：版本拒答取回升级信息即发——浏览器打开
    /// 仅 Interactive 且每进程一次，本事件每轮拒答均发（测试/前端数据链断言点）。</summary>
    public event Action<UpdateInfoResponse>? UpgradeRequired;

    /// <summary>本地 API 服务束（启动后可用；测试探查/宿主诊断）。</summary>
    public LocalApiServices Api => _api;

    /// <summary>本地 Web 实际监听端口（settings.localWebPort）。</summary>
    public int WebPort { get; private set; }

    /// <summary>本地 Web token 现值（M5-12，07 §14.2）：in-proc 诊断/集成注入用；随启动轮换。</summary>
    public string WebToken => _webToken.Value;

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
        _control = new ControlClient(addrs,
            new ControlClientOptions { ProtocolVersionOverride = _options.ControlProtocolVersionOverride },
            deviceId: _state.State.DeviceId, deviceSecret: _state.State.DeviceSecret);
        _control.Log += m => Log?.Invoke($"[control] {m}");
        _control.UpgradeRequired += OnUpgradeRequired; // 5004 拒答 → 升级引导（M2-26）
        _nic = _options.NicOverride ?? CreateNic();
        _nic.Degraded += m => Log?.Invoke($"[nic] 降级告警：{m}");
        _host = new TunnelHost();
        _host.SessionLog += (s, m) => Log?.Invoke($"[tunnel {s.PeerDeviceId.ToString()[..8]}{(s.ViaRelay ? "/relay" : "")}] {m}");
        _puncher = new LazyPuncher();
        // M7-02 生产装配（设计 12 §4.3）：planner 注入——打洞协议出队时点按设备级 punchMode 解析
        //（PeersStore.GetPunchMode 发送时点取现值，设备页修改对后续打洞会话即时生效）
        _scheduler = new PunchScheduler(_puncher, new PunchModePlanner(_peers.GetPunchMode));
        _scheduler.Log += m => Log?.Invoke($"[punch] {m}"); // 打洞完成四型日志——M2_38 排查曾因未接线被误导为"调度未返回"
        var bindIp = _options.LoopbackMappingMode ? IPAddress.Loopback // M8-01 生效点①（设计 13 §4.1）
            : IPAddress.TryParse(_state.State.VirtualIp, out var vip) ? vip : IPAddress.Loopback;
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
        _wizard = new ClientRegistrationService(_control, _state, _nic, new RegistrationOptions
        {
            MachineFingerprint = _options.MachineFingerprintOverride, // M8-01 指纹注入缝（设计 13 §4.4）
        });
        // 网段冲突检测（M3-13，FR-C-204）：枚举源=平台快照（测试缝可注入替身）
        _subnetConflicts = new SubnetConflictDetector(_options.SubnetSurveyOverride ?? P2P.Nic.SubnetSurvey.Snapshot);
        _subnetConflicts.Log += m => Log?.Invoke(m);
        // stun-test 判型（M3-15，05 §7.2）：端点=TD-07 派生（UDP/TCP 同宿主同端口 3478），凭据/端点
        // 委托惰性取——装配时设备可能未注册（调用期 state 已就绪）；ClockSync 未校准 offset=0 可用（OQ-12 ±120s 窗）
        _stunTester = new StunTester(
            () => ResolveStunEndpoint(_control.ServerAddrs.FirstOrDefault()),
            () => ResolveStunEndpoint(_control.ServerAddrs.FirstOrDefault()),
            () => _state.State.DeviceId is { } id && _state.State.DeviceSecret is { } secret
                ? new StunCredentials(id, secret, _control.Clock)
                : null);
        // ping-device（M3-16，04 §2.6）：设备列表=0x40 可见列表分页拉全量（同 /api/devices 口径），
        // 隧道=TunnelHost 设备对现查（02 §4.5 隧道是设备对级资源——IsClosed 兜底防已亡会话误判可用）
        _devicePinger = new DevicePinger(
            FetchVisibleDevicesAsync,
            peerId => _host.Get(peerId) is { IsClosed: false } session
                ? new DeviceTunnel(session.ViaRelay, session.PingAsync)
                : null);
        _api = new LocalApiServices(_control, _state, _settings, _peers, _wizard, _sync, _scheduler,
            _lanSegments, Path.Combine(_options.BaseDir, "logs"), _subnetConflicts, _stunTester,
            _devicePinger, _host, PeerLabelLookup,
            effectiveVirtualIp: _options.LoopbackMappingMode
                ? () => _engine.EffectiveBindIp.ToString()
                : null); // M2-27：白名单镜像 + 日志目录同源；M3-14 诊断区（tunnels/rekey 走本地隧道表）；
        // M8-01 展示归真缝（设计 13 §4.3）：无卡模式=引擎生效绑定地址（恒 127.0.0.1）；
        // 桌面 null=API 读 state.VirtualIp 现状零变化
        _control.ServerPush += OnServerPush; // 0x71 PunchInvite → 被邀请方打洞（02 §5.1③）
        _control.SessionReestablished += () => _ = OnSessionReestablishedAsync(); // 断线重连沿：登录重放+failed 映射兜底（缺陷批次 2 修 A/C）
        _wizard.LoginSucceeded += username => // P3 修复：登录后可见性就位，补发启用映射恢复
        {   //（即发即忘：不阻塞登录请求；恢复内部逐条容错，可见性未及生效单条失败仅日志）
            _api.Context.LoginUser = username; // P2 方案 a：web/向导/自动重放三路径统一 /api/auth/me 口径
            Log?.Invoke($"[restore] 登录成功（{username}）：补发启用映射恢复");
            _ = Task.Run(async () =>
            {
                try { await RestoreEnabledMappingsAsync("login"); }
                catch (OperationCanceledException) { /* 停机 */ }
                catch (Exception e) { Log?.Invoke($"[restore] 登录补发恢复异常：{e.Message}"); }
            });
        };

        // ③ 本地 Web（两种分支都启：向导也经它完成注册）
        WebPort = _settings.Settings.LocalWebPort;
        _webToken = new WebToken(_options.BaseDir); // M5-12：每次启动生成（重启即轮换，07 §14.2）
        // M8-02 Web 宿主降级（设计 13 §5.2）：构建/启动失败（Kestrel on Android R1、桌面端口占用）
        // 不拖垮数据面——ERROR 日志 + 页面/WS/API 不可用，隧道/打洞/映射监听照常运行。
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls($"http://127.0.0.1:{WebPort}"); // TD-12 仅本机回环
            _app = builder.Build();
            _app.UseWebSockets();
            if (_settings.Settings.WebTokenAuth) // M5-12：/api/* 与 /ws/status 校验（07 §14.2）——webTokenAuth=false 免鉴权（可配置化，重启生效）
                _app.UseWebTokenAuth(_webToken.Value);
            _app.MapLocalApi(_api);
            // SPA 同端口静态托管（04 §0「SPA 由同端口静态托管」、08 §2 NFR-31）：wwwroot 经
            // MapStaticAssets 服务（读 staticwebassets.endpoints.json，含 .br/.gz 预压缩直发）；
            // MapFallbackToFile 兜底 SPA 路由（/wizard、/login 等均回 index.html 由前端路由接管）。
            // 挂载顺序：API 端点在前，fallback 最后兜底，不遮蔽 /api/*。
            // 清单随发布产物存在才挂——集成测试宿主（testhost）无清单会抛
            // InvalidOperationException（MapStaticAssets 按 {宿主程序集}.staticwebassets 解析），跳过仅 API。
            var spaManifest = Path.Combine(AppContext.BaseDirectory,
                $"{typeof(ClientRuntime).Assembly.GetName().Name}.staticwebassets.endpoints.json");
            if (_options.SpaRootOverride is { } spaDir) // M8-02 物理目录托管（设计 13 §5.1/TD-35）：Android 打包布局不产出 staticwebassets 清单
            {
                if (Directory.Exists(spaDir))
                {
                    // 无预压缩——设备本地回环加载无意义（设计 13 §5.1）；静态资源不经 token 校验
                    //（与 MapStaticAssets 路径同口径：保护面=API/WS，07 §14.2）
                    var spa = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(spaDir);
                    _app.UseStaticFiles(new StaticFileOptions { FileProvider = spa });
                    _app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = spa });
                }
                else Log?.Invoke($"[web] SpaRootOverride 目录不存在（{spaDir}）：SPA 不托管（仅 API），数据面照常");
            }
            else if (File.Exists(spaManifest))
            {
                _app.MapStaticAssets(spaManifest);
                _app.MapFallbackToFile("index.html");
            }
            await _app.StartAsync(ct);
        }
        catch (Exception e)
        {
            Log?.Invoke($"[web] 本地 Web 宿主启动失败：{e.Message}——页面/WS/API 不可用，" +
                        "隧道、打洞与映射监听照常运行（设计 13 §5.2 降级，08 §5.2）");
            if (_app is not null) { try { await _app.DisposeAsync(); } catch { /* 收尾尽力而为 */ } }
            _app = null;
        }

        // ④ 分支：未注册 → 向导模式等待；已注册 → 注册后路径（Nic→通道→映射恢复）
        if (!_state.State.IsRegistered)
        {
            var url = ClientRegistrationService.WizardUrl(WebPort, _webToken.Value); // M5-12：自动打开携带 token（07 §14.2）
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

    /// <summary>版本拒答升级引导（M2-26，FR-C-904/OQ-5——手动升级，不自动拉包）：日志 + 事件
    ///（数据链断言点）+ 自动开浏览器 /upgrade?reason=version（沿 M1-24 机制：仅 Interactive，
    /// 服务模式仅日志；版本不符期间每轮重连都拒答，浏览器按进程去重防轰炸）。</summary>
    private void OnUpgradeRequired(UpdateInfoResponse info)
    {
        Log?.Invoke($"[control] 升级引导：服务端要求 {info.LatestVersion}"
                    + $"（协议 {info.MinProtocol}~{info.MaxProtocol}，本机 {ProtocolVersion.Current}），"
                    + $"指引 {info.UpgradeUrl}（FR-C-904）");
        UpgradeRequired?.Invoke(info);
        if (_options.Interactive && Interlocked.Exchange(ref _upgradeBrowserOpened, 1) == 0)
            TryOpenBrowser($"http://127.0.0.1:{_settings.Settings.LocalWebPort}/upgrade?reason=version&token={_webToken.Value}"); // M5-12：携带 token（07 §14.2）
    }

    /// <summary>注册后路径（01 §4.1：Nic → Established → 恢复启用映射）。幂等（向导完成事件与已注册启动共用）。</summary>
    private async Task RunRegisteredPathAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _registeredPathStarted, 1) == 1) return;
        try
        {
            // 虚拟 IP 应用（FR-C-201）：失败降级告警不阻断（05 §1.1；自愈 FR-C-202 属 M2-24 自愈循环兜底）
            if (IPAddress.TryParse(_state.State.VirtualIp, out var vip))
            {
                // 监听绑定地址切换（01 §3.2）：向导路径冷启动时 VirtualIp 尚空、引擎初值是 Loopback
                // 兜底——不切换则注册后映射监听落 127.0.0.1，与本地同端口服务相撞（A-4 场景）。
                // 先于 Nic 应用：网卡降级不回退绑定（虚拟 IP 已落盘即为本机监听地址）。
                // M8-01 生效点②（设计 13 §4.1）：无卡模式恒传 Loopback——state.VirtualIp 不篡改，
                // 本地真相与服务端记录一致；冲突检测随之按生效地址推导（127.0.0.0/24 无误报）。
                _engine.UpdateVirtualIp(_options.LoopbackMappingMode ? IPAddress.Loopback : vip);
                try { _subnetConflicts.OnNicApplied((await _nic.EnsureAsync(vip, CancellationToken.None)).AdapterName); }
                catch (Exception e)
                {
                    _subnetConflicts.OnNicApplied(null); // 未应用：不排除任何接口（枚举中通常亦无自身地址）
                    Log?.Invoke($"[nic] 虚拟网卡降级运行：{e.Message}（本地 Web 告警展示）");
                }

                // 冲突检测首查（M3-13，05 §1.4/FR-C-204）：Ensure 后主动跑一次（驱动日志+WS 提示）；
                // 后续外部变化/解除由 /api/system/state 每请求现场重算反映（无本机事件可听）
                _subnetConflicts.Check(_engine.EffectiveBindIp.ToString(), trigger: "注册后路径首查");

                // 网卡自愈循环（M2-24，FR-C-202/05 §1.2）：30s 探测存在性与 IP 一致性，异常重建恢复。
                // 启动 Ensure 失败同样纳入（首探测即重建尝试）；恢复沿立即重试 listen_failed 映射
                //（引擎侧另有周期兜底，M1 附录 A.4 重启竞态收口——不再需要手工 retry）。
                _nicMonitor = new NicHealthMonitor(_nic, vip, _options.NicProbeIntervalOverride);
                _nicMonitor.Log += m => Log?.Invoke(m);
                _nicMonitor.Restored += () => _ = Task.Run(async () =>
                {
                    _subnetConflicts.Check(_engine.EffectiveBindIp.ToString(), trigger: "网卡恢复沿重查"); // 重建=地址/路由重新应用，重查冲突（M8-01 起按生效地址）
                    try { await _engine.RetryListenFailedAsync(); }
                    catch (Exception e) when (e is ObjectDisposedException or OperationCanceledException)
                    { /* 停机竞态：引擎已释放 */ }
                    catch (Exception e) { Log?.Invoke($"[engine] 恢复沿监听重试失败：{e.Message}"); }
                });
            }
            else Log?.Invoke($"[nic] 虚拟 IP 非法（{_state.State.VirtualIp}），网卡未应用");

            AttachPuncher();

            // 控制通道建立（已注册设备唯一就绪态 = Established；NeedRegister 意味着服务端侧设备已删，交由重连循环）
            while (_control.State != ControlClientState.Established && !ct.IsCancellationRequested)
                await Task.Delay(50, ct);

            // P2 方案 a（2026-10-11 批复）：记住的凭据自动重放登录——登录态跨重启保持。
            // 登录成功经 LoginSucceeded 事件统一驱动（web 会话用户名+映射补恢复），故先于 startup 恢复。
            // 失败策略：密码已失效（Ack.Ok=false）即清除凭据（重启后须手动登录一次）；
            // 发送异常（通道竞态）保留凭据下次启动再试。
            if (_state.State.RememberedUsername is { } rememberedUser
                && _state.State.RememberedPassword is { } rememberedPassword)
            {
                try
                {
                    var ack = await _wizard.LoginBindAsync(rememberedUser, rememberedPassword, ct);
                    if (ack.Ok) Log?.Invoke($"[auth] 已自动登录（{rememberedUser}，记住的凭据，P2 方案 a）");
                    else
                    {
                        await _wizard.ForgetCredentialsAsync(CancellationToken.None);
                        Log?.Invoke($"[auth] 记住的凭据登录失败（密码已变更或账号不可用），已清除——请手动登录");
                    }
                }
                catch (OperationCanceledException) { /* 停机 */ }
                catch (Exception e)
                {
                    Log?.Invoke($"[auth] 自动登录异常（{e.Message}）：凭据保留，下次启动重试");
                }
            }

            // 恢复启用中的映射：启用即打洞（01 §4.1/FR-C-306）；单条失败不连坐其余
            //（登录后可见性就位时经 LoginSucceeded 补发重入，P3 修复）
            await RestoreEnabledMappingsAsync("startup");

            // 打洞周期循环（M2-19 中继回切 + M7-03 Failed 周期重试，设计 12 §2.3）：60s 周期统一驱动
            _punchRetryLoop = PunchRetryLoopAsync(_cts.Token);

            Log?.Invoke($"注册后路径完成：网卡已应用，映射恢复 {_state.State.Mappings.Count(m => m.Enabled)} 条");
        }
        catch (OperationCanceledException) { /* 停机 */ }
        catch (Exception e) { Log?.Invoke($"[restore] 注册后路径异常：{e.Message}"); }
    }

    /// <summary>启用映射恢复（P3 修复，可重入）：仅补引擎快照缺失或 invalid 的启用映射——
    /// 运行态在位（punching/direct/relay/failed）不扰（failed 有自身重试语义，04 §2.5）；
    /// 引擎 <see cref="MappingEngine.EnableAsync"/> 对无监听旧运行时先拆后建，重复启用安全。
    /// 入口：冷启动（startup）与登录成功补发（login——启动时未登录，无共同分组目标按
    /// 可见性判不可见而失败，登录后会话 OwnerUserId 就位即可见，须补一次）。
    /// 信号量闸并发（启动与登录补发同时到达时串行；第二次扫描见运行态已补齐自然空转）；
    /// 单条失败不连坐其余。</summary>
    private async Task RestoreEnabledMappingsAsync(string trigger)
    {
        if (Volatile.Read(ref _disposed) == 1) return;
        await _restoreMutex.WaitAsync();
        try
        {
            var engineStates = _engine.Snapshots.ToDictionary(s => s.Config.MappingId);
            foreach (var m in _state.State.Mappings.Where(m => m.Enabled).ToList())
            {
                try
                {
                    var snap = engineStates.GetValueOrDefault(m.MappingId);
                    if (snap is not null && snap.State != MappingState.Invalid) continue; // 运行态在位
                    await _sync.EnableAsync(m.MappingId, CancellationToken.None);
                }
                catch (Exception e) { Log?.Invoke($"[restore] 映射 {m.Name} 恢复失败（{trigger}）：{e.Message}"); }
            }
        }
        finally { _restoreMutex.Release(); }
    }

    /// <summary>重连沿补偿（缺陷批次 2 修 A，P2 方案 a 的重连对等修复）：新会话就绪且非首连——
    /// 有记住的凭据即重放登录（<see cref="ClientRegistrationService.LoginSucceeded"/> 统一驱动
    /// LoginUser 口径+映射补恢复，与冷启动同款语义）；凭据被拒（密码已变更/账号不可用）清凭据+
    /// 清本地登录态；发送异常保留凭据下次重连再试。无凭据而 UI 仍残留登录态（会话级登录已随
    /// 断线丢失）则如实清除广播，防"客户端显示已登录、服务端实际未登录"失真（TD-16：事件仅
    /// 提示，真相由前端 refetch /api/auth/me）。</summary>
    private async Task OnSessionReestablishedAsync()
    {
        try
        {
            if (_state.State.RememberedUsername is { } rememberedUser
                && _state.State.RememberedPassword is { } rememberedPassword)
            {
                var ack = await _wizard.LoginBindAsync(rememberedUser, rememberedPassword, _cts.Token);
                if (ack.Ok) Log?.Invoke($"[auth] 重连后已自动重放登录（{rememberedUser}，记住的凭据）");
                else
                {
                    await _wizard.ForgetCredentialsAsync(CancellationToken.None);
                    Log?.Invoke($"[auth] 重连登录重放被拒（密码已变更或账号不可用），凭据已清除——请手动登录");
                    ClearUiLoginState();
                }
            }
            else if (_api.Context.LoginUser is not null)
                ClearUiLoginState(); // 无凭据+UI 残留：服务端新会话实际未登录

            await RetryFailedMappingsAsync("reconnect");
        }
        catch (OperationCanceledException) { /* 停机 */ }
        catch (Exception e)
        {
            Log?.Invoke($"[auth] 重连登录重放异常（{e.Message}）：凭据保留，下次重连再试");
            if (_api.Context.LoginUser is not null) ClearUiLoginState(); // 登录态未知：按未登录显示
        }
    }

    /// <summary>清本地 web 登录态并广播 login_state（载荷与 LocalWebApi 能力变迁广播同格式）。</summary>
    private void ClearUiLoginState()
    {
        _api.Context.LoginUser = null;
        _api.Hub.Publish(new { ev = WsEventNames.LoginState, mode = "passive" });
    }

    /// <summary>failed 映射兜底（缺陷批次 2 修 C）：断线窗口内的重打常因控制通道未就绪/对端亦在
    /// 重连（4005）一次判死，而 Failed 态无自动重试。会话重建沿补一轮——引擎 <see
    /// cref="MappingEngine.RetryAsync"/> 幂等：failed 有监听重入打洞队列、无监听走先拆后建全量
    /// 重启用、非 failed 态 no-op。置于登录重放之后（同分组可见性不依赖登录，重放失败亦重试）。</summary>
    private async Task RetryFailedMappingsAsync(string trigger)
    {
        var failed = _engine.Snapshots.Where(s => s.State == MappingState.Failed).ToList();
        if (failed.Count > 0)
            Log?.Invoke($"[restore] 会话重建沿补试 failed 映射 {failed.Count} 条（{trigger}）");
        foreach (var snap in failed)
        {
            try { await _engine.RetryAsync(snap.Config.MappingId); }
            catch (Exception e) { Log?.Invoke($"[restore] 映射 {snap.Config.Name} 重连重试失败（{trigger}）：{e.Message}"); }
        }
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
            (targetId, trigger, proto, endpoints, localCandidates, bearerCaps, ct) => _control.SendRequestAsync<PunchRequestAck>(seq => new PunchRequest(
                seq, _control.TimestampMs(), MsgType.PunchRequest,
                targetId, trigger, proto, endpoints,
                PunchConcurrency: (byte?)_settings.Settings.PunchConcurrency,
                LocalCandidates: localCandidates,
                BearerCaps: bearerCaps), ct),
            (sessionId, endpoints, localCandidates, bearerCaps, ct) => _control.SendAsync(seq => new PunchEndpoint(
                seq, _control.TimestampMs(), MsgType.PunchEndpoint,
                sessionId, endpoints,
                LocalCandidates: localCandidates,
                BearerCaps: bearerCaps), ct),
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
                RelayClient.AllocateAsync(_control, punchSessionId, token),
            // 活中继会话复用缝（M2-37，05 §4）：回切周期重打失败再回退时复用 TunnelHost 表内会话，
            // 不再新分配整体替换（排水窗杀传输中 channel——公网实测 60s 周期下网页长传输必断）
            liveRelaySessionLookup: peerId =>
                _host.Get(peerId) is { ViaRelay: true, IsClosed: false } reused ? reused : null,
            // TCP 打洞诊断日志缝（P4 修复）：计划线+池收尾线——结果线由调度器既有四型日志承载
            log: m => Log?.Invoke($"[punch-tcp] {m}"),
            // 本机直连候选枚举缝（§5.4）：生产缺省真实枚举；NatSimulator 场景注入空表/别名
            localIpEnumerator: _options.LocalIpEnumeratorOverride,
            // 0x77 齐射锚点等待缝（§5.2⑤ 方案 A）：B 侧打洞齐射等服务器锚点（与 A 的 Ack 同刻下发）
            // 后与 A 各倒数同一 Δ 到点齐射；超时（旧服务端）兜底立即开火
            punchFireWaiter: (sessionId, timeout, token) =>
                _control.WaitForPunchFireAsync(sessionId, timeout, token),
            // 0x78 起跑缝（§5.2⑥ 晚探测流程）：N 随 settings 当前值（同 0x70 口径）；
            // DisableLatePunch 测试缝=true 时省略——Puncher 编排器判空即整体走旧流程
            punchStartSender: _options.DisableLatePunch ? null
                : (targetId, trigger, proto, token) =>
                    _control.SendRequestAsync<PunchStartGo>(seq => new PunchStart(seq, _control.TimestampMs(),
                        MsgType.PunchStart, targetId, trigger, proto,
                        PunchConcurrency: (byte?)_settings.Settings.PunchConcurrency), token),
            // STUN-TCP 交换缝（§5.2⑥）：预绑 L 的未连接句柄进、保持打开出（映射钉活，Puncher 管生命周期）
            tcpExchange: _options.DisableLatePunch ? null
                : (socket, sessionId, candidates, token) => stunEp is null
                    ? throw new IOException("STUN 地址解析失败（服务端主机名不可解析，TD-07 :3478）")
                    : StunTcpProber.ProbeExchangeAsync(socket, stunEp, deviceId, deviceSecret, _control.Clock,
                        sessionId, candidates, ct: token),
            // 本端承载能力位图缝（M6-04，设计 11 §8.2/§8.3）：DisableKcpBearer 测试缝 OR 用户
            // kill-switch（settings.KcpBearer）任一关闭 → 缝不装配=不上送位、判定恒 false（裸 UDP）
            localBearerCaps: _options.DisableKcpBearer ? null
                : () => _settings.Settings.KcpBearer ? BearerCaps.Kcp : 0u));
        Log?.Invoke($"打洞器已接线（STUN={stunEp?.ToString() ?? "解析失败"}，派生自控制地址 :3478，TD-07）");
    }

    /// <summary>0x71 PunchInvite：被邀请方即时响应（不进本地队列，02 §5.1③）；成功会话入宿主表（02 §4.5）。
    /// 0x74 RelayGrant（M2-18，02 §6.1② 双方下发）：被邀请侧加入中继并应答 PTP 握手（承载绑定回退路径）。
    /// 0x41 DeviceListUpdate（M2-15，TD-16）：提示事件→前端 refetch，本地不缓存设备列表。
    /// 0x75 Invalidation（M2-15，05 §4）：受影响映射 MarkInvalid（停转发置 invalid）；
    /// newCapability 降级已在 ControlClient 内联（连接级状态）→ CapabilityChanged → WS login_state。</summary>
    private void OnServerPush(IPcpMessage message)
    {
        switch (message)
        {
            case PunchInvite invite:
                _ = HandleInviteAsync(invite);
                break;
            case PunchStartGo go: // 0x78 起跑推送（02 §5.2⑥，晚探测流程 B 侧）
                _ = HandleGoAsync(go);
                break;
            case RelayGrant grant:
                _ = HandleRelayGrantAsync(grant);
                break;
            case PunchRetry retry: // S→C 0x73（M2-19）：访问方发起侧由请求-应答通道消费不走此路径，
                // 到达此处的必为被邀请方重打预备通知——实际端点交换由随后的 0x71 邀请驱动
                Log?.Invoke($"[punch] 服务端回切通知（session={retry.SessionId}）：预备重打洞（02 §6.2）");
                break;
            case DeviceListUpdate: // 0x41 无载荷提示帧：广播 device_list（04 §2.8）
                _api.Hub.Publish(new { ev = WsEventNames.DeviceList });
                break;
            case Invalidation invalidation:
                _ = HandleInvalidationAsync(invalidation);
                break;
        }
    }

    /// <summary>0x75 失效推送处理（M2-15，05 §4）：逐映射 MarkInvalid——拆监听/关 channel 停转发、
    /// 置 invalid 态（mapping_state 事件经引擎状态机既有接线广播，前端置灰）。
    /// 幂等：未启用/已拆映射 TryGetValue 落空即跳过；disabled 映射服务端本就不入集（M2-12 口径）。</summary>
    private async Task HandleInvalidationAsync(Invalidation invalidation)
    {
        foreach (var mappingId in invalidation.AffectedMappingIds)
        {
            try { await _engine.MarkInvalidAsync(mappingId, $"invalidated:{invalidation.Reason}"); }
            catch (Exception e) { Log?.Invoke($"[engine] 0x75 映射 {mappingId} 失效处理失败：{e.Message}"); }
        }
        if (invalidation.AffectedMappingIds.Length > 0)
            Log?.Invoke($"[control] 0x75 失效推送（reason={invalidation.Reason}，" +
                        $"{invalidation.AffectedMappingIds.Length} 条映射置 invalid）");
    }

    private async Task HandleInviteAsync(PunchInvite invite)
    {
        // 服务端合成 relayAllowed（M2-07）为真才可能跟来 0x74——预记邀请上下文供中继关联
        //（Grant 与 RespondAsync 并发到达：先记后应答，成功应答即撤销，M2-18）
        if (invite.RelayAllowed) RememberInvite(invite);
        try
        {
            var outcome = await _scheduler.RespondAsync(invite, _cts.Token);
            // 被邀请侧结果线（M7-04，设计 12 §6.2）：与 A 侧四型日志同口径携带 proto
            Log?.Invoke($"[punch] 被邀请打洞完成 peer={outcome.PeerDeviceId} proto={outcome.Proto} " +
                        $"ok={outcome.Ok}{(outcome.Ok ? "" : $"：{outcome.FailReason}")}（0x71 会话）");
            if (outcome.Ok && outcome.Session is { IsClosed: false })
            {
                _host.Attach(outcome.Session);
                ForgetInvite(invite.SessionId); // 握手互证成功：发起方不会再走中继
            }
        }
        catch (Exception e) { Log?.Invoke($"[punch] 0x71 处理失败：{e.Message}"); }
    }

    /// <summary>0x78 PunchStartGo（02 §5.2⑥ 晚探测流程 B 侧）：同 0x71 口径——relayAllowed 真预记
    /// 中继关联上下文，成功会话入宿主即撤销。</summary>
    private async Task HandleGoAsync(PunchStartGo go)
    {
        if (go.RelayAllowed)
            lock (_relayInvites)
            {
                PruneInvitesLocked();
                _relayInvites[go.SessionId] = (DateTimeOffset.UtcNow + RelayInviteTtl,
                    new RelayInviteContext(go.Peer.DeviceId, go.Peer.StaticPubKey));
            }
        try
        {
            var outcome = await _scheduler.RespondGoAsync(go, _cts.Token);
            // 被邀请侧结果线（M7-04，设计 12 §6.2）：与 0x71 同口径
            Log?.Invoke($"[punch] 被邀请打洞完成 peer={outcome.PeerDeviceId} proto={outcome.Proto} " +
                        $"ok={outcome.Ok}{(outcome.Ok ? "" : $"：{outcome.FailReason}")}（0x78 Go 会话）");
            if (outcome.Ok && outcome.Session is { IsClosed: false })
            {
                _host.Attach(outcome.Session);
                ForgetInvite(go.SessionId);
            }
        }
        catch (Exception e) { Log?.Invoke($"[punch] 0x78 Go 处理失败：{e.Message}"); }
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

    // ── 打洞周期循环（M2-19 中继回切 + M7-03 Failed 周期重试，02 §6.2/OQ-7/设计 12 §2.3/§5.3）──

    /// <summary>回切重试周期（PRD 06 §6 定值 60s；测试缝 RelayRetryIntervalOverride——名不变）。</summary>
    private static readonly TimeSpan RelayRetryInterval = TimeSpan.FromSeconds(60);

    /// <summary>打洞周期循环（原中继回切循环更名扩容，设计 12 §5.3）：每 tick ①中继回切——
    /// relay 态设备对重试直连：访问方（会话发起侧，IsInitiator）每周期发 0x73 请求协调（服务端
    /// 台账/活中继解析 → 双端 0x73 通知；被邀请侧非发起方不触发，防双端同时重打）→ 随后
    /// EnqueueRetry 入队全新 0x70 打洞（端点须新鲜，两段式不变；协议交由 planner 交替——以设备级
    /// 优先模式为起点 TCP/KCP 交替，单一形态不可达的网络下另一形态仍有周期性机会，设计 12 决策②）。
    /// 成功 → 直连会话替换中继（TunnelHost 排水窗，NET-75）→ 映射 relay→direct；失败且回退资格真 →
    /// 设备对活中继会话在位则直接复用保持现状（M2-37），会话已亡才全新分配重建。
    /// ②Failed 态周期重试（M7-03，设计 12 §2.3）：未回退中继的失败映射同 tick 补一轮打洞重试
    ///（仅重排打洞不拆建；无监听失败归 M2-24 监听重试集）——7×24 无人值守自愈。</summary>
    private async Task PunchRetryLoopAsync(CancellationToken ct)
    {
        try
        {
            var interval = _options.RelayRetryIntervalOverride ?? RelayRetryInterval;
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(ct))
            {
                // ① 中继回切（现状逻辑不变；proto 计算三行删除——协议交由 planner 交替，设计 12 §5.3）
                foreach (var session in _host.Sessions.Where(s => s.ViaRelay && !s.IsClosed && s.IsInitiator))
                {
                    try
                    {
                        await _control.SendRequestAsync<PunchRetry>(seq => new PunchRetry(
                            seq, _control.TimestampMs(), MsgType.PunchRetry,
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
                    _scheduler.EnqueueRetry(session.PeerDeviceId);
                }

                // ② Failed 态周期重试（设计 12 §2.3 新增）：仅重排打洞；非 Failed/无监听在引擎内 no-op
                foreach (var snap in _engine.Snapshots.Where(s => s.State == MappingState.Failed).ToList())
                    _ = _engine.RetryPunchAsync(snap.Config.MappingId);
            }
        }
        catch (OperationCanceledException) { /* 停机 */ }
    }

    /// <summary>0x40 可见设备列表分页拉全量（OQ-16，DeviceApi 同口径）——ping-device 远程码解析用。</summary>
    private async Task<IReadOnlyList<DeviceListItem>> FetchVisibleDevicesAsync(CancellationToken ct)
    {
        var items = new List<DeviceListItem>();
        uint offset = 0;
        while (true)
        {
            var resp = await _control.SendRequestAsync<DeviceListResponse>(seq => new DeviceListRequest(
                seq, _control.TimestampMs(), MsgType.DeviceList, offset, 100), ct);
            items.AddRange(resp.Items);
            if (!resp.HasMore) break;
            offset += (uint)resp.Items.Length;
        }
        return items;
    }

    /// <summary>隧道列表显示名（M3-14 诊断区）：引擎运行时映射（PeerDeviceId）→ MappingId →
    /// state.json 反查目标远程码——纯本地数据（passive 亦可达，不依赖 0x40）；无映射指向该
    /// 设备对时 null（前端回退显示 peerId 短码）。</summary>
    private string? PeerLabelLookup(Guid peerDeviceId)
    {
        var mappingId = _engine.Snapshots
            .FirstOrDefault(m => m.Config.PeerDeviceId == peerDeviceId)?.Config.MappingId;
        return mappingId is { } id
            ? _state.State.Mappings.FirstOrDefault(x => x.MappingId == id)?.TargetRemoteCode
            : null;
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

    private static INicManager CreateNic() => NicManagerFactory.Create(); // M8-01 收敛为调工厂（设计 13 §4.2：消除重复 + Android NoopNicManager 分支）

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
        if (_nicMonitor is not null) await _nicMonitor.DisposeAsync(); // 先停自愈循环（其恢复沿会驱动引擎）
        if (_app is not null) { try { await _app.DisposeAsync(); } catch { /* Kestrel 收尾（降级态 null=未启） */ } }
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

        public Task<PunchOutcome> RespondGoAsync(PunchStartGo go, CancellationToken ct = default)
            => (_inner ?? NotReadyPuncher.Instance).RespondGoAsync(go, ct);

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

        public Task<PunchOutcome> RespondGoAsync(PunchStartGo go, CancellationToken ct = default)
            => Task.FromResult(PunchOutcome.Failure(go.Peer.DeviceId, "puncher_not_ready"));
    }
}

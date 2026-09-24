using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using P2P.Client.Hosting;
using P2P.Client.Storage;
using P2P.Core.Crypto;
using P2P.Core.Stun;
using P2P.IntegrationTests.NatSimulator;
using P2P.Server.Data;
using P2P.Server.Services;
using Xunit;
using Xunit.Abstractions;

namespace P2P.IntegrationTests;

/// <summary>
/// 集成场景自动化 A-1~A-6 + M2-18 中继回退 + M2-19 中继回切直连（09 §2.3；M1-35 交付 A-1~A-4、
/// M2-31 交付 A-5 TCP 打洞命中率矩阵、M2-18 交付承载绑定中继路径、M2-19 交付回切排水切换、
/// M2-32 交付 A-6 中继回退场景：SymmetricRandom 必败/回退开关分岔/UdpBlocked TCP 承载变体）。
/// 三进程 in-proc：服务端 + 双客户端（各自独立 baseDir；A-6 断言①②为三方）；网卡以 StubNicManager
/// 替身、虚拟 IP 用 127.0.0.x 回环别名（A-4 隔离语义在 127.0.0.0/8 内等价成立）；A-3/A-4 打洞链路经
/// NatSimulator（STUN 派生 :3478，TD-07），A-5 双端 SymmetricSequential 经 TcpNatSimulator（TD-17/21），
/// M2-18 复用 A-5 miss 世界（打洞必败）驱动中继回退，M2-19 借其扰动一次性在重打时耗尽驱动回切命中，
/// M2-32 A-6 以 SymmetricRandom 随机分配（预测失配+APDF 拒绝必 miss）与 UdpBlocked（UDP 出站全丢）变体。
/// 全运行时重组件专用集合（与 ClientRuntimeIntegrationTests 共用）：每用例拉起双 Kestrel+服务端+打洞链路，
/// 相互串行避免与轻量类并行时叠加满载偶发（沿 M1-31 加固惯例）。
/// </summary>
[Collection("heavy-runtime")]
public sealed class ScenarioIntegrationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DeviceRegistry _registry = new();
    private readonly List<SignalingCoordinator> _signalings = [];
    private readonly List<RelayService> _relays = [];
    private readonly List<ControlServer> _servers = [];
    private readonly StubNicManager _nic = new();
    private readonly List<ClientRuntime> _runtimes = [];
    private readonly List<HttpClient> _https = [];
    private readonly List<TcpListener> _echoListeners = [];
    private CancellationTokenSource? _echoCts;
    private UdpNatSimulator? _sim;
    private StubStunUpstream? _upstream;
    private TcpStunStub? _tcpStun;
    private TcpNatSimulator? _tcpSim;
    private StubFactory _factory = null!;
    private string _rootDir = null!;
    private int _port;

    public ScenarioIntegrationTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        _connection.Open();
        _factory = new StubFactory(CreateDb);
        using (var init = _factory.CreateDbContext())
            DbInitializer.Initialize(init);
        var server = await StartServerAsync(0);
        _port = server.LocalEndPoint!.Port;
        _rootDir = Path.Combine(Path.GetTempPath(), $"p2p-it-sc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_rootDir);
    }

    public async Task DisposeAsync()
    {
        if (_echoCts is not null) _echoCts.Cancel();
        foreach (var l in _echoListeners) l.Stop();
        foreach (var r in _runtimes) await r.DisposeAsync();
        foreach (var h in _https) h.Dispose();
        if (_sim is not null) await _sim.DisposeAsync();
        if (_upstream is not null) await _upstream.DisposeAsync();
        if (_tcpStun is not null) await _tcpStun.DisposeAsync();
        if (_tcpSim is not null) await _tcpSim.DisposeAsync();
        foreach (var s in _servers) await s.DisposeAsync();
        foreach (var r in _relays) await r.DisposeAsync();
        foreach (var s in _signalings) await s.DisposeAsync();
        await Task.Delay(200); // 服务端收尾与夹具销毁竞态宽限（ClientRuntime 测试同法）
        try { _connection.Dispose(); }
        catch (InvalidOperationException) { }
        try { Directory.Delete(_rootDir, true); } catch (IOException) { }
    }

    private AppDbContext CreateDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);

    private async Task<ControlServer> StartServerAsync(int port)
    {
        var audit = new AuditLogger(_factory);
        var signaling = new SignalingCoordinator(_factory, _registry, new Authorizer(_factory), audit);
        var relay = new RelayService(_factory, _registry, signaling.ResolveRelayPeers);
        var router = new ControlMessageRouter(
            new RegistrationService(_factory, _registry, audit),
            new UserService(_factory, audit),
            new GroupService(_factory, _registry),
            signaling,
            new MappingService(_factory, audit),
            relay,
            new StatsService(_factory, audit),
            audit);
        var server = new ControlServer(_factory, _registry, router.DispatchAsync);
        await server.StartAsync(new IPEndPoint(IPAddress.Loopback, port));
        await relay.StartAsync(0, 0); // M2-18：中继数据面双端口系统分配（Grant 端点来源）
        _signalings.Add(signaling);
        _relays.Add(relay);
        _servers.Add(server);
        return server;
    }

    // ── 世界构建 ───────────────────────────────────────────────────────

    private sealed record SeededClient(Guid DeviceId, byte[] DeviceSecret, string RemoteCode, string Dir, IPAddress Ip);

    /// <summary>种已注册设备（静态密钥对/DeviceSecret/远程码/虚拟 IP 别名）+ 客户端 state.json 与 settings；
    /// punchConcurrency 经 0x70 上送、服务端回填统一下发（OQ-19/TD-20）——A-5 按用例参数化 N。</summary>
    private async Task<SeededClient> SeedClientAsync(string name, IPAddress ip, Group? commonGroup, int punchConcurrency = 1)
    {
        var deviceId = Guid.NewGuid();
        var secret = RandomGenerator.Bytes(32);
        var remoteCode = FakeRemoteCode();
        using var kp = EcKeyPair.Generate();
        await using var db = _factory.CreateDbContext();
        db.Devices.Add(new Device
        {
            Id = deviceId,
            DeviceName = name,
            Os = "windows",
            ClientVersion = "0.1.0",
            MacCode = $"SC-{Guid.NewGuid():N}"[..16],
            RemoteCode = remoteCode,
            VirtualIp = ip.ToString(),
            StaticPubKey = kp.ExportPublicKey(),
            DeviceSecret = secret,
            LastSeenAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
        });
        if (commonGroup is not null)
            db.GroupMembers.Add(new GroupMember { Id = Guid.NewGuid(), GroupId = commonGroup.Id, DeviceId = deviceId, Approved = true });
        await db.SaveChangesAsync();

        var dir = Path.Combine(_rootDir, name);
        Directory.CreateDirectory(dir);
        var store = new StateStore(dir);
        store.State.DeviceId = deviceId;
        store.State.DeviceSecret = secret;
        store.State.StaticPrivateKey = kp.ExportPrivateKey();
        store.State.RemoteCode = remoteCode;
        store.State.VirtualIp = ip.ToString();
        await store.SaveAsync();
        var settings = new SettingsStore(dir);
        await settings.SaveAsync(new ClientSettings
        {
            ServerAddrs = [$"127.0.0.1:{_port}"],
            LocalWebPort = FreePort(),
            // M2-16：tcp 映射走 TCP 打洞；A-3/A-4 恒等 NAT（TcpStunStub 无端口平移）N=1 直连命中；
            // N>1 预测矩阵归 A-5（TcpNatSimulator 端口平移，M2-31）
            PunchConcurrency = punchConcurrency,
        });
        return new SeededClient(deviceId, secret, store.State.RemoteCode, dir, ip);
    }

    /// <summary>共同分组（打洞目标 0x40 解析的 L2 可见性前提；owner=内置 admin 满足外键）。</summary>
    private async Task<Group> CreateGroupAsync()
    {
        await using var db = _factory.CreateDbContext();
        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = $"sc-{Guid.NewGuid():N}"[..16],
            OwnerUserId = db.Users.Single(u => u.IsAdmin).Id,
            IsDefault = false,
            JoinPolicy = "free",
            CreatedAt = DateTime.UtcNow,
        };
        db.Groups.Add(group);
        await db.SaveChangesAsync();
        return group;
    }

    /// <summary>启动客户端运行时（网卡替身 + 打洞 socket 绑内网别名缝；诊断日志进测试产物；
    /// relayRetryInterval：M2-19 回切 60s 周期的缩短缝——周期机制本身驱动回切，间接验证触发）。</summary>
    private async Task<HttpClient> StartRuntimeAsync(SeededClient c, TimeSpan? relayRetryInterval = null)
    {
        var runtime = new ClientRuntime(new ClientRuntimeOptions
        {
            BaseDir = c.Dir,
            NicOverride = _nic,
            PunchBindOverride = c.Ip,
            RelayRetryIntervalOverride = relayRetryInterval,
        });
        runtime.Log += m => _output.WriteLine($"[{c.Ip}] {m}");
        await runtime.StartAsync();
        _runtimes.Add(runtime);
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{runtime.WebPort}") };
        _https.Add(http);
        return http;
    }

    /// <summary>NatSimulator 世界：STUN 派生 :3478（TD-07）+ 上游替身 + 双客户端 NAT 注册；
    /// TCP 侧默认拉 TCP STUN 应答替身（恒等 NAT——A-3/A-4 的 N=1 直连命中）；
    /// A-5 置 tcpStub=false 改由 <see cref="StartTcpSimulatorAsync"/> 拉端口平移 NAT（同宿主 :3478/TCP 互斥）。</summary>
    private async Task StartSimulatorAsync((IPAddress Ip, UdpNatMode Mode) a, (IPAddress Ip, UdpNatMode Mode) b, bool tcpStub = true)
    {
        _upstream = new StubStunUpstream();
        await _upstream.StartAsync();
        _sim = new UdpNatSimulator(new UdpNatOptions { Upstream = _upstream.Endpoint, StunPort = 3478 });
        await _sim.StartAsync();
        _sim.RegisterClient(a.Ip, a.Mode, "A");
        _sim.RegisterClient(b.Ip, b.Mode, "B");
        if (tcpStub)
        {
            _tcpStun = new TcpStunStub();
            _tcpStun.Start();
        }
    }

    /// <summary>A-5 世界：双端 SymmetricSequential TCP NAT（TD-17 导演+桥接；TD-07 派生 :3478/TCP）——
    /// 探测分配顺序导演端口、出站 APDF 精确身份过滤、探测映射 listen 交付（TD-21）。
    /// PortBase 独立段（默认 20000 归 TcpNatSimulatorTests 机制测试，并行类窗口监听不互撞）。
    /// perturbB：B 首次探测后注入外来流扰动（端口预测失配 miss 场景）。
    /// mode：A-6 置 SymmetricRandom（随机分配 → 端口预测失配 + APDF 身份过滤拒绝 → 双侧必 miss，09 §2.2）；
    /// ipC：三方变体（A-6 目标 C 回退关对照）。</summary>
    private async Task StartTcpSimulatorAsync(int perturbB = 0,
        TcpNatMode mode = TcpNatMode.SymmetricSequential, IPAddress? ipC = null)
    {
        _tcpSim = new TcpNatSimulator(new TcpNatOptions { StunPort = 3478, PortBase = 20500 });
        _tcpSim.RegisterClient(IPAddress.Parse("127.0.0.4"), mode, "A");
        _tcpSim.RegisterClient(IPAddress.Parse("127.0.0.5"), mode, "B", perturbB);
        if (ipC is not null) _tcpSim.RegisterClient(ipC, mode, "C");
        await _tcpSim.StartAsync();
    }

    /// <summary>本机回环 echo TCP 服务（打洞目标 self:port 的载荷校验终点）。</summary>
    private void StartEcho(int port)
    {
        _echoCts ??= new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _echoListeners.Add(listener);
        var ct = _echoCts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(ct); }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { break; }
                _ = Task.Run(() => EchoClientAsync(client), ct);
            }
        });
    }

    private static async Task EchoClientAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            await using var stream = client.GetStream();
            var buf = new byte[64 * 1024];
            while (true)
            {
                var n = await stream.ReadAsync(buf);
                if (n <= 0) break;
                await stream.WriteAsync(buf.AsMemory(0, n));
            }
        }
        catch { /* 客户端断开 */ }
    }

    /// <summary>TCP STUN Binding 应答替身（M2-06 正式四道闸服务落地前的场景缝）：常驻
    /// 127.0.0.1:3478/TCP（TD-07 派生端口），收一帧→回 XOR-MAPPED（服务端所见的源端点）→关；
    /// 双客户端并发探测（A/B 各一笔短事务，02 §3.3）。</summary>
    private sealed class TcpStunStub : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 3478);
        private readonly CancellationTokenSource _cts = new();

        public void Start()
        {
            _listener.Start(16);
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var conn = await _listener.AcceptSocketAsync(_cts.Token);
                        _ = Task.Run(() => ServeAsync(conn));
                    }
                }
                catch { /* 停机关闭 */ }
            });
        }

        private async Task ServeAsync(Socket conn)
        {
            try
            {
                using var _ = conn;
                using var stream = new NetworkStream(conn, ownsSocket: false);
                var request = await StunTcpFraming.TryReadAsync(stream, _cts.Token);
                if (request is null) return;
                var tid = request.AsSpan(8, StunCodec.TransactionIdLen).ToArray(); // 头 type2+len2+magic4 → tid@8
                var remote = (IPEndPoint)conn.RemoteEndPoint!;
                await StunTcpFraming.WriteAsync(stream,
                    StunCodec.BuildBindingResponse(tid, remote.Address, (ushort)remote.Port), _cts.Token);
            }
            catch { /* 单事务尽力而为 */ }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            await Task.Delay(50); // 在飞事务收尾宽限
            _cts.Dispose();
        }
    }

    // ── HTTP/断言工具 ─────────────────────────────────────────────────

    private static async Task<JsonElement> GetAsync(HttpClient http, string path)
    {
        using var resp = await http.GetAsync(path);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
    }

    private static async Task<JsonElement> PostAsync(HttpClient http, string path, object? body)
    {
        using var content = body is null ? null
            : new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync(path, content);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
    }

    private static async Task<JsonElement> PutAsync(HttpClient http, string path, object? body)
    {
        using var content = body is null ? null
            : new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await http.PutAsync(path, content);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
    }

    private static async Task<string> GetPhaseAsync(HttpClient http)
    {
        var root = await GetAsync(http, "/api/system/state");
        return root.GetProperty("data").GetProperty("phase").GetString()!;
    }

    private static async Task WaitPhaseAsync(HttpClient http, string phase, int seconds = 20)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        string current = "";
        while (!cts.IsCancellationRequested)
        {
            current = await GetPhaseAsync(http);
            if (current == phase) return;
            await Task.Delay(200, cts.Token);
        }
        Assert.Fail($"等待 phase={phase} 超时（当前 {current}）");
    }

    /// <summary>轮询映射状态（打洞异步完成：direct/relay/failed 终态或 punching 中间态；
    /// mappingId 缺省=首条，指定=按 id 精确匹配——列表按 Name+MappingId 排序，顺序不可依赖）。</summary>
    private static async Task<string> WaitMappingStateAsync(HttpClient http, string state, int seconds = 30,
        Guid? mappingId = null)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        string current = "";
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var root = await GetAsync(http, "/api/mappings");
                var items = root.GetProperty("data").GetProperty("items");
                var match = items.EnumerateArray()
                    .FirstOrDefault(i => mappingId is null || i.GetProperty("mappingId").GetGuid() == mappingId);
                current = match.ValueKind == JsonValueKind.Undefined ? "" : match.GetProperty("state").GetString()!;
                if (current == state) return current;
                await Task.Delay(200, cts.Token);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { /* 超时走统一断言 */ }
        Assert.Fail($"等待映射 {mappingId} 状态 {state} 超时（当前 {current}）");
        return current; // 不可达
    }

    /// <summary>建立映射并启用（经真实本地 API → 控制协议全链路，04 §2.5）。</summary>
    private static async Task<Guid> CreateAndEnableMappingAsync(HttpClient http, ushort localPort,
        string targetRemoteCode, ushort targetPort)
    {
        var created = await PostAsync(http, "/api/mappings", new
        {
            name = "场景映射",
            localPort,
            proto = "tcp",
            targetRemoteCode,
            targetAddr = "self",
            targetPort,
        });
        Assert.Equal(0, created.GetProperty("code").GetInt32());
        var id = created.GetProperty("data").GetProperty("mappingId").GetGuid();
        var enabled = await PostAsync(http, $"/api/mappings/{id}/enable", null);
        Assert.Equal(0, enabled.GetProperty("code").GetInt32());
        return id;
    }

    /// <summary>连接 + echo 往返校验（payload 大于 1368B 分块覆盖 splice 路径）。</summary>
    private static async Task AssertEchoRoundtripAsync(IPAddress address, int port, byte[] payload)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(address, port).WaitAsync(TimeSpan.FromSeconds(10));
        await using var stream = client.GetStream();
        await stream.WriteAsync(payload);
        var got = new byte[payload.Length];
        var offset = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (offset < got.Length)
        {
            var n = await stream.ReadAsync(got.AsMemory(offset), cts.Token);
            Assert.True(n > 0, "echo 流提前关闭");
            offset += n;
        }
        Assert.True(payload.AsSpan().SequenceEqual(got), "echo 载荷不一致");
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string FakeRemoteCode()
    {
        const string alphabet = "0123456789abc";
        return new string(RandomGenerator.Bytes(6).Select(b => alphabet[b % alphabet.Length]).ToArray());
    }

    // ── A-1 首启注册：向导 → 服务端在线 → 替身地址生效 ────────────────

    [Fact]
    public async Task A1_全新注册_服务端在线_替身虚拟地址生效()
    {
        var dir = Path.Combine(_rootDir, "a1-fresh");
        Directory.CreateDirectory(dir);
        var settings = new SettingsStore(dir);
        await settings.SaveAsync(new ClientSettings { ServerAddrs = [], LocalWebPort = FreePort() });

        var runtime = new ClientRuntime(new ClientRuntimeOptions { BaseDir = dir, NicOverride = _nic });
        _runtimes.Add(runtime);
        await runtime.StartAsync(); // 未注册分支：向导模式
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{runtime.WebPort}") };
        _https.Add(http);
        Assert.Equal("unregistered", await GetPhaseAsync(http));

        // 向导注册（default 模式）：换址 → 0x10 → ECIES RegisterAck 三要素
        var registered = await PostAsync(http, "/api/wizard/register", new
        {
            serverAddr = $"127.0.0.1:{_port}",
            mode = "default",
            deviceName = "a1-dev",
        });
        Assert.Equal(0, registered.GetProperty("code").GetInt32());
        var remoteCode = registered.GetProperty("data").GetProperty("remoteCode").GetString();
        Assert.False(string.IsNullOrEmpty(remoteCode));

        // 注册后路径自动续跑：phase running + 替身应用统一下发 .2（OQ-13）
        await WaitPhaseAsync(http, "running");
        Assert.Contains(IPAddress.Parse("100.64.0.2"), _nic.Ensured);

        // 服务端侧在线（内存注册表；A-1 出口第三要素）
        var state = new StateStore(dir);
        state.Load();
        var deviceId = state.State.DeviceId ?? Guid.Empty;
        Assert.NotEqual(Guid.Empty, deviceId);
        Assert.True(_registry.IsOnline(deviceId), "注册完成后服务端应在线");
        Assert.Equal(remoteCode, state.State.RemoteCode);
    }

    // ── A-2 登录与发现：同账号两台设备互见（0x40 循环拉全量）──────────

    [Fact]
    public async Task A2_同账号双设备_互见且字段完整()
    {
        var a = await SeedClientAsync("a2-a", IPAddress.Parse("127.0.0.4"), null);
        var b = await SeedClientAsync("a2-b", IPAddress.Parse("127.0.0.5"), null);
        var httpA = await StartRuntimeAsync(a);
        var httpB = await StartRuntimeAsync(b);
        await WaitPhaseAsync(httpA, "running");
        await WaitPhaseAsync(httpB, "running");

        // 登录前无可见对端（无共同分组且未绑定账号）
        var before = await GetAsync(httpA, "/api/devices");
        Assert.Equal(0, before.GetProperty("code").GetInt32());
        Assert.Equal(0, before.GetProperty("data").GetArrayLength());

        // A 注册新号并登录；B 以已有账号登录绑定（M1-24"已有账号登录绑定"路径，A-2 前提）
        var username = $"a2-{Guid.NewGuid():N}"[..12];
        var password = "secret123";
        var register = await PostAsync(httpA, "/api/auth/register", new { username, password });
        Assert.Equal(0, register.GetProperty("code").GetInt32());
        var loginA = await PostAsync(httpA, "/api/auth/login", new { username, password });
        Assert.Equal(0, loginA.GetProperty("code").GetInt32());
        Assert.Equal("normal", loginA.GetProperty("data").GetProperty("mode").GetString());
        var loginB = await PostAsync(httpB, "/api/auth/login", new { username, password });
        Assert.Equal(0, loginB.GetProperty("code").GetInt32());

        // 互见 + 字段完整（远程码/虚拟 IP/在线状态——A-2 出口）
        var peersOfA = await GetAsync(httpA, "/api/devices");
        var peerB = Assert.Single(peersOfA.GetProperty("data").EnumerateArray(),
            d => d.GetProperty("deviceId").GetGuid() == b.DeviceId);
        Assert.Equal("a2-b", peerB.GetProperty("deviceName").GetString());
        Assert.Equal(b.RemoteCode, peerB.GetProperty("remoteCode").GetString());
        Assert.Equal("127.0.0.5", peerB.GetProperty("virtualIp").GetString());
        Assert.True(peerB.GetProperty("online").GetBoolean());

        var peersOfB = await GetAsync(httpB, "/api/devices");
        Assert.Contains(peersOfB.GetProperty("data").EnumerateArray(),
            d => d.GetProperty("deviceId").GetGuid() == a.DeviceId && d.GetProperty("online").GetBoolean());
    }

    // ── A-3 UDP 打洞直连：FullCone×RestrictedCone + TCP 回环载荷（经 UDP 隧道承载 TCP 流）──

    [Fact]
    public async Task A3_打洞直连_经虚拟地址TCP回环载荷校验()
    {
        await StartSimulatorAsync(
            (IPAddress.Parse("127.0.0.4"), UdpNatMode.FullCone),
            (IPAddress.Parse("127.0.0.5"), UdpNatMode.RestrictedCone));
        var group = await CreateGroupAsync();
        var a = await SeedClientAsync("a3-a", IPAddress.Parse("127.0.0.4"), group);
        var b = await SeedClientAsync("a3-b", IPAddress.Parse("127.0.0.5"), group);
        var httpA = await StartRuntimeAsync(a);
        var httpB = await StartRuntimeAsync(b);
        await WaitPhaseAsync(httpA, "running");
        await WaitPhaseAsync(httpB, "running");

        // B 侧 self 目标：本机 echo 服务
        var echoPort = FreePort();
        StartEcho(echoPort);
        var localPort = FreePort();
        await CreateAndEnableMappingAsync(httpA, (ushort)localPort, b.RemoteCode, (ushort)echoPort);

        // FullCone×RestrictedCone 双向发包命中 → direct（02 §5.1 全序列经真实控制面+NatSimulator）
        var state = await WaitMappingStateAsync(httpA, "direct");
        Assert.Equal("direct", state);

        // 虚拟地址:本地端口 TCP 载荷往返（>1368B 覆盖分块 splice；02 §4.3）
        var payload = RandomGenerator.Bytes(40 * 1024);
        await AssertEchoRoundtripAsync(IPAddress.Parse("127.0.0.4"), localPort, payload);

        // 同映射第二连接 = 同会话第二 channel（channelId 复用，02 §4.5）
        await AssertEchoRoundtripAsync(IPAddress.Parse("127.0.0.4"), localPort, RandomGenerator.Bytes(3 * 1024));
    }

    // ── A-4 端口隔离：本机 127.0.0.1:P 占用与替身地址:P 映射并存互不干扰 ──

    [Fact]
    public async Task A4_本机占用与虚拟地址同端口并存互不干扰()
    {
        await StartSimulatorAsync(
            (IPAddress.Parse("127.0.0.4"), UdpNatMode.FullCone),
            (IPAddress.Parse("127.0.0.5"), UdpNatMode.FullCone));
        var group = await CreateGroupAsync();
        var a = await SeedClientAsync("a4-a", IPAddress.Parse("127.0.0.4"), group);
        var b = await SeedClientAsync("a4-b", IPAddress.Parse("127.0.0.5"), group);
        var httpA = await StartRuntimeAsync(a);
        await StartRuntimeAsync(b);
        await WaitPhaseAsync(httpA, "running");

        // 同端口号双地址：本机占用（echo 直连目标）= 127.0.0.1:P；映射监听 = 127.0.0.4:P
        var port = FreePort();
        StartEcho(port);
        await CreateAndEnableMappingAsync(httpA, (ushort)port, b.RemoteCode, (ushort)port);
        await WaitMappingStateAsync(httpA, "direct");

        // 并存互不干扰：两条连接同时打开，各自 echo 往返
        var direct = AssertEchoRoundtripAsync(IPAddress.Loopback, port, RandomGenerator.Bytes(2048));
        var viaTunnel = AssertEchoRoundtripAsync(IPAddress.Parse("127.0.0.4"), port, RandomGenerator.Bytes(40 * 1024));
        await Task.WhenAll(direct, viaTunnel);
    }

    // ── A-5 TCP 打洞：SymmetricSequential 双端全序列 + N=1~5 命中率矩阵 ──

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task A5_TCPPunch_SymmetricSequential_N并发全序列直达(int n)
    {
        await StartSimulatorAsync(
            (IPAddress.Parse("127.0.0.4"), UdpNatMode.FullCone),
            (IPAddress.Parse("127.0.0.5"), UdpNatMode.FullCone), tcpStub: false);
        await StartTcpSimulatorAsync();
        var group = await CreateGroupAsync();
        var a = await SeedClientAsync($"a5-{n}-a", IPAddress.Parse("127.0.0.4"), group, punchConcurrency: n);
        var b = await SeedClientAsync($"a5-{n}-b", IPAddress.Parse("127.0.0.5"), group, punchConcurrency: n);
        var httpA = await StartRuntimeAsync(a);
        var httpB = await StartRuntimeAsync(b);
        await WaitPhaseAsync(httpA, "running");
        await WaitPhaseAsync(httpB, "running");

        // B 侧 self 目标：本机 echo 服务
        var echoPort = FreePort();
        StartEcho(echoPort);
        var localPort = FreePort();
        await CreateAndEnableMappingAsync(httpA, (ushort)localPort, b.RemoteCode, (ushort)echoPort);

        // 全序列（02 §5.2）：STUN-TCP 两段式端点互换（0x70{tcp:L'}→0x71→0x76→Ack{对端L',N 回填}）
        // → N 并发 listen(L)+connect（统一目标 对端L'+(N−1)）→ APDF 精确身份过滤 rendezvous（TD-21）
        // → THello1 扇出/THello2 消歧 → PTP 握手 → TcpFrameTransport 承载
        var state = await WaitMappingStateAsync(httpA, "direct");
        // 命中率矩阵数据行（A5-MATRIX 前缀可从测试产物聚合；M4 基准报告成文源）
        _output.WriteLine($"A5-MATRIX|N={n}|nat=SymmetricSequential×2|state={state}|transport=tcp");

        // 虚拟地址:本地端口 TCP 载荷往返（u16 定界承载；>1368B 覆盖分块 splice，02 §4.3）
        await AssertEchoRoundtripAsync(IPAddress.Parse("127.0.0.4"), localPort, RandomGenerator.Bytes(40 * 1024));
        // 同映射第二连接 = 同会话第二 channel（channelId 复用，02 §4.5）
        await AssertEchoRoundtripAsync(IPAddress.Parse("127.0.0.4"), localPort, RandomGenerator.Bytes(3 * 1024));
    }

    [Fact]
    public async Task A5_端口预测失配_打洞失败()
    {
        // 协议统一双方 N（TD-13/20：服务端以发起方 N 经 0x71/0x70 Ack 统一回填）→ N_A≠N_B 在真实
        // 协议路径上不存在（M2-16 单测已按注入式不对称 N 断言 miss）。场景级失配以 NAT 时序扰动等效
        // 呈现（TD-21 代数：扰动 s≥N−1 ⟺ 不对称必 miss 区）：B 首次探测后、Fleet 连接前被 2 条
        // 外来流占用端口序列（N=2）——A 的预测目标 P_B+1 落在无人监听的外来映射 → listener_refused；
        // B 的 c_k 分配端口（P_B+3 起）≠ A 侧映射目的端口 P_B+1 → 身份过滤——双侧全 miss。
        await StartSimulatorAsync(
            (IPAddress.Parse("127.0.0.4"), UdpNatMode.FullCone),
            (IPAddress.Parse("127.0.0.5"), UdpNatMode.FullCone), tcpStub: false);
        await StartTcpSimulatorAsync(perturbB: 2);
        var group = await CreateGroupAsync();
        var a = await SeedClientAsync("a5-miss-a", IPAddress.Parse("127.0.0.4"), group, punchConcurrency: 2);
        var b = await SeedClientAsync("a5-miss-b", IPAddress.Parse("127.0.0.5"), group, punchConcurrency: 2);
        var httpA = await StartRuntimeAsync(a);
        var httpB = await StartRuntimeAsync(b);
        await WaitPhaseAsync(httpA, "running");
        await WaitPhaseAsync(httpB, "running");

        var echoPort = FreePort();
        StartEcho(echoPort);
        var localPort = FreePort();
        await CreateAndEnableMappingAsync(httpA, (ushort)localPort, b.RemoteCode, (ushort)echoPort);

        // 双侧预测目标均无有效交付 → 无 THello 交换 → 10s 打洞预算耗尽 → failed（02 §5.2 ⑤）
        var state = await WaitMappingStateAsync(httpA, "failed", seconds: 40);
        _output.WriteLine($"A5-MATRIX|N=2|nat=SymmetricSequential×2+perturbB=2|state={state}|transport=tcp");
    }

    // ── M2-18 中继回退承载绑定（02 §4.5/§6、05 §4）────────────────────

    /// <summary>A-5 miss 同构世界 + A 侧设备级回退开（M2-23 /api/peers）：打洞 Ack 后失败 →
    /// 资格合成真 → 0x74 → Grant 双侧下发 → 双端 JOIN → PTP 握手即经中继（同一 TunnelSession 帧改发
    /// relay 地址）→ 映射 relay 态且虚拟地址可访问（echo 经中继密文转发往返）。</summary>
    [Fact]
    public async Task M2_18_直连失败回退开_中继承载_映射relay态_虚拟地址可访问()
    {
        await StartSimulatorAsync(
            (IPAddress.Parse("127.0.0.4"), UdpNatMode.FullCone),
            (IPAddress.Parse("127.0.0.5"), UdpNatMode.FullCone), tcpStub: false);
        await StartTcpSimulatorAsync(perturbB: 2); // 双侧预测失配 → 打洞必败（A-5 miss 同构）
        var group = await CreateGroupAsync();
        var a = await SeedClientAsync("r18-fb-a", IPAddress.Parse("127.0.0.4"), group, punchConcurrency: 2);
        var b = await SeedClientAsync("r18-fb-b", IPAddress.Parse("127.0.0.5"), group, punchConcurrency: 2);
        var httpA = await StartRuntimeAsync(a);
        var httpB = await StartRuntimeAsync(b);
        await WaitPhaseAsync(httpA, "running");
        await WaitPhaseAsync(httpB, "running");

        // 目标设备级回退配置开启（M2-23；服务端 relay_enabled 默认开=DbInitializer）
        var put = await PutAsync(httpA, $"/api/peers/{b.DeviceId}", new { relayFallback = true });
        Assert.Equal(0, put.GetProperty("code").GetInt32());

        var echoPort = FreePort();
        StartEcho(echoPort);
        var localPort = FreePort();
        await CreateAndEnableMappingAsync(httpA, (ushort)localPort, b.RemoteCode, (ushort)echoPort);

        // 10s 打洞预算 → 失败 → 0x74+JOIN+中继握手（全程真实运行时，双侧 ClientRuntime 各自驱动）
        var state = await WaitMappingStateAsync(httpA, "relay", seconds: 40);
        _output.WriteLine($"M2-18|relay-fallback|state={state}");

        // 虚拟地址经中继承载可访问：A 侧 OPEN → relay → B 连 self echo → 双向密文转发
        await AssertEchoRoundtripAsync(IPAddress.Parse("127.0.0.4"), localPort, RandomGenerator.Bytes(40 * 1024));
        await AssertEchoRoundtripAsync(IPAddress.Parse("127.0.0.4"), localPort, RandomGenerator.Bytes(3 * 1024));
    }

    [Fact]
    public async Task M2_18_回退关_打洞失败映射failed()
    {
        await StartSimulatorAsync(
            (IPAddress.Parse("127.0.0.4"), UdpNatMode.FullCone),
            (IPAddress.Parse("127.0.0.5"), UdpNatMode.FullCone), tcpStub: false);
        await StartTcpSimulatorAsync(perturbB: 2);
        var group = await CreateGroupAsync();
        var a = await SeedClientAsync("r18-off-a", IPAddress.Parse("127.0.0.4"), group, punchConcurrency: 2);
        var b = await SeedClientAsync("r18-off-b", IPAddress.Parse("127.0.0.5"), group, punchConcurrency: 2);
        var httpA = await StartRuntimeAsync(a);
        var httpB = await StartRuntimeAsync(b);
        await WaitPhaseAsync(httpA, "running");
        await WaitPhaseAsync(httpB, "running");
        // 无 peers.json 条目：默认关（PRD 06 §2）——资格合成假，不触发 0x74

        var echoPort = FreePort();
        StartEcho(echoPort);
        var localPort = FreePort();
        await CreateAndEnableMappingAsync(httpA, (ushort)localPort, b.RemoteCode, (ushort)echoPort);

        // 打洞失败且无回退 → failed（02 §4.5 承载绑定：未开启 → 该设备对全部映射 failed）
        var state = await WaitMappingStateAsync(httpA, "failed", seconds: 40);
        _output.WriteLine($"M2-18|relay-off|state={state}");
    }

    [Fact]
    public async Task M2_18_relay态新映射复用_不重复打洞()
    {
        await StartSimulatorAsync(
            (IPAddress.Parse("127.0.0.4"), UdpNatMode.FullCone),
            (IPAddress.Parse("127.0.0.5"), UdpNatMode.FullCone), tcpStub: false);
        await StartTcpSimulatorAsync(perturbB: 2);
        var group = await CreateGroupAsync();
        var a = await SeedClientAsync("r18-re-a", IPAddress.Parse("127.0.0.4"), group, punchConcurrency: 2);
        var b = await SeedClientAsync("r18-re-b", IPAddress.Parse("127.0.0.5"), group, punchConcurrency: 2);
        var httpA = await StartRuntimeAsync(a);
        var httpB = await StartRuntimeAsync(b);
        await WaitPhaseAsync(httpA, "running");
        await WaitPhaseAsync(httpB, "running");
        var put = await PutAsync(httpA, $"/api/peers/{b.DeviceId}", new { relayFallback = true });
        Assert.Equal(0, put.GetProperty("code").GetInt32());

        var echoPort = FreePort();
        StartEcho(echoPort);
        var localPort = FreePort();
        await CreateAndEnableMappingAsync(httpA, (ushort)localPort, b.RemoteCode, (ushort)echoPort);
        await WaitMappingStateAsync(httpA, "relay", seconds: 40); // 第一条：中继建立

        // 第二条映射启用 → 隧道复用检查命中（02 §4.5：relay 态会话存活 → 复用当前承载不重新打洞）
        var localPort2 = FreePort();
        var mapping2 = await CreateAndEnableMappingAsync(httpA, (ushort)localPort2, b.RemoteCode, (ushort)echoPort);
        var state2 = await WaitMappingStateAsync(httpA, "relay", seconds: 5, mapping2); // 5s 内直达=复用（真打洞 ≥10s）
        _output.WriteLine($"M2-18|relay-reuse|state2={state2}");

        // 复用路径证据：明细 tunnel_reused + 打洞队列空、无进行中会话（05 §3.1 诊断口径）
        var root = await GetAsync(httpA, "/api/mappings");
        var item = root.GetProperty("data").GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("mappingId").GetGuid() == mapping2);
        Assert.Equal("tunnel_reused", item.GetProperty("detail").GetString());
        var diag = await GetAsync(httpA, "/api/diagnostics");
        Assert.Equal(0, diag.GetProperty("data").GetProperty("punchQueueDepth").GetInt32());
        Assert.Equal(JsonValueKind.Null, diag.GetProperty("data").GetProperty("currentPunchPeer").ValueKind);

        // 同隧道第二 channel：第二条映射虚拟地址同样可访问
        await AssertEchoRoundtripAsync(IPAddress.Parse("127.0.0.4"), localPort2, RandomGenerator.Bytes(8 * 1024));
    }

    // ── M2-19 中继回切直连（02 §6.2/OQ-7/NET-75）──────────────────────

    /// <summary>A-5 miss 同构世界（扰动一次性）+ 回退开 → relay 态；60s 周期缩短 3s（测试缝）驱动回切
    /// 协调（0x73 → 全新 0x70）→ 重打时扰动已耗尽 → SymmetricSequential×2 同 N=2 干净命中 → 直连会话
    /// 替换中继（排水窗 2s，NET-75）→ 映射 relay→direct。序号载荷校验：relay 承载上持续写递增序号，
    /// echo 回程连续无缺无重无乱序直至断连止；新连接走新直连路径完整往返。</summary>
    [Fact]
    public async Task M2_19_中继回切直连_周期触发重打命中_排水切换序号不丢不乱()
    {
        await StartSimulatorAsync(
            (IPAddress.Parse("127.0.0.4"), UdpNatMode.FullCone),
            (IPAddress.Parse("127.0.0.5"), UdpNatMode.FullCone), tcpStub: false);
        await StartTcpSimulatorAsync(perturbB: 2); // 首打 miss；扰动一次性——重打时已耗尽 → 命中
        var group = await CreateGroupAsync();
        var a = await SeedClientAsync("r19-a", IPAddress.Parse("127.0.0.4"), group, punchConcurrency: 2);
        var b = await SeedClientAsync("r19-b", IPAddress.Parse("127.0.0.5"), group, punchConcurrency: 2);
        var httpA = await StartRuntimeAsync(a, relayRetryInterval: TimeSpan.FromSeconds(3));
        var httpB = await StartRuntimeAsync(b);
        await WaitPhaseAsync(httpA, "running");
        await WaitPhaseAsync(httpB, "running");

        var put = await PutAsync(httpA, $"/api/peers/{b.DeviceId}", new { relayFallback = true });
        Assert.Equal(0, put.GetProperty("code").GetInt32());

        var echoPort = FreePort();
        StartEcho(echoPort);
        var localPort = FreePort();
        await CreateAndEnableMappingAsync(httpA, (ushort)localPort, b.RemoteCode, (ushort)echoPort);

        // ① 首打 miss + 回退开 → 中继承载（relay 态，M2-18 路径）
        await WaitMappingStateAsync(httpA, "relay", seconds: 40);

        // ② 序号载荷流（NET-75）：relay 承载上 100ms 一笔递增序号，echo 回程收集至断连
        using var seqClient = new TcpClient();
        await seqClient.ConnectAsync(IPAddress.Parse("127.0.0.4"), localPort);
        var seqStream = seqClient.GetStream();
        using var writeCts = new CancellationTokenSource(TimeSpan.FromSeconds(120)); // 失败路径防挂起
        var writeTask = Task.Run(async () =>
        {
            var seq = 0;
            try
            {
                while (true)
                {
                    await seqStream.WriteAsync(Encoding.ASCII.GetBytes($"{seq}\n"), writeCts.Token);
                    seq++;
                    await Task.Delay(100, writeCts.Token);
                }
            }
            catch { /* 断连止（排水窗到期旧中继会话关闭） */ }
        });
        var readTask = Task.Run(async () =>
        {
            List<int> seqs = [];
            var pending = new StringBuilder();
            var buf = new byte[4096];
            try
            {
                while (true)
                {
                    var n = await seqStream.ReadAsync(buf);
                    if (n == 0) break; // 旧会话关闭 → channel 收尾 FIN
                    pending.Append(Encoding.ASCII.GetString(buf, 0, n));
                    while (true)
                    {
                        var line = pending.ToString();
                        var nl = line.IndexOf('\n');
                        if (nl < 0) break;
                        if (int.TryParse(line.AsSpan(0, nl), out var s)) seqs.Add(s);
                        pending.Remove(0, nl + 1);
                    }
                }
            }
            catch { /* 连接重置 */ }
            return seqs;
        });

        // ③ 回切：3s 周期 → 0x73 → 全新 0x70 → 扰动耗尽命中 → direct（明细 relay_to_direct）
        await WaitMappingStateAsync(httpA, "direct", seconds: 40);
        var root = await GetAsync(httpA, "/api/mappings");
        var item = root.GetProperty("data").GetProperty("items").EnumerateArray().Single();
        Assert.Equal("relay_to_direct", item.GetProperty("detail").GetString());
        _output.WriteLine("M2-19|relay-fallback-back|state=direct|detail=relay_to_direct");

        // ④ 排水窗到期旧中继会话关闭 → 序号流断连止；已收序号从 0 严格递增（无缺无重无乱序——
        //    断连后未送达的尾部写入不在顺序性语义内，NET-75）
        var received = await readTask.WaitAsync(TimeSpan.FromSeconds(15)); // 排水 2s + 余量
        await writeTask.WaitAsync(TimeSpan.FromSeconds(5)); // 写侧随断连自行收尾
        Assert.NotEmpty(received);
        for (var i = 0; i < received.Count; i++)
            Assert.Equal(i, received[i]);

        // ⑤ 新连接走新直连路径完整往返
        await AssertEchoRoundtripAsync(IPAddress.Parse("127.0.0.4"), localPort, RandomGenerator.Bytes(40 * 1024));
        await AssertEchoRoundtripAsync(IPAddress.Parse("127.0.0.4"), localPort, RandomGenerator.Bytes(3 * 1024));
    }

    // ── M2-32 场景 A-6（09 §2.2/§2.3、OQ-4/7）─────────────────────────

    /// <summary>A-6 断言①②：SymmetricRandom（随机分配导演端口）→ 端口预测失配 + APDF 身份过滤拒绝 →
    /// TCP 打洞双侧必 miss；目标 B 回退开（peers.json+服务端中继开）→ 0x74 → relay 态可访问（经中继密文
    /// 转发载荷往返）；同世界目标 C 无 peers.json 条目（默认关）→ failed。断言④（回切排水切直连数据连续）
    /// 由 M2_19 用例承担（同 A-6 回切路径，周期缩短缝下序号载荷校验）。</summary>
    [Fact]
    public async Task M2_32_A6_SymmetricRandom必败_B回退开relay可访问_C回退关failed()
    {
        await StartSimulatorAsync(
            (IPAddress.Parse("127.0.0.4"), UdpNatMode.FullCone),
            (IPAddress.Parse("127.0.0.5"), UdpNatMode.FullCone), tcpStub: false);
        var ipC = IPAddress.Parse("127.0.0.6");
        _sim!.RegisterClient(ipC, UdpNatMode.FullCone, "C"); // 三方：C 侧 UDP NAT（中继 JOIN 承载）
        await StartTcpSimulatorAsync(mode: TcpNatMode.SymmetricRandom, ipC: ipC);
        var group = await CreateGroupAsync();
        var a = await SeedClientAsync("a6-a", IPAddress.Parse("127.0.0.4"), group, punchConcurrency: 2);
        var b = await SeedClientAsync("a6-b", IPAddress.Parse("127.0.0.5"), group, punchConcurrency: 2);
        var c = await SeedClientAsync("a6-c", ipC, group, punchConcurrency: 2);
        var httpA = await StartRuntimeAsync(a);
        var httpB = await StartRuntimeAsync(b);
        var httpC = await StartRuntimeAsync(c);
        await WaitPhaseAsync(httpA, "running");
        await WaitPhaseAsync(httpB, "running");
        await WaitPhaseAsync(httpC, "running");

        // 仅 B 开（设备级回退配置，D3/OQ-10）；C 无条目=默认关（PRD 06 §2）
        var put = await PutAsync(httpA, $"/api/peers/{b.DeviceId}", new { relayFallback = true });
        Assert.Equal(0, put.GetProperty("code").GetInt32());

        var echoPort = FreePort();
        StartEcho(echoPort);
        var localPortB = FreePort();
        var mappingB = await CreateAndEnableMappingAsync(httpA, (ushort)localPortB, b.RemoteCode, (ushort)echoPort);

        // 断言①：随机分配必 miss（10s 打洞预算）→ 回退开 → 0x74 → relay（映射态+中继密文转发往返）
        await WaitMappingStateAsync(httpA, "relay", seconds: 40, mappingB);
        await AssertEchoRoundtripAsync(IPAddress.Parse("127.0.0.4"), localPortB, RandomGenerator.Bytes(40 * 1024));
        _output.WriteLine("A6|nat=SymmetricRandom|target=B|fallback=on|state=relay");

        // 断言②：同世界目标 C 回退关 → 打洞必败后无 0x74 → failed（明细为打洞失败而非服务端拒绝）
        var localPortC = FreePort();
        var mappingC = await CreateAndEnableMappingAsync(httpA, (ushort)localPortC, c.RemoteCode, (ushort)echoPort);
        await WaitMappingStateAsync(httpA, "failed", seconds: 40, mappingC);
        var root = await GetAsync(httpA, "/api/mappings");
        var itemC = root.GetProperty("data").GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("mappingId").GetGuid() == mappingC);
        Assert.DoesNotContain("server_", itemC.GetProperty("detail").GetString()); // 在线正常，失败源于打洞
        _output.WriteLine("A6|nat=SymmetricRandom|target=C|fallback=off|state=failed");
    }

    /// <summary>A-6 断言③ UdpBlocked 变体：B 的 UDP 出站全丢（STUN/JOIN 均不可达）→ TCP 打洞（SymmetricRandom
    /// 亦必败）→ 回退 → JOIN 先 UDP（10s 超时）→ **TCP 承载兜底**（FR-S-704，两端承载可异构 02 §6.2：
    /// A 保持 UDP、B 落 TCP）→ 中继仍建立且可访问。</summary>
    [Fact]
    public async Task M2_32_A6_UdpBlocked变体_TCP中继承载relay可访问()
    {
        await StartSimulatorAsync(
            (IPAddress.Parse("127.0.0.4"), UdpNatMode.FullCone),
            (IPAddress.Parse("127.0.0.5"), UdpNatMode.UdpBlocked), tcpStub: false);
        await StartTcpSimulatorAsync(mode: TcpNatMode.SymmetricRandom);
        var group = await CreateGroupAsync();
        var a = await SeedClientAsync("a6-ub-a", IPAddress.Parse("127.0.0.4"), group, punchConcurrency: 2);
        var b = await SeedClientAsync("a6-ub-b", IPAddress.Parse("127.0.0.5"), group, punchConcurrency: 2);
        var httpA = await StartRuntimeAsync(a);
        var httpB = await StartRuntimeAsync(b);
        await WaitPhaseAsync(httpA, "running");
        await WaitPhaseAsync(httpB, "running");
        var put = await PutAsync(httpA, $"/api/peers/{b.DeviceId}", new { relayFallback = true });
        Assert.Equal(0, put.GetProperty("code").GetInt32());

        var echoPort = FreePort();
        StartEcho(echoPort);
        var localPort = FreePort();
        await CreateAndEnableMappingAsync(httpA, (ushort)localPort, b.RemoteCode, (ushort)echoPort);

        // B JOIN UDP 重发 10s 超时计入回退预算（RelayFallbackTimeout=30s 独立计量覆盖）
        await WaitMappingStateAsync(httpA, "relay", seconds: 50);
        await AssertEchoRoundtripAsync(IPAddress.Parse("127.0.0.4"), localPort, RandomGenerator.Bytes(40 * 1024));
        await AssertEchoRoundtripAsync(IPAddress.Parse("127.0.0.4"), localPort, RandomGenerator.Bytes(3 * 1024));
        _output.WriteLine("A6-VARIANT|nat=UdpBlocked|carrier=relay-tcp|state=relay");
    }
}

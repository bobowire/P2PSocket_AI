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

namespace P2P.IntegrationTests;

/// <summary>
/// M1-35 集成场景自动化 A-1~A-4（09 §2.3，任务清单完成判定：四场景集成测试全绿、无需管理员权限可在 CI 运行）。
/// 三进程 in-proc：服务端 + 双客户端（各自独立 baseDir）；网卡以 StubNicManager 替身、虚拟 IP 用 127.0.0.x
/// 回环别名（A-4 隔离语义在 127.0.0.0/8 内等价成立）；A-3/A-4 打洞链路经 NatSimulator（STUN 派生 :3478，TD-07）。
/// 全运行时重组件专用集合（与 ClientRuntimeIntegrationTests 共用）：每用例拉起双 Kestrel+服务端+打洞链路，
/// 相互串行避免与轻量类并行时叠加满载偶发（沿 M1-31 加固惯例）。
/// </summary>
[Collection("heavy-runtime")]
public sealed class ScenarioIntegrationTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DeviceRegistry _registry = new();
    private readonly List<SignalingCoordinator> _signalings = [];
    private readonly List<ControlServer> _servers = [];
    private readonly StubNicManager _nic = new();
    private readonly List<ClientRuntime> _runtimes = [];
    private readonly List<HttpClient> _https = [];
    private readonly List<TcpListener> _echoListeners = [];
    private CancellationTokenSource? _echoCts;
    private UdpNatSimulator? _sim;
    private StubStunUpstream? _upstream;
    private TcpStunStub? _tcpStun;
    private StubFactory _factory = null!;
    private string _rootDir = null!;
    private int _port;

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
        foreach (var s in _servers) await s.DisposeAsync();
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
        var router = new ControlMessageRouter(
            new RegistrationService(_factory, _registry, audit),
            new UserService(_factory, audit),
            new GroupService(_factory, _registry),
            signaling,
            new MappingService(_factory, audit),
            audit);
        var server = new ControlServer(_factory, _registry, router.DispatchAsync);
        await server.StartAsync(new IPEndPoint(IPAddress.Loopback, port));
        _signalings.Add(signaling);
        _servers.Add(server);
        return server;
    }

    // ── 世界构建 ───────────────────────────────────────────────────────

    private sealed record SeededClient(Guid DeviceId, byte[] DeviceSecret, string RemoteCode, string Dir, IPAddress Ip);

    /// <summary>种已注册设备（静态密钥对/DeviceSecret/远程码/虚拟 IP 别名）+ 客户端 state.json 与 settings。</summary>
    private async Task<SeededClient> SeedClientAsync(string name, IPAddress ip, Group? commonGroup)
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
            // M2-16：tcp 映射改走 TCP 打洞；回环恒等 NAT（无端口平移）下 N=1 直连命中——
            // N>1 端口预测矩阵归 M2-30 NatSimulator-TCP / M2-31 A-5
            PunchConcurrency = 1,
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

    /// <summary>启动客户端运行时（网卡替身 + 打洞 socket 绑内网别名缝）。</summary>
    private async Task<HttpClient> StartRuntimeAsync(SeededClient c)
    {
        var runtime = new ClientRuntime(new ClientRuntimeOptions
        {
            BaseDir = c.Dir,
            NicOverride = _nic,
            PunchBindOverride = c.Ip,
        });
        await runtime.StartAsync();
        _runtimes.Add(runtime);
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{runtime.WebPort}") };
        _https.Add(http);
        return http;
    }

    /// <summary>NatSimulator 世界：STUN 派生 :3478（TD-07）+ 上游替身 + 双客户端 NAT 注册；
    /// TCP 打洞（M2-16）另拉 TCP STUN 替身（同宿主 :3478/TCP，与 UDP 模拟器不同协议互不冲突）。</summary>
    private async Task StartSimulatorAsync((IPAddress Ip, UdpNatMode Mode) a, (IPAddress Ip, UdpNatMode Mode) b)
    {
        _upstream = new StubStunUpstream();
        await _upstream.StartAsync();
        _sim = new UdpNatSimulator(new UdpNatOptions { Upstream = _upstream.Endpoint, StunPort = 3478 });
        await _sim.StartAsync();
        _sim.RegisterClient(a.Ip, a.Mode, "A");
        _sim.RegisterClient(b.Ip, b.Mode, "B");
        _tcpStun = new TcpStunStub();
        _tcpStun.Start();
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

    /// <summary>轮询首条映射状态（打洞异步完成：direct/failed 终态或 punching 中间态）。</summary>
    private static async Task<string> WaitMappingStateAsync(HttpClient http, string state, int seconds = 30)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        string current = "";
        while (!cts.IsCancellationRequested)
        {
            var root = await GetAsync(http, "/api/mappings");
            var items = root.GetProperty("data").GetProperty("items");
            current = items.GetArrayLength() == 0 ? "" : items[0].GetProperty("state").GetString()!;
            if (current == state) return current;
            await Task.Delay(200, cts.Token);
        }
        Assert.Fail($"等待映射状态 {state} 超时（当前 {current}）");
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
}

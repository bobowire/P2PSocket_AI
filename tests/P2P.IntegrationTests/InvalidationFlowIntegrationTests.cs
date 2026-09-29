using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using P2P.Client.Hosting;
using P2P.Client.Storage;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using P2P.Nic;
using P2P.Server.Data;
using P2P.Server.Services;
using Xunit;

namespace P2P.IntegrationTests;

/// <summary>
/// M2-15 ControlClient 下行扩展集成测试（02 §2.4、05 §4、04 §2.8、TD-16）：
/// 0x75 → 受影响映射 invalid+停转发+WS mapping_state、newCapability 即时降级+WS login_state；
/// 0x41 提示帧 → WS device_list（前端 refetch，本地不缓存）；0x14 端点 → 新码持久化 state.json+库同步。
/// 拓扑：in-proc ControlServer + 全 ClientRuntime（同 ClientRuntimeIntegrationTests 口径：
/// 虚拟 IP 127.0.0.1 模拟、网卡替身、真实打洞器 STUN 不可达 → failed 确定性终态）。
/// 全运行时重组件专用集合（避免并行叠加满载偶发）。
/// </summary>
[Collection("heavy-runtime")]
public sealed class InvalidationFlowIntegrationTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    private readonly DeviceRegistry _registry = new();
    private readonly List<SignalingCoordinator> _signalings = [];
    private readonly List<RelayService> _relays = [];
    private readonly List<ControlServer> _servers = [];
    private readonly StubNicManager _nic = new();
    private StubFactory _factory = null!;
    private string _rootDir = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        _factory = new StubFactory(CreateDb);
        using (var init = _factory.CreateDbContext())
            DbInitializer.Initialize(init);
        var server = await StartServerAsync(0);
        _port = server.LocalEndPoint!.Port;
        _rootDir = Path.Combine(Path.GetTempPath(), $"p2p-it-inv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_rootDir);
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _servers) await s.DisposeAsync();
        foreach (var r in _relays) await r.DisposeAsync();
        foreach (var s in _signalings) await s.DisposeAsync();
        await Task.Delay(200); // 服务端收尾与夹具销毁竞态宽限（同族夹具同法）
        try { _db.Dispose(); }
        catch (InvalidOperationException) { }
        try { Directory.Delete(_rootDir, true); } catch (IOException) { }
    }

    private AppDbContext CreateDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_db.DataSource).Options);

    private async Task<ControlServer> StartServerAsync(int port)
    {
        var audit = new AuditLogger(_factory);
        var signaling = new SignalingCoordinator(_factory, _registry, new Authorizer(_factory), audit);
        var relay = new RelayService(_factory, _registry, signaling.ResolveRelayPeers);
        var router = new ControlMessageRouter(
            new RegistrationService(_factory, _registry, audit),
            new UserService(_factory, audit),
            new GroupService(_factory, _registry, audit),
            signaling,
            new MappingService(_factory, audit),
            relay,
            new StatsService(_factory, audit),
            audit);
        var server = new ControlServer(_factory, _registry, router.DispatchAsync);
        await server.StartAsync(new IPEndPoint(IPAddress.Loopback, port));
        _signalings.Add(signaling);
        _relays.Add(relay);
        _servers.Add(server);
        return server;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private sealed record SeededDevice(Guid DeviceId, byte[] DeviceSecret, string RemoteCode);

    /// <summary>种已注册设备 + 服务端映射行（Enabled=true）+ A/B 共同分组 + 客户端侧 state.json/settings。</summary>
    private async Task<SeededDevice> SeedRegisteredWithMappingAsync(string name, SeededDevice? peer)
    {
        await using var db = _factory.CreateDbContext();
        var secret = RandomGenerator.Bytes(32);
        var device = new Device
        {
            Id = Guid.NewGuid(),
            DeviceName = name,
            Os = "windows",
            ClientVersion = "0.1.0",
            MacCode = $"IT-{Guid.NewGuid():N}"[..16],
            RemoteCode = FakeRemoteCode(),
            VirtualIp = "127.0.0.1", // 测试模拟（M1-35 口径）
            StaticPubKey = new byte[65],
            DeviceSecret = secret,
            OwnerUserId = null,
            LastSeenAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
        };
        db.Devices.Add(device);

        ushort localPort = 0;
        if (peer is not null)
        {
            localPort = (ushort)FreePort();
            db.Mappings.Add(new Mapping
            {
                Id = Guid.NewGuid(),
                OwnerDeviceId = device.Id,
                Name = "inv",
                LocalPort = localPort,
                Proto = "tcp",
                TargetDeviceId = peer.DeviceId,
                TargetAddr = "self",
                TargetPort = 80,
                Enabled = true,
                CreatedAt = DateTime.UtcNow,
            });
            var group = new Group
            {
                Id = Guid.NewGuid(),
                Name = $"inv-{Guid.NewGuid():N}"[..16],
                OwnerUserId = db.Users.Single(u => u.IsAdmin).Id,
                IsDefault = false,
                JoinPolicy = "free",
                CreatedAt = DateTime.UtcNow,
            };
            db.Groups.Add(group);
            db.GroupMembers.Add(new GroupMember { Id = Guid.NewGuid(), GroupId = group.Id, DeviceId = device.Id, Approved = true });
            db.GroupMembers.Add(new GroupMember { Id = Guid.NewGuid(), GroupId = group.Id, DeviceId = peer.DeviceId, Approved = true });
        }
        await db.SaveChangesAsync();

        var dir = Path.Combine(_rootDir, name);
        Directory.CreateDirectory(dir);
        var store = new StateStore(dir);
        store.State.DeviceId = device.Id;
        store.State.DeviceSecret = secret;
        store.State.StaticPrivateKey = EcKeyPair.Generate().ExportPrivateKey();
        store.State.RemoteCode = device.RemoteCode;
        store.State.VirtualIp = device.VirtualIp;
        if (peer is not null)
            store.State.Mappings.Add(new StoredMapping(
                db.Mappings.AsNoTracking().Single(m => m.OwnerDeviceId == device.Id).Id, "inv",
                localPort, "tcp", peer.RemoteCode, "self", 80, Enabled: true));
        await store.SaveAsync();
        var settings = new SettingsStore(dir);
        await settings.SaveAsync(new ClientSettings
        {
            ServerAddrs = [$"127.0.0.1:{_port}"],
            LocalWebPort = FreePort(),
        });
        return new SeededDevice(device.Id, secret, device.RemoteCode);
    }

    private static string FakeRemoteCode()
    {
        const string alphabet = "0123456789abc";
        return new string(RandomGenerator.Bytes(6).Select(b => alphabet[b % alphabet.Length]).ToArray());
    }

    // ── HTTP 辅助（envelope { code, msg, data }，04 §1）───────────────

    private static async Task<JsonElement> GetAsync(HttpClient http, string path)
    {
        using var response = await http.GetAsync(path);
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(0, root.GetProperty("code").GetInt32());
        return root.GetProperty("data").Clone();
    }

    private static async Task<JsonElement> PostAsync(HttpClient http, string path)
    {
        using var response = await http.PostAsync(path, content: null);
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(0, root.GetProperty("code").GetInt32());
        return root.GetProperty("data").Clone();
    }

    private static async Task<string> GetMappingStateAsync(HttpClient http)
    {
        var items = (await GetAsync(http, "/api/mappings")).GetProperty("items");
        return items.GetArrayLength() == 0 ? "" : items[0].GetProperty("state").GetString()!;
    }

    private static async Task WaitStateAsync(HttpClient http, string state)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string current = "";
        while (!cts.IsCancellationRequested)
        {
            current = await GetMappingStateAsync(http);
            if (current == state) return;
            await Task.Delay(200, cts.Token);
        }
        Assert.Fail($"等待映射状态 {state} 超时（当前 {current}）");
    }

    // ── WS 辅助（事件收件箱；LocalWebApiIntegrationTests 同法）─────────

    private sealed class WsTap : IAsyncDisposable
    {
        private readonly System.Net.WebSockets.ClientWebSocket _ws = new();
        private readonly Channel<JsonElement> _events = Channel.CreateUnbounded<JsonElement>();
        private Task _loop = Task.CompletedTask;

        public static async Task<WsTap> ConnectAsync(string url)
        {
            var tap = new WsTap();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await tap._ws.ConnectAsync(new Uri(url), cts.Token);
            tap._loop = tap.ReceiveLoopAsync();
            return tap;
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[16 * 1024];
            using var ms = new MemoryStream();
            try
            {
                while (_ws.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    System.Net.WebSockets.WebSocketReceiveResult r;
                    do
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        r = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                        ms.Write(buffer, 0, r.Count);
                    } while (!r.EndOfMessage);
                    _events.Writer.TryWrite(JsonDocument.Parse(ms.ToArray()).RootElement.Clone());
                    ms.SetLength(0);
                }
            }
            catch { /* 连接关闭 */ }
            finally { _events.Writer.TryComplete(); }
        }

        /// <summary>等待指定事件（跳过其它事件；超时抛 TimeoutException）。</summary>
        public async Task<JsonElement> NextAsync(string ev, TimeSpan? timeout = null)
        {
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
            while (true)
            {
                var evt = await _events.Reader.ReadAsync(cts.Token);
                if (evt.TryGetProperty("ev", out var name) && name.GetString() == ev) return evt;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _events.Writer.TryComplete();
            if (_ws.State == System.Net.WebSockets.WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await _ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "done", cts.Token); }
                catch { /* 服务端可能已停 */ }
            }
            _ws.Dispose();
            try { await _loop; } catch { /* 收环随连接终止 */ }
        }
    }

    private sealed class RuntimeHarness : IAsyncDisposable
    {
        public required ClientRuntime Runtime { get; init; }
        public required HttpClient Http { get; init; }
        public required string WsUrl { get; init; }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await Runtime.DisposeAsync();
        }
    }

    /// <summary>启动全运行时至稳定态（running + 映射 failed：恢复完成且 0x60 已同步，服务端行 Enabled=true）。</summary>
    private async Task<RuntimeHarness> StartRuntimeAsync(SeededDevice a, string dirA)
    {
        var runtime = new ClientRuntime(new ClientRuntimeOptions { BaseDir = dirA, NicOverride = _nic });
        await runtime.StartAsync();
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{runtime.WebPort}") };
        var harness = new RuntimeHarness
        {
            Runtime = runtime,
            Http = http,
            WsUrl = $"ws://127.0.0.1:{runtime.WebPort}/ws/status",
        };
        Assert.Equal("running", (await GetAsync(http, "/api/system/state")).GetProperty("phase").GetString());
        await WaitStateAsync(http, "failed");
        return harness;
    }

    // ── ① 0x75：受影响映射 invalid+停转发+WS 事件；newCapability 即时降级 ──

    [Fact]
    public async Task 失效推送_映射置invalid并广播WS事件_能力降级即时生效()
    {
        var b = await SeedRegisteredWithMappingAsync("if-b", null);
        var a = await SeedRegisteredWithMappingAsync("if-a", b);
        await using var harness = await StartRuntimeAsync(a, Path.Combine(_rootDir, "if-a"));
        await using var tap = await WsTap.ConnectAsync(harness.WsUrl);
        var pusher = new InvalidationPusher(_factory, _registry);

        // 0x75（remote_code_reset）：本人映射全量失效——引擎拆监听停转发、置 invalid（前端置灰）
        await pusher.PushOwnedAsync(a.DeviceId, InvalidationReason.RemoteCodeReset);
        var evt = await tap.NextAsync("mapping_state");
        Assert.Equal("invalid", evt.GetProperty("state").GetString());
        Assert.Contains("RemoteCodeReset", evt.GetProperty("reason").GetString());
        await WaitStateAsync(harness.Http, "invalid"); // /api/mappings 快照同口径（真相面）

        // 0x75 携 newCapability=Passive（用户禁用降级，空映射集独立推送）：连接级状态即时降级
        await pusher.PushByOwnerAsync(
            new Dictionary<Guid, Guid[]> { [a.DeviceId] = [] },
            InvalidationReason.UserDisabled, CapabilityMode.Passive);
        var login = await tap.NextAsync("login_state");
        Assert.Equal("passive", login.GetProperty("mode").GetString());
        Assert.Equal("passive", (await GetAsync(harness.Http, "/api/auth/me")).GetProperty("mode").GetString());
    }

    // ── ② 0x41：提示帧 → WS device_list（前端 refetch，本地不缓存）────────

    [Fact]
    public async Task 设备列表提示帧_广播device_list事件()
    {
        var b = await SeedRegisteredWithMappingAsync("dl-b", null);
        var a = await SeedRegisteredWithMappingAsync("dl-a", b);
        await using var harness = await StartRuntimeAsync(a, Path.Combine(_rootDir, "dl-a"));
        await using var tap = await WsTap.ConnectAsync(harness.WsUrl);

        // 服务端侧会话直接推 0x41（M2-12 触发点同帧型）
        var session = _registry.TryGet(a.DeviceId);
        Assert.NotNull(session);
        await session!.PushAsync(new DeviceListUpdate(session.NextSeq(), session.ServerTimestamp(),
            MsgType.DeviceListUpdate));

        await tap.NextAsync("device_list"); // 到达即通过（提示帧无载荷，TD-16：真相在前端 refetch）
    }

    // ── ③ 0x14 端点：新码持久化 state.json + 服务端库同步 + WS 提示 ────────

    [Fact]
    public async Task 远程码重置端点_新码持久化并同步服务端()
    {
        var b = await SeedRegisteredWithMappingAsync("rc-b", null);
        var a = await SeedRegisteredWithMappingAsync("rc-a", b);
        var dirA = Path.Combine(_rootDir, "rc-a");
        await using var harness = await StartRuntimeAsync(a, dirA);
        await using var tap = await WsTap.ConnectAsync(harness.WsUrl);

        var data = await PostAsync(harness.Http, "/api/device/reset-remote-code");
        var newCode = data.GetProperty("remoteCode").GetString()!;
        Assert.False(string.IsNullOrEmpty(newCode));
        Assert.NotEqual(a.RemoteCode, newCode); // 旧码立即失效（FR-S-903）

        // state.json 持久化（03 §5）：重启后新码仍在
        var reloaded = new StateStore(dirA);
        reloaded.Load();
        Assert.Equal(newCode, reloaded.State.RemoteCode);

        // 本机设备视图即时反映（04 §2.1 GET /api/device 读 state）
        Assert.Equal(newCode, (await GetAsync(harness.Http, "/api/device")).GetProperty("remoteCode").GetString());

        // 服务端库行同步（后续 0x60/0x70 用新码解析）
        await using var db = _factory.CreateDbContext();
        Assert.Equal(newCode, await db.Devices.AsNoTracking()
            .Where(d => d.Id == a.DeviceId).Select(d => d.RemoteCode).SingleAsync());

        await tap.NextAsync("device_list"); // WS 提示：本机码已变，前端刷新设备视图
    }
}

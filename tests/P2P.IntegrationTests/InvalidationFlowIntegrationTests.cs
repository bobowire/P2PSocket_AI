using System.Net;
using System.Net.Sockets;
using System.Text;
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
/// M2-33 场景 A-8/A-9（09 §2.3）：白名单三段（段内放行/段外 4002/移除联动 0x75 按映射 owner 路由）、
/// 远程码重置链（旧码 4003 → 存量失效提示 → 新码重建）——服务端失效链全接线（同 AdminServiceTests 口径）。
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
        // M2-33 A-8/A-9：真实失效链须全接线（0x63 移段→0x75、0x14 重置→PushTargeting→0x75）——
        // 同 AdminServiceTests 口径（invalidation/listPusher/LanSegmentService 尾参）
        var invalidation = new InvalidationPusher(_factory, _registry);
        var pusher = new DeviceListPusher(_factory, _registry);
        var router = new ControlMessageRouter(
            new RegistrationService(_factory, _registry, audit, invalidation: invalidation, listPusher: pusher),
            new UserService(_factory, audit, invalidation: invalidation),
            new GroupService(_factory, _registry, audit, pusher, invalidation),
            signaling,
            new MappingService(_factory, audit),
            relay,
            new StatsService(_factory, audit),
            audit,
            new LanSegmentService(_factory, _registry, audit));
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

    /// <summary>种已注册设备 + 服务端映射行（Enabled=true）+ A/B 共同分组 + 客户端侧 state.json/settings。
    /// seedMapping=false（M2-33 A-8）：只保共同分组（L2 可见性前提），映射由用例经本地 API 现建。</summary>
    private async Task<SeededDevice> SeedRegisteredWithMappingAsync(string name, SeededDevice? peer,
        bool seedMapping = true)
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
        var mappingId = Guid.NewGuid();
        if (peer is not null)
        {
            if (seedMapping)
            {
                localPort = (ushort)FreePort();
                db.Mappings.Add(new Mapping
                {
                    Id = mappingId,
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
            }
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
        if (peer is not null && seedMapping)
            store.State.Mappings.Add(new StoredMapping(
                mappingId, "inv",
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

    /// <summary>JSON 体 POST/PUT/DELETE（M2-33 A-8/A-9：本地 API 写路径）。</summary>
    private static async Task<JsonElement> SendJsonAsync(HttpClient http, string method, string path, object? body)
    {
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(new HttpRequestMessage(
            new HttpMethod(method), path) { Content = body is null ? null : content });
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(0, root.GetProperty("code").GetInt32());
        return root.GetProperty("data").Clone();
    }

    /// <summary>JSON 体请求并断言业务错误码（04 §1：HTTP 200 + code 表语义），返回 msg。</summary>
    private static async Task<string> SendExpectAsync(HttpClient http, string method, string path, object? body,
        int expectedCode)
    {
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(new HttpRequestMessage(
            new HttpMethod(method), path) { Content = body is null ? null : content });
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(expectedCode, root.GetProperty("code").GetInt32());
        return root.GetProperty("msg").GetString()!;
    }

    /// <summary>映射 PUT 请求体（04 §2.5 全字段必带；从现值视图构造，仅改远程码）。</summary>
    private static Dictionary<string, object?> MappingBody(JsonElement view, string remoteCode) => new()
    {
        ["name"] = view.GetProperty("name").GetString(),
        ["localPort"] = view.GetProperty("localPort").GetInt32(),
        ["proto"] = view.GetProperty("proto").GetString(),
        ["targetRemoteCode"] = remoteCode,
        ["targetAddr"] = view.GetProperty("targetAddr").GetString(),
        ["targetPort"] = view.GetProperty("targetPort").GetInt32(),
    };

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

    /// <summary>启动全运行时至稳定态（running + 映射 failed：恢复完成且 0x60 已同步，服务端行 Enabled=true）。
    /// requireMapping=false（M2-33）：无映射设备（B 侧）只候 running。</summary>
    private async Task<RuntimeHarness> StartRuntimeAsync(SeededDevice a, string dirA, bool requireMapping = true)
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
        if (requireMapping)
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

    // ── ④ A-8 白名单三段（09 §2.3，M2-33）：段内放行 / 段外 4002 / 移除联动 0x75 ──

    [Fact]
    public async Task A8_白名单三段_段内放行段外4002_移除后存量映射失效()
    {
        var b = await SeedRegisteredWithMappingAsync("a8-b", null);
        var a = await SeedRegisteredWithMappingAsync("a8-a", b, seedMapping: false); // 映射现建，分组保留
        await using var hb = await StartRuntimeAsync(b, Path.Combine(_rootDir, "a8-b"), requireMapping: false);
        await using var ha = await StartRuntimeAsync(a, Path.Combine(_rootDir, "a8-a"), requireMapping: false);
        await using var tap = await WsTap.ConnectAsync(ha.WsUrl);

        // 段一：B 开放 192.168.5.0/24（本地 API → 0x63 → 服务端行）
        var seg = await SendJsonAsync(hb.Http, "POST", "/api/lan-segments", new { cidr = "192.168.5.0/24" });
        var segmentId = seg.GetProperty("segmentId").GetGuid();
        await using (var db = _factory.CreateDbContext())
            Assert.True(await db.LanSegments.AsNoTracking()
                .AnyAsync(s => s.Id == segmentId && s.DeviceId == b.DeviceId));

        // 段二段内放行：A 建映射 targetAddr=192.168.5.10 → 0x60 过 L3（SEC-52 第一道）；
        // 启用 → 0x70 再过 L3 → 打洞（STUN 不可达 → failed 确定性终态，同族口径）
        var created = await SendJsonAsync(ha.Http, "POST", "/api/mappings", new
        {
            name = "nas-web",
            localPort = FreePort(),
            proto = "tcp",
            targetRemoteCode = b.RemoteCode,
            targetAddr = "192.168.5.10",
            targetPort = 80,
        });
        var mappingId = created.GetProperty("mappingId").GetGuid();
        await PostAsync(ha.Http, $"/api/mappings/{mappingId}/enable");
        await WaitStateAsync(ha.Http, "failed");

        // 段二段外 4002：targetAddr=10.9.9.9 不落任何段 → 服务端 0x60 L3 拒；本地不落库（仅一条映射）
        await SendExpectAsync(ha.Http, "POST", "/api/mappings", new
        {
            name = "out-seg",
            localPort = FreePort(),
            proto = "tcp",
            targetRemoteCode = b.RemoteCode,
            targetAddr = "10.9.9.9",
            targetPort = 80,
        }, ErrorCode.TargetAddrNotAllowed);

        // 段三移除联动：B 删段 → 0x75(lan_segment_removed, [M]) 按映射 owner 路由达 A
        //（M2-33 修正推送方向）→ 引擎置 invalid 停转发 + WS mapping_state（FR-C-702）。
        // tap 先于 enable 连接：跳过 punching/failed 中间态事件，候至 invalid 终态
        await SendJsonAsync(hb.Http, "DELETE", $"/api/lan-segments/{segmentId}", null);
        JsonElement evt;
        do { evt = await tap.NextAsync("mapping_state"); }
        while (evt.GetProperty("state").GetString() != "invalid");
        Assert.Contains("LanSegmentRemoved", evt.GetProperty("reason").GetString());
        await WaitStateAsync(ha.Http, "invalid");
        await using (var db = _factory.CreateDbContext())
            Assert.False(await db.LanSegments.AsNoTracking().AnyAsync(s => s.Id == segmentId));
    }

    // ── ⑤ A-9 远程码重置链（09 §2.3，M2-33）：旧码 4003 → 存量失效提示 → 新码重建 ──

    [Fact]
    public async Task A9_远程码重置链_旧码4003_存量失效提示_新码重建恢复()
    {
        var b = await SeedRegisteredWithMappingAsync("a9-b", null);
        var a = await SeedRegisteredWithMappingAsync("a9-a", b); // 存量映射：A→B（B 旧码）
        await using var ha = await StartRuntimeAsync(a, Path.Combine(_rootDir, "a9-a"));
        await using var hb = await StartRuntimeAsync(b, Path.Combine(_rootDir, "a9-b"), requireMapping: false);
        await using var tap = await WsTap.ConnectAsync(ha.WsUrl);

        // B 重置远程码（本地 API → 0x14）→ 服务端 PushTargeting → A 存量映射 0x75(remote_code_reset)
        var reset = await PostAsync(hb.Http, "/api/device/reset-remote-code");
        var newCode = reset.GetProperty("remoteCode").GetString()!;
        Assert.NotEqual(b.RemoteCode, newCode);

        var evt = await tap.NextAsync("mapping_state");
        Assert.Equal("invalid", evt.GetProperty("state").GetString());
        Assert.Contains("RemoteCodeReset", evt.GetProperty("reason").GetString());
        await WaitStateAsync(ha.Http, "invalid"); // 前端置灰提示重新配置（FR-S-903）

        // 旧码重建被拒：设备视图已无旧码 → 4003（ResolvePeer 与 0x60 同口径；映射现值不动）
        var current = (await GetAsync(ha.Http, "/api/mappings")).GetProperty("items")[0];
        var mappingId = current.GetProperty("mappingId").GetGuid();
        await SendExpectAsync(ha.Http, "PUT", $"/api/mappings/{mappingId}",
            MappingBody(current, b.RemoteCode), ErrorCode.RemoteCodeInvalid);

        // 新码重建：PUT 换码 → 0x60 更新服务端行 → 引擎重启监听再打洞 → failed 终态=恢复可配置
        await SendJsonAsync(ha.Http, "PUT", $"/api/mappings/{mappingId}", MappingBody(current, newCode));
        await WaitStateAsync(ha.Http, "failed");
        var after = (await GetAsync(ha.Http, "/api/mappings")).GetProperty("items")[0];
        Assert.Equal(newCode, after.GetProperty("targetRemoteCode").GetString());
    }
}

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using P2P.Client.Control;
using P2P.Client.Mapping;
using P2P.Client.Punch;
using P2P.Client.Registration;
using P2P.Client.Storage;
using P2P.Client.Tunnel;
using P2P.Client.Web;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using P2P.Server.Data;
using P2P.Server.Services;
using Xunit;

namespace P2P.IntegrationTests;

/// <summary>
/// M1-29 本地 Web API 集成测试（任务清单完成判定：接口契约=全端点响应形状与 04 §2 一致；
/// WS 事件到达=mapping_state/mapping_stats/login_state 三类均实测收到）。
/// 拓扑：in-proc ControlServer + LocalApiServices 全束 + 127.0.0.1 随机端口 WebApplication；
/// 流量路径用内存传输对 + 双端真实 PTP 握手（替身打洞器直连，02 §4.1 全语义仍走真）。
/// </summary>
public sealed class LocalWebApiIntegrationTests : IAsyncLifetime
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
        _rootDir = Path.Combine(Path.GetTempPath(), $"p2p-it-web-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_rootDir);
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _servers) await s.DisposeAsync();
        foreach (var r in _relays) await r.DisposeAsync();
        foreach (var s in _signalings) await s.DisposeAsync();
        // 服务端收尾审计/在线落库与服务端销毁并发时，SqliteConnection.Close 内部枚举可能竞态
        // （与 GroupTests 同源的已知瞬态）：宽限后仍异常则吞掉，不连坐测试结果
        await Task.Delay(200);
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

    /// <summary>种一台归属 admin 的已注册设备（同账号 → 与登录方互相可见）。</summary>
    private async Task<(Guid DeviceId, byte[] DeviceSecret, string RemoteCode)> SeedDeviceAsync(string name)
    {
        await using var db = _factory.CreateDbContext();
        var adminId = await db.Users.AsNoTracking()
            .Where(u => u.Username == DbInitializer.AdminUsername).Select(u => u.Id).SingleAsync();
        var secret = RandomGenerator.Bytes(32);
        var device = new Device
        {
            Id = Guid.NewGuid(),
            DeviceName = name,
            Os = "windows",
            ClientVersion = "0.1.0",
            MacCode = $"IT-{Guid.NewGuid():N}"[..16],
            RemoteCode = FakeRemoteCode(),
            VirtualIp = "100.64.0.2",
            StaticPubKey = new byte[65],
            DeviceSecret = secret,
            OwnerUserId = adminId,
            LastSeenAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return (device.Id, secret, device.RemoteCode);
    }

    private static string FakeRemoteCode()
    {
        const string alphabet = "0123456789abc";
        return new string(RandomGenerator.Bytes(6).Select(b => alphabet[b % alphabet.Length]).ToArray());
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    // ── 客户端全束 + 本地 Web 宿主 ─────────────────────────────────────

    /// <summary>打洞器代理：栈建好后注入真实现（隧道用例需要 engineA 先存在）。</summary>
    private sealed class PuncherProxy : IPuncher
    {
        private volatile IPuncher? _inner;
        public GatedPuncher Default { get; } = new();
        public void Set(IPuncher puncher) => _inner = puncher;

        public Task<PunchOutcome> InitiateAsync(Guid targetDeviceId, Guid? triggerMappingId, string proto,
            CancellationToken ct = default)
            => (_inner ?? Default).InitiateAsync(targetDeviceId, triggerMappingId, proto, ct);

        public Task<PunchOutcome> RespondAsync(PunchInvite invite, CancellationToken ct = default)
            => (_inner ?? Default).RespondAsync(invite, ct);
    }

    /// <summary>打洞桩：出队即闸死 → 稳定停留 punching；停机取消放行（真 Puncher 同样尊重 ct）。</summary>
    private sealed class GatedPuncher : IPuncher
    {
        private readonly TaskCompletionSource<PunchOutcome> _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<PunchOutcome> InitiateAsync(Guid targetDeviceId, Guid? triggerMappingId, string proto,
            CancellationToken ct = default)
        {
            try { return await _gate.Task.WaitAsync(ct); }
            catch (OperationCanceledException) { return PunchOutcome.Failure(targetDeviceId, "gate_cancelled"); }
        }

        public Task<PunchOutcome> RespondAsync(PunchInvite invite, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>打洞桩：出队即失败（failed 轨迹与手动重试用例）。</summary>
    private sealed class FailingPuncher : IPuncher
    {
        public Task<PunchOutcome> InitiateAsync(Guid targetDeviceId, Guid? triggerMappingId, string proto,
            CancellationToken ct = default)
            => Task.FromResult(PunchOutcome.Failure(targetDeviceId, "punch_timeout"));

        public Task<PunchOutcome> RespondAsync(PunchInvite invite, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>替身打洞器：内存传输对 + 双端真实 PTP 握手（流量计数路径与产品一致）。</summary>
    private sealed class TunnelPuncher : IPuncher
    {
        private readonly MappingEngine _engineA;
        private readonly MappingEngine _engineB;
        private readonly TunnelHost _hostB;
        private readonly Guid _peerA;
        private readonly Guid _peerB;
        private readonly EcKeyPair _keyA = EcKeyPair.Generate();
        private readonly EcKeyPair _keyB = EcKeyPair.Generate();

        public TunnelPuncher(MappingEngine engineA, MappingEngine engineB, TunnelHost hostB,
            Guid peerA, Guid peerB)
        {
            _engineA = engineA;
            _engineB = engineB;
            _hostB = hostB;
            _peerA = peerA;
            _peerB = peerB;
        }

        public async Task<PunchOutcome> InitiateAsync(Guid targetDeviceId, Guid? triggerMappingId, string proto,
            CancellationToken ct = default)
        {
            var sessionId = Guid.NewGuid();
            var options = new TunnelSessionOptions
            {
                KeepaliveInterval = TimeSpan.FromHours(1), // 测试期免心跳噪声
            };
            var (ta, tb) = MemoryTransport.CreatePair();
            var connectA = TunnelSession.ConnectAsync(sessionId, _peerB,
                _keyA, _keyB.ExportPublicKey(), ta, _engineA, options);
            var tHello1 = await tb.ReceiveAsync(ct) ?? throw new IOException("未收到 THello1");
            var sessionB = await TunnelSession.AcceptAsync(_peerA, tHello1,
                _keyB, _keyA.ExportPublicKey(), tb, _engineB, options);
            var sessionA = await connectA;
            _hostB.Attach(sessionB);
            return PunchOutcome.Success(sessionId, _peerB, sessionA,
                new IPEndPoint(IPAddress.Loopback, 40000), new IPEndPoint(IPAddress.Loopback, 40001));
        }

        public Task<PunchOutcome> RespondAsync(PunchInvite invite, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class ClientStack : IAsyncDisposable
    {
        public ControlClient Control = null!;
        public StateStore Store = null!;
        public SettingsStore Settings = null!;
        public ClientRegistrationService Wizard = null!;
        public MappingEngine Engine = null!;
        public PunchScheduler Scheduler = null!;
        public PuncherProxy Puncher = new();
        public TunnelHost Host = null!;
        public LocalApiServices Api = null!;
        public WebApplication App = null!;
        public HttpClient Http = null!;
        public string WsUrl = null!;

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await Api.DisposeAsync();
            await App.DisposeAsync();
            await Engine.DisposeAsync();
            await Scheduler.DisposeAsync();
            await Host.DisposeAsync();
            await Control.DisposeAsync();
        }
    }

    /// <summary>建栈：seed=true 时用已种设备登录 admin（L2 可见）；false 为全新未注册态（向导路径）。</summary>
    private async Task<ClientStack> StartStackAsync(
        (Guid DeviceId, byte[] DeviceSecret, string RemoteCode)? seed = null)
    {
        var dir = Path.Combine(_rootDir, $"s{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var stack = new ClientStack();
        stack.Control = new ControlClient(
            new[] { $"127.0.0.1:{_port}" },
            new ControlClientOptions { HeartbeatInterval = TimeSpan.FromHours(1) },
            deviceId: seed?.DeviceId, deviceSecret: seed?.DeviceSecret);
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            await stack.Control.WaitReadyAsync(cts.Token);
        if (seed is not null)
        {
            // L2：会话归属 admin 后可见同账号设备（与 MappingSync 测试同构）
            var login = await stack.Control.SendRequestAsync<UserLoginAck>(new UserLogin(
                stack.Control.NextSeq(), stack.Control.TimestampMs(), MsgType.UserLogin,
                DbInitializer.AdminUsername, DbInitializer.AdminUsername));
            Assert.True(login!.Ok);
        }

        stack.Store = new StateStore(dir);
        stack.Store.Load();
        stack.Settings = new SettingsStore(dir);
        stack.Settings.Load();
        var peers = new PeersStore(dir);
        peers.Load();
        stack.Wizard = new ClientRegistrationService(stack.Control, stack.Store, _nic);
        stack.Host = new TunnelHost();
        stack.Scheduler = new PunchScheduler(stack.Puncher);
        stack.Engine = new MappingEngine(stack.Host, stack.Scheduler, IPAddress.Loopback);
        var sync = new MappingSyncService(stack.Control, stack.Engine, stack.Store);
        stack.Api = new LocalApiServices(stack.Control, stack.Store, stack.Settings,
            peers, stack.Wizard, sync, stack.Scheduler);

        var webPort = FreePort();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{webPort}");
        var app = builder.Build();
        app.UseWebSockets();
        app.MapLocalApi(stack.Api);
        await app.StartAsync();
        stack.App = app;
        stack.Http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{webPort}") };
        stack.WsUrl = $"ws://127.0.0.1:{webPort}/ws/status";
        return stack;
    }

    // ── WS 辅助：连接 + 事件收件箱 ────────────────────────────────────

    private sealed class WsTap : IAsyncDisposable
    {
        private readonly ClientWebSocket _ws = new();
        private readonly Channel<JsonElement> _events =
            Channel.CreateUnbounded<JsonElement>(new UnboundedChannelOptions { SingleReader = true });
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
                while (_ws.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult r;
                    do
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        r = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                        ms.Write(buffer, 0, r.Count);
                    } while (!r.EndOfMessage);
                    var evt = JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
                    ms.SetLength(0);
                    _events.Writer.TryWrite(evt);
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
            if (_ws.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token); }
                    catch { /* 服务端可能已停 */ }
            }
            _ws.Dispose();
            try { await _loop; } catch { /* 收环随连接终止 */ }
        }
    }

    // ── HTTP 辅助（envelope { code, msg, data }，04 §1）───────────────

    private static async Task<(int code, JsonElement? data)> SendAsync(
        ClientStack stack, HttpMethod method, string path, object? body)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = new StringContent(
                JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
        using var response = await stack.Http.SendAsync(request);
        Assert.Equal(200, (int)response.StatusCode); // 业务错误也 HTTP 200（04 §1）
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return (root.GetProperty("code").GetInt32(),
            root.TryGetProperty("data", out var data) ? data : null);
    }

    private static Task<(int code, JsonElement? data)> PostAsync(
        ClientStack stack, string path, object? body)
        => SendAsync(stack, HttpMethod.Post, path, body);

    private static Task<(int code, JsonElement? data)> PutAsync(
        ClientStack stack, string path, object? body)
        => SendAsync(stack, HttpMethod.Put, path, body);

    private static Task<(int code, JsonElement? data)> GetAsync(
        ClientStack stack, string path) => SendAsync(stack, HttpMethod.Get, path, null);

    // ── ① 向导契约：server-test / register(default) / result / phase 迁移 ──

    [Fact]
    public async Task 向导注册_default模式_phase迁移_结果缓存_settings落盘()
    {
        await using var stack = await StartStackAsync();

        // 未注册：phase=unregistered + 通道可达 + 协议版本
        var state = await GetAsync(stack, "/api/system/state");
        Assert.Equal(ErrorCode.Ok, state.code);
        Assert.Equal("unregistered", state.data!.Value.GetProperty("phase").GetString());
        Assert.True(state.data.Value.GetProperty("serverReachable").GetBoolean());
        Assert.Equal(ProtocolVersion.Current, state.data.Value.GetProperty("protocolVersion").GetInt32());

        // server-test：可达 / 不可达
        var ok = await PostAsync(stack, "/api/wizard/server-test", new { serverAddr = $"127.0.0.1:{_port}" });
        Assert.True(ok.data!.Value.GetProperty("ok").GetBoolean());
        var bad = await PostAsync(stack, "/api/wizard/server-test", new { serverAddr = "127.0.0.1:1" });
        Assert.False(bad.data!.Value.GetProperty("ok").GetBoolean());
        Assert.Contains("127.0.0.1:1", bad.data.Value.GetProperty("detail").GetString());

        // 注册（default 模式：仅注册设备，不入组不登录）
        var reg = await PostAsync(stack, "/api/wizard/register", new
        {
            serverAddr = $"127.0.0.1:{_port}", mode = "default", deviceName = "web-dev-a",
        });
        Assert.Equal(ErrorCode.Ok, reg.code);
        var deviceId = reg.data!.Value.GetProperty("deviceId").GetGuid();
        Assert.NotEqual(Guid.Empty, deviceId);
        Assert.Matches("^[0-9abc]{6}$", reg.data.Value.GetProperty("remoteCode").GetString());
        Assert.Equal("100.64.0.2", reg.data.Value.GetProperty("virtualIp").GetString());
        Assert.NotEqual(0, reg.data.Value.GetProperty("groups").GetArrayLength()); // 服务端注册即入默认分组

        // phase → running；设备名落库；settings.json 换址落盘
        var state2 = await GetAsync(stack, "/api/system/state");
        Assert.Equal("running", state2.data!.Value.GetProperty("phase").GetString());
        await using (var db = _factory.CreateDbContext())
        {
            var row = await db.Devices.AsNoTracking().SingleAsync(d => d.Id == deviceId);
            Assert.Equal("web-dev-a", row.DeviceName);
        }
        var settings = new SettingsStore(Path.GetDirectoryName(stack.Settings.SettingsFilePath)!);
        settings.Load();
        Assert.Equal([$"127.0.0.1:{_port}"], settings.Settings.ServerAddrs);

        // result 缓存返回同结果；重复注册 1003
        var result = await GetAsync(stack, "/api/wizard/result");
        Assert.Equal(ErrorCode.Ok, result.code);
        Assert.Equal(deviceId, result.data!.Value.GetProperty("deviceId").GetGuid());
        var again = await PostAsync(stack, "/api/wizard/register", new
        { serverAddr = $"127.0.0.1:{_port}", mode = "default" });
        Assert.Equal(ErrorCode.Conflict, again.code);
    }

    // ── ② 向导 account 模式：建号+登录+建组，me 回显 ──────────────────

    [Fact]
    public async Task 向导注册_account模式_建号登录建组_登录态回显()
    {
        await using var stack = await StartStackAsync();
        var username = $"u{Guid.NewGuid():N}"[..12];

        var reg = await PostAsync(stack, "/api/wizard/register", new
        {
            serverAddr = $"127.0.0.1:{_port}", mode = "account",
            username, password = "secret123", deviceName = "web-dev-b",
        });
        Assert.Equal(ErrorCode.Ok, reg.code);
        var deviceId = reg.data!.Value.GetProperty("deviceId").GetGuid();

        // me：username + normal（登录态来自 ctx.LoginUser）
        var me = await GetAsync(stack, "/api/auth/me");
        Assert.Equal(ErrorCode.Ok, me.code);
        Assert.Equal(username, me.data!.Value.GetProperty("username").GetString());
        Assert.Equal("normal", me.data.Value.GetProperty("mode").GetString());

        // 设备查询回显三要素 + 登录账号
        var device = await GetAsync(stack, "/api/device");
        Assert.Equal(deviceId, device.data!.Value.GetProperty("deviceId").GetGuid());
        Assert.Equal(username, device.data.Value.GetProperty("username").GetString());
        Assert.Equal("normal", device.data.Value.GetProperty("capability").GetString());
        Assert.NotNull(device.data.Value.GetProperty("remoteCode").GetString());

        // 库：用户已建 + 分组"我的分组"归属该用户
        await using var db = _factory.CreateDbContext();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Username == username);
        Assert.NotEmpty(await db.Groups.AsNoTracking()
            .Where(g => g.OwnerUserId == user.Id && g.Name == "我的分组").ToListAsync());
    }

    // ── ③ 设备改名：PUT /api/device → 0x13 → 库中生效 ────────────────

    [Fact]
    public async Task 设备改名_空名1001_改名落库()
    {
        var a = await SeedDeviceAsync("web-dev-c");
        await using var stack = await StartStackAsync(a);

        var empty = await PutAsync(stack, "/api/device", new { deviceName = "" });
        Assert.Equal(ErrorCode.BadRequest, empty.code);

        var renamed = await PutAsync(stack, "/api/device", new { deviceName = "renamed-dev" });
        Assert.Equal(ErrorCode.Ok, renamed.code);
        await using var db = _factory.CreateDbContext();
        var row = await db.Devices.AsNoTracking().SingleAsync(d => d.Id == a.DeviceId);
        Assert.Equal("renamed-dev", row.DeviceName);
    }

    // ── ④ 账号端点：register/login/logout/me 全链路 + 错误码 ──────────

    [Fact]
    public async Task 账号注册登录登出_错误码与登录态维护()
    {
        var a = await SeedDeviceAsync("web-dev-d");
        await using var stack = await StartStackAsync(a);
        var username = $"u{Guid.NewGuid():N}"[..12];

        // 凭据校验：短密码 1001
        var shortPwd = await PostAsync(stack, "/api/auth/register", new { username, password = "123" });
        Assert.Equal(ErrorCode.BadRequest, shortPwd.code);

        var created = await PostAsync(stack, "/api/auth/register", new { username, password = "secret123" });
        Assert.Equal(ErrorCode.Ok, created.code);
        var duplicate = await PostAsync(stack, "/api/auth/register", new { username, password = "secret123" });
        Assert.Equal(ErrorCode.BadRequest, duplicate.code);

        // 错误密码 2001，登录态不建立
        var wrong = await PostAsync(stack, "/api/auth/login", new { username, password = "wrong-pw" });
        Assert.Equal(ErrorCode.Unauthorized, wrong.code);
        var me1 = await GetAsync(stack, "/api/auth/me");
        Assert.Null(me1.data!.Value.GetProperty("username").GetString());

        // 正确登录：mode=normal；登出：passive（FR-C-603 不重启）
        var login = await PostAsync(stack, "/api/auth/login", new { username, password = "secret123" });
        Assert.Equal(ErrorCode.Ok, login.code);
        Assert.Equal("normal", login.data!.Value.GetProperty("mode").GetString());
        var me2 = await GetAsync(stack, "/api/auth/me");
        Assert.Equal(username, me2.data!.Value.GetProperty("username").GetString());

        var logout = await PostAsync(stack, "/api/auth/logout", null);
        Assert.Equal(ErrorCode.Ok, logout.code);
        var me3 = await GetAsync(stack, "/api/auth/me");
        Assert.Null(me3.data!.Value.GetProperty("username").GetString());
        Assert.Equal("passive", me3.data.Value.GetProperty("mode").GetString());
    }

    // ── ⑤ 诊断端点形状 ────────────────────────────────────────────────

    [Fact]
    public async Task 诊断端点_队列深度形状()
    {
        var a = await SeedDeviceAsync("web-dev-e");
        await using var stack = await StartStackAsync(a);

        var diag = await GetAsync(stack, "/api/diagnostics");
        Assert.Equal(ErrorCode.Ok, diag.code);
        Assert.Equal(0, diag.data!.Value.GetProperty("punchQueueDepth").GetInt32());
        Assert.Equal(JsonValueKind.Null, diag.data.Value.GetProperty("currentPunchPeer").ValueKind);
    }

    // ── ⑤b 设备列表：0x40 转发、形状、在线判定、passive 拒发（M1-32）──

    [Fact]
    public async Task 设备列表端点_0x40转发_形状与在线判定()
    {
        var a = await SeedDeviceAsync("web-list-a");
        var b = await SeedDeviceAsync("web-list-b");
        // 种一个分组让 b 带分组摘要（SeedDeviceAsync 只写 Device 行，入组接口属 M2）
        await using (var db = _factory.CreateDbContext())
        {
            var adminId = await db.Users.AsNoTracking()
                .Where(u => u.Username == DbInitializer.AdminUsername).Select(u => u.Id).SingleAsync();
            var group = new Group { Id = Guid.NewGuid(), Name = "web-grp", OwnerUserId = adminId };
            db.Groups.Add(group);
            db.GroupMembers.Add(new GroupMember { GroupId = group.Id, DeviceId = b.DeviceId });
            await db.SaveChangesAsync();
        }
        await using var stack = await StartStackAsync(a); // admin 登录 → 同账号全可见

        var list = await GetAsync(stack, "/api/devices");
        Assert.Equal(ErrorCode.Ok, list.code);
        var items = list.data!.Value.EnumerateArray()
            .Where(d => d.GetProperty("deviceId").GetGuid() == a.DeviceId
                     || d.GetProperty("deviceId").GetGuid() == b.DeviceId)
            .ToDictionary(d => d.GetProperty("deviceId").GetGuid());

        // 形状：七字段齐（04 §2.4，camelCase）
        var mine = items[a.DeviceId];
        Assert.Equal("web-list-a", mine.GetProperty("deviceName").GetString());
        Assert.Matches("^[0-9abc]{6}$", mine.GetProperty("remoteCode").GetString());
        Assert.Equal("100.64.0.2", mine.GetProperty("virtualIp").GetString());
        Assert.True(mine.GetProperty("online").GetBoolean()); // 本机控制通道在连（registry 会话）
        Assert.Equal(0, mine.GetProperty("groups").GetArrayLength());
        Assert.Equal(0, mine.GetProperty("lanSegments").GetArrayLength());

        var other = items[b.DeviceId];
        Assert.False(other.GetProperty("online").GetBoolean()); // 未连接 → registry 无会话
        Assert.Contains("web-grp", other.GetProperty("groups")
            .EnumerateArray().Select(x => x.GetString()));

        // passive 会话 0x40 属主动类：本地拒发 2002（02 §2.5）
        await PostAsync(stack, "/api/auth/logout", null);
        var passive = await GetAsync(stack, "/api/devices");
        Assert.Equal(ErrorCode.ForbiddenPassive, passive.code);
    }

    // ── ⑤c 设置：GET 全量、PUT 部分合并+校验+换址即时生效（M1-32）────

    [Fact]
    public async Task 设置端点_GET全量_PUT部分合并校验与换址生效()
    {
        var a = await SeedDeviceAsync("web-set-a");
        await using var stack = await StartStackAsync(a);

        // GET 形状（04 §2.1：无机密字段；全新目录 serverAddrs 为空、其余取默认值）
        var before = await GetAsync(stack, "/api/settings");
        Assert.Equal(ErrorCode.Ok, before.code);
        Assert.Empty(before.data!.Value.GetProperty("serverAddrs").EnumerateArray());
        Assert.Equal(7100, before.data.Value.GetProperty("localWebPort").GetInt32());
        Assert.InRange(before.data.Value.GetProperty("punchConcurrency").GetInt32(), 1, 5);
        Assert.NotEqual(0, before.data.Value.GetProperty("keepaliveSec").GetInt32());
        Assert.True(before.data.Value.GetProperty("reconnect").ValueKind != JsonValueKind.Null);

        // PUT 部分修改：只送 punchConcurrency → 其余保持，落盘可复读
        var saved = await PutAsync(stack, "/api/settings", new { punchConcurrency = 5 });
        Assert.Equal(ErrorCode.Ok, saved.code);
        Assert.False(saved.data!.Value.GetProperty("restartRequired").GetBoolean());
        Assert.False(saved.data.Value.GetProperty("serverAddrsChanged").GetBoolean());
        Assert.Equal(5, saved.data.Value.GetProperty("settings").GetProperty("punchConcurrency").GetInt32());
        Assert.Equal(7100, saved.data.Value.GetProperty("settings").GetProperty("localWebPort").GetInt32());
        var reread = new SettingsStore(Path.GetDirectoryName(stack.Settings.SettingsFilePath)!);
        reread.Load();
        Assert.Equal(5, reread.Settings.PunchConcurrency);

        // 非法值不写盘：端口越界 → 1001
        var bad = await PutAsync(stack, "/api/settings", new { localWebPort = 70000 });
        Assert.Equal(ErrorCode.BadRequest, bad.code);
        var reread2 = new SettingsStore(Path.GetDirectoryName(stack.Settings.SettingsFilePath)!);
        reread2.Load();
        Assert.Equal(7100, reread2.Settings.LocalWebPort);

        // serverAddrs 变更 → 控制通道即时换址（本地 ServerAddrs 更新，M1-30 退避唤醒）
        var swapped = await PutAsync(stack, "/api/settings",
            new { serverAddrs = new[] { $"127.0.0.1:{_port}" } });
        Assert.Equal(ErrorCode.Ok, swapped.code);
        Assert.True(swapped.data!.Value.GetProperty("serverAddrsChanged").GetBoolean());
        Assert.Equal([$"127.0.0.1:{_port}"], stack.Control.ServerAddrs);

        // localWebPort 变更 → restartRequired=true（监听端口重启后生效）
        var port = await PutAsync(stack, "/api/settings", new { localWebPort = 7200 });
        Assert.Equal(ErrorCode.Ok, port.code);
        Assert.True(port.data!.Value.GetProperty("restartRequired").GetBoolean());
    }

    // ── ⑥ WS login_state：登录/登出推送 ─────────────────────────────

    [Fact]
    public async Task WS事件_login_state_登录登出推送()
    {
        var a = await SeedDeviceAsync("web-dev-f");
        await using var stack = await StartStackAsync(a);
        var username = $"u{Guid.NewGuid():N}"[..12];
        await PostAsync(stack, "/api/auth/register", new { username, password = "secret123" });

        await using var tap = await WsTap.ConnectAsync(stack.WsUrl);

        // 能力模式随会话初始 Normal：登出（→passive 推送）；重新登录须重连（02 §2.5：
        // 0x21 属主动类，passive 会话本地拒发）——重连建新会话复位 Normal（→normal 推送）
        var logout = await PostAsync(stack, "/api/auth/logout", null);
        Assert.Equal(ErrorCode.Ok, logout.code);
        var logoutEvt = await tap.NextAsync("login_state");
        Assert.Equal("passive", logoutEvt.GetProperty("mode").GetString());

        stack.Control.UpdateServerAddrs(stack.Control.ServerAddrs); // 同表换址：断连 → 重连复位
        var resetEvt = await tap.NextAsync("login_state");
        Assert.Equal("normal", resetEvt.GetProperty("mode").GetString());

        var login = await PostAsync(stack, "/api/auth/login", new { username, password = "secret123" });
        Assert.Equal(ErrorCode.Ok, login.code);
    }

    // ── ⑦ WS mapping_state + mapping_stats：真实隧道流量（完成判定）──

    [Fact]
    public async Task WS事件_mapping_state与stats_真实隧道流量()
    {
        var a = await SeedDeviceAsync("web-g");
        var b = await SeedDeviceAsync("web-h");
        await using var stack = await StartStackAsync(a);

        // B 侧：隧道宿主 + 引擎（目标侧 channel 处理）+ 回显服务
        var echo = new TcpListener(IPAddress.Loopback, 0);
        echo.Start();
        var echoPort = ((IPEndPoint)echo.LocalEndpoint).Port;
        using var echoCts = new CancellationTokenSource();
        var echoLoop = EchoAsync(echo, echoCts.Token);

        var hostB = new TunnelHost();
        var puncherB = new GatedPuncher();
        var schedulerB = new PunchScheduler(puncherB);
        var engineB = new MappingEngine(hostB, schedulerB, IPAddress.Loopback);
        stack.Puncher.Set(new TunnelPuncher(stack.Engine, engineB, hostB, a.DeviceId, b.DeviceId));

        await using var tap = await WsTap.ConnectAsync(stack.WsUrl);

        // 建映射并启用：punching → direct（替身打洞器真握手成功）
        var localPort = (ushort)FreePort();
        var created = await PostAsync(stack, "/api/mappings", new
        { name = "echo", localPort, targetRemoteCode = b.RemoteCode, targetPort = echoPort });
        Assert.Equal(ErrorCode.Ok, created.code);
        var mappingId = created.data!.Value.GetProperty("mappingId").GetGuid();

        var enabled = await PostAsync(stack, $"/api/mappings/{mappingId}/enable", null);
        Assert.Equal(ErrorCode.Ok, enabled.code);
        var punching = await tap.NextAsync("mapping_state");
        Assert.Equal(mappingId, punching.GetProperty("id").GetGuid());
        Assert.Equal("punching", punching.GetProperty("state").GetString());

        var direct = await tap.NextAsync("mapping_state");
        Assert.Equal(mappingId, direct.GetProperty("id").GetGuid());
        Assert.Equal("direct", direct.GetProperty("state").GetString());

        // 本地应用接入 → 回显流量 → mapping_stats（1s 采样，04 §2.8）
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, localPort);
        using var stream = client.GetStream();
        var payload = new byte[8192];
        Random.Shared.NextBytes(payload);
        await stream.WriteAsync(payload);
        var received = new byte[payload.Length];
        var got = 0;
        while (got < payload.Length)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var n = await stream.ReadAsync(received.AsMemory(got), cts.Token);
            Assert.NotEqual(0, n);
            got += n;
        }
        Assert.True(received.AsSpan().SequenceEqual(payload));

        var statsEvt = await tap.NextAsync("mapping_stats", TimeSpan.FromSeconds(8));
        Assert.Equal(mappingId, statsEvt.GetProperty("id").GetGuid());
        Assert.True(statsEvt.GetProperty("rateUp").GetInt64() > 0);
        Assert.True(statsEvt.GetProperty("rateDown").GetInt64() > 0);
        Assert.Equal("direct", statsEvt.GetProperty("path").GetString());

        // 收尾
        echoCts.Cancel();
        try { await echoLoop; } catch { /* 取消路径 */ }
        echo.Stop();
        await engineB.DisposeAsync();
        await schedulerB.DisposeAsync();
        await hostB.DisposeAsync();
    }

    // ── ⑧ 失败手动重试：POST /retry 重排打洞 + 未知 id 1002（M1-32）──

    [Fact]
    public async Task 映射重试端点_failed重排打洞_未知id_1002()
    {
        var a = await SeedDeviceAsync("web-retry-a");
        var b = await SeedDeviceAsync("web-retry-b");
        await using var stack = await StartStackAsync(a);
        stack.Puncher.Set(new FailingPuncher()); // 恒失败：failed→重试→再 failed 轨迹稳定

        await using var tap = await WsTap.ConnectAsync(stack.WsUrl);
        var localPort = (ushort)FreePort();
        var created = await PostAsync(stack, "/api/mappings", new
        { name = "m-retry", localPort, targetRemoteCode = b.RemoteCode, targetPort = 8080 });
        Assert.Equal(ErrorCode.Ok, created.code);
        var mappingId = created.data!.Value.GetProperty("mappingId").GetGuid();

        var enabled = await PostAsync(stack, $"/api/mappings/{mappingId}/enable", null);
        Assert.Equal(ErrorCode.Ok, enabled.code);
        Assert.Equal("punching", (await tap.NextAsync("mapping_state")).GetProperty("state").GetString());
        Assert.Equal("failed", (await tap.NextAsync("mapping_state")).GetProperty("state").GetString());

        // 手动重试：failed→punching 重排（WS 轨迹按序断言，不赌响应快照——桩同步失败）
        var retry = await PostAsync(stack, $"/api/mappings/{mappingId}/retry", null);
        Assert.Equal(ErrorCode.Ok, retry.code);
        Assert.Equal("punching", (await tap.NextAsync("mapping_state")).GetProperty("state").GetString());
        Assert.Equal("failed", (await tap.NextAsync("mapping_state")).GetProperty("state").GetString());

        // 未知 id → 1002（与启停同口径）
        var unknown = await PostAsync(stack, $"/api/mappings/{Guid.NewGuid()}/retry", null);
        Assert.Equal(ErrorCode.NotFound, unknown.code);
    }

    /// <summary>回显服务：收多少回多少。</summary>
    private static async Task EchoAsync(TcpListener listener, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch { continue; }
            _ = EchoOneAsync(client, buf, ct);
        }
    }

    private static async Task EchoOneAsync(TcpClient client, byte[] buf, CancellationToken ct)
    {
        using var _ = client;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var stream = client.GetStream();
            while (true)
            {
                var n = await stream.ReadAsync(buf.AsMemory(), linked.Token);
                if (n == 0) return;
                await stream.WriteAsync(buf.AsMemory(0, n), linked.Token);
            }
        }
        catch { /* 对端断开 */ }
    }

    // ── ⑧ 目标设备级配置（M2-23，04 §2.4 /api/peers；不经控制协议、不同步服务端）──

    [Fact]
    public async Task 设备级配置端点_形状与持久化_登录登出2002()
    {
        var (deviceId, secret, _) = await SeedDeviceAsync("peers-dev");
        await using var stack = await StartStackAsync((deviceId, secret, "000000"));

        // GET：无条目=默认关闭（PRD 06 §2）
        var before = await GetAsync(stack, $"/api/peers/{deviceId}");
        Assert.Equal(ErrorCode.Ok, before.code);
        Assert.False(before.data!.Value.GetProperty("relayFallback").GetBoolean());

        // PUT true → 回显与回读一致；不经控制协议（无服务端侧状态可断言，契约即本地生效）
        var put = await PutAsync(stack, $"/api/peers/{deviceId}", new { relayFallback = true });
        Assert.Equal(ErrorCode.Ok, put.code);
        Assert.True(put.data!.Value.GetProperty("relayFallback").GetBoolean());
        var after = await GetAsync(stack, $"/api/peers/{deviceId}");
        Assert.True(after.data!.Value.GetProperty("relayFallback").GetBoolean());

        // 设备隔离：另一目标不受影响
        var other = await GetAsync(stack, $"/api/peers/{Guid.NewGuid()}");
        Assert.False(other.data!.Value.GetProperty("relayFallback").GetBoolean());

        // passive（登出）→ 2002（04 §2.4 节仅 normal 模式）
        var logout = await PostAsync(stack, "/api/auth/logout", null);
        Assert.Equal(ErrorCode.Ok, logout.code);
        var denied = await GetAsync(stack, $"/api/peers/{deviceId}");
        Assert.Equal(ErrorCode.ForbiddenPassive, denied.code);
        var deniedPut = await PutAsync(stack, $"/api/peers/{deviceId}", new { relayFallback = false });
        Assert.Equal(ErrorCode.ForbiddenPassive, deniedPut.code);
    }
}

/// <summary>内存隧道传输对（集成测试本地副本；产品实现 UDP 承载，单测版在 P2P.Client.Tests）。</summary>
internal sealed class MemoryTransport(Channel<byte[]> inbox, Channel<byte[]> outbox) : ITunnelTransport
{
    public static (MemoryTransport A, MemoryTransport B) CreatePair()
    {
        var ab = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        var ba = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        return (new MemoryTransport(ba, ab), new MemoryTransport(ab, ba));
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
        => outbox.Writer.WriteAsync(frame.ToArray(), ct);

    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken ct = default)
    {
        try { return await inbox.Reader.ReadAsync(ct); }
        catch (ChannelClosedException) { return null; }
    }

    public ValueTask DisposeAsync()
    {
        inbox.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

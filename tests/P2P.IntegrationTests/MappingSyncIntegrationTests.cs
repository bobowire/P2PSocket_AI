using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using P2P.Client.Control;
using P2P.Client.Mapping;
using P2P.Client.Punch;
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
/// M1-28 映射同步全链路集成测试（任务清单完成判定：创建映射→服务端落库→enable 触发入队打洞→
/// 本地状态回显；另覆盖 state.json 持久化、停用/删除、未知远程码 4003、端口冲突 1003）。
/// 拓扑：in-proc ControlServer + ControlClient(A，admin 登录) + B 同账号可见；
/// 打洞桩闸死不完成 → enable 后稳定停留 punching（确定性断言）。
/// </summary>
public sealed class MappingSyncIntegrationTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DeviceRegistry _registry = new();
    private readonly List<SignalingCoordinator> _signalings = [];
    private readonly List<ControlServer> _servers = [];
    private StubFactory _factory = null!;
    private string _stateDir = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        _connection.Open();
        _factory = new StubFactory(CreateDb);
        using (var init = _factory.CreateDbContext())
            DbInitializer.Initialize(init);
        var server = await StartServerAsync(0);
        _port = server.LocalEndPoint!.Port;
        _stateDir = Path.Combine(Path.GetTempPath(), $"p2p-it-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_stateDir);
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _servers) await s.DisposeAsync();
        foreach (var s in _signalings) await s.DisposeAsync();
        _connection.Dispose();
        try { Directory.Delete(_stateDir, true); } catch (IOException) { }
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

    /// <summary>打洞桩：出队即记录并闸死（不主动完成）→ enable 后映射稳定停留 punching；
    /// 停机取消时以 Failure 放行（真 Puncher 同样尊重 ct，否则调度器 DisposeAsync 永久等待在飞任务）。</summary>
    private sealed class GatedPuncher : IPuncher
    {
        public ConcurrentQueue<Guid> Initiated { get; } = [];
        private readonly TaskCompletionSource<PunchOutcome> _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<PunchOutcome> InitiateAsync(Guid targetDeviceId, Guid? triggerMappingId,
            CancellationToken ct = default)
        {
            Initiated.Enqueue(targetDeviceId);
            try { return await _gate.Task.WaitAsync(ct); }
            catch (OperationCanceledException) { return PunchOutcome.Failure(targetDeviceId, "gate_cancelled"); }
        }

        public Task<PunchOutcome> RespondAsync(PunchInvite invite, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>客户端全家桶 + 本地 Web API 宿主。</summary>
    private sealed class ClientStack : IAsyncDisposable
    {
        public ControlClient Control = null!;
        public MappingEngine Engine = null!;
        public PunchScheduler Scheduler = null!;
        public TunnelHost Host = null!;
        public GatedPuncher Puncher = new();
        public StateStore Store = null!;
        public WebApplication App = null!;
        public HttpClient Http = null!;

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await App.DisposeAsync();
            await Engine.DisposeAsync();
            await Scheduler.DisposeAsync();
            await Host.DisposeAsync();
            await Control.DisposeAsync();
        }
    }

    private async Task<ClientStack> StartStackAsync()
    {
        var stack = new ClientStack();
        stack.Control = new ControlClient(
            new[] { $"127.0.0.1:{_port}" },
            new ControlClientOptions { HeartbeatInterval = TimeSpan.FromHours(1) },
            deviceId: _a.DeviceId, deviceSecret: _a.DeviceSecret);
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            await stack.Control.WaitReadyAsync(cts.Token);
        var login = await stack.Control.SendRequestAsync<UserLoginAck>(new UserLogin(
            stack.Control.NextSeq(), stack.Control.TimestampMs(), MsgType.UserLogin,
            DbInitializer.AdminUsername, DbInitializer.AdminUsername));
        Assert.True(login!.Ok); // L2：会话归属 admin 后可见同账号设备

        stack.Store = new StateStore(_stateDir);
        stack.Store.Load();
        stack.Host = new TunnelHost();
        stack.Scheduler = new PunchScheduler(stack.Puncher);
        stack.Engine = new MappingEngine(stack.Host, stack.Scheduler, IPAddress.Loopback);
        var sync = new MappingSyncService(stack.Control, stack.Engine, stack.Store);

        var webPort = FreePort();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{webPort}");
        var app = builder.Build();
        app.MapMappingApi(sync);
        await app.StartAsync();
        stack.App = app;
        stack.Http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{webPort}") };
        return stack;
    }

    private (Guid DeviceId, byte[] DeviceSecret, string RemoteCode) _a;
    private (Guid DeviceId, byte[] DeviceSecret, string RemoteCode) _b;

    // ── 完成判定：创建→落库→enable 入队打洞→状态回显→落盘→停用→删除 ──

    [Fact]
    public async Task 映射CRUD全链路_创建落库_enable打洞_状态回显_落盘恢复()
    {
        _a = await SeedDeviceAsync("it-map-a");
        _b = await SeedDeviceAsync("it-map-b");
        await using var stack = await StartStackAsync();
        var localPort = (ushort)FreePort();

        // ① 创建：envelope code 0 + 服务端落库 + state.json 落盘
        var created = await PostAsync(stack, "/api/mappings", new
        {
            name = "web", localPort, targetRemoteCode = _b.RemoteCode, targetPort = 80,
        });
        Assert.Equal(ErrorCode.Ok, created.code);
        var mappingId = created.data!.Value.GetProperty("mappingId").GetGuid();
        Assert.NotEqual(Guid.Empty, mappingId);

        await using (var db = _factory.CreateDbContext())
        {
            var row = await db.Mappings.AsNoTracking().SingleAsync(m => m.Id == mappingId);
            Assert.Equal(_a.DeviceId, row.OwnerDeviceId);
            Assert.Equal(_b.DeviceId, row.TargetDeviceId);
            Assert.False(row.Enabled); // 创建即禁用（PRD 06 §2 默认值）
        }

        var reloaded = new StateStore(_stateDir);
        reloaded.Load();
        var stored = Assert.Single(reloaded.State.Mappings);
        Assert.Equal(mappingId, stored.MappingId);
        Assert.Equal(_b.RemoteCode, stored.TargetRemoteCode);
        Assert.False(stored.Enabled);

        // ② enable：打洞出队（目标=B）+ 状态回显 punching
        var enabled = await PostAsync(stack, $"/api/mappings/{mappingId}/enable", null);
        Assert.Equal(ErrorCode.Ok, enabled.code);
        Assert.Equal("punching", enabled.data!.Value.GetProperty("state").GetString());
        Assert.Contains(_b.DeviceId, stack.Puncher.Initiated);

        var list = await GetListAsync(stack);
        var view = Assert.Single(list);
        Assert.Equal("punching", view.state);
        Assert.True(view.enabled);

        // ③ disable：状态回 disabled + 服务端 Enabled=false
        var disabled = await PostAsync(stack, $"/api/mappings/{mappingId}/disable", null);
        Assert.Equal(ErrorCode.Ok, disabled.code);
        Assert.Equal("disabled", disabled.data!.Value.GetProperty("state").GetString());
        await using (var db = _factory.CreateDbContext())
        {
            var row = await db.Mappings.AsNoTracking().SingleAsync(m => m.Id == mappingId);
            Assert.False(row.Enabled); // 0x60 Enabled=false 已同步
        }

        // ④ 删除：摘表 + 服务端行删 + state.json 清空
        var deleted = await DeleteAsync(stack, $"/api/mappings/{mappingId}");
        Assert.Equal(ErrorCode.Ok, deleted.code);
        Assert.Empty(await GetListAsync(stack));
        await using (var db = _factory.CreateDbContext())
            Assert.False(await db.Mappings.AsNoTracking().AnyAsync(m => m.Id == mappingId));
        var reloaded2 = new StateStore(_stateDir);
        reloaded2.Load();
        Assert.Empty(reloaded2.State.Mappings);
    }

    // ── 错误路径：未知远程码 4003 / 端口冲突 1003（envelope 业务码，HTTP 200）──

    [Fact]
    public async Task 创建失败_未知远程码4003_端口冲突1003()
    {
        _a = await SeedDeviceAsync("it-map-a2");
        _b = await SeedDeviceAsync("it-map-b2");
        await using var stack = await StartStackAsync();
        var localPort = (ushort)FreePort();

        var badCode = await PostAsync(stack, "/api/mappings", new
        { name = "x", localPort, targetRemoteCode = "zzzzzz", targetPort = 80 });
        Assert.Equal(ErrorCode.RemoteCodeInvalid, badCode.code);
        Assert.Equal(200, badCode.http);

        var ok = await PostAsync(stack, "/api/mappings", new
        { name = "m1", localPort, targetRemoteCode = _b.RemoteCode, targetPort = 80 });
        Assert.Equal(ErrorCode.Ok, ok.code);

        var conflict = await PostAsync(stack, "/api/mappings", new
        { name = "m2", localPort, targetRemoteCode = _b.RemoteCode, targetPort = 81 });
        Assert.Equal(ErrorCode.Conflict, conflict.code);
    }

    // ── HTTP 辅助（envelope { code, msg, data }，04 §1）────────────────

    private static async Task<(int code, int http, JsonElement? data)> SendAsync(
        ClientStack stack, HttpMethod method, string path, object? body)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = new StringContent(
                JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
        using var response = await stack.Http.SendAsync(request);
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return (root.GetProperty("code").GetInt32(), (int)response.StatusCode,
            root.TryGetProperty("data", out var data) ? data : null);
    }

    private static Task<(int code, int http, JsonElement? data)> PostAsync(
        ClientStack stack, string path, object? body)
        => SendAsync(stack, HttpMethod.Post, path, body);

    private static Task<(int code, int http, JsonElement? data)> DeleteAsync(
        ClientStack stack, string path)
        => SendAsync(stack, HttpMethod.Delete, path, null);

    /// <summary>GET 列表 → (state, enabled) 视图集合。</summary>
    private static async Task<List<(string state, bool enabled)>> GetListAsync(ClientStack stack)
    {
        using var response = await stack.Http.GetAsync("/api/mappings");
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(ErrorCode.Ok, root.GetProperty("code").GetInt32());
        return root.GetProperty("data").GetProperty("items").EnumerateArray()
            .Select(i => (i.GetProperty("state").GetString()!, i.GetProperty("enabled").GetBoolean()))
            .ToList();
    }
}

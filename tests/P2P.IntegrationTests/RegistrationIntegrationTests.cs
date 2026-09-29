using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using P2P.Client.Control;
using P2P.Client.Registration;
using P2P.Client.Storage;
using P2P.Core.Protocol;
using P2P.Nic;
using P2P.Server.Data;
using P2P.Server.Services;
using Xunit;

namespace P2P.IntegrationTests;

/// <summary>
/// M1-24 注册与向导后端集成测试（任务清单验收）：全新状态走完注册 → state.json 三要素落盘 →
/// 虚拟地址生效（替身网卡）→ 服务端在线；含已有账号登录绑定（A-2 前提）、新建账号建组、连通性探测。
/// </summary>
public sealed class RegistrationIntegrationTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    private readonly DeviceRegistry _registry = new();
    private readonly StubNicManager _nic = new();
    private readonly string _stateDir = Path.Combine(Path.GetTempPath(), "p2p-it-" + Guid.NewGuid().ToString("N"));
    private ControlServer _server = null!;
    private SignalingCoordinator _signaling = null!;
    private RelayService _relay = null!;
    private StubFactory _factory = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        _factory = new StubFactory(CreateDb);
        using (var init = _factory.CreateDbContext())
            DbInitializer.Initialize(init);
        var audit = new AuditLogger(_factory);
        _signaling = new SignalingCoordinator(_factory, _registry, new Authorizer(_factory), audit);
        _relay = new RelayService(_factory, _registry, _signaling.ResolveRelayPeers);
        var router = new ControlMessageRouter(
            new RegistrationService(_factory, _registry, audit),
            new UserService(_factory, audit),
            new GroupService(_factory, _registry, audit),
            _signaling,
            new MappingService(_factory, audit),
            _relay,
            new StatsService(_factory, audit),
            audit);
        _server = new ControlServer(_factory, _registry, router.DispatchAsync);
        await _server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
        _port = _server.LocalEndPoint!.Port;
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _relay.DisposeAsync();
        await _signaling.DisposeAsync();
        _db.Dispose();
        if (Directory.Exists(_stateDir)) Directory.Delete(_stateDir, true);
    }

    private AppDbContext CreateDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_db.DataSource).Options);

    private (ControlClient Client, StateStore Store, ClientRegistrationService Wizard) NewWizard()
    {
        var store = new StateStore(_stateDir);
        store.Load();
        var client = new ControlClient(new[] { $"127.0.0.1:{_port}" },
            new ControlClientOptions { HeartbeatInterval = TimeSpan.FromHours(1) });
        var wizard = new ClientRegistrationService(client, store, _nic);
        return (client, store, wizard);
    }

    private static async Task ReadyAsync(ControlClient client)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await client.WaitReadyAsync(cts.Token);
    }

    [Fact]
    public async Task 全新状态_走完注册_三要素落盘_网卡生效_服务端在线()
    {
        var (client, store, wizard) = NewWizard();
        await using (client)
        {
            Assert.True(ClientRegistrationService.NeedsWizard(store.State)); // 未注册态探测（FR-C-101）
            await ReadyAsync(client);
            Assert.Equal(ControlClientState.NeedRegister, client.State);

            var macCode = $"P2P-IT{Guid.NewGuid():N}"[..15];
            var result = await wizard.RegisterAsync(macCode);

            // RegisterAck 三要素 + 默认分组（服务端注册即入组）
            Assert.NotEqual(Guid.Empty, result.DeviceId);
            Assert.Matches("^[0-9abc]{6}$", result.RemoteCode); // OQ-8 字符集
            Assert.Equal("100.64.0.2", result.VirtualIp);        // 统一下发固定 .2（OQ-13）
            Assert.Contains(result.Groups, g => g.GroupName == "默认分组");

            // 会话已建立 + 服务端在线
            Assert.Equal(ControlClientState.Established, client.State);
            Assert.True(_registry.IsOnline(result.DeviceId));

            // 虚拟地址生效（替身网卡收到 Ensure）
            Assert.Equal([IPAddress.Parse("100.64.0.2")], _nic.Ensured);

            // state.json 落盘：新实例重读，三要素 + 机密完整（FR-C-103）
            var reloaded = new StateStore(_stateDir);
            reloaded.Load();
            Assert.Equal(result.DeviceId, reloaded.State.DeviceId);
            Assert.NotNull(reloaded.State.DeviceSecret);
            Assert.Equal(32, reloaded.State.DeviceSecret!.Length);
            Assert.NotNull(reloaded.State.StaticPrivateKey);
            Assert.Equal(32, reloaded.State.StaticPrivateKey!.Length);
            Assert.Equal(result.RemoteCode, reloaded.State.RemoteCode);
            Assert.Equal(result.VirtualIp, reloaded.State.VirtualIp);
            Assert.False(ClientRegistrationService.NeedsWizard(reloaded.State));
        }
    }

    [Fact]
    public async Task 已有账号登录绑定_设备归属落库_能力Normal()
    {
        var (client, store, wizard) = NewWizard();
        await using (client)
        {
            await ReadyAsync(client);
            var result = await wizard.RegisterAsync($"P2P-IT{Guid.NewGuid():N}"[..15]);

            var login = await wizard.LoginBindAsync(DbInitializer.AdminUsername, DbInitializer.AdminUsername);
            Assert.True(login.Ok);
            Assert.Equal(CapabilityMode.Normal, client.Capability);

            // owner 绑定持久在库（服务端 0x21 语义）
            await using var db = _factory.CreateDbContext();
            var owner = await db.Devices.AsNoTracking()
                .Where(d => d.Id == result.DeviceId).Select(d => d.OwnerUserId).SingleAsync();
            Assert.NotNull(owner);
        }
    }

    [Fact]
    public async Task 新建账号_登录_建组_全链路()
    {
        var (client, store, wizard) = NewWizard();
        await using (client)
        {
            await ReadyAsync(client);
            await wizard.RegisterAsync($"P2P-IT{Guid.NewGuid():N}"[..15]);

            var username = $"u{Guid.NewGuid():N}"[..12];
            var created = await wizard.CreateUserAsync(username, "secret123");
            Assert.True(created.Ok);

            var login = await wizard.LoginBindAsync(username, "secret123");
            Assert.True(login.Ok);

            var group = await wizard.CreateGroupAsync("我的分组", JoinPolicy.Free);
            Assert.NotEqual(Guid.Empty, group.GroupId);
        }
    }

    [Fact]
    public async Task 连通性探测_可达与不可达()
    {
        var ok = await ClientRegistrationService.TestConnectivityAsync(
            new[] { $"127.0.0.1:{_port}" }, TimeSpan.FromSeconds(3));
        Assert.True(ok.Ok);

        var bad = await ClientRegistrationService.TestConnectivityAsync(
            new[] { "127.0.0.1:1" }, TimeSpan.FromSeconds(3));
        Assert.False(bad.Ok);
        Assert.Contains("127.0.0.1:1", bad.Detail);

        // 多地址：首个不可达 → 次可达
        var fallback = await ClientRegistrationService.TestConnectivityAsync(
            new[] { "127.0.0.1:1", $"127.0.0.1:{_port}" }, TimeSpan.FromSeconds(3));
        Assert.True(fallback.Ok);
    }

    [Fact]
    public void 向导URL_本地回环()
    {
        Assert.Equal("http://127.0.0.1:7100/wizard", ClientRegistrationService.WizardUrl(7100));
    }
}

/// <summary>网卡替身：记录 Ensure 收到的虚拟地址（A-1 出口验证；真实网卡归 M1-37 实机冒烟）。
/// M2-24：外部可改 Exists/BoundIp 模拟网卡被删/IP 被改，CheckHealth 如实上报。</summary>
internal sealed class StubNicManager : INicManager
{
    public List<IPAddress> Ensured { get; } = [];
    /// <summary>替身适配器在位开关（测试模拟外部删除）。</summary>
    public volatile bool Exists = true;
    /// <summary>替身当前绑定 IP（null=在位但无绑定；测试模拟 IP 被改动）。</summary>
    public IPAddress? BoundIp;

#pragma warning disable CS0067 // 接口事件保留位（自愈异常经 NicHealthMonitor 日志展示）
    public event Action<string>? Degraded;
#pragma warning restore CS0067

    public Task<NicHandle> EnsureAsync(IPAddress virtualIp, CancellationToken ct = default)
    {
        Ensured.Add(virtualIp);
        Exists = true; // Ensure 即重建：适配器在位 + 恢复绑定
        BoundIp = virtualIp;
        return Task.FromResult(new NicHandle("stub-0", virtualIp));
    }

    public Task RemoveAsync(CancellationToken ct = default) => Task.CompletedTask;

    public NicHealth CheckHealth(IPAddress expectedIp)
        => !Exists ? new NicHealth(NicHealthState.AdapterMissing, null)
        : BoundIp is null || !BoundIp.Equals(expectedIp)
            ? new NicHealth(NicHealthState.IpMismatch, BoundIp)
            : new NicHealth(NicHealthState.Healthy, BoundIp);

    /// <summary>遗留适配器移除（M2-25 卸载测）：在位即移除（Exists→false），返回是否实际移除。</summary>
    public Task<bool> RemoveLeftoverAsync(CancellationToken ct = default)
    {
        if (!Exists) return Task.FromResult(false);
        Exists = false;
        return Task.FromResult(true);
    }
}

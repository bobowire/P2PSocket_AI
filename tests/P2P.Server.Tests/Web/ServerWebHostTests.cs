using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using P2P.Server.Data;
using P2P.Server.Services;
using P2P.Server.Web;
using Xunit;

namespace P2P.Server.Tests.Web;

/// <summary>
/// M3-01 服务端 Web 宿主（04 §3、D11 仅本机监听）：静态托管与 SPA 回退 /
/// 认证骨架（/api/* 未认证 401、login 白名单、有效会话放行）/ 绑定地址=webBind /
/// 与控制通道同进程共存、Web 先停不拖累控制面（停机序语义）。
/// </summary>
public sealed class ServerWebHostTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    private readonly DeviceRegistry _registry = new();
    private ControlServer _control = null!;
    private ServerWebHostService? _web;

    public Task InitializeAsync()
    {
        var factory = new StubFactory(() => new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_db.DataSource).Options));
        using (var init = factory.CreateDbContext())
            DbInitializer.Initialize(init);
        _control = new ControlServer(factory, _registry, static (_, _) => Task.CompletedTask);
        return _control.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
    }

    public async Task DisposeAsync()
    {
        if (_web is not null) await _web.StopAsync(CancellationToken.None);
        await _control.DisposeAsync();
        _db.Dispose();
    }

    private sealed record WebStack(ServerWebHostService Web, AdminSessionStore Sessions, int Port);

    /// <summary>起 Web 宿主；webRoot 缺省指向不存在的临时路径=纯 API 形态（静态跳过）。</summary>
    private async Task<WebStack> StartWebAsync(string? webRoot = null, string bind = "127.0.0.1", int? port = null)
    {
        var options = new ServerOptions();
        options.Listen.Web = port ?? Random.Shared.Next(21000, 24000); // 测试带：与 Client.Tests 映射族 24000-28000 不相交
        options.Listen.WebBind = bind;
        var sessions = new AdminSessionStore(TimeProvider.System);
        var factory = new StubFactory(() => new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_db.DataSource).Options));
        var web = new ServerWebHostService(options, TimeProvider.System, sessions, factory,
            new AuditLogger(factory), new AdminService(factory, _registry, new AuditLogger(factory)), _registry)
        {
            WebRootOverride = webRoot ?? Path.Combine(Path.GetTempPath(), $"p2p-no-webroot-{Guid.NewGuid():N}"),
        };
        await web.StartAsync(CancellationToken.None);
        _web = web;
        return new WebStack(web, sessions, options.Listen.Web);
    }

    [Fact]
    public async Task api_未认证一律401_登录白名单与有效会话放行()
    {
        var stack = await StartWebAsync();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{stack.Port}/") };

        // 未认证：/api/* 一律 401 + 错误码 2001（04 §5 语义）
        var denied = await http.GetAsync("/api/dashboard");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        var body = await denied.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2001, body.GetProperty("code").GetInt32());

        // /api/auth/login 白名单：未认证亦不落 401（端点本体 M3-02 挂载，此时 404 即证明穿透）
        var loginPass = await http.PostAsync("/api/auth/login", content: null);
        Assert.NotEqual(HttpStatusCode.Unauthorized, loginPass.StatusCode);

        // 有效会话放行：携带 Cookie 后不再 401（无此端点=404）
        http.DefaultRequestHeaders.Add("Cookie", $"{AdminSessionStore.CookieName}={stack.Sessions.Create()}");
        var granted = await http.GetAsync("/api/dashboard");
        Assert.NotEqual(HttpStatusCode.Unauthorized, granted.StatusCode);
    }

    [Fact]
    public async Task 静态资产与SPA回退()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"p2p-web-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "assets"));
        await File.WriteAllTextAsync(Path.Combine(dir, "index.html"), "<!doctype html><title>admin</title>");
        await File.WriteAllTextAsync(Path.Combine(dir, "assets", "app.css"), "body{}");
        try
        {
            var stack = await StartWebAsync(dir);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{stack.Port}/") };

            var index = await http.GetAsync("/"); // UseDefaultFiles → index.html
            Assert.Equal(HttpStatusCode.OK, index.StatusCode);
            Assert.Contains("admin", await index.Content.ReadAsStringAsync());

            var css = await http.GetAsync("/assets/app.css");
            Assert.Equal(HttpStatusCode.OK, css.StatusCode);
            Assert.Equal("body{}", await css.Content.ReadAsStringAsync());

            var fallback = await http.GetAsync("/admin/users"); // SPA 路由兜底回 index.html
            Assert.Equal(HttpStatusCode.OK, fallback.StatusCode);
            Assert.Contains("admin", await fallback.Content.ReadAsStringAsync());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task 绑定地址为webBind_仅该地址可达()
    {
        var stack = await StartWebAsync(bind: "127.0.0.5");
        using var bound = new HttpClient { BaseAddress = new Uri($"http://127.0.0.5:{stack.Port}/") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await bound.GetAsync("/api/x")).StatusCode);

        using var other = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{stack.Port}/") };
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => other.GetAsync("/api/x")); // 未绑 Any，仅 webBind
    }

    [Fact]
    public async Task 与控制通道同进程共存_Web先停控制面不受扰()
    {
        var (deviceId, secret) = SeedDevice();
        await using var client = await TestPcpClient.ConnectAsync(_control.LocalEndPoint!);
        Assert.Equal(HelloStatus.Ok, (await client.HelloAsync(deviceId)).Status);
        Assert.True((await client.ProofAsync(secret)).Ok);

        var stack = await StartWebAsync(); // 与控制通道同时服务
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{stack.Port}/") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/x")).StatusCode);

        await stack.Web.StopAsync(CancellationToken.None); // 逆序停机：Web 先停
        await using var reconnect = await TestPcpClient.ConnectAsync(_control.LocalEndPoint!); // 控制面不受扰
        Assert.Equal(HelloStatus.Ok, (await reconnect.HelloAsync(deviceId)).Status);
    }

    /// <summary>预置一台已注册设备（ControlSessionTests 同模式），返回 (deviceId, deviceSecret)。</summary>
    private (Guid DeviceId, byte[] Secret) SeedDevice()
    {
        var id = Guid.NewGuid();
        var secret = RandomGenerator.Bytes(32);
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_db.DataSource).Options);
        db.Devices.Add(new Device
        {
            Id = id,
            DeviceName = "测试机",
            Os = "windows",
            ClientVersion = "0.1.0",
            MacCode = $"MAC{Guid.NewGuid():N}"[..12],
            RemoteCode = "123456",
            VirtualIp = "100.64.0.2",
            StaticPubKey = new byte[65],
            DeviceSecret = secret,
            CreatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return (id, secret);
    }
}

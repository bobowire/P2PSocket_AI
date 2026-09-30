using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using P2P.Server.Data;
using P2P.Server.Services;
using P2P.Server.Web;
using Xunit;

namespace P2P.Server.Tests.Web;

/// <summary>
/// M3-02 管理员会话与账号（FR-S-203、04 §3.1、07 §8、OQ-3）：
/// admin/admin 首登 mustChangePassword / 改密后消失 / 错误密码 401+审计 / logout 失效 Cookie /
/// 旧密码校验 / 会话 8h 过期（假时钟）/ 限速递增延迟后成功登录仍恢复。
/// </summary>
public sealed class AdminAuthTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    private readonly FakeTimeProvider _time = new();
    private ServerWebHostService _web = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        var factory = new StubFactory(() => new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_db.DataSource).Options));
        using (var init = factory.CreateDbContext())
            DbInitializer.Initialize(init); // 种 admin/admin（OQ-3）
        var options = new ServerOptions
        {
            Listen = { Web = Random.Shared.Next(21000, 24000) }, // 测试带（与客户端测试带不相交）
        };
        _web = new ServerWebHostService(options, _time, new AdminSessionStore(_time), factory,
            new AuditLogger(factory, _time))
        {
            WebRootOverride = Path.Combine(Path.GetTempPath(), $"p2p-no-webroot-{Guid.NewGuid():N}"),
        };
        await _web.StartAsync(CancellationToken.None);
        _port = options.Listen.Web;
    }

    public async Task DisposeAsync()
    {
        await _web.StopAsync(CancellationToken.None);
        _db.Dispose();
    }

    /// <summary>带 Cookie 容器的客户端（login→logout 全链自动携带）。</summary>
    private HttpClient CreateClient()
        => new(new HttpClientHandler { CookieContainer = new CookieContainer() })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_port}/"),
        };

    private static JsonElement Data(JsonElement body) => body.GetProperty("data");

    private async Task<JsonElement> LoginAsync(HttpClient http, string username, string password)
    {
        var resp = await http.PostAsJsonAsync("/api/auth/login", new { username, password });
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStreamAsync());
    }

    private async Task<int> AuditCountAsync(string @event)
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_db.DataSource).Options);
        return await db.AuditLogs.AsNoTracking().CountAsync(a => a.Event == @event);
    }

    // ── FR-S-203 主链 ────────────────────────────────────────────────────

    [Fact]
    public async Task 首登admin默认口令_必须改密提示_审计与Cookie属性()
    {
        using var http = CreateClient();
        var resp = await http.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStreamAsync());
        Assert.Equal(0, body.GetProperty("code").GetInt32());
        Assert.True(Data(body).GetProperty("mustChangePassword").GetBoolean());

        // Cookie：HttpOnly、SameSite=Strict、无 Secure（HTTP，D11）——已由容器收下；属性断言走原始头
        var setCookie = Assert.Single(resp.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secure", setCookie, StringComparison.OrdinalIgnoreCase);

        // 会话生效：携 Cookie 访问受保护区不再 401；审计 admin_login
        Assert.NotEqual(HttpStatusCode.Unauthorized,
            (await http.GetAsync("/api/dashboard")).StatusCode);
        Assert.Equal(1, await AuditCountAsync("admin_login"));
    }

    [Fact]
    public async Task 错误密码401_审计admin_login_fail_无会话Cookie()
    {
        using var http = CreateClient();
        var resp = await http.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "wrong!" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStreamAsync());
        Assert.Equal(2001, body.GetProperty("code").GetInt32());

        Assert.False(resp.Headers.Contains("Set-Cookie")); // 失败不发会话
        Assert.Equal(1, await AuditCountAsync("admin_login_fail"));
        // 非管理员用户名同拒（单管理员 D16）
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await http.PostAsJsonAsync("/api/auth/login", new { username = "nobody", password = "x" })).StatusCode);
    }

    [Fact]
    public async Task 改密全链_旧密码校验_改后提示消失_旧口令失效()
    {
        using var http = CreateClient();
        Assert.Equal(HttpStatusCode.OK,
            (await http.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin" })).StatusCode);

        // 未登录不可改密（认证中间件 401）——换无 Cookie 客户端验证
        using var bare = new HttpClient { BaseAddress = http.BaseAddress };
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await bare.PostAsJsonAsync("/api/auth/change-password", new { oldPassword = "admin", newPassword = "abcdef" })).StatusCode);

        // 旧密码错 → 401；新密码 <6 → 400
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await http.PostAsJsonAsync("/api/auth/change-password", new { oldPassword = "bad", newPassword = "abcdef" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await http.PostAsJsonAsync("/api/auth/change-password", new { oldPassword = "admin", newPassword = "123" })).StatusCode);

        // 成功改密 → 审计；mustChangePassword 消失（重新登录 false）；旧口令 401、新口令可登
        Assert.Equal(HttpStatusCode.OK,
            (await http.PostAsJsonAsync("/api/auth/change-password", new { oldPassword = "admin", newPassword = "newpass6" })).StatusCode);
        Assert.Equal(1, await AuditCountAsync("admin_change_password"));

        using var re = CreateClient();
        var again = await LoginAsync(re, "admin", "newpass6");
        Assert.False(Data(again).GetProperty("mustChangePassword").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await re.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin" })).StatusCode);
    }

    [Fact]
    public async Task logout后Cookie失效_受保护区回落401()
    {
        using var http = CreateClient();
        Assert.Equal(HttpStatusCode.OK,
            (await http.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin" })).StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/dashboard")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/dashboard")).StatusCode); // 会话已删
    }

    [Fact]
    public async Task 会话8小时过期_假时钟推进后失效()
    {
        using var http = CreateClient();
        Assert.Equal(HttpStatusCode.OK,
            (await http.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin" })).StatusCode);
        _time.Advance(TimeSpan.FromHours(8).Add(TimeSpan.FromMinutes(1)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/dashboard")).StatusCode);
    }

    [Fact]
    public async Task 限速递增延迟_成功登录即恢复()
    {
        using var http = CreateClient();
        // 连续失败 2 次（各 0/500ms 阶梯）
        await LoginAsync(http, "admin", "bad-1");
        await LoginAsync(http, "admin", "bad-2");

        // 第 3 次正确口令：先吃 1s 递增延迟仍成功，且计数复位（第 4 次登录无延迟）
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ok = await LoginAsync(http, "admin", "admin");
        sw.Stop();
        Assert.Equal(0, ok.GetProperty("code").GetInt32());
        Assert.True(sw.Elapsed >= TimeSpan.FromSeconds(1), $"应吃 ≥1s 递增延迟，实耗 {sw.Elapsed}");

        sw.Restart();
        var ok2 = await LoginAsync(http, "admin", "admin");
        sw.Stop();
        Assert.Equal(0, ok2.GetProperty("code").GetInt32());
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"成功后应复位无延迟，实耗 {sw.Elapsed}");
        Assert.Equal(2, await AuditCountAsync("admin_login_fail")); // 仅两次失败
    }
}

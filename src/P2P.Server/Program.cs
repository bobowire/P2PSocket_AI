// M1-19 服务端宿主（08 §5.1/§6、01 §3.1、NFR-35）：
// Generic Host 单进程多服务装配 + 启动配置校验 + --console/--reset-admin + Serilog（控制台+滚动文件）。
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using P2P.Core.Crypto;
using P2P.Server;
using P2P.Server.Data;
using P2P.Server.Services;
using Serilog;
using Serilog.Events;

// 命令行开关（08 §5.1：优先级高于配置文件；裸开关须从配置命令行中剥除，否则 CommandLine 提供程序报错）
var flags = args.Where(a => a is "--console" or "--reset-admin").ToHashSet();
var configArgs = args.Where(a => !flags.Contains(a)).ToArray();

// 中文 Windows 控制台默认 GBK：诊断信息统一 UTF-8 输出（服务模式无控制台时忽略）
try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* 无控制台 */ }

var builder = Host.CreateApplicationBuilder(configArgs);

// ── 配置校验（NFR-35：非法值拒启，一次报全「字段名+范围+建议」）─────────
if (!File.Exists(Path.Combine(builder.Environment.ContentRootPath, "appsettings.json")))
{
    Console.Error.WriteLine("配置校验失败，拒绝启动（NFR-35）：");
    Console.Error.WriteLine("  - 缺少 appsettings.json；建议：从发布包恢复该文件（参照 08 §5.1 模板）");
    return 1;
}
var options = new ServerOptions();
builder.Configuration.Bind(options);
var errors = options.Validate();
if (errors.Count > 0)
{
    Console.Error.WriteLine("配置校验失败，拒绝启动（NFR-35）：");
    foreach (var error in errors) Console.Error.WriteLine($"  - {error}");
    return 1;
}

// ── --reset-admin（07 §9 R6：admin 密码重置为默认后退出）────────────────
if (flags.Contains("--reset-admin"))
    return await ResetAdminAsync(options);

// ── Serilog（08 §6：控制台 + 滚动文件 logs/app-.log 10MB×保留期；结构化字段 M3）──
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(Enum.TryParse<LogEventLevel>(options.Logging.Level, ignoreCase: true, out var level)
        ? level : LogEventLevel.Information)
    .WriteTo.Console()
    .WriteTo.File(Path.Combine("logs", "app-.log"),
        rollingInterval: RollingInterval.Day,
        fileSizeLimitBytes: 10 * 1024 * 1024,
        retainedFileCountLimit: Math.Clamp(options.Logging.RetentionDays, 1, 365),
        shared: true)
    .CreateLogger();
builder.Logging.ClearProviders();
builder.Services.AddSerilog();

// ── 服务装配（01 §3.1；解析顺序见 ServerHostService）───────────────────
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<IDbContextFactory<AppDbContext>>(ServerDatabase.CreateFactory(options.Database.Path));
builder.Services.AddSingleton<DeviceRegistry>();
builder.Services.AddSingleton<AuditLogger>();
builder.Services.AddSingleton<Authorizer>();
builder.Services.AddSingleton<RegistrationService>();
builder.Services.AddSingleton<UserService>();
builder.Services.AddSingleton<GroupService>();
builder.Services.AddSingleton<MappingService>();
builder.Services.AddSingleton(sp => new SignalingCoordinator(
    sp.GetRequiredService<IDbContextFactory<AppDbContext>>(),
    sp.GetRequiredService<DeviceRegistry>(),
    sp.GetRequiredService<Authorizer>(),
    sp.GetRequiredService<AuditLogger>(),
    sessionTimeout: TimeSpan.FromSeconds(options.Punch.TimeoutSec),
    // M2-19 回切 0x73 解析兜底：活中继会话反查（闭包延迟解析——RelayService 构造依赖本类，调用期才解环）
    activeRelayLookup: punchSessionId => sp.GetRequiredService<RelayService>().ResolveActiveRelay(punchSessionId)));
builder.Services.AddSingleton(sp => new PresenceMonitor(
    sp.GetRequiredService<DeviceRegistry>(),
    sp.GetRequiredService<IDbContextFactory<AppDbContext>>(),
    timeout: TimeSpan.FromSeconds(options.Heartbeat.TimeoutSec)));
// 中继（02 §6，M2-07）：对端解析走 SignalingCoordinator 结束会话台账（打洞完成/超时后 120s 内可分配）
builder.Services.AddSingleton(sp => new RelayService(
    sp.GetRequiredService<IDbContextFactory<AppDbContext>>(),
    sp.GetRequiredService<DeviceRegistry>(),
    sp.GetRequiredService<SignalingCoordinator>().ResolveRelayPeers,
    new RelayServiceOptions { IdleTimeout = TimeSpan.FromSeconds(options.Relay.IdleTimeoutSec) }));
builder.Services.AddSingleton<ControlMessageRouter>();
// 上报族处理器（M2-08）：0x62 审计 / 0x64 mapping_stats / 0x72 落库在 SignalingCoordinator；
// 保留清理 audit_logs+punch_stats 一并（启动+每日，OQ-17/03 §2.7/§2.9）
builder.Services.AddSingleton<StatsService>();
builder.Services.AddSingleton(sp => new RetentionCleaner(
    sp.GetRequiredService<IDbContextFactory<AppDbContext>>(),
    sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(sp => new ControlServer(
    sp.GetRequiredService<IDbContextFactory<AppDbContext>>(),
    sp.GetRequiredService<DeviceRegistry>(),
    sp.GetRequiredService<ControlMessageRouter>().DispatchAsync));
// stun_auth 与三速率键是库开关（03 §2.8）：首次解析发生在 ServerHostService.StartAsync 步骤③
// （晚于数据库初始化，键必然存在；stun_rate_per_ip/per_device/circuit_pps 为四道闸 TD-18 参数）
builder.Services.AddSingleton(sp =>
{
    var factory = sp.GetRequiredService<IDbContextFactory<AppDbContext>>();
    using var db = factory.CreateDbContext();
    var cfg = new ServerConfigStore(db);
    return new StunService(factory, requireAuth: cfg.GetBool("stun_auth"), guard: new StunGuardOptions
    {
        PerIpPps = cfg.GetInt("stun_rate_per_ip"),
        PerDeviceQps = cfg.GetInt("stun_rate_per_device"),
        CircuitPps = cfg.GetInt("stun_circuit_pps"),
    });
});
builder.Services.AddHostedService<ServerHostService>();

await builder.Build().RunAsync();
return 0;

// ── 子流程 ─────────────────────────────────────────────────────────────

static async Task<int> ResetAdminAsync(ServerOptions options)
{
    var path = Path.GetFullPath(options.Database.Path);
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"数据库不存在：{path}；建议：先正常启动一次以完成初始化，再执行 --reset-admin");
        return 1;
    }
    var factory = ServerDatabase.CreateFactory(options.Database.Path);
    using var db = factory.CreateDbContext();
    DbInitializer.Seed(db); // admin 缺失则补建
    var admin = db.Users.Single(u => u.Username == DbInitializer.AdminUsername && u.IsAdmin);
    admin.PasswordHash = PasswordHasher.Hash(DbInitializer.AdminUsername); // admin/admin（OQ-3）
    admin.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    Console.WriteLine($"admin 密码已重置为默认（{DbInitializer.AdminUsername}/{DbInitializer.AdminUsername}），请尽快登录修改（07 §9 R6）。");
    return 0;
}

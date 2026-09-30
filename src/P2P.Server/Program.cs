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

// 命令行开关（08 §5.1：优先级高于配置文件；裸开关须从配置命令行中剥除，否则 CommandLine 提供程序报错；
// M2-13 管理操作为 --op <value> 形式，同样剥除）
var flags = args.Where(a => a is "--console" or "--reset-admin"
    or "--disable-device" or "--enable-device" or "--disable-user" or "--enable-user" or "--unbind-device")
    .ToHashSet();
string? adminOp = null, adminArg = null;
var configArgs = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    if (flags.Contains(args[i]) && args[i] is not ("--console" or "--reset-admin"))
    {
        adminOp = args[i];
        adminArg = i + 1 < args.Length ? args[++i] : null;
    }
    else if (!flags.Contains(args[i]))
        configArgs.Add(args[i]);
}

// 中文 Windows 控制台默认 GBK：诊断信息统一 UTF-8 输出（服务模式无控制台时忽略）
try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* 无控制台 */ }

var builder = Host.CreateApplicationBuilder([.. configArgs]);

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

// ── 管理操作（M2-13，FR-S-105/204/103：禁用/启用/解绑后退出；踢线/降级由运行中
//    服务器的 PresenceMonitor 心跳兜底在 ~30s 窗口内补齐，读侧拒绝即时生效）────
if (adminOp is not null)
    return await AdminOperationAsync(options, adminOp, adminArg);

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
// 0x41 列表变更推送（M2-10，FR-S-403）：订阅 DeviceRegistry 在线事件 + GroupService 成员变更触发
builder.Services.AddSingleton<DeviceListPusher>();
// 0x75 失效推送（M2-12，FR-C-702）：按授权链反查受影响映射逐 owner 推送（登出/远程码重置/组关系终止）
builder.Services.AddSingleton<InvalidationPusher>();
// 管理操作统一执行点（M2-13，FR-S-105/204/103）：CLI 壳/测试直调/M3 Web 载体共用
builder.Services.AddSingleton<AdminService>();
builder.Services.AddSingleton<GroupService>();
builder.Services.AddSingleton<MappingService>();
// 0x63 内网段白名单（M2-11，FR-C-701/702）：段 CRUD + 移除联动 0x75 失效推送
builder.Services.AddSingleton<LanSegmentService>();
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
    timeout: TimeSpan.FromSeconds(options.Heartbeat.TimeoutSec),
    // M2-13 兜底：跨进程 CLI 写库的禁用/解绑，~30s 心跳窗口内踢线/降级 + 0x75 补推
    invalidation: sp.GetRequiredService<InvalidationPusher>()));
// 中继（02 §6，M2-07）：对端解析走 SignalingCoordinator 结束会话台账（打洞完成/超时后 120s 内可分配）；
// public_addr 是库开关（03 §2.8，M2-36）：首次解析晚于数据库初始化（键必然存在）——NAT 云部署
// （VM 网卡只见内网 IP）填公网地址覆盖 Grant 端点派生，空 = 派生（局域网/直绑公网 IP）
builder.Services.AddSingleton(sp =>
{
    using var db = sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext();
    var cfg = new ServerConfigStore(db);
    return new RelayService(
        sp.GetRequiredService<IDbContextFactory<AppDbContext>>(),
        sp.GetRequiredService<DeviceRegistry>(),
        sp.GetRequiredService<SignalingCoordinator>().ResolveRelayPeers,
        new RelayServiceOptions
        {
            IdleTimeout = TimeSpan.FromSeconds(options.Relay.IdleTimeoutSec),
            PublicHost = cfg.Get("public_addr"),
        });
});
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

// ── 管理操作子流程（M2-13：写库 + 审计后退出；独立进程触不到运行中服务器内存注册表，
//    踢线/降级/0x75 由其 PresenceMonitor 心跳兜底 ~30s 窗口补齐，读侧拒绝查库即时生效）──

static async Task<int> AdminOperationAsync(ServerOptions options, string op, string? arg)
{
    if (string.IsNullOrWhiteSpace(arg))
    {
        Console.Error.WriteLine($"用法：P2P.Server {op} <macCode|username>");
        return 1;
    }
    var path = Path.GetFullPath(options.Database.Path);
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"数据库不存在：{path}；建议：先正常启动一次以完成初始化，再执行 {op}");
        return 1;
    }
    var factory = ServerDatabase.CreateFactory(options.Database.Path);
    using (var db = factory.CreateDbContext())
        DbInitializer.Seed(db); // 键缺失补建（与 --reset-admin 同模式）
    // CLI 进程内注册表恒空：踢线/推送自然跳过，仅写库 + 审计
    var admin = new AdminService(factory, new DeviceRegistry(), new AuditLogger(factory));
    var ok = op switch
    {
        "--disable-device" => await admin.DisableDeviceAsync(arg),
        "--enable-device" => await admin.EnableDeviceAsync(arg),
        "--disable-user" => await admin.DisableUserAsync(arg),
        "--enable-user" => await admin.EnableUserAsync(arg),
        "--unbind-device" => await admin.UnbindDeviceAsync(arg),
        _ => false,
    };
    if (!ok)
    {
        Console.Error.WriteLine($"目标不存在或无需操作：{arg}");
        return 1;
    }
    Console.WriteLine($"已执行 {op} {arg}（运行中服务器的踢线/降级将在 ~30s 心跳窗口内生效，读侧拒绝即时生效）。");
    return 0;
}

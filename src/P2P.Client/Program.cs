// M1-30 客户端宿主入口（01 §3.2/§4.1、08 §5.2/§6、FR-C-901~903）：
// --console 前台运行（默认 Windows Service / systemd 承载，服务定义文件见 deploy/client/）；
// --uninstall 卸载清理（M2-25，FR-C-203：网卡/遗留适配器移除 + 可选 0x12 解绑 + 配置目录
// --keep-config 默认保留/--purge 删除；A-11 实机验收 M3 轮）；
// Serilog 控制台+滚动文件（同 M1-19 服务端口径）；未捕获异常落日志后快速失败（01 §4.2，由系统拉起）。
using Microsoft.Extensions.Hosting;
using P2P.Client.Hosting;
using P2P.Client.Storage;
using Serilog;
using Serilog.Events;

var console = args.Contains("--console");
var uninstall = args.Contains("--uninstall");

// 中文 Windows 控制台默认 GBK：诊断信息统一 UTF-8 输出（服务模式无控制台时忽略）
try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* 无控制台 */ }

// ── Serilog（08 §6：控制台 + 滚动文件；级别经 P2P_LOG_LEVEL 覆盖，默认 Information）──
// 卸载路径仅控制台（不占文件句柄——--purge 才能删除含日志的配置目录）
var level = Enum.TryParse(Environment.GetEnvironmentVariable("P2P_LOG_LEVEL"), ignoreCase: true,
    out LogEventLevel parsed) ? parsed : LogEventLevel.Information;
var logCfg = new LoggerConfiguration().MinimumLevel.Is(level).WriteTo.Console();
if (!uninstall)
{
    try
    {
        ClientPaths.EnsureBaseDir(ClientPaths.DefaultBaseDir);
        var logDir = Path.Combine(ClientPaths.DefaultBaseDir, "logs");
        Directory.CreateDirectory(logDir);
        logCfg = logCfg.WriteTo.File(Path.Combine(logDir, "client-.log"),
            rollingInterval: RollingInterval.Day, fileSizeLimitBytes: 10 * 1024 * 1024,
            retainedFileCountLimit: 14, shared: true);
    }
    catch (Exception e) { Console.Error.WriteLine($"日志目录不可用，仅控制台输出：{e.Message}"); }
}
Log.Logger = logCfg.CreateLogger();

// ── 卸载清理（M2-25，FR-C-203）：短命路径，执行后直接退出（不起宿主）──
if (uninstall)
{
    var report = await ClientUninstaller.RunAsync(new UninstallOptions
    {
        Unbind = args.Contains("--unbind"),
        Purge = args.Contains("--purge"),
    });
    Console.WriteLine($"卸载完成：解绑确认={report.UnbindConfirmed} 网卡清理={report.NicRemoved} " +
                      $"遗留适配器移除={report.LeftoverRemoved} 配置目录删除={report.ConfigDeleted}");
    foreach (var note in report.Notes) Console.WriteLine($"  · {note}");
    Log.CloseAndFlush();
    return 0;
}

// ── 未捕获异常：落日志后快速失败（01 §4.2——由服务恢复选项/systemd Restart=always 拉起）──
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    Log.Fatal(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject.ToString() ?? "未知"),
        "未捕获异常，快速失败（01 §4.2）");
    Log.CloseAndFlush();
    Environment.FailFast("unhandled exception");
};
// 后台任务未观察异常：记录并吸收（不连坐进程——真正的致命错误走 UnhandledException 路径）
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    Log.Error(e.Exception, "未观察任务异常（已吸收）");
    e.SetObserved();
};

// ── Generic Host 装配（Windows Service / systemd 自适应：非服务环境两扩展均空操作）──
var builder = Host.CreateApplicationBuilder(args.Where(a => a != "--console").ToArray());
builder.Logging.ClearProviders();
builder.Services.AddSerilog();
builder.Services.AddWindowsService(o => o.ServiceName = "P2PClient");
builder.Services.AddSystemd();
builder.Services.AddSingleton<IHostedService>(_ => new ClientHostService(
    new ClientRuntimeOptions { Interactive = console }));

try
{
    await builder.Build().RunAsync();
    return 0;
}
catch (Exception e)
{
    Log.Fatal(e, "客户端宿主退出（异常）");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

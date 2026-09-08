using System.Net;
using Microsoft.EntityFrameworkCore;
using P2P.Server.Data;

namespace P2P.Server.Services;

/// <summary>
/// 服务端启动编排（01 §3.1 单进程多服务；IHostedService）：
/// 启动序：数据库迁移+种子 → 控制面监听 → STUN UDP → （构造即运行的监视器/信令 reaper 已随后就绪）。
/// 停机逆序：STUN → 控制面（关闭全部会话）→ 监视器 → 信令。
/// </summary>
public sealed class ServerHostService(IServiceProvider sp, ILogger<ServerHostService> logger, ServerOptions options)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // 1) 数据库（03 §6：迁移自动执行；§2 种子幂等）
        var factory = sp.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using (var db = await factory.CreateDbContextAsync(cancellationToken))
            DbInitializer.Initialize(db);
        logger.LogInformation("数据库就绪：{Path}", Path.GetFullPath(options.Database.Path));

        // 2) 控制面（TCP listen.control）
        var control = sp.GetRequiredService<ControlServer>();
        await control.StartAsync(new IPEndPoint(IPAddress.Any, options.Listen.Control));
        logger.LogInformation("控制面监听 0.0.0.0:{Port}（PCP，02 §2）", options.Listen.Control);

        // 3) STUN UDP（stun_auth 库开关随首次解析读取——晚于上方初始化，键必然存在）
        var stun = sp.GetRequiredService<StunService>();
        await stun.StartAsync(options.Listen.StunUdp);
        logger.LogInformation("STUN-R 监听 0.0.0.0:{Port}/udp（02 §3）", options.Listen.StunUdp);

        // 4) 显式触发构造（后台循环随构造启动；无端口绑定，仅确认装配完整）
        _ = sp.GetRequiredService<PresenceMonitor>();
        _ = sp.GetRequiredService<SignalingCoordinator>();
        logger.LogInformation("服务端启动完成：心跳超时 {Timeout}s、打洞超时 {Punch}s",
            options.Heartbeat.TimeoutSec, options.Punch.TimeoutSec);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("服务端停机中……");

        var stun = sp.GetRequiredService<StunService>();
        await stun.DisposeAsync();

        var control = sp.GetRequiredService<ControlServer>();
        await control.DisposeAsync(); // 停监听 + 关闭全部会话（断连落库，05 §5）

        await sp.GetRequiredService<PresenceMonitor>().DisposeAsync();
        await sp.GetRequiredService<SignalingCoordinator>().DisposeAsync();
        logger.LogInformation("服务端已停止");
    }
}

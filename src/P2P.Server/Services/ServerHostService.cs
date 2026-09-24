using System.Net;
using Microsoft.EntityFrameworkCore;
using P2P.Server.Data;

namespace P2P.Server.Services;

/// <summary>
/// 服务端启动编排（01 §3.1 单进程多服务；IHostedService）：
/// 启动序：数据库迁移+种子 → 控制面监听 → STUN UDP → 中继 UDP/TCP → （构造即运行的监视器/信令 reaper 已随后就绪）。
/// 停机逆序：中继 → STUN → 控制面（关闭全部会话）→ 监视器 → 信令 → 保留清理。
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

        // 3) STUN UDP + TCP（stun_auth 与三速率键随首次解析读取——晚于上方初始化，键必然存在）
        var stun = sp.GetRequiredService<StunService>();
        await stun.StartAsync(options.Listen.StunUdp, options.Listen.StunTcp);
        logger.LogInformation("STUN-R 监听 0.0.0.0:{Udp}/udp + {Tcp}/tcp（02 §3，四道闸 TD-18）",
            options.Listen.StunUdp, options.Listen.StunTcp);

        // 3.5) 中继双承载（listen.relayPorts[0]=UDP、[1]=TCP，02 §6；relay_enabled 关仅拒分配不停端口）
        var relay = sp.GetRequiredService<RelayService>();
        await relay.StartAsync(options.Listen.RelayPorts[0], options.Listen.RelayPorts[1]);
        logger.LogInformation("中继监听 0.0.0.0:{Udp}/udp + {Tcp}/tcp（空闲回收 {Idle}s，TD-11 零解密）",
            options.Listen.RelayPorts[0], options.Listen.RelayPorts[1], options.Relay.IdleTimeoutSec);

        // 4) 显式触发构造（后台循环随构造启动；无端口绑定，仅确认装配完整）
        _ = sp.GetRequiredService<PresenceMonitor>();
        _ = sp.GetRequiredService<SignalingCoordinator>();
        _ = sp.GetRequiredService<RetentionCleaner>(); // 启动即清一轮，此后每日（M2-08/OQ-17）
        logger.LogInformation("服务端启动完成：心跳超时 {Timeout}s、打洞超时 {Punch}s",
            options.Heartbeat.TimeoutSec, options.Punch.TimeoutSec);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("服务端停机中……");

        await sp.GetRequiredService<RelayService>().DisposeAsync();

        var stun = sp.GetRequiredService<StunService>();
        await stun.DisposeAsync();

        var control = sp.GetRequiredService<ControlServer>();
        await control.DisposeAsync(); // 停监听 + 关闭全部会话（断连落库，05 §5）

        await sp.GetRequiredService<PresenceMonitor>().DisposeAsync();
        await sp.GetRequiredService<SignalingCoordinator>().DisposeAsync();
        await sp.GetRequiredService<RetentionCleaner>().DisposeAsync();
        logger.LogInformation("服务端已停止");
    }
}

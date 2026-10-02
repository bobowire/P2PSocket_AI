// M1-29 本地 Web API 组合根（04 §2、TD-12/TD-16）：
// - LocalApiServices：宿主（M1-30）装配的服务束 + 事件→WS 接线
//   （mapping_state←引擎状态机、login_state←能力模式变迁、device_list←0x41 提示/0x14 重置 M2-15、
//   upgrade_required←版本拒答取回升级信息 M2-26）；
// - MapLocalApi：单点挂全部端点模块；宿主负责 WebApplication 构建/UseWebSockets/监听 127.0.0.1:7100。
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using P2P.Client.Control;
using P2P.Client.Diagnostics;
using P2P.Client.Mapping;
using P2P.Client.Nic;
using P2P.Client.Punch;
using P2P.Client.Registration;
using P2P.Client.Storage;
using P2P.Core.Protocol;

namespace P2P.Client.Web;

/// <summary>本地 API 服务束（宿主构造各组件后打包；Dispose 只管 Hub，组件寿命归宿主）。</summary>
public sealed class LocalApiServices : IAsyncDisposable
{
    public ControlClient Control { get; }
    public StateStore State { get; }
    public SettingsStore Settings { get; }
    public PeersStore Peers { get; }
    public ClientRegistrationService Wizard { get; }
    public MappingSyncService Mappings { get; }
    public PunchScheduler Scheduler { get; }
    public LanSegmentsStore LanSegments { get; }
    public string LogsDir { get; }
    public SubnetConflictDetector? SubnetConflicts { get; }
    public StunTester? StunTester { get; }
    public DevicePinger? DevicePinger { get; }
    public Tunnel.TunnelHost? Tunnels { get; }
    public Func<Guid, string?>? PeerLabelLookup { get; }
    public LocalApiContext Context { get; }
    public StatusHub Hub { get; }

    public LocalApiServices(ControlClient control, StateStore state, SettingsStore settings,
        PeersStore peers, ClientRegistrationService wizard, MappingSyncService mappings,
        PunchScheduler scheduler, LanSegmentsStore? lanSegments = null, string? logsDir = null,
        SubnetConflictDetector? subnetConflicts = null, StunTester? stunTester = null,
        DevicePinger? devicePinger = null, Tunnel.TunnelHost? tunnels = null,
        Func<Guid, string?>? peerLabelLookup = null)
    {
        Control = control;
        State = state;
        Settings = settings;
        Peers = peers;
        Wizard = wizard;
        Mappings = mappings;
        Scheduler = scheduler;
        LanSegments = lanSegments ?? new LanSegmentsStore(ClientPaths.DefaultBaseDir);
        LogsDir = logsDir ?? Path.Combine(ClientPaths.DefaultBaseDir, "logs");
        SubnetConflicts = subnetConflicts;
        StunTester = stunTester;
        DevicePinger = devicePinger;
        Tunnels = tunnels;
        PeerLabelLookup = peerLabelLookup;
        Context = new LocalApiContext();
        Hub = new StatusHub(mappings.Traffic); // mapping_stats 1s 采样源（04 §2.8）

        // WS 事件源接线（TD-16：提示性推送，真相由前端 refetch）
        Mappings.StateChanged += e => Hub.Publish(new
        {
            ev = WsEventNames.MappingState,
            id = e.MappingId,
            state = MappingSyncService.StateString(e.State),
            reason = e.Detail ?? "",
        });
        Control.CapabilityChanged += mode => Hub.Publish(new
        {
            ev = WsEventNames.LoginState,
            mode = mode == CapabilityMode.Normal ? "normal" : "passive",
        });
        // 版本拒答取回升级信息（M2-26，FR-C-904）：状态类提示→前端 refetch /api/upgrade/info
        Control.UpgradeRequired += info => Hub.Publish(new
        {
            ev = WsEventNames.UpgradeRequired,
            latestVersion = info.LatestVersion,
        });
        // 网段冲突出现/解除（M3-13，FR-C-204）：状态类提示→前端 refetch /api/system/state
        if (subnetConflicts is not null)
            subnetConflicts.ConflictsChanged += s => Hub.Publish(new
            {
                ev = WsEventNames.SubnetConflict,
                hasConflict = s?.Items.Length > 0,
            });
    }

    public async ValueTask DisposeAsync() => await Hub.DisposeAsync();
}

public static class LocalWebApi
{
    /// <summary>挂载本地 API 全部端点（04 §2.1/2.2/2.3/2.4/2.5/2.6/2.8）。宿主须先 UseWebSockets。</summary>
    public static IEndpointRouteBuilder MapLocalApi(this IEndpointRouteBuilder app, LocalApiServices services)
    {
        app.MapSystemApi(services.Control, services.State, services.Context, services.SubnetConflicts);
        app.MapSettingsApi(services.Control, services.Settings);
        app.MapAuthApi(services.Control, services.Wizard, services.Context);
        app.MapWizardApi(services.Control, services.Wizard, services.State, services.Settings, services.Context);
        app.MapMappingApi(services.Mappings);
        app.MapStatsApi(services.Mappings); // M3-12 流量汇总/导出（FR-C-1002）
        app.MapDeviceApi(services.Control, services.State, services.Hub); // M2-15 加 0x14 重置端点
        app.MapPeersApi(services.Control, services.Peers); // M2-23 目标设备级配置（04 §2.4）
        app.MapUpgradeApi(services.Control); // M2-26 升级引导（04 §2.7）
        app.MapGroupsApi(services.Control, services.Hub); // M2-27 分组全套（04 §2.4，0x42/0x50~0x57）
        app.MapLanSegmentsApi(services.Control, services.LanSegments, services.Hub); // M2-27 白名单（0x63）
        app.MapLogsApi(services.LogsDir); // M2-27 日志查询/导出（NFR-51）
        app.MapDiagnosticsApi(services.Scheduler, services.StunTester, services.DevicePinger,
            services.Tunnels, services.Settings, services.PeerLabelLookup); // M3-15/16 判型测速 + M3-14 诊断区（server-test/tunnels/rekey）
        app.Map("/ws/status", services.Hub.HandleAsync); // 04 §2.8 实时通道
        return app;
    }
}

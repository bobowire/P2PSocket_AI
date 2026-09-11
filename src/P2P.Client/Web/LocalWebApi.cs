// M1-29 本地 Web API 组合根（04 §2、TD-12/TD-16）：
// - LocalApiServices：宿主（M1-30）装配的服务束 + 事件→WS 接线
//   （mapping_state←引擎状态机、login_state←能力模式变迁，device_list→M2）；
// - MapLocalApi：单点挂全部端点模块；宿主负责 WebApplication 构建/UseWebSockets/监听 127.0.0.1:7100。
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using P2P.Client.Control;
using P2P.Client.Mapping;
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
    public ClientRegistrationService Wizard { get; }
    public MappingSyncService Mappings { get; }
    public PunchScheduler Scheduler { get; }
    public LocalApiContext Context { get; }
    public StatusHub Hub { get; }

    public LocalApiServices(ControlClient control, StateStore state, SettingsStore settings,
        ClientRegistrationService wizard, MappingSyncService mappings, PunchScheduler scheduler)
    {
        Control = control;
        State = state;
        Settings = settings;
        Wizard = wizard;
        Mappings = mappings;
        Scheduler = scheduler;
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
    }

    public async ValueTask DisposeAsync() => await Hub.DisposeAsync();
}

public static class LocalWebApi
{
    /// <summary>挂载本地 API 全部端点（04 §2.1/2.2/2.3/2.4/2.5/2.6/2.8）。宿主须先 UseWebSockets。</summary>
    public static IEndpointRouteBuilder MapLocalApi(this IEndpointRouteBuilder app, LocalApiServices services)
    {
        app.MapSystemApi(services.Control, services.State, services.Context);
        app.MapSettingsApi(services.Control, services.Settings);
        app.MapAuthApi(services.Control, services.Wizard, services.Context);
        app.MapWizardApi(services.Control, services.Wizard, services.State, services.Settings, services.Context);
        app.MapMappingApi(services.Mappings);
        app.MapDeviceApi(services.Control);
        app.MapDiagnosticsApi(services.Scheduler);
        app.Map("/ws/status", services.Hub.HandleAsync); // 04 §2.8 实时通道
        return app;
    }
}

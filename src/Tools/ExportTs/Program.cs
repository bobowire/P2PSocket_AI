// M1-31 export-ts 反射导出工具（06 §5、08 §2③）：
// 以真实 MapLocalApi 注册代码为单一事实源——组装惰性组件 → WebApplication.Build() →
// 读取 EndpointDataSource 路由表 + 反射 WsEventNames 常量与展示 DTO →
// 输出 ui-shared/types/api.d.ts（类型）与 api-paths.ts（路径常量，前端禁止手写字符串路径）。
// M3-09（编制定案③）：同法扩服务端——ServerWebHostService.Build 真实路由表 + ServerViews 展示 DTO
// → api-server-paths.ts / api-server.d.ts（server-app 消费；与客户端生成物分文件防常量名冲突）。
// 用法：dotnet run --project src/Tools/ExportTs -c Release [输出目录=web/ui-shared/src/types]
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using P2P.Client.Control;
using P2P.Client.Mapping;
using P2P.Client.Punch;
using P2P.Client.Registration;
using P2P.Client.Storage;
using P2P.Client.Tunnel;
using P2P.Client.Web;
using P2P.Core.Protocol;
using P2P.Nic;
using P2P.Server.Data;
using P2P.Server.Services;
using P2P.Server.Web;

var outputDir = TsGen.ResolveOutputDir(args.FirstOrDefault());
Directory.CreateDirectory(outputDir);

// ① 惰性组装（占位地址 127.0.0.1:1 永不可达；仅 Build 不 Start——只要路由表不要监听）
var temp = Path.Combine(Path.GetTempPath(), $"exportts-{Guid.NewGuid():N}");
ClientPaths.EnsureBaseDir(temp);
var settings = new SettingsStore(temp);
await settings.SaveAsync(new ClientSettings { ServerAddrs = ["127.0.0.1:1"], LocalWebPort = 1 });
var state = new StateStore(temp);
state.Load();
var peers = new PeersStore(temp);
peers.Load();
await using var control = new ControlClient(["127.0.0.1:1"], new ControlClientOptions(),
    deviceId: null, deviceSecret: null);
await using var host = new TunnelHost();
var scheduler = new PunchScheduler(new NullPuncher());
await using var engine = new MappingEngine(host, scheduler, IPAddress.Loopback);
var sync = new MappingSyncService(control, engine, state);
var wizard = new ClientRegistrationService(control, state, new NullNicManager());
var lanSegments = new LanSegmentsStore(temp); // M2-27 白名单镜像（路由装配需要，占位空集）
lanSegments.Load();
await using var api = new LocalApiServices(control, state, settings, peers, wizard, sync, scheduler,
    lanSegments, Path.Combine(temp, "logs"));

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = temp });
var app = builder.Build();
app.MapLocalApi(api);

// ①b 服务端 Web 同法（M3-09）：占位服务实例装配 ServerWebHostService.Build——
// 端点组 Map() 仅注册 lambda 不执行 handler，实例构造即可（relay/stun/limiter 缺省=零值快照形态）
var serverDb = new ExportTsDbFactory();
var serverAudit = new AuditLogger(serverDb);
var serverRegistry = new DeviceRegistry();
var serverInvalidation = new InvalidationPusher(serverDb, serverRegistry);
var serverPusher = new DeviceListPusher(serverDb, serverRegistry);
var serverGroups = new GroupService(serverDb, serverRegistry, serverAudit, serverPusher, serverInvalidation);
var serverAdmin = new AdminService(serverDb, serverRegistry, serverAudit, serverInvalidation, listPusher: serverPusher);
var serverApp = ServerWebHostService.Build(new P2P.Server.ServerOptions(),
    TimeProvider.System, new AdminSessionStore(TimeProvider.System),
    serverDb, serverAudit, serverAdmin, serverRegistry, serverGroups);

// ② 路由表（真实注册代码产出；{id:guid} 约束归一为 {id}；/ws/* 无 HTTP 方法元数据单列）
//    注：未 Start 的 WebApplication 须从 IEndpointRouteBuilder.DataSources 直取（DI 侧 DataSource 未定稿）
var (endpoints, wsRoutes) = ExtractRoutes(app);
var (serverEndpoints, serverWsRoutes) = ExtractRoutes(serverApp);

(List<(string Pattern, string Methods)> Http, List<string> Ws) ExtractRoutes(WebApplication built)
{
    var routes = ((IEndpointRouteBuilder)built).DataSources
        .SelectMany(ds => ds.Endpoints).OfType<RouteEndpoint>().ToList();
    var http = routes
        .Where(ep => (ep.RoutePattern.RawText ?? "").Length > 0
            && ep.Metadata.GetMetadata<IHttpMethodMetadata>() is not null)
        .Select(ep => (
            Pattern: TsGen.RouteParamRegex().Replace(ep.RoutePattern.RawText!, "{$1}").TrimEnd('/'),
            Methods: string.Join("/", ep.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.OrderBy(m => m))))
        .Distinct()
        .OrderBy(e => e.Pattern)
        .ToList();
    var ws = routes
        .Where(ep => (ep.RoutePattern.RawText ?? "").StartsWith("/ws/"))
        .Select(ep => ep.RoutePattern.RawText!)
        .Distinct().OrderBy(p => p).ToList();
    return (http, ws);
}

// ③ 生成物落盘（先写后释放：路由提取不依赖运行）
var generator = $"src/Tools/ExportTs @ {DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'}";
await File.WriteAllTextAsync(Path.Combine(outputDir, "api-paths.ts"),
    TsGen.EmitPathsTs(endpoints, wsRoutes, "LocalWebApi 真实路由表", generator));
await File.WriteAllTextAsync(Path.Combine(outputDir, "api.d.ts"), TsGen.EmitTypesTs(generator));
await File.WriteAllTextAsync(Path.Combine(outputDir, "api-server-paths.ts"),
    TsGen.EmitPathsTs(serverEndpoints, serverWsRoutes, "ServerWebHost 服务端 Web 路由表", generator,
        constName: "ServerApiPaths", endpointsName: "ServerApiEndpoints", pathTypeName: "ServerApiPath"));
await File.WriteAllTextAsync(Path.Combine(outputDir, "api-server.d.ts"),
    TsGen.EmitServerTypesTs(generator));
Console.WriteLine(
    $"export-ts：客户端 {endpoints.Count} HTTP 端点 + {wsRoutes.Count} WS 路由；" +
    $"服务端 {serverEndpoints.Count} HTTP 端点 + {serverWsRoutes.Count} WS 路由 → {outputDir}");
return 0;

// ── 生成：路径常量（api-paths.ts）────────────────────────────────────
internal static partial class TsGen // 生成逻辑集中（顶层语句宿主是 partial class Program，不可再声明）
{
    [GeneratedRegex(@"\{(\w+):[^}]+\}")]
    internal static partial Regex RouteParamRegex();

    /// <summary>路由 → 常量名（/api/mappings/{id}/enable → MappingsByIdEnable；连字符分段帕斯卡化）。</summary>
    internal static string ConstName(string route)
    {
        var sb = new StringBuilder();
        foreach (var seg in route.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == "api") continue;
            if (seg.StartsWith('{'))
            {
                var name = seg[1..seg.IndexOf('}')];
                sb.Append("By").Append(Pascal(name));
            }
            else
                foreach (var part in seg.Split('-'))
                    sb.Append(Pascal(part));
        }
        return sb.ToString();
    }

    private static string Pascal(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();

    /// <summary>展示 DTO 清单（无机密字段；ClientState 的 deviceSecret/静态私钥不导出）。</summary>
    internal static readonly Type[] DtoTypes =
    [
        typeof(P2P.Client.Mapping.MappingView), // 与服务端域同名，全限定消歧
        typeof(MappingTrafficView),
        typeof(RegistrationResult),
        typeof(GroupInfo),
        typeof(ClientSettings),
        typeof(ReconnectSettings),
        typeof(UpgradeInfoView), // M2-26 升级引导（04 §2.7 /api/upgrade/info 载荷）
        typeof(P2P.Client.Web.GroupView),       // M2-27 分组全套（04 §2.4 /api/groups）——与服务端域同名，全限定消歧
        typeof(P2P.Client.Web.GroupRequestView),
        typeof(GroupsView),
        typeof(LanSegmentView),  // M2-27 白名单（04 §2.5 /api/lan-segments）
        typeof(LogEntryView),    // M2-27 日志（04 §2.6 /api/logs）
        typeof(LogPageView),
        typeof(MappingStatsView), // M3-12 流量汇总（04 §2.5 /api/stats/summary，FR-C-1002）
        typeof(DeviceStatsView),
        typeof(StatsSummaryView),
        typeof(P2P.Client.Nic.SubnetConflictItem), // M3-13 网段冲突（04 §2.1 /api/system/state.conflict，FR-C-204）
        typeof(P2P.Client.Nic.SubnetConflictState),
        typeof(P2P.Client.Diagnostics.StunTestView), // M3-15 stun-test 判型（04 §2.6 /api/diagnostics/stun-test，05 §7.2）
        typeof(P2P.Client.Diagnostics.PingDeviceView), // M3-16 ping-device RTT（04 §2.6 /api/diagnostics/ping-device）
    ];

    internal static string EmitPathsTs(List<(string Pattern, string Methods)> endpoints,
        List<string> wsRoutes, string source, string generator,
        string constName = "ApiPaths", string endpointsName = "ApiEndpoints",
        string pathTypeName = "ApiPath")
    {
        var sb = new StringBuilder();
        sb.AppendLine("// 本文件由 export-ts 反射生成（06 §5、08 §2③），禁止手改；");
        sb.AppendLine($"// 源：{source}（{generator}）。前端禁止手写 API 字符串路径。");
        sb.AppendLine();
        sb.AppendLine($"export const {constName} = {{");
        foreach (var g in endpoints.Select(e => e.Pattern).Concat(wsRoutes).Distinct().OrderBy(p => p))
            sb.AppendLine($"  {ConstName(g)}: \"{g}\",");
        sb.AppendLine("} as const;");
        sb.AppendLine();
        sb.AppendLine($"export type {pathTypeName} = (typeof {constName})[keyof typeof {constName}];");
        sb.AppendLine();
        sb.AppendLine("/** 端点元数据（method×path；WS 路由 methods 为 [\"WS\"]）。 */");
        sb.AppendLine($"export const {endpointsName} = [");
        foreach (var e in endpoints)
            sb.AppendLine($"  {{ path: \"{e.Pattern}\", methods: [\"{e.Methods.Replace("/", "\", \"")}\"] }},");
        foreach (var w in wsRoutes)
            sb.AppendLine($"  {{ path: \"{w}\", methods: [\"WS\"] }},");
        sb.AppendLine("] as const;");
        return sb.ToString();
    }

    /// <summary>服务端展示 DTO 清单（M3-09 渐进入列：M3-10/11 页面随任务补）。无机密字段。</summary>
    internal static readonly Type[] ServerDtoTypes =
    [
        typeof(LoginResult),        // M3-02 login 载荷（首登改密提示）
        typeof(DashboardView),      // M3-06 仪表盘六指标组（FR-S-810）
        typeof(MappingsSummaryView),
        typeof(RelaySnapshotView),
        typeof(StunSnapshotView),
        typeof(StunDroppedView),
        typeof(PunchStatsView),
        typeof(HourlyBucketView),
        typeof(UserView),           // M3-10 用户页（FR-S-820）
        typeof(UserListView),
        typeof(TempPasswordResult),
        typeof(DeviceView),         // M3-10 设备页（FR-S-821）
        typeof(DeviceListView),
        typeof(RemoteCodeResult),
        typeof(P2P.Server.Web.GroupView),          // M3-10 分组页（FR-S-822/305；与服务端域同名全限定）
        typeof(GroupListView),
        typeof(PolicyResult),
        typeof(P2P.Server.Web.GroupRequestView),
        typeof(GroupRequestListView),
        typeof(DecisionResult),
        typeof(P2P.Server.Web.MappingView),        // M3-10 映射页（FR-S-823；与服务端域同名全限定）
        typeof(MappingBytesView),
        typeof(MappingListView),
        typeof(ConfigItemView),     // M3-10 注册开关（registration_open 经 system/config，M3-11 全页）
        typeof(ConfigListView),
        typeof(RelayConfigView),    // M3-11 运维页（FR-S-824/825）
        typeof(RelayEndView),
        typeof(P2P.Server.Web.RelaySessionView),  // 与 Services.RelaySessionView 同名全限定
        typeof(RelaySessionsView),
        typeof(AuditLogView),
        typeof(AuditLogListView),
    ];

    /// <summary>api-server.d.ts（M3-09 编制定案③：server-app 类型同源）。</summary>
    internal static string EmitServerTypesTs(string generator)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// 本文件由 export-ts 反射生成（06 §5、编制定案③），禁止手改。");
        sb.AppendLine($"// 源：P2P.Server Web 展示 DTO（ServerViews，{generator}）。");
        sb.AppendLine();
        sb.AppendLine("/** 服务端响应包裹（04 §3：code=0 成功；!=0 见错误码表 04 §5）。注意字段名 message（客户端为 msg）。 */");
        sb.AppendLine("export interface ServerApiEnvelope<T> {");
        sb.AppendLine("  code: number;");
        sb.AppendLine("  message: string;");
        sb.AppendLine("  data: T;");
        sb.AppendLine("}");
        sb.AppendLine();
        foreach (var t in ServerDtoTypes)
            sb.AppendLine(EmitInterface(t));
        sb.AppendLine("/** TD-22 映射状态投影域（0x62 流水末次；无流水=unknown——客户端状态机是真相源）。 */");
        sb.AppendLine("export type MappingStatusProjection = \"direct\" | \"relay\" | \"failed\" | \"invalid\" | \"unknown\";");
        return sb.ToString();
    }

    internal static string EmitTypesTs(string generator)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// 本文件由 export-ts 反射生成（06 §5、08 §2③），禁止手改。");
        sb.AppendLine($"// 源：P2P.Client 展示 DTO + WsEventNames 常量（{generator}）。");
        sb.AppendLine();
        sb.AppendLine("/** 响应包裹（04 §0：code=0 成功；!=0 见错误码表 04 §5）。 */");
        sb.AppendLine("export interface ApiEnvelope<T> {");
        sb.AppendLine("  code: number;");
        sb.AppendLine("  msg: string;");
        sb.AppendLine("  data: T;");
        sb.AppendLine("}");
        sb.AppendLine();
        foreach (var t in DtoTypes)
            sb.AppendLine(EmitInterface(t));
        sb.AppendLine("/** 映射状态机（02 §4.5：disabled→punching→direct/failed；relay/invalid 为 M2 预留）。 */");
        sb.AppendLine("export type MappingState = \"disabled\" | \"punching\" | \"direct\" | \"relay\" | \"failed\" | \"invalid\";");
        sb.AppendLine();
        sb.AppendLine("/** 系统阶段（04 §2.1 /api/system/state.phase）。 */");
        sb.AppendLine("export type SystemPhase = \"unregistered\" | \"wizard\" | \"running\" | \"degraded\";");
        sb.AppendLine();
        sb.AppendLine("/** 能力模式（02 §2.5：Normal=可主动；Passive=哑节点仅响应）。 */");
        sb.AppendLine("export type CapabilityMode = \"normal\" | \"passive\";");
        sb.AppendLine();
        sb.AppendLine("// ── WS 事件（04 §2.8；事件名反射自 WsEventNames 单一事实源）──────");
        foreach (var f in typeof(WsEventNames).GetFields(BindingFlags.Public | BindingFlags.Static))
            sb.AppendLine($"// {f.Name} = \"{f.GetRawConstantValue()}\"");
        sb.AppendLine();
        sb.AppendLine("/** 高频数值类：直写 store（下秒覆盖，丢失无害，TD-16）。 */");
        sb.AppendLine("export interface MappingStatsEvent {");
        sb.AppendLine("  ev: \"mapping_stats\";");
        sb.AppendLine("  id: string;");
        sb.AppendLine("  rateUp: number;");
        sb.AppendLine("  rateDown: number;");
        sb.AppendLine("  path: \"direct\" | \"relay\";");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("/** 状态类：不直接改 store，仅触发 refetch（TD-16）。 */");
        sb.AppendLine("export interface MappingStateEvent {");
        sb.AppendLine("  ev: \"mapping_state\";");
        sb.AppendLine("  id: string;");
        sb.AppendLine("  state: MappingState;");
        sb.AppendLine("  reason: string;");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("export interface LoginStateEvent {");
        sb.AppendLine("  ev: \"login_state\";");
        sb.AppendLine("  mode: CapabilityMode;");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("/** 设备列表（0x41 属 M2；M1 前端以 10s 轮询 refetch 兜底）。 */");
        sb.AppendLine("export interface DeviceListEvent {");
        sb.AppendLine("  ev: \"device_list\";");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("/** 升级引导（M2-26，FR-C-904：版本拒答/升级信息到达→refetch /api/upgrade/info）。 */");
        sb.AppendLine("export interface UpgradeRequiredEvent {");
        sb.AppendLine("  ev: \"upgrade_required\";");
        sb.AppendLine("  latestVersion: string;");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("/** 网段冲突提示（M3-13，FR-C-204：出现/解除→refetch /api/system/state）。 */");
        sb.AppendLine("export interface SubnetConflictEvent {");
        sb.AppendLine("  ev: \"subnet_conflict\";");
        sb.AppendLine("  hasConflict: boolean;");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("export type WsEvent = MappingStatsEvent | MappingStateEvent | LoginStateEvent | DeviceListEvent | UpgradeRequiredEvent | SubnetConflictEvent;");
        return sb.ToString();
    }

    private static readonly NullabilityInfoContext Nullability = new();

    private static string EmitInterface(Type type)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"export interface {type.Name} {{");
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var nullable = Nullable.GetUnderlyingType(p.PropertyType) is not null
                || Nullability.Create(p).WriteState == NullabilityState.Nullable;
            sb.AppendLine($"  {Camel(p.Name)}: {TsType(p.PropertyType)}{(nullable ? " | null" : "")};");
        }
        sb.AppendLine("}");
        sb.AppendLine();
        return sb.ToString();
    }

    private static string TsType(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t == typeof(Guid) || t == typeof(string)) return "string";
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset)) return "string"; // ISO-8601（服务端 DTO，M3-09）
        if (t == typeof(bool)) return "boolean";
        if (DtoTypes.Contains(t) || ServerDtoTypes.Contains(t)) return t.Name; // 展示 DTO 互引
        if (t.IsArray) return $"{TsType(t.GetElementType()!)}[]";
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
            return $"{TsType(t.GetGenericArguments()[0])}[]"; // List<T>（M3-09 hourly 桶序列）
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>)
            && t.GetGenericArguments()[0] == typeof(string))
            return $"Record<string, {TsType(t.GetGenericArguments()[1])}>"; // Dictionary<string,T>（M3-09 byStatus）
        if (t.IsEnum) return string.Join(" | ", Enum.GetNames(t).Select(n => $"\"{n}\""));
        return "number"; // int/long/ushort/double 等数值族
    }

    private static string Camel(string s) => char.ToLowerInvariant(s[0]) + s[1..];

    internal static string ResolveOutputDir(string? arg)
    {
        if (arg is not null) return Path.GetFullPath(arg);
        var dir = new DirectoryInfo(Environment.CurrentDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "web", "ui-shared", "src", "types");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent!;
        }
        throw new InvalidOperationException("未找到 web/ui-shared/src/types（在仓库内执行，或显式传输出目录）");
    }
}

/// <summary>服务端路由装配的占位库工厂（内存 Sqlite；端点注册不触库）。</summary>
internal sealed class ExportTsDbFactory : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite("DataSource=:memory:").Options);
}

/// <summary>打洞器空实现（仅装配路由，永不调用）。</summary>
internal sealed class NullPuncher : IPuncher
{
    public Task<PunchOutcome> InitiateAsync(Guid targetDeviceId, Guid? triggerMappingId, string proto,
        CancellationToken ct = default)
        => Task.FromResult(PunchOutcome.Failure(targetDeviceId, "export_ts"));

    public Task<PunchOutcome> RespondAsync(PunchInvite invite, CancellationToken ct = default)
        => Task.FromResult(PunchOutcome.Failure(invite.Peer.DeviceId, "export_ts"));
}

/// <summary>网卡空实现（仅装配路由，永不调用）。</summary>
internal sealed class NullNicManager : INicManager
{
#pragma warning disable CS0067 // 接口事件保留位
    public event Action<string>? Degraded;
#pragma warning restore CS0067

    public Task<NicHandle> EnsureAsync(IPAddress virtualIp, CancellationToken ct = default)
        => Task.FromResult(new NicHandle("export-ts", virtualIp));

    public Task RemoveAsync(CancellationToken ct = default) => Task.CompletedTask;

    public NicHealth CheckHealth(IPAddress expectedIp) => new(NicHealthState.Healthy, expectedIp);

    public Task<bool> RemoveLeftoverAsync(CancellationToken ct = default) => Task.FromResult(false);
}

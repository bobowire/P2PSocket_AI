// M1-31 export-ts 反射导出工具（06 §5、08 §2③）：
// 以真实 MapLocalApi 注册代码为单一事实源——组装惰性组件 → WebApplication.Build() →
// 读取 EndpointDataSource 路由表 + 反射 WsEventNames 常量与展示 DTO →
// 输出 ui-shared/types/api.d.ts（类型）与 api-paths.ts（路径常量，前端禁止手写字符串路径）。
// 用法：dotnet run --project src/Tools/ExportTs -c Release [输出目录=web/ui-shared/src/types]
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
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
await using var api = new LocalApiServices(control, state, settings, peers, wizard, sync, scheduler);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = temp });
var app = builder.Build();
app.MapLocalApi(api);

// ② 路由表（真实注册代码产出；{id:guid} 约束归一为 {id}；/ws/* 无 HTTP 方法元数据单列）
//    注：未 Start 的 WebApplication 须从 IEndpointRouteBuilder.DataSources 直取（DI 侧 DataSource 未定稿）
var routes = ((IEndpointRouteBuilder)app).DataSources
    .SelectMany(ds => ds.Endpoints).OfType<RouteEndpoint>().ToList();
var endpoints = routes
    .Where(ep => (ep.RoutePattern.RawText ?? "").Length > 0
        && ep.Metadata.GetMetadata<IHttpMethodMetadata>() is not null)
    .Select(ep => (
        Pattern: TsGen.RouteParamRegex().Replace(ep.RoutePattern.RawText!, "{$1}").TrimEnd('/'),
        Methods: string.Join("/", ep.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.OrderBy(m => m))))
    .Distinct()
    .OrderBy(e => e.Pattern)
    .ToList();
var wsRoutes = routes
    .Where(ep => (ep.RoutePattern.RawText ?? "").StartsWith("/ws/"))
    .Select(ep => ep.RoutePattern.RawText!)
    .Distinct().OrderBy(p => p).ToList();

// ③ 生成物落盘（先写后释放：路由提取不依赖运行）
var generator = $"src/Tools/ExportTs @ {DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'}";
await File.WriteAllTextAsync(Path.Combine(outputDir, "api-paths.ts"), TsGen.EmitPathsTs(endpoints, wsRoutes, generator));
await File.WriteAllTextAsync(Path.Combine(outputDir, "api.d.ts"), TsGen.EmitTypesTs(generator));
Console.WriteLine($"export-ts：{endpoints.Count} HTTP 端点 + {wsRoutes.Count} WS 路由 → {outputDir}");
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
        typeof(MappingView),
        typeof(MappingTrafficView),
        typeof(RegistrationResult),
        typeof(GroupInfo),
        typeof(ClientSettings),
        typeof(ReconnectSettings),
        typeof(UpgradeInfoView), // M2-26 升级引导（04 §2.7 /api/upgrade/info 载荷）
    ];

    internal static string EmitPathsTs(List<(string Pattern, string Methods)> endpoints,
        List<string> wsRoutes, string generator)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// 本文件由 export-ts 反射生成（06 §5、08 §2③），禁止手改；");
        sb.AppendLine($"// 源：LocalWebApi 真实路由表（{generator}）。前端禁止手写 API 字符串路径。");
        sb.AppendLine();
        sb.AppendLine("export const ApiPaths = {");
        foreach (var g in endpoints.Select(e => e.Pattern).Concat(wsRoutes).Distinct().OrderBy(p => p))
            sb.AppendLine($"  {ConstName(g)}: \"{g}\",");
        sb.AppendLine("} as const;");
        sb.AppendLine();
        sb.AppendLine("export type ApiPath = (typeof ApiPaths)[keyof typeof ApiPaths];");
        sb.AppendLine();
        sb.AppendLine("/** 端点元数据（method×path；WS 路由 methods 为 [\"WS\"]）。 */");
        sb.AppendLine("export const ApiEndpoints = [");
        foreach (var e in endpoints)
            sb.AppendLine($"  {{ path: \"{e.Pattern}\", methods: [\"{e.Methods.Replace("/", "\", \"")}\"] }},");
        foreach (var w in wsRoutes)
            sb.AppendLine($"  {{ path: \"{w}\", methods: [\"WS\"] }},");
        sb.AppendLine("] as const;");
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
        sb.AppendLine("export type WsEvent = MappingStatsEvent | MappingStateEvent | LoginStateEvent | DeviceListEvent | UpgradeRequiredEvent;");
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
        if (t == typeof(bool)) return "boolean";
        if (DtoTypes.Contains(t)) return t.Name; // 展示 DTO 互引（如 ClientSettings.reconnect）
        if (t.IsArray) return $"{TsType(t.GetElementType()!)}[]";
        if (t.IsEnum) return string.Join(" | ", Enum.GetNames(t).Select(n => $"\"{n}\""));
        return "number"; // int/long/ushort 等整数族
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

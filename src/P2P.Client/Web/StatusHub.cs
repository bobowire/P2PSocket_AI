// M1-29 WS 状态通道（04 §2.8 /ws/status、TD-16）：
// - 连接注册表 + 每连接独立发送者（有界 256 条，慢消费者丢弃——服务端只管推提示，前端 refetch 取真相）；
// - mapping_stats：1s 采样流量快照算增量，仅活跃流量（增量>0）才推（04 §2.8"有活跃流量时"）；
// - 事件源接线在 LocalApiServices（mapping_state←引擎状态机、login_state←能力模式变迁）。
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using P2P.Client.Mapping;

namespace P2P.Client.Web;

/// <summary>WS 事件名常量（04 §2.8；单一事实源——ExportTs 反射同源生成前端 TS 联合类型）。</summary>
public static class WsEventNames
{
    /// <summary>映射状态变迁（状态类→前端 refetch，TD-16）。</summary>
    public const string MappingState = "mapping_state";

    /// <summary>映射速率增量（1s 数值类→前端直写，TD-16）。</summary>
    public const string MappingStats = "mapping_stats";

    /// <summary>设备列表变更提示（0x41/远程码重置触发→前端 refetch /api/devices，M2-15）。</summary>
    public const string DeviceList = "device_list";

    /// <summary>登录态/能力模式变迁（状态类→前端 refetch）。</summary>
    public const string LoginState = "login_state";

    /// <summary>版本拒答/升级信息到达（M2-26，FR-C-904：状态类→前端 refetch /api/upgrade/info）。</summary>
    public const string UpgradeRequired = "upgrade_required";
}

/// <summary>WS 事件广播中枢（单例；宿主挂 /ws/status 端点转 <see cref="HandleAsync"/>）。</summary>
public sealed class StatusHub : IAsyncDisposable
{
    private readonly Func<IReadOnlyList<MappingTrafficView>>? _traffic;
    private readonly ConcurrentDictionary<int, Connection> _connections = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _statsLoop;
    private readonly Dictionary<Guid, (long Up, long Down)> _lastSample = []; // _statsLoop 单线程访问
    private int _nextId;
    private int _disposed;

    public StatusHub(Func<IReadOnlyList<MappingTrafficView>>? traffic = null)
    {
        _traffic = traffic;
        _statsLoop = StatsLoopAsync(_cts.Token);
    }

    /// <summary>广播事件（序列化一次，逐连接入队；无连接时为空操作）。</summary>
    public void Publish(object evt)
    {
        if (_connections.IsEmpty || Volatile.Read(ref _disposed) == 1) return;
        var payload = JsonSerializer.SerializeToUtf8Bytes(evt, Api.Json);
        foreach (var conn in _connections.Values)
            conn.Out.Writer.TryWrite(payload); // 满则丢弃（TD-16：提示事件，真相在前端 refetch）
    }

    /// <summary>WS 升级与连接生命周期（M1 客户端不发上行——仅排空至对端关闭）。</summary>
    public async Task HandleAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var conn = new Connection(ws);
        conn.Sender = Task.Run(() => SendLoopAsync(conn, _cts.Token));
        var id = Interlocked.Increment(ref _nextId);
        _connections[id] = conn;
        try
        {
            var buffer = new byte[1024];
            while (true)
            {
                var received = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                if (received.CloseStatus.HasValue) break;
            }
        }
        catch { /* 对端断开/停机 */ }
        finally
        {
            _connections.TryRemove(id, out _);
            conn.Out.Writer.TryComplete();
            try { await conn.Sender; } catch { /* 发送侧随连接终止 */ }
        }
    }

    private static async Task SendLoopAsync(Connection conn, CancellationToken ct)
    {
        await foreach (var payload in conn.Out.Reader.ReadAllAsync(ct))
            await conn.Ws.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    /// <summary>mapping_stats 采样（04 §2.8：1s 周期、有活跃流量时）。</summary>
    private async Task StatsLoopAsync(CancellationToken ct)
    {
        if (_traffic is null) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (_connections.IsEmpty)
                {
                    _lastSample.Clear();
                    continue;
                }
                var views = _traffic();
                foreach (var view in views)
                {
                    var prev = _lastSample.GetValueOrDefault(view.MappingId);
                    _lastSample[view.MappingId] = (view.BytesUp, view.BytesDown);
                    var dUp = view.BytesUp - prev.Up;
                    var dDown = view.BytesDown - prev.Down;
                    if (dUp <= 0 && dDown <= 0) continue;
                    Publish(new { ev = WsEventNames.MappingStats, id = view.MappingId,
                        rateUp = dUp, rateDown = dDown, path = view.Path });
                }
                // 摘除已消失映射的采样基线（防长期增长）
                if (_lastSample.Count > views.Count)
                    foreach (var stale in _lastSample.Keys
                        .Where(k => views.All(v => v.MappingId != k)).ToList())
                        _lastSample.Remove(stale);
            }
        }
        catch (OperationCanceledException) { /* 停机 */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _cts.Cancel();
        foreach (var conn in _connections.Values) conn.Out.Writer.TryComplete();
        var senders = _connections.Values.Select(c => c.Sender).ToArray();
        _connections.Clear();
        try { await Task.WhenAll(senders); } catch { /* 取消路径 */ }
        try { await _statsLoop; } catch { /* 取消路径 */ }
        _cts.Dispose();
    }

    private sealed class Connection(WebSocket ws)
    {
        public WebSocket Ws { get; } = ws;
        public Channel<byte[]> Out { get; } = Channel.CreateBounded<byte[]>(256);
        public Task Sender { get; set; } = Task.CompletedTask;
    }
}

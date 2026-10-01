// M1-25 TunnelHost（05 §4、02 §4.5）：设备对粒度会话表——A↔B 同一时刻至多一条活跃隧道；
// 打洞+握手成功后 Attach；断链事件转发（映射状态机回 punching 由 M1-27/28 订阅）。
// M2-19 回切排水（02 §6.2/NET-75）：替换仍存活的旧会话（中继↔直连切换）不再立即关闭——
// 旧路径在途帧于 2s 接收窗口排空后才关闭，新会话即刻承载后续流量（先排水后切换）。
using System.Collections.Concurrent;

namespace P2P.Client.Tunnel;

/// <summary>隧道宿主：peerDeviceId → TunnelSession 会话表。</summary>
public sealed class TunnelHost : IAsyncDisposable
{
    /// <summary>NET-75 排水窗：旧路径在途帧接收窗口（02 §6.2 定值 2s；测试可注入缩短）。</summary>
    public static readonly TimeSpan DefaultDrainWindow = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<Guid, TunnelSession> _sessions = new();
    private readonly TimeSpan _drainWindow;
    private readonly TimeProvider _time;
    private int _disposed;

    public TunnelHost(TimeSpan? drainWindow = null, TimeProvider? time = null)
    {
        _drainWindow = drainWindow ?? DefaultDrainWindow;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>任一会话断链（含被新会话替换的旧会话——排水窗到期或已亡即关）。参数：会话、原因。
    /// 携带会话对象（M2-19）：排水期新旧会话并存，订阅方须按会话区分（只清理旧会话的 channel）。</summary>
    public event Action<TunnelSession, string>? SessionDisconnected;

    /// <summary>新会话挂入（新建或替换；M2-19）：被邀请方/回退路径由宿主直接 Attach——
    /// 映射状态机据此刻画 relay 态翻转（如对端回切直连时本端映射 relay→direct）。</summary>
    public event Action<TunnelSession>? SessionAttached;

    /// <summary>会话内部日志转发（接收异常/REKEY/断链原因等，M2_38 排查补齐的诊断缺口）：
    /// 携会话对象——排水期新旧会话并存时须按会话区分。</summary>
    public event Action<TunnelSession, string>? SessionLog;

    /// <summary>当前活跃会话（诊断/隧道复用检查，02 §4.5）。</summary>
    public IReadOnlyCollection<TunnelSession> Sessions => (IReadOnlyCollection<TunnelSession>)_sessions.Values;

    /// <summary>取设备对活跃隧道（无则 null——映射 enable 时据此决定排队打洞还是复用）。</summary>
    public TunnelSession? Get(Guid peerDeviceId)
        => _sessions.TryGetValue(peerDeviceId, out var s) && !s.IsClosed ? s : null;

    /// <summary>挂入会话（握手成功后）：同设备对已有会话则替换——旧会话已亡立即关闭（M1 重建语义）；
    /// 仍存活（M2-19 回切：中继↔直连/中继↔中继切换）走排水窗延迟关闭（NET-75 先排水后切换）。</summary>
    public void Attach(TunnelSession session)
    {
        ObjectDisposedException.ThrowIf(_disposed == 1, this);
        if (_sessions.TryGetValue(session.PeerDeviceId, out var old) && !ReferenceEquals(old, session))
        {
            if (old.IsClosed) old.Close("replaced");
            else _ = DrainAndCloseAsync(old);
        }
        session.Disconnected += OnSessionDisconnected;
        session.Log += m => SessionLog?.Invoke(session, m); // 闭包捕获会话上下文（Log 为 Action<string>；会话 Dispose 后不再触发，无须解绑）
        _sessions[session.PeerDeviceId] = session;
        try { SessionAttached?.Invoke(session); }
        catch (Exception) { /* 订阅方异常不阻断挂入 */ }
    }

    /// <summary>排水后关闭（NET-75）：窗口内旧会话接收循环与 channel splice 照常（在途帧送达），
    /// 到期 Close → 断链事件按会话转发（此时新会话已在表内，映射状态机不回 punching）。</summary>
    private async Task DrainAndCloseAsync(TunnelSession old)
    {
        try { await Task.Delay(_drainWindow, _time, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { /* 时间源异常：立即进入关闭 */ }
        old.Close($"replaced_drained: {_drainWindow.TotalSeconds:0.#}s 排水窗已满");
    }

    private void OnSessionDisconnected(TunnelSession session, string reason)
    {
        // 只有仍是表内当前会话才摘除（被替换的旧会话事件不误删新会话）
        if (_sessions.TryGetValue(session.PeerDeviceId, out var current) && ReferenceEquals(current, session))
            _sessions.TryRemove(session.PeerDeviceId, out _);
        SessionDisconnected?.Invoke(session, reason);
    }

    /// <summary>主动移除并销毁设备对会话（解绑/停机）。</summary>
    public async Task RemoveAsync(Guid peerDeviceId, string reason)
    {
        if (_sessions.TryRemove(peerDeviceId, out var session))
        {
            session.Disconnected -= OnSessionDisconnected;
            await session.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        foreach (var session in _sessions.Values)
        {
            session.Disconnected -= OnSessionDisconnected;
            await session.DisposeAsync();
        }
        _sessions.Clear();
    }
}

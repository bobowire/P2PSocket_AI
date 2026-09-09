// M1-25 TunnelHost（05 §4、02 §4.5）：设备对粒度会话表——A↔B 同一时刻至多一条活跃隧道；
// 打洞+握手成功后 Attach；断链事件转发（映射状态机回 punching 由 M1-27/28 订阅）。
using System.Collections.Concurrent;

namespace P2P.Client.Tunnel;

/// <summary>隧道宿主：peerDeviceId → TunnelSession 会话表。</summary>
public sealed class TunnelHost : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, TunnelSession> _sessions = new();
    private int _disposed;

    /// <summary>任一会话断链（含被新会话替换的旧会话）。参数：peerDeviceId、原因。</summary>
    public event Action<Guid, string>? SessionDisconnected;

    /// <summary>当前活跃会话（诊断/隧道复用检查，02 §4.5）。</summary>
    public IReadOnlyCollection<TunnelSession> Sessions => (IReadOnlyCollection<TunnelSession>)_sessions.Values;

    /// <summary>取设备对活跃隧道（无则 null——映射 enable 时据此决定排队打洞还是复用）。</summary>
    public TunnelSession? Get(Guid peerDeviceId)
        => _sessions.TryGetValue(peerDeviceId, out var s) && !s.IsClosed ? s : null;

    /// <summary>挂入会话（握手成功后）：同设备对已有会话则先销毁旧的（隧道重建场景：新 sessionId）。</summary>
    public void Attach(TunnelSession session)
    {
        ObjectDisposedException.ThrowIf(_disposed == 1, this);
        if (_sessions.TryGetValue(session.PeerDeviceId, out var old) && !ReferenceEquals(old, session))
        {
            // 保留订阅：Close 走 OnSessionDisconnected 摘表并转发事件（文档语义：含被替换的旧会话）
            old.Close("replaced"); // 旧会话销毁（02 §4.5：重建=新 sessionId 计数器清零）
        }
        session.Disconnected += OnSessionDisconnected;
        _sessions[session.PeerDeviceId] = session;
    }

    private void OnSessionDisconnected(TunnelSession session, string reason)
    {
        // 只有仍是表内当前会话才摘除（被替换的旧会话事件不误删新会话）
        if (_sessions.TryGetValue(session.PeerDeviceId, out var current) && ReferenceEquals(current, session))
            _sessions.TryRemove(session.PeerDeviceId, out _);
        SessionDisconnected?.Invoke(session.PeerDeviceId, reason);
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

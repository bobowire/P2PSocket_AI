using System.Collections.Concurrent;

namespace P2P.Server.Services;

/// <summary>
/// 在线设备注册表（05 §5：ConcurrentDictionary<deviceId, ControlSession>——并发白名单之一，TD-14）。
/// 在线判定在内存；移除时由会话负责 last_seen_at 落库。
/// </summary>
public sealed class DeviceRegistry
{
    private readonly ConcurrentDictionary<Guid, ControlSession> _sessions = [];

    /// <summary>设备上线（会话建立登记后；M2-10 0x41 推送订阅）。</summary>
    public event Action<Guid>? DeviceOnline;

    /// <summary>设备离线（会话关闭移除后；同设备换线重连时旧会话移除为 no-op 不触发）。</summary>
    public event Action<Guid>? DeviceOffline;

    /// <summary>会话建立（Proof 通过）后登记。同设备旧会话被替换并踢线（单会话在线）。</summary>
    public void Register(ControlSession session)
    {
        if (_sessions.TryGetValue(session.DeviceId, out var old) && !ReferenceEquals(old, session))
            _ = old.CloseAsync("replaced_by_new_session");
        _sessions[session.DeviceId] = session;
        DeviceOnline?.Invoke(session.DeviceId);
    }

    public void Unregister(ControlSession session)
    {
        if (_sessions.TryRemove(new KeyValuePair<Guid, ControlSession>(session.DeviceId, session)))
            DeviceOffline?.Invoke(session.DeviceId);
    }

    public bool IsOnline(Guid deviceId) => _sessions.ContainsKey(deviceId);

    public ControlSession? TryGet(Guid deviceId) => _sessions.TryGetValue(deviceId, out var s) ? s : null;

    public IReadOnlyCollection<Guid> OnlineDeviceIds => [.. _sessions.Keys];

    /// <summary>在线会话快照（PresenceMonitor 扫描用）。</summary>
    public IReadOnlyCollection<ControlSession> Sessions() => [.. _sessions.Values];
}

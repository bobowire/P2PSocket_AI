using System.Collections.Concurrent;

namespace P2P.Server.Services;

/// <summary>
/// 在线设备注册表（05 §5：ConcurrentDictionary<deviceId, ControlSession>——并发白名单之一，TD-14）。
/// 在线判定在内存；移除时由会话负责 last_seen_at 落库。
/// </summary>
public sealed class DeviceRegistry
{
    private readonly ConcurrentDictionary<Guid, ControlSession> _sessions = [];

    /// <summary>会话建立（Proof 通过）后登记。同设备旧会话被替换并踢线（单会话在线）。</summary>
    public void Register(ControlSession session)
    {
        if (_sessions.TryGetValue(session.DeviceId, out var old) && !ReferenceEquals(old, session))
            _ = old.CloseAsync("replaced_by_new_session");
        _sessions[session.DeviceId] = session;
    }

    public void Unregister(ControlSession session)
        => _sessions.TryRemove(new KeyValuePair<Guid, ControlSession>(session.DeviceId, session));

    public bool IsOnline(Guid deviceId) => _sessions.ContainsKey(deviceId);

    public ControlSession? TryGet(Guid deviceId) => _sessions.TryGetValue(deviceId, out var s) ? s : null;

    public IReadOnlyCollection<Guid> OnlineDeviceIds => [.. _sessions.Keys];
}

// M3-01/02 管理员会话存储（04 §3.1：Cookie 会话服务端内存态，重启失效即设计——07 §8）。
// M3-01 交付存储与校验骨架（认证中间件消费），login/change-password/logout 端点归 M3-02。
using System.Collections.Concurrent;

namespace P2P.Server.Web;

/// <summary>管理员会话：随机 id → 过期时刻（UTC）；懒过期（校验时判定，残留条目无害——单管理员规模）。</summary>
public sealed class AdminSessionStore(TimeProvider time)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new();

    /// <summary>Cookie 名（04 §3.1：HttpOnly、SameSite=Strict、无 Secure——HTTP，D11）。</summary>
    public const string CookieName = "p2p_admin";

    public string Create()
    {
        var id = Guid.NewGuid().ToString("N");
        _sessions[id] = time.GetUtcNow() + Lifetime;
        return id;
    }

    public bool IsValid(string? id)
        => id is not null && _sessions.TryGetValue(id, out var expires) && expires > time.GetUtcNow();

    public void Remove(string id) => _sessions.TryRemove(id, out _);

    public int Count => _sessions.Count;
}

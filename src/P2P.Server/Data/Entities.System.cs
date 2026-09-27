namespace P2P.Server.Data;

// ── 03 §2.7 audit_logs / §2.8 server_config / §2.9 punch_stats ────────────

/// <summary>审计事件（NFR-54、SEC-51）：register|login|punch_deny|mapping_deny|disable|…。</summary>
public sealed class AuditLog
{
    public long Id { get; set; }                       // AUTOINCREMENT
    public DateTime Ts { get; set; }
    public string Event { get; set; } = "";
    public Guid? DeviceId { get; set; }
    public Guid? UserId { get; set; }
    public string? Detail { get; set; }                // JSON 摘要
}

/// <summary>系统配置键值（FR-S-825；进程级配置在配置文件，库内仅业务开关）。</summary>
public sealed class ServerConfigEntry
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>打洞结果统计（FR-S-503/810）。数据来源 0x72（M2 落库；表结构 M1 建齐）。</summary>
public sealed class PunchStat
{
    public long Id { get; set; }                       // AUTOINCREMENT
    public DateTime Ts { get; set; }
    public Guid SessionId { get; set; }
    public Guid InitiatorId { get; set; }
    public Guid TargetId { get; set; }
    public string Proto { get; set; } = "";            // udp|tcp
    public int Concurrency { get; set; }               // TCP 打洞 N
    public string Result { get; set; } = "";           // direct|relay|failed
    public string? Reason { get; set; }
    public int DurationMs { get; set; }
}

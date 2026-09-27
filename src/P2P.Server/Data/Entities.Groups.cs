namespace P2P.Server.Data;

// ── 03 §2.3 groups / group_members / join_requests（FR-S-301~307）─────────

/// <summary>分组。全局唯一默认分组由初始化创建（owner=内置 admin）。</summary>
public sealed class Group
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public Guid OwnerUserId { get; set; }
    public bool IsDefault { get; set; }
    public string JoinPolicy { get; set; } = "free";   // free | approval（D9）
    public string? InviteCode { get; set; }            // 6 位去混淆字符集；撤销=置 NULL
    public DateTime CreatedAt { get; set; }

    public User Owner { get; set; } = null!;
}

public sealed class GroupMember
{
    public Guid Id { get; set; }
    public Guid GroupId { get; set; }
    public Guid DeviceId { get; set; }
    public bool Approved { get; set; }
    public DateTime JoinedAt { get; set; }

    public Group Group { get; set; } = null!;
    public Device Device { get; set; } = null!;
}

/// <summary>准入审批队列（approval 策略）。</summary>
public sealed class JoinRequest
{
    public Guid Id { get; set; }
    public Guid GroupId { get; set; }
    public Guid DeviceId { get; set; }
    public string Status { get; set; } = "pending";    // pending|approved|rejected
    public DateTime CreatedAt { get; set; }
    public DateTime? HandledAt { get; set; }

    public Group Group { get; set; } = null!;
    public Device Device { get; set; } = null!;
}

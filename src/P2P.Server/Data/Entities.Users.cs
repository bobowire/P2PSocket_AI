namespace P2P.Server.Data;

// ── 03 §2.1 users / §2.2 devices / §2.5 lan_segments ─────────────────────
// 实体分组存放：文件名按域（用户/设备/分组/映射/系统），列名蛇形在 AppModelCreating 统一映射。

/// <summary>用户（FR-S-201~205；内置 admin 初始化见 DbInitializer）。</summary>
public sealed class User
{
    public Guid Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";   // PasswordHasher 自包含格式
    public bool IsAdmin { get; set; }
    public bool Disabled { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<Device> OwnedDevices { get; } = [];
    public List<Group> OwnedGroups { get; } = [];
}

/// <summary>设备（FR-S-101~106）。deviceSecret 可恢复存储（07 §4）。</summary>
public sealed class Device
{
    public Guid Id { get; set; }                      // 服务端签发权威身份（D6）
    public Guid? OwnerUserId { get; set; }            // 登录绑定；NULL=未归属
    public string DeviceName { get; set; } = "";
    public string Os { get; set; } = "";              // windows|linux
    public string ClientVersion { get; set; } = "";
    public string MacCode { get; set; } = "";         // 同码再注册=覆盖式恢复（OQ-14）
    public string RemoteCode { get; set; } = "";      // 6 位 0-9+abc 唯一（OQ-8）
    public string VirtualIp { get; set; } = "";       // 统一下发固定 .2（OQ-13）
    public byte[] StaticPubKey { get; set; } = [];    // 65B 未压缩 P-256
    public byte[] DeviceSecret { get; set; } = [];    // 32B HMAC 密钥
    public DateTime? LastSeenAt { get; set; }         // 在线判定在内存，超时落库
    public bool Disabled { get; set; }
    public DateTime CreatedAt { get; set; }

    public User? Owner { get; set; }
}

/// <summary>开放内网段白名单（FR-C-701，D15）。</summary>
public sealed class LanSegment
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public string Cidr { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    public Device Device { get; set; } = null!;
}

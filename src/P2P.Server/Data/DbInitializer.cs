using Microsoft.EntityFrameworkCore;
using P2P.Core.Crypto;

namespace P2P.Server.Data;

/// <summary>
/// 数据库初始化（03 §6：启动 Migrate 自动升级；§2 初始化种子）。
/// 顺序：先建 admin 用户，再建默认分组（owner=admin）。
/// </summary>
public static class DbInitializer
{
    /// <summary>内置管理员（FR-S-203，OQ-3：admin/admin，首登提示改密）。</summary>
    public const string AdminUsername = "admin";
    public const string DefaultGroupName = "默认分组";

    /// <summary>server_config 初始键值（03 §2.8，FR-S-825）。</summary>
    public static readonly (string Key, string Value)[] ConfigDefaults =
    [
        ("registration_open", "1"),
        ("relay_enabled", "1"),
        ("relay_rate_limit", "0"),
        ("stun_auth", "1"),
        ("virtual_subnet", "100.64.0.0/24"),
        ("audit_retention_days", "90"),
        ("log_level", "Information"),
        ("max_devices", "500"),
        ("punch_retention_days", "90"),
        ("stun_rate_per_ip", "50"),
        ("stun_rate_per_device", "10"),
        ("stun_circuit_pps", "2000"),
        ("default_join_policy", "free"), // FR-S-304：默认分组准入策略（free|approval）
        // 0x03 升级信息（M2-14，FR-S-804/OQ-5）：latest/min/url/notes 出配置，max=宿主编译协议版本
        ("update_latest_version", "0.1.0"),
        ("update_min_protocol", "1"),
        ("update_url", ""),
        ("update_notes", ""),
    ];

    public static void Initialize(AppDbContext db)
    {
        db.Database.Migrate();

        // WAL + busy_timeout（03 §6；:memory: 库 journal_mode 保持 memory，不视为错误）
        try
        {
            db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
            db.Database.ExecuteSqlRaw("PRAGMA busy_timeout=5000;");
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // 内存库/只读场景忽略
        }

        Seed(db);
    }

    /// <summary>幂等种子：admin / 默认分组 / server_config 缺键补齐。</summary>
    public static void Seed(AppDbContext db)
    {
        var now = DateTime.UtcNow;

        var admin = db.Users.SingleOrDefault(u => u.Username == AdminUsername && u.IsAdmin);
        if (admin is null)
        {
            admin = new User
            {
                Id = Guid.NewGuid(),
                Username = AdminUsername,
                PasswordHash = PasswordHasher.Hash(AdminUsername), // OQ-3：admin/admin
                IsAdmin = true,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Users.Add(admin);
            db.SaveChanges();
        }

        // 配置键先行补齐（幂等）：默认分组准入策略取 default_join_policy（FR-S-304）
        var existing = db.ServerConfig.Select(c => c.Key).ToHashSet();
        var missing = ConfigDefaults.Where(kv => !existing.Contains(kv.Key))
            .Select(kv => new ServerConfigEntry { Key = kv.Key, Value = kv.Value })
            .ToList();
        if (missing.Count > 0)
        {
            db.ServerConfig.AddRange(missing);
            db.SaveChanges();
        }

        if (!db.Groups.Any(g => g.IsDefault))
        {
            var policy = db.ServerConfig.AsNoTracking()
                .SingleOrDefault(c => c.Key == "default_join_policy")?.Value == "approval"
                ? "approval" : "free"; // 非法值回退 free
            db.Groups.Add(new Group
            {
                Id = Guid.NewGuid(),
                Name = DefaultGroupName,
                OwnerUserId = admin.Id,
                IsDefault = true,
                JoinPolicy = policy,
                CreatedAt = now,
            });
            db.SaveChanges();
        }
    }
}

/// <summary>server_config 类型化读取（缓存策略由宿主决定；M1 直读）。</summary>
public sealed class ServerConfigStore(AppDbContext db)
{
    public string Get(string key)
        => db.ServerConfig.AsNoTracking().SingleOrDefault(c => c.Key == key)?.Value
           ?? throw new InvalidOperationException($"server_config 缺键：{key}（初始化未执行？）");

    public bool GetBool(string key) => Get(key) == "1";
    public int GetInt(string key) => int.Parse(Get(key));
}

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace P2P.Server.Data;

/// <summary>
/// 服务端数据库（03 §2：SQLite，11 张蛇形命名表）。
/// 时间列 TEXT ISO8601 UTC；Guid 列 TEXT；布尔/整数 INTEGER。
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();
    public DbSet<JoinRequest> JoinRequests => Set<JoinRequest>();
    public DbSet<Mapping> Mappings => Set<Mapping>();
    public DbSet<MappingStat> MappingStats => Set<MappingStat>();
    public DbSet<LanSegment> LanSegments => Set<LanSegment>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ServerConfigEntry> ServerConfig => Set<ServerConfigEntry>();
    public DbSet<PunchStat> PunchStats => Set<PunchStat>();

    /// <summary>DateTime ⇄ TEXT(ISO8601 UTC 往返格式)，SQLite 默认格式无 'T' 不符 DDL 口径。</summary>
    public static readonly ValueConverter<DateTime, string> DateTimeText = new(
        v => v.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
        s => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    protected override void OnModelCreating(ModelBuilder b)
    {
        // ── users（§2.1）──────────────────────────────────────────────
        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Username).HasColumnName("username");
            e.HasIndex(x => x.Username).IsUnique();
            e.Property(x => x.PasswordHash).HasColumnName("password_hash");
            e.Property(x => x.IsAdmin).HasColumnName("is_admin");
            e.Property(x => x.Disabled).HasColumnName("disabled");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasConversion(DateTimeText);
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasConversion(DateTimeText);
        });

        // ── devices（§2.2）────────────────────────────────────────────
        b.Entity<Device>(e =>
        {
            e.ToTable("devices");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            e.HasIndex(x => x.OwnerUserId).HasDatabaseName("idx_devices_owner");
            e.Property(x => x.DeviceName).HasColumnName("device_name");
            e.Property(x => x.Os).HasColumnName("os");
            e.Property(x => x.ClientVersion).HasColumnName("client_version");
            e.Property(x => x.MacCode).HasColumnName("mac_code");
            e.HasIndex(x => x.MacCode).HasDatabaseName("idx_devices_mac");
            e.Property(x => x.RemoteCode).HasColumnName("remote_code");
            e.HasIndex(x => x.RemoteCode).IsUnique();
            e.Property(x => x.VirtualIp).HasColumnName("virtual_ip");
            e.Property(x => x.StaticPubKey).HasColumnName("static_pub_key");
            e.Property(x => x.DeviceSecret).HasColumnName("device_secret");
            e.Property(x => x.LastSeenAt).HasColumnName("last_seen_at").HasConversion(DateTimeText);
            e.Property(x => x.Disabled).HasColumnName("disabled");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasConversion(DateTimeText);
            // 显式关系：约定名 OwnerId 与 owner_user_id 不符，须指定 FK 列（否则生成影子列）
            e.HasOne(x => x.Owner).WithMany(u => u.OwnedDevices)
                .HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
        });

        // ── lan_segments（§2.5）───────────────────────────────────────
        b.Entity<LanSegment>(e =>
        {
            e.ToTable("lan_segments");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.DeviceId).HasColumnName("device_id");
            e.Property(x => x.Cidr).HasColumnName("cidr");
            e.HasIndex(x => new { x.DeviceId, x.Cidr }).IsUnique();
            e.Property(x => x.Enabled).HasColumnName("enabled");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasConversion(DateTimeText);
        });

        // ── groups / group_members / join_requests（§2.3）─────────────
        b.Entity<Group>(e =>
        {
            e.ToTable("groups");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            e.Property(x => x.IsDefault).HasColumnName("is_default");
            e.Property(x => x.JoinPolicy).HasColumnName("join_policy");
            e.Property(x => x.InviteCode).HasColumnName("invite_code");
            e.HasIndex(x => x.InviteCode).IsUnique();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasConversion(DateTimeText);
            e.HasOne(x => x.Owner).WithMany(u => u.OwnedGroups)
                .HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<GroupMember>(e =>
        {
            e.ToTable("group_members");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.GroupId).HasColumnName("group_id");
            e.Property(x => x.DeviceId).HasColumnName("device_id");
            e.HasIndex(x => new { x.GroupId, x.DeviceId }).IsUnique();
            e.HasIndex(x => x.DeviceId).HasDatabaseName("idx_gm_device");
            e.Property(x => x.Approved).HasColumnName("approved");
            e.Property(x => x.JoinedAt).HasColumnName("joined_at").HasConversion(DateTimeText);
        });

        b.Entity<JoinRequest>(e =>
        {
            e.ToTable("join_requests");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.GroupId).HasColumnName("group_id");
            e.Property(x => x.DeviceId).HasColumnName("device_id");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasConversion(DateTimeText);
            e.Property(x => x.HandledAt).HasColumnName("handled_at").HasConversion(DateTimeText);
        });

        // ── mappings / mapping_stats（§2.4/§2.6）──────────────────────
        b.Entity<Mapping>(e =>
        {
            e.ToTable("mappings");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OwnerDeviceId).HasColumnName("owner_device_id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.LocalPort).HasColumnName("local_port");
            e.Property(x => x.Proto).HasColumnName("proto");
            e.Property(x => x.TargetDeviceId).HasColumnName("target_device_id");
            e.HasIndex(x => x.TargetDeviceId).HasDatabaseName("idx_mappings_target");
            e.Property(x => x.TargetAddr).HasColumnName("target_addr");
            e.Property(x => x.TargetPort).HasColumnName("target_port");
            e.Property(x => x.Enabled).HasColumnName("enabled");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasConversion(DateTimeText);
            e.HasIndex(x => new { x.OwnerDeviceId, x.LocalPort, x.Proto }).IsUnique();
        });

        b.Entity<MappingStat>(e =>
        {
            e.ToTable("mapping_stats");
            e.Property(x => x.MappingId).HasColumnName("mapping_id");
            e.HasKey(x => x.MappingId);
            e.Property(x => x.BytesUp).HasColumnName("bytes_up");
            e.Property(x => x.BytesDown).HasColumnName("bytes_down");
            e.Property(x => x.RelayBytes).HasColumnName("relay_bytes");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasConversion(DateTimeText);
        });

        // ── audit_logs（§2.7）────────────────────────────────────────
        b.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_logs");
            e.Property(x => x.Id).HasColumnName("id").HasAnnotation("Sqlite:Autoincrement", true);
            e.Property(x => x.Ts).HasColumnName("ts").HasConversion(DateTimeText);
            e.HasIndex(x => x.Ts).HasDatabaseName("idx_audit_ts");
            e.Property(x => x.Event).HasColumnName("event");
            e.Property(x => x.DeviceId).HasColumnName("device_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.Detail).HasColumnName("detail");
        });

        // ── server_config（§2.8）─────────────────────────────────────
        b.Entity<ServerConfigEntry>(e =>
        {
            e.ToTable("server_config");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasColumnName("key");
            e.Property(x => x.Value).HasColumnName("value");
        });

        // ── punch_stats（§2.9）───────────────────────────────────────
        b.Entity<PunchStat>(e =>
        {
            e.ToTable("punch_stats");
            e.Property(x => x.Id).HasColumnName("id").HasAnnotation("Sqlite:Autoincrement", true);
            e.Property(x => x.Ts).HasColumnName("ts").HasConversion(DateTimeText);
            e.HasIndex(x => x.Ts).HasDatabaseName("idx_punch_ts");
            e.Property(x => x.SessionId).HasColumnName("session_id");
            e.Property(x => x.InitiatorId).HasColumnName("initiator_id");
            e.Property(x => x.TargetId).HasColumnName("target_id");
            e.Property(x => x.Proto).HasColumnName("proto");
            e.Property(x => x.Concurrency).HasColumnName("concurrency");
            e.Property(x => x.Result).HasColumnName("result");
            e.Property(x => x.Reason).HasColumnName("reason");
            e.Property(x => x.DurationMs).HasColumnName("duration_ms");
            // DDL 引用 devices(id)；无导航（统计不参与设备对象图）
            e.HasOne<Device>().WithMany().HasForeignKey(x => x.InitiatorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Device>().WithMany().HasForeignKey(x => x.TargetId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

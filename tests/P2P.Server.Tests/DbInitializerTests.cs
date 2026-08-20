using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using P2P.Core.Crypto;
using P2P.Server.Data;
using Xunit;

namespace P2P.Server.Tests;

public sealed class DbInitializerTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;

    public DbInitializerTests()
    {
        _connection.Open(); // 内存库须保持连接存活
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection).Options);
    }

    [Fact]
    public void Initialize_CreatesAllTables_SnakeCase()
    {
        DbInitializer.Initialize(_db);

        var tables = _db.Database
            .SqlQuery<string>($"SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '\\_\\_%' ESCAPE '\\' ORDER BY name")
            .ToHashSet();
        var expected = new HashSet<string>
        {
            "users", "devices", "groups", "group_members", "join_requests",
            "mappings", "mapping_stats", "lan_segments", "audit_logs", "server_config", "punch_stats",
        };
        Assert.True(expected.IsSubsetOf(tables), $"缺表：{string.Join(",", expected.Except(tables))}");
        Assert.Equal(expected.Count, tables.Count); // 恰好 11 张业务表（__EFMigrationsHistory 等基础设施已滤除）
    }

    [Fact]
    public void Initialize_SeedsAdmin_DefaultGroup_AndConfig()
    {
        DbInitializer.Initialize(_db);

        var admin = Assert.Single(_db.Users.Where(u => u.IsAdmin));
        Assert.Equal("admin", admin.Username);
        Assert.True(PasswordHasher.Verify("admin", admin.PasswordHash)); // OQ-3：admin/admin

        var def = Assert.Single(_db.Groups.Where(g => g.IsDefault));
        Assert.Equal(DbInitializer.DefaultGroupName, def.Name);
        Assert.Equal(admin.Id, def.OwnerUserId); // 初始化顺序：先 admin 再默认分组
        Assert.Equal("free", def.JoinPolicy);

        foreach (var (key, _) in DbInitializer.ConfigDefaults)
            Assert.True(_db.ServerConfig.Any(c => c.Key == key), $"缺配置键 {key}");
    }

    [Fact]
    public void Initialize_IsIdempotent()
    {
        DbInitializer.Initialize(_db);
        DbInitializer.Initialize(_db); // 再跑不重复种、不抛

        Assert.Single(_db.Users.Where(u => u.IsAdmin));
        Assert.Single(_db.Groups.Where(g => g.IsDefault));
        Assert.Equal(DbInitializer.ConfigDefaults.Length, _db.ServerConfig.Count());
    }

    [Fact]
    public void RemoteCode_UniqueConstraint_Enforced()
    {
        DbInitializer.Initialize(_db);
        var a = NewDevice("AA:AA", "111111");
        var b = NewDevice("BB:BB", "111111"); // 同远程码
        _db.Devices.Add(a);
        _db.SaveChanges();
        _db.Devices.Add(b);
        Assert.Throws<DbUpdateException>(() => _db.SaveChanges());
    }

    [Fact]
    public void Mapping_PortUnique_PerDeviceProto_Enforced()
    {
        DbInitializer.Initialize(_db);
        _db.Devices.Add(NewDevice("AA:AA", "111111"));
        _db.Devices.Add(NewDevice("BB:BB", "222222"));
        _db.SaveChanges();
        var owner = _db.Devices.First(d => d.MacCode == "AA:AA");
        var target = _db.Devices.First(d => d.MacCode == "BB:BB");

        _db.Mappings.Add(NewMapping(owner.Id, target.Id, 8080, "tcp"));
        _db.SaveChanges();
        _db.Mappings.Add(NewMapping(owner.Id, target.Id, 8080, "tcp")); // 重复
        Assert.Throws<DbUpdateException>(() => _db.SaveChanges());

        _db.Mappings.RemoveRange(_db.Mappings);
        _db.SaveChanges();
        _db.Mappings.Add(NewMapping(owner.Id, target.Id, 8080, "udp")); // 同端口不同协议：允许
        _db.SaveChanges();
    }

    private static Device NewDevice(string mac, string remote) => new()
    {
        Id = Guid.NewGuid(),
        DeviceName = "t",
        Os = "windows",
        ClientVersion = "0.1.0",
        MacCode = mac,
        RemoteCode = remote,
        VirtualIp = "100.64.0.2",
        StaticPubKey = new byte[65],
        DeviceSecret = new byte[32],
        CreatedAt = DateTime.UtcNow,
    };

    private static Mapping NewMapping(Guid owner, Guid target, int port, string proto) => new()
    {
        Id = Guid.NewGuid(),
        OwnerDeviceId = owner,
        TargetDeviceId = target,
        Name = "m",
        LocalPort = port,
        Proto = proto,
        TargetAddr = "self",
        TargetPort = 80,
        CreatedAt = DateTime.UtcNow,
    };

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}

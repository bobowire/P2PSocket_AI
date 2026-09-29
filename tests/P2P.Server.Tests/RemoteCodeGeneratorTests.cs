using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using P2P.Server.Data;
using P2P.Server.Services;
using Xunit;

namespace P2P.Server.Tests;

public sealed class RemoteCodeGeneratorTests : IDisposable
{
    private readonly TestDatabase _dbFile = new();
    private readonly AppDbContext _db;

    public RemoteCodeGeneratorTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_dbFile.DataSource).Options);
        DbInitializer.Initialize(_db);
    }

    [Fact]
    public async Task Generate_LengthAndCharset_Valid()
    {
        var gen = new RemoteCodeGenerator(_db);
        for (var i = 0; i < 50; i++)
        {
            var code = await gen.GenerateAsync();
            Assert.Equal(RemoteCodeGenerator.Length, code.Length);
            Assert.All(code, c => Assert.Contains(c, RemoteCodeGenerator.FullCharset));
        }
    }

    [Fact]
    public async Task Generate_DigitSpacePreferred()
    {
        // 纯数字优先序：空库下生成应始终落在纯数字空间（10⁶ 未耗尽）
        var gen = new RemoteCodeGenerator(_db);
        for (var i = 0; i < 100; i++)
        {
            var code = await gen.GenerateAsync();
            Assert.All(code, c => Assert.True(char.IsDigit(c)));
            _db.Devices.Add(NewDevice($"MAC{i:000}", code));
            await _db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Generate_CollisionRetry_UsesExistingCodesNotRepeated()
    {
        // 连续撞码重试：已分配码必须落库（生成器以库内查重为准），500 个码两两互异
        var gen = new RemoteCodeGenerator(_db);
        var existing = new HashSet<string>();
        for (var i = 0; i < 500; i++)
        {
            var code = await gen.GenerateAsync();
            Assert.DoesNotContain(code, existing); // 查重确保唯一
            existing.Add(code);
            _db.Devices.Add(NewDevice($"MAC{i:000}", code));
            await _db.SaveChangesAsync();
        }
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

    public void Dispose()
    {
        _db.Dispose();
        _dbFile.Dispose();
    }
}

using Microsoft.EntityFrameworkCore;
using P2P.Server.Data;

namespace P2P.Server.Services;

/// <summary>宿主数据库装配（08 §5.1 database.path → SQLite；目录自动创建）。</summary>
public static class ServerDatabase
{
    public static IDbContextFactory<AppDbContext> CreateFactory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={fullPath}")
            .Options;
        return new SimpleFactory(options);
    }

    private sealed class SimpleFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}

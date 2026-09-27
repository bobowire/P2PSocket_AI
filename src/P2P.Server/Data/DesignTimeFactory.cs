using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace P2P.Server.Data;

/// <summary>dotnet ef 迁移设计时工厂（连接串仅用于生成迁移，运行时由宿主注入）。</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var dir = Directory.CreateDirectory("data").FullName; // 相对服务端工作目录（08 §5.1）
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(dir, "p2p.db")}")
            .Options;
        return new AppDbContext(options);
    }
}

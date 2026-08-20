namespace P2P.Core.Tests;

/// <summary>测试替身：手动推进时间的 TimeProvider（Core 时间抽象的注入实现，AI-28 测试纪律）。</summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan span) => _now += span;

    public void Set(DateTimeOffset value) => _now = value;
}

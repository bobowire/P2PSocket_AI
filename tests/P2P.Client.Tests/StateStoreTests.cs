// M1-22 StateStore 单测（任务清单验收：原子替换中途 kill 不损文件、损坏文件自愈、机密落盘加密）。
// 使用临时目录 + 可注入保护器（字节反转替身），断言落盘形态与语义，不依赖平台。
using System.Text.Json;
using P2P.Client.Storage;
using Xunit;

namespace P2P.Client.Tests;

public sealed class StateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "p2p-tests-" + Guid.NewGuid().ToString("N"));

    private StateStore NewStore() => new(_dir, new ReverseProtector());

    [Fact]
    public async Task RoundTrip_PreservesRegisteredState()
    {
        var secret = new byte[] { 1, 2, 3, 4, 5 };
        var key = new byte[] { 9, 8, 7 };
        var store = NewStore();
        store.State.DeviceId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        store.State.DeviceSecret = secret;
        store.State.StaticPrivateKey = key;
        store.State.RemoteCode = "123456";
        store.State.VirtualIp = "100.64.0.2";

        await store.SaveAsync();

        var reloaded = NewStore();
        reloaded.Load();
        Assert.True(reloaded.State.IsRegistered);
        Assert.Equal(store.State.DeviceId, reloaded.State.DeviceId);
        Assert.Equal(secret, reloaded.State.DeviceSecret);
        Assert.Equal(key, reloaded.State.StaticPrivateKey);
        Assert.Equal("123456", reloaded.State.RemoteCode);
        Assert.Equal("100.64.0.2", reloaded.State.VirtualIp);
    }

    [Fact]
    public async Task Secrets_ProtectedAtRest_PlaintextAbsentFromFile()
    {
        var secret = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x42 };
        var store = NewStore();
        store.State.DeviceSecret = secret;

        await store.SaveAsync();

        var raw = File.ReadAllText(Path.Combine(_dir, ClientPaths.StateFileName));
        Assert.DoesNotContain(Convert.ToBase64String(secret), raw); // 明文不落盘（07 §4）
        Assert.Contains(Convert.ToBase64String(Reverse(secret)), raw); // 落盘为保护后形态
    }

    // ── 验收①：原子替换中途 kill 不损终文件 ────────────────────────────
    // 原子写策略=先写全量 .tmp（WriteThrough+落盘）后替换。模拟两类中断残留：
    // a) .tmp 写满但未替换；b) .tmp 半写（torn write）。两种情况终文件均保持旧全量。
    [Fact]
    public async Task InterruptedSave_TmpLeftBehind_FinalFileIntact()
    {
        var store = NewStore();
        store.State.RemoteCode = "765432";
        await store.SaveAsync();

        // a) 写满未替换的残留 tmp
        await File.WriteAllTextAsync(Path.Combine(_dir, ClientPaths.StateFileName + ".tmp"),
            JsonSerializer.Serialize(new { remoteCode = "GARBAGE" }));
        var reloaded = NewStore();
        reloaded.Load();
        Assert.Equal("765432", reloaded.State.RemoteCode);

        // b) 半写残留（截断的 JSON 片段）——终文件同样不受影响
        await File.WriteAllBytesAsync(Path.Combine(_dir, ClientPaths.StateFileName + ".tmp"),
            "{ \"deviceId\": \"01234567"u8.ToArray());
        reloaded = NewStore();
        reloaded.Load();
        Assert.Equal("765432", reloaded.State.RemoteCode);
        Assert.False(File.Exists(Path.Combine(_dir, ClientPaths.StateFileName + ".tmp")), "Load 应清理残留 tmp");
    }

    [Fact]
    public async Task Save_LeavesNoTmpBehind()
    {
        var store = NewStore();
        await store.SaveAsync();
        await store.SaveAsync();
        Assert.False(File.Exists(Path.Combine(_dir, ClientPaths.StateFileName + ".tmp")));
    }

    // ── 验收②：损坏自愈（重建默认+告警事件）──────────────────────────
    [Fact]
    public void CorruptedFile_SelfHealsToDefaults_RaisesRecovered()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, ClientPaths.StateFileName);
        File.WriteAllText(path, "{ this is not json");

        string? recovered = null;
        var store = NewStore();
        store.Recovered += r => recovered = r;

        store.Load();

        Assert.NotNull(recovered);
        Assert.Contains("OQ-14", recovered);
        Assert.False(store.State.IsRegistered);
        Assert.Null(store.State.DeviceId);
    }

    [Fact]
    public void MissingFile_LoadsUnregisteredDefaults_WithoutEvent()
    {
        string? recovered = null;
        var store = NewStore();
        store.Recovered += r => recovered = r;

        store.Load();

        Assert.False(store.State.IsRegistered);
        Assert.Null(recovered);
    }

    [Fact]
    public void CorruptedSecretBox_SelfHeals_NotThrows()
    {
        // 密文还原失败（如 DPAPI 换机/密文损坏）也走自愈：身份可凭 macCode 覆盖恢复（OQ-14）
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, ClientPaths.StateFileName),
            $$"""{ "deviceId": "{{Guid.NewGuid()}}", "deviceSecretBox": "!!!not-base64!!!" }""");

        var store = NewStore();
        var fired = false;
        store.Recovered += _ => fired = true;

        store.Load();

        Assert.True(fired);
        Assert.False(store.State.IsRegistered);
    }

    // ── 辅助 ─────────────────────────────────────────────────────────

    private static byte[] Reverse(byte[] bytes)
    {
        var result = (byte[])bytes.Clone();
        Array.Reverse(result);
        return result;
    }

    private sealed class ReverseProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plaintext) => Reverse(plaintext);
        public byte[] Unprotect(byte[] boxed) => Reverse(boxed);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}

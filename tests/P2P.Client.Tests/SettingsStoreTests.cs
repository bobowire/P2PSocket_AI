// M1-22 SettingsStore 单测（任务清单验收：非法值拒启；08 §5.2 字段集与默认值）。
using System.Text.Json;
using P2P.Client.Storage;
using Xunit;

namespace P2P.Client.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "p2p-tests-" + Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_dir, ClientPaths.SettingsFileName);

    private void WriteSettings(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, json);
    }

    [Fact]
    public void MissingFile_ReturnsDefaults_WizardPreludeState()
    {
        var store = new SettingsStore(_dir);
        store.Load();

        Assert.Empty(store.Settings.ServerAddrs); // 首启向导前合法（08 §5.2）
        Assert.Equal(7100, store.Settings.LocalWebPort);
        Assert.Equal(3, store.Settings.PunchConcurrency);
        Assert.Equal(20, store.Settings.KeepaliveSec);
        Assert.Equal(1, store.Settings.Reconnect.MinSec);
        Assert.Equal(30, store.Settings.Reconnect.MaxSec);
    }

    [Fact]
    public void ValidFile_LoadsAllFields()
    {
        WriteSettings("""
            {
              "serverAddrs": [ "p2p.example.com:7000", "203.0.113.10:7000", "[2001:db8::1]:7000" ],
              "localWebPort": 7200,
              "punchConcurrency": 5,
              "keepaliveSec": 25,
              "reconnect": { "minSec": 2, "maxSec": 60 }
            }
            """);

        var store = new SettingsStore(_dir);
        store.Load();

        Assert.Equal(3, store.Settings.ServerAddrs.Length); // 含方括号 IPv6（08 §5.2 主备多候选）
        Assert.Equal(7200, store.Settings.LocalWebPort);
        Assert.Equal(5, store.Settings.PunchConcurrency);
        Assert.Equal(25, store.Settings.KeepaliveSec);
        Assert.Equal(2, store.Settings.Reconnect.MinSec);
        Assert.Equal(60, store.Settings.Reconnect.MaxSec);
    }

    // ── 验收③：非法值拒启（NFR-35：字段名+合法范围+修复建议）─────────

    [Fact]
    public void CorruptedJson_RejectsStartupWithDiagnostics()
    {
        WriteSettings("{ not json");

        var store = new SettingsStore(_dir);
        var ex = Assert.Throws<ConfigValidationException>(() => store.Load());
        Assert.Contains("解析失败", ex.Message);
    }

    [Fact]
    public void IllegalValues_RejectStartup_ListingEveryField()
    {
        WriteSettings("""
            {
              "serverAddrs": [ "no-port-here", "host:0", "host:70000", "2001:db8::1:7000" ],
              "localWebPort": 0,
              "punchConcurrency": 6,
              "keepaliveSec": 0,
              "reconnect": { "minSec": 30, "maxSec": 1 }
            }
            """);

        var store = new SettingsStore(_dir);
        var ex = Assert.Throws<ConfigValidationException>(() => store.Load());

        Assert.Contains("serverAddrs[0]", ex.Message);
        Assert.Contains("serverAddrs[1]", ex.Message);
        Assert.Contains("serverAddrs[2]", ex.Message);
        Assert.Contains("serverAddrs[3]", ex.Message); // 裸 IPv6 须方括号
        Assert.Contains("localWebPort", ex.Message);
        Assert.Contains("punchConcurrency", ex.Message);
        Assert.Contains("1~5", ex.Message); // 合法范围必须出现（NFR-35 修复建议）
        Assert.Contains("keepaliveSec", ex.Message);
        Assert.Contains("reconnect.maxSec", ex.Message);
    }

    [Fact]
    public void PunchConcurrency_BelowRange_Rejected()
    {
        WriteSettings("""{ "serverAddrs": [], "punchConcurrency": 0 }""");
        var store = new SettingsStore(_dir);
        Assert.Throws<ConfigValidationException>(() => store.Load());
    }

    [Fact]
    public async Task Save_RejectsIllegalValues_FileUntouched()
    {
        var store = new SettingsStore(_dir);
        store.Load();
        var original = File.Exists(SettingsPath) ? null : "absent"; // 尚未保存过

        var bad = new ClientSettings { ServerAddrs = ["bad-addr"], PunchConcurrency = 9 };
        await Assert.ThrowsAsync<ConfigValidationException>(() => store.SaveAsync(bad));

        Assert.Equal(original ?? "absent", File.Exists(SettingsPath) ? "exists" : "absent"); // 非法值不落盘
    }

    [Fact]
    public async Task Save_RoundTrip_AndOverwritesPrevious()
    {
        var store = new SettingsStore(_dir);
        store.Load();
        var first = new ClientSettings { ServerAddrs = ["a.example.com:7000"] };
        await store.SaveAsync(first);
        var second = new ClientSettings { ServerAddrs = ["b.example.com:7000"], LocalWebPort = 7300 };
        await store.SaveAsync(second);

        var reloaded = new SettingsStore(_dir);
        reloaded.Load();
        Assert.Equal(["b.example.com:7000"], reloaded.Settings.ServerAddrs);
        Assert.Equal(7300, reloaded.Settings.LocalWebPort);
    }

    [Fact]
    public void Load_CleansStaleTmp()
    {
        WriteSettings("""{ "serverAddrs": [] }""");
        File.WriteAllText(SettingsPath + ".tmp", "garbage");

        var store = new SettingsStore(_dir);
        store.Load();

        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}

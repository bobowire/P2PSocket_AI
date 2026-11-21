// M8-01 无卡模式生效点①单测（设计 13 §4.1/§10.1）：LoopbackMappingMode 对引擎构造初值的影响
// 经 /api/device 展示面观察（归真缝=引擎 EffectiveBindIp）——true=127.0.0.1、false=null（现状回归，
// 未注册态 state.VirtualIp 为空走 state 读数）。生效点②（注册后 UpdateVirtualIp 传 Loopback）与
// 指纹注入流需真实服务端，归 P2P.IntegrationTests（M8LoopbackIntegrationTests）。
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using P2P.Client.Hosting;
using P2P.Client.Storage;
using P2P.Nic;
using Xunit;

namespace P2P.Client.Tests.Hosting;

[Collection("loopback-mode")] // 全运行时组（与重活测试错峰：起真实 ClientRuntime + 本地 Web）
public sealed class LoopbackMappingModeTests : IAsyncDisposable
{
    private readonly string _rootDir = Path.Combine(Path.GetTempPath(), $"p2p-ut-loopback-{Guid.NewGuid():N}");
    private readonly List<ClientRuntime> _runtimes = [];

    public LoopbackMappingModeTests() => Directory.CreateDirectory(_rootDir);

    public async ValueTask DisposeAsync()
    {
        foreach (var r in _runtimes) await r.DisposeAsync();
        try { Directory.Delete(_rootDir, true); } catch (IOException) { }
    }

    private static async Task<string?> GetDeviceVirtualIpAsync(ClientRuntime runtime)
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{runtime.WebPort}") };
        using var response = await http.GetAsync("/api/device");
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(0, root.GetProperty("code").GetInt32());
        if (!root.GetProperty("data").TryGetProperty("virtualIp", out var vip)) return null;
        return vip.ValueKind == JsonValueKind.Null ? null : vip.GetString();
    }

    // 占位地址=不可路由 TEST-NET-1（RFC 5737）：connect 慢失败（SYN 重传），单次连接即可——
    // 127.0.0.1:1 即拒即连会让 ControlClient 重试环高频搅动临时端口段，与 PuncherTcp 恒等 NAT 缝
    // 的「探测即关+重绑同口」竞态叠加（闪败家族：负载越重临时口重绑成功率越低，TcpPunchTests 案）
    private static readonly string[] BlackholeAddr = ["192.0.2.1:1"];

    [Fact]
    public async Task 无卡模式true_引擎初值Loopback_展示归真127()
    {
        // 向导模式（未注册）即可观察生效点①：引擎构造初值=Loopback，归真缝透出 127.0.0.1
        var dir = Path.Combine(_rootDir, "on");
        var settings = new SettingsStore(dir);
        await settings.SaveAsync(new ClientSettings { ServerAddrs = BlackholeAddr, LocalWebPort = FreePort() });

        var runtime = new ClientRuntime(new ClientRuntimeOptions
        {
            BaseDir = dir,
            NicOverride = new NoopNicManager(), // 无卡模式产品级网卡（Android 同款）
            LoopbackMappingMode = true,
        });
        _runtimes.Add(runtime);
        await runtime.StartAsync(); // 未注册分支即返回（向导模式）

        Assert.Equal("127.0.0.1", await GetDeviceVirtualIpAsync(runtime));
    }

    [Fact]
    public async Task 无卡模式false_未注册展示state空值_现状回归()
    {
        var dir = Path.Combine(_rootDir, "off");
        var settings = new SettingsStore(dir);
        await settings.SaveAsync(new ClientSettings { ServerAddrs = BlackholeAddr, LocalWebPort = FreePort() });

        var runtime = new ClientRuntime(new ClientRuntimeOptions
        {
            BaseDir = dir,
            NicOverride = new NoopNicManager(),
            LoopbackMappingMode = false, // 桌面默认：归真缝不装配，API 读 state.VirtualIp
        });
        _runtimes.Add(runtime);
        await runtime.StartAsync();

        Assert.Null(await GetDeviceVirtualIpAsync(runtime)); // 未注册=state 值（现状零变化）
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}

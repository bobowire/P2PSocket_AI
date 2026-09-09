// M1-30 宿主服务适配（01 §3.2）：把 ClientRuntime 生命周期挂到 Generic Host。
// 启动异常（NFR-35 配置拒启等）上抛 → Host 停机 → Program 记日志退出码 1。
using Microsoft.Extensions.Hosting;
using P2P.Client.Storage;
using Serilog;

namespace P2P.Client.Hosting;

/// <summary>ClientRuntime 的 IHostedService 壳（服务/控制台两模式共用）。</summary>
public sealed class ClientHostService(ClientRuntimeOptions options) : IHostedService, IAsyncDisposable
{
    private ClientRuntime? _runtime;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var runtime = new ClientRuntime(options);
        runtime.Log += m => Log.Information("{Msg}", m);
        try
        {
            await runtime.StartAsync(cancellationToken);
            _runtime = runtime;
        }
        catch (ConfigValidationException e)
        {
            Log.Fatal("配置校验失败，拒绝启动（NFR-35）：{Msg}", e.Message);
            foreach (var error in e.Errors) Log.Fatal("  - {Error}", error);
            await runtime.DisposeAsync();
            throw;
        }
        catch (Exception e)
        {
            Log.Fatal(e, "客户端运行时启动失败");
            await runtime.DisposeAsync();
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _runtime, null) is { } runtime)
            await runtime.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _runtime, null) is { } runtime)
            await runtime.DisposeAsync();
    }
}

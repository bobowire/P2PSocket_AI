// M2-24 网卡自愈循环（FR-C-202、05 §1.2「后台 30s 检测适配器存在性与 IP 绑定，丢失则重建」、
// M1 附录 A.4 遗留观察收口）：周期 CheckHealth 只读探测 → 异常（被删除/IP 被改动）时
// RemoveAsync + EnsureAsync 重建适配器并恢复配置；失败仅告警下轮重试（不断服务）。
// 恢复沿（异常→健康）触发 Restored：宿主据此立即驱动映射 listen_failed 重试（重建后监听
// 绑定即可成功，不再需要手工 retry——Wintun 进程切换窗口 IP 未生效的规律复现即此路径）。
using System.Net;
using P2P.Nic;

namespace P2P.Client.Nic;

/// <summary>网卡健康自愈监视器：单虚拟 IP、周期探测、异常重建。</summary>
public sealed class NicHealthMonitor : IAsyncDisposable
{
    /// <summary>探测周期（05 §1.2 定值 30s；测试注入缩短）。</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

    private readonly INicManager _nic;
    private readonly IPAddress _virtualIp;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private int _unhealthy;   // 最近一轮探测是否异常（恢复沿判定）
    private int _healCount;
    private int _disposed;

    /// <summary>诊断日志（宿主接 Serilog，前缀 [nic-heal]）。</summary>
    public event Action<string>? Log;

    /// <summary>恢复沿事件：此前探测到异常、现回到健康（重建成功或外部自愈）。
    /// 宿主在此立即触发映射监听重试（M2-24 联动；引擎侧另有周期兜底）。</summary>
    public event Action? Restored;

    /// <summary>自愈重建执行计数（诊断/测试断言）。</summary>
    public int HealCount => Volatile.Read(ref _healCount);

    public NicHealthMonitor(INicManager nic, IPAddress virtualIp, TimeSpan? interval = null)
    {
        _nic = nic;
        _virtualIp = virtualIp;
        _interval = interval ?? DefaultInterval;
        _loop = LoopAsync(_cts.Token);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    var health = _nic.CheckHealth(_virtualIp);
                    if (health.State == NicHealthState.Healthy)
                    {
                        // 异常后自愈恢复（外部修复，如管理员手工重建）：同样触发映射重试沿
                        if (Interlocked.Exchange(ref _unhealthy, 0) == 1)
                        {
                            Log?.Invoke($"[nic-heal] 网卡恢复健康（外部修复，绑定 {health.BoundIp}）");
                            Restored?.Invoke();
                        }
                        continue;
                    }

                    Volatile.Write(ref _unhealthy, 1);
                    Log?.Invoke($"[nic-heal] 检测异常：{Describe(health)}，重建适配器并恢复 {_virtualIp}");
                    await _nic.RemoveAsync(ct);
                    await _nic.EnsureAsync(_virtualIp, ct);
                    Interlocked.Increment(ref _healCount);

                    var recheck = _nic.CheckHealth(_virtualIp);
                    if (recheck.State == NicHealthState.Healthy)
                    {
                        Interlocked.Exchange(ref _unhealthy, 0);
                        Log?.Invoke($"[nic-heal] 重建完成（绑定 {recheck.BoundIp}），网卡已恢复");
                        Restored?.Invoke();
                    }
                    else
                        Log?.Invoke($"[nic-heal] 重建后复检仍异常（{Describe(recheck)}），下轮重试");
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Volatile.Write(ref _unhealthy, 1);
                    Log?.Invoke($"[nic-heal] 自愈失败：{e.Message}（下轮重试）");
                }
            }
        }
        catch (OperationCanceledException) { /* 停机 */ }
    }

    private static string Describe(NicHealth health) => health.State switch
    {
        NicHealthState.AdapterMissing => "适配器不存在（被删除）",
        NicHealthState.IpMismatch => $"IP 绑定异常（期望外地址 {(health.BoundIp?.ToString() ?? "无绑定")}）",
        _ => health.State.ToString(),
    };

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try { await _cts.CancelAsync(); } catch { /* 已取消 */ }
        try { await _loop; } catch { /* 取消即退出 */ }
        _cts.Dispose();
    }
}

namespace P2P.Server.Services;

/// <summary>
/// 中继全局字节速率令牌桶（TD-23、05 §6，FR-S-703，M3-07 做实）：速率 = server_config
/// relay_rate_limit（bytes/s，0=不限），容量 = 速率（1s 突发）。**债务模型**——帧通过条件为
/// 桶非负，通过后扣减可入负（欠账）：欠账期间转发路径按还清时长异步等待（**存量会话仅降速
/// 不中断**），且 <see cref="HasBudget"/> 为假 → 新 0x74 分配拒绝 5002（**保护存量**）；
/// SignalingCoordinator relayAllowed 合成接入同一余量（M2-07"余量 M3 前恒真"收口）。
/// 独立类（沿 StunGuard 先例）：RelayService（转发消耗+分配闸）与 SignalingCoordinator
/// （invite 合成）共用，规避构造环（RelayService 依赖信令台账解析）。TimeProvider 计时——
/// 测试用 FakeTimeProvider 推进回填；PUT /api/relay/config 进程内直调 UpdateRate 即时生效。
/// </summary>
public sealed class RelayRateLimiter(long rateBytesPerSec = 0, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private long _rate = rateBytesPerSec; // bytes/s；0=不限（读写均持锁）
    private double _tokens;               // 债务模型：可负（欠账=存量降速等待期）
    private long _lastTicks;              // 上次回填时刻（0=未初始化，首轮回填自然满桶）

    public long RateBytesPerSec { get { lock (_gate) return _rate; } }

    /// <summary>速率变更（PUT /api/relay/config 进程内直调即时生效）：新速率下钳桶到 [-rate, rate]。</summary>
    public void UpdateRate(long bytesPerSec)
    {
        lock (_gate)
        {
            RefillNoLock();
            _rate = bytesPerSec;
            if (bytesPerSec > 0)
                _tokens = Math.Clamp(_tokens, -bytesPerSec, bytesPerSec);
        }
    }

    /// <summary>余量判定（新 0x74 分配闸与 relayAllowed 合成共用）：不限恒真；桶非负 = 未耗尽。</summary>
    public bool HasBudget()
    {
        lock (_gate)
        {
            if (_rate == 0) return true;
            RefillNoLock();
            return _tokens >= 0;
        }
    }

    /// <summary>转发路径取令牌（bytes = 剥离 8B 后载荷）：不限直通；桶非负即过并扣减（可入负 = 欠账）；
    /// 欠账则按还清时长异步等待——全局桶共享，等待即降速；ct 取中继停机令（等待中停机即取消收场）。</summary>
    public async ValueTask AcquireAsync(int bytes, CancellationToken ct)
    {
        while (true)
        {
            double waitForSec;
            lock (_gate)
            {
                if (_rate == 0) return;
                RefillNoLock();
                if (_tokens >= 0)
                {
                    _tokens -= bytes;
                    return;
                }
                waitForSec = -_tokens / _rate;
            }
            await Task.Delay(TimeSpan.FromSeconds(waitForSec), _time, ct);
        }
    }

    /// <summary>按流逝时长回填并封顶速率容量（须持锁；rate=0 仅校时）。</summary>
    private void RefillNoLock()
    {
        var now = _time.GetLocalNow().UtcTicks;
        if (_rate > 0)
            _tokens = Math.Min(_rate, _tokens + (now - _lastTicks) * (double)_rate / TimeSpan.TicksPerSecond);
        _lastTicks = now;
    }
}

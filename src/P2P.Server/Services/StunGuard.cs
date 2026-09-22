using System.Net;
using System.Runtime.InteropServices;

namespace P2P.Server.Services;

/// <summary>
/// STUN 风暴防护四道闸配置（TD-18、05 §7.1）。三速率键来自 server_config（03 §2.8，
/// M1-11 已初始化、Program 装配处接读取）；突发容量与 TCP 并发数为固定值非配置键。
/// </summary>
public sealed class StunGuardOptions
{
    /// <summary>stun_rate_per_ip：闸① 单 IP UDP 令牌桶速率（pps）。</summary>
    public int PerIpPps { get; init; } = 50;

    /// <summary>闸① 单 IP UDP 突发容量（=令牌桶容量）。</summary>
    public int PerIpBurst { get; init; } = 100;

    /// <summary>闸① 每 IP TCP 并发连接上限（连接级限速天然抗突发，TCP 不另设令牌桶）。</summary>
    public int TcpPerIpConnections { get; init; } = 4;

    /// <summary>stun_rate_per_device：闸③ 每设备 QPS（认证通过后，令牌桶容量=速率=QPS）。</summary>
    public int PerDeviceQps { get; init; } = 10;

    /// <summary>stun_circuit_pps：闸④ 全局熔断阈值（UDP 数据报 + TCP 连接到达聚合，秒窗计数）。</summary>
    public int CircuitPps { get; init; } = 2000;

    /// <summary>闸④ 熔断时长（熔断期客户端由 PunchScheduler 失败路径走中继/重试兜底）。</summary>
    public TimeSpan CircuitBreak { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>丢弃计数快照（stun_dropped_total{reason=rate|auth|circuit}，04 §3.2 仪表盘消费）。</summary>
public sealed record StunDropStats(long Rate, long Auth, long Circuit);

/// <summary>
/// STUN 风暴防护四道闸（TD-18、05 §7.1）——UDP 与 TCP 两路共用同一实例，闸序越早越便宜：
/// ④ 全局熔断（到达聚合，先于一切处理）→ ① 单 IP（UDP 令牌桶 / TCP 并发连接）→
/// ② 查表先于验签（在 <see cref="StunCodec.TryParseDeviceAuth"/> 的 secret 回调内，
/// 未注册 deviceId 不耗 HMAC）→ ③ 每设备 QPS（认证通过后）。
/// 闸②与认证由 StunService 持有（须查库），本类承载 ①③④ 与全部丢弃计数。
/// 令牌桶以注入的 <see cref="TimeProvider"/> 计时——测试用 FakeTimeProvider 推进补币。
/// </summary>
public sealed class StunGuard(StunGuardOptions? options = null, TimeProvider? time = null)
{
    /// <summary>单 IP 桶容量上限：伪造源洪泛的字典兜底（超限清 60s 空闲桶后仍满则拒新源——
    /// 彼时全局速率早已触发熔断接管，正常部署远达不到）。</summary>
    private const int IpBucketCap = 8192;

    private readonly StunGuardOptions _options = options ?? new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<IPAddress, Bucket> _ipBuckets = []; // 闸① UDP
    private readonly Dictionary<IPAddress, int> _tcpOpen = [];      // 闸① TCP 在途计数
    private readonly Dictionary<Guid, Bucket> _deviceBuckets = []; // 闸③（认证后键空间有界）
    private long _windowSec;          // 闸④ 当前秒窗（UtcTicks/秒）
    private int _windowCount;
    private long _circuitUntilTicks;
    private long _droppedRate;
    private long _droppedAuth;
    private long _droppedCircuit;

    /// <summary>闸④：全局到达聚合（每条 UDP 数据报/TCP 连接各计一次，先于其他闸）。
    /// 熔断开则拒；当前秒窗计数超阈值即触发熔断并拒本条。</summary>
    public bool AdmitArrival()
    {
        lock (_gate)
        {
            var now = _time.GetLocalNow().UtcTicks;
            if (now < _circuitUntilTicks) { _droppedCircuit++; return false; }
            var sec = now / TimeSpan.TicksPerSecond;
            if (sec != _windowSec) { _windowSec = sec; _windowCount = 0; }
            _windowCount++;
            if (_windowCount <= _options.CircuitPps) return true;
            _circuitUntilTicks = now + _options.CircuitBreak.Ticks;
            _windowCount = 0; // 复位：恢复后自新窗重评
            _droppedCircuit++;
            return false;
        }
    }

    /// <summary>闸① UDP：单 IP 令牌桶（容量=突发、速率=PerIpPps；初始满桶）。</summary>
    public bool TryAcquireUdp(IPAddress ip)
    {
        lock (_gate)
        {
            if (_ipBuckets.Count >= IpBucketCap && !_ipBuckets.ContainsKey(ip))
            {
                var staleBefore = _time.GetLocalNow().UtcTicks - TimeSpan.FromSeconds(60).Ticks;
                foreach (var stale in _ipBuckets.Where(kv => kv.Value.LastTicks < staleBefore)
                               .Select(kv => kv.Key).ToList())
                    _ipBuckets.Remove(stale);
                if (_ipBuckets.Count >= IpBucketCap) { _droppedRate++; return false; }
            }
            return TakeNoLock(_ipBuckets, ip, _options.PerIpBurst, _options.PerIpPps);
        }
    }

    /// <summary>闸① TCP：每 IP 并发连接上限（与 <see cref="ReleaseTcp"/> 须成对调用）。</summary>
    public bool TryAcquireTcp(IPAddress ip)
    {
        lock (_gate)
        {
            _tcpOpen.TryGetValue(ip, out var open);
            if (open >= _options.TcpPerIpConnections) { _droppedRate++; return false; }
            _tcpOpen[ip] = open + 1;
            return true;
        }
    }

    /// <summary>闸① TCP 释放（连接终结，finally 路径；重复释放安全）。</summary>
    public void ReleaseTcp(IPAddress ip)
    {
        lock (_gate)
        {
            if (_tcpOpen.TryGetValue(ip, out var open) && open > 1) _tcpOpen[ip] = open - 1;
            else _tcpOpen.Remove(ip); // 归零即移除，键空间随活跃 IP 收缩
        }
    }

    /// <summary>闸③：每设备 QPS 令牌桶（容量=QPS、速率=QPS/s；正常一次打洞仅 2~4 次 Binding）。</summary>
    public bool TryAcquireDevice(Guid deviceId)
    {
        lock (_gate)
            return TakeNoLock(_deviceBuckets, deviceId, _options.PerDeviceQps, _options.PerDeviceQps);
    }

    /// <summary>认证失败计数（reason=auth：未注册/HMAC/ts 窗/nonce 重放）。</summary>
    public void CountAuth() { lock (_gate) _droppedAuth++; }

    /// <summary>当前丢弃计数快照（测试断言与仪表盘 04 §3.2）。</summary>
    public StunDropStats Snapshot() { lock (_gate) return new(_droppedRate, _droppedAuth, _droppedCircuit); }

    /// <summary>取一枚令牌（须持锁）：按距上次消耗的时长补币、封顶容量；不足即拒并计 rate。</summary>
    private bool TakeNoLock<TKey>(Dictionary<TKey, Bucket> dict, TKey key, int capacity, int ratePerSec)
        where TKey : notnull
    {
        var now = _time.GetLocalNow().UtcTicks;
        ref var bucket = ref CollectionsMarshal.GetValueRefOrAddDefault(dict, key, out _);
        bucket ??= new Bucket(capacity, now); // 初始满桶（突发容量立即可用）
        bucket.Tokens = Math.Min(capacity,
            bucket.Tokens + (now - bucket.LastTicks) * ratePerSec / (double)TimeSpan.TicksPerSecond);
        bucket.LastTicks = now;
        if (bucket.Tokens < 1) { _droppedRate++; return false; }
        bucket.Tokens -= 1;
        return true;
    }

    private sealed class Bucket(double tokens, long lastTicks)
    {
        public double Tokens = tokens;
        public long LastTicks = lastTicks;
    }
}

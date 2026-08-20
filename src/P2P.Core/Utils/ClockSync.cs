namespace P2P.Core.Utils;

/// <summary>
/// 时钟对齐（02 §2.3 OQ-12）：客户端维护 offset，此后全部 timestampMs 以 本地时间+offset 生成。
/// 公式：offset = serverTs + RTT/2 − t(收到响应)；RTT = t(收) − t(发)。
/// 传输耗时按 RTT 半程补偿，误差 ≤ 服务端处理时延/2。
/// HeartbeatAck 的 serverTs 周期性重算同一公式（跟踪本机时钟漂移）。
/// </summary>
public sealed class ClockSync
{
    private long _offsetMs;
    private bool _calibrated;

    /// <summary>是否已对齐（未对齐前 Hello 不校 ts）。</summary>
    public bool Calibrated => Volatile.Read(ref _calibrated);

    /// <summary>当前偏移估计（远程时间 − 本地时间，毫秒）。未对齐为 0。</summary>
    public long OffsetMs => Volatile.Read(ref _offsetMs);

    /// <summary>握手/心跳校准：serverTs=对端响应时间戳，sent/recv=本地发出与收到时刻。</summary>
    public void Calibrate(ulong serverTsMs, long sentAtLocalMs, long receivedAtLocalMs)
    {
        var rtt = receivedAtLocalMs - sentAtLocalMs;
        if (rtt < 0) rtt = 0; // 时钟回拨保护：RTT 取非负
        var offset = (long)serverTsMs + rtt / 2 - receivedAtLocalMs;
        Volatile.Write(ref _offsetMs, offset);
        Volatile.Write(ref _calibrated, true);
    }

    /// <summary>本地毫秒 → 校准后时间戳（供消息 timestampMs 与 STUN DEVICE-AUTH 共用）。</summary>
    public ulong ToRemoteMs(long localMs) => (ulong)(localMs + _offsetMs);

    /// <summary>当前时刻的校准时间戳。</summary>
    public ulong NowRemoteMs(TimeProvider time)
        => ToRemoteMs(time.GetLocalNow().ToUnixTimeMilliseconds());
}

namespace P2P.Core.Tunnel;

/// <summary>
/// counter 滑动窗口防重放（02 §4.2/SEC-12）：窗口 64，收到重复/回退计数即丢弃（调用方告警）。
/// 单会话单接收循环调用，无需加锁；counter 从 1 起（0 保留给握手帧）。
/// </summary>
public sealed class ReplayWindow
{
    public const int WindowSize = 64;

    private ulong _highest; // 已接受的最大 counter（0=尚未接受任何帧）
    private ulong _bitmap;  // bit(63)…bit(0) 对应 _highest…_highest-63

    /// <summary>接受新帧返回 true；重复/回退/超出窗口返回 false（丢弃）。</summary>
    public bool Accept(ulong counter)
    {
        if (counter == 0) return false;

        if (counter > _highest)
        {
            var shift = counter - _highest;
            _bitmap = shift >= WindowSize ? 1UL : (_bitmap << (int)shift) | 1UL;
            _highest = counter;
            return true;
        }

        var diff = _highest - counter;
        if (diff >= WindowSize) return false;               // 回退出窗（旧帧迟达）
        var bit = 1UL << (int)diff;
        if ((_bitmap & bit) != 0) return false;             // 窗口内重复
        _bitmap |= bit;
        return true;
    }
}

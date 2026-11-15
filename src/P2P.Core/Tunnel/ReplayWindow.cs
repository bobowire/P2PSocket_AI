namespace P2P.Core.Tunnel;

/// <summary>
/// counter 滑动窗口防重放（02 §4.2/SEC-12；M6 决议② 64→4096，设计 11 §4.4/§6）：
/// 跨 conv 乱序深度上界 ≈ 每 channel 在途 64KiB 信用 ≈ 48 帧 × channel 数 + conv 0 控制帧
/// ——窗口 64 必然不够；4096 按 ConvCap=256 满载仍容 8.5 倍余量。窗口只放宽"旧帧迟达"容忍度，
/// 同 counter 重放拦截与窗口大小无关（bitmap 命中即拒）——安全面零变化（设计 11 §9.5）。
/// 单会话单接收循环调用，无需加锁；counter 从 1 起（0 保留给握手帧）。
/// </summary>
public sealed class ReplayWindow
{
    public const int WindowSize = 4096;
    private const int WordBits = 64;
    private const int Words = WindowSize / WordBits; // 64 字 × 64bit = 512B 位图

    private readonly ulong[] _bitmap = new ulong[Words];
    private ulong _highest; // 已接受的最大 counter（0=尚未接受任何帧）；bit 位=diff（_bitmap[diff/64] 的 1<<(diff%64)）

    /// <summary>接受新帧返回 true；重复/回退/超出窗口返回 false（丢弃）。</summary>
    public bool Accept(ulong counter)
    {
        if (counter == 0) return false;

        if (counter > _highest)
        {
            var shift = counter - _highest;
            if (shift >= WindowSize)
            {
                Array.Clear(_bitmap);
                _bitmap[0] = 1UL;
            }
            else
            {
                ShiftLeft(shift);
                _bitmap[0] |= 1UL;
            }
            _highest = counter;
            return true;
        }

        var diff = _highest - counter;
        if (diff >= WindowSize) return false;               // 回退出窗（旧帧迟达）
        var bit = 1UL << (int)(diff % WordBits);
        var word = (int)(diff / WordBits);
        if ((_bitmap[word] & bit) != 0) return false;       // 窗口内重复
        _bitmap[word] |= bit;
        return true;
    }

    /// <summary>位图整体左移 shift 位（diff 全体 +shift：旧 counter 位右移出窗自然消亡）。</summary>
    private void ShiftLeft(ulong shift)
    {
        var wordShift = (int)(shift / WordBits);
        var bitShift = (int)(shift % WordBits);
        // 自高字向低字原地搬移：源字下标恒低于目标字，降序遍历不破坏未读源
        for (var i = Words - 1; i >= 0; i--)
        {
            var src = i - wordShift;
            if (src < 0)
            {
                _bitmap[i] = 0UL;
                continue;
            }
            var value = _bitmap[src] << bitShift;
            if (bitShift > 0 && src - 1 >= 0)
                value |= _bitmap[src - 1] >> (WordBits - bitShift); // 低字高位补入
            _bitmap[i] = value;
        }
    }
}

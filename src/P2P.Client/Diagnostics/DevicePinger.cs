// M3-16 ping-device 诊断（04 §2.6、FR-C-808、02 §4.2 0x06/0x07）：
// 远程码 → 设备列表（0x40 可见列表，同映射解析口径）→ 设备对活隧道上 PTP PING×4 测 RTT。
// 错误通道：空码 1001 / 远程码不可见或已重置 4003（REMOTE_CODE_INVALID，与 0x60 映射创建同判据）/
// 无活隧道 1002（NOT_FOUND——隧道是设备对级资源（02 §4.5），须先启用一条到该设备的映射触发建立）。
// 单次丢失（外层 2s 预算超时/隧道 IOException）不整体失败——统计收到的样本数 Received，全丢时
// Min/Avg/Max=null 如实呈现；外层 ct 取消则正常上抛。
// 委托注入（列表/隧道 lookup）：DevicePinger 纯单测不依赖 TunnelSession/ControlClient。
using System.IO;
using System.Threading;
using P2P.Core.Protocol;

namespace P2P.Client.Diagnostics;

/// <summary>设备对隧道抽象（测试缝）：ViaRelay 承载标记 + 单次 PING（返回 RTT）。</summary>
public sealed record DeviceTunnel(bool ViaRelay, Func<CancellationToken, Task<TimeSpan>> Ping);

/// <summary>ping-device 结果视图（04 §2.6）。</summary>
public sealed record PingDeviceView(
    string TargetDevice,
    string TargetRemoteCode,
    int Sent,
    int Received,
    long? MinMs,
    double? AvgMs,
    long? MaxMs,
    bool ViaRelay,
    long DurationMs);

/// <summary>ping-device 编排器：4 次顺序 PING（单飞行语义复用 TunnelSession._pingTcs——顺序调用天然错峰）。</summary>
public sealed class DevicePinger(
    Func<CancellationToken, Task<IReadOnlyList<DeviceListItem>>> deviceListLookup,
    Func<Guid, DeviceTunnel?> tunnelLookup,
    TimeProvider? timeProvider = null)
{
    public const int PingCount = 4;

    /// <summary>单次 PING 预算（TunnelSession 自身 PONG 超时=KeepaliveInterval×3≈60s，外层收紧到
    /// 2s 使单次丢失快速计入而非长时间挂起；直连回环 RTT 毫秒级，2s 足量）。</summary>
    private static readonly TimeSpan PerPingTimeout = TimeSpan.FromSeconds(2);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<PingDeviceView> RunAsync(string? remoteCode, CancellationToken ct = default)
    {
        var started = _time.GetTimestamp();
        var code = (remoteCode ?? "").Trim();
        if (code.Length == 0)
            throw new DiagnosticsException(ErrorCode.BadRequest, "远程码不能为空");

        var devices = await deviceListLookup(ct);
        var target = devices.FirstOrDefault(d => string.Equals(d.RemoteCode, code, StringComparison.OrdinalIgnoreCase))
            ?? throw new DiagnosticsException(ErrorCode.RemoteCodeInvalid, "远程码不存在或已被重置（不在可见列表）");
        var tunnel = tunnelLookup(target.DeviceId)
            ?? throw new DiagnosticsException(ErrorCode.NotFound,
                $"设备「{target.DeviceName}」无活动隧道：须先启用一条到该设备的映射");

        var rtts = new List<long>(PingCount);
        for (var i = 0; i < PingCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var perPing = CancellationTokenSource.CreateLinkedTokenSource(ct);
            perPing.CancelAfter(PerPingTimeout);
            try
            {
                var rtt = await tunnel.Ping(perPing.Token);
                rtts.Add((long)Math.Round(rtt.TotalMilliseconds, MidpointRounding.AwayFromZero));
            }
            catch (Exception e) when (e is IOException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                // 该次丢失：PONG 未归（超时）或隧道瞬时故障——计入 Received 缺席，不中断诊断
            }
        }

        return new PingDeviceView(
            target.DeviceName, target.RemoteCode, PingCount, rtts.Count,
            rtts.Count > 0 ? rtts.Min() : null,
            rtts.Count > 0 ? Math.Round(rtts.Average(), 1) : null,
            rtts.Count > 0 ? rtts.Max() : null,
            tunnel.ViaRelay,
            (long)_time.GetElapsedTime(started).TotalMilliseconds);
    }
}

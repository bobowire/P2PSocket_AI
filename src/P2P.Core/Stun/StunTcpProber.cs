using System.Net;
using System.Net.Sockets;
using P2P.Core.Crypto;
using P2P.Core.Utils;

namespace P2P.Core.Stun;

/// <summary>
/// 客户端 STUN-TCP Binding 探测（02 §3.3、FR-S-602）：以专用本地端口 L 连 STUN:3478/TCP，
/// 完成一次 Binding 事务（DEVICE-AUTH 属性与 UDP 同口径，02 §3.2）后立即关闭连接；
/// 返回映射 (公网IP:L')——该映射即后续 TCP 打洞端口预测的数据源（§5.2）。
/// 与 UDP 探测（P2P.Client.Punch.StunProber）的差异：TCP 无重试语义（连接失败即失败，
/// 重试策略归 Puncher，M2-16）。
/// </summary>
public static class StunTcpProber
{
    /// <summary>
    /// 单事务整体超时默认值（连接+请求+响应；打洞总预算 10s 内的探测份额）。
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// socket 须由调用方预绑定本地端口 L 并设置 SO_REUSEADDR（Linux 另设 SO_REUSEPORT，TD-10）且未连接；
    /// 事务结束（成功或失败）本方法关闭并释放该 socket——端口 L 随后由调用方以新 socket
    /// （ReuseAddress）listen+connect 复用（02 §3.3 端口保留复用约定）。
    /// </summary>
    public static async Task<IPEndPoint> ProbeAsync(
        Socket socket, IPEndPoint stunServer, Guid deviceId, ReadOnlyMemory<byte> deviceSecret,
        ClockSync clock, TimeProvider? time = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(stunServer);
        ArgumentNullException.ThrowIfNull(clock);
        if (socket.Connected) throw new ArgumentException("socket 须为未连接的预绑定端口 L 句柄", nameof(socket));
        time ??= TimeProvider.System;
        timeout ??= DefaultTimeout;

        using var txnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        txnCts.CancelAfter(timeout.Value);
        try
        {
            await socket.ConnectAsync(stunServer, txnCts.Token).ConfigureAwait(false);
            using var stream = new NetworkStream(socket, ownsSocket: false);

            var tid = StunCodec.NewTransactionId();
            var request = StunCodec.BuildBindingRequest(tid, deviceId, deviceSecret.Span,
                clock.NowRemoteMs(time), RandomGenerator.Bytes(16)); // ts 用控制连接 offset 校准（OQ-12）
            await StunTcpFraming.WriteAsync(stream, request, txnCts.Token).ConfigureAwait(false);

            var wire = await StunTcpFraming.TryReadAsync(stream, txnCts.Token).ConfigureAwait(false)
                ?? throw new IOException($"STUN-TCP 连接在响应前关闭：{stunServer}");
            if (!StunCodec.TryParseBindingResponse(wire, out var response)
                || !response!.TransactionId.AsSpan().SequenceEqual(tid))
                throw new IOException($"STUN-TCP 响应无效：{stunServer}");
            return response.Mapped;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"STUN-TCP 探测超时（{timeout.Value.TotalSeconds:0}s）：{stunServer}");
        }
        catch (SocketException ex)
        {
            throw new IOException($"STUN-TCP 连接失败：{stunServer}", ex);
        }
        finally
        {
            socket.Close(); // 立即关闭（02 §3.3）；端口 L 复用依赖调用方预设的 SO_REUSEADDR
        }
    }
}

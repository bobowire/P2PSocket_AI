// M1-26 客户端 STUN 探测（02 §3.1/§5.1①、OQ-12/18）：
// 从打洞专用 socket 发 Binding（DEVICE-AUTH 认证）→ 读 XOR-MAPPED-ADDRESS 得本端公网端点。
// 探测与打洞同 socket——NAT 端口映射即后续隧道承载端口（05 §3 打洞包与 STUN 探测同经本端 NAT）。
using System.Net;
using System.Net.Sockets;
using P2P.Core.Crypto;
using P2P.Core.Stun;
using P2P.Core.Utils;

namespace P2P.Client.Punch;

/// <summary>STUN UDP Binding 探测（认证态，FR-S-601）。</summary>
public static class StunProber
{
    /// <summary>在指定 socket 上探测公网映射端点。丢包重试（次数可配）；期间混入的无关包丢弃。</summary>
    public static async Task<IPEndPoint> ProbeAsync(
        Socket socket, IPEndPoint stunServer, Guid deviceId, ReadOnlyMemory<byte> deviceSecret,
        ClockSync clock, TimeProvider? time = null,
        int retries = 3, TimeSpan? perTryTimeout = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(stunServer);
        ArgumentNullException.ThrowIfNull(clock);
        time ??= TimeProvider.System;
        perTryTimeout ??= TimeSpan.FromSeconds(1);

        for (var attempt = 0; attempt < retries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var tid = StunCodec.NewTransactionId();
            var nonce = RandomGenerator.Bytes(16);
            var request = StunCodec.BuildBindingRequest(tid, deviceId, deviceSecret.Span,
                clock.NowRemoteMs(time), nonce); // ts 用控制连接 offset 校准（OQ-12）
            try
            {
                await socket.SendToAsync(request, SocketFlags.None, stunServer, ct);
            }
            catch (SocketException)
            {
                continue; // ICMP 端口不可达（Windows ConnectionReset）：本轮作废重试
            }

            var deadline = time.GetLocalNow() + perTryTimeout.Value;
            while (time.GetLocalNow() < deadline)
            {
                using var tryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                tryCts.CancelAfter(deadline - time.GetLocalNow());
                var buf = new byte[StunCodec.HeaderLen + 64];
                SocketReceiveFromResult received;
                try
                {
                    received = await socket.ReceiveFromAsync(buf, SocketFlags.None,
                        new IPEndPoint(IPAddress.Any, 0), tryCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    break; // 本轮等待超时 → 重试
                }
                catch (SocketException)
                {
                    break; // ICMP 端口不可达：本轮作废重试（服务器重启窗口等）
                }
                if (StunCodec.TryParseBindingResponse(buf.AsSpan(0, received.ReceivedBytes), out var response)
                    && response!.TransactionId.AsSpan().SequenceEqual(tid))
                    return response.Mapped;
                // 无关数据报（打洞窗口混入对端帧）：丢弃继续等
            }
        }
        throw new IOException($"STUN 探测失败：{stunServer} 连续 {retries} 次无有效响应");
    }
}

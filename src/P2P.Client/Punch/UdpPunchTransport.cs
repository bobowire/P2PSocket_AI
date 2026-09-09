// M1-26 UDP 承载（02 §4.5：UDP 打洞成功→UDP 承载；05 §4 加密与路径解耦）。
// 一个数据报=一个完整 PTP 帧；发送侧保证整帧 ≤1400B（02 §4.3）。
using System.Net;
using System.Net.Sockets;
using P2P.Client.Tunnel;
using P2P.Core.Protocol;
using P2P.Core.Tunnel;

namespace P2P.Client.Punch;

/// <summary>打洞/隧道 UDP 传输：绑定专用 socket + 对端公网端点。</summary>
public sealed class UdpPunchTransport : ITunnelTransport
{
    /// <summary>UDP 承载整帧上限（02 §4.3：避免 IP 分片；DATA 分段由发送侧保证——M1-27 splice 分块）。</summary>
    public const int MaxUdpFrame = 1400;

    private readonly Socket _socket;
    private readonly IPEndPoint _peer;
    private int _disposed;

    public UdpPunchTransport(Socket socket, IPEndPoint peer)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(peer);
        _socket = socket;
        _peer = peer;
    }

    public IPEndPoint Peer => _peer;

    public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (frame.Length > MaxUdpFrame)
            throw new ProtocolException($"UDP PTP 帧 {frame.Length}B 超 {MaxUdpFrame}B 上限（02 §4.3 发送侧保证）");
        try
        {
            await _socket.SendToAsync(frame, SocketFlags.None, _peer, ct);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            throw new IOException($"UDP 发送失败（{_peer}）：{e.Message}", e);
        }
    }

    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken ct = default)
    {
        var buf = new byte[65_535];
        try
        {
            var res = await _socket.ReceiveFromAsync(buf, SocketFlags.None,
                new IPEndPoint(IPAddress.Any, 0), ct);
            return buf.AsMemory(0, res.ReceivedBytes).ToArray();
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            return null; // 承载关闭 → 会话销毁（TunnelSession 契约）
        }
        // OperationCanceledException 正常传播（停机/断链取消）
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return ValueTask.CompletedTask;
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}

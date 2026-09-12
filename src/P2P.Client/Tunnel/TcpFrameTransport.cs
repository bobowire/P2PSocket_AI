// M2-16 TCP 承载分帧（02 §6.2 TCP 承载口径：PTP 帧以 u16 长度前缀承载；UDP_DGRAM 语义不变）。
// 直连 TCP 打洞与中继 TCP（M2-17 RelayClient）同一定界：[u16 帧长（小端，与 PtpHeader 一致）][PTP 帧]。
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using P2P.Core.Tunnel;

namespace P2P.Client.Tunnel;

/// <summary>TCP 流承载：一个 u16 前缀段 = 一个完整 PTP 帧（02 §4.5 TCP 承载）。</summary>
public sealed class TcpFrameTransport : ITunnelTransport
{
    /// <summary>帧上限 = 16B 头 + DATA 16KiB 上限（02 §4.3）+ 16B AEAD tag——生产发送侧
    /// （SendDataAsync）已保证不超，此处防御性拒绝；接收侧超限视为流错位。</summary>
    public const int MaxFrame = PtpHeader.WireLen + PtpFrameCodec.MaxDataPayload + Aead.TagLen;

    private readonly NetworkStream _stream;
    private int _disposed;

    public TcpFrameTransport(Socket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        if (!socket.Connected) throw new ArgumentException("socket 须为已连接句柄", nameof(socket));
        LocalEndPoint = socket.LocalEndPoint as IPEndPoint;   // 快照（构造后 socket 生命周期归本类）
        RemoteEndPoint = socket.RemoteEndPoint as IPEndPoint; // 打洞结果端点上报用（M2-16）
        _stream = new NetworkStream(socket, ownsSocket: true);
    }

    /// <summary>本端端点快照（连接四元组的一半；打洞结果上报）。</summary>
    public IPEndPoint? LocalEndPoint { get; }

    /// <summary>对端端点快照。</summary>
    public IPEndPoint? RemoteEndPoint { get; }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (frame.Length is < PtpHeader.WireLen or > MaxFrame)
            throw new ProtocolException($"TCP PTP 帧 {frame.Length}B 越界（16~{MaxFrame}B，02 §4.3）");
        var wire = new byte[2 + frame.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(wire, (ushort)frame.Length);
        frame.Span.CopyTo(wire.AsSpan(2));
        try
        {
            await _stream.WriteAsync(wire, ct);
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            throw new IOException($"TCP 发送失败（{_stream.Socket.RemoteEndPoint}）：{e.Message}", e);
        }
    }

    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken ct = default)
    {
        try
        {
            var prefix = await ReadExactAsync(2, ct);
            if (prefix is null) return null; // 对端干净关闭（边界）
            var len = BinaryPrimitives.ReadUInt16LittleEndian(prefix);
            if (len < PtpHeader.WireLen || len > MaxFrame) return null; // 流错位/恶意长度：不可恢复
            return await ReadExactAsync(len, ct); // 中途 EOF → null（承载关闭契约）
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            return null; // 承载关闭 → 会话销毁（TunnelSession 契约，同 UdpPunchTransport）
        }
        // OperationCanceledException 正常传播（停机/断链取消）
    }

    /// <summary>读满 n 字节；对端关闭（含中途截断）返回 null。</summary>
    private async ValueTask<byte[]?> ReadExactAsync(int n, CancellationToken ct)
    {
        var buf = new byte[n];
        var read = 0;
        while (read < n)
        {
            var got = await _stream.ReadAsync(buf.AsMemory(read), ct);
            if (got == 0) return null;
            read += got;
        }
        return buf;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return ValueTask.CompletedTask;
        _stream.Dispose(); // ownsSocket=true：连同 socket 释放
        return ValueTask.CompletedTask;
    }
}

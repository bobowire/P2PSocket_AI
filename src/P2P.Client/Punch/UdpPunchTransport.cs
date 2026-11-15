// M1-26 UDP 承载（02 §4.5：UDP 打洞成功→UDP 承载；05 §4 加密与路径解耦）。
// 一个数据报=一个完整 PTP 帧；发送侧保证整帧 ≤1400B（02 §4.3）。
// §5.4 直连优先（2026-10-21）：构造可携带额外候选端点（对端网卡 IP×监听口）——握手期
// 发送向全部端点扇出（同网段/对端公网可直达时直连即刻命中）；首个到达包把回程**钉定**
// 到其源端点（此后只发该端点——B 经局域网收到 THello1 后 THello2 也从局域网回，不再绕
// 映射端点发夹）。单目标构造行为与 M1 形态一致（扇出集仅主端点）。
using System.Net;
using System.Net.Sockets;
using P2P.Client.Tunnel;
using P2P.Core.Protocol;
using P2P.Core.Tunnel;

namespace P2P.Client.Punch;

/// <summary>打洞/隧道 UDP 传输：绑定专用 socket + 对端公网端点（可选直连候选扇出集）。</summary>
public sealed class UdpPunchTransport : ITunnelTransport
{
    /// <summary>UDP 承载整帧上限（02 §4.3：避免 IP 分片；DATA 分段由发送侧保证——M1-27 splice 分块）。</summary>
    public const int MaxUdpFrame = 1400;

    private readonly Socket _socket;
    private readonly IReadOnlyList<IPEndPoint> _targets; // 扇出集：主映射端点 + 直连候选（去重后）
    private IPEndPoint? _pinned; // 首个到达包的源端点——回程钉定（直连路径回程不走映射端点）
    private int _disposed;
    private int _released; // M6-05：socket 所有权移交标记（Adopt 后 Dispose 不再关 socket）

    public UdpPunchTransport(Socket socket, IPEndPoint peer, IReadOnlyList<IPEndPoint>? extraTargets = null)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(peer);
        _socket = socket;
        // 去重保序：主端点在前，直连候选随后（与主端点重复的不再入列）
        var targets = new List<IPEndPoint> { peer };
        if (extraTargets is not null)
            foreach (var t in extraTargets)
                if (!targets.Contains(t))
                    targets.Add(t);
        _targets = targets;
    }

    /// <summary>对端端点：钉定后=实际可达源端点（直连路径），否则=主映射端点。</summary>
    public IPEndPoint Peer => Volatile.Read(ref _pinned) ?? _targets[0];

    public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (frame.Length > MaxUdpFrame)
            throw new ProtocolException($"UDP PTP 帧 {frame.Length}B 超 {MaxUdpFrame}B 上限（02 §4.3 发送侧保证）");
        // 钉定前向扇出集全发（直连优先并行竞赛）；钉定后只发实际可达端点
        var targets = Volatile.Read(ref _pinned) is { } pinned ? [pinned] : _targets;
        try
        {
            foreach (var t in targets)
                await _socket.SendToAsync(frame, SocketFlags.None, t, ct);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            throw new IOException($"UDP 发送失败（{targets[^1]}）：{e.Message}", e);
        }
    }

    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken ct = default)
    {
        var buf = new byte[65_535];
        try
        {
            var res = await _socket.ReceiveFromAsync(buf, SocketFlags.None,
                new IPEndPoint(IPAddress.Any, 0), ct);
            // 首个到达包钉定回程（§5.4：直连路径的对端源=其网卡端点；映射路径源=映射端点，
            // 与主端点一致时钉定无行为差异）。Stray 包钉错的代价=握手失败超时（与现状同级别）。
            if (Volatile.Read(ref _pinned) is null)
                Volatile.Write(ref _pinned, (IPEndPoint?)res.RemoteEndPoint);
            return buf.AsMemory(0, res.ReceivedBytes).ToArray();
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            return null; // 承载关闭 → 会话销毁（TunnelSession 契约）
        }
        // OperationCanceledException 正常传播（停机/断链取消）
    }

    /// <summary>socket 所有权移交（M6-05，设计 11 §5.4/§7.3）：此后 <see cref="DisposeAsync"/>
    /// 不再关闭 socket（由接管方 KcpTunnelTransport 独占并在其 Dispose 时关闭）。幂等。</summary>
    public void ReleaseSocket() => Volatile.Write(ref _released, 1);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return ValueTask.CompletedTask;
        if (Volatile.Read(ref _released) != 1) _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}

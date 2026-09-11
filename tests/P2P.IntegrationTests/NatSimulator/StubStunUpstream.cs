using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using P2P.Core.Stun;

namespace P2P.IntegrationTests.NatSimulator;

/// <summary>
/// 上游 STUN 替身（09 §2.2 装置半边）：绑定独立回环 IP（默认 127.0.0.2），对任意合法
/// Binding Request 回以**恒定垃圾映射地址**（203.0.113.7:9999）——若客户端读到该地址即证明
/// 模拟器没有改写；读到分配的公网端口则证明 XOR-MAPPED-ADDRESS 改写生效（TD-17"代理一切"）。
/// 不做 DEVICE-AUTH 校验（上游鉴权是 StunService 自身职责，另有单测覆盖）。
/// </summary>
public sealed class StubStunUpstream : IAsyncDisposable
{
    public static readonly IPAddress GarbageAddress = IPAddress.Parse("203.0.113.7");
    public const ushort GarbagePort = 9999;

    private readonly IPAddress _bindAddress;
    private UdpClient _socket = null!;
    private CancellationTokenSource _cts = null!;
    private Task _loop = Task.CompletedTask;

    /// <summary>已受理的 Binding Request 数（测试观测：代理链路确实到达上游）。</summary>
    public int Requests { get; private set; }

    public IPEndPoint Endpoint { get; private set; } = null!;

    public StubStunUpstream(IPAddress? bindAddress = null) => _bindAddress = bindAddress ?? IPAddress.Parse("127.0.0.2");

    public Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        _socket = new UdpClient(new IPEndPoint(_bindAddress, 0));
        Endpoint = (IPEndPoint)_socket.Client.LocalEndPoint!;
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _socket.ReceiveAsync(ct); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException) { break; }

            if (!IsBindingRequest(r.Buffer)) continue;
            Requests++;
            // 事务 ID 位于头 8..20（type|len|cookie 之后）
            var tid = r.Buffer.AsSpan(8, StunCodec.TransactionIdLen).ToArray();
            var response = StunCodec.BuildBindingResponse(tid, GarbageAddress, GarbagePort);
            _socket.Send(response, response.Length, r.RemoteEndPoint);
        }
    }

    /// <summary>Binding Request 头校验（type/cookie/长度自洽，不校验属性）。</summary>
    private static bool IsBindingRequest(ReadOnlySpan<byte> wire)
    {
        if (wire.Length < StunCodec.HeaderLen) return false;
        var type = BinaryPrimitives.ReadUInt16BigEndian(wire.Slice(0, 2));
        var msgLen = BinaryPrimitives.ReadUInt16BigEndian(wire.Slice(2, 2));
        var cookie = BinaryPrimitives.ReadUInt32BigEndian(wire.Slice(4, 4));
        return type == StunCodec.BindingRequest && cookie == StunCodec.MagicCookie
            && wire.Length == StunCodec.HeaderLen + msgLen;
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        _socket.Dispose();
        try { await _loop; }
        catch { /* 收尾竞态兜底 */ }
        _cts.Dispose();
    }
}

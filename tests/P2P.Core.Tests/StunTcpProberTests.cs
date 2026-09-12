using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using P2P.Core.Crypto;
using P2P.Core.Stun;
using P2P.Core.Utils;
using Xunit;

namespace P2P.Core.Tests;

/// <summary>M2-04 STUN-TCP Binding 封装——分帧纯逻辑 + in-proc 探测往返 + 端口 L 复用（完成判定全两条）。</summary>
public class StunTcpProberTests
{
    private static readonly Guid DeviceId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
    private static readonly byte[] Secret = RandomGenerator.Bytes(32);

    // ── StunTcpFraming 纯逻辑 ─────────────────────────────────────────────

    [Fact]
    public async Task Framing_RoundTrip_OverStream()
    {
        var tid = StunCodec.NewTransactionId();
        var message = StunCodec.BuildBindingRequest(tid, DeviceId, Secret, 123UL, RandomGenerator.Bytes(16));

        using var stream = new MemoryStream();
        await StunTcpFraming.WriteAsync(stream, message);
        stream.Position = 0;
        var read = await StunTcpFraming.TryReadAsync(stream);
        Assert.NotNull(read);
        Assert.Equal(message, read);
    }

    [Fact]
    public async Task Framing_EmptyStream_ReturnsNull()
    {
        using var empty = new MemoryStream();
        Assert.Null(await StunTcpFraming.TryReadAsync(empty));
    }

    [Fact]
    public async Task Framing_TruncatedMidMessage_Throws()
    {
        var message = StunCodec.BuildBindingResponse(StunCodec.NewTransactionId(),
            IPAddress.Loopback, 40001);
        using var cut = new MemoryStream(message[..^3]); // 尾部缺 3B
        await Assert.ThrowsAsync<InvalidDataException>(() => StunTcpFraming.TryReadAsync(cut));
    }

    [Fact]
    public async Task Framing_OversizedAttrLen_Throws()
    {
        // 头声明 msgLen 超上限：读到头即拒（读端不等待巨量字节）
        var header = new byte[StunCodec.HeaderLen];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0, 2), StunCodec.BindingSuccess);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2, 2), (ushort)(StunTcpFraming.MaxAttrLen + 1));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), StunCodec.MagicCookie);

        using var bogus = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => StunTcpFraming.TryReadAsync(bogus));
    }

    [Fact]
    public async Task Framing_WriteLenMismatch_Rejected()
    {
        // 头 msgLen 与实际长度不符：写出侧拒绝
        var message = StunCodec.BuildBindingResponse(StunCodec.NewTransactionId(),
            IPAddress.Loopback, 40001);
        message[2] = 0xFF; // 破坏 msgLen
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<ArgumentException>(() => StunTcpFraming.WriteAsync(stream, message));
    }

    // ── in-proc STUN-TCP 探测往返（完成判定①）──────────────────────────────

    /// <summary>极简 STUN-TCP 短事务服务（M2-06 落正式版前的测试替身）：收一帧→验 DEVICE-AUTH→回 XOR-MAPPED→关。</summary>
    private sealed class MiniStunTcpServer
    {
        private readonly TcpListener _listener;
        public IPEndPoint Endpoint { get; }
        public IPEndPoint? LastClientEndpoint; // 服务端所见的客户端公网端点（回包目标）

        public MiniStunTcpServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Endpoint = (IPEndPoint)_listener.LocalEndpoint;
        }

        /// <summary>处理一笔事务后退出（短事务语义，02 §3.3）。</summary>
        public async Task ServeOneAsync(CancellationToken ct = default)
        {
            using var conn = await _listener.AcceptSocketAsync(ct);
            using var stream = new NetworkStream(conn, ownsSocket: false);
            var request = await StunTcpFraming.TryReadAsync(stream, ct);
            Assert.NotNull(request); // 测试替身：连接必有请求

            // DEVICE-AUTH 校验同 UDP 口径（02 §3.2：未注册/HMAC 不符静默丢弃——此处直接断开）
            Assert.True(StunCodec.TryParseDeviceAuth(request!, id => id == DeviceId ? Secret : null));

            var tid = request!.AsSpan(8, StunCodec.TransactionIdLen).ToArray();
            var remote = (IPEndPoint)conn.RemoteEndPoint!;
            LastClientEndpoint = remote;
            await StunTcpFraming.WriteAsync(stream,
                StunCodec.BuildBindingResponse(tid, remote.Address, (ushort)remote.Port), ct);
        }

        public void Stop() => _listener.Stop();
    }

    private static Socket NewPortLSocket(out int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true); // TD-10
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        return socket;
    }

    private static ClockSync CalibratedClock() => new(); // ts 窗口校验在替身侧未启用（UDP 服务口径，M2-06 正式版）

    [Fact]
    public async Task Probe_RoundTrip_ReturnsMappedEndpoint()
    {
        var server = new MiniStunTcpServer();
        var serving = server.ServeOneAsync();

        var socket = NewPortLSocket(out var portL);
        var mapped = await StunTcpProber.ProbeAsync(socket, server.Endpoint, DeviceId, Secret,
            CalibratedClock(), timeout: TimeSpan.FromSeconds(5));

        await serving;
        server.Stop();

        // 无 NAT 环境：映射端点 = 服务端所见的客户端端点 = 本地端口 L
        Assert.Equal(IPAddress.Loopback, mapped.Address);
        Assert.Equal(portL, mapped.Port);
        Assert.Equal(portL, server.LastClientEndpoint!.Port);
    }

    [Fact]
    public async Task Probe_ServerUnreachable_ThrowsIoTimeout()
    {
        // 占住一个临时端口但绝不 listen：入连立即 RST → 连接失败被包裹为 IOException
        var holder = NewPortLSocket(out var deadPort);
        try
        {
            var socket = NewPortLSocket(out _);
            var ex = await Assert.ThrowsAsync<IOException>(() => StunTcpProber.ProbeAsync(
                socket, new IPEndPoint(IPAddress.Loopback, deadPort), DeviceId, Secret,
                CalibratedClock(), timeout: TimeSpan.FromMilliseconds(500)));
            Assert.Contains("STUN-TCP", ex.Message);
        }
        finally { holder.Close(); }
    }

    // ── 端口 L 复用：连接关闭后仍可 listen（完成判定②，SO_REUSEADDR）──────

    [Fact]
    public async Task Probe_AfterClose_PortLStillListensWithReuseAddress()
    {
        var server = new MiniStunTcpServer();
        var serving = server.ServeOneAsync();

        var socket = NewPortLSocket(out var portL);
        var mapped = await StunTcpProber.ProbeAsync(socket, server.Endpoint, DeviceId, Secret,
            CalibratedClock(), timeout: TimeSpan.FromSeconds(5));
        await serving;
        server.Stop();
        Assert.Equal(portL, mapped.Port);

        // 探测方主动关闭 → 端口 L 处于 TIME_WAIT；SO_REUSEADDR 允许新 socket 绑定同端口 listen
        // （02 §3.3 端口保留复用 = M2-16 Puncher listen+connect 的前提）
        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, portL));
        listener.Listen(1);
        listener.Close();

        // 原 socket 已被 ProbeAsync 关闭释放
        Assert.Throws<ObjectDisposedException>(() => socket.RemoteEndPoint);
    }
}

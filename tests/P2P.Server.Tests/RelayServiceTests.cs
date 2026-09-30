using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using P2P.Server.Data;
using P2P.Server.Services;
using Xunit;

namespace P2P.Server.Tests;

/// <summary>
/// 中继服务测试（02 §6、05 §6，FR-S-701/702/704，TD-11）：双端 RELAY_JOIN 地址学习 →
/// 双向密文帧转发（UDP/TCP/混合承载）、转发字节与发送内层逐字节相等（零解密断言——服务端无钥，
/// 任何解析/重编码都会破坏密文不变性）、先 JOIN 先发在对端加入前丢弃、未知源丢弃（防劫持）、
/// 空闲 90s 回收、TCP 断连即收会话并关对端、0x74 三错误路径（5002/1001/4005）、Grant 双侧下发。
/// 夹具 RelayService 挂 PublicHost=203.0.113.99（M2-36）：Grant 端点断言覆盖通告地址语义；
/// 默认本地侧派生路径由集成 harness（默认 options）端到端覆盖。
/// </summary>
public sealed class RelayServiceTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    private readonly DeviceRegistry _registry = new();
    private readonly List<TestPcpClient> _clients = [];
    private readonly List<UdpClient> _udps = [];
    private readonly List<TcpClient> _tcps = [];
    private ControlServer _server = null!;
    private SignalingCoordinator _signaling = null!;
    private RelayService _relay = null!;
    private FakeTimeProvider _time = null!;
    private IPEndPoint _relayUdp = null!;
    private IPEndPoint _relayTcp = null!;

    public async Task InitializeAsync()
    {
        var factory = new StubFactory(CreateDb);
        using var init = factory.CreateDbContext();
        DbInitializer.Initialize(init);
        _time = new FakeTimeProvider(DateTimeOffset.UtcNow);

        var audit = new AuditLogger(factory, _time);
        _signaling = new SignalingCoordinator(factory, _registry, new Authorizer(factory), audit, _time);
        _relay = new RelayService(factory, _registry, _signaling.ResolveRelayPeers,
            new RelayServiceOptions { PublicHost = "203.0.113.99" }, time: _time); // M2-36：通告地址覆盖派生
        var router = new ControlMessageRouter(
            new RegistrationService(factory, _registry, audit, _time),
            new UserService(factory, audit, _time),
            new GroupService(factory, _registry, audit, time: _time),
            _signaling,
            new MappingService(factory, audit),
            _relay,
            new StatsService(factory, audit),
            audit);
        _server = new ControlServer(factory, _registry, router.DispatchAsync, time: _time);
        await _server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
        await _relay.StartAsync(0, 0); // 双端口系统分配（测试隔离）
        // 服务端绑 0.0.0.0（LocalEndPoint 为 Any）：测试经回环寻址，取端口派生
        _relayUdp = new IPEndPoint(IPAddress.Loopback, _relay.UdpEndpoint!.Port);
        _relayTcp = new IPEndPoint(IPAddress.Loopback, _relay.TcpEndpoint!.Port);
    }

    public async Task DisposeAsync()
    {
        foreach (var c in _clients) await c.DisposeAsync();
        foreach (var u in _udps) u.Dispose();
        foreach (var t in _tcps) t.Dispose();
        await _server.DisposeAsync();
        await _relay.DisposeAsync();
        await _signaling.DisposeAsync();
        // 服务端收尾审计/台账清理与连接销毁并发时 SqliteConnection.Close 内部枚举可能竞态
        // （LocalWebApi/MappingSync 测试同源的已知瞬态）：宽限后仍异常则吞掉，不连坐测试结果
        await Task.Delay(200);
        try { _db.Dispose(); }
        catch (InvalidOperationException) { }
    }

    private AppDbContext CreateDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_db.DataSource).Options);

    // ── 世界构建 ───────────────────────────────────────────────────────

    private async Task<(TestPcpClient Client, Guid DeviceId)> RegisterAsync(string name)
    {
        var client = await TestPcpClient.ConnectAsync(_server.LocalEndPoint!);
        _clients.Add(client);
        await client.HelloAsync(deviceId: null);
        var key = EcKeyPair.Generate();
        var mac = $"P2P-RLY{Guid.NewGuid():N}"[..14];
        await client.SendAsync(new Register(client.NextSeq(), client.Now(), MsgType.Register,
            mac, name, "windows", "0.1.0", key.ExportPublicKey(), null, null, null), sign: false);
        var ack = await client.ReceiveAsync<RegisterAck>() ?? throw new IOException("注册无应答");
        client.EstablishWithSecret(Ecies.Decrypt(key, ack.DeviceSecretBox));
        return (client, ack.DeviceId);
    }

    /// <summary>走完两段式打洞（0x70→0x71→0x76→Ack）：会话完成入台账，返回 sessionId。</summary>
    private static async Task<Guid> CompletePunchAsync(TestPcpClient a, TestPcpClient b, Guid bId)
    {
        await a.SendAsync(new PunchRequest(a.NextSeq(), a.Now(), MsgType.PunchRequest,
            bId, null, "udp", new EndpointPair(new P2P.Core.Protocol.Endpoint("203.0.113.10", 50000), null), null));
        var invite = await b.ReceiveAsync<PunchInvite>() ?? throw new IOException("B 未收到 PunchInvite");
        await b.SendAsync(new PunchEndpoint(b.NextSeq(), b.Now(), MsgType.PunchEndpoint,
            invite.SessionId, new EndpointPair(new P2P.Core.Protocol.Endpoint("198.51.100.20", 50001), null)));
        var ack = await a.ReceiveAsync<PunchRequestAck>() ?? throw new IOException("A 未收到延后 Ack");
        return ack.SessionId;
    }

    /// <summary>0x74 分配：返回 (A 的 Grant, B 的 Grant)——Grant 向双方下发（02 §6.1②）。</summary>
    private static async Task<(RelayGrant A, RelayGrant B)> AllocateAsync(TestPcpClient a, TestPcpClient b, Guid sessionId)
    {
        await a.SendAsync(new RelayAllocate(a.NextSeq(), a.Now(), MsgType.RelayAllocate, sessionId));
        var ga = await a.ReceiveAsync<RelayGrant>() ?? throw new IOException("A 未收到 RelayGrant");
        var gb = await b.ReceiveAsync<RelayGrant>() ?? throw new IOException("B 未收到 RelayGrant");
        return (ga, gb);
    }

    // ── RLP 线格式辅助（测试侧独立实现，不复产码）─────────────────────

    private static byte[] BuildJoin(ulong sid)
    {
        var join = new byte[RelayService.JoinLen];
        join[0] = RelayService.JoinPrefix;
        BinaryPrimitives.WriteUInt64LittleEndian(join.AsSpan(1), sid);
        BinaryPrimitives.WriteUInt32LittleEndian(join.AsSpan(9), 0x5A5A5A5Au); // nonce 仅保形
        return join;
    }

    private static byte[] BuildData(ulong sid, ReadOnlySpan<byte> ptp)
    {
        var wire = new byte[RelayService.HeaderLen + ptp.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(wire, sid);
        ptp.CopyTo(wire.AsSpan(RelayService.HeaderLen));
        return wire;
    }

    /// <summary>PTP 密文帧替身：随机字节（服务端零解密——内容任意不可读即可，TD-11）。</summary>
    private static byte[] PtpCiphertext(int len) => RandomGenerator.Bytes(len);

    private async Task<UdpClient> JoinUdpAsync(ulong sid)
    {
        var udp = new UdpClient(AddressFamily.InterNetwork); // 显式 IPv4（M2-06 教训）
        _udps.Add(udp);
        udp.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        await udp.SendAsync(BuildJoin(sid), _relayUdp);
        var ack = await ReceiveUdpAsync(udp);
        Assert.Equal((byte)RelayService.JoinAckPrefix, Assert.Single(ack));
        return udp;
    }

    private async Task<TcpClient> JoinTcpAsync(ulong sid)
    {
        var tcp = new TcpClient(AddressFamily.InterNetwork);
        _tcps.Add(tcp);
        await tcp.ConnectAsync(_relayTcp);
        await WriteTcpFrameAsync(tcp.GetStream(), BuildJoin(sid));
        var ack = await ReadTcpFrameAsync(tcp.GetStream());
        Assert.Equal((byte)RelayService.JoinAckPrefix, Assert.Single(ack));
        return tcp;
    }

    private static async Task WriteTcpFrameAsync(NetworkStream stream, ReadOnlyMemory<byte> payload)
    {
        var buf = new byte[2 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(buf, (ushort)payload.Length);
        payload.Span.CopyTo(buf.AsSpan(2));
        await stream.WriteAsync(buf);
    }

    private static async Task<byte[]> ReadTcpFrameAsync(NetworkStream stream, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var header = new byte[2];
        await ReadExactAsync(stream, header, cts.Token);
        var len = BinaryPrimitives.ReadUInt16LittleEndian(header);
        Assert.InRange(len, 1, RelayService.MaxFrameLen);
        var payload = new byte[len];
        if (!await ReadExactAsync(stream, payload, cts.Token))
            throw new IOException("帧中途断开");
        return payload;
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, Memory<byte> buf, CancellationToken ct)
    {
        var read = 0;
        while (read < buf.Length)
        {
            var n = await stream.ReadAsync(buf[read..], ct);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    private static async Task<byte[]> ReceiveUdpAsync(UdpClient udp, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try { return (await udp.ReceiveAsync(cts.Token)).Buffer; }
        catch (OperationCanceledException) { throw new TimeoutException($"未在 {timeoutMs}ms 内收到 UDP 包"); }
    }

    private static async Task AssertUdpSilentAsync(UdpClient udp, int ms = 400)
    {
        using var cts = new CancellationTokenSource(ms);
        try { await udp.ReceiveAsync(cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (SocketException) { return; } // 取消竞态下的 aborted receive
        throw new Xunit.Sdk.XunitException($"预期 {ms}ms 内无 UDP 包，却收到了");
    }

    private static async Task AssertTcpSilentAsync(NetworkStream stream, int ms = 400)
    {
        try { await ReadTcpFrameAsync(stream, ms); }
        catch (TimeoutException) { return; }
        catch (OperationCanceledException) { return; }
        throw new Xunit.Sdk.XunitException($"预期 {ms}ms 内无 TCP 帧，却收到了");
    }

    // ── 0x74 分配：Grant 双侧下发与端点正确性 ─────────────────────────

    [Fact]
    public async Task Allocate_AfterPunchCompleted_GrantsBothSidesWithEndpoints()
    {
        var (a, _) = await RegisterAsync("relay-a");
        var (b, bId) = await RegisterAsync("relay-b");
        var punchId = await CompletePunchAsync(a, b, bId);

        var (ga, gb) = await AllocateAsync(a, b, punchId);

        Assert.Equal(ga.RelaySessionId, gb.RelaySessionId); // 同一 relaySessionId
        Assert.NotEqual(0UL, ga.RelaySessionId);
        // 端点 = public_addr 通告地址（夹具 PublicHost=203.0.113.99 覆盖本地侧派生，M2-36）
        // + relay 双端口；默认派生路径（控制连接本地侧地址）由集成 harness 全量接线覆盖
        // （RelayClientIntegrationTests 等四套默认 options，回环派生可达性端到端验证）
        Assert.Equal("203.0.113.99", ga.RelayEndpoints.Udp!.Host);
        Assert.Equal("203.0.113.99", ga.RelayEndpoints.Tcp!.Host);
        Assert.Equal(_relayUdp.Port, ga.RelayEndpoints.Udp.Port);
        Assert.Equal(_relayTcp.Port, ga.RelayEndpoints.Tcp!.Port);
        Assert.Equal(ga.RelayEndpoints.Udp!.Port, gb.RelayEndpoints.Udp!.Port);
        Assert.Equal(1, _relay.Stats.Sessions); // 台账命中（A 为发起方）
    }

    [Fact]
    public async Task Allocate_RelayDisabled_Returns5002()
    {
        await using (var db = CreateDb())
        {
            db.ServerConfig.Single(c => c.Key == "relay_enabled").Value = "0";
            await db.SaveChangesAsync();
        }
        var (a, _) = await RegisterAsync("off-a");
        var (b, bId) = await RegisterAsync("off-b");
        var punchId = await CompletePunchAsync(a, b, bId);

        await a.SendAsync(new RelayAllocate(a.NextSeq(), a.Now(), MsgType.RelayAllocate, punchId));
        var error = await a.ReceiveAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.RelayDisabled, error!.Code); // 5002（03 §2.8）
        Assert.Equal(0, _relay.Stats.Sessions);
    }

    [Fact]
    public async Task Allocate_UnknownPunchSession_Returns1001()
    {
        var (a, _) = await RegisterAsync("unk-a");
        var (b, _) = await RegisterAsync("unk-b");

        await a.SendAsync(new RelayAllocate(a.NextSeq(), a.Now(), MsgType.RelayAllocate, Guid.NewGuid()));
        var error = await a.ReceiveAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.BadRequest, error!.Code);
        Assert.Equal("session_unknown", error.HttpLikeMsg);
    }

    [Fact]
    public async Task Allocate_NonInitiatorOrTargetOffline_Returns4005()
    {
        var (a, _) = await RegisterAsync("gone-a");
        var (b, bId) = await RegisterAsync("gone-b");
        var punchId = await CompletePunchAsync(a, b, bId);

        await b.DisposeAsync(); // B 控制连接断开 → 注销（台账仍在，注册表 miss → 4005）
        _clients.Remove(b);
        var deadline = Environment.TickCount + 5000;
        while (_registry.IsOnline(bId) && Environment.TickCount < deadline)
            await Task.Delay(20);
        Assert.False(_registry.IsOnline(bId));

        await a.SendAsync(new RelayAllocate(a.NextSeq(), a.Now(), MsgType.RelayAllocate, punchId));
        var error = await a.ReceiveAsync<ErrorMessage>();
        Assert.Equal(ErrorCode.TargetOffline, error!.Code);
    }

    // ── UDP 承载：地址学习 + 双向转发 + 零解密 ─────────────────────────

    [Fact]
    public async Task Relay_UdpBothEnds_JoinLearnForwardBothWays()
    {
        var (a, _) = await RegisterAsync("udp-a");
        var (b, bId) = await RegisterAsync("udp-b");
        var punchId = await CompletePunchAsync(a, b, bId);
        var (ga, _) = await AllocateAsync(a, b, punchId);
        var sid = ga.RelaySessionId;

        var aUdp = await JoinUdpAsync(sid);
        var bUdp = await JoinUdpAsync(sid);

        // A → B：relay 剥离 8B 后送出，密文逐字节不变（服务端无钥——任何解析都会破坏不变性）
        var aToB = PtpCiphertext(96);
        await aUdp.SendAsync(BuildData(sid, aToB), _relayUdp);
        var gotAtB = await ReceiveUdpAsync(bUdp);
        Assert.Equal(aToB, gotAtB);

        // B → A（双向）
        var bToA = PtpCiphertext(72);
        await bUdp.SendAsync(BuildData(sid, bToA), _relayUdp);
        var gotAtA = await ReceiveUdpAsync(aUdp);
        Assert.Equal(bToA, gotAtA);

        Assert.Equal(aToB.Length + bToA.Length, _relay.Stats.BytesForwarded); // 剥离后字节数
        Assert.Equal(1, _relay.Stats.Sessions);
    }

    [Fact]
    public async Task Relay_UdpDataBeforePeerJoin_DroppedUntilBothJoined()
    {
        var (a, _) = await RegisterAsync("early-a");
        var (b, bId) = await RegisterAsync("early-b");
        var punchId = await CompletePunchAsync(a, b, bId);
        var (ga, _) = await AllocateAsync(a, b, punchId);

        var aUdp = await JoinUdpAsync(sid: ga.RelaySessionId);
        var early = PtpCiphertext(64);
        await aUdp.SendAsync(BuildData(ga.RelaySessionId, early), _relayUdp); // B 未 JOIN：丢弃

        var bUdp = await JoinUdpAsync(ga.RelaySessionId);
        await aUdp.SendAsync(BuildData(ga.RelaySessionId, early), _relayUdp); // 再发：可达
        var got = await ReceiveUdpAsync(bUdp);
        Assert.Equal(early, got);
    }

    [Fact]
    public async Task Relay_UnknownSourceDatagram_Dropped_AntiHijack()
    {
        var (a, _) = await RegisterAsync("hijack-a");
        var (b, bId) = await RegisterAsync("hijack-b");
        var punchId = await CompletePunchAsync(a, b, bId);
        var (ga, _) = await AllocateAsync(a, b, punchId);

        var aUdp = await JoinUdpAsync(ga.RelaySessionId);
        var bUdp = await JoinUdpAsync(ga.RelaySessionId);

        var stranger = new UdpClient(AddressFamily.InterNetwork); // 第三方伪造源
        _udps.Add(stranger);
        stranger.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        await stranger.SendAsync(BuildData(ga.RelaySessionId, PtpCiphertext(64)), _relayUdp);

        await AssertUdpSilentAsync(bUdp); // 未学源：保守丢弃
        // 正常源不受影响
        var ok = PtpCiphertext(48);
        await aUdp.SendAsync(BuildData(ga.RelaySessionId, ok), _relayUdp);
        Assert.Equal(ok, await ReceiveUdpAsync(bUdp));
    }

    // ── TCP 承载：u16 分帧 + 双向转发 ─────────────────────────────────

    [Fact]
    public async Task Relay_TcpBothEnds_ForwardBothWaysWithFraming()
    {
        var (a, _) = await RegisterAsync("tcp-a");
        var (b, bId) = await RegisterAsync("tcp-b");
        var punchId = await CompletePunchAsync(a, b, bId);
        var (ga, _) = await AllocateAsync(a, b, punchId);
        var sid = ga.RelaySessionId;

        var aTcp = await JoinTcpAsync(sid);
        var bTcp = await JoinTcpAsync(sid);

        var aToB = PtpCiphertext(128);
        await WriteTcpFrameAsync(aTcp.GetStream(), BuildData(sid, aToB));
        var gotAtB = await ReadTcpFrameAsync(bTcp.GetStream());
        Assert.Equal(aToB, gotAtB); // 剥离 8B → u16 分帧裸密文

        var bToA = PtpCiphertext(80);
        await WriteTcpFrameAsync(bTcp.GetStream(), BuildData(sid, bToA));
        var gotAtA = await ReadTcpFrameAsync(aTcp.GetStream());
        Assert.Equal(bToA, gotAtA);
    }

    [Fact]
    public async Task Relay_MixedCarriers_CrossForwardUdpTcp()
    {
        var (a, _) = await RegisterAsync("mix-a");
        var (b, bId) = await RegisterAsync("mix-b");
        var punchId = await CompletePunchAsync(a, b, bId);
        var (ga, _) = await AllocateAsync(a, b, punchId);
        var sid = ga.RelaySessionId;

        var aUdp = await JoinUdpAsync(sid);  // A 走 UDP
        var bTcp = await JoinTcpAsync(sid);  // B 走 TCP

        // UDP → TCP：datagram 剥离后以 u16 分帧送出
        var udpToTcp = PtpCiphertext(56);
        await aUdp.SendAsync(BuildData(sid, udpToTcp), _relayUdp);
        Assert.Equal(udpToTcp, await ReadTcpFrameAsync(bTcp.GetStream()));

        // TCP → UDP：帧剥离后以裸 datagram 送出
        var tcpToUdp = PtpCiphertext(40);
        await WriteTcpFrameAsync(bTcp.GetStream(), BuildData(sid, tcpToUdp));
        Assert.Equal(tcpToUdp, await ReceiveUdpAsync(aUdp));
    }

    [Fact]
    public async Task Relay_TcpDisconnect_ReapsSessionAndClosesPeer()
    {
        var (a, _) = await RegisterAsync("disc-a");
        var (b, bId) = await RegisterAsync("disc-b");
        var punchId = await CompletePunchAsync(a, b, bId);
        var (ga, _) = await AllocateAsync(a, b, punchId);

        var aTcp = await JoinTcpAsync(ga.RelaySessionId);
        var bTcp = await JoinTcpAsync(ga.RelaySessionId);
        Assert.Equal(1, _relay.Stats.Sessions);

        bTcp.Dispose(); // B 断连 = KEEPALIVE 停止 → 会话即时回收（02 §6.2）
        var deadline = Environment.TickCount + 5000;
        while (_relay.Stats.Sessions > 0 && Environment.TickCount < deadline)
            await Task.Delay(20);
        Assert.Equal(0, _relay.Stats.Sessions);
        Assert.Equal(1, _relay.Stats.Reaped);

        // A 的连接被服务端一并关闭（读得 EOF）
        var stream = aTcp.GetStream();
        var buf = new byte[16];
        Assert.Equal(0, await stream.ReadAsync(buf)); // EOF
    }

    // ── 空闲回收（02 §6.2：任端 90s 无包）──────────────────────────────

    [Fact]
    public async Task Relay_Idle90s_Reaped()
    {
        var (a, _) = await RegisterAsync("idle-a");
        var (b, bId) = await RegisterAsync("idle-b");
        var punchId = await CompletePunchAsync(a, b, bId);
        var (ga, _) = await AllocateAsync(a, b, punchId);

        var aUdp = await JoinUdpAsync(ga.RelaySessionId);
        var bUdp = await JoinUdpAsync(ga.RelaySessionId);
        await aUdp.SendAsync(BuildData(ga.RelaySessionId, PtpCiphertext(32)), _relayUdp);
        Assert.Equal(32, (await ReceiveUdpAsync(bUdp)).Length); // 业务流量刷新空闲时钟

        _time.Advance(TimeSpan.FromSeconds(106)); // 跨 15s 回收 tick：idle 105s > 90s
        Assert.Equal(0, _relay.Stats.Sessions);
        Assert.Equal(1, _relay.Stats.Reaped);

        // 回收后数据包静默丢弃
        await aUdp.SendAsync(BuildData(ga.RelaySessionId, PtpCiphertext(32)), _relayUdp);
        await AssertUdpSilentAsync(bUdp);
    }
}

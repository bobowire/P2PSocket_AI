using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using P2P.Server.Data;
using P2P.Server.Services;
using P2P.Server.Web;
using Xunit;

namespace P2P.Server.Tests.Web;

/// <summary>
/// M3-07 中继管理 API（FR-S-824/703、04 §3.2、TD-23）：会话快照字段与字节/承载表达、
/// PUT config 写库+限速进程内直调即时生效（部分更新语义）+校验拒绝、审计行。
/// 全链夹具同 AdminDashboardApiTests + 真实启动 relay 双端口（UDP/TCP JOIN 驱动真会话）。
/// 限速欠账等待语义（假时钟）由 RelayServiceTests 覆盖——本夹具走系统时钟，仅验配置生效面。
/// </summary>
public sealed class AdminRelayApiTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    private readonly DeviceRegistry _registry = new();
    private readonly List<TestPcpClient> _clients = [];
    private readonly List<UdpClient> _udps = [];
    private readonly List<TcpClient> _tcps = [];
    private ControlServer _server = null!;
    private SignalingCoordinator _signaling = null!;
    private RelayService _relay = null!;
    private RelayRateLimiter _limiter = null!;
    private ServerWebHostService _web = null!;
    private HttpClient _http = null!;
    private IPEndPoint _relayUdp = null!;
    private IPEndPoint _relayTcp = null!;

    public async Task InitializeAsync()
    {
        var factory = new StubFactory(CreateDb);
        using (var init = factory.CreateDbContext())
            DbInitializer.Initialize(init);

        var audit = new AuditLogger(factory);
        _limiter = new RelayRateLimiter(0);
        _signaling = new SignalingCoordinator(factory, _registry, new Authorizer(factory), audit,
            rateLimiter: _limiter);
        _relay = new RelayService(factory, _registry, _signaling.ResolveRelayPeers, rateLimiter: _limiter);
        var invalidation = new InvalidationPusher(factory, _registry);
        var pusher = new DeviceListPusher(factory, _registry);
        var groupService = new GroupService(factory, _registry, audit, pusher, invalidation);
        var router = new ControlMessageRouter(
            new RegistrationService(factory, _registry, audit, invalidation: invalidation, listPusher: pusher),
            new UserService(factory, audit, invalidation: invalidation),
            groupService,
            _signaling,
            new MappingService(factory, audit),
            _relay,
            new StatsService(factory, audit),
            audit,
            new LanSegmentService(factory, _registry, audit));
        var admin = new AdminService(factory, _registry, audit, invalidation, listPusher: pusher);
        _server = new ControlServer(factory, _registry, router.DispatchAsync);
        await _server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
        await _relay.StartAsync(0, 0); // 双端口系统分配（测试隔离）
        _relayUdp = new IPEndPoint(IPAddress.Loopback, _relay.UdpEndpoint!.Port);
        _relayTcp = new IPEndPoint(IPAddress.Loopback, _relay.TcpEndpoint!.Port);

        var options = new ServerOptions { Listen = { Web = Random.Shared.Next(21000, 24000) } };
        _web = new ServerWebHostService(options, TimeProvider.System, new AdminSessionStore(TimeProvider.System),
            factory, audit, admin, _registry, groupService, _relay, null, _limiter)
        {
            WebRootOverride = Path.Combine(Path.GetTempPath(), $"p2p-no-webroot-{Guid.NewGuid():N}"),
        };
        await _web.StartAsync(CancellationToken.None);

        _http = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{options.Listen.Web}/"),
        };
        Assert.Equal(HttpStatusCode.OK,
            (await _http.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin" })).StatusCode);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _web.StopAsync(CancellationToken.None);
        await _relay.DisposeAsync();
        foreach (var u in _udps) u.Dispose();
        foreach (var t in _tcps) t.Dispose();
        foreach (var c in _clients) await c.DisposeAsync();
        await _server.DisposeAsync();
        await _signaling.DisposeAsync();
        // 真会话收尾与台账清理并发时 SqliteConnection.Close 内部枚举可能竞态
        // （RelayServiceTests 同源的已知瞬态）：宽限后仍异常则吞掉，不连坐测试结果
        await Task.Delay(200);
        try { _db.Dispose(); }
        catch (InvalidOperationException) { }
    }

    private AppDbContext CreateDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_db.DataSource).Options);

    /// <summary>注册设备（未登录——打洞/分配链不依赖账号）。</summary>
    private async Task<(TestPcpClient Client, Guid DeviceId)> RegisterAsync(string name)
    {
        var client = await TestPcpClient.ConnectAsync(_server.LocalEndPoint!);
        _clients.Add(client);
        await client.HelloAsync(deviceId: null);
        var key = EcKeyPair.Generate();
        await client.SendAsync(new Register(client.NextSeq(), client.Now(), MsgType.Register,
            $"P2P-R07{Guid.NewGuid():N}"[..16], name, "windows", "0.1.0", key.ExportPublicKey(), null, null, null),
            sign: false);
        var ack = await client.ReceiveSkippingPushesAsync<RegisterAck>() ?? throw new IOException("注册无应答");
        client.EstablishWithSecret(Ecies.Decrypt(key, ack.DeviceSecretBox));
        return (client, ack.DeviceId);
    }

    /// <summary>两段式打洞（0x70→0x71→0x76→Ack）+ 0x74 分配：返回 Grant（A 侧）。
    /// 接收走跳推送变体——本夹具挂 DeviceListPusher，对端注册触发的 0x41 会与握手交错。</summary>
    private async Task<RelayGrant> AllocateAsync(TestPcpClient a, TestPcpClient b, Guid bId)
    {
        await a.SendAsync(new PunchRequest(a.NextSeq(), a.Now(), MsgType.PunchRequest,
            bId, null, "udp", new EndpointPair(new P2P.Core.Protocol.Endpoint("203.0.113.10", 50000), null), null));
        var invite = await b.ReceiveSkippingPushesAsync<PunchInvite>() ?? throw new IOException("B 未收到 PunchInvite");
        await b.SendAsync(new PunchEndpoint(b.NextSeq(), b.Now(), MsgType.PunchEndpoint,
            invite.SessionId, new EndpointPair(new P2P.Core.Protocol.Endpoint("198.51.100.20", 50001), null)));
        var ack = await a.ReceiveSkippingPushesAsync<PunchRequestAck>() ?? throw new IOException("A 未收到延后 Ack");
        await a.SendAsync(new RelayAllocate(a.NextSeq(), a.Now(), MsgType.RelayAllocate, ack.SessionId));
        return await a.ReceiveSkippingPushesAsync<RelayGrant>() ?? throw new IOException("A 未收到 RelayGrant");
    }

    // ── RLP 线格式辅助（与 RelayServiceTests 同口径，测试侧独立实现不复产码）──

    private static byte[] BuildJoin(ulong sid)
    {
        var join = new byte[RelayService.JoinLen];
        join[0] = RelayService.JoinPrefix;
        BinaryPrimitives.WriteUInt64LittleEndian(join.AsSpan(1), sid);
        BinaryPrimitives.WriteUInt32LittleEndian(join.AsSpan(9), 0x5A5A5A5Au);
        return join;
    }

    private static byte[] BuildData(ulong sid, ReadOnlySpan<byte> ptp)
    {
        var wire = new byte[RelayService.HeaderLen + ptp.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(wire, sid);
        ptp.CopyTo(wire.AsSpan(RelayService.HeaderLen));
        return wire;
    }

    private async Task<UdpClient> JoinUdpAsync(ulong sid)
    {
        var udp = new UdpClient(AddressFamily.InterNetwork);
        _udps.Add(udp);
        udp.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        await udp.SendAsync(BuildJoin(sid), _relayUdp);
        var ack = await udp.ReceiveAsync();
        Assert.Equal((byte)RelayService.JoinAckPrefix, Assert.Single(ack.Buffer));
        return udp;
    }

    private async Task<TcpClient> JoinTcpAsync(ulong sid)
    {
        var tcp = new TcpClient(AddressFamily.InterNetwork);
        _tcps.Add(tcp);
        await tcp.ConnectAsync(_relayTcp);
        var stream = tcp.GetStream();
        var join = BuildJoin(sid);
        var buf = new byte[2 + join.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(buf, (ushort)join.Length);
        join.CopyTo(buf, 2);
        await stream.WriteAsync(buf);
        var header = new byte[2];
        await ReadExactAsync(stream, header);
        var len = BinaryPrimitives.ReadUInt16LittleEndian(header);
        var payload = new byte[len];
        await ReadExactAsync(stream, payload);
        Assert.Equal((byte)RelayService.JoinAckPrefix, Assert.Single(payload));
        return tcp;
    }

    private static async Task ReadExactAsync(NetworkStream stream, Memory<byte> buf)
    {
        var read = 0;
        while (read < buf.Length)
        {
            var n = await stream.ReadAsync(buf[read..]);
            if (n == 0) throw new IOException("流提前关闭");
            read += n;
        }
    }

    private static async Task WriteTcpFrameAsync(NetworkStream stream, ReadOnlyMemory<byte> payload)
    {
        var buf = new byte[2 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(buf, (ushort)payload.Length);
        payload.Span.CopyTo(buf.AsSpan(2));
        await stream.WriteAsync(buf);
    }

    private static async Task<byte[]> ReadTcpFrameAsync(NetworkStream stream)
    {
        var header = new byte[2];
        await ReadExactAsync(stream, header);
        var len = BinaryPrimitives.ReadUInt16LittleEndian(header);
        var payload = new byte[len];
        await ReadExactAsync(stream, payload);
        return payload;
    }

    private async Task<JsonElement> GetAsync(string url)
        => JsonSerializer.Deserialize<JsonElement>(
            await (await _http.GetAsync(url)).Content.ReadAsStreamAsync());

    private async Task<(HttpStatusCode Status, JsonElement Body)> PutAsync(string url, object body)
    {
        var resp = await _http.PutAsJsonAsync(url, body);
        return (resp.StatusCode,
            JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStreamAsync()));
    }

    [Fact]
    public async Task 会话快照_字段承载与字节()
    {
        var (a, aId) = await RegisterAsync("r07-a");
        var (b, bId) = await RegisterAsync("r07-b");
        var grant = await AllocateAsync(a, b, bId);
        var sid = grant.RelaySessionId;

        var aUdp = await JoinUdpAsync(sid); // A=UDP 承载
        var bTcp = await JoinTcpAsync(sid); // B=TCP 承载（混合）

        // A → B 一帧（UDP 入 → TCP 出）：会话级字节 96
        var payload = RandomGenerator.Bytes(96);
        await aUdp.SendAsync(BuildData(sid, payload), _relayUdp);
        Assert.Equal(payload, await ReadTcpFrameAsync(bTcp.GetStream()));

        var data = (await GetAsync("/api/relay/sessions")).GetProperty("data");
        var session = data.GetProperty("sessions").EnumerateArray().Single();
        Assert.Equal(sid.ToString(), session.GetProperty("sid").GetString()); // u64 字符串承载
        Assert.Equal(aId, session.GetProperty("a").GetProperty("deviceId").GetGuid());
        Assert.Equal(bId, session.GetProperty("b").GetProperty("deviceId").GetGuid());
        Assert.Equal("udp", session.GetProperty("a").GetProperty("carrier").GetString());
        Assert.Equal("tcp", session.GetProperty("b").GetProperty("carrier").GetString()); // 承载表达
        Assert.NotNull(session.GetProperty("a").GetProperty("udpAddr").GetString()); // 已学地址
        Assert.Equal(96, session.GetProperty("bytesForwarded").GetInt64());
        Assert.True(session.GetProperty("lastActivity").GetDateTimeOffset()
            >= session.GetProperty("createdAt").GetDateTimeOffset());
        Assert.False(string.IsNullOrEmpty(session.GetProperty("punchSessionId").GetString())); // 打洞会话回链
    }

    [Fact]
    public async Task PUT配置_写库与限速即时生效_部分更新()
    {
        // 基线现值（种子：开+不限）
        var before = (await GetAsync("/api/relay/config")).GetProperty("data");
        Assert.True(before.GetProperty("relayEnabled").GetBoolean());
        Assert.Equal(0, before.GetProperty("rateLimitBytes").GetInt32());

        // 关开关
        var (status, body) = await PutAsync("/api/relay/config", new { relayEnabled = false });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, body.GetProperty("code").GetInt32());
        Assert.False(body.GetProperty("data").GetProperty("relayEnabled").GetBoolean());
        Assert.Equal(0, body.GetProperty("data").GetProperty("rateLimitBytes").GetInt32()); // 未提供不动

        // 限速（部分更新：开关保持关）
        (status, body) = await PutAsync("/api/relay/config", new { rateLimitBytes = 65536 });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body.GetProperty("data").GetProperty("relayEnabled").GetBoolean());
        Assert.Equal(65536, body.GetProperty("data").GetProperty("rateLimitBytes").GetInt32());
        Assert.Equal(65536, _limiter.RateBytesPerSec); // 进程内直调即时生效

        await using (var db = CreateDb())
        {
            var cfg = db.ServerConfig.ToDictionary(c => c.Key, c => c.Value);
            Assert.Equal("0", cfg["relay_enabled"]);
            Assert.Equal("65536", cfg["relay_rate_limit"]); // 持久化（重启口径）
            Assert.Contains(db.AuditLogs.AsNoTracking(),
                l => l.Event == "relay_config_change" && l.Detail!.Contains("65536")); // 审计行
        }

        // 恢复（组合更新）
        (status, body) = await PutAsync("/api/relay/config",
            new { relayEnabled = true, rateLimitBytes = 0 });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("data").GetProperty("relayEnabled").GetBoolean());
        Assert.Equal(0, _limiter.RateBytesPerSec);
    }

    [Fact]
    public async Task PUT配置_非法值_400()
    {
        var (status, body) = await PutAsync("/api/relay/config", new { rateLimitBytes = -1 });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(1001, body.GetProperty("code").GetInt32());

        (status, body) = await PutAsync("/api/relay/config", new { });
        Assert.Equal(HttpStatusCode.BadRequest, status); // 至少一项
        Assert.Equal(1001, body.GetProperty("code").GetInt32());

        // 拒绝路径不动库值
        var current = (await GetAsync("/api/relay/config")).GetProperty("data");
        Assert.True(current.GetProperty("relayEnabled").GetBoolean());
        Assert.Equal(0, current.GetProperty("rateLimitBytes").GetInt32());
    }
}

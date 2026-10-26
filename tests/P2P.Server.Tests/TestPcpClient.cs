using System.IO.Pipelines;
using System.Text;
using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using P2P.Core.Utils;
using P2P.Server.Data;

namespace P2P.Server.Tests;

// 测试桩（Server.Tests 内共享）：共享连接的 IDbContextFactory 与最小 PCP 客户端。

internal sealed class StubFactory(Func<AppDbContext> create) : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext() => create();
    public Task<AppDbContext> CreateDbContextAsync(CancellationToken ct = default)
        => Task.FromResult(create());
}

/// <summary>最小 PCP 客户端桩：分帧 + 可选 HMAC 签名/验签，辅助方法复现握手/注册时序。</summary>
internal sealed class TestPcpClient : IAsyncDisposable
{
    /// <summary>信令推送等待上限（2026-10-26 CI 实证：并行夹具满载下 0x70→0x71 推送 15s 静默超时
    /// 一次——本地与同 run 其余 302 用例全绿，纯调度长尾无确定性缺陷）：15s→30s 慢机余量。
    /// 仅失败路径付出等待，绿灯路径零开销。</summary>
    internal const int PushWaitMs = 30000;

    private readonly TcpClient _tcp;
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    private uint _seq;

    private TestPcpClient(TcpClient tcp)
    {
        _tcp = tcp;
        _reader = PipeReader.Create(tcp.GetStream());
        _writer = PipeWriter.Create(tcp.GetStream());
    }

    public byte[] NonceC { get; private set; } = [];
    public byte[] NonceS { get; private set; } = [];
    public byte[] ConnMacKey { get; private set; } = [];
    /// <summary>自此服务端入站消息全部签名（ProofAck(true)/RegisterAck 后手动置位）。</summary>
    public bool IncomingSigned { get; set; }
    /// <summary>消息时间戳时钟（默认系统真实时钟）：假时钟夹具注入后与服务端 TsWindow 对齐（M2-19）。</summary>
    public TimeProvider? Time { get; set; }

    /// <summary>超时取证对端（2026-10-26 CI 瞬态取证缝）：错误路径（0x7E handler_error/4005/5005 等）
    /// 的测试表象即「推送静默超时」——拒因帧落在发起方通道上，不在等待方。接线后本端等待超时
    /// 即转储本端迟帧 + 对端在途帧进异常消息。不接线=零行为变化（AssertSilent 类「预期静默」
    /// 用例不受取证读帧干扰）。</summary>
    public TestPcpClient? DiagnosticPeer { get; set; }

    public static async Task<TestPcpClient> ConnectAsync(IPEndPoint endpoint)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(endpoint);
        tcp.NoDelay = true;
        return new TestPcpClient(tcp);
    }

    public uint NextSeq() => ++_seq;
    public ulong Now() => (ulong)(Time?.GetLocalNow() ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();

    public async Task SendAsync<T>(T message, bool sign = true, byte[]? keyOverride = null)
        where T : class, IPcpMessage
    {
        var key = keyOverride ?? ConnMacKey;
        var body = sign && key.Length > 0 ? PcpCodec.EncodeSigned(message, key) : PcpCodec.Encode(message);
        SentWire.Add(body); // 帧转储（M5-06 A-16 探针：断言报文不含明文密码子串）
        await FrameCodec.WriteFrameAsync(_writer, body);
    }

    /// <summary>已发帧转储（msgpack 本体，签名前后均为线上字节；A-16 明文探针用）。</summary>
    public List<byte[]> SentWire { get; } = [];

    public async Task SendRawAsync(byte[] frameBody)
        => await FrameCodec.WriteFrameAsync(_writer, frameBody);

    public async Task<T?> ReceiveAsync<T>(int timeoutMs = 5000) where T : class, IPcpMessage
    {
        var body = await ReadFrameOrThrow<T>(timeoutMs);
        if (body is null) return null;
        var msgpack = IncomingSigned ? PcpCodec.DecodeSigned(body, ConnMacKey) : body;
        return PcpCodec.Decode<T>(msgpack);
    }

    /// <summary>接收指定类型应答，途中交错的 0x41 列表推送帧跳过（M2-10：服务端异步提示帧与
    /// 请求-应答流同连接交错，严格接收会误吞应答前的推送帧）。skipInvalidation：连 0x75 一并跳过——
    /// 容忍 PresenceMonitor 兜底扫描与进程内即时路径的双推竞态（M2-13 设计固有：扫描落在
    /// 「写库后/内存降级前」窗口即补推同载荷 0x75，客户端消费幂等——DisableUser 家族用）。
    /// skipPunchFire：0x77 齐射锚点一并跳过（§5.2⑤：B 回 0x76 后服务端恒随 Ack 同刻推送 0x77，
    /// 0x76 后 B 的下一次类型化接收须跳过该帧才能见到后续消息）。</summary>
    public async Task<T?> ReceiveSkippingPushesAsync<T>(int timeoutMs = 5000, bool skipInvalidation = false,
        bool skipPunchFire = false) where T : class, IPcpMessage
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var remaining = (int)Math.Max(0, deadline - Environment.TickCount64);
            var body = await ReadFrameOrThrow<T>(remaining);
            if (body is null) return null;
            var msgpack = IncomingSigned ? PcpCodec.DecodeSigned(body, ConnMacKey) : body;
            var type = PcpCodec.Peek(msgpack).MsgType;
            if (type == MsgType.DeviceListUpdate) continue;
            if (skipInvalidation && type == MsgType.Invalidation) continue;
            if (skipPunchFire && type == MsgType.PunchFire) continue;
            return PcpCodec.Decode<T>(msgpack);
        }
    }

    /// <summary>等待连接关闭（EOF/IO 错均算）；超时返回 false。跳过在途消息。</summary>
    public async Task<bool> WaitClosedAsync(int timeoutMs = 5000)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            while (await FrameCodec.ReadFrameAsync(_reader, cts.Token) is not null) { }
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception) { return true; } // IOException/InvalidDataException = 断连
    }

    public async Task<HelloAck> HelloAsync(Guid? deviceId, ushort version = ProtocolVersion.Current)
    {
        NonceC = RandomGenerator.Bytes(16);
        await SendAsync(new Hello(NextSeq(), Now(), MsgType.Hello, version, deviceId, NonceC), sign: false);
        var ack = await ReceiveAsync<HelloAck>() ?? throw new IOException("连接在 HelloAck 前关闭");
        NonceS = ack.NonceS;
        LoginEphPub = ack.LoginEphPub; // M5-06/SEC-26：登录族加密公钥（07 §10.2）
        return ack;
    }

    /// <summary>本连接登录加密公钥（HelloAck 携带；旧服务端缺失）。</summary>
    public byte[]? LoginEphPub { get; private set; }

    /// <summary>登录族密码 ECIES 封装（M5-06：info="pcp-login-v1"，与生产客户端同口径）。</summary>
    public byte[] BoxLoginSecret(string secret)
        => Ecies.Encrypt(LoginEphPub!, Encoding.UTF8.GetBytes(secret), "pcp-login-v1"u8);

    public async Task<ProofAck> ProofAsync(byte[] deviceSecret, bool valid = true)
    {
        ConnMacKey = Hkdf.Derive(deviceSecret, [.. NonceC, .. NonceS], "pcp-mac"u8, 32);
        var input = NonceC.Concat(NonceS).ToArray();
        var hmac = valid ? Mac.HmacSha256(deviceSecret, input) : RandomGenerator.Bytes(Mac.HashLen);
        await SendAsync(new Proof(NextSeq(), Now(), MsgType.Proof, hmac), sign: false);
        // 仅成功路径自此签名（失败 ProofAck(false) 未签名——共享密钥未经双方确认，02 §2.3）
        if (valid) IncomingSigned = true;
        return await ReceiveAsync<ProofAck>() ?? throw new IOException("连接在 ProofAck 前关闭");
    }

    /// <summary>注册完成（ECIES 解出 deviceSecret 后）转入签名会话。</summary>
    public void EstablishWithSecret(byte[] deviceSecret)
    {
        ConnMacKey = Hkdf.Derive(deviceSecret, [.. NonceC, .. NonceS], "pcp-mac"u8, 32);
        IncomingSigned = true;
    }

    private async Task<byte[]?> ReadFrameOrThrow<T>(int timeoutMs) where T : class, IPcpMessage
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            return await FrameCodec.ReadFrameAsync(_reader, cts.Token);
        }
        catch (OperationCanceledException)
        {
            var detail = DiagnosticPeer is null ? "" : await DescribeTimeoutSceneAsync();
            throw new TimeoutException($"未在 {timeoutMs}ms 内收到 {typeof(T).Name}{detail}");
        }
    }

    /// <summary>超时现场取证（诊断缝，仅 DiagnosticPeer 接线时）：本端迟帧（迟到即证明调度长尾
    /// 而非消息缺失）+ 对端在途帧（错误路径拒因）。读窗 800ms/端；取证自身失败不掩盖原超时。</summary>
    private async Task<string> DescribeTimeoutSceneAsync()
    {
        var own = await DumpFramesAsync(800);
        var peerFrames = "（未接线）";
        if (DiagnosticPeer is not null)
        {
            try
            {
                var peer = await DiagnosticPeer.DumpFramesAsync(800);
                peerFrames = peer.Count == 0 ? "无" : string.Join(" | ", peer);
            }
            catch { peerFrames = "（取证失败）"; }
        }
        return $"；现场：本端迟帧 {(own.Count == 0 ? "无" : string.Join(" | ", own))}；对端在途帧 {peerFrames}";
    }

    public async ValueTask DisposeAsync()
    {
        _reader.Complete();
        _writer.Complete();
        _tcp.Dispose();
        await ValueTask.CompletedTask;
    }

    /// <summary>读一帧并描述（不校验类型；超时返回 null）。排干/诊断用：0x7E 展开错误码与原因。</summary>
    public async Task<string?> ReceiveDescribeAsync(int timeoutMs = 400)
    {
        byte[]? body;
        using var cts = new CancellationTokenSource(timeoutMs);
        try { body = await FrameCodec.ReadFrameAsync(_reader, cts.Token); }
        catch (OperationCanceledException) { return null; }
        if (body is null) return "<EOF>";
        try
        {
            var msgpack = IncomingSigned ? PcpCodec.DecodeSigned(body, ConnMacKey) : body;
            var h = PcpCodec.Peek(msgpack);
            if (h.MsgType == MsgType.Error)
            {
                var e = PcpCodec.Decode<ErrorMessage>(msgpack);
                return $"0x{h.MsgType:X2} seq={h.Seq} ERROR code={e.Code} msg={e.HttpLikeMsg}";
            }
            return $"0x{h.MsgType:X2} seq={h.Seq}";
        }
        catch (Exception ex) { return $"解码失败: {ex.Message}"; }
    }

    /// <summary>转储后续帧（类型 + ErrorMessage 内容）：夹具竞态类失败现场取证。</summary>
    public async Task<List<string>> DumpFramesAsync(int timeoutMs = 2500)
    {
        var frames = new List<string>();
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var remaining = (int)Math.Max(0, deadline - Environment.TickCount64);
            if (remaining == 0) return frames;
            byte[]? body;
            using var cts = new CancellationTokenSource(remaining);
            try { body = await FrameCodec.ReadFrameAsync(_reader, cts.Token); }
            catch (OperationCanceledException) { return frames; }
            if (body is null) { frames.Add("<EOF>"); return frames; }
            try
            {
                var msgpack = IncomingSigned ? PcpCodec.DecodeSigned(body, ConnMacKey) : body;
                var h = PcpCodec.Peek(msgpack);
                if (h.MsgType == MsgType.Error)
                {
                    var e = PcpCodec.Decode<ErrorMessage>(msgpack);
                    frames.Add($"0x{h.MsgType:X2} seq={h.Seq} ERROR code={e.Code} msg={e.HttpLikeMsg}");
                }
                else frames.Add($"0x{h.MsgType:X2} seq={h.Seq}");
            }
            catch (Exception ex) { frames.Add($"解码失败: {ex.Message}"); }
        }
    }
}

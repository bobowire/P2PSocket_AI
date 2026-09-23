using System.IO.Pipelines;
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
        await FrameCodec.WriteFrameAsync(_writer, body);
    }

    public async Task SendRawAsync(byte[] frameBody)
        => await FrameCodec.WriteFrameAsync(_writer, frameBody);

    public async Task<T?> ReceiveAsync<T>(int timeoutMs = 5000) where T : class, IPcpMessage
    {
        var body = await ReadFrameOrThrow<T>(timeoutMs);
        if (body is null) return null;
        var msgpack = IncomingSigned ? PcpCodec.DecodeSigned(body, ConnMacKey) : body;
        return PcpCodec.Decode<T>(msgpack);
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
        return ack;
    }

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
            throw new TimeoutException($"未在 {timeoutMs}ms 内收到 {typeof(T).Name}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _reader.Complete();
        _writer.Complete();
        _tcp.Dispose();
        await ValueTask.CompletedTask;
    }
}

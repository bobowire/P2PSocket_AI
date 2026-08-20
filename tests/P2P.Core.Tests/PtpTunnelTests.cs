using System.Security.Cryptography;
using P2P.Core.Crypto;
using P2P.Core.Tunnel;
using Xunit;
using MessagePack;
using P2P.Core.Protocol;

namespace P2P.Core.Tests;

public class PtpTunnelTests
{
    // ── 帧编解码 ─────────────────────────────────────────────────────────

    [Fact]
    public void Frame_SealOpen_RoundTrip()
    {
        var key = RandomGenerator.Bytes(32);
        var plain = new byte[] { 1, 2, 3, 4, 5 };
        var wire = PtpFrameCodec.Seal(PtpFrameType.Data, channelId: 0x11223344, counter: 7, plain, key);

        var header = PtpFrameCodec.ParseHeader(wire);
        Assert.Equal(1, header.Ver);
        Assert.Equal(PtpFrameType.Data, header.Type);
        Assert.Equal(0x11223344u, header.ChannelId);
        Assert.Equal(7UL, header.Counter);
        Assert.Equal(plain.Length + 16, header.CipherLen);

        var opened = PtpFrameCodec.Open(wire, key);
        Assert.True(plain.AsSpan().SequenceEqual(opened));
    }

    [Fact]
    public void Frame_Tamper_Rejected()
    {
        var key = RandomGenerator.Bytes(32);
        var wire = PtpFrameCodec.Seal(PtpFrameType.Data, 1, 1, new byte[] { 9 }, key);
        wire[^1] ^= 0x01;
        Assert.ThrowsAny<CryptographicException>(() => PtpFrameCodec.Open(wire, key));
    }

    [Fact]
    public void Frame_HeaderTamperAsAad_Rejected()
    {
        // 头部明文是 AAD：改 channelId 同样解密失败（防头部篡改，02 §4.2）
        var key = RandomGenerator.Bytes(32);
        var wire = PtpFrameCodec.Seal(PtpFrameType.Data, 1, 1, new byte[] { 9 }, key);
        wire[2] ^= 0x01;
        Assert.ThrowsAny<CryptographicException>(() => PtpFrameCodec.Open(wire, key));
    }

    [Fact]
    public void Frame_WrongDirectionKey_Rejected()
    {
        var key = RandomGenerator.Bytes(32);
        var other = RandomGenerator.Bytes(32);
        var wire = PtpFrameCodec.Seal(PtpFrameType.Data, 1, 1, new byte[] { 1 }, key);
        Assert.ThrowsAny<CryptographicException>(() => PtpFrameCodec.Open(wire, other));
    }

    [Fact]
    public void Frame_WrongCounterAsNonce_Rejected()
    {
        // nonce=counter：同一密文换 counter 位置重放解不开
        var key = RandomGenerator.Bytes(32);
        var wire = PtpFrameCodec.Seal(PtpFrameType.Data, 1, counter: 5, new byte[] { 1 }, key);
        var forged = wire.ToArray();
        forged[6] = 6; // counter 低字节 +1
        Assert.ThrowsAny<CryptographicException>(() => PtpFrameCodec.Open(forged, key));
    }

    [Fact]
    public void Frame_BadVersion_Rejected()
    {
        var wire = PtpFrameCodec.BuildHandshake(PtpFrameType.THello1, new byte[97]);
        wire[0] = 2;
        Assert.Throws<ProtocolException>(() => PtpFrameCodec.ParseHeader(wire));
    }

    [Fact]
    public void OpenPayload_RoundTrip()
    {
        var payload = new OpenPayload("tcp", "self", 3389);
        var decoded = MessagePackSerializer.Deserialize<OpenPayload>(MessagePackSerializer.Serialize(payload));
        Assert.Equal("self", decoded.TargetAddr);
        Assert.Equal(3389, decoded.TargetPort);

        var result = new OpenResultPayload(false, "connect_refused");
        var decodedResult = MessagePackSerializer.Deserialize<OpenResultPayload>(MessagePackSerializer.Serialize(result));
        Assert.False(decodedResult.Ok);
        Assert.Equal("connect_refused", decodedResult.FailReason);
    }

    // ── 防重放窗口（SEC-12）──────────────────────────────────────────────

    [Fact]
    public void ReplayWindow_NormalSequence_AllAccepted()
    {
        var w = new ReplayWindow();
        for (var i = 1UL; i <= 200; i++)
            Assert.True(w.Accept(i));
    }

    [Fact]
    public void ReplayWindow_Duplicate_Rejected()
    {
        var w = new ReplayWindow();
        Assert.True(w.Accept(10));
        Assert.False(w.Accept(10));
        Assert.True(w.Accept(11));
        Assert.False(w.Accept(11));
    }

    [Fact]
    public void ReplayWindow_Regress_RejectedWithinWindow()
    {
        var w = new ReplayWindow();
        Assert.True(w.Accept(100));
        Assert.True(w.Accept(99));   // 窗口内乱序（合法：迟达）
        Assert.False(w.Accept(99));  // 重复
        Assert.False(w.Accept(100)); // 与最高值重复
        Assert.True(w.Accept(98));
    }

    [Fact]
    public void ReplayWindow_BeyondWindow_Rejected()
    {
        var w = new ReplayWindow();
        Assert.True(w.Accept(100));
        Assert.False(w.Accept(100 - ReplayWindow.WindowSize)); // 恰出窗
        Assert.True(w.Accept(90));                              // 窗口内
    }

    [Fact]
    public void ReplayWindow_Zero_Rejected()
    {
        Assert.False(new ReplayWindow().Accept(0)); // 0 保留给握手帧
    }

    // ── 双重 ECDH 握手（SEC-12/13）───────────────────────────────────────

    private static (PtpHandshake.Initiator A, PtpHandshake.Responder B, EcKeyPair StaticA, EcKeyPair StaticB)
        NewPair(Guid sessionId)
    {
        var staticA = EcKeyPair.Generate();
        var staticB = EcKeyPair.Generate();
        var a = PtpHandshake.StartInitiator(sessionId, staticA, staticB.ExportPublicKey());
        var b = PtpHandshake.AcceptTHello1(a.THello1Wire, staticB, staticA.ExportPublicKey());
        return (a, b, staticA, staticB);
    }

    [Fact]
    public void Handshake_FullFlow_BothSidesSameKeys()
    {
        var sessionId = Guid.NewGuid();
        var (a, b, _, _) = NewPair(sessionId);

        var (tConfirm, keysA) = a.HandleTHello2(b.THello2Wire);
        var keysB = b.VerifyTConfirm(tConfirm);

        Assert.Equal(sessionId, keysA.SessionId);
        Assert.Equal(sessionId, keysB.SessionId);
        Assert.True(keysA.KeyAtoB.AsSpan().SequenceEqual(keysB.KeyAtoB));
        Assert.True(keysA.KeyBtoA.AsSpan().SequenceEqual(keysB.KeyBtoA));
    }

    [Fact]
    public void Handshake_DirectionalKeys_Independent()
    {
        var (a, b, _, _) = NewPair(Guid.NewGuid());
        var (_, keysA) = a.HandleTHello2(b.THello2Wire);
        var keysB = b.SessionKeys;
        Assert.False(keysA.KeyAtoB.AsSpan().SequenceEqual(keysA.KeyBtoA));

        // A 用发送密钥（A→B）封帧：B 以接收密钥可解，以自己的发送密钥（B→A）解不开
        var frame = PtpFrameCodec.Seal(PtpFrameType.Data, 1, 1, new byte[] { 1 }, keysA.SendKey(isInitiator: true));
        var opened = PtpFrameCodec.Open(frame, keysB.ReceiveKey(isInitiator: false));
        Assert.Single(opened);
        Assert.ThrowsAny<CryptographicException>(
            () => PtpFrameCodec.Open(frame, keysB.SendKey(isInitiator: false)));
    }

    [Fact]
    public void Handshake_TamperedTHello2_Rejected()
    {
        var (a, b, _, _) = NewPair(Guid.NewGuid());
        var wire = b.THello2Wire.ToArray();
        wire[^1] ^= 0x01; // 篡改 mac2
        Assert.Throws<ProtocolException>(() => a.HandleTHello2(wire));
    }

    [Fact]
    public void Handshake_TamperedTConfirm_Rejected()
    {
        var (a, b, _, _) = NewPair(Guid.NewGuid());
        var (tConfirm, _) = a.HandleTHello2(b.THello2Wire);
        tConfirm[^1] ^= 0x01;
        Assert.Throws<ProtocolException>(() => b.VerifyTConfirm(tConfirm));
    }

    [Fact]
    public void Handshake_WrongPeerStaticKey_Rejected()
    {
        // A 以为对端是 C，实际 B 用自己的身份应答 → 静态绑定失败（防冒充，SEC-13）
        var sessionId = Guid.NewGuid();
        var staticA = EcKeyPair.Generate();
        var staticB = EcKeyPair.Generate();
        var staticC = EcKeyPair.Generate(); // A 持有的对端公钥是 C 的

        var a = PtpHandshake.StartInitiator(sessionId, staticA, staticC.ExportPublicKey());
        var b = PtpHandshake.AcceptTHello1(a.THello1Wire, staticB, staticA.ExportPublicKey());
        Assert.Throws<ProtocolException>(() => a.HandleTHello2(b.THello2Wire));
    }

    [Fact]
    public void Handshake_EachSessionFreshKeys()
    {
        // 临时-临时 ECDH：两次握手（不同 eph）密钥不同（会话独立性）
        var (a1, b1, staticA, staticB) = NewPair(Guid.NewGuid());
        var (_, keys1) = a1.HandleTHello2(b1.THello2Wire);

        var a2 = PtpHandshake.StartInitiator(Guid.NewGuid(), staticA, staticB.ExportPublicKey());
        var b2 = PtpHandshake.AcceptTHello1(a2.THello1Wire, staticB, staticA.ExportPublicKey());
        var (_, keys2) = a2.HandleTHello2(b2.THello2Wire);

        Assert.False(keys1.KeyAtoB.AsSpan().SequenceEqual(keys2.KeyAtoB));
    }
}

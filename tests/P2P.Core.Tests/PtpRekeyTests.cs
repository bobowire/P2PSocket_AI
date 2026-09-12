using System.Security.Cryptography;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using P2P.Core.Tunnel;
using Xunit;

namespace P2P.Core.Tests;

/// <summary>M2-05 REKEY/WINDOW 帧协议层——轮换密钥派生、排水窗口、信用账本（完成判定全两条）。</summary>
public class PtpRekeyTests
{
    private static readonly Guid SessionId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");

    /// <summary>完整双重 ECDH 握手得原会话密钥（REKEY 的新钥须与之不同且同构可加解密）。</summary>
    private static (PtpSessionKeys OldKeys, EcKeyPair StaticA, EcKeyPair StaticB) Handshake()
    {
        var staticA = EcKeyPair.Generate();
        var staticB = EcKeyPair.Generate();
        using var initiator = PtpHandshake.StartInitiator(SessionId, staticA, staticB.ExportPublicKey());
        using var responder = PtpHandshake.AcceptTHello1(initiator.THello1Wire, staticB, staticA.ExportPublicKey());
        var (confirm, keysA) = initiator.HandleTHello2(responder.THello2Wire);
        var keysB = responder.VerifyTConfirm(confirm);
        Assert.True(keysA.KeyAtoB.AsSpan().SequenceEqual(keysB.KeyAtoB));
        return (keysA, staticA, staticB);
    }

    // ── REKEY 往返：新钥双方一致、可加解密、与旧钥不同（完成判定①前半）────────

    [Fact]
    public void Rekey_BothSidesDeriveSameNewKeys_UsableForAead()
    {
        var (oldKeys, staticA, staticB) = Handshake();

        using var rekeyA = PtpRekey.Start(SessionId, staticA, staticB.ExportPublicKey());
        using var rekeyB = PtpRekey.Accept(rekeyA.RekeyPayload, SessionId, staticB, staticA.ExportPublicKey());
        var newKeysA = rekeyA.HandleAck(rekeyB.AckPayload);

        // 双方新钥一致（A→B / B→A 双方向）
        Assert.True(newKeysA.KeyAtoB.AsSpan().SequenceEqual(rekeyB.NewKeys.KeyAtoB));
        Assert.True(newKeysA.KeyBtoA.AsSpan().SequenceEqual(rekeyB.NewKeys.KeyBtoA));

        // 新钥与旧钥不同（轮换确实发生）
        Assert.False(newKeysA.KeyAtoB.AsSpan().SequenceEqual(oldKeys.KeyAtoB));
        Assert.False(newKeysA.KeyBtoA.AsSpan().SequenceEqual(oldKeys.KeyBtoA));

        // 新钥加解密生效（A→B 发、B 以新收钥解；反方向同验）
        var frame = PtpFrameCodec.Seal(PtpFrameType.Data, channelId: 7, counter: 100,
            "rekey-payload"u8.ToArray(), newKeysA.KeyAtoB);
        Assert.Equal("rekey-payload"u8.ToArray(), PtpFrameCodec.Open(frame, rekeyB.NewKeys.KeyAtoB));

        var frameBack = PtpFrameCodec.Seal(PtpFrameType.Data, 7, 101,
            "ack-ok"u8.ToArray(), rekeyB.NewKeys.KeyBtoA);
        Assert.Equal("ack-ok"u8.ToArray(), PtpFrameCodec.Open(frameBack, newKeysA.KeyBtoA));

        // 旧钥帧在新钥-only 环境不可解（密钥确已切换；.NET 10 AesGcm 抛 AuthenticationTagMismatchException ⊂ CryptographicException）
        Assert.ThrowsAny<CryptographicException>(
            () => PtpFrameCodec.Open(frame, oldKeys.KeyAtoB));
    }

    [Fact]
    public void Rekey_NewKeysDifferAcrossRekeyRounds()
    {
        // 两次连续轮换产生不同密钥（eph 随机性 + nonce 随机性）
        var (_, staticA, staticB) = Handshake();

        using var r1A = PtpRekey.Start(SessionId, staticA, staticB.ExportPublicKey());
        using var r1B = PtpRekey.Accept(r1A.RekeyPayload, SessionId, staticB, staticA.ExportPublicKey());
        var k1 = r1A.HandleAck(r1B.AckPayload);

        using var r2A = PtpRekey.Start(SessionId, staticA, staticB.ExportPublicKey());
        using var r2B = PtpRekey.Accept(r2A.RekeyPayload, SessionId, staticB, staticA.ExportPublicKey());
        var k2 = r2A.HandleAck(r2B.AckPayload);

        Assert.False(k1.KeyAtoB.AsSpan().SequenceEqual(k2.KeyAtoB));
    }

    [Fact]
    public void Rekey_PayloadLengthValidated()
    {
        var (_, staticA, staticB) = Handshake();
        using var a = PtpRekey.Start(SessionId, staticA, staticB.ExportPublicKey());

        Assert.Throws<ProtocolException>(() => a.HandleAck(new byte[PtpRekey.PayloadLen - 1]));
        Assert.Throws<ProtocolException>(() => PtpRekey.Accept(
            new byte[PtpRekey.PayloadLen + 1], SessionId, staticB, staticA.ExportPublicKey()));
    }

    // ── 排水窗口：旧钥窗内尾帧可解、超窗拒收（完成判定①后半）────────────────

    private static byte[] DataFrame(ulong counter, byte[] key)
        => PtpFrameCodec.Seal(PtpFrameType.Data, channelId: 3, counter: counter, [1, 2, 3], key);

    [Fact]
    public void KeyRing_OldKeyFrameOpens_WithinDrainWindow()
    {
        var time = new FakeTimeProvider();
        var oldKey = RandomGenerator.Bytes(32);
        var newKey = RandomGenerator.Bytes(32);
        using var ring = new PtpReceiveKeyRing(oldKey, time);

        var oldFrame = DataFrame(50, oldKey);
        var newFrame = DataFrame(51, newKey);
        ring.Rotate(newKey);
        Assert.Equal(1, ring.Generation);

        // 切换后 1.5s：旧钥尾帧可解（排水），新钥帧照常
        time.Advance(TimeSpan.FromSeconds(1.5));
        Assert.True(ring.TryOpen(oldFrame, out var plainOld));
        Assert.Equal([1, 2, 3], plainOld);
        Assert.True(ring.TryOpen(newFrame, out _));
    }

    [Fact]
    public void KeyRing_OldKeyFrameRejected_AfterWindowExpiry()
    {
        var time = new FakeTimeProvider();
        var oldKey = RandomGenerator.Bytes(32);
        var newKey = RandomGenerator.Bytes(32);
        using var ring = new PtpReceiveKeyRing(oldKey, time);

        var oldFrame = DataFrame(60, oldKey);
        var newFrame = DataFrame(61, newKey);
        ring.Rotate(newKey);

        time.Advance(TimeSpan.FromSeconds(2.1)); // 超过 2s 排水窗口
        Assert.False(ring.TryOpen(oldFrame, out _)); // 旧钥已销毁：拒收
        Assert.True(ring.TryOpen(newFrame, out _));  // 现钥不受影响
    }

    [Fact]
    public void KeyRing_BothKeysFail_TamperedFrameRejected()
    {
        var time = new FakeTimeProvider();
        using var ring = new PtpReceiveKeyRing(RandomGenerator.Bytes(32), time);
        ring.Rotate(RandomGenerator.Bytes(32));

        // 合法帧结构但以无关密钥密封（模拟篡改/错钥）
        var stranger = DataFrame(70, RandomGenerator.Bytes(32));
        Assert.False(ring.TryOpen(stranger, out _));
    }

    [Fact]
    public void KeyRing_DoubleRotate_KeepsOnlyOnePrevious()
    {
        var time = new FakeTimeProvider();
        var k0 = RandomGenerator.Bytes(32);
        var k1 = RandomGenerator.Bytes(32);
        var k2 = RandomGenerator.Bytes(32);
        using var ring = new PtpReceiveKeyRing(k0, time);

        var frame0 = DataFrame(80, k0);
        ring.Rotate(k1);
        ring.Rotate(k2); // k0 被挤掉销毁——至多保留一代旧钥
        Assert.Equal(2, ring.Generation);

        Assert.False(ring.TryOpen(frame0, out _));      // 隔代旧钥不可解
        Assert.True(ring.TryOpen(DataFrame(81, k1), out _)); // 上一代仍在窗内
        Assert.True(ring.TryOpen(DataFrame(82, k2), out _));
    }

    // ── WINDOW 信用帧与账本语义（完成判定②）───────────────────────────────

    [Fact]
    public void WindowCreditPayload_RoundTripThroughSealedFrame()
    {
        var key = RandomGenerator.Bytes(32);
        var payload = new WindowCreditPayload(CreditBytes: 4096);
        var plain = MessagePack.MessagePackSerializer.Serialize(payload);

        var frame = PtpFrameCodec.Seal(PtpFrameType.Window, channelId: 9, counter: 200, plain, key);
        var opened = MessagePack.MessagePackSerializer.Deserialize<WindowCreditPayload>(PtpFrameCodec.Open(frame, key));
        Assert.Equal(4096u, opened.CreditBytes);

        // 帧类型合法性与头解析（0x0A 在业务帧区间）
        var header = PtpFrameCodec.ParseHeader(frame);
        Assert.Equal(PtpFrameType.Window, header.Type);
        Assert.Equal(9u, header.ChannelId);
    }

    [Fact]
    public void CreditWindow_ConsumeUntilExhausted_GrantResumes_CappedAtCapacity()
    {
        var window = new CreditWindow(); // 默认 64KiB（05 §2.3）
        Assert.Equal(64 * 1024, CreditWindow.DefaultCapacity);
        Assert.Equal(CreditWindow.DefaultCapacity, window.Available);

        // 整窗扣完 → 耗尽
        Assert.True(window.TryConsume(16 * 1024));
        Assert.True(window.TryConsume(48 * 1024));
        Assert.True(window.Exhausted);

        // 耗尽后：不足不部分扣
        Assert.False(window.TryConsume(1));
        Assert.Equal(0, window.Available);

        // 对端回报恢复
        window.Grant(1024);
        Assert.Equal(1024, window.Available);
        Assert.False(window.Exhausted);
        Assert.True(window.TryConsume(1024));
        Assert.True(window.Exhausted);

        // 迟到/重复回报封顶：不放大窗口
        window.Grant(CreditWindow.DefaultCapacity * 2);
        Assert.Equal(CreditWindow.DefaultCapacity, window.Available);
    }

    [Fact]
    public void CreditWindow_PartialConsume_LeavesAvailableIntact()
    {
        var window = new CreditWindow(1024);
        Assert.True(window.TryConsume(300));
        Assert.False(window.TryConsume(1000)); // 超出可用：拒绝且不部分扣
        Assert.Equal(724, window.Available);
        Assert.True(window.TryConsume(724));
        Assert.True(window.Exhausted);
    }

    [Fact]
    public void CreditWindow_InvalidArgs_Rejected()
    {
        var window = new CreditWindow(64);
        Assert.Throws<ArgumentOutOfRangeException>(() => window.TryConsume(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => window.Grant(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CreditWindow(0));
    }
}

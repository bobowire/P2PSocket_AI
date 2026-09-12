using System.Security.Cryptography;
using P2P.Core.Crypto;
using P2P.Core.Protocol;

namespace P2P.Core.Tunnel;

/// <summary>
/// PTP 密钥轮换（02 §4.4、SEC-14）。REKEY(0x08)/REKEY_ACK 承载于**现会话密钥** AEAD 内（密文业务帧，
/// 通道本身已认证——区别于明文期的 THello2/TConfirm，无需另加握手 mac）。
/// 载荷布局（定长 81B，两向同构）：新 eph 公钥(65) | 新 nonce(16)；
/// 新会话密钥按 §4.1 同式派生：双重 ECDH（ECDH(ephA',ephB')‖静态共享，salt=sessionId|nonceA'|nonceB'，
/// sessionId 不变——轮换不重建隧道）。TTL 定时/手动触发与排水切换编排属 TunnelSession（M2-21）。
/// </summary>
public static class PtpRekey
{
    public const int PubLen = PtpHandshake.PubLen;
    public const int NonceLen = PtpHandshake.NonceLen;
    public const int PayloadLen = PubLen + NonceLen; // REKEY 与 REKEY_ACK 同构

    /// <summary>发起方（会话发起方 A，02 §4.4）状态：构造即产出 REKEY 载荷；收到 ACK 派生新钥。</summary>
    public sealed class Initiator : IDisposable
    {
        private readonly Guid _sessionId;
        private readonly EcKeyPair _ephA;
        private readonly byte[] _nonceA;
        private readonly byte[] _staticShared;

        internal Initiator(Guid sessionId, EcKeyPair staticA, ReadOnlySpan<byte> peerStaticPub)
        {
            _sessionId = sessionId;
            _ephA = EcKeyPair.Generate();
            _nonceA = RandomGenerator.Bytes(NonceLen);
            _staticShared = staticA.DeriveSharedKey(peerStaticPub);

            var payload = new byte[PayloadLen];
            _ephA.ExportPublicKey().CopyTo(payload.AsSpan(0, PubLen));
            _nonceA.CopyTo(payload.AsSpan(PubLen));
            RekeyPayload = payload;
        }

        /// <summary>明文载荷；调用方以现会话发送钥 Seal 为 0x08 帧发出。</summary>
        public byte[] RekeyPayload { get; }

        /// <summary>解析 REKEY_ACK（B 的新 eph 公钥与 nonce）→ 新会话密钥（此后双方切换、旧钥进入排水窗口）。</summary>
        public PtpSessionKeys HandleAck(ReadOnlySpan<byte> ackPayload)
        {
            var (ephBPub, nonceB) = Split(ackPayload);
            var ephShared = _ephA.DeriveSharedKey(ephBPub);
            var (aToB, bToA) = TunnelKeyDerivation.DeriveSessionKeys(
                ephShared, _staticShared, _sessionId.ToByteArray(), _nonceA, nonceB);
            return new PtpSessionKeys(_sessionId, aToB, bToA);
        }

        public void Dispose()
        {
            _ephA.Dispose();
            CryptoUtil.Zero(_staticShared);
            CryptoUtil.Zero(_nonceA);
            CryptoUtil.Zero(RekeyPayload);
        }
    }

    /// <summary>响应方（B）状态：解析 REKEY 载荷即派生新钥并产出 ACK 载荷（回 ACK 后切换、旧钥进入排水窗口）。</summary>
    public sealed class Responder : IDisposable
    {
        private readonly EcKeyPair _ephB;
        private readonly byte[] _nonceB;
        private readonly byte[] _staticShared;

        internal Responder(ReadOnlySpan<byte> rekeyPayload, Guid sessionId, EcKeyPair staticB, ReadOnlySpan<byte> peerStaticPubA)
        {
            var (ephAPub, nonceA) = Split(rekeyPayload);
            _staticShared = staticB.DeriveSharedKey(peerStaticPubA);

            _ephB = EcKeyPair.Generate();
            _nonceB = RandomGenerator.Bytes(NonceLen);

            var payload = new byte[PayloadLen];
            _ephB.ExportPublicKey().CopyTo(payload.AsSpan(0, PubLen));
            _nonceB.CopyTo(payload.AsSpan(PubLen));
            AckPayload = payload;

            var ephShared = _ephB.DeriveSharedKey(ephAPub);
            var (aToB, bToA) = TunnelKeyDerivation.DeriveSessionKeys(
                ephShared, _staticShared, sessionId.ToByteArray(), nonceA, _nonceB);
            NewKeys = new PtpSessionKeys(sessionId, aToB, bToA);
        }

        /// <summary>明文载荷；调用方以现会话发送钥 Seal 为 0x08 帧回给发起方。</summary>
        public byte[] AckPayload { get; }

        /// <summary>新会话密钥（解析 REKEY 即已可派生）。</summary>
        public PtpSessionKeys NewKeys { get; }

        public void Dispose()
        {
            _ephB.Dispose();
            CryptoUtil.Zero(_staticShared);
            CryptoUtil.Zero(_nonceB);
            CryptoUtil.Zero(AckPayload);
        }
    }

    public static Initiator Start(Guid sessionId, EcKeyPair staticA, ReadOnlySpan<byte> peerStaticPub)
        => new(sessionId, staticA, peerStaticPub);

    /// <summary>从 REKEY 载荷构造响应方（sessionId 由隧道状态提供——轮换不重建隧道，载荷不携带）。</summary>
    public static Responder Accept(ReadOnlySpan<byte> rekeyPayload, Guid sessionId, EcKeyPair staticB, ReadOnlySpan<byte> peerStaticPubA)
        => new(rekeyPayload, sessionId, staticB, peerStaticPubA);

    private static (byte[] EphPub, byte[] Nonce) Split(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != PayloadLen)
            throw new ProtocolException($"REKEY 载荷长度 {payload.Length} ≠ {PayloadLen}");
        return (payload[..PubLen].ToArray(), payload[PubLen..].ToArray());
    }
}

/// <summary>
/// 接收侧密钥环（02 §4.4 排水语义/NET-75）：REKEY 切换后旧接收密钥保留 2s 窗口——窗内可解在途尾帧，
/// 超窗销毁（置零）后旧钥帧不可解；现钥与旧钥均解不开 = 篡改/错包，交调用方按协议错处置。
/// Actor 单线程使用（05 §0 并发纪律），不加锁；TunnelSession 集成于 M2-21。
/// </summary>
public sealed class PtpReceiveKeyRing : IDisposable
{
    public static readonly TimeSpan DefaultDrainWindow = TimeSpan.FromSeconds(2);

    private readonly TimeProvider _time;
    private readonly TimeSpan _drainWindow;
    private byte[] _current;
    private byte[]? _previous;
    private long _previousExpiresUtcTicks; // 0 = 无旧钥在窗

    public PtpReceiveKeyRing(byte[] currentReceiveKey, TimeProvider? time = null, TimeSpan? drainWindow = null)
    {
        ArgumentNullException.ThrowIfNull(currentReceiveKey);
        _time = time ?? TimeProvider.System;
        _drainWindow = drainWindow ?? DefaultDrainWindow;
        _current = currentReceiveKey;
    }

    /// <summary>已完成的轮换次数（诊断/测试观察用）。</summary>
    public int Generation { get; private set; }

    /// <summary>现钥转旧（截止 = 现在 + 排水窗口；原旧钥若仍在窗直接销毁——至多保留一代）。</summary>
    public void Rotate(byte[] newReceiveKey)
    {
        ArgumentNullException.ThrowIfNull(newReceiveKey);
        CryptoUtil.Zero(_previous);
        _previous = _current;
        _previousExpiresUtcTicks = _time.GetUtcNow().UtcTicks + _drainWindow.Ticks;
        _current = newReceiveKey;
        Generation++;
    }

    /// <summary>
    /// 解密业务帧：先现钥；失败且旧钥在窗内再试旧钥（轮换切换期在途尾帧）。返回 false = 两钥均不可解
    /// （篡改/错包）。握手帧或格式非法的 <see cref="ProtocolException"/> 原样上抛。
    /// </summary>
    public bool TryOpen(ReadOnlySpan<byte> wire, out byte[] plain)
    {
        try
        {
            plain = PtpFrameCodec.Open(wire, _current);
            return true;
        }
        catch (CryptographicException) { /* 现钥不可解：可能是排水期旧钥帧 */ }

        if (_previous is null)
        {
            plain = [];
            return false;
        }
        if (_time.GetUtcNow().UtcTicks >= _previousExpiresUtcTicks)
        {
            CryptoUtil.Zero(_previous); // 超窗销毁（02 §4.4）
            _previous = null;
            _previousExpiresUtcTicks = 0;
            plain = [];
            return false;
        }
        try
        {
            plain = PtpFrameCodec.Open(wire, _previous);
            return true;
        }
        catch (CryptographicException)
        {
            plain = [];
            return false;
        }
    }

    public void Dispose()
    {
        CryptoUtil.Zero(_current);
        CryptoUtil.Zero(_previous);
        _previous = null;
    }
}

/// <summary>
/// 发送侧每 channel 信用账本（05 §2.3：每 channel 独立窗口默认 64KiB，按消费回报——对端 WINDOW(0x0A) 帧
/// 回报已消费字节，信用耗尽暂停读本地 socket，回报到达恢复）。Actor 单线程使用（05 §0），不加锁；
/// TunnelSession 集成于 M2-21。
/// </summary>
public sealed class CreditWindow
{
    public const int DefaultCapacity = 64 * 1024;

    private int _available;

    public CreditWindow(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        _available = capacity;
    }

    public int Capacity { get; }
    public int Available => _available;
    public bool Exhausted => _available == 0;

    /// <summary>预扣信用：足够则扣减返回 true；不足返回 false 且**不部分扣**（调用方暂停读、待回报）。</summary>
    public bool TryConsume(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytes, 1);
        if (_available < bytes) return false;
        _available -= bytes;
        return true;
    }

    /// <summary>对端消费回报累加，封顶 capacity（迟到/重复回报不放大窗口）。</summary>
    public void Grant(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytes, 1);
        _available = Math.Min(Capacity, _available + bytes);
    }
}

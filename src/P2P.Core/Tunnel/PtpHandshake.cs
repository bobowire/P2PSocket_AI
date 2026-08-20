using System.Security.Cryptography;
using P2P.Core.Crypto;
using P2P.Core.Protocol;

namespace P2P.Core.Tunnel;

/// <summary>PTP 会话密钥（02 §4.1：双方向独立；A→B 与 B→A 各 32B）。</summary>
public sealed record PtpSessionKeys(Guid SessionId, byte[] KeyAtoB, byte[] KeyBtoA)
{
    /// <summary>按角色取发送密钥（A 用 A→B；B 用 B→A）。</summary>
    public byte[] SendKey(bool isInitiator) => isInitiator ? KeyAtoB : KeyBtoA;

    /// <summary>按角色取接收密钥。</summary>
    public byte[] ReceiveKey(bool isInitiator) => isInitiator ? KeyBtoA : KeyAtoB;
}

/// <summary>
/// PTP 双重 ECDH 握手（02 §4.1，SEC-12/13）。双方 static 公钥已经服务端 PunchInvite 交换。
/// 明文载荷布局（定长）：
/// THello1 = sessionId(16) | ephA(65) | nonceA(16)
/// THello2 = ephB(65) | nonceB(16) | mac2(32)
/// TConfirm = mac3(32)
/// mac2 = HMAC(kB, SHA256(t1载荷‖ephB‖nonceB))；mac3 = HMAC(kA, SHA256(t1载荷‖ephB‖nonceB‖mac2))
/// kA/kB = DeriveHandshakeAuthKeys(静态共享, sessionId)——kA 认证发起方，kB 认证响应方。
/// </summary>
public static class PtpHandshake
{
    public const int PubLen = 65;
    public const int NonceLen = 16;
    public const int MacLen = 32;
    private const int T1Len = 16 + PubLen + NonceLen;
    private const int T2Len = PubLen + NonceLen + MacLen;

    /// <summary>发起方 A 的握手状态（Start 后持 THello1Wire 待发）。</summary>
    public sealed class Initiator : IDisposable
    {
        private readonly Guid _sessionId;
        private readonly EcKeyPair _ephA;
        private readonly byte[] _nonceA;
        private readonly byte[] _staticShared;
        private readonly byte[] _authKeyA;
        private readonly byte[] _authKeyB;
        private readonly byte[] _t1Payload;

        internal Initiator(Guid sessionId, EcKeyPair staticA, ReadOnlySpan<byte> peerStaticPub)
        {
            _sessionId = sessionId;
            _ephA = EcKeyPair.Generate();
            _nonceA = RandomGenerator.Bytes(NonceLen);
            _staticShared = staticA.DeriveSharedKey(peerStaticPub);
            (_authKeyA, _authKeyB) = TunnelKeyDerivation.DeriveHandshakeAuthKeys(
                _staticShared, sessionId.ToByteArray());

            _t1Payload = new byte[T1Len];
            sessionId.ToByteArray().CopyTo(_t1Payload.AsSpan(0, 16));
            _ephA.ExportPublicKey().CopyTo(_t1Payload.AsSpan(16, PubLen));
            _nonceA.CopyTo(_t1Payload.AsSpan(16 + PubLen));
            THello1Wire = PtpFrameCodec.BuildHandshake(PtpFrameType.THello1, _t1Payload);
        }

        public byte[] THello1Wire { get; }

        /// <summary>校验 THello2 → 产出 TConfirm 与会话密钥；mac 失败即拒（对端非服务端分发的那个 B）。</summary>
        public (byte[] TConfirmWire, PtpSessionKeys Keys) HandleTHello2(ReadOnlySpan<byte> wire)
        {
            var payload = PtpFrameCodec.ReadHandshakePayload(wire);
            if (payload.Length != T2Len)
                throw new ProtocolException($"THello2 载荷长度 {payload.Length} ≠ {T2Len}");
            var ephB = payload.AsSpan(0, PubLen).ToArray();
            var nonceB = payload.AsSpan(PubLen, NonceLen).ToArray();
            var mac2 = payload.AsSpan(PubLen + NonceLen, MacLen).ToArray();

            var expected2 = Mac.HmacSha256(_authKeyB, TranscriptHash(_t1Payload, ephB, nonceB, null));
            if (!Mac.Verify(mac2, expected2))
                throw new ProtocolException("THello2 mac 校验失败（对端身份不符或载荷被篡改）");

            var ephShared = _ephA.DeriveSharedKey(ephB);
            var (aToB, bToA) = TunnelKeyDerivation.DeriveSessionKeys(
                ephShared, _staticShared, _sessionId.ToByteArray(), _nonceA, nonceB);
            var keys = new PtpSessionKeys(_sessionId, aToB, bToA);

            var mac3 = Mac.HmacSha256(_authKeyA, TranscriptHash(_t1Payload, ephB, nonceB, mac2));
            return (PtpFrameCodec.BuildHandshake(PtpFrameType.TConfirm, mac3), keys);
        }

        public void Dispose()
        {
            _ephA.Dispose();
            CryptoUtil.Zero(_staticShared);
            CryptoUtil.Zero(_authKeyA);
            CryptoUtil.Zero(_authKeyB);
            CryptoUtil.Zero(_nonceA);
            CryptoUtil.Zero(_t1Payload);
        }
    }

    /// <summary>响应方 B 的握手状态（构造即生成 THello2Wire；密钥已可派生，TConfirm 完成对 A 的认证）。</summary>
    public sealed class Responder : IDisposable
    {
        private readonly Guid _sessionId;
        private readonly EcKeyPair _ephB;
        private readonly byte[] _nonceB;
        private readonly byte[] _ephAPub;
        private readonly byte[] _nonceA;
        private readonly byte[] _t1Payload;
        private readonly byte[] _transcriptNoMac; // t1载荷‖ephB‖nonceB（mac2/mac3 的共同前文）
        private readonly byte[] _mac2;
        private readonly byte[] _authKeyA;
        private readonly byte[] _staticShared;

        internal Responder(byte[] t1Payload, EcKeyPair staticB, ReadOnlySpan<byte> peerStaticPubA)
        {
            _t1Payload = t1Payload;
            _sessionId = new Guid(t1Payload.AsSpan(0, 16).ToArray());
            _ephAPub = t1Payload.AsSpan(16, PubLen).ToArray();
            _nonceA = t1Payload.AsSpan(16 + PubLen, NonceLen).ToArray();

            _staticShared = staticB.DeriveSharedKey(peerStaticPubA);
            (_authKeyA, var authKeyB) = TunnelKeyDerivation.DeriveHandshakeAuthKeys(
                _staticShared, _sessionId.ToByteArray());

            _ephB = EcKeyPair.Generate();
            _nonceB = RandomGenerator.Bytes(NonceLen);

            _transcriptNoMac = Concat(_t1Payload, _ephB.ExportPublicKey(), _nonceB);
            var mac2 = Mac.HmacSha256(authKeyB, SHA256.HashData(_transcriptNoMac));
            _mac2 = mac2;

            var payload = new byte[T2Len];
            _ephB.ExportPublicKey().CopyTo(payload.AsSpan(0, PubLen));
            _nonceB.CopyTo(payload.AsSpan(PubLen, NonceLen));
            mac2.CopyTo(payload.AsSpan(PubLen + NonceLen));
            THello2Wire = PtpFrameCodec.BuildHandshake(PtpFrameType.THello2, payload);
        }

        public byte[] THello2Wire { get; }

        /// <summary>校验 TConfirm → 返回会话密钥；失败即销毁状态（调用方 Dispose）。</summary>
        public PtpSessionKeys VerifyTConfirm(ReadOnlySpan<byte> wire)
        {
            var payload = PtpFrameCodec.ReadHandshakePayload(wire);
            if (payload.Length != MacLen)
                throw new ProtocolException($"TConfirm 载荷长度 {payload.Length} ≠ {MacLen}");
            // mac3 的输入是全部前文（含 mac2），与发起方 TranscriptHash(t1, ephB, nonceB, mac2) 一致
            var expected = Mac.HmacSha256(_authKeyA, SHA256.HashData(Concat(_transcriptNoMac, _mac2)));
            if (!Mac.Verify(payload, expected))
                throw new ProtocolException("TConfirm mac 校验失败（对端身份不符或载荷被篡改）");
            return SessionKeys;
        }

        /// <summary>会话密钥（THello2 发出即已可派生）。</summary>
        public PtpSessionKeys SessionKeys
        {
            get
            {
                var ephShared = _ephB.DeriveSharedKey(_ephAPub);
                var (aToB, bToA) = TunnelKeyDerivation.DeriveSessionKeys(
                    ephShared, _staticShared, _sessionId.ToByteArray(), _nonceA, _nonceB);
                return new PtpSessionKeys(_sessionId, aToB, bToA);
            }
        }

        public void Dispose()
        {
            _ephB.Dispose();
            CryptoUtil.Zero(_staticShared);
            CryptoUtil.Zero(_authKeyA);
            CryptoUtil.Zero(_nonceA);
            CryptoUtil.Zero(_nonceB);
            CryptoUtil.Zero(_t1Payload);
            CryptoUtil.Zero(_transcriptNoMac);
        }
    }

    public static Initiator StartInitiator(Guid sessionId, EcKeyPair staticA, ReadOnlySpan<byte> peerStaticPub)
        => new(sessionId, staticA, peerStaticPub);

    /// <summary>从 THello1 原始帧构造响应方（解析并绑定发起方身份材料）。</summary>
    public static Responder AcceptTHello1(ReadOnlySpan<byte> tHello1Wire, EcKeyPair staticB, ReadOnlySpan<byte> peerStaticPubA)
    {
        var payload = PtpFrameCodec.ReadHandshakePayload(tHello1Wire);
        if (payload.Length != T1Len)
            throw new ProtocolException($"THello1 载荷长度 {payload.Length} ≠ {T1Len}");
        return new Responder(payload, staticB, peerStaticPubA);
    }

    /// <summary>mac2 的输入摘要：SHA256(t1载荷 ‖ ephB ‖ nonceB)——即"THello1||THello2 前文"。</summary>
    private static byte[] TranscriptHash(byte[] t1, byte[] ephB, byte[] nonceB, byte[]? mac2)
    {
        var noMac = Concat(t1, ephB, nonceB);
        return SHA256.HashData(mac2 is null ? noMac : Concat(noMac, mac2));
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var total = 0;
        foreach (var p in parts) total += p.Length;
        var result = new byte[total];
        var off = 0;
        foreach (var p in parts)
        {
            p.CopyTo(result.AsSpan(off));
            off += p.Length;
        }
        return result;
    }
}

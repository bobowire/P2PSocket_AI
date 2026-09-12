using MessagePack;
using P2P.Core.Crypto;

namespace P2P.Core.Protocol;

/// <summary>
/// PCP 消息编解码（02 §2：MessagePack 数组格式；帧内布局 = [msgpack][hmac32?]）。
/// HMAC 规则（02 §2.2）：connMacKey 派生后每消息尾附 HMAC-SHA256(header||payload)；
/// 未注册设备首个 Register 与握手前消息（Hello/Proof 阶段）不携带。
/// </summary>
public static class PcpCodec
{
    /// <summary>序列化 MessagePack（不含 hmac 尾）。</summary>
    public static byte[] Encode<T>(T message) where T : class, IPcpMessage
    {
        ValidateType<T>(message.MsgType);
        return MessagePackSerializer.Serialize(message);
    }

    /// <summary>运行时类型序列化（静态类型为 IPcpMessage 的持有方，如客户端 5005 重试的重建消息）。</summary>
    public static byte[] EncodeObject(IPcpMessage message)
    {
        var type = message.GetType();
        var expected = ExpectedMsgType(type)
            ?? throw new ProtocolException($"类型 {type.Name} 未登记 msgType");
        if (message.MsgType != expected)
            throw new ProtocolException($"msgType 不匹配：期望 0x{expected:X2}，实得 0x{message.MsgType:X2}");
        return MessagePackSerializer.Serialize(type, message);
    }

    /// <summary>反序列化为具体类型；msgType 与 T 期望值不符抛 <see cref="ProtocolException"/>。</summary>
    public static T Decode<T>(ReadOnlyMemory<byte> msgpackBody) where T : class, IPcpMessage
    {
        var msg = MessagePackSerializer.Deserialize<T>(msgpackBody);
        ValidateType<T>(msg.MsgType);
        return msg;
    }

    /// <summary>探测头三字段；数组格式容忍尾部未知字段（02 §7 加法演进）。</summary>
    public static PcpHeader Peek(ReadOnlyMemory<byte> msgpackBody)
        => MessagePackSerializer.Deserialize<PcpHeader>(msgpackBody);

    /// <summary>未知 msgType 的容忍解码：仅保留头三字段，不抛异常（M1-06：未知 msgType 容忍）。</summary>
    public static IPcpMessage DecodeLoose(ReadOnlyMemory<byte> msgpackBody)
        => Peek(msgpackBody) is { } h ? new UnknownMessage(h.Seq, h.TimestampMs, h.MsgType) : throw new ProtocolException("空消息体");

    // ── 帧内签名布局：[msgpack][hmac 32B]（02 §2.2）──────────────────

    /// <summary>组签名帧体：msgpack || HMAC-SHA256(connMacKey, msgpack)。</summary>
    public static byte[] EncodeSigned<T>(T message, ReadOnlySpan<byte> connMacKey) where T : class, IPcpMessage
    {
        var body = Encode(message);
        return AppendMac(body, connMacKey);
    }

    /// <summary>运行时类型组签名帧体（与 <see cref="EncodeObject"/> 配对）。</summary>
    public static byte[] EncodeSignedObject(IPcpMessage message, ReadOnlySpan<byte> connMacKey)
        => AppendMac(EncodeObject(message), connMacKey);

    private static byte[] AppendMac(byte[] body, ReadOnlySpan<byte> connMacKey)
    {
        var mac = Mac.HmacSha256(connMacKey, body);
        var wire = new byte[body.Length + mac.Length];
        body.AsSpan().CopyTo(wire);
        mac.AsSpan().CopyTo(wire.AsSpan(body.Length));
        return wire;
    }

    /// <summary>拆签名帧体并验证 HMAC；失败抛 <see cref="ProtocolException"/>（调用方决定断连，07 §4）。</summary>
    public static byte[] DecodeSigned(ReadOnlyMemory<byte> wire, ReadOnlySpan<byte> connMacKey)
    {
        if (wire.Length < Mac.HashLen)
            throw new ProtocolException("签名帧体长度不足");
        var msgpack = wire[..^Mac.HashLen].ToArray();
        var mac = wire.Span[^Mac.HashLen..];
        if (!Mac.Verify(mac, Mac.HmacSha256(connMacKey, msgpack)))
            throw new ProtocolException("HMAC 校验失败");
        return msgpack;
    }

    private static void ValidateType<T>(byte actual) where T : class, IPcpMessage
    {
        var expected = ExpectedMsgType(typeof(T))
            ?? throw new ProtocolException($"类型 {typeof(T).Name} 未登记 msgType");
        if (actual != expected)
            throw new ProtocolException($"msgType 不匹配：期望 0x{expected:X2}，实得 0x{actual:X2}");
    }

    internal static byte? ExpectedMsgType(Type t) => t.Name switch
    {
        nameof(Hello) or nameof(HelloAck) => MsgType.Hello,
        nameof(Proof) or nameof(ProofAck) => MsgType.Proof,
        nameof(Heartbeat) or nameof(HeartbeatAck) => MsgType.Heartbeat,
        nameof(UpdateInfoRequest) or nameof(UpdateInfoResponse) => MsgType.UpdateInfo,
        nameof(Register) => MsgType.Register,
        nameof(RegisterAck) => MsgType.RegisterAck,
        nameof(UnbindMe) => MsgType.UnbindMe,
        nameof(DeviceUpdate) or nameof(DeviceUpdateAck) => MsgType.DeviceUpdate,
        nameof(RemoteCodeReset) or nameof(RemoteCodeResetAck) => MsgType.RemoteCodeReset,
        nameof(UserRegister) or nameof(UserRegisterAck) => MsgType.UserRegister,
        nameof(UserLogin) or nameof(UserLoginAck) => MsgType.UserLogin,
        nameof(UserLogout) or nameof(UserLogoutAck) => MsgType.UserLogout,
        nameof(UserChangePassword) or nameof(UserChangePasswordAck) => MsgType.UserChangePassword,
        nameof(DeviceListRequest) or nameof(DeviceListResponse) => MsgType.DeviceList,
        nameof(DeviceListUpdate) => MsgType.DeviceListUpdate,
        nameof(GroupCreate) or nameof(GroupCreateAck) => MsgType.GroupCreate,
        nameof(GroupJoin) or nameof(GroupJoinAck) => MsgType.GroupJoin,
        nameof(GroupLeave) or nameof(GroupLeaveAck) => MsgType.GroupLeave,
        nameof(JoinRequests) or nameof(JoinRequestsResponse) or nameof(JoinRequestsAck) => MsgType.JoinRequests,
        nameof(GroupInviteGen) or nameof(GroupInviteGenAck) => MsgType.GroupInviteGen,
        nameof(GroupUpdate) or nameof(GroupUpdateAck) => MsgType.GroupUpdate,
        nameof(GroupDissolve) or nameof(GroupDissolveAck) => MsgType.GroupDissolve,
        nameof(GroupRemoveMember) or nameof(GroupRemoveMemberAck) => MsgType.GroupRemoveMember,
        nameof(MappingUpsert) or nameof(MappingUpsertAck) => MsgType.MappingUpsert,
        nameof(MappingDelete) or nameof(MappingDeleteAck) => MsgType.MappingDelete,
        nameof(MappingStatus) => MsgType.MappingStatus,
        nameof(LanSegmentsUpsert) or nameof(LanSegmentsUpsertAck) => MsgType.LanSegmentsUpsert,
        nameof(StatsReport) => MsgType.StatsReport,
        nameof(PunchRequest) or nameof(PunchRequestAck) => MsgType.PunchRequest,
        nameof(PunchInvite) => MsgType.PunchInvite,
        nameof(PunchResult) => MsgType.PunchResult,
        nameof(PunchRetry) => MsgType.PunchRetry,
        nameof(RelayAllocate) or nameof(RelayGrant) => MsgType.RelayAllocate,
        nameof(Invalidation) => MsgType.Invalidation,
        nameof(PunchEndpoint) => MsgType.PunchEndpoint,
        nameof(ErrorMessage) => MsgType.Error,
        _ => null,
    };
}

/// <summary>协议层异常（编解码/校验失败；不承载任何秘密，AI-17）。</summary>
public sealed class ProtocolException(string message) : Exception(message);

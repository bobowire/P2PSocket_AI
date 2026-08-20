using P2P.Core.Crypto;
using P2P.Core.Protocol;
using Xunit;

namespace P2P.Core.Tests;

public class PcpBasicMessageTests
{
    [Fact]
    public void Hello_RoundTrip_AllFieldsPreserved()
    {
        var msg = new Hello(7, 123456789UL, MsgType.Hello, ProtocolVersion.Current,
            Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"), new byte[] { 1, 2, 3, 4 });
        MsgAssert.Equal(msg, PcpCodec.Decode<Hello>(PcpCodec.Encode(msg)));
    }

    [Fact]
    public void HelloAck_RoundTrip_AllFieldsPreserved()
    {
        var msg = new HelloAck(1, 99UL, MsgType.Hello, "1.0.0", ProtocolVersion.Current,
            new byte[16], HelloStatus.NeedRegister, 777777UL);
        MsgAssert.Equal(msg, PcpCodec.Decode<HelloAck>(PcpCodec.Encode(msg)));
    }

    [Fact]
    public void ProofHeartbeatError_RoundTrip_AllFieldsPreserved()
    {
        var proof = new Proof(2, 1UL, MsgType.Proof, new byte[32]);
        MsgAssert.Equal(proof, PcpCodec.Decode<Proof>(PcpCodec.Encode(proof)));

        var hb = new Heartbeat(3, 2UL, MsgType.Heartbeat);
        Assert.Equal(hb, PcpCodec.Decode<Heartbeat>(PcpCodec.Encode(hb)));

        var hba = new HeartbeatAck(4, 3UL, MsgType.Heartbeat, 456UL);
        Assert.Equal(hba, PcpCodec.Decode<HeartbeatAck>(PcpCodec.Encode(hba)));

        var err = new ErrorMessage(5, 4UL, MsgType.Error, ErrorCode.TimeSkew, "time_skew");
        Assert.Equal(err, PcpCodec.Decode<ErrorMessage>(PcpCodec.Encode(err)));
    }

    [Fact]
    public void Decode_UnknownMsgType_Tolerated()
    {
        // 手工构造 msgType=0x6F（不在 v1 表中）的消息体：fixarray(3) [seq=9, ts=8, 0x6F]
        var wire = new byte[] { 0x93, 0x09, 0x08, 0x6F };
        var loose = PcpCodec.DecodeLoose(wire);
        Assert.IsType<UnknownMessage>(loose);
        Assert.Equal(0x6F, loose.MsgType);
        Assert.Equal(9u, loose.Seq);
    }

    [Fact]
    public void Peek_HeaderOfLongerMessage_ToleratesExtraFields()
    {
        // Hello 有 6 个字段，PcpHeader 只取前 3 —— 加法演进兼容（02 §7）
        var wire = PcpCodec.Encode(new Hello(11, 22UL, MsgType.Hello, 1, null, [1]));
        var header = PcpCodec.Peek(wire);
        Assert.Equal(11u, header.Seq);
        Assert.Equal(22UL, header.TimestampMs);
        Assert.Equal(MsgType.Hello, header.MsgType);
    }

    [Fact]
    public void Encode_WrongMsgTypeForType_Rejected()
    {
        var bad = new Heartbeat(1, 1UL, MsgType.Error);
        Assert.Throws<ProtocolException>(() => PcpCodec.Encode(bad));
    }

    [Fact]
    public void SignedFrame_RoundTrip_AndTamperRejected()
    {
        var key = RandomGenerator.Bytes(32);
        var msg = new Heartbeat(10, 20UL, MsgType.Heartbeat);
        var wire = PcpCodec.EncodeSigned(msg, key);

        var msgpack = PcpCodec.DecodeSigned(wire, key);
        Assert.Equal(msg, PcpCodec.Decode<Heartbeat>(msgpack));

        wire[^1] ^= 0x01;
        Assert.Throws<ProtocolException>(() => PcpCodec.DecodeSigned(wire, key));
    }

    /// <summary>错误码常量与 04 §5 总表逐一对齐（AI-14：引用的每个码必须存在于总表）。</summary>
    [Theory]
    [InlineData(ErrorCode.Ok, 0, "ok")]
    [InlineData(ErrorCode.BadRequest, 1001, "bad_request")]
    [InlineData(ErrorCode.NotFound, 1002, "not_found")]
    [InlineData(ErrorCode.Conflict, 1003, "conflict")]
    [InlineData(ErrorCode.Unauthorized, 2001, "unauthorized")]
    [InlineData(ErrorCode.ForbiddenPassive, 2002, "forbidden_passive")]
    [InlineData(ErrorCode.Forbidden, 2003, "forbidden")]
    [InlineData(ErrorCode.RegistrationClosed, 2004, "registration_closed")]
    [InlineData(ErrorCode.GroupNotFound, 3001, "group_not_found")]
    [InlineData(ErrorCode.GroupNeedApproval, 3002, "group_need_approval")]
    [InlineData(ErrorCode.TargetNotAuthorized, 4001, "target_not_authorized")]
    [InlineData(ErrorCode.TargetAddrNotAllowed, 4002, "target_addr_not_allowed")]
    [InlineData(ErrorCode.RemoteCodeInvalid, 4003, "remote_code_invalid")]
    [InlineData(ErrorCode.DeviceActive, 4004, "device_active")]
    [InlineData(ErrorCode.TargetOffline, 4005, "target_offline")]
    [InlineData(ErrorCode.PunchFailed, 5001, "punch_failed")]
    [InlineData(ErrorCode.RelayDisabled, 5002, "relay_disabled")]
    [InlineData(ErrorCode.ServerUnreachable, 5003, "server_unreachable")]
    [InlineData(ErrorCode.VersionNotSupported, 5004, "version_not_supported")]
    [InlineData(ErrorCode.TimeSkew, 5005, "time_skew")]
    public void ErrorCode_MatchesDesignTable(int code, int expectedValue, string expectedName)
    {
        Assert.Equal(expectedValue, code);
        Assert.Equal(expectedName, ErrorCode.Describe(code));
    }

    /// <summary>msgType 常量与 02 §2.4 消息表逐一对齐。</summary>
    [Theory]
    [InlineData(MsgType.Hello, 0x01)]
    [InlineData(MsgType.Proof, 0x02)]
    [InlineData(MsgType.UpdateInfo, 0x03)]
    [InlineData(MsgType.Register, 0x10)]
    [InlineData(MsgType.RegisterAck, 0x11)]
    [InlineData(MsgType.UnbindMe, 0x12)]
    [InlineData(MsgType.DeviceUpdate, 0x13)]
    [InlineData(MsgType.RemoteCodeReset, 0x14)]
    [InlineData(MsgType.UserRegister, 0x20)]
    [InlineData(MsgType.UserLogin, 0x21)]
    [InlineData(MsgType.UserLogout, 0x22)]
    [InlineData(MsgType.UserChangePassword, 0x23)]
    [InlineData(MsgType.Heartbeat, 0x30)]
    [InlineData(MsgType.DeviceList, 0x40)]
    [InlineData(MsgType.DeviceListUpdate, 0x41)]
    [InlineData(MsgType.GroupCreate, 0x50)]
    [InlineData(MsgType.GroupJoin, 0x51)]
    [InlineData(MsgType.GroupLeave, 0x52)]
    [InlineData(MsgType.JoinRequests, 0x53)]
    [InlineData(MsgType.GroupInviteGen, 0x54)]
    [InlineData(MsgType.GroupUpdate, 0x55)]
    [InlineData(MsgType.GroupDissolve, 0x56)]
    [InlineData(MsgType.GroupRemoveMember, 0x57)]
    [InlineData(MsgType.MappingUpsert, 0x60)]
    [InlineData(MsgType.MappingDelete, 0x61)]
    [InlineData(MsgType.MappingStatus, 0x62)]
    [InlineData(MsgType.LanSegmentsUpsert, 0x63)]
    [InlineData(MsgType.StatsReport, 0x64)]
    [InlineData(MsgType.PunchRequest, 0x70)]
    [InlineData(MsgType.PunchInvite, 0x71)]
    [InlineData(MsgType.PunchResult, 0x72)]
    [InlineData(MsgType.PunchRetry, 0x73)]
    [InlineData(MsgType.RelayAllocate, 0x74)]
    [InlineData(MsgType.Invalidation, 0x75)]
    [InlineData(MsgType.PunchEndpoint, 0x76)]
    [InlineData(MsgType.Error, 0x7E)]
    public void MsgType_MatchesDesignTable(byte actual, byte expected)
    {
        Assert.Equal(expected, actual);
    }
}

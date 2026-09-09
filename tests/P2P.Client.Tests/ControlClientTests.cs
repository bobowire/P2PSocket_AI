using P2P.Client.Control;
using P2P.Core.Protocol;
using Xunit;

namespace P2P.Client.Tests;

/// <summary>
/// M1-23 纯逻辑单测：passive 主动类镜像表（02 §2.5）、请求→Ack 族配对、
/// 5005 重试的消息重建（MessageRebuilder）。
/// </summary>
public sealed class ControlClientTests
{
    [Theory]
    [InlineData(MsgType.UserRegister, true)]   // 0x20
    [InlineData(MsgType.UserLogin, true)]      // 0x21（passive 会话登录被拒，重连后再登）
    [InlineData(MsgType.UserChangePassword, true)]
    [InlineData(MsgType.DeviceList, true)]     // 0x40
    [InlineData(MsgType.GroupCreate, true)]    // 0x50
    [InlineData(MsgType.GroupJoin, true)]
    [InlineData(MsgType.GroupLeave, true)]
    [InlineData(MsgType.JoinRequests, true)]
    [InlineData(MsgType.GroupInviteGen, true)]
    [InlineData(MsgType.GroupUpdate, true)]
    [InlineData(MsgType.GroupDissolve, true)]
    [InlineData(MsgType.GroupRemoveMember, true)]
    [InlineData(MsgType.MappingUpsert, true)]  // 0x60
    [InlineData(MsgType.MappingDelete, true)]
    [InlineData(MsgType.PunchRequest, true)]   // 0x70
    // 被动类（本机管理/降级/上报；02 §2.5 矩阵）
    [InlineData(MsgType.Hello, false)]
    [InlineData(MsgType.Proof, false)]
    [InlineData(MsgType.Register, false)]      // 注册本身是未注册设备唯一出路
    [InlineData(MsgType.UnbindMe, false)]
    [InlineData(MsgType.DeviceUpdate, false)]  // 0x13 本机管理类
    [InlineData(MsgType.UserLogout, false)]    // 0x22 降级操作
    [InlineData(MsgType.Heartbeat, false)]
    [InlineData(MsgType.MappingStatus, false)] // 上报
    [InlineData(MsgType.PunchEndpoint, false)] // 被邀请方回传
    [InlineData(MsgType.Error, false)]
    public void 主动类镜像表_与02_2_5清单一致(byte msgType, bool expected)
        => Assert.Equal(expected, ControlClient.IsActiveClass(msgType));

    [Fact]
    public void 族配对_Register族异码_其余共用()
    {
        Assert.Equal(MsgType.RegisterAck, ControlClient.AckTypeFor(MsgType.Register)); // 0x10→0x11
        Assert.Equal(MsgType.DeviceUpdate, ControlClient.AckTypeFor(MsgType.DeviceUpdate));
        Assert.Equal(MsgType.UserLogin, ControlClient.AckTypeFor(MsgType.UserLogin));
        Assert.Equal(MsgType.UserLogout, ControlClient.AckTypeFor(MsgType.UserLogout));
        Assert.Equal(MsgType.DeviceList, ControlClient.AckTypeFor(MsgType.DeviceList));
        Assert.Equal(MsgType.PunchRequest, ControlClient.AckTypeFor(MsgType.PunchRequest));
    }

    [Fact]
    public void 消息重建_换seq_ts_其余字段保留()
    {
        IPcpMessage original = new UserLogin(7, 111_111, MsgType.UserLogin, "alice", "pw");
        var rebuilt = (UserLogin)MessageRebuilder.WithHeader(original, 42, 222_222);

        Assert.Equal(42u, rebuilt.Seq);
        Assert.Equal(222_222ul, rebuilt.TimestampMs);
        Assert.Equal(MsgType.UserLogin, rebuilt.MsgType);
        Assert.Equal("alice", rebuilt.Username);
        Assert.Equal("pw", rebuilt.Password);
    }

    [Fact]
    public void 消息重建_最小消息_头三字段全部换新()
    {
        IPcpMessage original = new UserLogout(1, 100, MsgType.UserLogout);
        var rebuilt = (UserLogout)MessageRebuilder.WithHeader(original, 9, 200);

        Assert.Equal(9u, rebuilt.Seq);
        Assert.Equal(200ul, rebuilt.TimestampMs);
        Assert.Equal(MsgType.UserLogout, rebuilt.MsgType);
    }

    [Fact]
    public void serverAddrs为空_构造即拒()
        => Assert.Throws<ArgumentException>(() => new ControlClient(Array.Empty<string>()));
}

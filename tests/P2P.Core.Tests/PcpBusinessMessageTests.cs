using P2P.Core.Crypto;
using P2P.Core.Protocol;
using Xunit;

namespace P2P.Core.Tests;

public class PcpBusinessMessageTests
{
    // ── 全消息往返（M1-08 完成判定）────────────────────────────────────────

    [Fact]
    public void UserFamily_RoundTrip()
    {
        var dev = new DeviceUpdate(1, 1UL, MsgType.DeviceUpdate, "新名字");
        MsgAssert.Equal(dev, PcpCodec.Decode<DeviceUpdate>(PcpCodec.Encode(dev)));

        var reg = new UserRegister(2, 2UL, MsgType.UserRegister, "alice", "pw");
        MsgAssert.Equal(reg, PcpCodec.Decode<UserRegister>(PcpCodec.Encode(reg)));

        var login = new UserLogin(3, 3UL, MsgType.UserLogin, "alice", "pw");
        MsgAssert.Equal(login, PcpCodec.Decode<UserLogin>(PcpCodec.Encode(login)));

        var loginAck = new UserLoginAck(4, 4UL, MsgType.UserLogin, true, CapabilityMode.Normal, "tok-1");
        MsgAssert.Equal(loginAck, PcpCodec.Decode<UserLoginAck>(PcpCodec.Encode(loginAck)));

        var logout = new UserLogout(5, 5UL, MsgType.UserLogout);
        MsgAssert.Equal(logout, PcpCodec.Decode<UserLogout>(PcpCodec.Encode(logout)));
    }

    [Fact]
    public void GroupFamily_RoundTrip()
    {
        var create = new GroupCreate(1, 1UL, MsgType.GroupCreate, "开发组", JoinPolicy.Approval);
        MsgAssert.Equal(create, PcpCodec.Decode<GroupCreate>(PcpCodec.Encode(create)));

        var createAck = new GroupCreateAck(2, 2UL, MsgType.GroupCreate, Guid.NewGuid());
        MsgAssert.Equal(createAck, PcpCodec.Decode<GroupCreateAck>(PcpCodec.Encode(createAck)));

        // 改名不改策略：Policy=null 语义保留
        var update = new GroupUpdate(3, 3UL, MsgType.GroupUpdate, Guid.NewGuid(), "新名", null);
        var decodedUpdate = PcpCodec.Decode<GroupUpdate>(PcpCodec.Encode(update));
        Assert.Null(decodedUpdate.Policy);
        Assert.Equal("新名", decodedUpdate.Name);

        var dissolve = new GroupDissolve(4, 4UL, MsgType.GroupDissolve, Guid.NewGuid());
        MsgAssert.Equal(dissolve, PcpCodec.Decode<GroupDissolve>(PcpCodec.Encode(dissolve)));
    }

    [Fact]
    public void MappingFamily_RoundTrip()
    {
        var upsert = new MappingUpsert(1, 1UL, MsgType.MappingUpsert,
            MappingId: null, Name: "远程桌面", LocalPort: 33890, Proto: "tcp",
            TargetRemoteCode: "0a2b3c", TargetAddr: "self", TargetPort: 3389, Enabled: false);
        var decoded = PcpCodec.Decode<MappingUpsert>(PcpCodec.Encode(upsert));
        Assert.Null(decoded.MappingId);
        Assert.Equal("0a2b3c", decoded.TargetRemoteCode);

        var del = new MappingDelete(2, 2UL, MsgType.MappingDelete, Guid.NewGuid());
        MsgAssert.Equal(del, PcpCodec.Decode<MappingDelete>(PcpCodec.Encode(del)));

        // 0x62 仅定义编解码（M2 处理）
        var status = new MappingStatus(3, 3UL, MsgType.MappingStatus, Guid.NewGuid(), "direct", null);
        MsgAssert.Equal(status, PcpCodec.Decode<MappingStatus>(PcpCodec.Encode(status)));
    }

    // ── 0x40 分页三字段语义（OQ-16）────────────────────────────────────────

    [Fact]
    public void DeviceList_PaginationFields_Preserved()
    {
        var req = new DeviceListRequest(1, 1UL, MsgType.DeviceList, Offset: 100, Limit: 100);
        var decodedReq = PcpCodec.Decode<DeviceListRequest>(PcpCodec.Encode(req));
        Assert.Equal(100u, decodedReq.Offset);
        Assert.Equal(100u, decodedReq.Limit);

        var items = new[]
        {
            new DeviceListItem(Guid.NewGuid(), "pc-a", "0a2b3c", "10.10.0.2", true,
                ["默认分组"], ["192.168.1.0/24"]),
            new DeviceListItem(Guid.NewGuid(), "pc-b", "987654", "10.10.0.2", false, [], []),
        };
        var resp = new DeviceListResponse(2, 2UL, MsgType.DeviceList, Total: 250, Items: items, HasMore: true);
        var decoded = PcpCodec.Decode<DeviceListResponse>(PcpCodec.Encode(resp));

        Assert.Equal(250u, decoded.Total);
        Assert.True(decoded.HasMore);
        Assert.Equal(2, decoded.Items.Length);
        Assert.True(decoded.Items[0].Online);
        Assert.False(decoded.Items[1].Online);
        Assert.Equal("192.168.1.0/24", decoded.Items[0].LanSegments[0]);
        Assert.Empty(decoded.Items[1].Groups);
    }

    // ── 0x70/0x71/0x76 两段式端点载荷（OQ-18/TD-19）───────────────────────

    private static PeerInfo NewPeer() => new(Guid.NewGuid(), "pc-peer", "0a2b3c",
        EcKeyPair.Generate().ExportPublicKey());

    [Fact]
    public void PunchRequest_RequesterEndpointsOptional_Survives()
    {
        // 出队前尚无端点：requesterEndpoints=null（Ack 携带的端点由 0x76 回传补齐）
        var noEp = new PunchRequest(1, 1UL, MsgType.PunchRequest, Guid.NewGuid(), Guid.NewGuid(), "udp", null);
        Assert.Null(PcpCodec.Decode<PunchRequest>(PcpCodec.Encode(noEp)).RequesterEndpoints);

        var withEp = new PunchRequest(2, 2UL, MsgType.PunchRequest, Guid.NewGuid(), null, "tcp",
            new EndpointPair(new Endpoint("203.0.113.10", 40001), null));
        var decoded = PcpCodec.Decode<PunchRequest>(PcpCodec.Encode(withEp));
        Assert.NotNull(decoded.RequesterEndpoints);
        Assert.Equal("203.0.113.10", decoded.RequesterEndpoints.Udp!.Host);
        Assert.Equal(40001, decoded.RequesterEndpoints.Udp.Port);
        Assert.Null(decoded.RequesterEndpoints.Tcp);
    }

    [Fact]
    public void PunchRequestAck_CarriesPeerAndEndpoints()
    {
        var peer = NewPeer();
        var ack = new PunchRequestAck(1, 1UL, MsgType.PunchRequest, Guid.NewGuid(), peer,
            new EndpointPair(new Endpoint("198.51.100.7", 50001), new Endpoint("198.51.100.7", 50002)),
            PunchCount: 5, RelayAllowed: false);
        var decoded = PcpCodec.Decode<PunchRequestAck>(PcpCodec.Encode(ack));

        Assert.Equal(peer.DeviceId, decoded.Peer.DeviceId);
        Assert.Equal(65, decoded.Peer.StaticPubKey.Length);
        Assert.Equal(50001, decoded.PeerEndpoints.Udp!.Port);
        Assert.Equal(50002, decoded.PeerEndpoints.Tcp!.Port);
        Assert.Equal(5, decoded.PunchCount);
        Assert.False(decoded.RelayAllowed); // M1 relayAllowed 恒 false（M1-17）
    }

    [Fact]
    public void PunchInvite_CarriesInitiatorInfo()
    {
        var invite = new PunchInvite(1, 1UL, MsgType.PunchInvite, Guid.NewGuid(), NewPeer(),
            new EndpointPair(new Endpoint("203.0.113.10", 40001), null), PunchCount: 5, RelayAllowed: false);
        var decoded = PcpCodec.Decode<PunchInvite>(PcpCodec.Encode(invite));
        Assert.Equal(invite.SessionId, decoded.SessionId);
        Assert.Equal(invite.Peer.DeviceId, decoded.Peer.DeviceId);
        Assert.Equal(40001, decoded.PeerEndpoints.Udp!.Port);
    }

    [Fact]
    public void PunchEndpoint_RoundTrip()
    {
        var msg = new PunchEndpoint(1, 1UL, MsgType.PunchEndpoint, Guid.NewGuid(),
            new EndpointPair(null, new Endpoint("198.51.100.7", 50002)));
        var decoded = PcpCodec.Decode<PunchEndpoint>(PcpCodec.Encode(msg));
        Assert.Null(decoded.Endpoints.Udp);
        Assert.Equal(50002, decoded.Endpoints.Tcp!.Port);
    }

    [Fact]
    public void PunchResult_DefinedForM2_RoundTrip()
    {
        // 0x72 仅定义编解码（处理属 FR-C-404 → M2）
        var ok = new PunchResult(1, 1UL, MsgType.PunchResult, Guid.NewGuid(), true,
            new Endpoint("203.0.113.10", 40001), null);
        MsgAssert.Equal(ok, PcpCodec.Decode<PunchResult>(PcpCodec.Encode(ok)));

        var fail = new PunchResult(2, 2UL, MsgType.PunchResult, Guid.NewGuid(), false, null, "no_reply");
        MsgAssert.Equal(fail, PcpCodec.Decode<PunchResult>(PcpCodec.Encode(fail)));
    }
}

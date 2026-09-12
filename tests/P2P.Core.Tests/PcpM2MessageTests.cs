using P2P.Core.Protocol;
using Xunit;

namespace P2P.Core.Tests;

/// <summary>M2-03 协议消息补全——新增消息族编解码往返与语义保留断言（完成判定全四条）。</summary>
public class PcpM2MessageTests
{
    // ── 0x03/0x14/0x23/0x41：升级信息、远程码重置、改密、列表推送 ───────────

    [Fact]
    public void UpdateInfo_RoundTrip()
    {
        var req = new UpdateInfoRequest(1, 1UL, MsgType.UpdateInfo);
        Assert.Equal(req, PcpCodec.Decode<UpdateInfoRequest>(PcpCodec.Encode(req)));

        var resp = new UpdateInfoResponse(2, 2UL, MsgType.UpdateInfo, "1.2.0",
            MinProtocol: 1, MaxProtocol: 1, UpgradeUrl: "https://example.com/dl", Notes: "修复与说明");
        var decoded = PcpCodec.Decode<UpdateInfoResponse>(PcpCodec.Encode(resp));
        Assert.Equal("1.2.0", decoded.LatestVersion);
        Assert.Equal(1, decoded.MinProtocol);
        Assert.Equal(1, decoded.MaxProtocol);
        Assert.Equal("https://example.com/dl", decoded.UpgradeUrl);
        Assert.Equal("修复与说明", decoded.Notes);
    }

    [Fact]
    public void RemoteCodeReset_AckNewCodeOptional()
    {
        var req = new RemoteCodeReset(1, 1UL, MsgType.RemoteCodeReset);
        Assert.Equal(req, PcpCodec.Decode<RemoteCodeReset>(PcpCodec.Encode(req)));

        var ok = new RemoteCodeResetAck(2, 2UL, MsgType.RemoteCodeReset, true, "a3b4c5");
        Assert.Equal("a3b4c5", PcpCodec.Decode<RemoteCodeResetAck>(PcpCodec.Encode(ok)).NewRemoteCode);

        // 失败态：无新码
        var fail = new RemoteCodeResetAck(3, 3UL, MsgType.RemoteCodeReset, false, null);
        Assert.Null(PcpCodec.Decode<RemoteCodeResetAck>(PcpCodec.Encode(fail)).NewRemoteCode);
    }

    [Fact]
    public void UserChangePassword_RoundTrip()
    {
        var msg = new UserChangePassword(1, 1UL, MsgType.UserChangePassword, "old-pw", "new-pw");
        var decoded = PcpCodec.Decode<UserChangePassword>(PcpCodec.Encode(msg));
        Assert.Equal("old-pw", decoded.OldPassword);
        Assert.Equal("new-pw", decoded.NewPassword);

        var ack = new UserChangePasswordAck(2, 2UL, MsgType.UserChangePassword, true);
        MsgAssert.Equal(ack, PcpCodec.Decode<UserChangePasswordAck>(PcpCodec.Encode(ack)));
    }

    [Fact]
    public void DeviceListUpdate_HeaderOnlyHintFrame()
    {
        var msg = new DeviceListUpdate(7, 77UL, MsgType.DeviceListUpdate);
        var decoded = PcpCodec.Decode<DeviceListUpdate>(PcpCodec.Encode(msg));
        Assert.Equal(7u, decoded.Seq);
        Assert.Equal(77UL, decoded.TimestampMs);
        Assert.Equal(MsgType.DeviceListUpdate, decoded.MsgType);
    }

    // ── 0x51/0x52/0x57：入组 / 退组 / 移出 ────────────────────────────────

    [Fact]
    public void GroupMembership_RoundTrip()
    {
        var join = new GroupJoin(1, 1UL, MsgType.GroupJoin, "ab3cde");
        Assert.Equal("ab3cde", PcpCodec.Decode<GroupJoin>(PcpCodec.Encode(join)).InviteCode);

        var joinAck = new GroupJoinAck(2, 2UL, MsgType.GroupJoin, Guid.NewGuid());
        MsgAssert.Equal(joinAck, PcpCodec.Decode<GroupJoinAck>(PcpCodec.Encode(joinAck)));

        var leave = new GroupLeave(3, 3UL, MsgType.GroupLeave, Guid.NewGuid());
        MsgAssert.Equal(leave, PcpCodec.Decode<GroupLeave>(PcpCodec.Encode(leave)));

        var leaveAck = new GroupLeaveAck(4, 4UL, MsgType.GroupLeave, true);
        MsgAssert.Equal(leaveAck, PcpCodec.Decode<GroupLeaveAck>(PcpCodec.Encode(leaveAck)));

        var kick = new GroupRemoveMember(5, 5UL, MsgType.GroupRemoveMember, Guid.NewGuid(), Guid.NewGuid());
        var decodedKick = PcpCodec.Decode<GroupRemoveMember>(PcpCodec.Encode(kick));
        Assert.Equal(kick.GroupId, decodedKick.GroupId);
        Assert.Equal(kick.MemberDeviceId, decodedKick.MemberDeviceId);

        var kickAck = new GroupRemoveMemberAck(6, 6UL, MsgType.GroupRemoveMember, true);
        MsgAssert.Equal(kickAck, PcpCodec.Decode<GroupRemoveMemberAck>(PcpCodec.Encode(kickAck)));
    }

    // ── 0x53：审批队列三种操作共用一个 msgType ────────────────────────────

    [Fact]
    public void JoinRequests_ListApproveReject_Discriminated()
    {
        var list = new JoinRequests(1, 1UL, MsgType.JoinRequests, JoinRequestAction.List,
            GroupId: Guid.NewGuid(), RequestId: null);
        var decodedList = PcpCodec.Decode<JoinRequests>(PcpCodec.Encode(list));
        Assert.Equal(JoinRequestAction.List, decodedList.Action);
        Assert.NotNull(decodedList.GroupId);
        Assert.Null(decodedList.RequestId);

        var approve = new JoinRequests(2, 2UL, MsgType.JoinRequests, JoinRequestAction.Approve,
            GroupId: null, RequestId: Guid.NewGuid());
        var decodedApprove = PcpCodec.Decode<JoinRequests>(PcpCodec.Encode(approve));
        Assert.Equal(JoinRequestAction.Approve, decodedApprove.Action);
        Assert.Null(decodedApprove.GroupId);
        Assert.NotNull(decodedApprove.RequestId);

        var reject = new JoinRequests(3, 3UL, MsgType.JoinRequests, JoinRequestAction.Reject,
            GroupId: null, RequestId: Guid.NewGuid());
        Assert.Equal(JoinRequestAction.Reject, PcpCodec.Decode<JoinRequests>(PcpCodec.Encode(reject)).Action);

        var items = new[]
        {
            new JoinRequestItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "pc-a", 1000UL),
            new JoinRequestItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "pc-b", 2000UL),
        };
        var resp = new JoinRequestsResponse(4, 4UL, MsgType.JoinRequests, items);
        var decodedResp = PcpCodec.Decode<JoinRequestsResponse>(PcpCodec.Encode(resp));
        Assert.Equal(2, decodedResp.Items.Length);
        Assert.Equal("pc-b", decodedResp.Items[1].DeviceName);
        Assert.Equal(items[0].RequestId, decodedResp.Items[0].RequestId);

        var ack = new JoinRequestsAck(5, 5UL, MsgType.JoinRequests, true);
        MsgAssert.Equal(ack, PcpCodec.Decode<JoinRequestsAck>(PcpCodec.Encode(ack)));
    }

    // ── 0x54：邀请码生成/撤销 ──────────────────────────────────────────────

    [Fact]
    public void GroupInviteGen_GenerateAndRevoke()
    {
        var gen = new GroupInviteGen(1, 1UL, MsgType.GroupInviteGen, Guid.NewGuid(), Revoke: false);
        Assert.False(PcpCodec.Decode<GroupInviteGen>(PcpCodec.Encode(gen)).Revoke);

        var genAck = new GroupInviteGenAck(2, 2UL, MsgType.GroupInviteGen, true, "xy9z8w");
        Assert.Equal("xy9z8w", PcpCodec.Decode<GroupInviteGenAck>(PcpCodec.Encode(genAck)).InviteCode);

        // 撤销成功：无码返回
        var revokeAck = new GroupInviteGenAck(3, 3UL, MsgType.GroupInviteGen, true, null);
        Assert.Null(PcpCodec.Decode<GroupInviteGenAck>(PcpCodec.Encode(revokeAck)).InviteCode);
    }

    // ── 0x63/0x64：内网段白名单与流量上报 ──────────────────────────────────

    [Fact]
    public void LanSegmentsUpsert_CreateAndUpdate()
    {
        // 新建：SegmentId=null
        var create = new LanSegmentsUpsert(1, 1UL, MsgType.LanSegmentsUpsert,
            SegmentId: null, Cidr: "192.168.1.0/24", Enabled: true);
        var decodedCreate = PcpCodec.Decode<LanSegmentsUpsert>(PcpCodec.Encode(create));
        Assert.Null(decodedCreate.SegmentId);
        Assert.Equal("192.168.1.0/24", decodedCreate.Cidr);
        Assert.True(decodedCreate.Enabled);

        // 更新/移除语义：Enabled=false
        var disable = new LanSegmentsUpsert(2, 2UL, MsgType.LanSegmentsUpsert,
            Guid.NewGuid(), "10.0.0.0/8", false);
        var decodedDisable = PcpCodec.Decode<LanSegmentsUpsert>(PcpCodec.Encode(disable));
        Assert.NotNull(decodedDisable.SegmentId);
        Assert.False(decodedDisable.Enabled);

        var ack = new LanSegmentsUpsertAck(3, 3UL, MsgType.LanSegmentsUpsert, Guid.NewGuid());
        MsgAssert.Equal(ack, PcpCodec.Decode<LanSegmentsUpsertAck>(PcpCodec.Encode(ack)));
    }

    [Fact]
    public void StatsReport_BatchEntriesRoundTrip()
    {
        var report = new StatsReport(1, 1UL, MsgType.StatsReport,
        [
            new StatsEntry(Guid.NewGuid(), BytesUp: 1024, BytesDown: 2048, RelayBytes: 512),
            new StatsEntry(Guid.NewGuid(), BytesUp: 1UL << 40, BytesDown: 0, RelayBytes: 1UL << 40),
        ]);
        var decoded = PcpCodec.Decode<StatsReport>(PcpCodec.Encode(report));
        Assert.Equal(2, decoded.Entries.Length);
        Assert.Equal(1024UL, decoded.Entries[0].BytesUp);
        Assert.Equal(0UL, decoded.Entries[1].BytesDown);
        Assert.Equal(1UL << 40, decoded.Entries[1].RelayBytes); // 大累计值（>4GiB）不截断
    }

    // ── 0x73/0x74：重试协调与中继授权 ──────────────────────────────────────

    [Fact]
    public void PunchRetryAndRelay_RoundTrip()
    {
        var retry = new PunchRetry(1, 1UL, MsgType.PunchRetry, Guid.NewGuid());
        MsgAssert.Equal(retry, PcpCodec.Decode<PunchRetry>(PcpCodec.Encode(retry)));

        var alloc = new RelayAllocate(2, 2UL, MsgType.RelayAllocate, Guid.NewGuid());
        MsgAssert.Equal(alloc, PcpCodec.Decode<RelayAllocate>(PcpCodec.Encode(alloc)));

        // relaySessionId 为 RLP 外层 8B 会话头标识（ulong 非 Guid）
        var grant = new RelayGrant(3, 3UL, MsgType.RelayAllocate,
            RelaySessionId: 0x1122334455667788UL,
            RelayEndpoints: new EndpointPair(
                new Endpoint("203.0.113.20", 7010),
                new Endpoint("203.0.113.20", 7011)));
        var decodedGrant = PcpCodec.Decode<RelayGrant>(PcpCodec.Encode(grant));
        Assert.Equal(0x1122334455667788UL, decodedGrant.RelaySessionId);
        Assert.Equal(7010, decodedGrant.RelayEndpoints.Udp!.Port);
        Assert.Equal(7011, decodedGrant.RelayEndpoints.Tcp!.Port);
    }

    // ── 0x75：失效推送 reason 全枚举覆盖（完成判定②）───────────────────────

    [Theory]
    [InlineData(InvalidationReason.LoggedOut)]
    [InlineData(InvalidationReason.UserDisabled)]
    [InlineData(InvalidationReason.DeviceDisabled)]
    [InlineData(InvalidationReason.RemoteCodeReset)]
    [InlineData(InvalidationReason.LanSegmentRemoved)]
    [InlineData(InvalidationReason.GroupLeft)]
    [InlineData(InvalidationReason.GroupDissolved)]
    public void Invalidation_AllSevenReasons_RoundTrip(InvalidationReason reason)
    {
        var msg = new Invalidation(1, 1UL, MsgType.Invalidation, reason,
            [Guid.NewGuid(), Guid.NewGuid()], NewCapability: null);
        var decoded = PcpCodec.Decode<Invalidation>(PcpCodec.Encode(msg));
        Assert.Equal(reason, decoded.Reason);
        Assert.Equal(2, decoded.AffectedMappingIds.Length);
        Assert.Null(decoded.NewCapability);
    }

    [Fact]
    public void Invalidation_NewCapabilityOptional()
    {
        // 用户禁用降级：newCapability=passive 携带
        var msg = new Invalidation(1, 1UL, MsgType.Invalidation, InvalidationReason.UserDisabled,
            [], NewCapability: CapabilityMode.Passive);
        var decoded = PcpCodec.Decode<Invalidation>(PcpCodec.Encode(msg));
        Assert.Equal(CapabilityMode.Passive, decoded.NewCapability);
        Assert.Empty(decoded.AffectedMappingIds);
    }

    // ── 0x70.punchConcurrency 三态（完成判定③，OQ-19/TD-20）────────────────

    [Fact]
    public void PunchRequest_PunchConcurrency_ThreeStates()
    {
        // 缺省：不携带（旧端）→ roundtrip 保 null，Normalize=3
        var absent = new PunchRequest(1, 1UL, MsgType.PunchRequest, Guid.NewGuid(), null, "udp", null, null);
        Assert.Null(PcpCodec.Decode<PunchRequest>(PcpCodec.Encode(absent)).PunchConcurrency);
        Assert.Equal(3, PunchPolicy.Normalize(null));

        // 合法：1 与 5（闭区间边界）原样保留
        foreach (var n in (byte[])[1, 3, 5])
        {
            var valid = new PunchRequest(2, 2UL, MsgType.PunchRequest, Guid.NewGuid(), null, "tcp", null, n);
            Assert.Equal(n, PcpCodec.Decode<PunchRequest>(PcpCodec.Encode(valid)).PunchConcurrency);
            Assert.Equal(n, PunchPolicy.Normalize(n));
        }

        // 越界：0 与 6 roundtrip 保原值（schema 不丢信息），服务端侧 Normalize 归 3
        foreach (var n in (byte[])[0, 6, 255])
        {
            var bad = new PunchRequest(3, 3UL, MsgType.PunchRequest, Guid.NewGuid(), null, "udp", null, n);
            Assert.Equal(n, PcpCodec.Decode<PunchRequest>(PcpCodec.Encode(bad)).PunchConcurrency);
            Assert.Equal(3, PunchPolicy.Normalize(n));
        }
    }

    [Fact]
    public void PunchRequest_OldWire_WithoutConcurrencyField_Decodes()
    {
        // 加法演进兼容（02 §7）：M1 端编码的 7 元素数组（无 punchConcurrency）在 M2 schema 下可解
        var full = PcpCodec.Encode(new PunchRequest(1, 1UL, MsgType.PunchRequest,
            Guid.NewGuid(), null, "udp", null, null));
        Assert.Equal(0x98, full[0]); // fixarray(8)

        var oldWire = full[..^1]; // 去掉尾部 nil（第 8 元素）
        oldWire[0] = 0x97;        // 改写为 fixarray(7)
        var decoded = PcpCodec.Decode<PunchRequest>(oldWire);
        Assert.Null(decoded.PunchConcurrency);
        Assert.Equal("udp", decoded.Proto);
    }

    // ── 未知 msgType 容忍不回归（完成判定④，02 §7）────────────────────────

    [Fact]
    public void UnknownMsgType_Tolerance_NotRegressed()
    {
        // 手工构造 v1 表外 msgType=0x6E：DecodeLoose 不抛、Peek 只取头三字段
        var wire = new byte[] { 0x93, 0x11, 0x22, 0x6E };
        var loose = PcpCodec.DecodeLoose(wire);
        Assert.IsType<UnknownMessage>(loose);
        Assert.Equal(0x6E, loose.MsgType);

        // 新增消息体同样可被头三字段视图容忍（尾部字段不干扰 Peek）
        var header = PcpCodec.Peek(PcpCodec.Encode(new Invalidation(
            9, 99UL, MsgType.Invalidation, InvalidationReason.GroupLeft, [], null)));
        Assert.Equal(9u, header.Seq);
        Assert.Equal(99UL, header.TimestampMs);
        Assert.Equal(MsgType.Invalidation, header.MsgType);
    }

    /// <summary>0x75 reason 枚举与 02 §2.4 七值逐一对齐（AI-14：引用必须存在于设计表）。</summary>
    [Theory]
    [InlineData(InvalidationReason.LoggedOut, 0)]
    [InlineData(InvalidationReason.UserDisabled, 1)]
    [InlineData(InvalidationReason.DeviceDisabled, 2)]
    [InlineData(InvalidationReason.RemoteCodeReset, 3)]
    [InlineData(InvalidationReason.LanSegmentRemoved, 4)]
    [InlineData(InvalidationReason.GroupLeft, 5)]
    [InlineData(InvalidationReason.GroupDissolved, 6)]
    public void InvalidationReason_MatchesDesignTable(InvalidationReason reason, byte expected)
        => Assert.Equal(expected, (byte)reason);
}

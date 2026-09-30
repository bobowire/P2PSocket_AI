namespace P2P.Core.Protocol;

/// <summary>PCP 消息类型常量（02 §2.4 消息表 v1，单一事实来源，AI-11/AI-14）。</summary>
public static class MsgType
{
    // 握手与连接
    public const byte Hello = 0x01;            // Hello / HelloAck
    public const byte Proof = 0x02;            // Proof / ProofAck
    public const byte UpdateInfo = 0x03;       // UpdateInfoRequest / Response（升级引导，冻结接口）

    // 设备族
    public const byte Register = 0x10;
    public const byte RegisterAck = 0x11;
    public const byte UnbindMe = 0x12;
    public const byte DeviceUpdate = 0x13;     // DeviceUpdate / Ack（设备改名）
    public const byte RemoteCodeReset = 0x14;  // RemoteCodeReset / Ack

    // 用户族
    public const byte UserRegister = 0x20;
    public const byte UserLogin = 0x21;
    public const byte UserLogout = 0x22;
    public const byte UserChangePassword = 0x23; // UserChangePassword / Ack

    // 心跳与列表
    public const byte Heartbeat = 0x30;        // Heartbeat / HeartbeatAck
    public const byte DeviceList = 0x40;       // DeviceListRequest / Response
    public const byte DeviceListUpdate = 0x41; // 列表变更推送（提示帧）
    public const byte GroupList = 0x42;        // GroupListRequest / Response（已加入分组，M2-27 定案）

    // 分组族
    public const byte GroupCreate = 0x50;
    public const byte GroupJoin = 0x51;
    public const byte GroupLeave = 0x52;
    public const byte JoinRequests = 0x53;
    public const byte GroupInviteGen = 0x54;
    public const byte GroupUpdate = 0x55;
    public const byte GroupDissolve = 0x56;
    public const byte GroupRemoveMember = 0x57;

    // 映射与统计
    public const byte MappingUpsert = 0x60;
    public const byte MappingDelete = 0x61;
    public const byte MappingStatus = 0x62;    // M1 仅定义（FR-C-404 → M2 处理）
    public const byte LanSegmentsUpsert = 0x63; // LanSegmentsUpsert / Ack
    public const byte StatsReport = 0x64;      // StatsReport（批量累计上报）

    // 打洞与信令
    public const byte PunchRequest = 0x70;     // PunchRequest / Ack
    public const byte PunchInvite = 0x71;      // S→C 发往被邀请方
    public const byte PunchResult = 0x72;      // M1 仅定义
    public const byte PunchRetry = 0x73;       // PunchRetry（回切直连协调）
    public const byte RelayAllocate = 0x74;    // RelayAllocate / RelayGrant
    public const byte Invalidation = 0x75;     // 失效推送（reason 七值）
    public const byte PunchEndpoint = 0x76;    // C→S 被邀请方端点回传（OQ-18/TD-19）

    // 其他
    public const byte Error = 0x7E;
}

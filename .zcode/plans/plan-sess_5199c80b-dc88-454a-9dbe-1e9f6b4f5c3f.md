# M6-01~06 · KCP 协议集成（UDP 承载可靠化）

## Context
M2-38 遗留：UDP 直连承载为裸数据报——丢 DATA 帧字节流缺段、丢 WINDOW 信用发送侧永久挂起、丢 OPEN 帧断连。设计文档 11 号（v0.2，已批复 7/7 决议点）+ 任务清单 M6：打洞成功后的 UDP 直连承载升级为「单 socket KCP 多 conv（conv=channelId）按 PTP channel 分流 + 裸 UDP 旁路」，channel 级可靠有序、channel 间队头隔离，打洞流程/PTP 握手/加密体系零语义变更。本次完成 M6-01~06（协议核→承载→会话缝→caps 协商→Puncher 接线→弱网红绿矩阵）；M6-07/08（基准调参/部署文档）后续单独做。

## 实施步骤（严格串行，每步含单测红绿）

### M6-01 · KCP 协议核自研移植 — `src/P2P.Core/Transport/Kcp/`（新建）
- **KcpPeer.cs**（单 conv 端点，~600 行）：协议数学对照 skywind3000/kcp 参考实现 1:1 移植（实现时拉取 ikcp.c 逐函数对照）——段头 24B LE `conv u32|cmd u8|frg u8|wnd u16|ts u32|sn u32|una u32|len u32`，cmd PUSH=81/ACK=82；RTT/SRTT/RTTVAR 估计、RTO 退避（nodelay ×1.5）、快重传（fastack≥resend）、una 滑动、ACK 列表（ts 回显）、flush MTU 攒包、rcv_buf→rcv_queue 有序搬移。裁剪：frg 恒 0（流模式）、WASK/WINS、dead_link（重传到会话消亡为止）。**WINS 裁剪的必要补偿**：应用读出后接收窗重开（0→正或增长过半）时发窗口通告 ACK 携带新 wnd——否则 rmt_wnd=0 且 snd_buf 空时对端永久死锁（壳层补丁，不动协议数学，代码注记说明）。
- **注入缝**：`IKcpDatagramSink`（flush 输出）+ `KcpPeer.Input(ReadOnlySpan<byte>)`（收包注入）；算法层同步纯函数、不认 Socket、无 static 态；TimeProvider 注入（FakeTimeProvider 确定性单测）。KcpSegment struct + ArrayPool 池化数据缓冲。
- **单测**（Core.Tests 新增 `KcpPeerTests.cs` + 可编程介质脚本桩）：受控丢包 0~30%×乱序×重复下可靠有序（零缺段/零重复交付/零死锁）、闭合窗收敛（snd_buf 排空）、RTO 退避序列、快重传触发点对拍。

### M6-02 · KcpTunnelTransport — `src/P2P.Client/Tunnel/KcpTunnelTransport.cs`
- **Adopt(socket, pinnedEndpoint, options)** 静态工厂（§5.4）；构造即建 conv 0；实现 `IKcpConvControl`。
- **收包泵**：魔数 0x4B→conv 表（ConcurrentDictionary，Actor 豁免点）命中→`Input`+立即提取帧；未命中丢弃+`kcp_unknown_conv_dropped` 计数。非魔数→防御校验 `ParseHeader ver==1 && type∈{UdpDgram 0x09, Frag 0x0B}`→裸接收队列（有界 256 DropOldest）；其余丢弃+计数——**顺带修复迟到 STUN 应答整隧道误杀缺陷**。
- **帧提取**：per-conv 流缓冲，16B 头+CipherLen 自分界（§5.5 PTP 帧自带长度）→汇入单一接收 Channel（无界；背压由 KCP rcv_wnd 通告承载）；ParseHeader 失败=流破坏→承载 fault→ReceiveAsync null→断链（SEC-12 同口径）。
- **发包路由**（§4.1 路由表）：OPEN/KEEPALIVE/PING/PONG/REKEY 族→conv 0；OPEN_RESULT/DATA/CLOSE/WINDOW→conv=channelId（未登记或超 ConvCap=256→降级 conv 0+计数）；UDP_DGRAM/FRAG→裸 `SendToAsync` 钉定端点。整帧 ≤1400B 校验；KCP 写入 snd_queue 即返（不阻塞 `_sendGate`，在途量由 WINDOW 信用+SndWnd 界定）。发送后立即 flush（壳层行为，PING RTT 增量门禁依赖；Input 末尾遇 PUSH 即 flush 与 ikcp.c 一致）。
- **更新泵**：单 PeriodicTimer(10ms) 驱动全 conv Update；输出经单发送任务串行 SendToAsync。
- **单测**（Client.Tests）：真实 loopback socket 对 + 中间人注入介质——帧型×conv 路由穷举、unknown-conv 拒绝、conv 上限降级、Adopt 单次性、裸路径白名单闭包、裸队列 DropOldest。

### M6-03 · 会话层三缝
- 新建 `src/P2P.Client/Tunnel/IKcpConvControl.cs`：`EnsureChannel/RemoveChannel`——transport 未实现即 no-op。
- **TunnelSession.cs** 五接线点（均幂等）：SendOpenAsync 前置 Ensure；接收循环 Open case `_handler.OnOpen` 前 Ensure；OpenResult !Ok→Remove；Close case→Remove；`RemoveChannelCredit()` 追加 Remove（覆盖本地关闭/引擎侧路径）。
- **ReplayWindow.cs**：WindowSize 64→4096（决议②），`ulong` 单字位图改 `ulong[64]`，同 counter 重放拦截语义不变；Core.Tests 扩回归（跨 conv 乱序深度注入/出窗拒绝/重放拦截）。
- **单测**：KCP 承载上全帧型往返（复用 TunnelSessionTests.EstablishAsync 模式）；TCP/中继/裸承载 no-op 零回归（现有套件兜底）。

### M6-04 · caps 协商
- **Core**：`Protocol/BearerCaps.cs`（bit0=CapKcp；`KcpEnabled(myCaps, peerCaps)` 两端同式公式）；四消息尾部加 `Key(9) uint?`——PunchRequest.BearerCaps / PunchInvite.PeerBearerCaps / PunchEndpoint.BearerCaps / PunchRequestAck.PeerBearerCaps（缺省 null，MessagePack 尾部加法惯例）。
- **服务端**（SignalingCoordinator）：PunchSession 加 `InitiatorBearerCaps`；`StartSessionAsync` 存 `msg.BearerCaps`、invite 构造透传；`HandlePunchEndpointAsync` Ack 构造透传 `msg.BearerCaps`——纯存转零解析两处。
- **客户端**：`ClientSettings.KcpBearer`（默认 true）；`ClientRuntimeOptions.DisableKcpBearer`（DisableLatePunch 同款测试缝）；`PunchRequestSender/EndpointReporter` 委托签名加 `uint? bearerCaps` 参数（ClientRuntime 接线+测试假件同步）；Puncher ctor 加 `Func<uint>? localBearerCaps`；AttachPuncher 0x70/0x76 工厂发送时点取值上送。Web 设置链：SettingsApi DTO+kcpBearer 开关+ExportTs 重生成前端类型。
- **单测**：四组合判定一致性；混布矩阵（新新=KCP/单旧=裸/旧服务端剥字段=双端同判 false）；服务端存转不解析；序列化尾部截断容忍（旧端模拟）。

### M6-05 · Puncher 接线
- **InitiateUdpAsync/RespondUdpAsync 尾段承载切换分支**：caps 判定（A=Ack.peerBearerCaps、B=Invite.peerBearerCaps）true→Puncher 侧外驱动握手（`PtpHandshake.StartInitiator/AcceptTHello1` over UdpPunchTransport 裸帧，THello1 连发语义照抄 ConnectAsync）→ `KcpTunnelTransport.Adopt(socket, transport.Peer)` → `transport.ReleaseSocket()` → `FromEstablishedKeys(…, kcp, …, staticKey, peerStaticPub)`（M2-16 先例）；false→现状裸路径**逐字节不动**。PunchOptions 加 KcpTunnelTransportOptions 透传（§5.4 默认表）。
- **UdpPunchTransport**：加 `ReleaseSocket()`（Interlocked 标记，DisposeAsync 不再关 socket）；Puncher finally 所有权链更新（Adopt 后失败由 kcp.DisposeAsync 关 socket）。
- **单测**：两态全矩阵（kcp on/off×打洞成功）、Adopt 后 socket 所有权单次性、KEEPALIVE/OPEN 首帧走 KCP 实证。

### M6-06 · 弱网红绿矩阵（立项级证据）
- **UdpNatSimulator** 加注入旋钮：丢包率/乱序深度/重复率/延迟抖动/突发档（200ms 窗连片丢，决议⑥）。
- **红绿矩阵**（IntegrationTests 新增）：丢包 0/1/5/20%×乱序×100ms 抖动+突发档，8 并发映射持续传输——KCP 组零缺段/零 credit 挂起/会话存活；**裸 UDP 对照组同条件复现 M2-38 家族故障（红证）**。
- **黑洞档**：全丢下 KCP 重传不伪造活性（缩短 keepalive 的 3×miss 断链照常）。
- **场景回归**：S54/命中矩阵 kcp on/off 两态；Integration 114 基线不减。

## 验证
1. 每任务完成即跑对应套件红绿；KCP 数学关键场景对拍参考实现（RTO 退避序列/快重传触发点断言）。
2. 最终全量：五测试套件本机串行（Core 196+/Nic/Client 222+/Server 303+/Integration 114+ 基线不减，新增用例在外）+ `dotnet build -c Release` 0 警 0 错（TreatWarningsAsErrors=true）。
3. 任务清单 `docs/任务清单/M6-UDP承载可靠化.md` 状态行随完成逐项勾选（M6-01~06）。

## 不做（本次，防范围蔓延）
M6-07 基准调参（P2P.Bench 四组对照/丢包-吞吐曲线/参数回填）、M6-08 部署灰度与 §13 文档全表修订、中继 UDP 承载可靠化、混淆/抗指纹、会话中途承载切换。
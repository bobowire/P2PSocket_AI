# 11 · UDP 承载可靠化设计（KCP 多 conv · 最终形态）

> 版本：v0.2　创建日期：2026-10-25　状态：**已批复（2026-11-14 评审，决议点 7/7，M6 立项）**
> 依据：用户指令（2026-10-25，维护期扩展——"直接进入单 socket 多 conv 按 channel 分流的最终形态"）；PRD 条目 NET-77 已回流（v0.16，2026-11-14）
> 定位：UDP 打洞直连承载的可靠化改造施工图纸。核心文档修订（02/05/07/09/10/README）在评审批复后随落地同步（§13 修订点清单），本文档先行承载全部设计内容。
> 关联：02 §4.2/§4.3/§4.5/§5.1、05 §0/§2.3/§2.4/§3/§4、07 §9、09 §2.2/§2.3/§3、README TD-15/19/20/29（v0.1 原文"TD-38"系笔误，全库无此编号，v0.2 勘误）

## 0. 摘要与决议请求

**一句话**：打洞成功后的 UDP 直连承载从"裸 UDP 数据报"升级为"单 socket 上 KCP 多会话（conv）按 PTP channel 分流 + 裸 UDP 旁路"——channel 级可靠有序、channel 间队头隔离、UDP_DGRAM 保持丢包容忍；TunnelSession 帧逻辑、加密体系、打洞流程、成功判定全部不动。

**决策点**（原建议见下表，明细见对应章节；批复结果见下段）：

| # | 决策点 | 建议 | 章节 |
|---|---|---|---|
| ① | conv 映射模型 | conv=channelId（32bit），conv 0=全局控制流；OPEN 走 conv 0，OPEN_RESULT/WINDOW/CLOSE/DATA 走 channel conv | §4 |
| ② | 防重放窗口 | ReplayWindow 64 → 4096（跨 conv 乱序深度吸收；接收侧策略，wire 兼容） | §6 |
| ③ | 能力协商载体 | 0x70/0x71/0x76/0x70 Ack 四消息加可选字段 bearerCaps（u32 位图），服务端纯存转；判定公式两端同式本地计算 | §8 |
| ④ | KCP 算法层来源 | 决策标准驱动三候选：KcpSharp → kcp2k 抽核 → 自研移植 skywind3000/kcp；落地任务 T1 定案 | §2.3 |
| ⑤ | channel conv 上限 | 256/会话（对齐 TD-15 量级），超限 channel 全帧降级 conv 0（保正确性牺牲隔离） | §4.3 |
| ⑥ | 验收门禁线 | 0% loss 回环：KCP 吞吐 ≥ 裸 UDP 70% 且 RTT 增量 ≤ 5ms；5% loss 注入下 8 并发映射零缺段零挂起 | §12 |
| ⑦ | PRD 回流与编号 | 新增 NET-77（PRD v0.14+）、TD-29、任务清单里程碑编号（M5 后新立或维护期） | §0/§14 |

**批复记录（2026-11-14，7/7）**：①②③⑤⑦ 按建议批复（⑤ 与 ① 同章 §4.3，随模型一并成立）；两处修订——④ 改为**定案自研移植**（不再等 T1 三候选筛选，§2.3 已重写），⑥ 正确性闸升级为**弱网矩阵全格+新增突发档**（公网高丢包主流形态为连片丢而非均匀撒点，§12 已重写）。落地载体：新立 **M6 · UDP 承载可靠化**（任务清单 v1.0，T1-T8=M6-01~08）、NET-77（PRD v0.16）、TD-29 定案（README v1.84）。

## 1. 背景与动机

### 1.1 现状缺陷链（既有定案，引用不改写）

- UDP 打洞成功 → 绑定 UDP 承载（02 §4.5 承载绑定行）；承载=裸 UDP 数据报，一数据报一帧，发送即忘（`UdpPunchTransport`）。
- 会话层无重传：丢 DATA 帧字节流缺段（应用收不满卡死）/ 丢 WINDOW 信用发送侧永久挂起 / 丢 OPEN 帧连接即断——02 §6.2 M2-38 反转动机的原文已如实承认；单帧丢失触发不了 KEEPALIVE 断链兜底，卡住的 channel 就一直卡着。
- M2-38 的对策是中继 JOIN 反转 TCP 优先（7020），**UDP 直连路径本身没有获得任何补救**——直连失败才去中继，直连成功但弱网丢包时无帧级恢复。

### 1.2 目标

1. UDP 直连承载上 DATA/控制帧**不丢不乱**（channel 内严格有序）；
2. **channel 间队头隔离**——一条重载映射的弱网重传不拖累其它 channel 与控制帧（相对 TCP 承载的全局单流是超越点；TCP 承载不改造，其全局单流 HOL 维持现状）；
3. UDP_DGRAM（UDP 映射数据报）语义**保持丢包容忍**，不被可靠层的重传延迟污染；
4. 打洞流程、PTP 握手、成功判定、加密体系、信令协议骨架**零语义变更**；
5. 混布安全：新旧客户端/服务端任意组合可互通（自动降级裸 UDP）。

### 1.3 非目标

- TCP 直连承载与中继 TCP 承载不加 KCP（TCP 本身可靠，叠加纯开销）；
- 中继 UDP（7010）承载可靠化——二期复用本期 `KcpTunnelTransport`（同类裸 UDP 承载，JOIN UDP 模式），本期不做；
- 混淆/抗指纹（udp2raw 类）——02 §4.3 IObfuscationAdapter 扩展点维持预留，本期直通（§9.4 说明 KCP 魔数与该扩展点的衔接）；
- 更换 PTP 加密体系、QUIC 化、多路径（MP-TCP 类）。

## 2. 选型结论与决策记录

### 2.1 TD-29（定案 2026-11-14）：UDP 直连承载可靠层选 KCP，多 conv 最终形态

| 决策 | 内容 |
|---|---|
| 结论 | KCP 作为 UDP 直连承载的可靠层，**跳过"单 conv 过渡版"直接落多 conv 最终形态**（用户指令 2026-10-25） |
| 否决 QUIC | ① socket 所有权死穴：打洞的全部前提是 STUN 探测/连发/数据同用**一个本地 socket**（NAT 映射绑定 local endpoint）；System.Net.Quic/MsQuic 自建自持 UDP socket、无收编现有 socket 的 API，换本地端口=换 NAT 映射=洞白打；让 QUIC 承载生效须把打洞流程本身重做成 QUIC 包打洞，而届时又无法对 QUIC socket 做 STUN 探测——0x70/0x76 两段式、扇发、钉定全部推倒。② 加密重复：QUIC 强制 TLS 1.3，与 PTP AEAD 双层加密或整体替换安全面（证书体系/REKEY→TLS key update/设备预置重做）。③ 原生依赖：Linux 端 libmsquic（08 部署面复杂化）。④ per-stream 优势与 PTP channel 模型重叠。 |
| 否决自研 ARQ | PTP counter 虽是现成序号，但 RTO 估计/快重传/拥塞窗口全套自写自测=重复造 KCP 已大规模验证的轮子 |
| 否决单 conv 过渡版 | 单 conv=全 channel 共享一个有序队列：弱网下一条重载映射的重传队头阻塞拖死全部 channel 与 conv 0 控制帧（KEEPALIVE 延迟→误判断链风险）；多 conv 直接消解，增量复杂度集中在 conv 表 + 会话层三缝（§6），可控。 |
| 单 socket | 打洞产物 socket 全程独占（探测→连发→握手→数据），KCP 层必须能外挂该 socket 的收发——这是相对 QUIC 的决定性约束，也是 KCP 的决定性优势。 |

### 2.2 与既有架构扩展点的吻合

- **02 §4.3 混淆扩展点**：`IObfuscationAdapter`（直通/混淆，P2 不实现）预留的正是"帧格式外层传输包装层"——KCP 承载即该槽位的第二个直通实现：PTP 帧格式与加密层零改动。
- **M2-16 先例**：TCP 打洞已存在"握手在 Puncher 侧驱动、`TunnelSession.FromEstablishedKeys` 以既得密钥启动会话"的模式——承载与握手解耦已验证。
- **帧头明文可分流**：PTP 帧头 16B 明文（ver|type|channelId|counter|u16len，AEAD 的 AAD，`PtpFrameCodec.ParseHeader` 直接作用于 wire bytes）——承载层**不解密**即可按 type+channelId 路由，这是多 conv 分流可行性的根基。

### 2.3 KCP 算法层来源：自研移植（决议点 ④，2026-11-14 评审定案）

**定案**：自研移植，不引入外部 KCP 库——原三候选降为参考实现。协议数学（RTO 估计/快重传/nodelay 收紧曲线/拥塞窗/ACK-una 滑动）对照 skywind3000/kcp 参考实现 **1:1 移植、不重新发明**（改协议数学=自找回归面）；壳层按本项目约束定制（"适合本项目"=壳，不是数学）。

| 参考（均 MIT，文件头 attribution 注记） | 借鉴面 |
|---|---|
| skywind3000/kcp（C 原版） | 协议行为基准真值：段头格式、flush 时序、重传/窗口公式 |
| kcp2k Kcp.cs | C# 段队列写法与生产验证过的边界处理 |
| Cysharp/KcpTransport | 现代 API 形态（Span/Memory 收发、会话端点接口——KcpPeer API 对标） |
| limpo1989/kcp-csharp | zero-GC 技巧（struct 段、池化缓冲——257 conv×10ms 泵的分配纪律） |

**原五条决策标准转为自研版构造性质**：① 外部 socket 收发注入（IKcpDatagramSink/Source，算法层不认 Socket 类型——QUIC 否决案死穴同源）；② 多实例无全局态（≤257 conv/会话 × 多会话并存）；③ 纯托管；④ **只做流模式**（PTP 帧自带 u16 长度自分界，§5.5——裁 frg 分片与 WASK/WINS，消息模式不做）；⑤ 许可自持（MIT attribution 保留四参考出处）。

**本项目裁剪定案（评审新增）**：**dead_link 放弃计数不实现**——重传到会话消亡为止、永不主动放弃（高丢包下零挂起的前提；活性判定归 KEEPALIVE 3×20s miss 断链，§11.6）。

**代价（诚实账）**：代码预算 <700 → ~1400-1600 行（算法核 ~600-900 行新增，大头前移 T1=M6-01）；测试矩阵自担由 T1 纯算法属性单测（强制门禁）+T6 弱网红绿矩阵兜；移植失真风险由属性测试对拍参考实现兜底（§15 风险表随更）。

## 3. 总体架构

### 3.1 分层与分流全景

```
TunnelSession（PTP 帧：AEAD/counter/channel/credit/keepalive/rekey）—— 语义零改动（三缝除外，§6）
ITunnelTransport —— 接口零改动（SendAsync 一帧 / ReceiveAsync 一帧）
└── KcpTunnelTransport（新，UDP 直连承载专用）
    ├── conv 0（全局控制流）：KEEPALIVE / PING / PONG / REKEY / REKEY_ACK
    ├── channel conv ×N（conv=channelId）：OPEN_RESULT / WINDOW / CLOSE / DATA
    ├── 裸 UDP 旁路：UDP_DGRAM / FRAG（丢包容忍语义保持）
    └── 单 socket 收包泵：魔数分流（0x4B=KCP 包，其余=裸路径）
UdpPunchTransport（保留）：打洞期承载（THello1/2/TConfirm 裸帧）——握手完成 socket 移交
```

### 3.2 承载形态对照

| 承载 | 可靠性 | 有序性 | 队头隔离 | 本期 |
|---|---|---|---|---|
| UDP 直连·裸（现状） | 无 | 无（窗口 64 容差） | — | 保留（混布降级形态） |
| **UDP 直连·KCP 多 conv** | **per-conv 重传** | **per-conv 严格有序** | **channel 级** | **本期** |
| TCP 直连 | TCP | 全局单流 | 无（既有） | 不动 |
| 中继 TCP（JOIN 默认） | TCP | 全局单流 | 无（既有） | 不动 |
| 中继 UDP（7010 兜底） | 无 | 无 | — | 二期复用本期成果 |

## 4. conv 路由设计

### 4.1 路由表（帧 → conv，承载层按明文帧头判定，不解密）

| PTP 帧 | 值 | conv | 理由 |
|---|---|---|---|
| OPEN | 0x01 | **conv 0** | 接收侧只能从已建立的 conv 学知新 channel（§4.4 无 lazy accept——安全边界）；双方 conv 0 会话建立即存在 |
| OPEN_RESULT | 0x02 | channel conv | OPEN_FAIL 路径同流；发起侧此时必已建 conv（SendOpenAsync 前建） |
| DATA | 0x03 | channel conv | channel 内严格有序=字节流语义修复的核心 |
| CLOSE | 0x04 | channel conv | 与该 channel DATA 同流有序——CLOSE 不会越过尾部 DATA 提前交付（裸 UDP 现状可能，TCP 承载单流天然免疫；同 conv 恢复此保证） |
| KEEPALIVE / PING / PONG / REKEY / REKEY_ACK | 0x05~0x08/0x0C | conv 0 | 全局帧；量小，HOL 无虞 |
| WINDOW | 0x0A | channel conv | 与同 channel DATA 同流，记账顺序自然 |
| UDP_DGRAM / FRAG | 0x09/0x0B | **裸旁路** | 丢包容忍语义保持（TD-15/05 §2.4）；不被重传延迟污染 |
| THello1/2/TConfirm | 0x11~0x13 | 打洞期裸帧 | 不进 KCP（§7） |

**基础事实**：channelId 分配跳过 0（`_nextChannelId = random | 1u`，"跳过 0 与握手保留值"）→ conv 0 专用无碰撞；两侧独立分配靠 32bit 随机起点避碰撞（02 §4.3 既有口径）→ conv=channelId 完整承袭该风险模型（碰撞概率与现状同级，不新增）。

### 4.2 conv 生命周期

| 时机 | 动作 |
|---|---|
| 会话构造（两端） | 建 conv 0（deterministic，无需协商） |
| 访问侧 SendOpenAsync 前 | `EnsureChannel(channelId)`（A 侧在 OPEN 上 wire 前建好） |
| 目标侧 OnOpen（接收循环内） | `EnsureChannel(channelId)`（OPEN 经 conv 0 解密校验后才登记——鉴权前置） |
| OnOpenResult !Ok / OnClose / 本地关闭 | `RemoveChannel(channelId)`（排空后销毁 conv 端点；与 `RemoveChannelCredit` 同点触发） |
| 会话销毁 | 全 conv 端点 + 泵 + socket 一并释放 |

### 4.3 未授权 conv 防线（不做 lazy accept）

- KCP 层无认证：接收侧对 **conv ∉ {0 ∪ 已登记 channel 集}** 的 KCP 包一律丢弃 + 计数（`kcp_unknown_conv_dropped`，诊断暴露可选）。conv 集的每一条目来源都是经 AEAD 解密校验的 OPEN 帧——未授权方无法凭空造 conv。
- channel conv 上限 256/会话（对齐 TD-15 每 UDP 映射 256 并发量级）：超限 channel 的**全部帧（含 DATA）降级走 conv 0**——保正确性、牺牲隔离，日志计数（决议点 ⑤）。
- 迟到包：channel 关闭后该 conv 端点销毁，后续包落 unknown 丢弃路径（对端重传耗尽自然放弃，KCP 自愈）。

### 4.4 有序性语义与防重放窗口

- per-conv 严格有序；**跨 conv 无全局序**。M2_38 不变量"counter 序=线上序"（闸内写）收窄表述为：**counter 序=conv 内线上序**（同 conv 帧在 `_sendGate` 内分配 counter 后依序入队，KCP 依序交付）。
- 跨 conv 乱序深度上界 ≈ 每 channel 在途 64KiB 信用 ≈ 48 帧（@1368B）× channel 数 + conv 0 少量控制帧。窗口 64 必然不够 → **ReplayWindow.WindowSize 64 → 4096**（bitmap 512B；窗口只影响"旧帧迟达"容忍度，同 counter 重放拦截不受窗口大小影响——安全面无实质变化）。接收侧策略、wire 兼容、两端可独立升级（决议点 ②）。
- 裸旁路帧（UDP_DGRAM/FRAG）同过 ReplayWindow（现状口径不变），其在途深度小，4096 余量充足。

## 5. KcpTunnelTransport 模块设计

### 5.1 收包泵（单 socket Actor）

```
ReceiveFromAsync 循环：
  datagram[0] == 0x4B（魔数）
    → 解 conv（随后 4B LE）→ conv 表命中 → 端点 Input()；未命中 → 丢弃 + kcp_unknown_conv_dropped++
  非魔数（裸路径）
    → 防御校验：ParseHeader ver==1 且 type ∈ {UDP_DGRAM(0x09), FRAG(0x0B)} → 裸接收队列
    → 其余（迟到握手帧/迟到 STUN 应答/垃圾）→ 丢弃 + 计数
```

- **裸路径类型白名单**是 KCP 模式下的完整闭包（其余全部帧型走 KCP），顺带修复一个现状潜伏缺陷：**迟到 STUN 重传应答**（首字节 0x01 恰与 PTP ver 同值，type 字节 0x01=OPEN 形似）在现状裸承载上会以形似 OPEN 的垃圾帧进入接收循环——MessagePack 反序列化异常未被 ProtocolException 过滤器捕获 → `Disconnect("receive_failed")` 整隧道误杀；白名单后此类包降级为丢弃+计数。
- 裸接收队列有界（256 条，FullMode=DropOldest+计数）：UDP_DGRAM 丢一包=语义内丢失（可容忍），绝不阻塞收包泵（泵阻塞会拖累全部 conv 的 KCP ACK 时效）。
- conv 交付的完整 PTP 帧（KCP 流内自分界，§5.5）与裸队列帧汇入**单一接收队列** → `ReceiveAsync` 消费（TunnelSession 单接收循环契约不变）。

### 5.2 发包路径

`SendAsync(frame)` → `ParseHeader`（明文头）→ 路由表判定：
- UDP_DGRAM/FRAG → `socket.SendToAsync(frame, 钉定端点)`（与现状 UdpPunchTransport 钉定后行为一致）；
- 其余 → 路由表查 conv → 写入对应 KCP 会话（流式写帧字节，§5.5）→ KCP 更新泵负责分片 flush。

### 5.3 更新泵

单 `PeriodicTimer`（默认 10ms）驱动全部 conv 的 Update/Flush（避免 N 个定时器；conv 表遍历 O(N) 每 tick，N≤257 量级 CPU 可忽略）。空闲优化项（可后置）：全部 conv 无待发且无待 ACK 时泵降频 200ms。Actor 纪律（05 §0）：conv 表为共享可变状态白名单豁免点（ConcurrentDictionary，与 `_credits` 同款口径），收包泵/更新泵/发送方三面并发访问。

### 5.4 socket 接管与参数

- `KcpTunnelTransport.Adopt(Socket, sessionId, pinnedEndpoint, options)`：握手成功后从 `UdpPunchTransport` 接管 socket（`UdpPunchTransport.Peer` 钉定端点此时已定——首个到达包钉定语义不变）；PunchOutcome 形状不变。
- 参数表（`KcpTunnelTransportOptions`；**M6-07 校准回填 2026-11-15**：默认参数经基准与弱网矩阵实证确认，见下表注记与基准报告 v1.1 §5）：

| 参数 | 默认 | 说明 |
|---|---|---|
| UpdateInterval | 10ms | 更新泵周期 |
| Nodelay | 1 | 快速模式（无延迟 ACK 等待） |
| Resend | 2 | 快重传触发（跳过已被 ACK 的后续段数） |
| Nc | 1 | 关 KCP 拥塞窗。**M6-07 校准注记**：原预设「KCP CC 冗余且弱网伤吞吐」在 0% loss 回环被实测推翻——nc=0 吞吐系统性高 20-24%（162 vs 131Mbps，87.7% vs 70.7% 闸比值）；维持 nc=1 默认=弱网优先（M6-06 弱网红绿矩阵全系 nc=1 全格绿，nd=1 快重传+iv=10 ACK 时效为弱网恢复性实测支柱；nc=0 弱网档未验证，cwnd 丢失收缩后发送窗恢复慢的弱网风险未测），吞吐敏感场景可配 |
| SndWnd / RcvWnd | 64 / 64 | per conv；对齐单 channel 信用窗帧数（48）+余量 |
| MTU | 1400 | 对齐 02 §4.3 IP 分片规避；MSS≈1376 |
| Magic | 0x4B | demux 魔数 |
| ConvCap | 256 | channel conv 上限（超限降级 conv 0） |
| RawQueueCap | 256 | 裸旁路接收队列（DropOldest） |

> **M6-07 校准结论（基准报告 v1.1 §5，2026-11-15）**：性能闸 PASS——0% loss 回环吞吐比 70.7%（≥70%）且 PING p50 增量 0.052ms（≤5ms）；调参矩阵 24 cell 维持默认族推荐（见 Nc 行注记）；丢包-吞吐曲线 0→30% 完整性恒成立（30% 档仍 45.6Mbps 有效吞吐）。小帧档（100B）KCP 反超裸承载 10.8 倍（裸满速内核收缓冲溢出丢包 vs KCP rcv_wnd 背压）——rcv_wnd 通告背压的机理红利实证。

### 5.5 承载模式：流式 + PTP 帧自分界

PTP wire 帧长 = 16B 头 + CipherLen（头内 u16 明文）——**帧自带长度字段**，KCP 流模式下接收侧无需额外分帧前缀，逐帧解析即自分界。④ 自研定案后**只做流模式**（消息模式不做）——帧自带长度字段使流模式零额外分帧成本，且裁 frg 后段头更简；原"按候选库 API 形态定两模式"随定案废止。DATA 分块维持 `MappingEngine.ChunkSize=1368B` 不变（全承载统一，零 MappingEngine 改动；sealed 帧 1400B 超 MSS≈1376 会拆 2 段，~1.7% 段开销，接受；对齐优化（ChunkSize 拆分常量使单帧=MSS）列为可选后置项）。

### 5.6 背压

双层各司其职：WINDOW 信用（64KiB/channel，端到端消费背压，TunnelSession 既有）+ KCP SndWnd（64 段/conv，承载在途窗）。发送侧顺序：DATA 先过 CreditGate 再入 conv 队列——KCP 不放大在途量；credit 耗尽挂起语义不变，且**WINDOW 回报帧本身不再丢**（KCP 重传），M2-38 家族"发送侧永久挂起"根除。

## 6. 会话层接缝（TunnelSession 全部改动点）

| 缝 | 改动 |
|---|---|
| `IKcpConvControl`（新可选接口） | `void EnsureChannel(uint channelId); void RemoveChannel(uint channelId);`——transport 未实现（TCP/中继/裸 UDP 承载）即 no-op，零回归 |
| SendOpenAsync | 发送前置 `(_transport as IKcpConvControl)?.EnsureChannel(channelId)` |
| 接收循环 Open case | `_handler.OnOpen` 前 `EnsureChannel(channelId)`（目标侧 conv 就位于 OnOpen 交付） |
| OnClose / RemoveChannelCredit 路径 / OnOpenResult !Ok | 追加 `RemoveChannel(channelId)` |
| ReplayWindow | `WindowSize` 64 → 4096（决议点 ②；纯常量+bitmap 容量，单测回归） |

不改动：握手三帧、AEAD/counter 分配（`_sendGate` 闸内序不变）、KEEPALIVE/REKEY/PING 循环、信用账本、UDP_DGRAM/FRAG 重组、ViaRelay 判定（`transport is RelayTransport` 与 KCP 正交）。

## 7. 打洞与承载切换（流程零语义变更）

1. 打洞期（THello1/2/TConfirm 裸帧）：**逐字不动**——双方连发开 NAT（B 侧裸 THello1 连发的 NAT 开洞职责保留：address-dependent NAT 下被动 ACK 不足以先开洞，时序死锁风险不引入）、成功判定=加密握手全程跑通、首包钉定回程端点、10s 窗口。
2. 握手成功瞬间承载切换（Puncher 双角色同款，`InitiateUdpAsync`/`RespondUdpAsync` 尾段）：
   ```
   caps 判定 kcpEnabled？（§8，握手前已完成——双方在开火前即各知两端 caps）
     ├─ true  → KcpTunnelTransport.Adopt(socket, sessionId, transport.Peer, …)
     │          → TunnelSession.FromEstablishedKeys(…, kcpTransport, …)   // M2-16 缝既有
     └─ false → TunnelSession.FromEstablishedKeys(…, udpPunchTransport, …) // 现状路径
   ```
3. Adopt 语义：socket 所有权移交 KCP 承载（`UdpPunchTransport` 释放不关 socket）；conv 0 两端构造即建；首个会话帧（KEEPALIVE 或 OPEN）起即走 KCP。会话中途**不可**在 KCP/裸间切换——承载绑定与会话同生共死（02 §4.5 口径），断链重建时重新协商。
4. caps 判定与 0x78 晚探测（TCP 路径）正交；UDP 打洞窗、信令时序、服务端 SignalingCoordinator 状态机均无涉。

## 8. 能力协商与混布

### 8.1 载体（决议点 ③）

| 消息 | 新增可选字段 | 说明 |
|---|---|---|
| 0x70 PunchRequest | `bearerCaps: u32` | 发起方能力位图，服务端**纯存转**零解析（`localCandidates` v1.63 先例） |
| 0x71 PunchInvite | `peerBearerCaps: u32` | 透传 A 的 bearerCaps 给 B |
| 0x76 PunchEndpoint | `bearerCaps: u32` | B 的能力位图 |
| 0x70 Ack | `peerBearerCaps: u32` | 透传 B 的 bearerCaps 给 A |

位定义：bit0 = KCP-BEARER（本期唯一位；u32 余量后续扩展）。MessagePack 尾部可选项缺省=0=不支持（`punchConcurrency` TD-20 / `localCandidates` NET-76 同款加法惯例）。

### 8.2 判定公式（两端同式、仅本地输入、无第三态）

```
kcpEnabled = (myCaps & peerCaps & CAP_KCP) != 0
A 侧：my=随 0x70 上送值，peer=Ack.peerBearerCaps        （Ack 到手 → 开火前判定完毕）
B 侧：my=随 0x76 上送值，peer=Invite.peerBearerCaps     （Invite 到手 → 开火前判定完毕）
```

两端输入对称可得、公式同式 → 判定必然一致（单测断言四组合一致），无脑裂窗。

### 8.3 混布矩阵

| 组合 | 行为 | 机理 |
|---|---|---|
| 新 A + 新 B + 新 S | **KCP** | caps 双向透传 |
| 新 A + 新 B + 旧 S | 裸 UDP（一致降级） | 旧服务端 typed 消息解析后重序列化剥未知字段 → 双方互相收不到 caps → 双端同判 false，无脑裂 |
| 新 A + 旧 B（任意 S） | 裸 UDP | B 不上送位 → 公式 false |
| 旧 A + 新 B（任意 S） | 裸 UDP | 同上对称 |
| 旧 A + 旧 B | 裸 UDP（现状） | 零改动路径 |

客户端 kill-switch：设置项 `kcpBearer`（默认 true）——false 时本端不上送 CAP_KCP 位（强制裸 UDP，双角色同效）；测试缝 `ClientRuntimeOptions.DisableKcpBearer`（`DisableLatePunch` 同款模式）。服务端仅 MessagePack 字段 + SignalingCoordinator 两处透传（量级：实体字段×4 + 复制×2）。上线顺序：服务端先行（纯透传零风险）→ 客户端灰度（三端分发惯例 + bak 回滚件）。

## 9. 安全分析（07 修订点）

1. **KCP 层无认证**：注入面与现状裸 UDP **同暴露级**——on-path 注入垃圾 → 汇入帧流后 AEAD 校验失败 → 断链（SEC-12 既有口径）；off-path 需猜中四元组（现状同难）。KCP 头字段（conv/sn）可被构造，但无法产出可通过 AEAD 的帧；造成的最坏结果=断链 DoS，与现状等价，无回归。
2. **未授权 conv 防线**：无 lazy accept（§4.3）——conv 创建凭据=经 AEAD 的 OPEN；unknown-conv 丢弃+计数，内存有界（conv 端点数 ≤ 257）。
3. **ACK 反射放大有界**：伪造源地址诱使对端发 KCP ACK——需包形合法（魔数+conv+cmd+sn 窗口内）才触发 ACK，放大比 ~1:1（ACK ≈ 单包小帧），且 unknown-conv/畸形头先过滤；低于 STUN 侧既有反射面（TD-18 四道闸覆盖路径），可接受，注明不做专门限速。
4. **魔数指纹**：KCP 包首字节恒 0x4B=固定特征。02 §4.3 混淆扩展点预留位（`IObfuscationAdapter`）注明衔接：未来混淆层可将魔数并入包装层，本期接受指纹暴露（与现状裸 PTP ver 字节同级的可识别性）。
5. **防重放窗口扩至 4096**：同 counter 重放仍被 bitmap 拦截（与窗口大小无关），4096 只放宽"旧帧迟达"容忍度——SEC-12 语义不变，07 §9 无新增风险条目，仅注记。
6. 设备对唯一会话（02 §4.5）+ conv=channelId 唯一性 → 无跨会话串流面。

## 10. 与既有机制的交互清单

| 机制 | 交互 | 结论 |
|---|---|---|
| WINDOW 信用 | WINDOW 帧走 channel conv 可靠到达 | "信用永不回报→永久挂起"根除（M2-38 家族收口于直连路径） |
| KEEPALIVE | 走 conv 0；KCP 重传保证必达；对端死→无 ACK→无响应→3 miss 断链 | 语义不变；20s 心跳继续养 NAT 映射 |
| REKEY | 走 conv 0；排水窗 2s（NET-75）覆盖跨 conv 旧代尾帧 | 语义不变，乱序深度由扩窗吸收 |
| AEAD/防重放 | 帧流汇入点在 `_replay.Accept` 与解密**之前**之外（承载层只搬运整帧） | SEC-12 断链口径不变 |
| UDP_DGRAM/FRAG | 裸旁路，重组逻辑（in-flight 8/驻留 10s）不动 | 丢片超时丢弃=既有容忍语义，**刻意不修** |
| ViaRelay | KCP 仅绑 UDP 直连；RelayTransport 不动 | 判定正交 |
| 空闲回收 | conv 无待发时 KCP 无流量，KEEPALIVE 每 20s 必有帧出 | NAT 养洞不受影响 |
| 迟到 STUN 应答 | 裸路径白名单（§5.1） | 顺带修复整隧道误杀潜伏缺陷 |
| TCP 打洞 0x78 | 完全无涉 | 正交 |

## 11. 测试设计（09 修订点）

1. **受控介质单测**（核心新增）：内存 socket 对 + 可编程通道（丢包率/乱序深度/重复/延迟抖动/带宽无界）——`KcpTunnelTransport` 双端对拉，穷举帧型×conv 路由；unknown-conv 拒绝；conv 上限降级；Adopt 后 socket 所有权移交单次性。
2. **会话级**：KCP 承载上全帧型往返（OPEN/OPEN_OK/OPEN_FAIL/DATA/WINDOW/CLOSE/REKEY/REKEY_ACK/PING/KEEPALIVE/UDP_DGRAM/FRAG）；ReplayWindow 4096 回归（含跨 conv 乱序深度注入）。
3. **弱网红绿矩阵**（本设计的立项级证据）：丢包 0/1/5/20% × 乱序 × 100ms 抖动，8 并发映射持续传输——**KCP 组**断言零缺段/零 credit 挂起/会话存活；**裸 UDP 对照组**同条件须复现 M2-38 家族故障（红证）。红绿对照语义与 0x78 落地同风格。
4. **场景回归**：UdpNatSimulator 增丢包/乱序注入旋钮（`persistPerturbation` 同款路数）；S54/命中矩阵全量回归 kcp on/off 两态；Integration 113 基线不减。
5. **混布矩阵**：§8.3 四组组合（旧端行为用现网旧版构建物/协议帧探针模拟）；caps 判定两端一致性单测。
6. **弱网断链与时序**：黑洞（全丢）下 KCP 重传不伪造活性——3×20s miss 断链照常；KCP conv 半死（单向可达）同断链口径。

## 12. 基准与调参（09 §3 增补，L1 口径，TD-24 环境局限如实标注）

- 组别：裸 UDP（现状基线）/ KCP（nc=1 默认参数）/ KCP（nc=0 对照）/ TCP 中继参照。指标：吞吐、RTT 分布、建立耗时增量、CPU/内存采样（ResourceSampler 既有）。
- 调参矩阵：Nodelay×Resend×UpdateInterval（10/20/40ms）×nc——产出推荐参数回填 §5.4 表。
- **验收门禁**（决议点 ⑥，2026-11-14 评审修订版）：**性能闸**=0% loss 本机回环，KCP 吞吐 ≥ 裸 UDP 70% 且 PING RTT 增量 ≤ 5ms（语义=好网络下别劣化，与高丢包无关）；**正确性闸=弱网矩阵全格绿**——丢包 0/1/5/20% × 乱序 × 100ms 抖动 + **突发档**（200ms 窗连片丢，模拟 WiFi 漫游/NAT 重绑定形态——公网高丢包主流非均匀撒点，评审新增）每格零缺段/零 credit 挂起/会话存活，裸 UDP 对照组同条件红证；高丢包段**不设绝对吞吐门禁**（拍数字锁死自己），T7 产出丢包-吞吐曲线入基准报告、L2 生产 punch_stats 丢包统计验证包络；持续 >20%=网络不可用，职责归 KEEPALIVE 3×20s 断链（§11.6）；阈值最终值随 T7 基准确认，不达线不合入——先回调参矩阵，仍不达回**移植核对层**（自研定案后兜底路径，§2.3）。

## 13. 核心文档修订点清单（批复后随落地同步）

| 文档 | 修订 |
|---|---|
| 02 §4.2 | ReplayWindow 64→4096 注记；新增 §4.6「UDP 承载可靠化（KCP 多 conv）」：conv 路由表/魔数 demux/裸旁路白名单/承载切换时序 |
| 02 §4.3 | "UDP 承载帧 ≤1400B"补 KCP 承载措辞（KCP 自分段，发送侧 1368 分块不变） |
| 02 §2.4/§5.1 | 0x70/0x71/0x76/0x70 Ack 四行 bearerCaps 字段；UDP 打洞流程⑤后补承载协商一步 |
| 02 §6.2 | 非目标注记：中继 UDP 可靠化二期 |
| 05 §2.3/§2.4 | KcpTunnelTransport 模块设计（§5 全文）；TunnelSession 三缝实现注记 |
| 05 §3/§4 | Puncher 双角色 Adopt 分支；承载协商判定 |
| 07 §9 | §9 全表 6 条安全注记 |
| 09 §2/§3 | 受控介质/弱网矩阵/场景两态/基准组别与门禁 |
| 10 追溯矩阵 | NET-77 行（PRD 回流后） |
| README | TD-29 行；文档索引 11 行；修订记录条目 |
| PRD | 新增 NET-77（UDP 承载可靠化，决议点 ⑦）+ README 版本头 |

## 14. 实施切分（已批复：新立 M6 · UDP 承载可靠化，T1-T8=M6-01~08——决议点 ⑦，2026-11-14）

| # | 任务 | 内容 | 依赖 |
|---|---|---|---|
| T1（M6-01） | KCP 协议核自研移植 | §2.3 定案执行：协议数学对照 skywind3000/kcp 1:1+四参考壳层借鉴+流模式裁剪（frg/WASK-WINS）+dead_link 不实现；纯算法属性单测为强制门禁（丢包/乱序/重复注入下可靠有序性质） | — |
| T2（M6-02） | KcpTunnelTransport | 收包泵/更新泵/conv 表/裸旁路/Adopt/参数组；受控介质单测 | T1 |
| T3（M6-03） | 会话层接缝 | IKcpConvControl 三缝+ReplayWindow 扩窗+全帧型路由单测 | T2 |
| T4（M6-04） | caps 协商 | 四消息字段+服务端透传+两端判定+kill-switch+混布矩阵测试 | T3 |
| T5（M6-05） | Puncher 接线 | 双角色 Adopt/降级分支；UdpNatSimulator 两态全矩阵回归 | T4 |
| T6（M6-06） | 弱网场景组 | 注入旋钮+弱网红绿矩阵（含裸 UDP 对照组红证；修订版=矩阵全格门禁+突发档） | T5 |
| T7（M6-07） | 基准与调参 | 四组对照+调参矩阵→门禁线确认→参数回填+丢包-吞吐曲线（修订版） | T6 |
| T8（M6-08） | 部署灰度 | 三端分发（服务端先行）/bak 回滚件/生产观察（punch_stats 扩丢包统计+新增诊断计数）/文档落地同步（§13 全表） | T7 |

工作量粗估（④ 自研定案后修正）：T1 算法核 ~600-900 行 + Core 协议字段 ~50 行 + Client（KcpTunnelTransport ~500 行、TunnelSession 三缝 ~30 行、Puncher 分支 ~60 行、Server 透传 ~40 行）合计 ~1400-1600 行；大头前移 T1（协议核与属性单测），T6/T7（受控介质与弱网矩阵）次之。

## 15. 风险清单

| 风险 | 缓解 |
|---|---|
| 移植失真（④ 自研定案后的首险） | 协议数学 1:1 对照参考实现不重发明（§2.3）+T1 属性单测强制门禁（丢包/乱序/重复注入）+T6 弱网矩阵红绿对拍；不达线回移植核对层（§12） |
| 调参不当→轻载吞吐劣化 | T7 门禁线（70%/5ms）硬门槛；nc=1/0 对照；不达线不合入 |
| conv 泄漏（RemoveChannel 路径漏网） | T3 单测穷举关闭路径（OnClose/OpenResult !Ok/本地 close/会话销毁）+上限守卫兜底 |
| 混布判定不一致脑裂 | 判定公式两端同式纯本地输入（§8.2）+四组合一致性单测 |
| 扩窗后安全质疑 | 同 counter 重放拦截与窗口无关（§9.5）；07 注记 |
| 跨 conv 乱序超窗丢帧 | 深度上界 48×channel 数（§4.4）；4096 窗按 ConvCap=256 也能容 8.5 倍满载；超限 channel 降级 conv 0 同时**缩小**乱序源 |
| KCP 泵 CPU 空转 | 257 conv × 10ms 遍历可忽略；空闲降频列优化项 |
| 生产不可观测 | 诊断计数（kcp_unknown_conv_dropped/裸路径丢弃/conv 存量）接入本地诊断页（M3-14 隧道快照同区，可选轻量） |

## 16. 修订记录

| 日期 | 版本 | 说明 |
|---|---|---|
| 2026-10-25 | v0.1 | 首版草案：KCP 多 conv 最终形态全套设计（选型 TD-29 拟/conv 路由/承载切换/caps 协商与混布/安全分析/测试基准/实施切分），待评审 7 决议点 |
| 2026-11-14 | v0.2 | **评审批复 7/7 决议点**（M6 立项）：①②③⑤⑦ 按建议；④ 修订=定案自研移植（§2.3 重写：三候选降参考实现+流模式裁剪+dead_link 不实现）；⑥ 修订=正确性闸弱网矩阵全格+新增突发档+丢包-吞吐曲线（§12 重写）；落地=M6 清单 v1.0/NET-77（PRD v0.16）/TD-29 定案（README v1.84） |

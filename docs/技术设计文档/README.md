# P2P 内网穿透产品 · 技术设计文档

> 版本：v1.31　创建日期：2026-08-19　状态：待评审
> 依据：[产品需求文档 PRD v0.6](../prd/README.md)（决策 D1~D21，D3 经 v0.4 修订；开放问题 OQ-1~OQ-19 已全部决议）
> 定位：开发阶段的**施工图纸**——所有实现以本文档为准；与 PRD 冲突时以 PRD 为准并提出修订。

## 文档索引

| 编号 | 文档 | 内容 |
|---|---|---|
| 01 | [总体架构设计](01-总体架构设计.md) | 系统架构、解决方案结构、进程模型、启动与生命周期 |
| 02 | [通信协议设计](02-通信协议设计.md) | 控制协议、STUN 子集、隧道协议（加密/复用/轮换）、打洞与中继报文交互 |
| 03 | [数据库设计](03-数据库设计.md) | ER 图、SQLite 表结构、索引、迁移、客户端本地文件 |
| 04 | [API 接口设计](04-API接口设计.md) | 客户端本地 Web API、服务端 Web API、WebSocket 推送、错误码 |
| 05 | [关键模块设计](05-关键模块设计.md) | 虚拟网卡、映射引擎、打洞器、中继、哑节点、服务端各服务实现 |
| 06 | [前端设计](06-前端设计.md) | 前端工程结构、页面路由、状态与实时通道、构建与嵌入 |
| 07 | [安全设计](07-安全设计.md) | 密钥体系、控制通道防重放、凭据存储、授权执行点 |
| 08 | [构建与部署设计](08-构建与部署设计.md) | CI/CD、单文件发布、安装包、deb/rpm、Docker、配置文件 |
| 09 | [测试与基准设计](09-测试与基准设计.md) | 单元/集成测试、NAT 模拟、基准测试方案（NFR-11） |
| 10 | [追溯矩阵](10-追溯矩阵.md) | PRD ⇄ 设计全量编号对照台账（D/OQ/FR/NET/SEC/NFR/A/F） |

> 编码阶段约束见 [docs/开发规范/](../开发规范/README.md)（AI 开发基础规则 / 模块职责边界 / 目录结构规范）。

## 技术栈总览

| 层 | 选型 | 说明 |
|---|---|---|
| 运行时 | **.NET 10（LTS）** | 目标框架 `net10.0`；.NET 8/9 于 2026-11-10 停止支持，禁用 |
| 语言 | C#（客户端/服务端/共享库） | |
| Web/API | ASP.NET Core Minimal API + Kestrel | 服务端 Web 仅绑本机；客户端本地 Web 仅绑 127.0.0.1 |
| 前端 | **Vue 3 + TypeScript + Element Plus + Vite** | 双端 Web 共用组件库与工程链，产物嵌入二进制 |
| 数据库 | **SQLite + EF Core**（Code First 迁移） | 服务端业务数据；单文件零运维 |
| 消息编码 | **MessagePack**（MessagePack-CSharp） | 控制通道与隧道内部消息，schema 定义于共享库 |
| 加密 | .NET 内置：**ECDH(P-256) + HKDF-SHA256 + AES-GCM + HMAC-SHA256** | 零第三方加密依赖（已核实 .NET 10 未内置 X25519，故不选） |
| 虚拟网卡 | Windows：**Wintun**（P/Invoke）；Linux：**TUN**（ioctl/netlink） | 仅作本地监听地址，不做三层转发 |
| 网络框架 | System.Net.Sockets + **System.IO.Pipelines** | 自定义协议分帧、高性能 splice |
| 日志 | Serilog（文件轮转）+ Microsoft.Extensions.Logging | |
| 测试 | xUnit + TestHost | |

## 技术决策记录（TD）

| # | 决策 | 依据（PRD） |
|---|---|---|
| TD-01 | 运行时 .NET 10，单文件自包含发布 | D13、NFR-31 |
| TD-02 | 前端 Vue 3 + TS + Element Plus；客户端/服务端两个 SPA 共享组件库 | 本轮选型问答 |
| TD-03 | 服务端存储 SQLite + EF Core 迁移 | D4、NFR-36 |
| TD-04 | 隧道加密用 .NET 内置 ECDH(P-256)+HKDF+AES-GCM；控制通道 HMAC-SHA256 防重放（不依赖 TLS） | D5、D21、SEC-11~14、SEC-22 |
| TD-05 | 控制协议与隧道内部消息统一 MessagePack 编码 | 本轮选型问答 |
| TD-06 | 虚拟网卡仅提供本地监听地址：Wintun（保持 session 开启维持 up）/ TUN + IP Helper/netlink 配址 | D2、PRD 06 §1 注 |
| TD-07 | 端口规划：控制 7000/TCP、STUN 3478/UDP+TCP、中继 7010/UDP+TCP 起、客户端 Web 7100、服务端 Web 7500，全部可配 | PRD 02 §4 |
| TD-08 | 服务端**无公网 HTTP**：对外仅私有 TCP 控制协议 + STUN + 中继；Web 后台仅绑本机（F7 经映射接入） | D11、FR-S-810~825 |
| TD-09 | 数据面是**流/数据报复用隧道**（channel 模型），不做 IP 包转发——与端口映射语义一致 | D2、PRD 06 §1 |
| TD-10 | TCP 打洞：双方在自己 STUN 探测的本地端口上 listen+connect（SO_REUSEADDR），并发 N 条连向 STUN 公网端口 +(N−1) | D10、OQ-1 |
| TD-11 | 中继只读外层 8B 会话头转发密文帧，不可解密；UDP/TCP 双承载 | D3、FR-S-701/704 |
| TD-12 | 客户端本地 API 无鉴权（仅回环监听，风险 T7 已知情接受）；服务端 Web 用 Cookie 会话认证 | D11、07 §4 |
| TD-13 | TCP 打洞并发路数 N 取**发起方配置**，经 PunchInvite 统一下发，双方该次打洞使用同一 N（端口预测对称性要求 N_A=N_B，双方 N 不一致必然 miss） | D10、OQ-1 |
| TD-14 | 并发模型总纲：四类 Actor（状态机/隧道/控制会话/打洞调度）+ 纯 I/O 循环；共享可变状态仅三处白名单；禁新增 lock；事件有界背压；优雅停机定序 | 第三轮审查 P2 补全；05 §0 |
| TD-15 | UDP channel = 本地应用端点：OPEN 后乐观直发不等 OPEN_OK，双向空闲 60s 回收，每 UDP 映射并发上限 256（源端口粘滞型协议的副作用已知悉） | FR-C-303；05 §2.4 |
| TD-16 | 前端实时原则"REST 是真相、WS 只是提示"：状态类事件仅触发 refetch，重连/页面唤醒全量 resync；否决 seq+增量补发 | FR-C-306/1001；06 §4 |
| TD-17 | NatSimulator TCP 模拟 = 代理 STUN 改写映射地址 + Bind 导演源端口 + 应用层终结桥接；不可验证真机 SYN 时差覆盖（保留 A-5 手工样本） | NFR-11、A-5；09 §2.2 |
| TD-18 | STUN 风暴防护分层四道闸（HMAC 前）：单 IP 令牌桶 50pps → 查表先于验签 → 每设备 10QPS → 全局 2000pps 熔断；stun_dropped_total 计数 | FR-S-603；05 §7.1、03 §2.8 |
| TD-19 | 打洞端点互换**两段式**：发起方端点随 0x70 上送 → 0x71 邀请被访问方（携发起方端点）→ 被访问方即时探测、经新增 0x76 PunchEndpoint 回传、随即直接开始发包 → 服务端以 0x70 Ack 向发起方送达对端端点；目标离线 → 4005；STUN 仍仅打洞期内瞬时使用（不违反 02 §5.3 不长持） | FR-S-502、OQ-18；02 0x70/0x71/0x76 + §5.1/§5.2、05 §3/§3.1/§5、04 §5 |
| TD-20 | 打洞并发路数 N 上送载体：0x70 PunchRequest 增可选字段 punchConcurrency（int 1~5，发起方本地配置，缺省 3），服务端校验范围后经 0x71 PunchInvite 与 0x70 Ack 统一回填下发——补全 TD-13"统一下发"的 N 通路（M1 实现中服务端硬编码 3，客户端配置无通路到达服务端） | FR-C-402/808、OQ-19；02 §2.4 0x70/0x71、05 §3 |
| TD-21 | TcpNatSimulator 入站交付语义（M2-30 随实现定案）：**探测映射**=任意注册来源可入、多次可入、交付探测时的内部端点 listen（N=1 直连 STUN 端口前提，02 §5.2）；**出站映射**=APDF 精确身份过滤——入站连接在源侧新分配的外部端口须等于映射目的地端口（对称 rendezvous c_{N−2} 四元组交叉，一次桥接幂等去重）；miss（无映射/listen 未起）前 600ms 有界重试=模拟 SYN 重传；每客户端独占端口子段（打洞窗口互不重叠）。**命中代数**：命中 ⟺ \|ΔN\|≤1 或 min(N_A,N_B)=1；必 miss ⟺ \|ΔN\|≥2 且 min≥2。**miss 断言口径（M2-31 修正）**：协议统一双方 N（TD-13/20 服务端回填）→ N_A≠N_B 在真实路径不存在，不对称 miss 以**单测级注入**断言（M2-16）；场景级失配以 **NAT 时序扰动**等效——对端首次探测后、Fleet 连接前被 s 条外来流占用端口序列（模拟器 punchPerturbation 注入，探测型映射指向无人监听端口），s≥N−1 ⟺ 不对称必 miss 区 | TD-17、A-5；09 §2.2 |

## 与 PRD 的追溯约定

- 每个设计章节标注对应 PRD 需求编号（FR-S-xxx / FR-C-xxx / NET-xx / SEC-xx / NFR-xx）；
- 本文档不改变需求范围；若实现中发现 PRD 缺口，回到 PRD 补充决策（新 OQ 自 OQ-18 续编）后再修订本文档。

## 修订记录

| 日期 | 版本 | 说明 |
|---|---|---|
| 2026-08-19 | v1.0 | 依据 PRD v0.2 与技术选型问答（前端/数据库/加密/编码共 4 项）首次创建 |
| 2026-08-19 | v1.1 | 澄清 TCP 打洞为双方对称并发（simultaneous open）；修正 N 条连接的本地端口分配——第 1 条沿用 STUN 探测端口、其余用不同本地端口（避免重复四元组）；目标端口 +(N−1) 规则不变 |
| 2026-08-19 | v1.2 | 新增 02 §4.5 隧道生命周期：明确隧道为设备对粒度、多连接/多映射复用同一条隧道（channel 复用）、仅隧道建立/死亡/空闲回收时打洞；PunchRequest 语义改为"建立隧道"，TunnelHost 键改为 peerDeviceId |
| 2026-08-19 | v1.3 | 依 PRD 全面审查修订。**错误更正**：devices 表补 os/client_version 列、Register 补 clientVersion 字段（FR-S-101）；打洞 N 归属明确为 PunchInvite 统一下发且双方同一 N（TD-13）；Docker 容器内 webBind=0.0.0.0 + 宿主回环映射（否则 F7 断链）。**模糊修订**：新增 0x75 Invalidation 消息承载 PRD 05 §4 全部失效推送场景（登出/禁用/码重置/白名单移除/退组/解散）；默认分组 owner=内置 admin 及初始化顺序；混淆扩展点落位 02 §4.3。**遗漏补充**：0x23 用户改密、0x55~0x57 分组编辑/解散/移出成员、0x13 设备改名；03 §2.9 punch_stats 表（打洞成功率数据源）；05 §10 设备识别码生成（多网卡选取+机器指纹回退）；serverAddrs 主备多候选（FR-C-604）；汇总/导出统计 API（FR-C-1002）；配置启动校验（NFR-35）；测试补 A-2/A-11/A-12；08 §8 随版交付文档清单（NFR-61/62） |
| 2026-08-19 | v1.4 | 第二轮审查（对照追溯矩阵）。新增 **10-追溯矩阵.md**（全量编号台账）。**修复**：0x14 RemoteCodeReset（重置码此前无协议载体，FR-S-903/SEC-25）与 0x64 StatsReport（流量统计上报此前误挂 0x62，FR-C-1002）；02 §2.5 哑节点消息分类清单同步 v1.3 新增消息（0x23/0x55~0x57 归主动类，0x13/0x14/0x64 归本机管理/被动类）；04 §2.4 错误码 4031→2002（错误码表无 4031）；服务端 Web 补审批端点（FR-S-305 明确双端审批）；FR-S-825 端口/日志级别口径统一为 server_config 覆盖键（重启生效）；06 前端补所有者操作/改密/汇总导出/多候选地址 UI。**PRD 回流修订 v0.3**：FR-C-805 补所有者操作、01 §6.2 审计口径澄清、09 §4 检查表 D1~D21、README 版本头笔误 |
| 2026-08-19 | v1.5 | 第三轮审查（内部一致性/边界条件）13 项发现中的 8 项 P0/P1 经用户决议落地（OQ-10~17）：**①中继回退改目标设备层级**（D3 修订：mappings 表去 relay_fallback 列，新增客户端本地 peers.json + /api/peers，UI 移 /devices 设备卡片，消除与设备对隧道粒度的冲突）；**②时钟对齐**（Hello 不校 ts，HelloAck 带 serverTs 按 RTT/2 补偿算 offset，新错误码 5005 TIME_SKEW 重校准，0x30 持续校准，消除时钟死锁）；**③RegisterAck deviceSecret ECIES 加密下发**（客户端 staticPubKey，新残余风险 R7）；**④凭据覆盖式恢复**（同 mac_code 离线重注册覆盖绑定、在线 4004 拒绝；虚拟 IP 取消唯一改统一下发固定 .2，规模上限改 max_devices=500）；**⑤串行打洞队列**（客户端 PunchScheduler FIFO + 服务端 per-device 会话串行）；**⑥0x40 应用层分页**（offset/limit→total/items/hasMore）；**⑦邀请码 6 位去混淆字符集全局唯一**；**⑧punch_stats 90 天保留**（punch_retention_days）。**PRD 回流 v0.4**。同步：10-追溯矩阵 OQ-10~17 与 D3/FR-S-103/702、FR-C-304/803 行；07 §5 注册滥用防护第 ③⑥⑤ 条、§9 R7；09 测试补时钟/ECIES/恢复/排队/分页场景 |
| 2026-08-20 | v1.6 | 第三轮审查剩余 **5 项 P2 全部落地**（新增 TD-14~TD-18，不改需求范围）：**① 05 §0 并发模型总纲**（四类 Actor 拓扑 + 共享可变状态三处白名单 + 六条纪律 + 优雅停机定序）；**② 05 §2.4 UDP channel 生命周期**（channel=本地应用端点，OPEN 后乐观直发，双向空闲 60s 回收，每映射 256 上限；02 0x03/0x08 三视图对齐——UDP_DGRAM 不再携带对端地址）；**③ 06 §4 WS resync**（REST 真相/WS 提示二分，状态类事件只触发 refetch，重连与页面唤醒全量 resync）；**④ 09 §2.2 TCP NAT 模拟机理与边界**（STUN 改写+Bind 导演+应用层桥接；真机 SYN 时序保留 A-5 手工样本；端口段 20000-21000 系统排除）；**⑤ 05 §7.1 STUN 风暴防护**（分层四道闸 + 03 §2.8 配置键 stun_rate_per_ip/per_device/circuit_pps + 04 仪表盘 stun_dropped_total）。追溯矩阵同步（FR-C-303/306、FR-S-603、§10 挂账关闭） |
| 2026-08-20 | v1.7 | M1 任务清单完整性检查发现**设计缺口 OQ-18：打洞端点互换时序无协议载体**（02 §5.1①"双方各自探测"与 05 §5.3"STUN 不长持"矛盾；被动方在邀请前无探测动机；0x70/0x71 均未定义端点生产者），经用户决议落地为 **TD-19 两段式**：0x70 增可选字段 requesterEndpoints、新增 **0x76 PunchEndpoint**（被邀请方端点回传）、0x70 Ack 延后至对端端点就绪（与 OQ-11 排队语义一致）、新增错误码 **4005 TARGET_OFFLINE**；02 §5.1/§5.2 时序重写、05 §3/§3.1/§5 SignalingCoordinator 两段式协调、04 §5 同步；追溯矩阵 OQ-18/FR-S-502/FR-C-401 行更新 |
| 2026-09-12 | v1.8 | M2 任务清单编制回流两项：**① OQ-19 决议（TD-20）**——打洞并发路数 N 无上送载体（TD-13 要求 N 取发起方配置并经 PunchInvite 统一下发，但 0x70 载荷未定义 N 字段、服务端实现硬编码 3）：0x70 增可选字段 punchConcurrency（1~5，缺省 3），服务端校验后经 0x71/0x70 Ack 统一回填；02 §2.4 0x70/0x71 行、05 §3 N 来源句同步。**② 帧值表勘误（对齐 M1 实现）**——02 §4.2 原表 0x06 PING/PONG 合一、0x07 REKEY、0x08 UDP_DGRAM、0x09 WINDOW 与代码实际帧值不符（M1 实现将 PING/PONG 拆为独立帧 0x06/0x07，后续顺移：0x08 REKEY、0x09 UDP_DGRAM、0x0A WINDOW；协议未对外发布无兼容负担，文档对齐实现）；FRAG 帧值随 M2 UDP 映射实现定案后回填 |
| 2026-09-12 | v1.9 | 技术问答澄清落档（用户指示）：STUN 版本与打洞成功率无因果关系（本项目 M1 起即 RFC5389 子集，非 3489；Binding 所得映射端点各版本等价，成败由 NAT mapping × filtering 组合决定）；**M3 诊断扩展预留 05 §7.2 NAT 行为发现（RFC5780 子集）**——双地址监听 + OTHER-ADDRESS/RESPONSE-ORIGIN 属性、mapping（EIM/ADM/APDM）与 filtering（EIF/ADF/APDF）分级判型、TCP 变体端口分配规律（SymmetricSequential 判定），供 FR-C-808 stun-test"NAT 行为初判"与打洞前不可穿透组合预判；04 §2.6 stun-test 行同步；M2 范围不动 |
| 2026-09-12 | v1.10 | M2-03 消息表字段级细化（随编码落地，02 §2.4 八行）：0x03 响应字段形状 {latestVersion, minProtocol~maxProtocol, upgradeUrl, notes}；0x41 无附加载荷提示帧；0x53 三操作共用 msgType 的判别式 {action: list/approve/reject, groupId?/requestId?} 与队列项字段；0x54 {groupId, revoke}→Ack{ok, inviteCode?}；0x63 {segmentId?, cidr, enabled}（false=移除语义）；0x64 批量化 entries[]；0x73 方向列澄清 C↔S 双向（访问方 60s 请求协调 / 服务端通知双端重打）；0x74 relaySessionId 标注 8B 整数（RLP 外层会话头标识，非 Guid）。另：v1.9 时版本头笔误仍标 v1.8，本次一并修正 |
| 2026-09-12 | v1.11 | M2-05 落地同步：05 §2.3 WINDOW 帧值勘误补遗——v1.8 帧值勘误仅改 02 §4.2，05 §2.3 本行残留勘误前旧值 0x09，随 M2-05 交付（WINDOW=0x0A、CreditWindow 账本默认 64KiB、载荷=已消费字节数而 channelId 在帧头）一并修正；REKEY(0x08)/REKEY_ACK 载荷定长 81B（新 eph 公钥 65+新 nonce 16，承载于现会话密钥 AEAD 内无需另加握手 mac）随编码定案记入 02 §4.4 补注（不改变决议范围） |
| 2026-09-12 | v1.12 | M2-16 落地同步：02 §5.2 实现备注补承载分帧条目——直连 TCP 打洞胜出连接的 PTP 会话承载与 §6.2 中继 TCP 承载复用同一定界（`[u16 帧长（小端）][PTP 帧]`，§6.2 原文为中继场景所写），帧上限 16416B（16B 帧头 + 16KiB DATA 载荷上限 + 16B AEAD tag）随编码定案：发送侧越界拒绝、接收侧前缀越界/中途截断按承载关闭契约处理（不改变决议范围） |
| 2026-09-13 | v1.13 | M2-30 落地同步（**新增 TD-21**）：09 §2.2 TCP 模拟交付语义随实现定案——探测映射 listen 交付（N=1 前提）/出站映射 APDF 精确身份过滤/miss 有界重试（SYN 重传语义）/每客户端端口子段；**勘误** 09 §2.2 原"N_A=N_B 违反时必 miss"表述——命中代数实为：命中 ⟺ \|ΔN\|≤1 或 min(N)=1，必 miss ⟺ \|ΔN\|≥2 且 min≥2（N=1 参与的不对称对经探测 listen 路径命中），A-5 miss 断言取 (2,4)/(2,5) 类对；模式表行名对齐实现（SymmetricRandom 并入 UDP 行、UdpBlocked 单列、UDP 变体 SymmetricSequential 标注） |
| 2026-09-13 | v1.14 | M2-31 落地同步：**TD-21 miss 断言口径修正**——v1.13 所记"A-5 miss 断言取 (2,4)/(2,5) 类对"基于 N_A≠N_B 可经客户端设置构造的前提，实现发现协议统一双方 N（TD-13/20：0x70 上送→服务端回填→双方同 N）→ N_A≠N_B 在真实路径不存在。修正为：不对称 miss 以单测级注入断言（M2-16 已覆盖）；A-5 场景级失配以 NAT 时序扰动等效（模拟器 `punchPerturbation`：首次探测后、Fleet 连接前注入 s 条探测型外来流，s≥N−1 ⟺ 必 miss 区，09 §2.2/§2.3 同步）；09 §2.3 A-5 行标注已自动化（M2-31，含扰动失配 miss） |
| 2026-09-22 | v1.15 | M2-06 落地同步：05 §7.1 表 ④ 行改**单级熔断执行口径**（原"仅服务在线设备"中间级未实现——闸② 认证后伪造源已被排除、预认证洪泛由 ①④ 覆盖，中间级须查控制面在线态成本高于收益）并补实现注记（闸序 ④→①→②→③；TCP 侧并发连接≤4 不另设令牌桶；③ 桶容量=QPS；nonce 缓存 UDP/TCP 共用；TCP 短事务读超时 3s；TimeProvider 计时；计数进程内承载、仪表盘 M3）——TD-18 决议语义不变 |
| 2026-09-22 | v1.16 | M2-07 落地同步：02 §6.2 补实现注记——§6.1④ "源地址更新"**收窄为 RELAY_JOIN 认领时刷新**（数据包未知源保守丢弃防劫持，地址变更经重新 JOIN/空闲回收兜底）、0x74 错误路径定案（5002 / 1001 session_unknown[台账 120s] / 4005）、Grant 端点 host 取控制连接本地侧地址、TCP 端首帧 10s 与断连即收会话；05 新增 §6.1 实现注记（端槽认领算法、转发承载 TCP 优先无缓冲丢弃、per-end 写串行化、回收细节）——TD-11/90s/限速 M3 决议语义不变 |
| 2026-09-23 | v1.17 | M2-18 落地同步：05 §4 补实现注记——回退链内联 Puncher（0x74→JOIN 先 UDP 后 TCP 承载兜底[两端承载可异构]→PTP 握手即经中继，THello1 200ms 周期重发覆盖对端 JOIN 窗、回退预算独立计量、失败并入映射 failed）；被邀请侧关联机制（RelayGrant 不含对端信息——以 THello1 载荷打洞 sessionId 关联预记邀请上下文[对端 deviceId+静态公钥，服务端 relayAllowed 真才预记/应答成功即撤销/TTL 120s 对齐服务端台账/先记后应答防并发竞态]）；TunnelSession.ViaRelay 承载标记驱动映射态分派/隧道复用/断链回 punching。Primary+Fallback 双 socket 并存切换（NET-75 排水）属 M2-19 回切，本版未含 |
| 2026-09-23 | v1.18 | M2-19 落地同步：05 §4 补实现注记（回切直连 OQ-7/NET-75）——0x73 只做协调触发（访问方 IsInitiator 60s 周期[测试缝 RelayRetryIntervalOverride]→服务端台账解析[120s 过期以 RelayService.ResolveActiveRelay 按 PunchSessionId 反查活中继兜底；非发起方 1001 防双端同打]→双端 0x73{原 sessionId}，实际端点交换由 A 随后全新 0x70 驱动；客户端被 1001 拒退化全新 0x70）；NET-75 排水切换落 TunnelHost.Attach（存活旧会话 2s 排水窗内接收照常后 Close("replaced_drained")、已亡立即 Close、会话表即刻指新）；排水期新旧并存一致性（SessionDisconnected 携带会话对象[引擎只清旧会话 channel]、新增 SessionAttached 驱动 B 侧/兜底 relay→direct[明细 relay_to_direct]）；重打失败映射保持 relay 态服务连续、失败+回退资格真走新中继会话双端整体替换（防单端保留旧会话脑裂）；每连接帧连续即满足 NET-75 顺序性（断连后尾部写入不在语义内） |
| 2026-09-24 | v1.19 | M2-08 落地同步：05 §5 补实现注记（上报族 0x72/0x62/0x64 与保留清理）——0x72 结果映射（Ok+端点=direct / Ok 无端点=relay[FailReason 携带打洞失败原因] / !Ok=failed，relay 语义靠端点缺席表达不改 schema）、proto/N/时长取自会话台账（M2-07 台账扩为 RelayLedgerEntry 含 proto/PunchCount/CreatedAt；120s 窗覆盖全部上报时点，窗外 drop+审计；仅发起方可报、sessionId 去重首份即定论）；0x62 → mapping_status 审计流水（映射运行态不落表，客户端状态机是真相源；仅受理本人映射）；0x64 → mapping_stats 覆盖式 upsert（载荷=客户端本地累计绝对值→重发/乱序不叠加，最新到达=客户端当前真相；逐项归属校验、同报告重复项去重）；RetentionCleaner 启动+每日（audit_logs 与 punch_stats 一并，OQ-17；键每轮现读、非法回退 90）；03 §2.7/§2.9 保留口径与 concurrency 来源勘误同步 |
| 2026-09-24 | v1.20 | M2-22 落地同步：05 §5 补客户端上报实现注记（ClientReporter 三消息）——0x72 上报点=PunchScheduler.PunchCompleted（仅访问方；端点映射与服务端三态判据互证，Ack 前失败[SessionId 空]不上报——Puncher Ack 后失败改携 ack.SessionId 补齐 failed 行归属前提）；0x62 状态机迁移三态（direct/relay/failed）入流水、invalid 预留 0x75 联动；0x64 TrafficSnapshots 30s 周期+优雅停机终刷（停机序先于引擎/控制通道）、零值条目也上报（重启清零如实覆盖旧行=覆盖式 upsert 的客户端配合面）；relay_bytes 口径明确=双向经中继合计、含于 up+down 总量（channel 会话 ViaRelay 归属，03 §2.6） |
| 2026-09-27 | v1.21 | M2-09 落地同步：05 §5 补分组服务全量实现注记（0x51/0x52/0x53/0x54/0x57，FR-S-303~307）——**创建设备即首成员**（语义定案：0x50 建组同时写 group_members，否则共同分组可见性口径下创建者与跨账号入组设备互不可见，FR-S-306 无法闭环；M1 直写库构造掩盖了缺口）；0x51 登录前置、码无效/撤销一律 3001、已是成员幂等 Ack、free 即入+清残留 pending、approval 建单去重回 3002；0x53 List/Approve/Reject 仅所有者（已处理申请 Ok=false 诚实应答）；0x54 六位去混淆字符集（31 字符，03 §3）加密随机+UNIQUE 撞码重试、每分组至多一码=覆盖式、审计不含码值（AI-17）；0x52/0x57 删行+清 pending（非成员 Ok=false 幂等）；默认分组准入策略 server_config 键 default_join_policy（Seed 建组时读取、非法回退 free，FR-S-304）；0x75 联动触发点归 M2-12 |
| 2026-09-28 | v1.22 | M2-10 落地同步：05 §5 补 0x41 推送实现注记（FR-S-403/TD-16）——独立 DeviceListPusher 单例（构造订阅 DeviceRegistry 上线/离线事件[换线旧会话按 (deviceId,session) 配对移除 no-op 不误触]、GroupService 成员变更 Ack+审计后显式调用、远程码重置触发归 M2-12）；收件人=与 0x40 可见性同口径的逆向查询（同账号∪共同分组≠自身、并集去重）；解散先捕获成员清单再删行；哑节点 Passive 不下发、离线跳过、单收件人失败静默（提示帧语义，轮询 0x40 兜底）；推送与 Ack 共用会话 seq 严格递增；presence 推送 fire-and-forget 不阻塞读循环、分组推送 await 且 Ack 先行；GroupService 可选参数注入 pusher（DI 默认值，既有夹具零改动）；客户端 0x41 接线归 M2-15 |
| 2026-09-28 | v1.23 | M2-11 落地同步：05 §2.5 附实现注记 + 05 §5 补白名单实现注记（FR-C-701/702、SEC-52/53）——0x63 三分支语义（新建须 Enabled=true、更新 Cidr、移除=Cidr 不消费的协议容错；归属校验限定本人防跨设备探测）；CIDR 规范化取 .NET IPNetwork 语义（裸 IP=主机地址补满前缀，全开放须显式 0.0.0.0/0 或 ::/0；落库统一规范形态）；L3 双路径=0x60 L2 后校验（create/edit 共用）+ 0x70 TriggerMappingId 查映射**现值**再校验（伪造 id → 1002、段收窄后旧映射 → 4002、SQLite 无 CIDR 数学 → 段集内存过滤、self 恒放行、非法地址 fail closed）；移除联动 0x75 仅携受影响映射（disabled 映射不入集——重启用 0x60 再过 L3；更新不触发 0x75——由现值再校验兜底）；客户端执行点=lan-segments.json 本地镜像（03 §5 补录）+ MappingEngine 注入 enabledCidrsProvider（null=空集 fail closed 保持 M1 语义）；UDP 侧同口径归 M2-20、本地 API/0x63 上报接线归 M2-27、0x75 置 invalid 归 M2-15 |
| 2026-09-29 | v1.24 | M2-12 落地同步：05 §5 补失效推送实现注记（FR-S-903/FR-C-702、02 §2.4）——InvalidationPusher 独立服务（授权链反查逐 owner 推送；离线跳过/单收件人静默/disabled 映射不入集）；五种触发点反查口径（logged_out=本人 enabled 全量；remote_code_reset 按 TargetDeviceId 反查保守全量失效——D7 码是定位别名；group_left/移出=切断边任一端为离开者；group_dissolved=两端均原成员）；残余可见性复核 IsStillVisibleAsync（同账号 ∪ 共同分组与 VisibleDevices 同口径——另一共同组/同账号对保留防过度失效）；0x14 处理凭连接级设备身份无须登录（passive 允许）、RemoteCodeGenerator 换值旧码立即 4003、审计不含码值（AI-17）、Ack→0x75→0x41 帧序；GroupDissolved=6 兼 0x56/0x57、GroupLeft=5 仅 0x52；user_disabled/device_disabled 触发点归 M2-13 |
| 2026-09-29 | v1.25 | M2-13 落地同步：05 §5 补禁用与解绑实现注记（FR-S-105/204/103）——AdminService 统一执行点（CLI 五开关壳[M1-19 模式]/测试直调/M3 Web 共用）；设备禁用先推 0x75(device_disabled) 后踢线，读侧拒绝补齐 Hello[禁用直接断连不落 NeedRegister——堵借覆盖式恢复重签凭据绕回]与注册恢复[2003]两处；用户禁用名下在线设备降级 passive+0x75 携 newCapability（PushOwnedAsync 扩参、空映射集也推）不踢线，恢复路径=重连+登录（0x21 属主动类清单）；解绑清理集合对齐 0x12+同 MAC 重注册全新身份；跨进程 CLI 窗口由 PresenceMonitor 心跳兜底（admin_check_interval 节流读库复核+幂等闸） |
| 2026-09-29 | v1.27 | M2-15 落地同步：05 §5 补 ControlClient 下行扩展实现注记（02 §2.4、TD-16）——DecodeKnown 扩容四条（0x41/0x75/0x14Ack/0x63Ack）；0x75 消费分工（newCapability 降级 ControlClient 内联[连接级]/映射失效 ClientRuntime MarkInvalid[预留 hook 启用]）；WsEventNames 补 device_list；0x14 新端点 POST /api/device/reset-remote-code 新码持久化+WS 提示 |
| 2026-09-29 | v1.26 | M2-14 落地同步：05 §5 补 UpdateInfo 实现注记（FR-S-804/OQ-5、02 §7）——0x03 统一应答（update_* 四键 + maxProtocol 代码注入 + minProtocol clamp）；版本不符受理窗（5004 后 0x03 唯一受理应答后断/非 0x03 即断/75s 空闲兜底）；已建立会话 signed 受理连接保持 |
| 2026-09-29 | v1.28 | M2-20 落地同步：02 §4.2 帧表 FRAG 行回填 **0x0B**（载荷 {dgramId, index, more, chunk}；明文超 1368B=1400−16头−16tag 按 1352B 切片，more=false 末片，channelId+dgramId 攒齐重组；阈值与承载类型解耦）+ 帧类型合法域上界扩至 0x0B（原 0x0A 会拒收 FRAG 断会话）；05 §2.4 附实现注记（分片/重组在 TunnelSession 帧层、per channel in-flight 8/驻留 10s；channel 双端同构 _udpChannels、访问侧端点表同锁上限检查；OPEN_FAIL 表项保留静默丢弃；空闲扫描 5s×60s 可配）+ §2.5 注 UDP 侧 L3 与 TCP 共用 TryResolveTarget |
| 2026-09-29 | v1.29 | M2-21 落地同步：02 §4.2 帧表 REKEY_ACK 行回填 **0x0C**（与 REKEY 载荷同构 81B；旧发送钥封印；合法域上界扩至 0x0C）+ §4.4 补切换次序保证（响应方接收环先行→旧钥 ACK→发送钥交换；发起方解析 ACK→派生→双钥切换**内联接收循环**——修复"ACK 唤醒异步任务再轮换"竞态窗：响应方发 ACK 即换钥，紧跟的新代帧在旧环下不可解→SEC-12 误判断链，间歇复现于高频数据流+短轮换周期）；05 §2.3 附背压实现注记（CreditGate 账本在 TunnelSession、SpliceIn 每块消费即回 WINDOW、M1 槽位机制删除、256KiB 入站队列保留兜底）+ §4 附 M2-21 实现注记（TTL 仅发起方/静态钥透传/TriggerRekeyAsync 预留/失败仅日志下轮重试） |
| 2026-09-29 | v1.30 | M2-24 落地同步：05 §1.1 接口块补 CheckHealth 只读探测原语（存在性+IP 一致性；探测不可用按健康返回防重建风暴）+ 自愈实现注记（检测循环与重建编排归客户端宿主 NicHealthMonitor，30s 探测→Remove+Ensure 重建→恢复沿 Restored 事件）；§1.2 Windows 探测手段（独立 Open/Close 句柄 + LUID 单播表比对）、§1.3 Linux 探测手段（sysfs 存在性 + SIOCGIFADDR 主地址）；§2.1 补 listen_failed 自动重试注记（重试集+周期 5s+恢复沿触发 RetryListenFailedAsync 非阻塞闸串行，M1 附录 A.4 Wintun 重启竞态规律复现收口，不再需要手工 retry） |
| 2026-09-29 | v1.31 | M2-25 落地同步：05 §1.2 卸载注记（INicManager.RemoveLeftoverAsync 遗留适配器移除[Open→WintunDeleteAdapter forceCloseSessions]、ClientUninstaller `--uninstall` CLI 编排[可选 0x12 解绑确认=发送后观察连接离开 Established、配置 --keep-config 默认/--purge]、Wintun.dll 与映射监听释放的职责边界[安装器/A-11、服务停止]）+ §1.3 Linux 卸载（ip link delete；RTM_DELLINK A-11 补强） |

# 任务清单 · P2P 内网穿透产品

> 依据：[PRD 09 里程碑与验收](../prd/09-里程碑与验收.md) + [技术设计 v1.6](../技术设计文档/README.md) + [开发规范](../开发规范/README.md)
> 定位：**实现层执行台账**——追溯矩阵管"设计覆盖了什么"，任务清单管"代码写到哪了"。一个 FR 进入编码，就必然出现在某里程碑清单中。

## 索引

| 清单 | 里程碑 | 出口标准 | 状态 |
|---|---|---|---|
| [M1-核心链路.md](M1-核心链路.md) | M1 · 核心链路可用（两台设备注册 + 第一条 UDP 直连映射） | 验收场景 A-1~A-4 通过 | ✅ 收官（2026-09-12；M1-37 Windows 轮完、环境依赖项转 M2 穿插，见清单附录 A.3 收官口径） |
| [M2-打洞完整与授权闭环.md](M2-打洞完整与授权闭环.md) | M2 · 打洞完整与授权闭环（TCP 打洞、中继回退、分组与远程码全量生效） | 验收场景 A-5~A-9 通过 | 🔨 进行中（2026-09-12 起；M2-03/04/05/06/07/16/17/23/30/31 ✅ 10/35） |
| M3（待建） | 管理面与可观测 | A-10~A-12 | — |
| M4（待建） | 交付打磨 | 交付清单齐全 | — |

## 使用规则

1. **状态图例**：☐ 未开始　🔨 进行中（注执行人/会话）　✅ 完成（注日期）　⏸ 阻塞（注明阻塞原因与关联 OQ/TD）；
2. 每个任务完成前执行[开发规范 01 §7.2 交付自检清单](../开发规范/01-AI开发基础规则.md)，通过才可勾选 ✅；
3. **任务粒度 = 一次可评审交付**：一个任务内含自身单测；跨任务依赖见各清单依赖列；
4. 编码中发现规格缺口/矛盾：停任务 → 按 AI-08 上报（新 OQ 自 OQ-18 续编）→ 决议后更新任务描述，不擅自扩大任务范围；
5. 任务清单不承载设计决策：实现中产生的新决策先记 TD（设计 README），再回填任务描述引用该 TD；
6. 新任务在对应清单末尾续编编号（如 M1-38）；插入不重排已有编号。

## 修订记录

| 日期 | 说明 |
|---|---|
| 2026-08-20 | 创建目录；M1 清单 v1.0（37 项任务，六阶段） |
| 2026-08-20 | M1 清单 v1.1 回归检查：13 处修订（6 处 M2 越界收界、auth/wizard 端点补齐、2 处依赖修正、4 处文案），详见 M1 修订记录 |
| 2026-08-20 | M1 清单 v1.2 完整性检查：10 处修订（macCode 生成/注册→网卡触发链/被动侧打洞响应/NIC 测试替身 4 处 P1，隧道复用/relayAllowed/Serilog/export-ts/STUN 注入/已有账号绑定 6 处 P2）；发现设计缺口 1 项 → **OQ-18 打洞端点互换时序待决议** |
| 2026-08-20 | M1 清单 v1.3：OQ-18 决议（两段式，TD-19）落地——设计 v1.7 同步，任务 M1-06/08/17/26 更新 |
| 2026-09-12 | **M2 清单建立 v1.0**（35 项任务六阶段，含 M1 遗留穿插项）；编制回流决议 OQ-19/TD-20（0x70 增可选 punchConcurrency）——PRD v0.6/设计 v1.8 同步；FR-S-203 admin 首登提示顺延 M3（用户决议）；M1 收官（Windows 轮完、v1.8 收官口径），索引 M1 行置 ✅ |
| 2026-09-12 | M2 开工：**M2-03 协议消息补全 ✅**（1/35）——14 msgType schema + PcpCodec 登记 + 0x70 punchConcurrency（旧线兼容）；字段级定案回流设计 02 §2.4 八行（设计 v1.10）；测试 373 全绿、Core 覆盖率 82.69%；索引 M2 行置 🔨 |
| 2026-09-12 | **M2-04 STUN-TCP Binding 封装 ✅**（2/35）——Core/Stun 增 StunTcpFraming（RFC5389 §7.2.2 流定界）+ StunTcpProber（端口 L 单事务即关，M2-06 服务端复用同定界）；测试 8 项新增，全仓 381 全绿、Core 覆盖率 82.69% |
| 2026-09-12 | **M2-05 REKEY/WINDOW 帧协议层 ✅**（3/35）——Core/Tunnel 增 PtpRekey（81B 载荷双重 ECDH 同式派生、AEAD 通道内免握手 mac）+ PtpReceiveKeyRing（2s 排水窗、至多保留一代）+ CreditWindow（64KiB 不部分扣/回报封顶）；05 §2.3 帧值 0x09→0x0A 勘误补遗（设计 v1.11）；测试 11 项新增，全仓 386 全绿、Core 覆盖率 82.69% |
| 2026-09-12 | **M2-16 Puncher TCP 打洞 ✅**（4/35）——客户端增 TcpPunchPlan/TcpPunchFleet（listen(L)+N 并发 connect、胜出释放其余）+ TcpFrameTransport（u16 前缀定界复用 §6.2）+ Puncher TCP 两向（THello1 扇出消歧、TunnelSession.FromEstablishedKeys）；N 通路 OQ-19 打通（0x70 上送→服务端回填→双方同 N）；proto 路由（映射 proto→打洞承载）；场景 A-3/A-4 升级 TCP 打洞真实栈全链路（TcpStunStub + N=1 恒等命中）；02 §5.2 补承载分帧条目（设计 v1.12）；测试 +30，全仓 416 全绿、Core 覆盖率 83.61% |
| 2026-09-13 | **M2-30 NatSimulator-TCP ✅**（5/35）——新增 TcpNatSimulator（TD-17 导演+桥接：STUN-TCP 代理改写映射地址/每客户端端口子段/探测后 E+0..E+4 打洞窗口/出站归因按 accept 序分配/APDF 精确身份过滤+探测映射 listen 交付（TD-21）/miss 600ms 有界重试=SYN 重传语义/桥接幂等去重）；UdpNatSimulator 增 SymmetricSequential 与 UdpBlocked（丢弃 UDP 出站迫使 TCP 承载，FR-S-704）；测试 +10（UDP 两 Theory 扩参+背靠背 +1+UdpBlocked、TCP 6 项含 N=2 rendezvous 四元组交叉与三类 miss）；设计回流 TD-21 勘误命中代数（必 miss ⟺ |ΔN|≥2 且 min≥2——A-5 miss 断言取 (2,4)/(2,5) 类对，README v1.13）；全仓 426 全绿、Core 83.61% |
| 2026-09-13 | **M2-31 场景 A-5 ✅**（6/35）——SymmetricSequential 双端 TCP 打洞 N=1~5 Theory 全序列命中矩阵（A5-MATRIX 数据行暂存测试产物，M4 成文）+ 40KiB/3KiB 双连接 echo；失配 miss 以 **punchPerturbation 时序扰动**等效（协议统一 N（TD-13/20）→ N_A≠N_B 不可达——原 (2,4)/(2,5) 类对口径修正，TD-21/设计 v1.14，09 §2.2/§2.3 同步）；TcpNatSimulator 场景端口段独立 20500 防并行互撞；测试 +6（集成 49→55），全仓 432 全绿、Core 82.69%、CI quick 通过 |
| 2026-09-22 | **M2-06 STUN-TCP + 四道闸 ✅**（7/35）——StunGuard（TD-18 闸①③④+计数：UDP 单 IP 令牌桶/TCP 并发≤4/设备 QPS/全局秒窗熔断 5s，TimeProvider 计时）+ StunService 增 3478/TCP 短事务（StunTcpFraming 复用、3s 读超时、回包即关、nonce 缓存两运输层共用）；闸④ 执行口径单级（05 §7.1 中间级裁撤，设计 v1.15）；Program 接三配置键；测试 +15（服务端 70→85），全仓 447 全绿、Core 83.61%、CI quick 通过 |
| 2026-09-22 | **M2-07 中继服务 ✅**（8/35）——RelayService（UDP 单 socket+TCP 转发对双承载、8B 会话头剥离零解密、JOIN 端槽认领[未知源保守丢弃]、0x74→Grant 双侧下发[三错误路径 5002/1001/4005]、90s 空闲回收+TCP 断连即收、per-end 写串行化）+ SignalingCoordinator relayAllowed 真实合成与会话台账 120s；02 §6.2/05 §6.1 实现注记（设计 v1.16：§6.1④ 地址更新收窄为 JOIN 认领）；测试 +12（服务端 85→97），全仓 459 全绿、Core 83.61%、CI quick 通过 |
| 2026-09-22 | **M2-17 RelayClient ✅**（9/35）——RelayClient 静态编排（AllocateAsync 0x74 族配对 + JoinAsync）+ RelayTransport:ITunnelTransport 双承载（UDP 500ms 窗重发 JOIN/[0x02]、TCP 首帧分帧；发送附加 8B sid、接收即裸 PTP 帧；90s 空闲自关闭 watchdog 兜底）；集成测 +5（含 Grant 双侧同 sid、密文逐字节往返、1001、JOIN 超时、FakeTime 空闲自关闭）；全仓 464 全绿、Core 83.61%、CI quick 通过 |
| 2026-09-22 | **M2-23 peers.json 与 /api/peers ✅**（10/35）——PeersStore（{deviceId→relayFallback} 默认关/原子替换/损坏自愈/非法键容错）+ PeersApi GET·PUT（04 §2.4 形状、passive 2002）+ PunchOutcome.RelayAllowed 出队合成（本地配置 AND Ack.relayAllowed，仅 Ack 后失败携带，供 M2-18）；测 +12，全仓 476 全绿、Core 83.61%、CI quick 通过 |

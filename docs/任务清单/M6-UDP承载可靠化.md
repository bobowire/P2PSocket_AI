# M6 · UDP 承载可靠化 · 任务清单

> 所属文档集：[README](README.md)　创建：2026-11-14　状态：✅ 收官（8/8，2026-11-15——代码/测试/基准/文档全链落地，性能闸 PASS、弱网矩阵全格绿；三端灰度分发与混布实测=待实机窗[部署活动，服务端先行口径]；立项评审 2026-11-14 决议点 7/7 批复）
> **目标**：UDP 打洞直连承载可靠化——单 socket 上 KCP 多 conv（conv=channelId）按 PTP channel 分流 + 裸 UDP 旁路（UDP_DGRAM 丢包容忍语义保持）：channel 级可靠有序、channel 间队头隔离；打洞流程/PTP 握手/成功判定/加密体系零语义变更（PRD NET-77 · 技术设计 11 号 v0.2 · TD-29）。
> **出口标准**：技术设计 11 §12 两道闸全过——性能闸（0% loss 回环：KCP 吞吐 ≥ 裸 UDP 70% 且 PING RTT 增量 ≤5ms）＋正确性闸（弱网矩阵全格绿：0/1/5/20% × 乱序 × 100ms 抖动 + 突发档，KCP 组零缺段/零挂起/会话存活，裸 UDP 对照组红证）＋混布矩阵全组合＋全仓测试绿（Integration 114 基线不减）＋编译 0 警 0 错。
> **立项背景**：M2-38 遗留（待办池 P1②）——UDP 直连承载为裸数据报：丢 DATA 帧字节流缺段/丢 WINDOW 信用发送侧永久挂起/丢 OPEN 帧断连，单帧丢失触发不了 KEEPALIVE 断链兜底；M2-38 对策=中继 TCP 优先，直连成功但弱网丢包路径无帧级恢复。设计文档 11 号 2026-10-25 起草（用户指令"直接进入单 socket 多 conv 按 channel 分流的最终形态"），2026-11-14 评审 7/7 决议点批复立项。
> **决议**（2026-11-14，7/7）：① conv=channelId 多 conv 模型（conv 0=全局控制流/OPEN 走 conv 0 无 lazy accept/生命周期四触发点；⑤ conv 上限 256 随①同章成立）② ReplayWindow 64→4096（接收侧策略 wire 兼容）③ bearerCaps 四消息 u32 位图+两端同式本地判定（开火前完成、无第三态无脑裂）+服务端纯存转+kill-switch ④ **KCP 算法层自研移植**（协议数学对照 skywind3000/kcp 1:1 不重发明+kcp2k/Cysharp/limpo1989 壳层借鉴全 MIT attribution；流模式裁 frg/WASK-WINS；**dead_link 不实现=重传无上限活性归 KEEPALIVE**）⑥ 门禁修订版：正确性闸矩阵全格+新增突发档+丢包-吞吐曲线（高丢包不设绝对吞吐门禁）⑦ 新立 M6/NET-77（PRD v0.16）/TD-29 定案。
> 任务执行受 [开发规范](../开发规范/README.md) 约束；状态图例与使用规则见清单 README。

## 0. 依赖总览

```
M6-01 协议核（自研移植）─▶ M6-02 KcpTunnelTransport ─▶ M6-03 会话层三缝 ─▶ M6-04 caps 协商 ─▶ M6-05 Puncher 接线 ─▶ M6-06 弱网矩阵 ─▶ M6-07 基准调参 ─▶ M6-08 部署灰度
```

关键串行链（全链在上图，无分支）：**M6-01 协议核（纯算法属性单测门禁）→ M6-02 承载模块（受控介质单测）→ M6-03 会话层接线（全帧型路由单测）→ M6-04 caps 协商 → M6-05 Puncher 接线 → M6-06 弱网红绿矩阵（立项级证据）→ M6-07 门禁确认 → M6-08 部署**。M6-01~08 严格串行（设计 11 §14 依赖列），无并行泳道。

## 1. M6 非目标（防范围蔓延，以下明确不做）

| 不做 | 归属 |
|---|---|
| TCP 直连承载与中继 TCP 承载加 KCP（TCP 本身可靠，叠加纯开销） | 设计 11 §1.3 |
| 中继 UDP（7010）承载可靠化 | 二期复用本期 KcpTunnelTransport（11 §1.3） |
| 混淆/抗指纹（udp2raw 类）——KCP 魔数指纹本期接受 | 02 §4.3 IObfuscationAdapter 扩展点维持预留 |
| 更换 PTP 加密体系/QUIC 化/多路径 | TD-29 否决记录（socket 所有权死穴等四条） |
| KCP 协议数学重新发明（RTO/快重传/拥塞窗公式自创） | 决议④：1:1 移植，只换壳 |
| KCP 承载会话中途切换（KCP↔裸） | 02 §4.5 承载与会话同生共死，断链重建重新协商 |

## 2. 任务清单

| 编号 | 任务 | 依据 | 完成判定 | 依赖 | 状态 |
|---|---|---|---|---|---|
| M6-01 | **KCP 协议核自研移植**（P2P.Core 新建 Transport/Kcp 纯算法层）：KcpSegment 段 struct（池化）＋KcpPeer 单 conv 端点（snd_queue/snd_buf/rcv_buf/ack_list＋RTO 估计 RTT/RTTVAR＋快重传＋nodelay 收紧曲线＋una 滑动＋窗口通告——协议数学对照 skywind3000/kcp 参考实现 1:1）＋IKcpDatagramSink/Source 外部 socket 收发注入缝（算法层不认 Socket 类型，禁自建 socket）＋无任何 static 全局态＋流模式（PTP 帧自带 u16 长度自分界；裁 frg 分片与 WASK/WINS）＋**dead_link 不实现（重传到会话消亡为止，永不主动放弃）**＋MIT attribution 文件头（四参考出处与许可注记） | 11 §2.3/§5.5（决议④定案） | 纯算法属性单测全绿：受控丢包 0~30% × 乱序 × 重复注入下可靠有序性质（零缺段/零重复交付/零死锁、闭合窗收敛、ACK 全确认）＋对拍 skywind3000/kcp 参考行为关键场景（RTO 退避序列/快重传触发点） | — | ✅ |
| M6-02 | **KcpTunnelTransport**：收包泵（ReceiveFromAsync 循环：魔数 0x4B→解 conv→conv 表命中→端点 Input；unknown-conv 丢弃+kcp_unknown_conv_dropped 计数）＋裸路径白名单（ParseHeader type ∈ {UDP_DGRAM 0x09, FRAG 0x0B}→裸接收队列；**迟到 STUN 应答降级丢弃+计数**——顺带修复现状整隧道误杀潜伏缺陷）＋裸队列有界 DropOldest＋更新泵（单 PeriodicTimer 10ms 驱动全 conv Update/Flush；空闲降频优化后置）＋conv 表 ConcurrentDictionary（Actor 纪律豁免点）＋裸旁路 SendToAsync（钉定端点）＋Adopt(Socket,…) 所有权移交（UdpPunchTransport 释放不关 socket）＋KcpTunnelTransportOptions 参数组（11 §5.4 默认表，M6-07 校准回填） | 11 §5 | 受控介质单测（内存 socket 对+可编程通道：丢包/乱序/延迟注入）：帧型×conv 路由穷举、unknown-conv 拒绝、conv 上限降级 conv 0、Adopt 单次性、裸路径白名单闭包（迟到 STUN 形似帧不再杀隧道） | M6-01 | ✅ |
| M6-03 | **会话层三缝**：IKcpConvControl（EnsureChannel/RemoveChannel——transport 未实现即 no-op 零回归）＋四触发点接线（SendOpenAsync 前置/接收循环 Open case/OnOpenResult !Ok·OnClose·本地关闭三路径）＋ReplayWindow.WindowSize 64→4096（决议②；bitmap 512B；同 counter 重放拦截与窗口无关=安全语义零变化）＋M2-38 不变量收窄表述（counter 序=conv 内线上序）落地核对 | 11 §4.2/§4.4/§6（决议①②） | 全帧型路由单测（OPEN→conv 0/DATA·CLOSE·WINDOW·OPEN_RESULT→channel conv/UDP_DGRAM·FRAG→裸旁路/KEEPALIVE·REKEY 族→conv 0）＋ReplayWindow 4096 回归（含跨 conv 乱序深度注入）＋TCP/中继/裸 UDP 承载 no-op 零回归 | M6-02 | ✅ |
| M6-04 | **caps 协商**：0x70 PunchRequest.bearerCaps/0x71 PunchInvite.peerBearerCaps/0x76 PunchEndpoint.bearerCaps/0x70 Ack.peerBearerCaps 四消息 u32 可选字段（MessagePack 尾部加法惯例，缺省=0）＋位定义 bit0=CAP_KCP＋服务端 SignalingCoordinator 纯存转两处（localCandidates v1.63 同款）＋两端同式判定 `kcpEnabled=(myCaps&peerCaps&CAP_KCP)!=0`（开火前完成、仅本地输入无第三态）＋kill-switch 设置项 kcpBearer（默认 true，false=不上送位强制裸 UDP 双角色同效）＋测试缝 ClientRuntimeOptions.DisableKcpBearer（DisableLatePunch 同款） | 11 §8（决议③） | 判定公式两端一致性单测（四组合输入断言同判）＋混布矩阵单测（新新新=KCP/新新+旧S=一致降级/单旧=裸/旧旧=现状）＋服务端透传单测（存转不解析）＋旧服务端剥字段降级路径实证 | M6-03 | ✅ |
| M6-05 | **Puncher 接线**：InitiateUdpAsync/RespondUdpAsync 尾段承载切换分支——caps 判定 true→KcpTunnelTransport.Adopt(socket,sessionId,transport.Peer)→TunnelSession.FromEstablishedKeys 接 KCP 承载（M2-16 缝既有），false→现状裸路径（逐字节不动）＋打洞期 THello1/2/TConfirm 裸帧零改动＋会话中途不切换承载＋caps 判定与 0x78 晚探测正交核对 | 11 §7 | UdpNatSimulator 两态全矩阵回归（kcp on/off × 打洞成功矩阵/建立耗时/映射收发全链）＋Adopt 后 socket 所有权单次性（Puncher 释放不关）＋KEEPALIVE/OPEN 首帧走 KCP 实证 | M6-04 | ✅ |
| M6-06 | **弱网场景组**（立项级证据）：UdpNatSimulator 注入旋钮（丢包率/乱序深度/重复/延迟抖动/带宽+**突发档=200ms 窗连片丢**[决议⑥新增，模拟 WiFi 漫游/NAT 重绑定形态]）＋**弱网红绿矩阵全格门禁**——丢包 0/1/5/20% × 乱序 × 100ms 抖动+突发档，8 并发映射持续传输：KCP 组零缺段/零 credit 挂起/会话存活，**裸 UDP 对照组同条件复现 M2-38 家族故障（红证）**＋会话级全帧型往返＋场景回归（S54/命中矩阵全量 kcp on/off 两态）＋黑洞（全丢）下 KCP 重传不伪造活性（3×20s miss 断链照常） | 11 §11（决议⑥修订版） | 矩阵全格绿（含对照组红证）＋Integration 114 基线不减＋S54/命中矩阵两态全过 | M6-05 | ✅ |
| M6-07 | **基准与调参**：P2P.Bench 四组对照（裸 UDP/KCP nc=1 默认/KCP nc=0 对照/TCP 中继参照）×指标（吞吐/RTT 分布/建立耗时增量/CPU·内存 ResourceSampler）＋调参矩阵（Nodelay×Resend×UpdateInterval 10/20/40ms×nc）→推荐参数回填 11 §5.4 表＋**丢包-吞吐曲线入基准报告**（高丢包段不设绝对吞吐门禁——决议⑥）＋验收门禁确认（性能闸 70%/5ms@0% loss；不达线回调参矩阵→移植核对层）＋TD-24 L1 口径如实标注（本机回环≠真实网络） | 11 §12（决议⑥） | 两道闸全过＋推荐参数表回填＋基准报告增节（丢包-吞吐曲线+四组对照+环境局限标注） | M6-06 | ✅ |
| M6-08 | **部署灰度与文档收官**：三端分发顺序=**服务端先行**（纯透传零风险）→客户端灰度+bak 回滚件＋punch_stats 扩丢包统计（L2 包络验证）＋生产观察（kcp_unknown_conv_dropped/裸路径丢弃计数/conv 存量诊断计数接入本地诊断页可选轻量）＋核心文档修订同步（11 §13 全表：02 §4.2 新增 §4.6+§4.3 措辞+§2.4/§5.1 bearerCaps 四行+§6.2 二期注记、05 §2.3/§2.4 模块设计+三缝注记、05 §3/§4 Puncher 双角色+承载协商、07 §9 六条安全注记、09 §2/§3、10 追溯矩阵 NET-77 行、README TD-29 行/索引/修订记录）＋收官口径与索引更新 | 11 §13/§8.3 | 混布实测（三端灰度期间新旧组合日志核对）＋文档全表落地＋M6 索引行收官 | M6-07 | ✅ |

## 3. 修订记录

| 日期 | 版本 | 说明 |
|---|---|---|
| 2026-11-14 | v1.0 | 首版立项（设计 11 号 v0.1→v0.2 评审批复决议点 7/7 当日）：T1-T8 切分承设计 11 §14 映射 M6-01~08；决议④（自研移植+dead_link 不实现）折入 M6-01、决议⑥修订（矩阵全格+突发档+丢包-吞吐曲线）折入 M6-06/M6-07、部署顺序（服务端先行）折入 M6-08 |
| 2026-11-15 | v1.1 | **M6-01~06 ✅（代码与测试全链落地）**：①协议核（P2P.Core/Transport/Kcp——数学对照 ikcp.c 逐函数 1:1[RTO 退避 nodelay ×1.5 对拍 200/500/950/1625ms 序列/快重传先于 RTO/FASTACK_CONSERVE]、流模式裁 frg+WASK-WINS+dead_link、WINS 裁剪的窗口重开通告补偿防死锁、时钟注入确定性单测）；②KcpTunnelTransport（魔数 demux/conv 表/裸旁路白名单闭包[顺带修复迟到 STUN 误杀]/DropOldest/Adopt/排空销毁/即时 flush）；③三缝（IKcpConvControl 五接线点+ReplayWindow 4096 位图重写[同 counter 重放拦截不变]）；④caps（四消息 Key(9) 尾部加法+服务端纯存转两处+两端同式判定+kill-switch kcpBearer/DisableKcpBearer+Web 设置链 ExportTs 再生）；⑤Puncher 双角色 KCP 分支（外驱动握手 M2-16 形态→Adopt→ReleaseSocket→FromEstablishedKeys；裸路径逐字节不动）；⑥弱网红绿矩阵（UdpNatSimulator 注入旋钮[丢/乱/重/抖/突发+相位对齐]+会话级矩阵 8/8[KCP 组 1/5/20%×乱序×抖动+突发档零缺段零挂起、裸 UDP 对照组红证、黑洞档断链照常+净网重建]；世界级场景 proto=tcp 走 TCP 打洞不经 UDP NAT，故 KCP 弱网证据由会话级矩阵承载+世界级补 A3 形态 kcp 关布线）。测试：Core 223/Client 241/Server 305 全绿（基线 196/222/303 全保）；Integration 114 基线+新增 9 例。M6-07/08 未开工 |
| 2026-11-15 | v1.2 | **M6-07 ✅ 基准与调参（性能闸 PASS）**：P2P.Bench 新增 KcpBearerBench 三段（⑧四组对照 ⑨调参矩阵 24 cell ⑩丢包-吞吐曲线六档；会话级 DATA 口径=同信用窗同 chunk 仅承载不同，UdpNatSimulator FC×FC 介质+tcp 参照=回环 TcpFrameTransport）。**性能闸判定 PASS：吞吐比 70.7%（≥70%）+PING p50 增量 0.052ms（≤5ms）**；nc0 对照 87.7% 余量；小帧档 KCP 反超裸承载 10.8 倍（裸满速内核收缓冲溢出丢包[M2-38 机理再现] vs KCP rcv_wnd 背压 delivered 恒 100%）；丢包曲线 0→30% 完整性恒成立（30% 档 45.6Mbps）；调参矩阵维持默认族推荐（nd=1/rs=2/iv=10/nc=1——nc=0 弱网档未验证不做默认，Nc 行注记回填 11 §5.4）。文档：基准报告 v1.1 §5 全节+设计 11 §5.4 校准回填。debug 记录：Bench 建链三修（上游替身缺失/lambda 参数位/Opened TCS 预置）+裸组完整性口径修正（宽松记账） |
| 2026-11-15 | v1.3 | **M6-08 ✅ 部署灰度与文档收官（8/8 收官）**：①punch_stats 扩承载统计——0x72 尾部 Key(7) Bearer（0=裸/1=KCP，仅直连成功携带；PcpCodec 尾部加法容忍）+PunchStat.bearer 列（EF 迁移 20261115092307_AddPunchStatBearer，启动 Migrate 自动加列旧数据 NULL 兼容）+Dashboard KcpDirect24h/RawDirect24h 承载分组（api-server.d.ts 再生+server-app 打洞卡一行）——L2 包络验证载体（KCP 采用率+按承载分组成功率/时长包络）；②生产观察——KcpTunnelTransport.Snapshot()（承载层计数+全 conv 算法层聚合）+TunnelSession.KcpStats 访问器+诊断页隧道快照 TunnelView.Kcp（ExportTs 再生+client-app 承载列「直连·KCP」+tooltip 计数）；③文档全表（11 §13 九个未动点全落地+设计 README v1.85 收官行，明细见该行）；④测试 +5（Core bearer 往返/Server 落库 2 例/Client BearerFor 3 例）。三端灰度分发（服务端先行）与混布实测=部署活动待实机窗，代码/协议/文档面全就绪 |

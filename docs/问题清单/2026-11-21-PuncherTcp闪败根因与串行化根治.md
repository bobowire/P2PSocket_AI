# 2026-11-21 · PuncherTcp 恒等 NAT 织物闪败根因与根治（M8-06 收口时发现）

## 现象

- `P2P.Client.Tests` 全套运行时 `PuncherTcpTests.TCP两段式全链路_N1_双方握手成功_可互通` 断言失配（L593）：
  `Assert.Equal(outcomeB.LocalEndpoint!.Port, reportedPair.Tcp!.Port)` —— 期望 8235、实际 8213（两端口差 22，量级=临时端口段游走）。
- 单测类隔离运行恒绿；全套并行运行闪败。**基线 HEAD 2322eb4（M7 收官提交）即存在**：3 轮 1 败（~1/3 概率）；
  M8 加 5 例后负载上升，近必现（4/4）。

## 根因（恒等 NAT 织物的固有竞态）

测试织物 `FakeTcpSeams.TcpProbeAsync`（TcpPunchTests.cs）模拟恒等 NAT：探测事务即关 socket（M2-04 契约，
`socket.Dispose()`），仅保留端口号 L 供 Puncher 后续 `listen(L)` **重绑**复用。真实世界该端口由 NAT 设备持有
（映射钉活），不存在重绑；织物里 L 释放与重绑之间存在窗口——并行测试类的出站连接（临时端口段 49152+ 顺序分配）
可在窗口内抢走 L → B 侧 listen(L) 失败 → 扇形出站腿胜出（本地口=新临时口 8235）→ 与 0x76 上报口（8213）失配。
双方握手与四元组交叉一致性断言全部通过——仅「上报口=胜出口」这条织物理想断言失配。**非生产行为缺陷**
（生产 NAT 持有映射），属测试织物在共享机器负载下的固有闪败，与 FreePort 迁带/ClosedPort 专带/
每运行时独立网卡替身同族（「跨类串扰家族」，M2-38 起多次治理）。

## 验证过程（M8-06 收口实录）

1. 基线 worktree（HEAD 2322eb4）全套 3 轮：1 败 → 存量问题确证，非 M8 回归；
2. M8 树排除 M8 新增 5 例（=基线测试集）3 轮全绿 → M8 源码改动零影响；
3. M8 树含新增 5 例 4 轮全败 → 新增用例的并行负载放大既有竞态（LoopbackMappingModeTests 的
   ControlClient 对 127.0.0.1:1 即拒即连=高频临时端口扰动源，已改 TEST-NET-1 黑洞地址降扰动，单跑不足以根治）。

## 根治（2026-11-21 落地）

`tests/P2P.Client.Tests/xunit.runner.json`：`parallelizeTestCollections=false`（类级并行全关）。
理由：本套件真 socket 用例密度全仓最高，逐缝加固已多轮（FreePort 迁带/专带/独立替身），整体串行一劳永逸
消灭跨类临时端口扰动族；代价 9s→~46s（五套件总时长仍以 Integration ~7min 为长尾，CI 预算余量充足）。
落地后 269/269 ×3 轮全绿（含 M8 新增 5 例）。

## 关联

- M8-06 测试收口（任务清单 M8）；五套件回归口径不变（套间本就串行，ci.yml 2026-10-26 实弹回流注记）。

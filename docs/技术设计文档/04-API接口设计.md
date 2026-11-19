# 04 · API 接口设计

> 所属文档集：[README](README.md)
> 两个独立 HTTP 端点：**客户端本地 API**（127.0.0.1:7100，**请求 token 鉴权**——v1.67/M5 修订，SEC-82/FR-C-813；TD-12 的「无鉴权」旧口径废止）：载体=REST `Authorization: Bearer` / WS 与自动打开 URL `?token=`，静态资源豁免，07 §14.2）与**服务端 Web API**（127.0.0.1:7500，Cookie 会话 + IP 黑名单中间件前置[认证之前，07 §13.3]）。
> 服务端业务数据（注册/分组/映射/信令）**不经 HTTP**，全部走客户端本地 API → 控制协议转发（TD-08）。

## 1. 通用约定

- REST + JSON（UTF-8）；时间 ISO8601 UTC；
- 响应包裹：`{ "code": 0, "msg": "ok", "data": ... }`；`code != 0` 见 §5 错误码表；
- 列表接口统一 `{ items, total }` 分页参数 `page/pageSize`（默认 1/20）；
- 本地 API 跨域：仅允许同源（SPA 由同端口静态托管），无 CORS 需求。

## 2. 客户端本地 API（127.0.0.1:7100）

> **token 鉴权（v1.67/M5，SEC-82，M5-12）**：token=32B CSPRNG hex，服务启动生成、随服务重启轮换、落 ACL 文件 `web-token`（Windows 数据目录 ACL/Linux 0600，07 §14.2）；REST=`Authorization: Bearer`、WS 与自动打开 URL=`?token=`（SPA 读取后转存 sessionStorage）；未携带/错误 → REST 401{2001}、WS 升级请求 401；静态资源豁免（保护目标=API 动作面）。

### 2.1 系统与设备

| 方法 | 路径 | 说明 | 对应需求 |
|---|---|---|---|
| GET | `/api/system/state` | 运行状态：`{ phase: unregistered\|wizard\|running\|degraded, serverReachable, protocolVersion }` | FR-C-801 |
| GET | `/api/device` | 本机：deviceId、**deviceName（v1.58；未改名[含存量升级]回落主机名=注册默认名，仅展示）**、remoteCode、virtualIp、登录账号、能力模式（normal/passive） | FR-C-801、FR-S-107 |
| PUT | `/api/device` | `{ deviceName }` 设备改名（0x13）：trim 后 1~64 字符（越界 1001；passive 本地拒 2002）；成功后 state.json 持久化（凭据恢复重注册沿用，防洗回 hostname 默认值）+ WS device_list 提示（本机设备列表即时刷新） | FR-S-107 |
| POST | `/api/device/reset-remote-code` | 请求服务端重置远程码，返回新码 | FR-S-903 |
| GET | `/api/settings` | settings.json 全量 | FR-C-808 |
| PUT | `/api/settings` | 修改：serverAddrs（域名/主备多候选，FR-C-604）、punchConcurrency(1~5)、webPort 等（校验后即时生效/提示重启） | FR-C-808、OQ-1 |

### 2.2 首启向导（未注册阶段）

| 方法 | 路径 | 请求体 | 说明 |
|---|---|---|---|
| POST | `/api/wizard/server-test` | `{ serverAddr }` | 连通性探测（含版本协商预检） |
| POST | `/api/wizard/register` | `{ serverAddr, mode: "default"\|"invite"\|"account", inviteCode?, username?, password?, deviceName }` | 执行 FR-C-102/103：注册+入组（account 模式同时完成用户注册/登录+建组） |
| GET | `/api/wizard/result` | — | 注册结果（含下发的 remoteCode/virtualIp） |

### 2.3 账号（转发控制协议 0x20~0x22）

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | `/api/auth/register` | `{ username, password }` |
| POST | `/api/auth/login` | 成功返回用户信息并切换能力模式 |
| POST | `/api/auth/logout` | 切换为 passive（FR-C-603，不重启） |
| POST | `/api/auth/change-password` | 修改自己密码（0x23，FR-S-205） |
| GET | `/api/auth/me` | 当前登录态 |

### 2.4 设备发现与分组（仅 normal 模式，否则 2002 FORBIDDEN_PASSIVE）

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/devices` | 可见设备列表（账号 ∪ 分组），字段：deviceId/deviceName/remoteCode/virtualIp/online/groups[]/lanSegments[] |
| GET | `/api/groups` | 已加入分组 + 待审批申请（若为所有者） |
| POST | `/api/groups` | `{ name, joinPolicy }` 建组（FR-C-805） |
| POST | `/api/groups/join` | `{ inviteCode }` 凭码入组 |
| POST | `/api/groups/{id}/leave` | 退出（触发 0x75 联动清理授权） |
| PUT | `/api/groups/{id}` | 所有者编辑：改名/改准入策略（0x55，FR-S-302） |
| DELETE | `/api/groups/{id}` | 所有者解散（0x56，FR-S-302）；组内在线成员收 0x75 |
| POST | `/api/groups/{id}/members/{deviceId}/kick` | 所有者移出成员（0x57，FR-S-307）；被移出设备收 0x75 |
| GET · PUT | `/api/peers/{deviceId}` | **目标设备级配置** `{ relayFallback, punchMode }`（relayFallback=D3 v0.4/OQ-10；punchMode=tcp/kcp 默认 tcp——NET-78/设计 12 号 M7，值域不含 "udp"（配置层无此值），非法 → 1001 §5；读写本地 peers.json，不经控制协议、不同步服务端；该目标设备下全部映射共用；PUT 部分更新——null/缺省=保持现值，单字段写不丢另一字段） | FR-S-702/FR-C-803/FR-C-405 |
| GET | `/api/groups/{id}/invite` | 生成邀请码（所有者） |
| DELETE | `/api/groups/{id}/invite` | 撤销邀请码 |
| GET | `/api/groups/{id}/requests` | 审批队列 |
| POST | `/api/group-requests/{id}/approve` · `/reject` | 审批（FR-S-305） |

### 2.5 端口映射（本地核心）

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/mappings` | 全量 + 实时状态（见 §2.8 状态对象） |
| POST | `/api/mappings` | `{ name, localPort, proto, targetRemoteCode, targetAddr, targetPort }`（中继回退已移至目标设备级配置 /api/peers，OQ-10）；服务端做 L2/L3 校验（0x60） |
| PUT | `/api/mappings/{id}` | 修改（禁用状态下才允许改本地端口/协议） |
| DELETE | `/api/mappings/{id}` | 删除并释放监听 |
| POST | `/api/mappings/{id}/enable` · `/disable` | 启停（enable 触发打洞流程） |
| POST | `/api/mappings/{id}/retry` | 失败手动重试 |
| GET | `/api/mappings/{id}/stats` | `{ bytesUp, bytesDown, rateUp, rateDown, path, since }`（FR-C-1001） |
| GET | `/api/stats/summary` | 汇总流量：按设备/按映射分组（FR-C-1002） |
| GET | `/api/stats/export?format=csv` | 导出统计数据文件（FR-C-1002） |
| GET | `/api/lan-segments` · POST · DELETE `/{id}` | 开放内网段白名单（FR-C-701/702） |

### 2.6 日志与诊断

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/logs?level=&page=` | 结构化日志查询（NFR-51） |
| GET | `/api/logs/export` | 下载导出文件 |
| POST | `/api/diagnostics/stun-test` | 触发 STUN UDP/TCP 探测，返回 `StunTestView{publicEndpoint, udpMapping, udpFiltering, tcpSequential, tcpPortDependent, downgraded, notes[], durationMs}`（M3-15 已落地：RFC5780 子集两桶族判型 `eim/adm_or_apdm`×`eif/adf_or_apdf`，编排与降级语义见 05 §7.2 实现注记；未注册 1002、探测失败 1001；服务端双地址由 `stun_alt_addr` 配置驱动） |
| POST | `/api/diagnostics/ping-device` | `{ remoteCode }` 隧道 PING 测 RTT（PTP 0x06）。M3-16 已落地：返回 `PingDeviceView{targetDevice, targetRemoteCode, sent, received, minMs, avgMs, maxMs, viaRelay, durationMs}`（4 次 PING 各 2s 预算、丢失容忍只统计收到的样本）；错误通道：空码 1001/远程码不在可见列表 4003（同映射解析口径）/无活隧道 1002"须先启用一条到该设备的映射"；列表解析走 0x40 主动类 → passive 下本地拒发（须 normal），编排详见 05 §7.3 |
| POST | `/api/diagnostics/server-test` | 服务端连通性运行态入口（FR-C-808 诊断区）。M3-14 已落地：对 settings 全部 serverAddrs 候选**逐个** TCP 探测（复用向导 server-test 的版本协商预检同源逻辑，单元素数组独立呈现——短路语义仅服务向导换址），返回 `ServerTestView{items[]{addr, ok, detail}}`；纯 socket 不走控制通道 → passive 亦可达；未配置候选 1001 |
| GET | `/api/diagnostics/tunnels` | 活动隧道列表快照（M3-14 已落地）：`TunnelListView{items[]{peerDeviceId, label, viaRelay, isInitiator, kcp?, bearer}}`——TunnelHost 本地表（IsClosed 过滤、peerDeviceId 确定性排序）；label=本地映射运行时反查的目标远程码（引擎快照 join state.json，无映射指向时 null，前端回退 peerId 短码）；kcp=KCP 承载计数快照（M6-08，非 KCP 承载 null）；bearer=承载形态四态 `relay/kcp/raw-udp/tcp`（NET-78/设计 12 §7，M7：TunnelSession 按 transport 类型判别；设备页"隧道类型"列同源），纯本地数据 passive 亦可达 |
| POST | `/api/diagnostics/rekey` | `{ peerDeviceId }` 手动 REKEY 密钥轮换（05 §2.3 M2-21 预留收口，M3-14 已落地）：返回 `RekeyResultView{peerDeviceId, outcome, detail}`，outcome=`ok/not_initiator/busy/failed`（发起方本轮完成/本端为响应方密钥由对端轮换/上一轮未完/发送或等待 ACK 失败携原因）；空 peerDeviceId 1001/无活动隧道 1002；纯本地隧道操作 passive 亦可达 |

### 2.7 升级页（FR-C-904）

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/upgrade/info` | 代理服务端 UpdateInfo（0x03），驱动 `/upgrade` 页面渲染；协议不兼容时自动打开 |

### 2.8 WebSocket 实时通道

`WS /ws/status`（同一 7100 端口），服务端推送事件（JSON）：

```
{ "ev": "mapping_state", "id": "...", "state": "punching|direct|relay|failed|disabled|invalid", "reason": "" }
{ "ev": "mapping_stats", "id": "...", "rateUp": 1234, "rateDown": 5678, "path": "direct" }   # 1s 周期(有活跃流量时)
{ "ev": "device_list" }        # 触发前端刷新 /api/devices
{ "ev": "login_state", "mode": "normal|passive" }
```

前端全部实时状态经此通道刷新，避免轮询（06 §4）。

## 3. 服务端 Web API（127.0.0.1:7500，管理员）

### 3.1 会话

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | `/api/auth/login` | `{ username, password }`；默认 admin/admin（D16），首登响应携带 `mustChangePassword: true` |
| POST | `/api/auth/change-password` | 修改密码（首登提示，不强制阻断） |
| POST | `/api/auth/logout` | |

Cookie：`HttpOnly, SameSite=Strict`（无 Secure——HTTP，D11）；会话服务端内存态，重启失效。

### 3.2 管理功能

| 方法 | 路径 | 说明 | 对应 |
|---|---|---|---|
| GET | `/api/dashboard` | 在线设备数/分组数/活跃映射数/中继总流量/STUN QPS 与丢弃计数（stun_dropped_total，05 §7.1）/打洞成功率（近24h） | FR-S-810 |
| GET | `/api/users` · POST `/api/users/{id}/disable` · `/enable` · PUT `/password-reset` | 用户管理 | FR-S-820 |
| GET | `/api/devices` | 列表：deviceId/名称/remoteCode/virtualIp/归属/分组/在线 | FR-S-821 |
| POST | `/api/devices/{id}/disable` · `/unbind` · `/reset-remote-code` | 禁用/解绑/重置码（解绑=释放归属与身份，配合 FR-S-103 重注册） | |
| GET | `/api/groups` · PUT `/api/groups/default` | 分组总览、默认分组策略 | FR-S-822 |
| GET | `/api/group-requests?status=pending` · POST `/api/group-requests/{id}/approve` · `/reject` | 审批队列（服务端 Web 侧，FR-S-305 与客户端 0x53 同语义） | FR-S-305/822 |
| GET | `/api/mappings` | 全部映射配置与状态（只读）。**状态=TD-22 投影口径（v1.40）**：0x62 mapping_status 审计流水每映射最新一条（direct/relay/failed）为"最后已知状态"——客户端状态机是真相源（M2-08），服务端管理面仅流水投影；无流水行（长期离线）=unknown。累计流量 join mapping_stats。M3-08 落地编制补充：归属设备名/远程码/对端设备名子查询 join；`?deviceId=`（归属设备）与 `?status=`（投影态）过滤；disabled 映射仍投影最后已知流水（enabled 独立字段供前端置灰——分布的 enabled 限定是仪表盘聚合口径，列表为逐映射明细口径）；投影核与仪表盘共用 AdminDashboardApi.LatestStatusByMappingAsync | FR-S-823 |
| GET | `/api/relay/sessions` · PUT `/api/relay/config` | 中继会话列表；全局开关/限速。**限速语义=TD-23（v1.40）**：relay_rate_limit=全局字节速率令牌桶作用于中继转发发送路径（UDP+TCP 双承载，0=不限）；令牌耗尽时**新** 0x74 分配拒绝（5002，保护存量会话仅降速）；SignalingCoordinator relayAllowed 合成接入余量判定（M2-07"限速余量恒真"收口）。M3-07 落地编制补充 GET `/api/relay/config`（读现值，管理页表单回显用）；sid/端点承载表达详见 05 §6.2 | FR-S-824 |
| GET | `/api/system/config` · PUT | server_config 键值读写（**白名单键集=ConfigDefaults 全集**：业务开关在库，listen.* 进程级在 appsettings 不属此端——08 §5.1 分工；值校验 log_level 枚举/数值范围/virtual_subnet CIDR/public_addr IP 或域名，未覆盖键取种子值，FR-S-825）。M3-08 落地编制补充：GET 逐键标注 restartRequired（启动期一次性读取的键 true：public_addr/stun_*/log_level，其余运行期现读即时生效）；PUT 全量校验先行（任一键非法整单拒绝 400 {1001} 不留半更新）+审计 system_config_change+log_level 存规范形态；relay_rate_limit 进程内直调 RelayRateLimiter.UpdateRate 即时生效（与 PUT /api/relay/config 同执行链——两条写路径共享限速热路径不漂移）。v1.67/M5：安全键随 ConfigDefaults 扩充自动入白名单（device_registration_open/register_rate_per_ip/login_fail_window_sec/login_delay_cap_ms/autoban_*/control_conn_*/relay_join_rate_per_ip/alert_*/update_artifact_sha256；web_max_connections 标注 restartRequired[Kestrel 启动期设定]）；**隐藏键 ip_blacklist 不在白名单**（编辑面=/api/security/blacklist，07 §13.3）；update_artifact_sha256 值校验=JSON 对象{产物名→64 位小写 hex}；update_url 值校验**仅接受 https:// 或空**（SEC-84，存量 http 值运行时屏蔽口径见 07 §14.4） | FR-S-825 |
| GET | `/api/audit-logs?event=&page=` | 审计日志（NFR-54）。M3-08 落地编制补充：newest-first（自增 Id 降序）+事件精确过滤+分页（pageSize 默认 20、clamp 1~100）；detail 为原始 JSON 文本透传（前端格式化展示，SEC-51 写入侧已保证不含凭据材料）。M5 新事件族（v1.67）：register_deny/login_throttled/ip_block/ip_unblock/ip_auto_block/security_lockdown（07 §5/§11/§13） | NFR-54 |
| GET | `/api/security/overview` | **安全中心聚合（v1.67/M5，SEC-72/FR-S-827，M5-09）**：今日/近 7 天登录失败（login_failed+admin_login_fail）/新注册（register+register_recover）/授权拒绝族（punch_deny+mapping_deny+passive_deny+group_join_deny）计数——数据血缘=audit_logs 时间窗聚合（与审计查询同口径）+ STUN 丢弃三分桶（StunGuard 进程累计，卡片如实标注「自启动累计」）+ 黑名单快照与自动封禁规则现值（07 §13.2/§13.3） | FR-S-827 |
| POST | `/api/security/blacklist` · DELETE `/api/security/blacklist/{ip}` | IP 黑名单手动加黑（`{ip, reason?}`，manual 条目永久）/解封（07 §13.3 四面接入；审计 ip_block/ip_unblock） | FR-S-828 |
| POST | `/api/security/alert-test` | 立即向已配置 webhook 发送 type=test 告警载荷（07 §13.1） | FR-S-826 |
| POST | `/api/security/lockdown` | 踢全部会话：断开全部在线 ControlSession（reason=security_lockdown）+审计；**不设拒连窗**（口径与理由见 07 §13.4） | FR-S-827 |

## 4. 实时与统计的数据流

```
客户端内存(滑动窗口速率) ──WS 1s──▶ 前端仪表盘
客户端累计计数 ──0x64 定时30s──▶ 服务端 ──EF Core──▶ mapping_stats 表 ──GET stats──▶ 前端/后台
```

## 5. 错误码表（协议 0x7E 与 HTTP 共用语义）

| code | 名称 | 场景 |
|---|---|---|
| 0 | OK | |
| 1001 | BAD_REQUEST | 参数/格式错误 |
| 1002 | NOT_FOUND | 资源不存在 |
| 1003 | CONFLICT | 端口占用/重名等 |
| 2001 | UNAUTHORIZED | 未登录/凭据失效 |
| 2002 | FORBIDDEN_PASSIVE | 哑节点发起主动类操作（SEC-51，写审计） |
| 2003 | FORBIDDEN | 无权限（非所有者等） |
| 2004 | REGISTRATION_CLOSED | 注册开关已关 |
| 3001 | GROUP_NOT_FOUND / INVITE_INVALID | 邀请码无效或已撤销 |
| 3002 | GROUP_NEED_APPROVAL | 申请已提交待审批 |
| 4001 | TARGET_NOT_AUTHORIZED | 映射目标不在授权范围（L2 失败） |
| 4002 | TARGET_ADDR_NOT_ALLOWED | 目标地址不在对端开放网段（L3 失败） |
| 4003 | REMOTE_CODE_INVALID | 远程码不存在/已重置 |
| 4004 | DEVICE_ACTIVE | 同 mac_code 重注册但原设备在线（防伪造 MAC 抢注，OQ-14；需管理员解绑后重试） |
| 4005 | TARGET_OFFLINE | 打洞目标设备离线/邀请不可达（OQ-18；发起方映射立即 failed，不等超时） |
| 4006 | REGISTRATION_DENIED | 设备注册被拒（**三重闸门统一应答**：开关关/单 IP 频控/总量超限，对外不区分拒因防探测枚举——拒因维度仅入审计 register_deny[switch\|rate\|quota]；07 §5.1，v1.67/M5/SEC-61。注：2004 保持「账号注册开关」专属语义不复用） |
| 5001 | PUNCH_FAILED | 打洞失败（含细分 reason） |
| 5002 | RELAY_DISABLED | 服务端中继关闭 |
| 5003 | SERVER_UNREACHABLE | 控制通道断开 |
| 5004 | VERSION_NOT_SUPPORTED | 协议不兼容（触发升级引导）。v1.67/M5 复用：登录族明文拒绝——0x20/0x21/0x23 passwordBox 缺失或明文字段非空、0x10 携带 password 字段 → 5004（httpLikeMsg=`password_ecies_required`），OQ-21① 硬切无明文双轨，07 §10.3 |
| 5005 | TIME_SKEW | 时间戳超 ±120s 窗口（OQ-12；附 serverTs，客户端重算 offset 后重试一次） |

## 6. API 演进约定

- 破坏性变更递增 `/api/v2` 前缀；v1 仅加法演进；
- WebSocket 事件新增字段允许（前端忽略未知字段）；
- TS 类型由 `P2P.Core` DTO 生成脚本导出（06 §5），保证前后端一致。

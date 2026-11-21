# 13 · Android 客户端设计（端口映射模式 · 无卡宿主）

> 版本：v1.0　创建日期：2026-11-21　状态：**已落地**（决策点①②③用户对话定向 2026-11-21；TD-31~35 随施工转正。M8-01~06/08 编码与测试完成（2026-11-21）：五套件回归全绿、Android 工程 Release 0 警 0 错、签名 APK 入 SHA256SUMS、前端移动适配+375px 走查通过；**M8-07 真机验收 A-19~A-22 待用户配合 adb 装机**（R1 运行时验证随之一并完成）。施工修正三处已回注：§2.4 R2 实际解法、§4.3 DeviceApi 不改写、§4.4 注入优先）
> 依据：用户需求（2026-11-21，"希望在手机上运行客户端的功能，在手机上进行内网穿透"）+ 可行性对话三轮定向（AskUserQuestion 答复：Android / 端口映射够用 / M8 内加移动适配）
> 定位：Android 客户端宿主形态与"无卡端口映射模式"的施工图纸。核心文档修订（01/06/08/技术设计 01/06/08/开发规范 03/打包说明）随立项同批注记，实现随 M8-01~08 落地（任务清单 M8）。
> 关联：技术设计 01 §2/§3.2（宿主形态）、05 §1（INicManager）、06 §6（构建与嵌入）、08 §2/§3（流水线/交付物）；PRD NET-79、FR-C-814~816、NFR-64/65、OQ-23、M8 里程碑行

---

## 0. 摘要与决策记录

**一句话**：新增第四种客户端宿主形态——**Android App**（纯 .NET Android 壳：前台服务承载既有 `ClientRuntime` + WebView 装载既有本地 Web 管理页），并以**无卡端口映射模式**（映射监听绑 `127.0.0.1`，不建虚拟网卡、零 VPN 权限）把客户端全部能力（注册/打洞/映射/中继兜底/管理页）带到手机：手机上任意 App（RD Client、浏览器、SSH 工具）连接 `127.0.0.1:本地端口` 直达对端内网服务。

**决策点**（明细见 §2；①②③经用户对话定向，④⑤为设计定案提案）：

| # | 决策点 | 结论 | 章节 |
|---|---|---|---|
| ① | 平台与范围 | **Android 先行**；iOS 不做（App 沙箱 localhost 不互通 + NetworkExtension 需付费开发者账号与 entitlement，门槛完全不同量级） | §2.1 |
| ② | 使用形态 | **端口映射模式**——映射监听绑 `127.0.0.1`，本机任意 App 可连；不做透明接入（100.64.0.2 直达/VpnService 属后续增量档位） | §2.2 |
| ③ | 界面方案 | **WebView 复用现有 SPA**（Kestrel 同源装载，零前端 API 层改动）＋ **M8 内前端移动适配**（小屏断点/表格卡片化/侧边栏抽屉化，PC 窄窗口同时受益）；不做原生 UI | §2.3 |
| ④ | 宿主技术 | **纯 .NET Android 工程（net10.0-android），不用 MAUI**；**不进 AI-P2P.sln**——脚本守卫式独立构建（免所有构建机装 workload） | §2.4 |
| ⑤ | 无卡模式与指纹 | `ClientRuntimeOptions.LoopbackMappingMode` 显式开关 + 产品级 `NoopNicManager`（state.VirtualIp 不篡改）；设备指纹注入 **ANDROID_ID**（防全设备同 MAC 顶号） | §2.5/§4 |

**边界总述**：本设计为**客户端宿主层与装配层**改动——控制协议（PCP/PTP）、打洞、KCP 承载、中继、加密体系、服务端**全部零变更**；P2P.Core 零改动直接被 net10.0-android 引用；P2P.Client 加三个注入缝（LoopbackMappingMode / MachineFingerprintOverride / SpaRootOverride，§4/§5）；Web 前端加移动适配（§7）。同一账号/分组内 Android 设备与桌面设备互为对端，天然互通（设备对隧道与平台无关）。

**PRD 回流**（2026-11-21，v0.18）：NET-79（06 §7 能力级约束——Android 无卡模式网络口径）、FR-C-814/815/816（04 §4.11 移动端客户端）、NFR-64/65（08 §2/§3——Android 兼容与 APK 交付）、M8 里程碑行（09 §1，出口=本设计 §10 测试口径+真机验收 A-19~A-22）、OQ-23 决议记录（09 §3）；任务清单 M8 随立项同批编制（文档先行，编码未开工）。

---

## 1. 背景与可行性结论

### 1.1 现状（既有定案，引用不改写）

- 客户端宿主形态三种：Windows 服务 / 控制台（`--console`）/ systemd（`ClientHostService` 包 `ClientRuntime` 挂 Generic Host，01 §3.2）；PRD 01 §6.2 将"移动端（Android/iOS）"列为首版不做。
- 虚拟网卡：Windows Wintun / Linux TUN（05 §1），`NicManagerFactory` 在非 Win/Linux 平台抛 `PlatformNotSupportedException`；`ClientRuntime.CreateNic()` 同语义（启动早期无条件执行，注册前也执行）。
- 数据面：每条启用映射一个 `TcpListener.Bind(virtualIp:localPort)`（TCP）/ `UdpClient.Bind(virtualIp:port)`（UDP，M2-20）——`MappingEngine._virtualIP` 全部引用仅四处（声明/构造/UpdateVirtualIp/两处 Bind），**不进日志、不进上报、不参与 channel 路由**；引擎从 accept 到的 socket 读数据，从不经 TUN fd。虚拟 IP 的唯一作用是"该地址本机可绑"（服务端统一下发常量 `100.64.0.2`，OQ-13）。

### 1.2 可行性结论（2026-11-21 全仓探查）

| 层 | 结论 |
|---|---|
| P2P.Core | **零 OS 特定代码**（无 P/Invoke / OperatingSystem 分支 / 注册表 / 控制台 API），唯一 NuGet 依赖 MessagePack（纯托管）——net10.0-android 直接引用，**无移植工作** |
| 数据面（MappingEngine/TunnelSession/Puncher） | 全部普通 IPv4 UDP/TCP socket，无 Raw socket / IP_PKTINFO；`SIO_UDP_CONNRESET` 已 catch 平台异常（Puncher）；`SO_REUSEPORT` 走 `IsLinux()` 分支（Android 内核 ≥3.9 支持且 TCP 打洞必需，`IsLinux()` 在 Android 返回 true，行为正确）；**数据面无"包从虚拟网卡进来"的假设** |
| ClientRuntime | 手工组合根（不依赖 DI/Generic Host），`ClientRuntimeOptions` 已有 `BaseDir`/`NicOverride` 等注入缝；全部后台循环为 PeriodicTimer 托管任务，无控制台/服务依赖——**清掉 CreateNic 崩点后可原样跑在 Android 前台服务里** |
| Web 前端 | 简易版默认入口 `/simple` 顶部 tab 布局；viewport meta 已有、hash 路由、生产 API baseURL 留空同源（api.ts）——WebView 装载零障碍；全仓零 `@media` 断点（V-2 评估欠账，本里程碑 M8-04 补） |
| 测试先例 | 集成测试已用 `VirtualIp="127.0.0.1"` + `StubNicManager` 整跑 ClientRuntime（ClientRuntimeIntegrationTests，M1-35 口径"监听可绑定，隔离语义等价"）——**无卡模式有测试级先例，直接产品化** |

### 1.3 障碍清单（实施处置见 §4/§5，按启动序）

| # | 位置 | 障碍 | 处置 |
|---|---|---|---|
| 1 | `ClientRuntime.CreateNic()`（Hosting/ClientRuntime.cs） | 非 Win/Linux 抛 PlatformNotSupportedException——Android 上**连未注册的向导模式都进不去** | 收敛为调 `NicManagerFactory`；工厂加 IsAndroid 分支 → NoopNicManager（§4.2） |
| 2 | `MacCodeProvider`（Registration/） | Android 6+ 系统 MAC 统一改写 `02:00:00:00:00:00`——可通过 `IsValidMac` 非全零校验且**所有设备同值**，覆盖式恢复（OQ-14）会互相顶号；机器指纹源（注册表/`/etc/machine-id`）Android 均不存在 | `MachineFingerprintOverride` 注入缝 + ANDROID_ID（§4.4） |
| 3 | `ClientPaths.DefaultBaseDir` | 非 Windows 硬编码 `/etc/p2p-client`（Android 只读） | 壳注入 `BaseDir`＝应用私有目录（既有缝，零代码改动；DefaultBaseDir 在 Android 路径不触达） |
| 4 | SPA 托管（ClientRuntime Web 装配） | staticwebassets 清单按 `AppContext.BaseDirectory` 探测，Android 打包布局下不产出 → SPA 静默不托管 | `SpaRootOverride` 物理目录缝（§5.1） |
| 5 | 注册路径 `nic.EnsureAsync(virtualIp)` | 无 try/catch（桌面依赖降级告警容错在调用侧） | NoopNicManager.EnsureAsync 恒成功（§4.2），无需改注册链 |
| 6 | Os 上报（ClientRegistrationService） | `IsWindows ? "windows" : "linux"` 二值——Android 上报错误值 | 加 `"android"` 分支（协议 string、服务端无枚举校验，§4.5） |
| 7 | Android 平台面 | WebView 明文 HTTP（API 28+ 默认禁 cleartext）；后台进程被杀；通知权限（API 33+） | networkSecurityConfig 放行 loopback + 前台服务 + POST_NOTIFICATIONS（§6） |

**不用改的**：DPAPI（`SecretProtector` 有 IsWindows 守卫，Android 走 PlainSecretProtector+0600）；`AddWindowsService`/`AddSystemd`（自探测空操作，Android 壳不经 Program.cs）；`AtomicFile`/`WebToken` Unix 分支；全部后台循环；WebSocket/StatusHub；KCP/打洞 socket 层。

---

## 2. 决策记录

### 2.1 ① 平台与范围：Android 先行（2026-11-21 用户定向）

Android 上普通 App 之间可以互连 `127.0.0.1`（共享网络命名空间，adb reverse/本地代理生态即此原理）——这是端口映射模式在 Android 成立的根基。iOS App 沙箱隔离 localhost，跨 App 访问必须走 NetworkExtension（付费开发者账号 + entitlement + VPN 配置描述文件），工程量与发布门槛完全不同量级。**iOS 维持非目标**（PRD 01 §6.2 保留 iOS 行，仅 Android 移出），待 Android 验证价值后另行立项评估。

### 2.2 ② 使用形态：端口映射模式（2026-11-21 用户定向，"端口映射够用"）

| 形态 | 说明 | 工程量 | 本期 |
|---|---|---|---|
| 端口映射模式 | 映射监听退绑 `127.0.0.1:本地端口`，手机任意 App 显式连该地址 | 小（监听地址缝 + 无卡 NIC） | ✅ |
| 透明接入 | VpnService 建 TUN、100.64.0.2 可直达、任意 App 填虚拟 IP 即用 | 大（VpnService 实现 + 用户 VPN 确认弹窗 + 全流量路由治理） | 非目标（后续增量档位，13 §12 R5） |

覆盖场景核验（README §2）：远程桌面（RD Client 连 127.0.0.1:3389）、内网服务发布访问（浏览器/SSH/SMB 客户端）、跨网络设备互访——全部核心场景成立；服务方角色同样可用（通道从隧道内进来，不需要本机监听，"PC 访问手机上的服务"反向场景顺带覆盖）。

### 2.3 ③ 界面：WebView 复用 SPA + M8 内移动适配（2026-11-21 用户定向）

- **复用路径**：前台服务内 ClientRuntime 照常起 Kestrel（127.0.0.1:7100）→ App 本体是全屏 WebView 加载之。同源相对路径天然成立（前端生产 baseURL 留空）、注册向导/简易版/高级版/诊断页全部现成、PC 与手机同一份代码同一套测试。
- **移动适配**（M8-04，PC 窄窗口同时受益）：小屏断点 `<768px`（`useBreakpoint` composable）；高级版 AdminLayout 侧边栏 → 汉堡按钮 + el-drawer；表格页（Mappings/Devices/SimpleMappings/Logs）小屏切卡片列表（断点 v-if 双渲染，桌面路径 DOM 不变）；el-dialog 宽度 `min(Npx, 94vw)`；SimpleLayout tab 溢出滚动。
- **否决原生 UI / 移动专属页（/m 路由组）**：任何重写都是数周级工程且从此双份 UI 维护；适配一次改造两端受益。

### 2.4 ④ 宿主技术：纯 .NET Android、不进主 sln（设计定案提案 → TD-31）

| 决策 | 内容 |
|---|---|
| 工程形态 | `src/P2P.Client.Android/`，TFM `net10.0-android`（csproj 工程级覆盖根 Directory.Build.props——Tools 工程已有先例）。**不用 MAUI**：只需 Activity + 前台服务 + WebView 三个 Android 概念，MAUI 跨平台抽象是纯负担（iOS 非目标前提下无第二平台可抽象） |
| sln 挂载 | **不进 AI-P2P.sln**：`dotnet build AI-P2P.sln` 是 build.sh 第一步且 CI 同序，进 sln 即强迫所有构建机装数 GB android workload。独立经脚本构建 + workload 探测守卫（无则跳过并明示，沿 ExportTs 目录守卫与 ISCC 缺失跳过先例） |
| 引用关系 | ~~ProjectReference → P2P.Client（Web SDK Exe 产 dll，跨 TFM 可引；FrameworkReference Microsoft.AspNetCore.App 传递）。**风险前置**：M8-03 首步真机 spike 验证 Kestrel on Android + 跨 TFM 引用；若摩擦大，备用方案＝ClientRuntime 抽薄为类库（预估 +1 天）~~ **施工落地（M8-03 定案，R2 实际解法比备用方案更轻——零代码抽库）**：①Android 闭包不可携带 Microsoft.AspNetCore.App 框架引用（无 android RID 运行时包 → NETSDK1082，dotnet/aspnetcore#35077 官方未支持目标）——剪断经 ProjectReference 传递入壳的框架引用不可行（RID 另经 NuGet 资产图索要 net10.0/android-* 目标 → NETSDK1047）；②**改为构建产物 dll 直引**：自举目标（源码陈旧即先 `dotnet build P2P.Client`）+ bin 全量 dll Reference（CopyLocal 副本，链接器零缺引用）；③AspNetCore 运行时程序集由本机 dotnet SDK 自带共享框架目录直供（143 个程序集全路径 Reference，排除 aspnetcorev2_inprocess 本机垫片；版本取最高，内联代码任务发现）。构建级验证：`dotnet build src/P2P.Client.Android -c Release` 0 警 0 错、APK 内 Kestrel 双 ABI AOT 产物齐备 |
| 版本注入 | `src/Directory.Build.props` 的 `$(Version)` 注入 `ApplicationDisplayVersion`（单一事实源不动） |

### 2.5 ⑤ 无卡模式：显式开关 + 不篡改本地真相（设计定案提案 → TD-32/TD-34）

**LoopbackMappingMode（显式开关，默认 false）**：开启后引擎监听绑定地址＝`IPAddress.Loopback`（初值与 `UpdateVirtualIp` 两处生效点），**state.VirtualIp 保持服务端下发的 100.64.0.2 不篡改**——本地真相与服务器记录一致，未来 VpnService 透明接入形态可复用同一 state 零迁移。展示地址归真：引擎暴露"生效绑定地址"，`DeviceApi/SystemApi/WizardApi` 的虚拟 IP 展示改读之（无卡模式＝127.0.0.1，UI 不误导手机用户去连 100.64.0.2）。

**NoopNicManager（产品级，落 P2P.Nic 项目根）**：`EnsureAsync` 返回 `NicHandle("loopback", ip)` 恒成功、`CheckHealth` 恒 Healthy、`Remove/RemoveLeftover` no-op——`NicManagerFactory` 加 `IsAndroid` 分支选用。NicHealthMonitor 装配后 30s 探测恒健康空转（无害）；SubnetConflictDetector 以生效地址推导 /24＝127.0.0.0/24，枚举侧排除 loopback，无误报。

**设备指纹（TD-34）**：`ClientRuntimeOptions.MachineFingerprintOverride`（`Func<string?>`）注入缝 → `ClientRegistrationService` → `MacCodeProvider.FromFingerprint` 派生；Android 壳提供 ANDROID_ID 派生指纹（空值/模拟器旧常量回落平台采集）。**正确性依据**：Android 6+ 对普通 App 的 `NetworkInterface.GetPhysicalAddress()` 统一返回 `02:00:00:00:00:00`——非全零、可通过既有 `IsValidMac` 校验，macCode 将全设备同值 `P2P-020000000000`；OQ-14 覆盖式恢复按同 macCode 判定身份 → **一台手机重装即顶号另一台手机的设备记录**。此为正确性缺陷而非体验问题，故指纹注入为 M8-01 必做项。

**施工修正（M8-01 实测发现）**：原设计"既有 `Generate(machineFingerprint)` 参数缝"不成立——该参数是**回退位序**（枚举到合格网卡即胜出），而 Android 的 02:00:00:00:00:00 恰可通过 IsValidMac 校验，回退位序下注入永不生效、顶号缺陷依旧。落地为新增 `MacCodeProvider.FromFingerprint`：注入取到值即**优先派生**（SHA-256 前 12 位与回退同构），无注入/取值 null 才平台采集——集成测试以"本机存在合法网卡仍派生注入值"锁定该位序。

---

## 3. 总体架构：第四种宿主形态

### 3.1 结构与复用

```
src/P2P.Client.Android/              # 新工程（net10.0-android，不进主 sln）
├─ P2P.Client.Android.csproj         # ProjectReference → P2P.Client；wwwroot 产物以 AndroidAsset 打包
├─ MainActivity.cs                   # 全屏 WebView 壳：加载 http://127.0.0.1:7100/?token=<web-token>
├─ P2pForegroundService.cs           # 前台服务：new ClientRuntime(options) 承载（不经 Program.cs/Generic Host）
├─ SpaAssets.cs                      # 首启将 wwwroot assets 拷至应用数据目录（SpaRootOverride 指向）
├─ AndroidFingerprint.cs             # ANDROID_ID → MachineFingerprintOverride
├─ Resources/                        # AndroidManifest、networkSecurityConfig、图标、通知渠道
└─ Properties/
```

复用四层零分叉：**P2P.Core**（协议/加密/KCP，零改动）、**P2P.Nic**（+NoopNicManager 一文件）、**P2P.Client**（ClientRuntime + LocalWebApi + Web 前端，+三缝）、**web/client-app**（+移动适配）。wwwroot 单一构建产物两头消费（PC＝staticwebassets / APK＝AndroidAsset），无第二份前端。

### 3.2 宿主形态对照

| 宿主形态 | 承载 | BaseDir | NIC | 监听地址 | 界面 |
|---|---|---|---|---|---|
| Windows 服务/控制台 | Generic Host | %ProgramData%\P2PClient | Wintun | 100.64.0.2 | 浏览器 127.0.0.1:7100 |
| systemd | Generic Host | /etc/p2p-client | TUN | 100.64.0.2 | 浏览器 |
| **Android（本设计）** | 前台服务直构 ClientRuntime | 应用私有目录（注入） | **NoopNicManager** | **127.0.0.1（LoopbackMappingMode）** | **App 内 WebView** |

### 3.3 装配（P2pForegroundService 内，伪码）

```csharp
var options = new ClientRuntimeOptions
{
    BaseDir = appDataDir,                       // state/settings/peers/web-token/logs 全落应用私有目录
    LoopbackMappingMode = true,                 // TD-32：映射监听绑 127.0.0.1
    MachineFingerprintOverride = () => AndroidFingerprint.Get(),  // TD-34：ANDROID_ID 派生
    SpaRootOverride = spaDir,                   // TD-35：SPA 物理目录托管（§5.1）
};
_runtime = new ClientRuntime(options);
_runtime.Log += m => fileLogger.AppendLine(m);  // Serilog 不进壳，自写轻量文件日志（LocalWebApi /logs 页可读）
await _runtime.StartAsync(ct);
// WebView 加载 Url = $"http://127.0.0.1:{WebPort}/?token={WebToken}"
```

停机沿 `ClientRuntime.DisposeAsync` 既有优雅序；前台服务 onDestroy 触发之。

---

## 4. P2P.Client/P2P.Nic 无卡模式改造（M8-01，Windows 上可完整测试）

### 4.1 ClientRuntimeOptions 新增两缝（LoopbackMappingMode / MachineFingerprintOverride）

- `bool LoopbackMappingMode { get; init; }`（默认 false，桌面行为零变化）：生效点两处——引擎构造初值（原 VirtualIp 解析失败兜底 Loopback 处）与注册后路径 `UpdateVirtualIp` 调用点（原传 state 值，模式开启时传 `IPAddress.Loopback`）；
- `Func<string?>? MachineFingerprintOverride`（默认 null＝现状）：经 ClientRegistrationService 流至 `MacCodeProvider.Generate(machineFingerprint)`（既有参数缝）。

### 4.2 NoopNicManager + CreateNic 收敛

- 新建 `src/P2P.Nic/NoopNicManager.cs`（模板＝集成测试 StubNicManager，产品级化）：幂等、恒成功、恒健康；
- `NicManagerFactory.Create()` 加 `OperatingSystem.IsAndroid()` 分支 → NoopNicManager；
- `ClientRuntime.CreateNic()` 私有复制逻辑**收敛为调工厂**（消除既有重复；桌面路径行为不变）。

### 4.3 展示地址归真

`MappingEngine` 暴露生效绑定地址（`public IPAddress EffectiveBindIp`）；`Web/SystemApi.cs`、`Web/WizardApi.cs` 两处**本机**虚拟 IP 展示改读之（LocalApiServices.EffectiveVirtualIp 缝，无卡模式装配=引擎现值、桌面 null=state 现状）。桌面模式值＝虚拟 IP（展示不变）；无卡模式值＝127.0.0.1（手机用户看到的"连接地址"即真实可达地址，映射卡/向导预览引导连 127.0.0.1:port——前端连接地址展示源头即 /api/device）。

**施工修正**：`Web/DeviceApi.cs`（/api/devices 列表）**不随无卡模式改写**——列表 virtualIp 为服务端记录的**他设备**远端真相（每设备 100.64.0.2 常量，OQ-13），非本机连接地址；统一改 127.0.0.1 会让全部远端设备呈现"本机可达"的误导。连接地址归真落在本机两端点已覆盖全部用户可见路径（映射卡/向导预览均取 /api/device）。

### 4.4 指纹流与 ANDROID_ID

Android 壳 `AndroidFingerprint`：`Settings.Secure.GetString(ContentResolver, Secure.AndroidId)`（64 位 hex，恢复出厂重置、签名变更会换值——粒度对"设备识别码"语义足够：重装不换、卸载重装不换*，见 §12 R4）→ SHA-256 前 12 位 hex 充当"MAC"（与 MacCodeProvider 指纹回退同构，FingerprintHex 既有纯函数复用）。*注：Android 8.0+ ANDROID_ID 按"签名+用户"作用域，卸载重装保持稳定，恰好优于机器指纹漂移场景。

### 4.5 Os 上报

`RegistrationOptions.Os` 加 `OperatingSystem.IsAndroid() ? "android"` 分支（值域 windows/linux/android；协议 string、服务端无枚举校验、管理页 os 列原样展示——零服务端改动）。

---

## 5. Web 托管缝与界面装载（M8-02）

### 5.1 SpaRootOverride（物理目录托管）

`ClientRuntimeOptions.SpaRootOverride { get; init; }`（string?，默认 null）：

- null → 现状 staticwebassets 路径（`p2p-client.staticwebassets.endpoints.json` 探测 + MapStaticAssets），桌面行为零变化；
- 非 null → `PhysicalFileProvider(dir)` + `UseStaticFiles` + `MapFallbackToFile("index.html")`（无预压缩——设备本地回环加载，无意义）。Android 壳首启把 APK assets 内 wwwroot 拷至 `<appData>/spa/<version>/`（版本戳防半新半旧），SpaRootOverride 指向之。

### 5.2 Web 宿主启动韧性（降级不拖垮数据面）

Kestrel on Android 属官方未支持目标（社区实测可跑）。Web 宿主构建/启动包 try/catch：失败记 ERROR 日志 + WS/API/页面不可用，**隧道、打洞、映射监听照常运行**（127.0.0.1:port 仍可被其他 App 连接使用）。此降级同时惠及桌面（端口被占等场景不再拒启）——语义变更在 08 §5.2 注记。

### 5.3 WebView 装载与明文/Token

- networkSecurityConfig：`cleartextTrafficPermitted` 仅对 `127.0.0.1` 放行（API 28+ 默认全局禁明文，本产品本地 Web 全 HTTP——D11 口径在 Android 的等价落地）；
- token 送达：壳读 `<BaseDir>/web-token` 文件（M5-12 既有机制，文件在应用私有目录天然 ACL）拼 `?token=` 进首载 URL，SPA 既有 sessionStorage 转存链路接管；
- WebSocket（/ws/status）同源明文 `ws://127.0.0.1:7100`，networkSecurityConfig 一并覆盖。

---

## 6. 前台服务与生命周期（M8-03）

| 事项 | 口径 |
|---|---|
| 服务类型 | `specialUse` 前台服务（Android 14+ 须声明 foregroundServiceType；`dataSync` 正被收紧、`connectedDevice` 语义不符）；`FOREGROUND_SERVICE`+`FOREGROUND_SERVICE_SPECIAL_USE`+`INTERNET`+`POST_NOTIFICATIONS`（API 33+）权限 |
| 常驻通知 | 通知渠道 + 常驻条（在线状态/隧道计数，点击回 App）——前台服务法定要求 |
| 保活边界 | 前台服务=系统不杀承诺的常规上限；Doze/厂商激进省电下仍可能被限。**不做**厂商白名单引导页（第一期），用户手册注明"电池优化白名单"建议（manual 增补随 M8-08） |
| 开机自启 | `RECEIVE_BOOT_COMPLETED` 接收器 → 启动前台服务（FR-C-901 开机自启语义的 Android 等价；厂商 ROM 行为差异如实标注，不承诺全部生效） |
| 停机 | 服务 onDestroy → `ClientRuntime.DisposeAsync`（既有优雅停机序：上报终刷→监听释放→控制通道关闭） |

蜂窝网络预期管理：运营商 CGNAT 多为对称型 NAT，打洞成功率天然偏低——按既有逻辑回退内置中继（功能不断、速度打折）；Wi-Fi 下与 PC 体验一致。此为网络现实非实现缺陷，用户手册/使用说明注明（A-22 验收含中继回退路径）。

---

## 7. 前端移动适配（M8-04，web/client-app）

| 项 | 口径 |
|---|---|
| 断点基础设施 | `useBreakpoint` composable（matchMedia `<768px`，SSR 无关、vue-tsc 全程零错）；统一断点常量入 ui-shared |
| 高级版布局 | AdminLayout 侧边栏小屏 → 汉堡按钮 + `el-drawer`（临时抽屉导航，路由不变） |
| 表格卡片化 | Mappings/Devices/SimpleMappings/Logs：小屏 `v-if` 切卡片列表（卡片=行数据纵排+关键操作保留），**桌面路径 DOM 不变**（大屏分支原样渲染 el-table） |
| 弹窗与表单 | el-dialog 宽度统一 `min(Npx, 94vw)` 模式；向导/映射表单小屏纵排（el-form label-position top） |
| 简易版微调 | SimpleLayout tab 溢出横向滚动；960px 居中容器窄屏自然全宽（max-width 语义本就如此） |
| 主题 | 简易版暗色皮肤（simple-tech）在 WebView 渲染核验（§10.3 窄屏走查覆盖） |
| 非目标 | 移动专属路由组/独立移动 App 壳、平板双栏优化、全站深色模式（评估暂缓项不动） |

验证：dev server（MSW mock 模式）+ 375px 视窗逐页走查（简易版三页/高级版十页/向导/弹窗）；vitest 含 useBreakpoint 单测与卡片/表格双渲染断言。

---

## 8. 构建与打包（M8-05）

- `build/build.sh`：新增 android 步骤——`dotnet workload list` 探测 android workload，在则 `dotnet build src/P2P.Client.Android -c Release`，无则跳过并明示（沿 ExportTs/ISCC 守卫先例）；
- `build/package.sh`：新增 `run_android()`（web_build client-app → dotnet publish android 工程 → APK 落 `out/client-android/`）+ case `android` 目标 + `write_sums` glob 增 `out/P2PSocket-*.apk`；产物名 `P2PSocket-Client-Android-v<版本>.apk`（P2PSocket-* 命名口径，v0.12）；
- 签名：`deploy/client-android/` 存 keystore（个人自部署口径，不入 .gitignore 白名单例外——密钥文件入库的取舍在 M8-05 实施时按仓库纪律定夺：自签 keystore 入库（个人用途、仓库已私有前提）或 .gitignore+手册指引）；`keytool` 生成随 M8-05 一次性落地；
- 版本：`$(Version)` → `ApplicationDisplayVersion`（08 §1 单一事实源延伸）；
- CI（.github/workflows/ci.yml）：android 构建步骤同守卫口径（无 workload 跳过），**不新增强制门禁**（真机验收 M8-07 人工执行）。

---

## 9. 兼容与共存

| 场景 | 行为 |
|---|---|
| Android 设备 ↔ 桌面设备互为对端 | 天然互通：设备对隧道、打洞信令、KCP/中继承载与平台无关；Android 作为服务方（被动响应打洞/通道接入）同样成立 |
| 同一 Android 设备卸载重装 | ANDROID_ID 稳定（Android 8.0+ 签名作用域）→ 同指纹覆盖式恢复（OQ-14）→ 设备身份延续 |
| 127.0.0.1 端口冲突域 | 无卡模式映射监听并入本机回环冲突域：与手机上既有 localhost 监听（含本客户端 Web 端口 7100）同域——`AddressAlreadyInUse` 走既有 listen_failed 重试集（M2-24）；映射本地端口选择由用户避让（向导不预占 7100） |
| 旧服务端 + Android 客户端 | 零约束：注册/信令/上报全走既有协议，Os="android" 为新增 string 值（服务端无枚举校验） |
| 桌面客户端（LoopbackMappingMode=false） | 行为与现状完全一致（开关默认关、缝默认 null） |
| 多 Android 设备同账号 | 各自独立 deviceId（ANDROID_ID 指纹互异，§4.4 顶号缺陷已消除） |

---

## 10. 测试口径（09 §2/§3 惯例）

### 10.1 单元测试（P2P.Client.Tests / P2P.Nic.Tests）

| 文件 | 用例 |
|---|---|
| `Nic/NoopNicManagerTests`（新） | EnsureAsync 恒成功返回 loopback 句柄且幂等；CheckHealth 恒 Healthy；Remove/RemoveLeftover no-op 不抛 |
| `Hosting/ClientRuntimeOptions` 相关（扩展） | LoopbackMappingMode=true 时引擎初值与 UpdateVirtualIp 均落 Loopback；false 时按 state.VirtualIp（现状回归）；MachineFingerprintOverride 注入后注册 macCode 取指纹派生值 |
| `Mapping/MappingEngineTests`（扩展） | EffectiveBindIp 暴露口径（随 UpdateVirtualIp 联动） |

### 10.2 集成测试（P2P.IntegrationTests，Windows 全绿可跑）

| 用例 | 断言 |
|---|---|
| 无卡模式整跑（新） | LoopbackMappingMode=true + NoopNicManager + BaseDir=临时目录 整跑 ClientRuntime → 注册（fake 服务端）→ 建映射 → 监听落 127.0.0.1:port（本机真实可连）→ 经隧道 splice 数据往返通 |
| 展示归真（新） | 无卡模式下 /api/system/state、/api/wizard、/api/device 的虚拟 IP 字段值＝127.0.0.1；桌面模式＝state 值（回归） |
| SpaRootOverride 托管（新） | 注入物理目录含 index.html → GET / 返回之、fallback 路由生效；null=现状（回归） |
| Web 宿主降级（新） | 端口占用场景启动 → 运行时不抛、隧道照常、API 不可达如实呈现 |

### 10.3 前端（client-app vitest + 窄屏走查）

- useBreakpoint 单测（matchMedia mock 两态）；表格卡片双渲染断言（大屏 el-table / 小屏卡片列表）；抽屉导航开合；dialog 宽度样式断言；
- 人工走查：375px 视窗简易版三页/高级版/向导/弹窗逐页截图核对（MSW mock）。

### 10.4 真机验收（A-19~A-22，M8-07，需用户配合 adb 装机）

| # | 场景 | 预期 |
|---|---|---|
| A-19 | Android 注册与向导 | App 安装启动 → WebView 向导连既有服务端注册成功 → 设备页可见（os=android）→ 重启 App 状态恢复 |
| A-20 | 手机访问远端内网 | 建映射到 PC 对端（如 RDP 3389）→ 手机 RD Client 连 `127.0.0.1:3389` 直达；浏览器连 HTTP 服务同验 |
| A-21 | 打洞与承载 | Wi-Fi 下打洞直连（隧道类型列可见）；蜂窝下回退中继仍可访问（A-22 前置） |
| A-22 | 后台保活与中继回退 | 退到后台/锁屏 ≥10min 会话保持；蜂窝网络映射自动经中继可用 |

### 10.5 回归门禁

五套件全绿（Core/Nic/Client/Server/Integration 串行）+ `dotnet build AI-P2P.sln` 0 警 0 错（android 工程不在 sln，桌面链零影响）；`dotnet build src/P2P.Client.Android -c Release` 0 警 0 错（有 workload 环境）；前端 vitest 全绿 + vue-tsc 零错；`sh build/package.sh android` 产出签名 APK + sha256 入 SHA256SUMS。

**落地态（2026-11-21，M8-06/05 实测）**：单测——NoopNicManager 3 例 + Loopback 生效点①两态 + MacCode 注入优先/回落 3 例 + EffectiveBindIp 联动；集成——M8 四例（无卡整跑 splice 通/注册链指纹+双端归真/SpaRootOverride+null 回归/Web 端口占用降级隧道照常）全绿；前端——breakpoint.test.ts 8 例（useBreakpoint 两态/卡片双渲染/抽屉开合/弹窗 min() 宽度）+ 既有全量 82 例 + ui-shared；vue-tsc 零错；`sh build/package.sh android` 全链产出 `P2PSocket-Client-Android-v1.0.0.apk`（27.6MB，Kestrel 双 ABI AOT）+ `.sha256` 入 SHA256SUMS；375px 走查（MSW mock+DOM 溢出检测）：10 页无文档级横向滚动、卡片/抽屉/表单纵排全生效、两处实测溢出（Settings addr-row/简易版仓库链接）当日修复复验。

**真机验收落地态（2026-11-21，M8-07 收官）**：Xiaomi 2211133C/Android 14（API 34）四场景全过（A-19~A-22，详见 `docs/验收记录/2026-11-21-M8-07-Android真机验收/`），**R1 运行时验证关闭**（Kestrel 实跑承载 Web API+SPA 全程）。验收发现并修复两真机缺陷：①清单合并双 LAUNCHER（手写清单相对名与特性默认 crc* 包名不匹配→双图标+死条目 ClassNotFoundException——三特性 `Name="com.p2psocket.client.*"`+清单全名对齐修复；合并按字符串精确名匹配是施工要点）；②`FromPrivateKey` 仅 D 导入在 Android（BouncyCastle 系）抛 CryptographicException（Windows CNG 才支持仅 D 推导 Q）→ 新增 `P2P.Core/Crypto/P256.cs` 托管标量乘补 Q=d×G 全参数导入（NIST 向量测锁定）——**通用平台兼容修复**，非 Android 专属分支。厂商差异如实记录：MIUI 旁加载应用 BOOT_COMPLETED 自启需手动开启「自启动」权限（未开启时重启后手动启动一次即全量恢复）。

---

## 11. 任务拆分（任务清单 M8 承接，编号已定）

| # | 任务 | 面 |
|---|---|---|
| M8-01 | 无卡模式（LoopbackMappingMode/MachineFingerprintOverride 两缝、NoopNicManager、CreateNic 收敛、展示归真、Os=android）+ 单测 | Nic/Client/测试 |
| M8-02 | Web 托管缝（SpaRootOverride）+ Web 宿主降级 | Client/Web |
| M8-03 | Android 壳工程（首步 spike：真机验 Kestrel+WebView；MainActivity/前台服务/SpaAssets/指纹/manifest） | Android |
| M8-04 | 前端移动适配（useBreakpoint/抽屉/卡片化/弹窗） | web/client-app |
| M8-05 | 构建链（build.sh 守卫/package.sh android 目标/签名/SHA256SUMS/版本注入） | build/deploy |
| M8-06 | 测试收口（10.1/10.2/10.3 全量 + 五套件回归） | tests/web |
| M8-07 | 真机验收 A-19~A-22（需用户配合） | 实机 |
| M8-08 | 文档收官（§12 修订点落地态回填 + 手册/排障增补） | docs |

依赖：M8-01 → M8-02 → M8-03 →（M8-05、M8-07）；M8-04 独立可并行（仅依赖现有 SPA）；M8-06 依赖 01/02/04；M8-08 收官依赖全部。

---

## 12. 核心文档修订点清单（立项批次已注记"随 M8-0x 落地"；**收官批次（2026-11-21）落地态回填**——PRD 族随 v0.18 回流即为终态，其余各文档收官行已落）

| 文档 | 修订点 | 落地态 |
|---|---|---|
| PRD 01 §6.1/§6.2/§5 | Android 入范围行、iOS 保留非目标、竞品对照目标平台 | ✅ v0.18（立项批次） |
| PRD 02 §2.2/§5 | 客户端组件宿主形态行、客户端平台约束行 | ✅ v0.18（立项批次） |
| PRD 04 §4.11 | FR-C-814/815/816 | ✅ v0.18（立项批次） |
| PRD 06 §7 | NET-79 | ✅ v0.18（立项批次） |
| PRD 08 §2/§3 | NFR-64/65 | ✅ v0.18（立项批次） |
| PRD 09 §1/§2/§3 | M8 行、A-19~A-22、OQ-23 | ✅ v0.18（立项批次；A-19~A-22 待真机执行） |
| 技术设计 01 §2/§3.2 | 解决方案结构加 P2P.Client.Android（不进 sln 注记）、进程模型第四宿主形态 | ✅ 立项注记即终态（工程不进 sln 施工一致） |
| 技术设计 06 §2/§6 | 移动适配口径、WebView 装载与 AndroidAsset 打包 | ✅ 移动适配实现随 M8-04（useBreakpoint/抽屉/卡片/min() 宽度入 ui-shared 与 client-app） |
| 技术设计 08 §2/§3 | 流水线 android 守卫步骤、APK 交付物 | ✅ build.sh android 步骤+ci.yml 守卫步骤+package.sh android 目标（M8-05） |
| 开发规范 03 §2 | `src/P2P.Client.Android/`、`deploy/client-android/` 目录行 | ✅ 目录已建（壳五文件+manifest+networkSecurityConfig；keystore+p2pclient.keystore） |
| docs/deploy 打包说明 | android 目标行 | ✅ 目标行落地态随 v1.4 收官行回填 |
| 根 README §1.1/§1.3 | 产品组成、仓库结构 | ✅ §1.1 状态行收官更新 |
| manual 用户手册/排障 | Android 安装使用/保活指引/蜂窝预期 | ✅ 手册 §1.6 Android 客户端+排障 §24~§26（M8-08） |

---

## 13. 风险与开放问题

| # | 风险/开放问题 | 处置 |
|---|---|---|
| R1 | **Kestrel on Android 属官方未支持目标**（社区实测可跑，.NET on Android 未列 ASP.NET Core 支持矩阵） | M8-03 首步真机 spike 前置验证；M8-02 降级保险（Web 挂≠隧道挂）；若 spike 失败且无解 → 退路：界面改 appassets 域 + fetch 绝对地址（前端 VITE_API_BASE 机制扩展运行时配置），API 仍需 Kestrel——彻底无 Kestrel 方案不在本期（升级为 OQ）。**当前态**：~~运行时验证待 M8-07 真机~~ **已关闭（2026-11-21 M8-07 实证）**——真机全场景经 Kestrel 承载 Web API+SPA（向导注册/映射 CRUD/诊断/日志导出全程 HTTP 200），降级保险未触发 |
| R2 | Web SDK Exe 跨 TFM 引用摩擦（staticwebassets/FrameworkReference 传递边角） | ~~同 M8-03 spike 验证；备用＝ClientRuntime 抽薄类库（+1 天）~~ **已解（M8-03 落地，§2.4 施工定案）**：NETSDK1150/1082/1047 三重摩擦均以工程配置消解（校验豁免+剪断框架引用+共享框架直供/dll 直引），零代码抽库 |
| R3 | 蜂窝 CGNAT 打洞成功率天然偏低 | 中继兜底既有；A-22 验收含中继路径；手册注明预期。**实证（M8-07）**：蜂窝 punch_timeout→中继回退可用（relayFallback 设备级 opt-in 开启后），数据完整性五场景同哈希 |
| R4 | ANDROID_ID 作用域变化（恢复出厂/系统多用户/刷机换值） | 走既有覆盖式恢复/新注册语义（OQ-14 框架内），无新增机制；手册注明"恢复出厂后视为新设备，管理员可解绑旧记录" |
| R5 | 透明接入（100.64.0.2 直达/VpnService）不做 | 明示非目标；无卡模式覆盖核心场景；后续增量立项（届时 INicManager 加 VpnService 实现，state.VirtualIp 已保留可复用） |
| R6 | 厂商 ROM 后台激进而被杀（Doze/白名单外） | 前台服务+BOOT_COMPLETED 兜底；手册引导电池白名单；不承诺全部 ROM 生效（如实标注） |
| R7 | 127.0.0.1 端口冲突域并入（A-4 桌面语义的镜像场景在手机同样存在） | 既有 listen_failed 重试集兜底；向导/简易版连接地址展示 127.0.0.1:port 让冲突可归因；手册提示避开常用端口 |

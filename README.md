# AI-P2P · P2P 内网穿透产品

> 单仓库（AI-P2P.sln）· 控制面中心化 + 数据面 P2P · .NET 10 + Vue 3
> **当前状态**：编码进行中——**M1 核心链路 ✅ 收官**（2026-09-12）；**M2 打洞完整与授权闭环 35/38**（余 M2-01/02/35 实机环境依赖项）；**M3 管理面与可观测 🔨 进行中**（0/18，2026-09-30 起）。进度详见 [任务清单](docs/任务清单/README.md)。

## 1. 这是什么

一个自部署的内网穿透产品：把一台设备（服务方）上的内网服务，经 **P2P 打洞直连隧道** 暴露给另一台设备（访问方）。访问方在本机连接 `虚拟IP:本地端口`，流量经端到端加密隧道直达对端——无需公网中转（打洞失败时可回退内置中继）。

```
访问方设备 A                     服务端（公网部署）                  服务方设备 B
┌──────────────┐                ┌───────────────┐                ┌──────────────┐
│ P2P.Client   │◀──控制 7000/TCP▶│ P2P.Server    │◀──控制 7000/TCP─│ P2P.Client   │
│ 虚拟IP 100.64.0.2│  STUN 3478  │ SQLite·注册/授权│  STUN 3478     │ 虚拟IP 100.64.0.2│
│ 本地Web 127.0.0.1:7100│        │ Web 仅本机:7500 │               │ 本地Web 127.0.0.1:7100│
└──────┬───────┘                └───────────────┘                └──────┬───────┘
       │        UDP 打洞 → PTP 加密隧道（channel 复用；失败→中继 7010）       │
       └──────────────────────────────────────────────────────────────────┘
本地应用连 100.64.0.2:8080 ──▶ 映射引擎 ──▶ 隧道 channel ──▶ 对端 127.0.0.1:22
```

## 2. 产品模型要点

| # | 要点 | 说明 |
|---|---|---|
| 1 | **控制面集中、数据面 P2P** | 服务端只做注册/授权/信令/STUN/中继兜底；业务流量端到端，不经过服务端 |
| 2 | **虚拟网卡仅作本机监听地址** | 所有设备统一下发同一固定地址（100.64.0.2），不参与跨机寻址、不做三层转发（OQ-13） |
| 3 | **流/数据报复用隧道** | 隧道是设备对级资源，全部映射与连接以 channel 复用同一条隧道，不按连接重复打洞（02 §4.5） |
| 4 | **手动端口映射 + 设备级中继回退** | 用户显式建映射；回退开关配置在目标设备层级（D3/OQ-10，peers.json） |
| 5 | **全链路不依赖 TLS** | 控制通道 HMAC-SHA256 防重放 + 时钟对齐；隧道 ECDH(P-256)+HKDF+AES-256-GCM 端到端加密；凭据 ECIES 下发（TD-04） |

## 3. 技术栈

| 层 | 选型 |
|---|---|
| 运行时 | **.NET 10**（仅 net10.0；.NET 8/9 于 2026-11-10 停止支持，禁用） |
| 服务端/客户端 | C# · ASP.NET Core Minimal API + Kestrel · Generic Host · System.IO.Pipelines |
| 前端 | Vue 3 + TypeScript + Element Plus + Vite（pnpm workspace，双端 SPA 嵌入二进制） |
| 存储 | 服务端 SQLite + EF Core（WAL）；客户端本地 JSON（state/settings/peers） |
| 协议编码 | MessagePack（MessagePack-CSharp） |
| 加密 | .NET 内置：ECDH(P-256) / HKDF-SHA256 / AES-256-GCM / HMAC-SHA256 / ECIES（零第三方加密依赖） |
| 虚拟网卡 | Windows Wintun（P/Invoke）/ Linux TUN（ioctl + rtnetlink） |
| 日志/测试 | Serilog · xUnit + TestHost + NatSimulator（NAT 模拟） |

## 4. 仓库结构

```
AI_P2P_Project/
├─ README.md                   # 本文件
├─ AI-P2P.sln                  # .NET 解决方案
├─ docs/                       # 全部文档（见 §5 导航）
│  ├─ prd/                     #   产品需求文档 v0.5
│  ├─ 技术设计文档/             #   施工图纸 v1.7（01~10 + README/TD 台账）
│  ├─ 开发规范/                 #   编码约束层 v1.0（AI 规则/模块边界/目录规范）
│  └─ 任务清单/                 #   实现层执行台账（M1 ✅ / M2 · M3 🔨）
├─ src/                        # C# 后端（01 §2 目录规范）
│  ├─ P2P.Core/                #   协议 / 隧道 / 打洞 / 映射引擎（平台无关核心）
│  ├─ P2P.Nic/                 #   虚拟网卡适配（Windows Wintun / Linux TUN）
│  ├─ P2P.Server/              #   服务端：控制面 + STUN + 中继 + Web 后台宿主
│  ├─ P2P.Client/              #   客户端宿主：系统服务 + 本地 Web（127.0.0.1:7100）
│  └─ Tools/ExportTs/          #   API 类型导出器（反射生成 ui-shared 类型）
├─ web/                        # pnpm workspace 前端（构建产物嵌入宿主 wwwroot）
│  ├─ ui-shared/               #   共享组件 + API 类型（api.d.ts / api-paths.ts）
│  ├─ client-app/              #   客户端本地 SPA
│  └─ server-app/              #   服务端后台 SPA（M3 建设中）
├─ tests/                      # xUnit：Core / Server / Client / Nic 四项目测试 + 集成测试
├─ build/build.sh              # 本地构建全链（build → export-ts → pnpm build → test）
└─ deploy/                     # 部署脚本（Windows 服务安装/卸载、systemd unit）
```

## 5. 文档体系（先读这里）

四层文档自上而下约束，**优先级链：PRD > 技术设计 > 开发规范 > AI 判断**，冲突即停并上报：

| 层 | 入口 | 定位与状态 |
|---|---|---|
| 需求 | [docs/prd/README.md](docs/prd/README.md) | 产品要做什么。v0.5，决策 D1~D21，开放问题 OQ-1~18 全部决议（台账见 [09-里程碑与验收](docs/prd/09-里程碑与验收.md) §3） |
| 设计 | [docs/技术设计文档/README.md](docs/技术设计文档/README.md) | 怎么做（施工图纸）。v1.7，技术决策 TD-01~19；11 份分册 + [10-追溯矩阵](docs/技术设计文档/10-追溯矩阵.md)（PRD⇄设计全量对照台账） |
| 规范 | [docs/开发规范/README.md](docs/开发规范/README.md) | 编码时的硬约束：[01 AI 开发基础规则](docs/开发规范/01-AI开发基础规则.md)（AI-01~34）· [02 模块职责边界](docs/开发规范/02-模块职责边界.md)（MOD-1~6）· [03 目录结构规范](docs/开发规范/03-目录结构规范.md)（DIR-1~7） |
| 任务 | [docs/任务清单/README.md](docs/任务清单/README.md) | 写到哪了（执行台账）。[M1-核心链路](docs/任务清单/M1-核心链路.md) ✅ 收官 · [M2-打洞完整与授权闭环](docs/任务清单/M2-打洞完整与授权闭环.md)（35/38）· [M3-管理面与可观测](docs/任务清单/M3-管理面与可观测.md)（0/18）+ 待办池 |

## 6. 里程碑与进度

| 里程碑 | 目标 | 出口标准 | 状态 |
|---|---|---|---|
| **M1 核心链路可用** | 两台设备注册 + 第一条 UDP 直连映射 | 验收场景 A-1~A-4 | ✅ 收官（2026-09-12） |
| M2 打洞完整与授权闭环 | TCP 打洞、中继回退、分组/远程码 | A-5~A-9 | 🔨 进行中（35/38；余实机环境依赖项） |
| M3 管理面与可观测 | 服务端 Web 后台、仪表盘、统计 | A-10~A-12 | 🔨 进行中（0/18） |
| M4 交付打磨 | 安装包、Docker、基准报告、交付文档 | 交付清单齐全 | 待建清单 |

## 7. 端口与地址规划

| 用途 | 值 | 说明 |
|---|---|---|
| 控制协议 PCP | 7000/TCP | 注册/鉴权/心跳/信令 |
| STUN | 3478/UDP+TCP | 公网端点探测（带设备认证） |
| 中继 | 7010+/UDP+TCP | 打洞失败回退路径 |
| 客户端本地 Web | 127.0.0.1:7100 | 无鉴权（仅回环，风险已知情接受） |
| 服务端 Web | 127.0.0.1:7500 | 无公网 HTTP，经映射接入 |
| 虚拟网段 | 100.64.0.0/24（固定 .2） | 仅作本机监听地址，各设备同值 |

## 8. 构建与运行

环境依赖：**.NET 10 SDK**、**Node ≥ 20**、**pnpm**（`corepack enable pnpm`）。

```bash
sh build/build.sh            # 一条命令全链：① dotnet build → ② export-ts → ③ pnpm build → ④ dotnet test
sh build/build.sh quick      # quick = 跳过前端构建阶段

# 或分步执行（08 §2）
dotnet build AI-P2P.sln -c Release
dotnet run --project src/Tools/ExportTs -c Release   # API 类型 → web/ui-shared（api.d.ts / api-paths.ts）
pnpm -C web --filter @p2p/client-app --filter @p2p/server-app build   # 前端产物 → 嵌入宿主 wwwroot
dotnet test AI-P2P.sln -c Release                    # xUnit 全仓测试（M2-34 收口：606 项全绿，Core 覆盖率 80.44%）

# 前端开发（MSW mock，与后端进度解耦）
cd web && pnpm install && pnpm dev
```

部署脚本见 `deploy/`（Windows 服务安装/卸载 PowerShell、systemd unit）。构建产物与运行时数据不入库（DIR-4，见 [.gitignore](.gitignore)）。

## 9. 开发协作约定

- **中文交流**；需求收集为问答式，用户自定义的技术参数**原样精确落档**；
- 一切实现须带**编号依据**（FR-/D-/OQ-/TD-/MOD-/DIR-…），提交格式 `[模块] 类型：摘要（依据 FR/TD/OQ-编号）`；
- 发现规格缺口/矛盾：**停任务 → 记 OQ（自 OQ-19 续编）→ 用户决议 → 回填**，不擅自扩大范围（AI-06/AI-08）；
- 每个任务完成前过 [交付自检清单](docs/开发规范/01-AI开发基础规则.md)（十项）方可勾选 ✅；
- 测试不触公网：本地回环 + NatSimulator 四模式（真实 NAT 行为留 A-5 手工样本）。

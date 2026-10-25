# Windows 客户端部署手册

> 适用版本：1.0.0　依据：NFR-31/32、设计 08 §3.1/§7、03 §5
> 阅读方式：**§0–§3 为核心流程**（安装包 Setup exe，照做即可用）；**§4 为常见问题、§5 为进阶**（静默安装/portable 手工部署，按需查阅）。
> 早期本机部署实录（M1-37 验收记录）见 [docs/部署文档/本机Windows客户端部署.md](../部署文档/本机Windows客户端部署.md)，最新口径以本手册为准。

## 0. 部署总览

| 步骤 | 做什么 |
|---|---|
| ① | 双击安装包，UAC 确认（自动落位 + 注册并启动系统服务） |
| ② | 浏览器打开本地向导，填服务端地址完成注册 |
| ③ | 验证：服务运行中、虚拟网卡出现 |

装完你就拥有：

| 项 | 值 |
|---|---|
| 程序目录 | `C:\Program Files\P2PSocketClient` |
| 数据目录 | `C:\ProgramData\P2PClient`（settings/state/logs——程序目录外，升级与卸载默认保留） |
| Windows 服务 | `P2PClient`（开机自启；崩溃 5s×3 档自动拉起） |
| 本地 Web/向导 | `http://127.0.0.1:7100`（仅本机回环，**无需开任何防火墙端口**） |
| 依赖 | 免装 .NET（自包含单文件）；Wintun 虚拟网卡驱动已随包（须与 exe 同目录） |

## 1. 安装

### 1.1 取得安装包

构建机执行（详见 [打包说明](打包说明.md)）：

```bash
sh build/package.sh win-setup
# 产物：out/P2PSocket-Client-Setup-1.0.0-x64.exe（~36MB），拷贝到目标 Windows 机
```

### 1.2 安装步骤

1. 双击 `P2PSocket-Client-Setup-1.0.0-x64.exe` → UAC「是」→ 完成。
2. 首次运行若弹 SmartScreen「未知发布者」（安装包未做代码签名）：点「**更多信息 → 仍要运行**」。

安装器自动完成：程序落位、创建数据目录（SYSTEM/Administrators 完全控制）、创建并启动服务 `P2PClient`、开始菜单「P2PSocket 客户端 → Web 管理页」快捷方式。

### 1.3 首启注册向导

浏览器打开 `http://127.0.0.1:7100/wizard`（服务模式不自动弹浏览器）：

1. 服务端地址填 `<服务器IP>:7000`；
2. 按向导完成注册；
3. 注册成功后按 §1.4 验证。

### 1.4 验证

```powershell
Get-Service P2PClient          # 期望 Running（管理员）
ipconfig                       # 期望出现虚拟网卡，IP=100.64.0.2
```

- `C:\ProgramData\P2PClient\logs\` 有当日日志；
- 本地 Web 管理页可打开（开始菜单快捷方式或 `http://127.0.0.1:7100`）。

## 2. 日常运维

### 2.1 常用命令（管理员 PowerShell）

```powershell
Get-Service P2PClient          # 状态
Restart-Service P2PClient      # 重启
Get-Content C:\ProgramData\P2PClient\logs\client-$(Get-Date -Format yyyyMMdd).log -Wait -Tail 20   # 日志跟随
```

### 2.2 升级

**直接再运行新版 Setup exe**：安装器先停既有服务 → 覆盖文件 → 重启服务；`%ProgramData%` 数据与凭据保留。（已装服务机也可用 portable 产物覆盖升级，见 §4.2。）

### 2.3 卸载

「设置 → 应用」卸载，或运行 `C:\Program Files\P2PSocketClient\unins000.exe`。卸载链自动完成：停服务 → 清 Wintun 适配器并向服务端解绑（`p2p-client --uninstall`）→ 删服务 → 询问是否删数据目录（默认保留）。

### 2.4 版本查看

Web 管理页「设置 → 关于」（版本/构建提交/协议版本）。协议与服务端不兼容时客户端自动打开 `/upgrade` 升级引导页。

## 3. 常见问题

| 症状 | 处置 |
|---|---|
| 服务起不来/一启动即停 | Wintun.dll 缺失或与 exe 不同目录最常见（安装包形态不会发生，portable 形态注意）；查事件查看器与 `C:\ProgramData\P2PClient\logs\` |
| 7100 打不开 | `Get-Service P2PClient` 是否 Running；7100 仅回环——远程桌面场景须在本机浏览器开 |
| 端口映射连不上 | 映射监听绑**虚拟 IP 100.64.0.2** 而非 127.0.0.1——看端口占用/排障时勿混淆；先 Web 页 ping-device 看隧道状态 |
| 升级后行为异常 | 「设置→关于」核对版本；旧版回滚=停服务换回备份 exe 再起服务 |

## 4. 进阶（按需查阅）

### 4.1 静默安装/卸载（批量部署）

```bat
P2PSocket-Client-Setup-1.0.0-x64.exe /VERYSILENT /SUPPRESSMSGBOXES      :: 静默安装（服务照装照启）
"C:\Program Files\P2PSocketClient\unins000.exe" /VERYSILENT             :: 静默卸载（数据目录默认保留）
"C:\Program Files\P2PSocketClient\unins000.exe" /VERYSILENT /PURGE      :: 静默卸载并删数据目录
```

### 4.2 portable 手工部署（不跑安装包）

适合无法运行安装器的环境或想自管目录的场景：

```text
① 构建：sh build/package.sh client-win        → out/client-win/{p2p-client.exe, wwwroot/, 端点清单}
② Wintun.dll：out/wintun/Wintun.dll 缺则从 https://www.wintun.net/builds/wintun-0.14.1.zip
   取 bin/amd64/wintun.dll 放入发布目录（与 exe 同目录）
③ 拷贝到目标目录（无空格路径，如 D:\P2PClient）：
   p2p-client.exe + Wintun.dll + staticwebassets.endpoints.json + wwwroot\
   + deploy/client/install-windows-service.ps1 / uninstall-windows-service.ps1
④ 安装服务：管理员 PowerShell 在该目录执行 .\install-windows-service.ps1
⑤ 验证：Get-Service P2PClient = Running；http://127.0.0.1:7100/wizard = 200；
   C:\ProgramData\P2PClient\logs\ 有当日日志
```

- **升级**：`Stop-Service P2PClient` → 覆盖 exe+wwwroot+端点清单（Wintun.dll 与数据目录不动）→ 起服务；
- **卸载**：`.\uninstall-windows-service.ps1`（停+删服务），建议再跑 `p2p-client.exe --uninstall` 清 Wintun 适配器与服务端绑定；
- **编码坑**：仓库 ps1 为 UTF-8 无 BOM，Windows PowerShell 5.1 按 GBK 解析中文注释会语法崩——分发副本须重存为 **UTF-8 带 BOM**；.cmd 批处理保持纯 ASCII。

## 相关文档

- [打包说明](打包说明.md)（win-setup / client-win 目标）
- 设计：[08-构建与部署设计](../技术设计文档/08-构建与部署设计.md) §3.1

## 修订记录

| 日期 | 说明 |
|---|---|
| 2026-10-10 | v1.0（M4-07）：Setup 安装包全流程（M4-02 交付）+portable 手工形态（install-windows-service.ps1 口径）+静默参数/升级/卸载数据去留三路径 |
| 2026-10-25 | 重排版：核心流程（安装/向导/验证/运维）与进阶（静默/portable）、常见问题分层重组；命令补预期结果。内容口径不变 |

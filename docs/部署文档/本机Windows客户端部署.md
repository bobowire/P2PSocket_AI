# 本机 Windows 客户端部署(M1-37 实机验收)

> 部署时间:2026-09-11;对应任务 M1-37 客户端侧 A 机(依据 08 §3.1/§5.2、03 §5、A.0②)。
> 部署方式:本机 `dotnet publish`(win-x64 自包含单文件)→ `D:\P2PClient` → Windows 服务 `P2PClient`。

## 1. 部署信息

| 项 | 值 |
|---|---|
| 部署机 | 本机 Windows 10 Pro(19045) |
| 程序目录 | `D:\P2PClient` |
| 数据目录 | `C:\ProgramData\P2PClient`(settings/state/logs,03 §5) |
| Windows 服务 | `P2PClient`(LocalSystem——需加载 Wintun 驱动;开机自启 + 崩溃 5s×3 档自愈,FR-C-902) |
| 本地 Web/向导 | `http://127.0.0.1:7100`(**仅回环,无需开任何防火墙/安全组端口**) |

## 2. 目录结构

```text
D:\P2PClient\                          # 程序目录(更新只动这里)
├── p2p-client.exe                     # 主程序(win-x64 自包含单文件,~105MB)
├── Wintun.dll                         # Wintun 0.14.1 amd64(08 §3.1,M4 前手工分发)
├── p2p-client.staticwebassets.endpoints.json  # 静态资源端点映射(SPA 托管)
├── web.config / aspnetcorev2_inprocess.dll    # 宿主伴随文件
├── wwwroot\                           # 本地 Web 前端(vite 产物,含 .br/.gz 预压缩)
├── install.cmd / install-windows-service.ps1  # 安装(双击 cmd,UAC 提权)
├── uninstall.cmd / uninstall-windows-service.ps1
└── update.cmd / update.ps1            # 更新(停服务→复制新构建→启服务)

C:\ProgramData\P2PClient\              # 数据目录(程序目录外,更新不受影响)
├── settings.json / state.json         # 配置与设备凭据/映射状态
└── logs\client-yyyyMMdd.log           # 滚动日志(10MB×保留 14 天,08 §6)
```

## 3. 端口与网络

| 端口 | 绑定 | 用途 | 外部可达性 |
|---|---|---|---|
| 7100 | 127.0.0.1(TD-12) | 本地 Web/向导/管理页 | 仅本机浏览器,无需开放 |

> 客户端不对外监听任何端口;虚拟 IP 100.64.0.2 上的映射监听在注册成功后由 Wintun 适配器承载(01 §3.2)。

## 4. 日常运维

```powershell
# 服务状态/启停(需管理员)
Get-Service P2PClient
Restart-Service P2PClient
# 日志跟随
Get-Content C:\ProgramData\P2PClient\logs\client-$(Get-Date -Format yyyyMMdd).log -Wait -Tail 20
```

- **更新流程**(代码变更后):本机 `dotnet publish src/P2P.Client -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true -o out/client-win` → 双击 `D:\P2PClient\update.cmd`(UAC 确认)→ 窗口显示 `Update done.`。Wintun.dll 与数据目录不受影响。
- **卸载**:双击 `D:\P2PClient\uninstall.cmd`。

## 5. 首次部署实录(2026-09-11,其它 Windows 机可复用)

```text
① dotnet publish src/P2P.Client -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true -o out/client-win
② Wintun.dll:官方 https://www.wintun.net/builds/wintun-0.14.1.zip 取 bin/amd64/wintun.dll
   (Git Bash 解压须用 /c/Windows/System32/tar.exe——GNU tar 不认 zip)→ 置于发布目录(与 exe 同目录,08 §3.1)
③ 部署目录 D:\P2PClient:exe + Wintun.dll + staticwebassets.endpoints.json + web.config
   + aspnetcorev2_inprocess.dll + wwwroot\ + deploy/client/{install,uninstall}-windows-service.ps1
④ 编码适配(本次实踩的两个坑,复用必看):
   · install/uninstall-windows-service.ps1 含中文注释,仓库为 UTF-8 无 BOM——Windows PowerShell 5.1
     按 GBK 解析直接语法崩(字符串终止符错乱)。修复:部署副本重存为 UTF-8 带 BOM:
     [IO.File]::WriteAllText($f, [IO.File]::ReadAllText($f,[Text.Encoding]::UTF8), (New-Object Text.UTF8Encoding $true))
   · .cmd 批处理须纯 ASCII(UTF-8 中文注释在 CMD 的 GBK 代码页下变乱码破坏命令行)。
⑤ 安装:双击 install.cmd(UAC"是")→ sc create P2PClient(start=auto)+ failure 配置 + 启动。
⑥ 验证:Get-Service P2PClient = Running;curl http://127.0.0.1:7100/wizard = 200;
   C:\ProgramData\P2PClient\logs\ 有当日日志。
```

## 6. 验收对接(M1-37)

1. 本机浏览器打开 **http://127.0.0.1:7100/wizard**(A-1 首启注册入口;服务模式不自动弹浏览器,`--console` 前台跑才会)。
2. 向导服务端地址填 **139.155.70.54:7000**(见《公网服务端部署.md》)。
3. 注册成功 → phase=running,适配器出现且 IP=100.64.0.2;日志 `[control]` 停止 `127.0.0.1:1` 占位重连(该占位重连为未配置服务端时的预期行为,1s 退避)。
4. 本机即"客户端 A(发起)"角色;B 机另部署(A-2/A-3 需第二台设备,A-3 需不同公网出口)。

## 7. 本次部署发现并修复的缺陷(代码回流)

- **缺陷一(SPA 托管缺失)**:部署后 `/wizard`、`/` 均 404,仅 `/api/*` 通。根因:M1 实现中 [ClientRuntime.cs](../../src/P2P.Client/Hosting/ClientRuntime.cs) 构建 `WebApplication` 后只挂 `MapLocalApi`,**SPA 同端口静态托管整块缺失**——与 04 §0"SPA 由同端口静态托管"、08 §2 NFR-31"单 exe 完整"漂移。修复:`_app.MapStaticAssets()` + `_app.MapFallbackToFile("index.html")`(API 端点在前、fallback 兜底,不遮蔽 /api/*);`P2P.Client.Tests` 全过后重发布验证 `/wizard`=200。
- **缺陷二(映射监听绑定地址,2026-09-12 A-4 验收时发现)**:向导路径装机(先装服务后注册)的客户端,注册完成后映射监听一直绑 **127.0.0.1** 而非虚拟 IP 100.64.0.2,与本地同端口服务相撞报 `listen_failed: AddressAlreadyInUse`。根因:冷启动装配引擎时 VirtualIp 尚空(未注册),绑定地址降级 Loopback,注册完成后从不切换(违反 01 §3.2)。修复:[MappingEngine.cs](../../src/P2P.Client/Mapping/MappingEngine.cs) 增 `UpdateVirtualIp()`,注册后路径在 Nic 应用前切换;引擎级回归用例(新监听绑新地址、与回环同端口占位并存互不干扰)随套件全绿。已部署验证:`netstat` 见 `100.64.0.2:8080 LISTENING`,与 `127.0.0.1:8080` 占位并存。
- **已知观察(M2 自愈候选,非缺陷)**:服务重启瞬间恢复映射可能报 `listen_failed: AddressNotAvailable`(Wintun 适配器进程切换窗口、IP 未对 TCP 栈生效),retry 即恢复;重启后监听失败自动重试属恢复健壮性,记 M2。
- 以上代码变更在工作区待提交(随下一次任务收口提交)。

## 8. 离线分发到其它 Windows 机(如客户端 B,无 .NET/无仓库环境)

分发包:`D:\p2pclient-windows-x64.zip`(44MB,自包含,目标机免联网免装 .NET;制作源 `D:\p2pclient-windows-x64\`)。

```text
p2pclient-windows-x64\
├── p2p-client.exe / Wintun.dll / *.json / web.config / aspnetcorev2_inprocess.dll
├── wwwroot\                            # 前端页面(缺了则 7100 全 404)
├── install.cmd                         # %~dp0 相对定位——解压到任意"无空格"目录可用
├── install-windows-service.ps1         # 已重存 UTF-8 带 BOM(规避 PS5.1 GBK 坑)
├── uninstall.cmd / uninstall-windows-service.ps1
└── 部署说明.txt                         # 给执行者的四步说明(见下)
```

目标机人工步骤(全部分钟级):

1. U 盘/微信/局域网共享把 zip 拷过去,解压到 **`D:\P2PClient`**(无空格路径,解压后勿挪动);
2. 双击 `install.cmd` → UAC"是" → 蓝色 PowerShell 窗口出现"服务 P2PClient 已安装并启动(本地管理页 http://127.0.0.1:7100)"即成,关窗;
3. 浏览器开 `http://127.0.0.1:7100/wizard`,服务端地址填 `139.155.70.54:7000`,走注册向导(即 A-1);
4. 验证:服务 P2PClient 运行中 + 虚拟网卡 100.64.0.2 出现;排障看 `C:\ProgramData\P2PClient\logs\`。

> 重新制作分发包(代码变更后):重复 §5①②③ → 复制产物+带 BOM 的 ps1 到 `D:\p2pclient-windows-x64\` → `Compress-Archive`。
> ⚠️ 当前 zip(2026-09-11 制作)**不含缺陷二修复**(映射监听绑定地址)——B 机若补跑 A-4 完整版前须按上条重打包并覆盖 B 机 exe。
> A-3 提醒:客户端 B 须与 A(本机)处于**不同公网出口**(如家宽+手机热点),同局域网不构成真实打洞路径。

## 修订记录

| 日期 | 说明 |
|---|---|
| 2026-09-11 | v1.0:首次部署;修复 SPA 静态托管缺失缺陷并验证;沉淀 ps1-BOM/cmd-ASCII 编码坑 |
| 2026-09-11 | v1.1:新增 §8 离线分发包(客户端 B 人工部署,p2pclient-windows-x64.zip) |
| 2026-09-12 | v1.2:§7 增缺陷二(映射监听绑 127.0.0.1 而非虚拟 IP,UpdateVirtualIp 修复)+重启 AddressNotAvailable 观察项;§8 标注当前 zip 不含该修复;更新流程补 publish 须单文件自包含参数(-p:PublishSingleFile=true -p:SelfContained=true,框架依赖散装会致服务起不来) |

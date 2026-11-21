# Android 客户端部署手册

> 适用版本：1.0.0　依据：设计 13 号 §6/§8/§9、设计 08 §1/§2⑤、NFR-31/32
> 阅读方式：**§0–§3 为核心流程**（构建机产出 APK → 手机安装注册 → 验证 → 日常运维）；**§4 为常见问题、§5 为进阶**（adb 通道/日志/keystore 更换，按需查阅）。
> 形态口径：Android 客户端 = **端口映射模式（无卡模式）**——不建虚拟网卡、零 VPN 权限，映射监听绑 `127.0.0.1`；与桌面客户端（虚拟网卡 `100.64.0.2`）同一账号体系互为对端。日常使用见 [用户使用手册 §1.6](../manual/用户使用手册.md)。

## 0. 部署总览

| 步骤 | 做什么 |
|---|---|
| ① | 构建机执行 `sh build/package.sh android` 产出签名 APK（§1） |
| ② | APK 传到手机，sideload 安装（允许「未知来源」）（§2.1） |
| ③ | 打开 App，WebView 向导填服务端地址完成注册（§2.2） |
| ④ | 厂商权限两处授权（自启动/电池白名单）+ 验证（§2.3/§2.4） |

装完你就拥有：

| 项 | 值 |
|---|---|
| App 包名 | `com.p2psocket.client`（显示名「P2P Socket」） |
| 系统要求 | **Android 8.0+**（minSdk 26——ANDROID_ID 签名作用域语义起点，设计 13 §4.4） |
| 使用形态 | 端口映射模式：手机上任意 App 连 `127.0.0.1:本地端口` 直达对端内网服务 |
| 管理页 | App 本体=全屏 WebView 自动加载（带 token）；同机浏览器亦可开 `http://127.0.0.1:7100` |
| 保活 | 常驻前台服务（通知栏「隧道服务」频道）+ 开机自启（BOOT_COMPLETED，厂商条件见 §2.3） |
| 数据目录 | App 私有存储（state/settings/peers/web-token/logs，`FilesDir` 下）——卸载即清空 |
| 依赖 | 免装 .NET（自包含）；APK 已内置 Web 前端（SPA 以 AndroidAsset 打包，安装时拷出） |
| 分发件 | `P2PSocket-Client-Android-v<版本>.apk`（~28MB）+ 同名 `.sha256`（自签名自分发，不走应用商店） |

## 1. 构建安装包（APK）

### 1.1 环境依赖（构建机，一次性）

基础三件与桌面链同款（dotnet SDK 10、node ≥20 + pnpm，见 [打包说明 §1](打包说明.md)），另需 Android 专属三件：

| 依赖 | 安装 |
|---|---|
| .NET android workload | `dotnet workload install android`（免提权；**打包脚本探测缺失则跳过并明示，不报错**） |
| JDK 17 | zip 免提权解压（`aka.ms/download-jdk/microsoft-jdk-17-windows-x64.zip`），设 `JAVA_HOME` 并入 PATH |
| Android SDK | commandline-tools 解压至任意目录 → `sdkmanager --licenses` → 装 `platform-tools` + `platforms;android-36`（+`36.1`）+ `build-tools;36.0.0`（+`36.1.0`），设 `ANDROID_HOME` 并入 PATH |

> 完整参数与本机实录见 [打包说明 §1](打包说明.md) 环境依赖表；本手册不重复罗列版本号。

### 1.2 生成 APK

构建机执行（Git Bash，仓库根）：

```bash
git status --short                                      # ① 应无未提交改动——先提交再打包（版本纪律见打包说明 §3）
sh build/package.sh android                             # ② 前端构建 → publish → 签名
sha256sum -c out/P2PSocket-Client-Android-v1.0.0.apk.sha256   # ③ 清单核验
# 产物：out/P2PSocket-Client-Android-v1.0.0.apk（~28MB，双 ABI AOT）
```

链路（[打包说明 §2.4](打包说明.md)）：前端 `client-app` 构建（wwwroot 以 AndroidAsset 打包）→ `dotnet publish src/P2P.Client.Android -c Release`（net10.0-android，**工程不进主 sln**——免桌面构建机强制装 workload）→ keystore 签名 → 改名 `P2PSocket-Client-Android-v<版本>.apk` + `.sha256` 入 `out/SHA256SUMS`。

**版本号两处递进**（升级发布前检查）：

| 来源 | 去向 | 递进方式 |
|---|---|---|
| `src/Directory.Build.props` 的 `<Version>` | APK 文件名 + 管理页「关于」显示版本 | 手动递进（单一事实源，设计 08 §1） |
| `src/P2P.Client.Android/P2P.Client.Android.csproj` 的 `<ApplicationVersion>` | **versionCode**（Android 安装器判定新旧） | **随发布手动递进**——只改 `<Version>` 不改 versionCode 时，Android 视为同版本覆盖安装（`adb install -r` 可强装，部分 ROM 侧载界面会拒） |

### 1.3 签名与 keystore

- 签名件=仓库内 `deploy/client-android/p2pclient.keystore`（keytool 自签生成，alias/口令见 `build/package.sh` 的 `run_android()` 传参段——**单一事实源在脚本**，本手册不复制口令）；个人自部署口径、仓库私有前提下入库（设计 13 §8 取舍）；
- **升级必须同一签名**：换 keystore 后 Android 拒绝覆盖安装（`INSTALL_FAILED_UPDATE_INCOMPATIBLE`），须先卸载（数据随之清空，见 §3.1）；
- 自行更换 keystore（如转正式分发）：

```bash
keytool -genkeypair -v -keystore deploy/client-android/<新名>.keystore -alias <别名> \
  -keyalg RSA -keysize 2048 -validity 10000
# 同步修改 build/package.sh run_android() 的 AndroidSigningKeyStore/Alias/KeyPass/StorePass 五参
```

> keystore 一旦用于分发即**不可丢失**（丢失=无法覆盖升级）且口令泄露面=仓库访问面——转多用户分发时应改为 .gitignore + 环境变量注入，此处维持个人自部署最简口径。

## 2. 装机与注册

### 2.1 传输与安装

APK 任意方式传手机（USB 拷贝/局域网共享/网盘），点开安装：

1. 首次 sideload 需允许「**未知来源/安装未知应用**」（各 ROM 入径不同：系统设置 → 应用 → 特殊权限，或安装弹窗直接引导）；
2. 自签名包首次安装即建立信任，安装完成桌面出现「P2P Socket」图标。

开发者路线（USB 调试已开）：`adb install -r out/P2PSocket-Client-Android-v1.0.0.apk`（`-r`=覆盖保留数据）。

### 2.2 首启向导注册

打开 App（WebView 自动加载向导）：

1. 服务端地址填 `<服务器IP>:7000`；
2. 按向导完成注册（产出 deviceId/远程码，默认落默认分组）；
3. 可选：登录与桌面同账号完成设备绑定（跨设备互见更直观；default 模式下不同账号设备亦经分组互通，口径见用户手册 §1.5.3）。

### 2.3 厂商权限（保能口径，装机必看）

| 权限 | 入口与效果 |
|---|---|
| **自启动**（MIUI 等） | 应用信息 → 自启动 手动开启。**MIUI 默认拦截旁加载应用的自启**（M8-07 实机实证）：未开启时重启手机后须手动点开一次 App（启动后注册态/登录/映射全量自动恢复，无功能损失）；开启后重启无需干预 |
| **电池优化白名单** | 设置 → 电池 → 不受限制/允许后台运行（各厂商入口不同）。前台服务已是最强保活承诺，但激进省电 ROM（Doze 之外）仍可能杀进程——被杀后重开 App/重启手机会自动恢复映射，断开期间的连接需重连 |

### 2.4 验证

- 通知栏出现常驻通知（频道「隧道服务」）；
- App 管理页「设置 → 关于」版本/构建提交正确；或同机浏览器开 `http://127.0.0.1:7100`（Android 上 localhost 全 App 互通）；
- 建一条映射（如 `127.0.0.1:18080 → 对端设备:端口`），手机浏览器访问验证连通；管理页映射列表「连接地址」应如实显示 `127.0.0.1:端口`；
- 蜂窝网络下预期打洞偏低自动回退中继（设备级 `relayFallback` 开关默认关，蜂窝用户建议在对端设备页开启——用户手册 §1.6）。

## 3. 日常运维

### 3.1 升级

**同签名新版 APK 覆盖安装**：`adb install -r` 或文件管理器点开新 APK → 数据保留（私有目录不清）→ 重启 App 后自动登录+映射自动恢复。发布前记得递进 versionCode（§1.2）。

**卸载重装**：数据目录随卸载清空，须重走 §2.2 向导——但设备身份经 ANDROID_ID 指纹在卸载重装间保持稳定（Android 8.0+ 签名作用域），**服务端同设备覆盖恢复，不产生重复设备行**（M8-07 实证：强杀/重启/重装三场景 deviceId 不变）。

### 3.2 卸载

系统设置直接卸载。服务端侧设备行保留（管理员可在管理页解绑/禁用）；重装同签名版本即按 §3.1 后半段身份延续。

### 3.3 版本查看

App 管理页「设置 → 关于」（版本/构建提交/协议版本）。协议与服务端不兼容时 WebView 自动进 `/upgrade` 升级引导页（Android 形态升级=手动装新 APK，页内提示分发件来源）。

## 4. 常见问题

| 症状 | 处置 |
|---|---|
| 装不上：签名不一致 | 新旧 APK keystore 不同——先卸载旧版再装（数据清空，§1.3/§3.1） |
| 装不上：未知来源被拒 | ROM 侧载授权未给（§2.1①） |
| 重启手机不自启 | MIUI 等「自启动」权限未开（§2.3，M8-07 实证口径）；开启前重启后手动点开一次即可 |
| 锁屏/后台断流 | 电池白名单未加或 ROM 激进省电杀进程（§2.3；重开自动恢复）；详见[排障 §25](../manual/troubleshooting.md) |
| 映射端口连不上 | 监听在 `127.0.0.1` 与手机其它 localhost 监听同冲突域（含本客户端 7100）——避让常用端口；详见[排障 §24](../manual/troubleshooting.md) |
| App 白屏/WebView 报错 | Web 宿主降级（隧道仍在）——杀掉 7100 占用进程后重启 App；详见[排障 §26](../manual/troubleshooting.md) |
| 蜂窝打洞失败走中继 | CGNAT 网络现实非缺陷；对端开 `relayFallback` 获回退能力（§2.4） |

## 5. 进阶（按需查阅）

### 5.1 adb 自动化通道（脚本验机/排障）

```bash
adb forward tcp:17100 tcp:7100                                   # 本机 17100 → 手机 127.0.0.1:7100
curl http://127.0.0.1:17100/api/system/state                     # 版本/phase/commit 直读（M8-07 验收用法）
adb shell am start -n com.p2psocket.client/com.p2psocket.client.MainActivity   # 拉起 App
adb logcat --pid=$(adb shell pidof -s com.p2psocket.client)      # 进程日志跟随
```

### 5.2 日志获取

- App 管理页「日志」（FileLog 全量，与桌面同源格式）；
- `adb logcat`（运行时诊断，过滤 `com.p2psocket.client`）。

### 5.3 CI 口径

CI（.github/workflows/ci.yml）android 壳构建为**守卫式**：runner 未装 android workload 即跳过明示，不设强制门禁——真机构建以本地/专用构建机 `build/build.sh`、打包以 `build/package.sh android` 为准（设计 13 §8）。

## 相关文档

- [打包说明](打包说明.md)（android 目标/环境依赖全表）· [Windows 客户端部署手册](Windows客户端部署手册.md) · [Linux 客户端部署手册](Linux客户端部署手册.md) · [服务端部署手册](服务端部署手册.md)
- 使用：[用户使用手册 §1.6](../manual/用户使用手册.md)　排障：[troubleshooting §24~26](../manual/troubleshooting.md)
- 设计：[13-Android客户端设计](../技术设计文档/13-Android客户端设计.md)（§6 前台服务/§8 构建打包/§9 兼容共存）
- 真机实证：[M8-07 验收记录](../验收记录/2026-11-21-M8-07-Android真机验收/验收报告.md)（Xiaomi/Android 14 四场景+两缺陷修复实录）

## 修订记录

| 日期 | 说明 |
|---|---|
| 2026-11-21 | v1.0（M8 收官文档补全）：新建——APK 构建（环境三件/命令/版本双递进）/keystore 签名与更换/装机注册/厂商权限（MIUI 自启+电池白名单，M8-07 实证）/升级卸载（ANDROID_ID 身份延续）/常见问题/adb 通道与日志进阶。既有 Android 内容归位：生成走查提级进[打包说明 §2.4](打包说明.md)，日常使用在用户手册 §1.6，症状处置在排障 §24~26 |

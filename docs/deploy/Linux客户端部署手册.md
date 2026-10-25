# Linux 客户端部署手册

> 适用版本：1.0.0　依据：NFR-31/33、设计 08 §3.2/§7、03 §5
> 阅读方式：**§0–§3 为核心流程**（tar.gz 直装 + systemd，分发口径形态，照做即可用）；**§4 为常见问题、§5 为进阶**（deb/rpm 包形态，按需查阅）。
> 注意：若装在**服务器本机**（与 P2P.Server 同机），打洞端口会被云安全组挡住，该形态的常态承载是设备级中继回退（Web 页设备列表开启「中继回退」）。

## 0. 部署总览

| 步骤 | 做什么 |
|---|---|
| ① | 解压 tar.gz 到部署目录 |
| ② | 写 systemd 单元，`enable`（**不 start**——注册完成后再启动） |
| ③ | 浏览器打开本地向导完成注册 |
| ④ | `systemctl start`，验证虚拟网卡 |

装完你就拥有：

| 项 | 值 |
|---|---|
| 部署目录 | 自定（本文以 `/opt/p2p/client` 为例；**exe 与 wwwroot/ 必须同目录**） |
| 数据目录 | `/etc/p2p-client/`（0700 root——settings/state/凭据/logs，代码硬编码） |
| systemd 服务 | `p2p-client`（root 运行——TUN 需 CAP_NET_ADMIN；崩溃 5s 自动拉起） |
| 本地 Web/向导 | `http://127.0.0.1:7100`（仅回环；远程机器经 SSH 隧道访问，见 §2） |
| 依赖 | 免装 .NET（自包含单文件）；**iproute2**（TUN 地址配置）；内核 TUN 支持（`/dev/net/tun` 存在——云镜像默认有，容器内须显式挂载） |

## 1. 安装

### 步骤 1：取得并解压安装包

构建机执行（详见 [打包说明](打包说明.md)）：

```bash
sh build/package.sh client-linux
# 产物：out/P2PSocket-client-linux-v1.0.0.tar.gz，传到目标机
```

目标机执行：

```bash
sudo mkdir -p /opt/p2p/client
sudo tar -C /opt/p2p/client -xzf P2PSocket-client-linux-v1.0.0.tar.gz
sudo chmod +x /opt/p2p/client/p2p-client
```

> `chmod +x` 必做：Windows 构建机打出的归档不保留执行位，漏掉则 systemd 起服务报 203/EXEC。

### 步骤 2：安装 systemd 单元（注册前只 enable 不 start）

```bash
sudo tee /etc/systemd/system/p2p-client.service <<'EOF'
[Unit]
Description=P2P Client (virtual NIC + port mapping tunnel)
After=network-online.target
Wants=network-online.target
[Service]
Type=simple
WorkingDirectory=/opt/p2p/client
ExecStart=/opt/p2p/client/p2p-client
Restart=always
RestartSec=5
[Install]
WantedBy=multi-user.target
EOF
sudo systemctl daemon-reload && sudo systemctl enable p2p-client
```

**为什么先不启动**：未注册的客户端起来只会空转重连——向导注册完成（§2）后再 `systemctl start` 即进入运行。

## 2. 首启注册向导

浏览器打开 `http://127.0.0.1:7100/wizard`（部署在远程机器时先在本机建隧道）：

```bash
ssh -L 7100:127.0.0.1:7100 <用户>@<主机>    # 保持窗口，本机浏览器访问 http://127.0.0.1:7100/wizard
```

向导中服务端地址填 `<服务器IP>:7000`，完成注册，然后启动服务：

```bash
sudo systemctl start p2p-client
```

## 3. 验证与日常运维

### 3.1 验证

```bash
systemctl status p2p-client     # 期望 active (running)
ip link                          # 期望出现 p2p 虚拟网卡（100.64.0.2/24）
ls /etc/p2p-client/logs/         # 期望 client-<日期>.log
curl -s http://127.0.0.1:7100/wizard | head -1    # 期望 200
```

### 3.2 常用命令

```bash
systemctl status p2p-client                       # 状态
sudo systemctl restart p2p-client                 # 重启
journalctl -u p2p-client -f                       # systemd 侧日志
tail -f /etc/p2p-client/logs/client-$(date +%Y%m%d).log   # 应用日志
```

版本查看：Web「设置 → 关于」；协议与服务端不兼容时客户端自动打开 `/upgrade` 升级引导页。

### 3.3 升级

```bash
sudo systemctl stop p2p-client
sudo cp /opt/p2p/client/p2p-client /opt/p2p/client/p2p-client.bak-$(date +%Y%m%d)   # 备份备回滚
# 解压新版覆盖 p2p-client + wwwroot/ + 端点清单（/etc/p2p-client 数据不动），并 chmod +x
sudo systemctl start p2p-client
```

### 3.4 卸载

```bash
sudo systemctl stop p2p-client && sudo systemctl disable p2p-client
p2p-client --uninstall      # 清 TUN 适配器 + 服务端解绑（另有 --unbind / --purge，见 --help）
sudo rm -rf /opt/p2p/client /etc/systemd/system/p2p-client.service
sudo systemctl daemon-reload
```

数据目录 `/etc/p2p-client/` 按需保留或删除。

## 4. 常见问题

| 症状 | 处置 |
|---|---|
| 打洞必 punch_timeout | 云安全组只放行固定端口，随机打洞端口被挡——开启**设备级中继回退**（Web 页设备列表；该形态属预期常态） |
| 7100 打不开 | 仅回环监听：`ssh -L 7100:127.0.0.1:7100 <主机>` 后本机访问 |
| 服务 Running 但未注册 | 正常——首装只 enable 不 start，须先走 §2 向导；看 `/etc/p2p-client/state.json` 是否已有设备凭据 |
| `--uninstall`/注册报 SIOCGIFINDEX/EINVAL | 确认内核 TUN 可用：`/dev/net/tun` 存在（容器内须显式挂载） |
| systemd 起服务报 203/EXEC | 解包后漏了 `chmod +x p2p-client`（§1 步骤 1） |

## 5. 进阶：deb / rpm 包形态（按需查阅）

适合 want 包管理器托管的 Debian/Ubuntu 或 RHEL 系环境；布局为 `/usr/lib/p2p-client/`（主程序+wwwroot）+ `/usr/bin/p2p-client`（符号链接）+ `/lib/systemd/system/p2p-client.service`，postinst 自动创建 `/etc/p2p-client/`（0700）。

### 5.1 deb（Debian/Ubuntu）

```bash
# 构建机（仓库根）先出产物，再到 Debian/Ubuntu 打包机打 deb：
sh build/package.sh client-linux
sh deploy/client-linux/build-deb.sh             # → out/p2p-client_1.0.0-1_amd64.deb（校验：dpkg-deb -c / -I）

# 目标机：
sudo dpkg -i p2p-client_1.0.0-1_amd64.deb       # 首装：自动 enable，不 start——注册（§2）后手工 start
sudo dpkg -i p2p-client_<新版>_amd64.deb        # 升级：在跑则自动重启，无缝切版
sudo dpkg -r p2p-client                         # 移除（保留 /etc/p2p-client 数据）
sudo dpkg -P p2p-client                         # 清除（连数据目录一起删）
```

### 5.2 rpm（CentOS/RHEL/openEuler 等，需 rpmbuild）

```bash
rpmbuild -bb deploy/client-linux/p2p-client.spec    # 产物在 ~/rpmbuild/RPMS/x86_64/
sudo rpm -ivh p2p-client-1.0.0-1.x86_64.rpm         # 安装（同 deb：enable 不 start）
sudo rpm -Uvh p2p-client-<新版>.rpm                 # 升级（自动切版重启）
sudo rpm -e p2p-client                              # 卸载（rpm 无 purge 两段，erase 即停+清状态）
```

与 tar 直装的差异：包形态自带依赖声明与 systemd 单元；tar 形态无依赖声明（iproute2 须自查）、升级需手工停/起。

## 相关文档

- [打包说明](打包说明.md)（client-linux 目标）
- [服务端部署手册](服务端部署手册.md)
- 设计：[08-构建与部署设计](../技术设计文档/08-构建与部署设计.md) §3.2

## 修订记录

| 日期 | 说明 |
|---|---|
| 2026-10-10 | v1.0（M4-07）：deb 三命令族（装/升级/卸载双路径）+rpm spec 跑法与语义差异+手工 tar 直装（含现网 /opt/p2p/client 布局）+TUN/root 要求+云主机回退常态注记 |
| 2026-10-25 | 重排版：tar 直装提为核心路径（分发口径），deb/rpm 收进进阶；命令补预期结果。内容口径不变 |

// M1-21 Linux 虚拟网卡管理（FR-C-201、05 §1.3）：
// 打开 /dev/net/tun → ioctl TUNSETIFF(IFF_TUN|IFF_NO_PI) 绑定接口名 p2p-tun → rtnetlink 配置 /32
// 地址并拉起接口（失败降级 ip 命令，任务清单 M1-21 明示允许）→ 持有 fd 保活（05 §1.3：
// 接口生命周期与 fd 绑定，未设 TUNSETPERSIST，close 即移除）。运行期自愈（FR-C-202，M2-24）：
// CheckHealth 只读探测原语（sysfs 存在性+SIOCGIFADDR 主地址），检测循环与重建编排归客户端宿主
// NicHealthMonitor（P2P.Client/Nic）。
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace P2P.Nic.Linux;

[SupportedOSPlatform("linux")]
public sealed class LinuxTunNicManager : INicManager, IAsyncDisposable
{
    /// <summary>接口名（05 §1.3；IFNAMSIZ=16 含 NUL 内）。</summary>
    public const string InterfaceName = "p2p-tun";
    private const string TunDevice = "/dev/net/tun";

    private static int _seq; // netlink 请求序号（进程内递增）

    private readonly object _gate = new();
    private SafeFileHandle? _tunFd; // fd 保活：接口随 fd 释放自动移除（05 §1.3）
    private uint _ifindex;          // rtnetlink 定位接口用（SIOCGIFINDEX 取得）
    private IPAddress? _appliedIp;
    private NicHandle? _handle;

    /// <summary>运行期降级告警（05 §1.1 接口成员；M2-24 起自愈异常经 NicHealthMonitor 日志展示，
    /// 本实现不触发（探测结果由宿主循环消费））。</summary>
#pragma warning disable CS0067
    public event Action<string>? Degraded;
#pragma warning restore CS0067

    public Task<NicHandle> EnsureAsync(IPAddress virtualIp, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(virtualIp);
        if (virtualIp.AddressFamily != AddressFamily.InterNetwork)
            throw new NicException($"虚拟 IP 仅支持 IPv4，收到 {virtualIp}");

        lock (_gate)
        {
            if (_handle is not null)
            {
                if (!_appliedIp!.Equals(virtualIp))
                    throw new NicException($"网卡已以 {_appliedIp} 运行；M1 不支持运行中改址（重启客户端生效）");
                return Task.FromResult(_handle); // 复用幂等（FR-C-201）
            }

            try
            {
                // ① 打开 TUN 设备并绑定接口名（IFF_NO_PI：裸 IP 包，无 4B 协议信息前缀）
                _tunFd = File.OpenHandle(TunDevice, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                AttachInterface(_tunFd);

                // ② 配置 /32 地址并拉起接口：rtnetlink 优先，ip 命令降级（05 §1.3）
                ApplyAddress(virtualIp);
                SetLinkUp();
            }
            catch (IOException e)
            {
                Teardown();
                throw new NicException($"无法打开 {TunDevice}：内核需加载 tun 模块（modprobe tun）。{e.Message}");
            }
            catch
            {
                Teardown(); // 半建状态不留脏 fd/半配置接口
                throw;
            }

            _appliedIp = virtualIp;
            _handle = new NicHandle(InterfaceName, virtualIp);
            return Task.FromResult(_handle);
        }
    }

    public Task RemoveAsync(CancellationToken ct = default)
    {
        // close fd：非持久 TUN 接口随 fd 释放自动移除，地址随之消失（05 §1.3）
        lock (_gate) Teardown();
        return Task.CompletedTask;
    }

    /// <summary>健康探测（FR-C-202，M2-24）：sysfs 存在性（接口随 fd 生命周期，外部
    /// `ip link del` 亦摘除 sysfs 节点）→ SIOCGIFADDR 取接口 IPv4 主地址比对。
    /// 探测手段不可用（无 socket/errno）按健康返回（不触发重建）。</summary>
    public NicHealth CheckHealth(IPAddress expectedIp)
    {
        ArgumentNullException.ThrowIfNull(expectedIp);
        if (!File.Exists("/sys/class/net/" + InterfaceName))
            return new NicHealth(NicHealthState.AdapterMissing, null);

        var fd = LinuxNative.socket(LinuxNative.AfInet, LinuxNative.SockDgram, LinuxNative.IpProtoUdp);
        if (fd < 0) return new NicHealth(NicHealthState.Healthy, expectedIp); // 探测不可用：按健康
        // ifreq：ifr_name[16] + ifr_addr(sockaddr_in：family@16、port@18、addr@20 网络序)
        var ifr = new byte[LinuxNative.IfReqSize];
        Encoding.ASCII.GetBytes(InterfaceName, ifr);
        using var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true); // Dispose 即 close(fd)
        if (LinuxNative.ioctl(handle, LinuxNative.SiocGifAddr, ifr) != 0)
            return new NicHealth(NicHealthState.IpMismatch, null); // 接口无 IPv4 地址
        if (BinaryPrimitives.ReadUInt16LittleEndian(ifr.AsSpan(LinuxNative.IfAddrOffset)) != LinuxNative.AfInet)
            return new(NicHealthState.IpMismatch, null);
        Span<byte> addr = stackalloc byte[4];
        ifr.AsSpan(LinuxNative.IfAddrOffset + 4, 4).CopyTo(addr);
        var bound = new IPAddress(addr);
        return bound.Equals(expectedIp)
            ? new NicHealth(NicHealthState.Healthy, bound)
            : new NicHealth(NicHealthState.IpMismatch, bound);
    }

    public ValueTask DisposeAsync() => new(RemoveAsync());

    // ── TUN 设备绑定 ───────────────────────────────────────────────────

    private void AttachInterface(SafeFileHandle fd)
    {
        // struct ifreq 40B（64 位 Linux）：ifr_name[16]@0 + 联合体（ifr_flags short / ifr_ifindex int）@16。
        // 内核 TUNSETIFF 处理器按完整 sizeof(struct ifreq) 拷贝，缓冲不足会越界，须 40B。
        var ifr = new byte[LinuxNative.IfReqSize];
        Encoding.ASCII.GetBytes(InterfaceName, ifr); // 名 <15 字符，尾部 NUL 由零初始化补齐
        BinaryPrimitives.WriteInt16LittleEndian(ifr.AsSpan(LinuxNative.IfFlagsOffset),
            (short)(LinuxNative.IfFTun | LinuxNative.IfFNoPi));

        if (LinuxNative.ioctl(fd, LinuxNative.TunSetIf, ifr) != 0)
            ThrowErrno("TUNSETIFF");

        // 同一 ifreq 复用取 if_index（rtnetlink 配址/拉起按索引定位接口）
        Array.Clear(ifr, LinuxNative.IfFlagsOffset, sizeof(int));
        if (LinuxNative.ioctl(fd, LinuxNative.SiocGifIndex, ifr) != 0)
            ThrowErrno("SIOCGIFINDEX");
        _ifindex = (uint)BinaryPrimitives.ReadInt32LittleEndian(ifr.AsSpan(LinuxNative.IfFlagsOffset));
    }

    // ── rtnetlink 配址与拉起（ip 命令降级）────────────────────────────

    private void ApplyAddress(IPAddress virtualIp)
    {
        var err = NetlinkRequest(BuildNewAddrMessage(virtualIp, _ifindex, NextSeq()));
        if (err == 0 || err == -LinuxNative.EExist) return; // EEXIST：地址已配置，幂等成功
        if (RunIp($"addr add {virtualIp}/32 dev {InterfaceName}", tolerateExists: true)) return;
        throw new NicException(
            $"配置地址 {virtualIp}/32 失败：rtnetlink errno {-err}，ip 命令降级亦失败（需 root/CAP_NET_ADMIN，05 §1.3）");
    }

    private void SetLinkUp()
    {
        var err = NetlinkRequest(BuildNewLinkMessage(_ifindex, NextSeq()));
        if (err == 0) return;
        if (RunIp($"link set {InterfaceName} up", tolerateExists: false)) return;
        throw new NicException($"拉起接口 {InterfaceName} 失败：rtnetlink errno {-err}，ip 命令降级亦失败");
    }

    /// <summary>执行 ip 命令降级路径。tolerateExists：addr add 的「File exists」视为幂等成功。</summary>
    private static bool RunIp(string arguments, bool tolerateExists)
    {
        try
        {
            var psi = new ProcessStartInfo("ip", arguments)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return false;
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(5_000))
            {
                process.Kill();
                return false;
            }
            if (process.ExitCode == 0) return true;
            return tolerateExists && stderr.Contains("File exists", StringComparison.Ordinal);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return false; // 系统无 ip 二进制（最小容器镜像），降级路径不可用
        }
    }

    /// <summary>发送单条 netlink 请求并等待内核 ACK（本机同步应答）。返回 nlmsgerr.error（0 成功/负 errno；-1 表示传输层失败）。</summary>
    private static int NetlinkRequest(byte[] message)
    {
        var fd = LinuxNative.socket(LinuxNative.AfNetlink, LinuxNative.SockRaw, LinuxNative.NetlinkRoute);
        if (fd < 0) return -1;
        try
        {
            // 1s 接收超时（本机内核应答即时；仅防异常挂起，失败则退化为无限等待）
            var timeout = new byte[16]; // struct timeval（64 位）：tv_sec=1、tv_usec=0
            BinaryPrimitives.WriteInt64LittleEndian(timeout, 1);
            _ = LinuxNative.setsockopt(fd, LinuxNative.SolSocket, LinuxNative.SoRcvTimeo, timeout, timeout.Length);

            var kernel = new LinuxNative.SockaddrNl { Family = LinuxNative.AfNetlink };
            if (LinuxNative.sendto(fd, message, message.Length, 0, ref kernel, LinuxNative.SockaddrNlSize)
                != message.Length) return -1;

            var buffer = new byte[4096];
            var from = new LinuxNative.SockaddrNl();
            var fromLen = LinuxNative.SockaddrNlSize;
            var received = LinuxNative.recvfrom(fd, buffer, buffer.Length, 0, ref from, ref fromLen);
            // 期待 NLMSG_ERROR：响应头(16) + nlmsgerr{ error@16、请求头回显@20 }，seq 回显 @28
            if (received < 36) return -1;
            if (BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(4)) != LinuxNative.NlmsgError) return -1;
            if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(28))
                != BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(8))) return -1; // 串扰防护
            return BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(16));
        }
        finally { _ = LinuxNative.close(fd); }
    }

    /// <summary>RTM_NEWADDR 消息（rtnetlink.h/if_addr.h，AI-03 核对）：40B——
    /// nlmsghdr(16)+ifaddrmsg(8)+rtattr IFA_LOCAL(4+4)+rtattr IFA_ADDRESS(4+4)，4B 对齐无填充。
    /// if_addr.h 注释：点对点设备 IFA_ADDRESS 为对端地址、IFA_LOCAL 为本端——两者同值即普通单播地址
    /// （与 iproute2 `ip addr add` 行为一致）。</summary>
    internal static byte[] BuildNewAddrMessage(IPAddress virtualIp, uint ifIndex, uint seq)
    {
        var msg = new byte[40];
        var span = msg.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, 40);                        // nlmsg_len
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], LinuxNative.RtmNewAddr);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..],
            LinuxNative.NlmFRequest | LinuxNative.NlmFAck | LinuxNative.NlmFCreate | LinuxNative.NlmFExcl);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], seq);                  // nlmsg_seq
        // nlmsg_pid@12 恒 0（发送进程端口 ID，内核按 socket 自动回填）
        msg[16] = 2;                            // ifa_family = AF_INET
        msg[17] = 32;                           // ifa_prefixlen：/32 on-link（TD-06 本地交付）
        msg[18] = LinuxNative.IfaFPermanent;    // ifa_flags：不随链路 down 移除
        msg[19] = 0;                            // ifa_scope = RT_SCOPE_UNIVERSE
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], ifIndex);            // ifa_index
        BinaryPrimitives.WriteUInt16LittleEndian(span[24..], 8);                  // rta_len（头 4 + 地址 4）
        BinaryPrimitives.WriteUInt16LittleEndian(span[26..], LinuxNative.IfaLocal);
        virtualIp.GetAddressBytes().CopyTo(msg, 28);                              // 网络序原样
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], 8);
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], LinuxNative.IfaAddress);
        virtualIp.GetAddressBytes().CopyTo(msg, 36);
        return msg;
    }

    /// <summary>RTM_NEWLINK 消息（rtnetlink.h，AI-03 核对）：32B——
    /// nlmsghdr(16)+ifinfomsg(16)：family(1)+pad(1)+type(2)+index(4)+flags(4)+change(4)。</summary>
    internal static byte[] BuildNewLinkMessage(uint ifIndex, uint seq)
    {
        var msg = new byte[32];
        var span = msg.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, 32);                       // nlmsg_len
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], LinuxNative.RtmNewLink);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], LinuxNative.NlmFRequest | LinuxNative.NlmFAck);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], seq);
        msg[16] = 0;                            // ifi_family = AF_UNSPEC
        // __ifi_pad@17、ifi_type@18 恒 0（不修改设备类型）
        BinaryPrimitives.WriteInt32LittleEndian(span[20..], checked((int)ifIndex)); // ifi_index
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], LinuxNative.IfFUp);  // ifi_flags
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], LinuxNative.IfFUp);  // ifi_change：仅 UP 位生效
        return msg;
    }

    private static uint NextSeq() => (uint)Interlocked.Increment(ref _seq);

    // ── 清理 ──────────────────────────────────────────────────────────

    private void Teardown()
    {
        _tunFd?.Dispose(); // 接口随 fd 关闭移除（unregister 异步生效，测试侧轮询兜底）
        _tunFd = null;
        _ifindex = 0;
        _appliedIp = null;
        _handle = null;
    }

    private static void ThrowErrno(string api)
    {
        var errno = Marshal.GetLastWin32Error();
        var hint = errno switch
        {
            LinuxNative.EBusy => "（接口 fd 已被其他进程占用）",
            LinuxNative.EPerm => "（需要 root/CAP_NET_ADMIN）",
            _ => "",
        };
        throw new NicException($"{api} 失败：errno {errno}{hint}");
    }
}

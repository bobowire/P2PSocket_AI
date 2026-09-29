// M1-21 Linux TUN/netlink 原生 API（05 §1.3）。AI-03：常量与布局对照内核 UAPI 头逐项核对——
//   include/uapi/linux/if_tun.h    TUNSETIFF=_IOW('T',202,int)、IFF_TUN=0x1、IFF_NO_PI=0x1000
//   include/uapi/linux/sockios.h   SIOCGIFINDEX=0x8933
//   include/uapi/linux/netlink.h   NETLINK_ROUTE=0、sockaddr_nl=12B、NLM_F_*、NLMSG_ERROR
//   include/uapi/linux/rtnetlink.h RTM_NEWLINK=16、RTM_NEWADDR=20
//   include/uapi/linux/if_addr.h   IFA_LOCAL/IFA_ADDRESS、IFA_F_PERMANENT
// 纯声明不注 [SupportedOSPlatform]（镜像 WintunNative 策略，使布局测试跨平台可跑）。
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace P2P.Nic.Linux;

internal static class LinuxNative
{
    private const string Library = "libc";

    // ── if_tun.h（TUN 设备绑定）／sockios.h（接口索引）──
    public const uint TunSetIf = 0x400454CA; // _IOW('T', 202, int)：绑定接口名与类型
    public const short IfFTun = 0x0001;      // 三层 TUN（裸 IP 包）
    public const short IfFNoPi = 0x1000;     // 无 4B 协议信息前缀
    public const uint SiocGifIndex = 0x8933; // name -> if_index（rtnetlink 定位接口用）
    public const int IfReqSize = 40;         // sizeof(struct ifreq)，64 位 Linux
    public const int IfFlagsOffset = 16;     // ifr_name[16] 之后的联合体首字段：ifr_flags(short)/ifr_ifindex(int)

    // ── 健康探测（M2-24，FR-C-202）：sysfs 存在性 + SIOCGIFADDR 主地址 ──
    public const int AfInet = 2;             // AF_INET
    public const int SockDgram = 2;          // SOCK_DGRAM
    public const int IpProtoUdp = 17;        // IPPROTO_UDP
    public const uint SiocGifAddr = 0x8915;  // name -> ifr_addr（接口 IPv4 主地址，sockios.h）
    public const int IfAddrOffset = 16;      // ifr_name[16] 之后联合体的 ifr_addr（sockaddr_in）

    // ── netlink.h / rtnetlink.h / if_addr.h（rtnetlink 配址与拉起）──
    public const int AfNetlink = 16;         // AF_NETLINK
    public const int SockRaw = 3;            // SOCK_RAW
    public const int NetlinkRoute = 0;       // NETLINK_ROUTE
    public const int SolSocket = 1;          // SOL_SOCKET
    public const int SoRcvTimeo = 20;        // SO_RCVTIMEO
    public const int SockaddrNlSize = 12;    // sizeof(struct sockaddr_nl)
    public const ushort NlmFRequest = 0x01;
    public const ushort NlmFAck = 0x04;
    public const ushort NlmFExcl = 0x200;
    public const ushort NlmFCreate = 0x400;
    public const ushort NlmsgError = 0x2;    // ACK/错误应答消息类型
    public const ushort RtmNewLink = 16;     // 拉起接口（IFF_UP）
    public const ushort RtmNewAddr = 20;     // 配置地址
    public const ushort IfaAddress = 1;      // 前缀地址（点对点设备为对端）
    public const ushort IfaLocal = 2;        // 本地接口地址
    public const byte IfaFPermanent = 0x80;  // 地址不随链路 down 移除
    public const uint IfFUp = 0x1;           // IFF_UP
    public const int EExist = 17;            // EEXIST（地址已配置的幂等信号）
    public const int EBusy = 16;             // EBUSY（接口 fd 被其他进程占用）
    public const int EPerm = 13;             // EPERM（无 CAP_NET_ADMIN）

    /// <summary>sockaddr_nl（netlink.h）：family(2)+pad(2)+pid(4)+groups(4) = 12B。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SockaddrNl
    {
        public ushort Family;
        public ushort Pad;
        public uint Pid;
        public uint Groups;
    }

    // int socket(int domain, int type, int protocol)
    [DllImport(Library, SetLastError = true)]
    internal static extern int socket(int domain, int type, int protocol);

    // int close(int fd)
    [DllImport(Library, SetLastError = true)]
    internal static extern int close(int fd);

    // int ioctl(int fd, unsigned long request, void *argp)——TUNSETIFF 会回写 ifr_name，需双向封送
    [DllImport(Library, SetLastError = true)]
    internal static extern int ioctl(SafeFileHandle fd, uint request, [In, Out] byte[] argp);

    // ssize_t sendto(int fd, const void *buf, size_t len, int flags, const struct sockaddr_nl *to, socklen_t addrlen)
    [DllImport(Library, SetLastError = true)]
    internal static extern int sendto(int fd, [In] byte[] buf, int len, int flags, ref SockaddrNl to, int toLen);

    // ssize_t recvfrom(int fd, void *buf, size_t len, int flags, struct sockaddr_nl *from, socklen_t *addrlen)
    [DllImport(Library, SetLastError = true)]
    internal static extern int recvfrom(int fd, [Out] byte[] buf, int len, int flags, ref SockaddrNl from, ref int fromLen);

    // int setsockopt(int fd, int level, int optname, const void *optval, socklen_t optlen)
    [DllImport(Library, SetLastError = true)]
    internal static extern int setsockopt(int fd, int level, int optname, [In] byte[] optval, int optlen);

    // uid_t geteuid(void)——实机测试守护用（配址需 root/CAP_NET_ADMIN）
    [DllImport(Library)]
    internal static extern int geteuid();
}

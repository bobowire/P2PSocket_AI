// M1-20 Windows 虚拟网卡管理（FR-C-201、05 §1.2）：
// 复用同名适配器（WintunOpenAdapter）失败则创建（WintunCreateAdapter）→ 开启最小 ring 会话保活
// （会话存续期间适配器保持 up，M1 不读写 ring）→ IP Helper 写入 /32 on-link 单播地址（不依赖 netsh）。
// RemoveAsync 结束会话并关闭适配器：自建适配器随 WintunCloseAdapter 一并移除（删除干净）。
// 运行期自愈（FR-C-202，M2-24）：CheckHealth 只读探测原语（存在性+IP 一致性），
// 30s 检测循环与重建编排归客户端宿主 NicHealthMonitor（P2P.Client/Nic）；网段冲突告警（FR-C-204）属 M3。
using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace P2P.Nic.Windows;

/// <summary>本机 IPv4 单播表项（GetUnicastIpAddressTable 摘录；M3 网段冲突检测可复用）。</summary>
public readonly record struct UnicastAddress(IPAddress Address, ulong InterfaceLuid, byte PrefixLength);

[SupportedOSPlatform("windows")]
public sealed class WintunNicManager : INicManager, IAsyncDisposable
{
    /// <summary>适配器名（05 §1.2）。</summary>
    public const string AdapterName = "P2P-Tun";
    private const string TunnelType = "P2P";

    private readonly object _gate = new();
    private IntPtr _adapter;   // WINTUN_ADAPTER_HANDLE
    private IntPtr _session;   // WINTUN_SESSION_HANDLE（保活）
    private ulong _luid;       // 适配器 NET_LUID（IP Helper 定位接口）
    private IPAddress? _appliedIp;
    private NicHandle? _handle;
    private bool _createdByUs; // WintunCloseAdapter 仅对自建适配器执行移除

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
                // ① 适配器：优先复用同名遗留适配器（进程重启场景），无则创建
                _adapter = WintunNative.WintunOpenAdapter(AdapterName);
                if (_adapter == IntPtr.Zero)
                {
                    _adapter = WintunNative.WintunCreateAdapter(AdapterName, TunnelType, IntPtr.Zero);
                    if (_adapter == IntPtr.Zero)
                        throw NewWin32("WintunCreateAdapter");
                    _createdByUs = true;
                }

                // ② 会话保活：最小 ring（0x20000），仅维持 up（05 §1.2）
                WintunNative.WintunGetAdapterLUID(_adapter, out _luid);
                _session = WintunNative.WintunStartSession(_adapter, WintunNative.MinRingCapacity);
                if (_session == IntPtr.Zero)
                    throw NewWin32("WintunStartSession");

                // ③ 配址：/32 on-link 单播地址（决策 D2 独立地址空间；TD-06 本地交付）
                ApplyAddress(virtualIp);
            }
            catch (DllNotFoundException e)
            {
                Teardown();
                throw new NicException($"未找到 Wintun.dll：请随安装包置于程序目录（05 §1.2）。{e.Message}");
            }
            catch
            {
                Teardown(); // 半建状态不留脏句柄/半配置适配器
                throw;
            }

            _appliedIp = virtualIp;
            _handle = new NicHandle(AdapterName, virtualIp);
            return Task.FromResult(_handle);
        }
    }

    public Task RemoveAsync(CancellationToken ct = default)
    {
        lock (_gate) Teardown();
        return Task.CompletedTask;
    }

    /// <summary>健康探测（FR-C-202，M2-24）：WintunOpenAdapter 探存在性（零=被删除）→
    /// 该适配器 LUID 在单播地址表中的绑定与期望比对。探测句柄独立 Open/Close，不碰运行会话；
    /// 枚举失败按健康返回（探测手段不可用不触发重建）。</summary>
    public NicHealth CheckHealth(IPAddress expectedIp)
    {
        ArgumentNullException.ThrowIfNull(expectedIp);
        var adapter = WintunNative.WintunOpenAdapter(AdapterName);
        if (adapter == IntPtr.Zero)
            return new NicHealth(NicHealthState.AdapterMissing, null);
        try
        {
            WintunNative.WintunGetAdapterLUID(adapter, out var luid);
            var bound = QueryLocalAddresses().FirstOrDefault(a => a.InterfaceLuid == luid);
            if (bound.Address is null)
                return new NicHealth(NicHealthState.IpMismatch, null);
            return bound.Address.Equals(expectedIp)
                ? new NicHealth(NicHealthState.Healthy, bound.Address)
                : new NicHealth(NicHealthState.IpMismatch, bound.Address);
        }
        finally
        {
            WintunNative.WintunCloseAdapter(adapter); // Open 来源句柄：Close 仅释放不删适配器
        }
    }

    public ValueTask DisposeAsync() => new(RemoveAsync());

    private void ApplyAddress(IPAddress virtualIp)
    {
        // 幂等预检：复用的适配器可能已带该地址；若已存在则跳过创建
        if (QueryLocalAddresses().Any(a => a.InterfaceLuid == _luid && a.Address.Equals(virtualIp)))
            return;

        var row = new IpHelperNative.MibUnicastIpAddressRow();
        IpHelperNative.InitializeUnicastIpAddressEntry(ref row);
        row.Address.Family = IpHelperNative.AfInet;
        row.Address.Address = BinaryPrimitives.ReadUInt32LittleEndian(virtualIp.GetAddressBytes());
        row.InterfaceLuid = _luid;
        row.OnLinkPrefixLength = 32;

        var code = IpHelperNative.CreateUnicastIpAddressEntry(ref row);
        if (code != IpHelperNative.NoError
            && !QueryLocalAddresses().Any(a => a.InterfaceLuid == _luid && a.Address.Equals(virtualIp)))
        {
            // 与预检存在竞态时可能返回「已存在」类错误：复查表项仍缺失才视为真失败
            throw new NicException(
                $"CreateUnicastIpAddressEntry 失败：Win32 {code} {new Win32Exception(checked((int)code)).Message}");
        }
    }

    private void Teardown()
    {
        if (_session != IntPtr.Zero)
        {
            WintunNative.WintunEndSession(_session);
            _session = IntPtr.Zero;
        }
        if (_adapter != IntPtr.Zero)
        {
            // 自建适配器随句柄释放移除；复用的遗留适配器仅释放句柄（避免误删他进程资产）
            if (_createdByUs) WintunNative.WintunCloseAdapter(_adapter);
            _adapter = IntPtr.Zero;
        }
        _createdByUs = false;
        _luid = 0;
        _appliedIp = null;
        _handle = null;
    }

    private static NicException NewWin32(string api)
        => new($"{api} 失败：{new Win32Exception(Marshal.GetLastWin32Error()).Message}");

    /// <summary>本机 IPv4 单播地址枚举（GetUnicastIpAddressTable；M3 网段冲突检测可复用）。</summary>
    public static IReadOnlyList<UnicastAddress> QueryLocalAddresses()
    {
        var code = IpHelperNative.GetUnicastIpAddressTable(IpHelperNative.AfInet, out var tablePtr);
        if (code != IpHelperNative.NoError)
            return []; // 枚举失败按无信息处理：幂等预检退化为直接创建，由创建结果校验兜底
        try
        {
            var count = Marshal.ReadInt32(tablePtr);
            var addresses = new List<UnicastAddress>(count);
            var rowSize = Marshal.SizeOf<IpHelperNative.MibUnicastIpAddressRow>();
            var first = tablePtr + IpHelperNative.TableOffset;
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<IpHelperNative.MibUnicastIpAddressRow>(first + i * rowSize);
                if (row.Address.Family != IpHelperNative.AfInet) continue;
                addresses.Add(new UnicastAddress(
                    ToIpAddress(row.Address.Address), row.InterfaceLuid, row.OnLinkPrefixLength));
            }
            return addresses;
        }
        finally { IpHelperNative.FreeMibTable(tablePtr); }
    }

    private static IPAddress ToIpAddress(uint littleEndianAddress)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, littleEndianAddress);
        return new IPAddress(bytes);
    }
}

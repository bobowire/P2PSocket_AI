// M1-24 注册与向导后端（FR-C-101/102/103、05 §10、01 §3.2/§4.1）：
// - 未注册态探测（FR-C-101：决定首启是否开向导/浏览器）；
// - 向导第一步：服务端地址连通性探测（TCP 端口级）；
// - 向导第三步（确认注册）：0x10（未签名，NeedRegister 态唯一例外）→ ECIES 解密 deviceSecret
//   → 三要素 + 静态私钥持久化（FR-C-103）→ CompleteRegistration（会话转已建立）
//   → 以 RegisterAck.virtualIp 触发首次网卡创建（A-1 出口，衔接 M1-20/21 INicManager）；
// - 向导第二步分组方式：默认分组（服务端注册即入组）/ 已有账号登录绑定（0x21，A-2 双设备同账号前提）
//   / 新建账号（0x20→0x21）后建组（0x50）；邀请码选项 M2 置灰（UI 层处理）；
// - 注册完成事件：宿主（M1-30）据此开浏览器/打印引导 URL（FR-C-101）。
using System.Net;
using System.Net.Sockets;
using P2P.Client.Control;
using P2P.Client.Storage;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using P2P.Nic;

namespace P2P.Client.Registration;

/// <summary>注册上报的本机描述（0x10 载荷）。</summary>
public sealed record RegistrationOptions
{
    public string Hostname { get; init; } = Environment.MachineName;
    public string Os { get; init; } = OperatingSystem.IsWindows() ? "windows" : "linux";
    public string ClientVersion { get; init; } = "0.1.0";
}

/// <summary>注册结果（向导展示：远程码/虚拟 IP/分组）。</summary>
public sealed record RegistrationResult(
    Guid DeviceId, string RemoteCode, string VirtualIp, GroupInfo[] Groups);

/// <summary>注册与首启向导后端（与 LocalWebApi/UI 的 M1-29/32 对接）。</summary>
public sealed class ClientRegistrationService(
    ControlClient client,
    StateStore store,
    INicManager nic,
    RegistrationOptions? options = null)
{
    private readonly RegistrationOptions _options = options ?? new RegistrationOptions();

    /// <summary>注册成功（网卡已创建；FR-C-101 宿主可开浏览器/打印 URL）。</summary>
    public event Action<RegistrationResult>? RegistrationCompleted;

    /// <summary>最近一次使用的 macCode（覆盖式恢复提示用，OQ-14）。</summary>
    public string? LastMacCode { get; private set; }

    /// <summary>未注册态探测（FR-C-101：state 三要素不完整 → 首启向导）。</summary>
    public static bool NeedsWizard(ClientState state) => !state.IsRegistered;

    /// <summary>向导引导 URL（FR-C-101：127.0.0.1 本地 Web；Linux 无桌面时终端打印同此）。</summary>
    public static string WizardUrl(int localWebPort) => $"http://127.0.0.1:{localWebPort}/wizard";

    /// <summary>向导第一步：服务端连通性探测（顺序同 serverAddrs；返回首个可达项或全部失败明细）。</summary>
    public static async Task<(bool Ok, string Detail)> TestConnectivityAsync(
        IEnumerable<string> serverAddrs, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var failures = new List<string>();
        foreach (var addr in serverAddrs)
        {
            var (ok, detail) = await ProbeAsync(addr, timeout, ct);
            if (ok) return (true, detail);
            failures.Add(detail);
        }
        return (false, string.Join("；", failures));
    }

    private static async Task<(bool Ok, string Detail)> ProbeAsync(string addr, TimeSpan? timeout, CancellationToken ct)
    {
        var (host, port) = ParseHostPort(addr);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(3));
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, cts.Token);
            return (true, $"{addr} 可达");
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException)
        {
            var reason = e is OperationCanceledException ? "超时" : e.Message;
            return (false, $"{addr} 不可达（{reason}）");
        }
    }

    /// <summary>确认注册（向导第三步）。前置：会话处于 NeedRegister（未注册设备握手后停留态）。</summary>
    /// <param name="macCodeOverride">测试注入固定 macCode；默认按 05 §10 生成。</param>
    public async Task<RegistrationResult> RegisterAsync(string? macCodeOverride = null, CancellationToken ct = default)
    {
        if (client.State != ControlClientState.NeedRegister)
            throw new ControlClientException($"当前状态 {client.State} 不可注册（须为 NeedRegister）");

        var macCode = macCodeOverride ?? MacCodeProvider.Generate();
        LastMacCode = macCode;

        // 未注册设备首个 Register 不签名（02 §2.2）；staticPubKey 承载 deviceSecret 的 ECIES 下发（OQ-15）
        using var keyPair = EcKeyPair.Generate();
        var ack = await client.SendRequestAsync<RegisterAck>(new Register(
            client.NextSeq(), client.TimestampMs(), MsgType.Register,
            macCode, _options.Hostname, _options.Os, _options.ClientVersion,
            keyPair.ExportPublicKey(), null, null, null));
        var deviceSecret = Ecies.Decrypt(keyPair, ack.DeviceSecretBox);

        // 三要素 + 静态私钥持久化（FR-C-103：重启不丢失；机密经存储层保护，07 §4）
        store.State.DeviceId = ack.DeviceId;
        store.State.DeviceSecret = deviceSecret;
        store.State.StaticPrivateKey = keyPair.ExportPrivateKey();
        store.State.RemoteCode = ack.RemoteCode;
        store.State.VirtualIp = ack.VirtualIp;
        await store.SaveAsync(ct);

        // 会话转已建立（镜像服务端 CompleteRegistration 的密钥派生）
        client.CompleteRegistration(ack.DeviceId, deviceSecret);

        // 注册成功以 RegisterAck.virtualIp 触发首次网卡创建（A-1 出口；幂等，M1-20/21）
        await nic.EnsureAsync(IPAddress.Parse(ack.VirtualIp), ct);

        var result = new RegistrationResult(ack.DeviceId, ack.RemoteCode, ack.VirtualIp, ack.Groups);
        RegistrationCompleted?.Invoke(result);
        return result;
    }

    /// <summary>向导第二步·已有账号登录绑定（0x21）：设备 owner 绑定持久在库（A-2 双设备同账号前提）。</summary>
    public Task<UserLoginAck> LoginBindAsync(string username, string password, CancellationToken ct = default)
        => client.SendRequestAsync<UserLoginAck>(new UserLogin(
            client.NextSeq(), client.TimestampMs(), MsgType.UserLogin, username, password), ct);

    /// <summary>向导第二步·新建账号（0x20）：成功后仍需 <see cref="LoginBindAsync"/> 登录。</summary>
    public Task<UserRegisterAck> CreateUserAsync(string username, string password, CancellationToken ct = default)
        => client.SendRequestAsync<UserRegisterAck>(new UserRegister(
            client.NextSeq(), client.TimestampMs(), MsgType.UserRegister, username, password), ct);

    /// <summary>登录后新建分组（0x50，向导"注册账号建组"路径收尾）。</summary>
    public Task<GroupCreateAck> CreateGroupAsync(string name, JoinPolicy policy, CancellationToken ct = default)
        => client.SendRequestAsync<GroupCreateAck>(new GroupCreate(
            client.NextSeq(), client.TimestampMs(), MsgType.GroupCreate, name, policy), ct);

    private static (string Host, int Port) ParseHostPort(string addr)
    {
        if (addr.StartsWith('['))
        {
            var close = addr.IndexOf(']');
            return (addr[1..close], int.Parse(addr[(close + 2)..]));
        }
        var colon = addr.LastIndexOf(':');
        return (addr[..colon], int.Parse(addr[(colon + 1)..]));
    }
}

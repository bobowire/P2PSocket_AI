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

    /// <summary>OS 值域 windows/linux/android（M8-01，设计 13 §4.5）：协议 string 字段，
    /// 服务端无枚举校验（管理页 os 列原样展示）——android 为无卡端口映射模式新增值。</summary>
    public string Os { get; init; } = OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsAndroid() ? "android"
        : "linux";

    public string ClientVersion { get; init; } = "0.1.0";

    /// <summary>机器指纹注入缝（M8-01，设计 13 §4.4/TD-34）：Android 6+ 系统 MAC 全设备统一
    /// 02:00:00:00:00:00（可通过 IsValidMac 校验）——默认采集生成的 macCode 会全设备同值，
    /// 覆盖式恢复（OQ-14）按 macCode 判身份将互相顶号；壳注入 ANDROID_ID 派生指纹。
    /// 默认 null＝平台采集（05 §10 现状，桌面零变化）。</summary>
    public Func<string?>? MachineFingerprint { get; init; }
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

    /// <summary>账号登录绑定成功（0x21 Ack.Ok；P3 修复）：web 登录与向导登录路径均触发——
    /// 可见性前提变化（会话 OwnerUserId 就位），宿主据此补发启用映射恢复。</summary>
    public event Action<string>? LoginSucceeded;

    /// <summary>最近一次使用的 macCode（覆盖式恢复提示用，OQ-14）。</summary>
    public string? LastMacCode { get; private set; }

    /// <summary>未注册态探测（FR-C-101：state 三要素不完整 → 首启向导）。</summary>
    public static bool NeedsWizard(ClientState state) => !state.IsRegistered;

    /// <summary>向导引导 URL（FR-C-101：127.0.0.1 本地 Web；Linux 无桌面时终端打印同此）。
    /// M5-12：token 可选参——自动打开 URL 携带 token 免手工输入（07 §14.2）。</summary>
    public static string WizardUrl(int localWebPort, string? token = null)
        => $"http://127.0.0.1:{localWebPort}/wizard" + (token is null ? "" : $"?token={token}");

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
    /// <param name="deviceName">向导填写的设备名（04 §2.2）；空则用 <see cref="RegistrationOptions.Hostname"/>。</param>
    public async Task<RegistrationResult> RegisterAsync(string? macCodeOverride = null,
        string? deviceName = null, CancellationToken ct = default)
    {
        if (client.State != ControlClientState.NeedRegister)
            throw new ControlClientException($"当前状态 {client.State} 不可注册（须为 NeedRegister）");

        // M8-01 指纹注入（设计 13 §4.4/TD-34）：注入指纹**优先**——Android 枚举 MAC 全设备同值，
        // 回退位序会被击穿（FromFingerprint 注记）；无注入源=平台采集现状
        var macCode = macCodeOverride ?? MacCodeProvider.FromFingerprint(_options.MachineFingerprint);
        LastMacCode = macCode;

        // 未注册设备首个 Register 不签名（02 §2.2）；staticPubKey 承载 deviceSecret 的 ECIES 下发（OQ-15）
        // 设备名回落链（v1.58，FR-S-107）：向导输入 → 已改名持久值（凭据恢复重注册不洗名）→ Hostname 默认
        var name = deviceName ?? store.State.DeviceName ?? _options.Hostname;
        using var keyPair = EcKeyPair.Generate();
        var ack = await client.SendRequestAsync<RegisterAck>(seq => new Register(
            seq, client.TimestampMs(), MsgType.Register,
            macCode, name, _options.Os, _options.ClientVersion,
            keyPair.ExportPublicKey(), null, null, null));
        var deviceSecret = Ecies.Decrypt(keyPair, ack.DeviceSecretBox);

        // 三要素 + 静态私钥持久化（FR-C-103：重启不丢失；机密经存储层保护，07 §4）
        store.State.DeviceId = ack.DeviceId;
        store.State.DeviceSecret = deviceSecret;
        store.State.StaticPrivateKey = keyPair.ExportPrivateKey();
        store.State.RemoteCode = ack.RemoteCode;
        store.State.VirtualIp = ack.VirtualIp;
        store.State.DeviceName = name;
        await store.SaveAsync(ct);

        // 会话转已建立（镜像服务端 CompleteRegistration 的密钥派生）
        client.CompleteRegistration(ack.DeviceId, deviceSecret);

        // 注册成功以 RegisterAck.virtualIp 触发首次网卡创建（A-1 出口；幂等，M1-20/21）
        await nic.EnsureAsync(IPAddress.Parse(ack.VirtualIp), ct);

        var result = new RegistrationResult(ack.DeviceId, ack.RemoteCode, ack.VirtualIp, ack.Groups);
        RegistrationCompleted?.Invoke(result);
        return result;
    }

    /// <summary>向导第二步·已有账号登录绑定（0x21）：设备 owner 绑定持久在库（A-2 双设备同账号前提）。
    /// P1 修复：0x21 属主动类（02 §2.5），登出后的 passive 会话本地拒发 2002——先强制重连
    /// （新会话初始 Normal）再发，落地 05 §324"重新登录须重连后再发"意图。
    /// P2 方案 a（2026-10-11 批复）：成功即记住凭据（重启自动重放），登出忘记。</summary>
    public async Task<UserLoginAck> LoginBindAsync(string username, string password, CancellationToken ct = default)
    {
        if (client.Capability == CapabilityMode.Passive)
            await client.ReconnectAsync(ct);
        // M5-06/SEC-26：密码现加密经 passwordBox（07 §10.2）；旧服务端无 loginEphPub 时
        // BoxLoginSecret 本地拒绝——记住我重放沿「发送异常保留凭据」语义（下次重连再试）
        var ack = await client.SendRequestAsync<UserLoginAck>(seq => new UserLogin(
            seq, client.TimestampMs(), MsgType.UserLogin, username, "", client.BoxLoginSecret(password)), ct);
        if (ack.Ok)
        {
            await RememberCredentialsAsync(username, password, ct);
            LoginSucceeded?.Invoke(username);
        }
        return ack;
    }

    /// <summary>记住账号凭据（P2 方案 a）：密码经 ISecretProtector 落盘（07 §4，与 deviceSecret 同级）。
    /// 登录成功与改密成功两入口（改密更新重放凭据，防重启后旧密码失败）。</summary>
    public async Task RememberCredentialsAsync(string username, string password, CancellationToken ct = default)
    {
        store.State.RememberedUsername = username;
        store.State.RememberedPassword = password;
        await store.SaveAsync(ct);
    }

    /// <summary>忘记账号凭据（登出语义=P2 方案 a 的"登出即忘记"）。</summary>
    public async Task ForgetCredentialsAsync(CancellationToken ct = default)
    {
        store.State.RememberedUsername = null;
        store.State.RememberedPassword = null;
        await store.SaveAsync(ct);
    }

    /// <summary>向导第二步·新建账号（0x20）：成功后仍需 <see cref="LoginBindAsync"/> 登录。
    /// M5-06/SEC-26：密码经 passwordBox 现加密（同 0x21 封装口径）。</summary>
    public Task<UserRegisterAck> CreateUserAsync(string username, string password, CancellationToken ct = default)
        => client.SendRequestAsync<UserRegisterAck>(seq => new UserRegister(
            seq, client.TimestampMs(), MsgType.UserRegister, username, "", client.BoxLoginSecret(password)), ct);

    /// <summary>登录后新建分组（0x50，向导"注册账号建组"路径收尾）。</summary>
    public Task<GroupCreateAck> CreateGroupAsync(string name, JoinPolicy policy, CancellationToken ct = default)
        => client.SendRequestAsync<GroupCreateAck>(seq => new GroupCreate(
            seq, client.TimestampMs(), MsgType.GroupCreate, name, policy), ct);

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

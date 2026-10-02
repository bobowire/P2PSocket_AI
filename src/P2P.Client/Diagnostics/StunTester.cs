// M3-15 stun-test RFC5780 子集判型（05 §7.2、04 §2.6、FR-C-808）：
// 客户端本地编排多次 Binding 对比判型——
//   UDP mapping：主/辅两探（同 socket 换目标）比对映射端点（同=EIM 锥形 / 异=ADM·APDM Symmetric 族）；
//   UDP filtering：CHANGE-REQUEST(changeIP|changePort) 从辅端点回包能收=EIF FullCone（比对
//     RESPONSE-ORIGIN==学到的 OTHER-ADDRESS，防服务端忽略 change 请求的假阳性）；
//     收不到=ADF·APDF Restricted 族；
//   TCP 变体：映射端口分配规律（3 新连接 deltas 全 +1 → sequential=端口预测适用，05 §3.2）；
//     同本地端口 L 重绑连主/辅比对（异=port-dependent）。
// 编排顺序（关键）：探主 → filtering（CHANGE-REQUEST）→ 探辅——探辅会把辅端点加入 NAT
// contacted 集，Restricted 族将放行 change 回包产生假 EIF，故 filtering 必须先于探辅。
// 单公网 IP 降级（stun_alt_addr 未配置→响应无 OTHER-ADDRESS）：UdpMapping/UdpFiltering 均
// null+downgraded=true（UDP 判型两维都依赖辅目标；TCP 分配规律不依赖辅仍可判——05 §7.2
// "仅 mapping 行为可判"即指 TCP 变体属映射分配规律）。
// 同 IP 辅地址：change 回包源 IP 已在 contacted 集（探主时联系过主），EIF/ADF 细分不可测——
// 观测值如实返回+注记；集成测试以回环别名（主 127.0.0.1/辅 127.0.0.2）构造异 IP 世界。
// 委托注入（端点/凭据惰性取）：装配时设备可能未注册，解析推迟到调用期。
using System.Net;
using System.Net.Sockets;
using P2P.Core.Crypto;
using P2P.Core.Protocol;
using P2P.Core.Stun;
using P2P.Core.Utils;

namespace P2P.Client.Diagnostics;

/// <summary>stun-test 判型结果视图（04 §2.6；IPEndPoint 以字符串承载——System.Text.Json 无内置转换）。</summary>
public sealed record StunTestView(
    string PublicEndpoint,
    string? UdpMapping,
    string? UdpFiltering,
    bool? TcpSequential,
    bool? TcpPortDependent,
    bool Downgraded,
    string[] Notes,
    long DurationMs);

/// <summary>stun-test 判型失败（04 §5 码表：未注册 1002/探测失败 1001）。</summary>
public sealed class StunTestException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>STUN 探测凭据三件套（运行时 state.json 惰性快照；ClockSync 未校准 offset=0 亦可用——
/// ts 窗口 ±120s 系统时钟常态在窗内，OQ-12）。</summary>
public sealed record StunCredentials(Guid DeviceId, byte[] DeviceSecret, ClockSync Clock);

/// <summary>RFC5780 子集判型器（05 §7.2）。udp/tcp 端点 lookup 分离——测试世界可只接其一。</summary>
public sealed class StunTester(
    Func<IPEndPoint?> udpEndpointLookup,
    Func<IPEndPoint?> tcpEndpointLookup,
    Func<StunCredentials?> credentialsLookup,
    IPAddress? bindAddress = null,
    TimeProvider? time = null)
{
    private static readonly TimeSpan UdpAttemptTimeout = TimeSpan.FromSeconds(2);
    private const int UdpAttempts = 2;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<StunTestView> RunAsync(CancellationToken ct = default)
    {
        var started = _time.GetTimestamp();
        var creds = credentialsLookup()
            ?? throw new StunTestException(ErrorCode.NotFound, "设备未注册：stun-test 须先完成注册向导");

        var notes = new List<string>();
        string? udpMapping = null, udpFiltering = null, publicEndpoint = "";
        bool? tcpSequential = null, tcpPortDependent = null;
        var downgraded = false;

        var udpEp = udpEndpointLookup();
        if (udpEp is null)
            notes.Add("UDP STUN 端点不可用：UDP 判型未测");
        else
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(bindAddress ?? IPAddress.Any, 0)); // 全程单 socket：三次事务同一 NAT 映射视角

            var primary = await ExchangeAsync(socket, udpEp, creds, changeFlags: null, ct).ConfigureAwait(false)
                ?? throw new StunTestException(ErrorCode.BadRequest, $"STUN 探测失败：{udpEp} 连续 {UdpAttempts} 次无有效响应");
            publicEndpoint = primary.Mapped.ToString();
            var alt = primary.OtherAddress;

            if (alt is null)
            {
                downgraded = true;
                notes.Add($"服务端未通告辅端点（stun_alt_addr 未配置）：UDP mapping/filtering 不可判（05 §7.2 单公网 IP 降级）；TCP 分配规律仍可判");
            }
            else
            {
                // ① filtering 先于探辅（contacted 集污染防假 EIF，类注释）
                var change = await ExchangeAsync(socket, udpEp, creds,
                    StunCodec.ChangeIpFlag | StunCodec.ChangePortFlag, ct).ConfigureAwait(false);
                udpFiltering = ClassifyFiltering(change, alt);
                if (change is not null && udpFiltering is null)
                    notes.Add($"CHANGE-REQUEST 回包源 {change.ResponseOrigin} ≠ 学到的 OTHER-ADDRESS {alt}：filtering 不可判（服务端未按 change 切换回包源）");
                if (alt.Address.Equals(udpEp.Address))
                    notes.Add($"辅端点 {alt} 与主端点同 IP：EIF/ADF（IP 维 filtering）不可细分——change 回包源 IP 已在 NAT contacted 集");

                // ② 探辅 → mapping 判型
                var secondary = await ExchangeAsync(socket, alt, creds, changeFlags: null, ct).ConfigureAwait(false);
                udpMapping = ClassifyMapping(primary.Mapped, secondary?.Mapped);
                if (secondary is null)
                    notes.Add($"辅端点 {alt} 探测失败：mapping 不可判");
            }
        }

        var tcpEp = tcpEndpointLookup();
        if (tcpEp is null)
            notes.Add("TCP STUN 端点不可用：TCP 变体未测");
        else
        {
            var ports = new List<int>();
            IPEndPoint? tcpAlt = null;
            int reusePort = 0;
            try
            {
                for (var i = 0; i < 3; i++)
                {
                    var local = NewTcpProbeSocket(bindAddress);
                    if (i == 0)
                    {
                        reusePort = ((IPEndPoint)local.LocalEndPoint!).Port;
                        var first = await StunTcpProber.ProbeFullAsync(local, tcpEp, creds.DeviceId,
                            creds.DeviceSecret, creds.Clock, _time, ct: ct).ConfigureAwait(false);
                        tcpAlt = first.OtherAddress; // TCP 响应同带两属性（服务端 M3-15）
                        ports.Add(first.Mapped.Port);
                        continue;
                    }
                    ports.Add((await StunTcpProber.ProbeFullAsync(local, tcpEp, creds.DeviceId,
                        creds.DeviceSecret, creds.Clock, _time, ct: ct).ConfigureAwait(false)).Mapped.Port);
                }
                tcpSequential = ports[1] - ports[0] == 1 && ports[2] - ports[1] == 1;

                if (tcpAlt is not null)
                {
                    // 同本地端口 L 重绑（SO_REUSEADDR，TD-10 端口保留复用同模式）连主/辅比对
                    var viaPrimary = await StunTcpProber.ProbeFullAsync(NewTcpProbeSocket(bindAddress, reusePort),
                        tcpEp, creds.DeviceId, creds.DeviceSecret, creds.Clock, _time, ct: ct).ConfigureAwait(false);
                    var viaAlt = await StunTcpProber.ProbeFullAsync(NewTcpProbeSocket(bindAddress, reusePort),
                        tcpAlt, creds.DeviceId, creds.DeviceSecret, creds.Clock, _time, ct: ct).ConfigureAwait(false);
                    tcpPortDependent = viaPrimary.Mapped.Port != viaAlt.Mapped.Port;
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                notes.Add($"TCP 探测失败：{ex.Message}（已判定项保留）");
            }
        }

        return new StunTestView(publicEndpoint, udpMapping, udpFiltering, tcpSequential, tcpPortDependent,
            downgraded, [.. notes], (long)_time.GetElapsedTime(started).TotalMilliseconds);
    }

    /// <summary>单端点 Binding 事务（2 次尝试×2s）：接收任意源响应（filtering 的 change 回包来自
    /// 辅端点、探辅响应亦非主端点），无关包丢弃；全部尝试耗尽返回 null——filtering 场景超时本身
    /// 即判据（ADF·APDF）而非错误。</summary>
    private async Task<StunCodec.BindingResponse?> ExchangeAsync(
        Socket socket, IPEndPoint to, StunCredentials creds, ushort? changeFlags, CancellationToken ct)
    {
        for (var attempt = 0; attempt < UdpAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var tid = StunCodec.NewTransactionId();
            var request = StunCodec.BuildBindingRequest(tid, creds.DeviceId, creds.DeviceSecret,
                creds.Clock.NowRemoteMs(_time), RandomGenerator.Bytes(16), changeFlags);
            try { await socket.SendToAsync(request, SocketFlags.None, to, ct).ConfigureAwait(false); }
            catch (SocketException) { continue; } // ICMP 端口不可达：本轮作废重试

            var deadline = _time.GetLocalNow() + UdpAttemptTimeout;
            while (_time.GetLocalNow() < deadline)
            {
                using var tryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                tryCts.CancelAfter(deadline - _time.GetLocalNow());
                var buf = new byte[StunCodec.HeaderLen + 128];
                SocketReceiveFromResult received;
                try
                {
                    received = await socket.ReceiveFromAsync(buf, SocketFlags.None,
                        new IPEndPoint(IPAddress.Any, 0), tryCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
                catch (SocketException) { break; }
                if (StunCodec.TryParseBindingResponse(buf.AsSpan(0, received.ReceivedBytes), out var response)
                    && response!.TransactionId.AsSpan().SequenceEqual(tid))
                    return response;
                // 无关数据报：丢弃继续等
            }
        }
        return null;
    }

    private static Socket NewTcpProbeSocket(IPAddress? bindAddress, int port = 0)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        // RST 关闭（读毕即弃）：同端口重连判定与首连接构成相同四元组——优雅关闭后服务端侧
        // TIME_WAIT TCB 会间歇丢弃新 SYN（新 ISN 落旧接收窗口内即判旧重复段），3s 探测超时
        // 使 port-dependent 判定随机丢失；RST 终结双向 TCB 不留 TIME_WAIT，重连确定性成立。
        socket.LingerState = new LingerOption(true, 0);
        socket.Bind(new IPEndPoint(bindAddress ?? IPAddress.Any, port));
        return socket;
    }

    // ── 判型纯函数（单测覆盖点，05 §7.2 两桶族口径）──────────────────────

    /// <summary>mapping 判型：两探映射端点相同=EIM（Endpoint-Independent Mapping，锥形）；
    /// 不同=ADM·APDM 合并桶（Symmetric 族——端口预测难度同域，本项目不细分）。</summary>
    internal static string? ClassifyMapping(IPEndPoint? primary, IPEndPoint? secondary)
        => primary is null || secondary is null ? null : primary.Equals(secondary) ? "eim" : "adm_or_apdm";

    /// <summary>filtering 判型：change 回包未达=ADF·APDF 合并桶（Restricted 族）；回包源==学到的
    /// OTHER-ADDRESS=EIF（FullCone）；回包源不符=不可判（服务端未切换——null 由调用方注记）。</summary>
    internal static string? ClassifyFiltering(StunCodec.BindingResponse? change, IPEndPoint learnedAlt)
        => change is null ? "adf_or_apdf"
            : change.ResponseOrigin is not null && change.ResponseOrigin.Equals(learnedAlt) ? "eif"
            : null;
}

using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using P2P.Core.Stun;
using P2P.Server.Data;

namespace P2P.Server.Services;

/// <summary>
/// STUN-R UDP 服务（02 §3、05 §7，FR-S-601）：3478 单 socket 收发循环，
/// DEVICE-AUTH 校验（HMAC → ts ±120s → nonce 去重 5min）→ XOR-MAPPED-ADDRESS 回包。
/// 校验不通过一律静默丢弃（UDP 无连接，不回应即无回显放大）。
/// STUN over TCP（FR-S-602）与四道闸风暴防护（TD-18/FR-S-603）→ M2。
/// </summary>
public sealed class StunService : IAsyncDisposable
{
    public const int DefaultPort = 3478;
    public static readonly TimeSpan TsWindow = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan NonceTtl = TimeSpan.FromMinutes(5);

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly bool _requireAuth; // stun_auth=0 仅测试环境关闭（05 §7）
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _cts = new();
    private readonly System.Threading.Lock _nonceGate = new();
    private readonly Dictionary<string, long> _nonces = []; // nonce(b64) → 过期时刻 ticks
    private readonly Task _sweeper;
    private UdpClient _udp = null!;
    private Task _loop = Task.CompletedTask;
    private int _port;
    private int _disposed; // 宿主 StopAsync 与容器释放各调一次（幂等）

    public StunService(IDbContextFactory<AppDbContext> dbFactory, bool requireAuth = true, TimeProvider? time = null)
    {
        _dbFactory = dbFactory;
        _requireAuth = requireAuth;
        _time = time ?? TimeProvider.System;
        _sweeper = SweepAsync(_cts.Token);
    }

    /// <summary>实际绑定端口（0 → 系统分配，测试用）。</summary>
    public int Port => _port;

    public Task StartAsync(int port = DefaultPort, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_cts.IsCancellationRequested, this);
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        _port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        _loop = ReceiveLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var datagram = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
                await HandleAsync(datagram).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* 停机 */ }
        catch (ObjectDisposedException) { /* 停机 */ }
        catch (SocketException) { /* 停机 */ }
    }

    private async Task HandleAsync(UdpReceiveResult datagram)
    {
        var wire = datagram.Buffer;
        if (wire.Length < StunCodec.HeaderLen) return;

        if (_requireAuth)
        {
            if (!StunCodec.TryParseDeviceAuth(wire, LookupSecret)) return; // 结构/未注册/HMAC 任一不符：静默（02 §3.2）
            var auth = StunCodec.ParseDeviceAuthFields(wire)!;

            // ts 窗口 ±120s（客户端以控制连接 offset 校准后组包，OQ-12）
            var nowMs = _time.GetLocalNow().ToUnixTimeMilliseconds();
            var drift = nowMs > (long)auth.TsMs ? nowMs - (long)auth.TsMs : (long)auth.TsMs - nowMs;
            if (drift > TsWindow.TotalMilliseconds) return;

            // nonce 去重 5min（防重放；transactionId 由 HMAC 绑定无法替换）
            if (!TryAddNonce(auth.Nonce)) return;
        }

        var response = StunCodec.BuildBindingResponse(
            wire.AsSpan(8, StunCodec.TransactionIdLen),
            datagram.RemoteEndPoint.Address,
            (ushort)datagram.RemoteEndPoint.Port);
        try { await _udp.SendAsync(response, datagram.RemoteEndPoint).ConfigureAwait(false); }
        catch (SocketException) { /* 对端口已换：丢弃 */ }
    }

    /// <summary>查表先于验签（05 §7 闸②语义：未注册 deviceId 直接丢，无 HMAC 消耗）。</summary>
    private byte[]? LookupSecret(Guid deviceId)
    {
        using var db = _dbFactory.CreateDbContext();
        return db.Devices.AsNoTracking()
            .Where(d => d.Id == deviceId && !d.Disabled)
            .Select(d => d.DeviceSecret)
            .SingleOrDefault();
    }

    // ── nonce 缓存（容量上限 + 周期清扫，防重放窗口 5min）─────────────

    private bool TryAddNonce(byte[] nonce)
    {
        var key = Convert.ToBase64String(nonce);
        var now = _time.GetLocalNow().UtcTicks;
        lock (_nonceGate)
        {
            if (_nonces.TryGetValue(key, out var expiry) && expiry > now) return false; // 5min 内重放
            _nonces[key] = now + NonceTtl.Ticks;
            if (_nonces.Count > 65_536) // 容量兜底：清扫过期项（正常速率远达不到）
                _nonces.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList()
                    .ForEach(k => _nonces.Remove(k));
            return true;
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), _time);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var now = _time.GetLocalNow().UtcTicks;
                lock (_nonceGate)
                {
                    foreach (var key in _nonces.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
                        _nonces.Remove(key);
                }
            }
        }
        catch (OperationCanceledException) { /* 停机 */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await _cts.CancelAsync().ConfigureAwait(false);
        _udp?.Close();
        try { await _loop.ConfigureAwait(false); } catch { }
        try { await _sweeper.ConfigureAwait(false); } catch { }
        _udp?.Dispose();
        _cts.Dispose();
    }
}

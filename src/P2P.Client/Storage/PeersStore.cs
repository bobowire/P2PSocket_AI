// M2-23 peers.json 目标设备级配置存储（03 §5、04 §2.4、D3 v0.4/OQ-10）：
// - 形态：{ targetDeviceId → { relayFallback, punchMode } }，本机对每个目标设备一份、该设备下全部映射共用；
// - relayFallback 默认关闭（PRD 06 §2）：无条目/无文件 = false（打洞失败不回退中继）；
// - punchMode（M7-01，设计 12 §3 决策①）：设备级打洞优先模式，配置层值域 tcp/kcp、默认 tcp——
//   打洞协议决策权上收设备级（映射 proto 不再决定打洞）；Load 容错缺字段/非法值（"TCP"/"udp"/"xxx"
//   ——配置层无 udp 值，防口径混入）单条回 tcp，与非法键丢弃同口径不整体作废；
// - 单写者（信号量串行）+ 临时文件原子替换（05 §0 纪律 4，同 state.json 口径）；
//   写入统一走 SetAsync 部分更新合并（null=保持现值——单字段写不丢另一字段，设计 12 §3.3）；
// - 损坏自愈重建默认 + <see cref="Recovered"/> 告警（任务清单 M2-23；条目可由设备页随时重设，
//   与 settings 的拒启语义不同——不阻断启动）；
// - 不经控制协议、不同步服务端（OQ-10：回退是访问方本地决策输入；punchMode 同为本地真相）。
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace P2P.Client.Storage;

/// <summary>peers.json 条目内存形态（M7-01，设计 12 §3.1）：PunchMode 规范化存 "tcp"/"kcp"（配置层口径）。</summary>
public sealed record PeerConfig(bool RelayFallback, string PunchMode);

public sealed class PeersStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, // 03 §5 文件字段小驼峰
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 本机文件：deviceId 键为十六进制字面量
    };

    private readonly string _path;
    private readonly SemaphoreSlim _writeGate = new(1, 1); // 单写者：读无锁（快照不可变字典）
    private IReadOnlyDictionary<Guid, PeerConfig> _entries = new Dictionary<Guid, PeerConfig>();

    /// <summary>损坏自愈告警（重建默认）。宿主转日志/Web 展示（同 StateStore.Recovered 口径）。</summary>
    public event Action<string>? Recovered;

    public string PeersFilePath => _path;

    public PeersStore(string baseDir)
        => _path = Path.Combine(baseDir, ClientPaths.PeersFileName);

    /// <summary>加载（幂等）：文件缺失=默认（回退全关/tcp）；损坏=自愈默认+告警（不抛异常，允许进程继续）。</summary>
    public void Load()
    {
        CleanupStaleTmp();
        if (!File.Exists(_path))
            return; // 缺失即默认：无条目 → GetRelayFallback 恒 false / GetPunchMode 恒 tcp

        try
        {
            var dto = JsonSerializer.Deserialize<Dictionary<string, PeerConfigDto>>(File.ReadAllText(_path), JsonOptions)
                ?? throw new JsonException("空文档");
            var parsed = new Dictionary<Guid, PeerConfig>();
            foreach (var (key, value) in dto)
            {
                if (Guid.TryParse(key, out var deviceId))
                    parsed[deviceId] = new PeerConfig(value?.RelayFallback ?? false, NormalizePunchMode(value?.PunchMode));
                // 非法键（手改文件）丢弃：自愈语义容错单条损坏，不整体作废
            }
            _entries = parsed;
        }
        catch (Exception e) when (e is JsonException or FormatException or IOException)
        {
            // 自愈（任务清单 M2-23）：开关可在设备页随时重设，重建默认不阻断启动
            _entries = new Dictionary<Guid, PeerConfig>();
            Recovered?.Invoke($"peers.json 损坏（{e.GetType().Name}: {e.Message}），已重建为默认（全部目标设备回退关闭）");
        }
    }

    /// <summary>目标设备级回退配置（04 §2.4；无条目=默认关闭，PRD 06 §2）。
    /// Puncher 出队执行时与 PunchRequestAck.relayAllowed 合成 RelayAllowed（05 §3.1，消费方 M2-18）。</summary>
    public bool GetRelayFallback(Guid targetDeviceId)
        => _entries.TryGetValue(targetDeviceId, out var value) && value.RelayFallback;

    /// <summary>目标设备级打洞优先模式（M7-01，设计 12 §3.2 决策①默认 tcp；配置层口径 tcp/kcp——
    /// 执行层映射 kcp→udp 在 PunchModePlanner 出队时单向完成）。无条目/无文件 = "tcp"。</summary>
    public string GetPunchMode(Guid targetDeviceId)
        => _entries.TryGetValue(targetDeviceId, out var value) ? value.PunchMode : "tcp";

    /// <summary>统一合并写（M7-01，设计 12 §3.3 本地 Web PUT /api/peers/{deviceId} 唯一写入口）：
    /// null = 保持现值（部分更新语义，同 SettingsApi PUT 口径）——单字段写不丢另一字段；
    /// punchMode 提供且非 tcp/kcp 抛 <see cref="ArgumentException"/>（由 API 层转 Api.Fail=1001，
    /// 04 §5 口径；"udp" 同样拒绝——配置层值域无 udp，设计 12 §2.1）。</summary>
    public async Task SetAsync(Guid targetDeviceId, bool? relayFallback = null, string? punchMode = null,
        CancellationToken ct = default)
    {
        if (punchMode is not null && punchMode is not ("tcp" or "kcp"))
            throw new ArgumentException($"punchMode 须为 tcp|kcp（得 {punchMode}；配置层值域见设计 12 §2.1）",
                nameof(punchMode));

        await _writeGate.WaitAsync(ct);
        try
        {
            var current = _entries.TryGetValue(targetDeviceId, out var existing)
                ? existing : new PeerConfig(false, "tcp"); // 无条目=默认（false/tcp）
            var next = new Dictionary<Guid, PeerConfig>(_entries)
            {
                [targetDeviceId] = new PeerConfig(
                    relayFallback ?? current.RelayFallback,
                    punchMode ?? current.PunchMode),
            };
            var dto = next.ToDictionary(kv => kv.Key.ToString(),
                kv => new PeerConfigDto { RelayFallback = kv.Value.RelayFallback, PunchMode = kv.Value.PunchMode });
            await AtomicFile.WriteAllBytesAsync(_path,
                JsonSerializer.SerializeToUtf8Bytes(dto, JsonOptions), ct);
            _entries = next; // 落盘成功才换快照（失败不污染内存）
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>配置层 punchMode 规范化（设计 12 §3.2）：null/非法值（手改文件，如 "TCP"/"udp"/"xxx"）
    /// → "tcp"（默认）；合法值原样。值域只有 tcp/kcp——"udp" 属执行层口径，不因手改混入配置层。</summary>
    private static string NormalizePunchMode(string? punchMode)
        => punchMode is "tcp" or "kcp" ? punchMode : "tcp";

    /// <summary>清理上次中断写入残留的 .tmp（原子替换语义保证对终文件无影响）。</summary>
    private void CleanupStaleTmp()
    {
        var tmp = _path + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
    }

    /// <summary>条目落盘形态（03 §5：{ relayFallback, punchMode } 双字段，M7-01）。</summary>
    private sealed class PeerConfigDto
    {
        public bool RelayFallback { get; set; }
        public string? PunchMode { get; set; } // null=旧文件缺字段（Load 容错回 tcp）
    }
}

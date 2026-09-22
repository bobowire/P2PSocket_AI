// M2-23 peers.json 目标设备级配置存储（03 §5、04 §2.4、D3 v0.4/OQ-10）：
// - 形态：{ targetDeviceId → { relayFallback } }，本机对每个目标设备一份、该设备下全部映射共用；
// - 默认关闭（PRD 06 §2）：无条目/无文件 = relayFallback false（打洞失败不回退中继）；
// - 单写者（信号量串行）+ 临时文件原子替换（05 §0 纪律 4，同 state.json 口径）；
// - 损坏自愈重建默认 + <see cref="Recovered"/> 告警（任务清单 M2-23；条目可由设备页随时重设，
//   与 settings 的拒启语义不同——不阻断启动）；
// - 不经控制协议、不同步服务端（OQ-10：回退是访问方本地决策输入）。
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace P2P.Client.Storage;

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
    private IReadOnlyDictionary<Guid, bool> _entries = new Dictionary<Guid, bool>();

    /// <summary>损坏自愈告警（重建默认）。宿主转日志/Web 展示（同 StateStore.Recovered 口径）。</summary>
    public event Action<string>? Recovered;

    public string PeersFilePath => _path;

    public PeersStore(string baseDir)
        => _path = Path.Combine(baseDir, ClientPaths.PeersFileName);

    /// <summary>加载（幂等）：文件缺失=默认（全部关闭）；损坏=自愈默认+告警（不抛异常，允许进程继续）。</summary>
    public void Load()
    {
        CleanupStaleTmp();
        if (!File.Exists(_path))
            return; // 缺失即默认：无条目 → GetRelayFallback 恒 false

        try
        {
            var dto = JsonSerializer.Deserialize<Dictionary<string, PeerConfigDto>>(File.ReadAllText(_path), JsonOptions)
                ?? throw new JsonException("空文档");
            var parsed = new Dictionary<Guid, bool>();
            foreach (var (key, value) in dto)
            {
                if (Guid.TryParse(key, out var deviceId))
                    parsed[deviceId] = value?.RelayFallback ?? false;
                // 非法键（手改文件）丢弃：自愈语义容错单条损坏，不整体作废
            }
            _entries = parsed;
        }
        catch (Exception e) when (e is JsonException or FormatException or IOException)
        {
            // 自愈（任务清单 M2-23）：开关可在设备页随时重设，重建默认不阻断启动
            _entries = new Dictionary<Guid, bool>();
            Recovered?.Invoke($"peers.json 损坏（{e.GetType().Name}: {e.Message}），已重建为默认（全部目标设备回退关闭）");
        }
    }

    /// <summary>目标设备级回退配置（04 §2.4；无条目=默认关闭，PRD 06 §2）。
    /// Puncher 出队执行时与 PunchRequestAck.relayAllowed 合成 RelayAllowed（05 §3.1，消费方 M2-18）。</summary>
    public bool GetRelayFallback(Guid targetDeviceId)
        => _entries.TryGetValue(targetDeviceId, out var value) && value;

    /// <summary>设置并原子落盘（本地 Web PUT /api/peers/{deviceId} 唯一写入口）。</summary>
    public async Task SetRelayFallbackAsync(Guid targetDeviceId, bool relayFallback, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            var next = new Dictionary<Guid, bool>(_entries) { [targetDeviceId] = relayFallback };
            var dto = next.ToDictionary(kv => kv.Key.ToString(), kv => new PeerConfigDto { RelayFallback = kv.Value });
            await AtomicFile.WriteAllBytesAsync(_path,
                JsonSerializer.SerializeToUtf8Bytes(dto, JsonOptions), ct);
            _entries = next; // 落盘成功才换快照（失败不污染内存）
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>清理上次中断写入残留的 .tmp（原子替换语义保证对终文件无影响）。</summary>
    private void CleanupStaleTmp()
    {
        var tmp = _path + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
    }

    /// <summary>条目落盘形态（03 §5：{ relayFallback }）。</summary>
    private sealed class PeerConfigDto
    {
        public bool RelayFallback { get; set; }
    }
}

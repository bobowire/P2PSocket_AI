// M2-11 lan-segments.json 内网段白名单本地镜像（03 §5、05 §2.5、SEC-53）：
// - 形态：[{ segmentId, cidr, enabled }]——本机作为**目标设备**时向他方开放的可访问地址段；
// - 与服务端 lan_segments 表经 0x63 同步（本地 API 写入后上报，M2-27 挂接）；
// - MappingEngine 目标侧连接 targetAddr 前本地校验（SEC-52 双保险第二道）：enabled 段 CIDR
//   覆盖才放行——服务端 0x60/0x70 双路径校验（第一道）之后的最后一道防线；
// - 单写者（信号量串行）+ 临时文件原子替换 + 损坏自愈重建默认 + <see cref="Recovered"/> 告警
//   （同 peers.json 口径：段可在设备页随时重设，不阻断启动）。
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace P2P.Client.Storage;

public sealed class LanSegmentsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, // 03 §5 文件字段小驼峰
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 本机文件：cidr 为 ASCII 字面量
    };

    /// <summary>条目（与 0x63 字段一一对应；SegmentId=服务端权威 id）。</summary>
    public sealed record LanSegmentEntry(Guid SegmentId, string Cidr, bool Enabled);

    private readonly string _path;
    private readonly SemaphoreSlim _writeGate = new(1, 1); // 单写者：读无锁（快照不可变列表）
    private IReadOnlyList<LanSegmentEntry> _entries = [];

    /// <summary>损坏自愈告警（重建默认空集）。宿主转日志/Web 展示（同 PeersStore.Recovered 口径）。</summary>
    public event Action<string>? Recovered;

    public string LanSegmentsFilePath => _path;

    public LanSegmentsStore(string baseDir)
        => _path = Path.Combine(baseDir, ClientPaths.LanSegmentsFileName);

    /// <summary>加载（幂等）：文件缺失=默认（无段 → 非 self 目标一律拒绝，fail closed）；
    /// 损坏=自愈默认+告警（不抛异常，允许进程继续）。</summary>
    public void Load()
    {
        CleanupStaleTmp();
        if (!File.Exists(_path))
            return; // 缺失即默认：白名单未配置，MappingEngine 本地校验恒不覆盖

        try
        {
            var dto = JsonSerializer.Deserialize<List<LanSegmentDto>>(File.ReadAllText(_path), JsonOptions)
                ?? throw new JsonException("空文档");
            _entries = dto
                .Select(e => Guid.TryParse(e?.SegmentId, out var sid) && !string.IsNullOrWhiteSpace(e?.Cidr)
                    ? new LanSegmentEntry(sid, e!.Cidr.Trim(), e.Enabled)
                    : null) // 非法条目（手改文件）丢弃：自愈语义容错单条损坏，不整体作废
                .OfType<LanSegmentEntry>()
                .ToList();
        }
        catch (Exception e) when (e is JsonException or FormatException or IOException)
        {
            // 自愈：段可在设备页随时重设（M2-27 API 重上报），重建默认不阻断启动
            _entries = [];
            Recovered?.Invoke($"lan-segments.json 损坏（{e.GetType().Name}: {e.Message}），已重建为默认（无开放段）");
        }
    }

    /// <summary>enabled 段 CIDR 快照（MappingEngine SEC-52 校验读入口；空集=fail closed）。</summary>
    public IReadOnlyList<string> EnabledCidrs()
        => _entries.Where(e => e.Enabled).Select(e => e.Cidr).ToList();

    /// <summary>全量条目快照（本地 API /api/lan-segments 读入口，M2-27；含 segmentId 供删除定位）。</summary>
    public IReadOnlyList<LanSegmentEntry> Entries() => _entries;

    /// <summary>全量替换并原子落盘（0x63 同步与本地 API 的统一写入口）。</summary>
    public async Task ReplaceAllAsync(IEnumerable<LanSegmentEntry> entries, CancellationToken ct = default)
    {
        var next = entries.ToList();
        await _writeGate.WaitAsync(ct);
        try
        {
            var dto = next.Select(e => new LanSegmentDto
            {
                SegmentId = e.SegmentId.ToString(),
                Cidr = e.Cidr,
                Enabled = e.Enabled,
            }).ToList();
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

    /// <summary>条目落盘形态（03 §5：{ segmentId, cidr, enabled }）。</summary>
    private sealed class LanSegmentDto
    {
        public string SegmentId { get; set; } = "";
        public string Cidr { get; set; } = "";
        public bool Enabled { get; set; }
    }
}

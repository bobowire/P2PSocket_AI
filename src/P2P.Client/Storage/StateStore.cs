// M1-22 state.json 单写者存储（03 §5、任务清单 M1-22）：
// - 单写者：全部写入经本实例串行（信号量），宿主只装配一个实例；
// - 原子替换：经 AtomicFile（临时文件+落盘+替换）；
// - 损坏自愈：JSON 解析失败/机密还原失败 → 重建默认（未注册态）+ <see cref="Recovered"/> 告警事件，
//   后续走覆盖式恢复重新注册（OQ-14：macCode 命中离线记录即可找回身份）；
// - 机密字段在序列化边界经 ISecretProtector 加密（07 §4/SEC-23）。
using System.IO;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace P2P.Client.Storage;

public sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, // 03 §5 文件字段小驼峰
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 本机文件：base64 保持字面量（可读可 grep）
    };

    private readonly string _path;
    private readonly ISecretProtector _protector;
    private readonly SemaphoreSlim _writeGate = new(1, 1); // 单写者：写串行化，读无锁（宿主内同步协调）

    /// <summary>损坏自愈告警（任务清单 M1-22：重建默认+告警事件）。宿主（M1-30）转日志/Web 展示。</summary>
    public event Action<string>? Recovered;

    public ClientState State { get; private set; } = new();
    public string StateFilePath => _path;

    /// <param name="baseDir">状态目录（测试/集成注入临时目录；默认 <see cref="ClientPaths.DefaultBaseDir"/>）。</param>
    /// <param name="protector">机密保护器；空则按平台默认（Windows=DPAPI，Linux=明文+0600）。</param>
    public StateStore(string baseDir, ISecretProtector? protector = null)
    {
        _path = Path.Combine(baseDir, ClientPaths.StateFileName);
        _protector = protector ?? SecretProtectorFactory.Create();
    }

    /// <summary>加载（幂等）：文件缺失=未注册默认态；损坏=自愈默认+告警（不抛异常，允许进程继续）。</summary>
    public void Load()
    {
        CleanupStaleTmp();
        if (!File.Exists(_path))
        {
            State = new ClientState();
            return;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<StateFileDto>(File.ReadAllText(_path), JsonOptions)
                ?? throw new JsonException("空文档");
            State = new ClientState
            {
                DeviceId = dto.DeviceId,
                DeviceSecret = dto.DeviceSecretBox is null ? null : _protector.Unprotect(Convert.FromBase64String(dto.DeviceSecretBox)),
                StaticPrivateKey = dto.StaticPrivateKeyBox is null ? null : _protector.Unprotect(Convert.FromBase64String(dto.StaticPrivateKeyBox)),
                RemoteCode = dto.RemoteCode,
                VirtualIp = dto.VirtualIp,
                Mappings = dto.Mappings ?? [],
            };
        }
        catch (Exception e) when (e is JsonException or FormatException or CryptographicException)
        {
            // 自愈（03 §5/任务清单）：state 为机器生成数据，重建后凭 macCode 覆盖式恢复即可（OQ-14）
            State = new ClientState();
            Recovered?.Invoke(
                $"state.json 损坏（{e.GetType().Name}: {e.Message}），已重建为默认（未注册）状态；" +
                "如需找回原设备身份，请凭 MAC 识别码重新注册（离线方可覆盖恢复，OQ-14）");
        }
    }

    /// <summary>保存当前 <see cref="State"/>（原子替换；单写者串行）。未变更机密每次重加密（DPAPI 非确定性，无碰撞风险）。</summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            var dto = new StateFileDto
            {
                DeviceId = State.DeviceId,
                DeviceSecretBox = State.DeviceSecret is null ? null : Convert.ToBase64String(_protector.Protect(State.DeviceSecret)),
                StaticPrivateKeyBox = State.StaticPrivateKey is null ? null : Convert.ToBase64String(_protector.Protect(State.StaticPrivateKey)),
                RemoteCode = State.RemoteCode,
                VirtualIp = State.VirtualIp,
                Mappings = State.Mappings.Count == 0 ? null : State.Mappings, // WhenWritingNull 省略空表
            };
            await AtomicFile.WriteAllBytesAsync(_path,
                JsonSerializer.SerializeToUtf8Bytes(dto, JsonOptions), ct);
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>清理上次中断写入残留的 .tmp（对终文件无影响，原子替换语义保证）。</summary>
    private void CleanupStaleTmp()
    {
        var tmp = _path + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
    }

    /// <summary>state.json 落盘形态（03 §5 字段；机密为保护后 base64，07 §4）。</summary>
    private sealed class StateFileDto
    {
        public Guid? DeviceId { get; set; }
        public string? DeviceSecretBox { get; set; }
        public string? StaticPrivateKeyBox { get; set; }
        public string? RemoteCode { get; set; }
        public string? VirtualIp { get; set; }
        public List<StoredMapping>? Mappings { get; set; }
    }
}

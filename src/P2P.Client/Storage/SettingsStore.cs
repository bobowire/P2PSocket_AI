// M1-22 settings.json 存储与启动校验（08 §5.2、NFR-35、任务清单 M1-22）：
// - 文件缺失 → 默认值（首启向导写入前的合法状态，serverAddrs 空）；
// - 解析失败/校验失败 → ConfigValidationException 拒绝启动（NFR-35：字段名+合法范围+修复建议，
//   不允许带病静默运行；「本地 Web 端口合法时 Web 展示错误页」由宿主 M1-30 处理）；
//   —— 与 state.json 的损坏自愈不同：settings 含用户手工维护的服务端地址，静默清空等同丢失配置；
// - 写入（本地 Web 可改，04 §2.1）走同一原子替换与校验。
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace P2P.Client.Storage;

/// <summary>配置校验失败（NFR-35 拒启诊断）。Message 为汇总，<see cref="Errors"/> 逐条列出字段与修复建议。</summary>
public sealed class ConfigValidationException(string message, IReadOnlyList<string> errors) : Exception(message)
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, // 08 §5.2 文件字段小驼峰
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 本机文件：无需 HTML 防御转义
    };

    private readonly string _path;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public ClientSettings Settings { get; private set; } = new();
    public string SettingsFilePath => _path;

    public SettingsStore(string baseDir)
        => _path = Path.Combine(baseDir, ClientPaths.SettingsFileName);

    /// <summary>加载：缺失=默认；解析/校验失败=抛 <see cref="ConfigValidationException"/>（NFR-35 拒启）。</summary>
    public void Load()
    {
        CleanupStaleTmp();
        if (!File.Exists(_path))
        {
            Settings = new ClientSettings(); // 首启前合法状态（向导负责写入，08 §5.2）
            return;
        }

        ClientSettings parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(_path), JsonOptions)
                ?? throw new JsonException("空文档");
        }
        catch (JsonException e)
        {
            throw NewReject($"settings.json 解析失败：{e.Message}", "请对照 08 §5.2 修正 JSON 语法后重启");
        }
        parsed.Reconnect ??= new ReconnectSettings();

        var errors = ClientSettingsValidator.Validate(parsed);
        if (errors.Count > 0)
            throw NewReject("settings.json 校验失败（NFR-35：拒绝带病启动）", string.Join("；", errors));

        Settings = parsed;
    }

    /// <summary>保存（本地 Web 修改配置，04 §2.1）：先校验后落盘，非法值不写盘。</summary>
    public async Task SaveAsync(ClientSettings settings, CancellationToken ct = default)
    {
        var errors = ClientSettingsValidator.Validate(settings);
        if (errors.Count > 0)
            throw NewReject("settings 保存被拒绝（非法值不落盘）", string.Join("；", errors));

        await _writeGate.WaitAsync(ct);
        try
        {
            Settings = settings;
            await AtomicFile.WriteAllBytesAsync(_path,
                JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions), ct);
        }
        finally { _writeGate.Release(); }
    }

    private ConfigValidationException NewReject(string summary, string detail)
        => new($"{summary}。{detail}", [detail]);

    private void CleanupStaleTmp()
    {
        var tmp = _path + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
    }
}

/// <summary>启动配置校验（NFR-35；范围依据 08 §5.2 与 OQ-1 决议）。</summary>
public static class ClientSettingsValidator
{
    public static IReadOnlyList<string> Validate(ClientSettings s)
    {
        var errors = new List<string>();

        if (s.ServerAddrs is null)
        {
            errors.Add("serverAddrs：缺失（首启向导前允许为空数组，但字段须存在）");
        }
        else
        {
            for (var i = 0; i < s.ServerAddrs.Length; i++)
            {
                var addr = s.ServerAddrs[i];
                if (!TryParseHostPort(addr, out var host, out var port))
                    errors.Add($"serverAddrs[{i}]：\"{addr}\" 不是合法的 host:port（端口 1~65535；IPv6 需方括号，如 [2001:db8::1]:7000）");
                else if (port is < 1 or > 65535)
                    errors.Add($"serverAddrs[{i}]：端口 {port} 越界（合法 1~65535）");
                else if (string.IsNullOrWhiteSpace(host))
                    errors.Add($"serverAddrs[{i}]：主机名为空");
            }
        }

        if (s.LocalWebPort is < 1 or > 65535)
            errors.Add($"localWebPort：{s.LocalWebPort} 越界（合法 1~65535，默认 7100）");

        if (s.PunchConcurrency is < 1 or > 5)
            errors.Add($"punchConcurrency：{s.PunchConcurrency} 越界（合法 1~5，OQ-1 决议，默认 3）");

        if (s.KeepaliveSec < 1)
            errors.Add($"keepaliveSec：{s.KeepaliveSec} 非法（须 ≥1，默认 20）");

        if (s.Reconnect is null)
        {
            errors.Add("reconnect：缺失（合法 minSec 1~maxSec，默认 1→30）");
        }
        else
        {
            if (s.Reconnect.MinSec < 1)
                errors.Add($"reconnect.minSec：{s.Reconnect.MinSec} 非法（须 ≥1）");
            if (s.Reconnect.MaxSec < s.Reconnect.MinSec)
                errors.Add($"reconnect.maxSec：{s.Reconnect.MaxSec} 不能小于 minSec（{s.Reconnect.MinSec}）");
        }

        return errors;
    }

    /// <summary>解析 host:port（IPv6 须方括号形式）。端口合法范围由调用方校验（此处仅切分）。</summary>
    private static bool TryParseHostPort(string value, out string host, out int port)
    {
        host = "";
        port = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;

        if (value.StartsWith('['))
        {
            // IPv6 字面量：[addr]:port
            var close = value.IndexOf(']');
            if (close < 0 || close + 1 >= value.Length || value[close + 1] != ':') return false;
            host = value[1..close];
            return int.TryParse(value[(close + 2)..], out port) && host.Contains(':');
        }

        var colon = value.LastIndexOf(':');
        if (colon <= 0 || colon == value.Length - 1) return false; // 无端口/只有端口/空主机
        if (value.AsSpan(0, colon).Contains(":", StringComparison.Ordinal)) return false; // 裸 IPv6 必须加方括号
        host = value[..colon];
        return int.TryParse(value[(colon + 1)..], out port);
    }
}

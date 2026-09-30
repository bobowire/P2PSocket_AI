// M2-27 日志端点（04 §2.6、NFR-51）：
// - 读 Serilog 滚动文件（Program.cs：{baseDir}/logs/client-YYYYMMDD.log，10MB×14 保留）纯文本行——
//   默认输出模板 `{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}`（非 JSON），
//   按行前缀判条目边界，续行（异常堆栈等）归前一条；
// - 文件序=时间序（文件名日期倒序读 + 文件内倒序），条目解析总量封顶 5 万防全量载入；
// - GET /api/logs?level=&page= 分页（page 1 起、pageSize 200；level 精确类别过滤）；
// - GET /api/logs/export?level= 下载 text/plain 全量（时间正序）；
// - 纯本地读取：无协议往返、passive 无关；目录缺失=空结果（未落盘前可查询）。
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using P2P.Core.Protocol;

namespace P2P.Client.Web;

public static partial class LogsApi
{
    /// <summary>Serilog 默认文件模板行首（时间 + [三级级别缩写]）。</summary>
    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}[.,]\d{3}(?: [+-]\d{2}:?\d{2})?) \[(DBG|INF|WRN|ERR|FTL)\] (.*)$")]
    private static partial Regex LinePrefixRegex();

    private const int PageSize = 200;
    private const int MaxEntries = 50_000; // 解析封顶（14 天 × 10MB 上界的保护性子集）

    /// <summary>UI 查询参数 → Serilog 三字符级别缩写（不识别=不过滤）。</summary>
    private static readonly Dictionary<string, string> LevelTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["debug"] = "DBG", ["dbg"] = "DBG",
        ["info"] = "INF", ["inf"] = "INF", ["information"] = "INF",
        ["warn"] = "WRN", ["warning"] = "WRN", ["wrn"] = "WRN",
        ["error"] = "ERR", ["err"] = "ERR",
        ["fatal"] = "FTL", ["ftl"] = "FTL",
    };

    public static IEndpointRouteBuilder MapLogsApi(this IEndpointRouteBuilder app, string logsDir)
    {
        // 分页查询：newest-first（文件名日期倒序 + 文件内倒序）
        app.MapGet("/api/logs", (string? level, int? page) =>
        {
            try
            {
                var token = LevelFilter(level);
                var entries = ReadEntries(logsDir); // newest-first
                if (token is not null)
                    entries = entries.Where(e => e.Level == token).ToList();
                var total = entries.Count;
                var pageNo = Math.Max(1, page ?? 1);
                var items = entries.Skip((pageNo - 1) * PageSize).Take(PageSize + 1).ToList();
                return Api.Ok(new LogPageView(
                    items.Take(PageSize).Select(e => new LogEntryView(e.Ts, e.Level, e.Message)).ToArray(),
                    pageNo, PageSize, total, items.Count > PageSize));
            }
            catch (Exception e) { return Api.Fail(e); }
        });

        // 导出：text/plain 附件，时间正序全量（过滤同 GET）
        app.MapGet("/api/logs/export", (string? level) =>
        {
            try
            {
                var token = LevelFilter(level);
                var entries = ReadEntries(logsDir);
                if (token is not null)
                    entries = entries.Where(e => e.Level == token).ToList();
                var sb = new StringBuilder();
                foreach (var e in Enumerable.Reverse(entries))
                    sb.Append(e.Ts).Append(" [").Append(e.Level).Append("] ").AppendLine(e.Message);
                return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/plain; charset=utf-8",
                    $"p2p-logs-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            }
            catch (Exception e) { return Api.Fail(e); }
        });

        return app;
    }

    private static string? LevelFilter(string? level)
        => level is null || level.Length == 0 ? null
        : LevelTokens.GetValueOrDefault(level)
          ?? throw new ApiException(ErrorCode.BadRequest, $"level 须为 debug/info/warning/error/fatal（得 {level}）");

    /// <summary>滚动文件读取（newest-first）：client-YYYYMMDD.log 文件名倒序 + 文件内行序倒序；
    /// 续行归前条（异常堆栈）；总量封顶 <see cref="MaxEntries"/>。</summary>
    private static List<LogEntry> ReadEntries(string logsDir)
    {
        var result = new List<LogEntry>();
        if (!Directory.Exists(logsDir))
            return result;
        var regex = LinePrefixRegex();
        foreach (var file in Directory.EnumerateFiles(logsDir, "client-*.log").OrderByDescending(f => f))
        {
            // 单文件条目正序聚合，最后整体倒序并入（续行语义要求先正序归并）
            var fileEntries = new List<LogEntry>();
            LogEntry? current = null;
            foreach (var line in File.ReadLines(file))
            {
                var m = regex.Match(line);
                if (m.Success)
                {
                    current = new LogEntry(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
                    fileEntries.Add(current);
                }
                else if (current is not null && line.Length > 0)
                    current.AppendContinuation(line); // 堆栈/多行消息归前条
            }
            for (var i = fileEntries.Count - 1; i >= 0; i--)
                result.Add(fileEntries[i]);
            if (result.Count >= MaxEntries)
            {
                result.RemoveRange(MaxEntries, result.Count - MaxEntries);
                break;
            }
        }
        return result;
    }

    /// <summary>单条日志（解析中间态；Message 含续行换行）。</summary>
    private sealed class LogEntry(string ts, string level, string message)
    {
        public string Ts { get; } = ts;
        public string Level { get; } = level;
        public string Message { get; private set; } = message;

        public void AppendContinuation(string line) => Message += "\n" + line;
    }
}

// ── 展示 DTO（export-ts 单一事实源）──────────────────────────────

public sealed record LogEntryView(string Ts, string Level, string Message);

public sealed record LogPageView(LogEntryView[] Items, int Page, int PageSize, int Total, bool HasMore);

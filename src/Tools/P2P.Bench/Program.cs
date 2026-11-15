// M4-05 基准 harness 主入口（NFR-11/13/14、09 §3、TD-24 L1）。
// 用法：dotnet run --project src/Tools/P2P.Bench -c Release [-- all|matrix|punch-latency|throughput|rtt|concurrency|devices]
//      （逗号分隔多选；默认 all）——输出 out/benchmark/*.csv + summary.md（不进 CI 常规轮，见 README.md）。
using System.Reflection;
using P2P.Bench;

var only = args.FirstOrDefault() ?? "all";
var selection = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
bool Want(string name) => selection.Contains("all") || selection.Contains(name);
var outDir = Path.Combine(FindRepoRoot(), "out", "benchmark");
Console.WriteLine($"P2P.Bench L1 本地自动化基准（TD-24：本机回环口径，吞吐/延迟≠真实网络性能）");
Console.WriteLine($"输出目录：{outDir}");
Console.WriteLine($"选择：{string.Join(",", selection)}\n");

// 环境信息进 summary（M4-06 报告环境规格数据源）
var env = new Dictionary<string, string>
{
    ["timestamp"] = DateTimeOffset.UtcNow.ToString("u"),
    ["machine"] = Environment.MachineName,
    ["os"] = Environment.OSVersion.VersionString,
    ["processorCount"] = Environment.ProcessorCount.ToString(),
    ["dotnet"] = Environment.Version.ToString(),
    ["appVersion"] = BenchAppVersion.Informational,
};

var summary = new List<string>
{
    "# P2P L1 本地自动化基准 summary（机器可读汇总；报告成文=M4-06）",
    "",
    "> 口径：in-proc 服务端+双客户端+NatSimulator 全链本机回环（TD-24 L1）。吞吐/延迟为本机基线",
    "> 测量（≠真实网络性能）；打洞矩阵为 NAT 模拟语义确定性结果（与真实 NAT 行为一致性归协议设计）。",
    "",
    "## 环境",
    "",
};
foreach (var (k, v) in env) summary.Add($"- {k}: {v}");
summary.Add("");

var csvs = new List<Csv>();
{
    // ⑥资源采样全程覆盖（500ms 周期；各基准段叠加即全运行剖面）
    var resources = new Csv("resources.csv",
        "elapsed_ms", "cpu_pct", "working_set_mb", "private_mb", "handles", "threads");
    csvs.Add(resources);
    using var sampler = new ResourceSampler(resources);

    if (Want("matrix"))
    {
        Console.WriteLine("── ① 打洞成功率矩阵（UDP 27 格 + TCP 20 格，每格独立世界）──");
        var matrix = new Csv("punch-matrix.csv",
            "proto", "nat_a", "nat_b", "n", "state", "elapsed_ms", "punch_stats");
        csvs.Add(matrix);
        await PunchMatrixBench.RunMatrixAsync(matrix, outDir);
        SummarizeMatrix(matrix, summary);
        await WriteAllAsync(csvs, outDir); // 分段落盘——中断亦保留已完成段
    }

    if (Want("punch-latency"))
    {
        Console.WriteLine("── ② 打洞建立耗时（direct 50 + relay 50 独立世界样本）──");
        var latency = new Csv("punch-latency.csv", "target", "trial", "state", "elapsed_ms");
        csvs.Add(latency);
        await PunchMatrixBench.RunLatencyAsync(latency, "direct", samples: 50);
        await PunchMatrixBench.RunLatencyAsync(latency, "relay", samples: 50);
        SummarizeLatency(latency, summary);
        await WriteAllAsync(csvs, outDir);
    }

    if (Want("throughput"))
    {
        Console.WriteLine("── ③ 单隧道吞吐本机回环基线（裸/direct-udp/direct-tcp/relay-fallback × 1流/4流/小包）──");
        var throughput = new Csv("throughput.csv",
            "scenario", "streams", "chunk_bytes", "total_bytes", "elapsed_ms", "mbps");
        csvs.Add(throughput);
        await ThroughputBench.RunAsync(throughput);
        SummarizeThroughput(throughput, summary);
        await WriteAllAsync(csvs, outDir);
    }

    if (Want("rtt"))
    {
        Console.WriteLine("── ④ 新增延迟（直连/中继 PTP PING + 隧道/中继/裸回环 echo，NFR-11 分别测量）──");
        var ping = new Csv("rtt-ping.csv", "scenario", "trial", "min_ms", "avg_ms", "max_ms", "received", "via_relay");
        var rtt = new Csv("rtt-echo.csv", "scenario", "trial", "ms");
        csvs.Add(ping);
        csvs.Add(rtt);
        await RttBench.RunAsync(ping, rtt);
        SummarizeRtt(ping, rtt, summary);
        await WriteAllAsync(csvs, outDir);
    }

    if (Want("concurrency"))
    {
        Console.WriteLine("── ⑤ 并发映射与 channel 复用（8 映射并发 + 100 channel）──");
        var concurrency = new Csv("concurrency.csv",
            "scenario", "streams", "per_stream_bytes", "total_bytes", "establish_ms", "total_ms", "mbps");
        csvs.Add(concurrency);
        await ThroughputBench.RunConcurrencyAsync(concurrency);
        SummarizeConcurrency(concurrency, summary);
        await WriteAllAsync(csvs, outDir);
    }

    if (Want("kcp"))
    {
        // M6-07 KCP 承载基准（设计 11 §12 决议⑥；NET-77/TD-29）
        Console.WriteLine("── ⑧ KCP 承载四组对照（0% loss；性能闸=吞吐比≥70% 且 PING 增量≤5ms）──");
        var kcpFour = new Csv("kcp-four.csv",
            "group", "tier", "chunk", "streams", "total_bytes", "elapsed_ms", "mbps", "cpu_pct", "delivered_bytes");
        var kcpSetup = new Csv("kcp-setup.csv", "group", "trial", "establish_ms");
        var kcpPing = new Csv("kcp-ping.csv", "group", "trial", "ms");
        csvs.Add(kcpFour);
        csvs.Add(kcpSetup);
        csvs.Add(kcpPing);
        await KcpBearerBench.RunFourGroupsAsync(kcpFour, kcpSetup, kcpPing);
        SummarizeKcpFour(kcpFour, kcpPing, summary);
        await WriteAllAsync(csvs, outDir); // 分段落盘

        Console.WriteLine("── ⑨ KCP 调参矩阵（Nodelay×Resend×Interval×nc，24 cell）──");
        var kcpMatrix = new Csv("kcp-matrix.csv",
            "nodelay", "resend", "interval_ms", "nc", "total_bytes", "elapsed_ms", "mbps");
        csvs.Add(kcpMatrix);
        await KcpBearerBench.RunTuningMatrixAsync(kcpMatrix);
        SummarizeKcpMatrix(kcpMatrix, summary);
        await WriteAllAsync(csvs, outDir);

        Console.WriteLine("── ⑩ KCP 丢包-吞吐曲线（0~30%，完整性恒断言；高丢包段不设绝对吞吐门禁——决议⑥）──");
        var kcpLoss = new Csv("kcp-loss.csv", "loss_pct", "total_bytes", "elapsed_ms", "mbps", "sim_dropped");
        csvs.Add(kcpLoss);
        await KcpBearerBench.RunLossCurveAsync(kcpLoss);
        SummarizeKcpLoss(kcpLoss, summary);
        await WriteAllAsync(csvs, outDir);
    }

    if (Want("devices"))
    {
        Console.WriteLine("── ⑦ 服务端并发设备数（200/500 注册+稳态心跳，in-proc 标注）──");
        var devices = new Csv("device-scale.csv",
            "tier", "devices", "batch", "register_ms", "per_dev_ms", "established",
            "window_s", "cpu_pct", "ws_start_mb", "ws_end_mb");
        csvs.Add(devices);
        await DeviceScaleBench.RunAsync(devices, [200, 500]);
        SummarizeDevices(devices, summary);
        await WriteAllAsync(csvs, outDir);
    }
}

await WriteAllAsync(csvs, outDir);
await File.WriteAllLinesAsync(Path.Combine(outDir, "summary.md"), summary);
Console.WriteLine($"\n完成：{csvs.Count} CSV + summary.md → {outDir}");

// ── summary 统计段 ─────────────────────────────────────────────────────

static void SummarizeMatrix(Csv matrix, List<string> summary)
{
    var rows = Rows(matrix);
    var byTransport = rows.GroupBy(r => r[0]);
    summary.Add("## ① 打洞成功率矩阵");
    summary.Add("");
    summary.Add("| transport | 组合 | direct | relay | failed | timeout | 合计 |");
    summary.Add("|---|---|---|---|---|---|---|");
    foreach (var transport in byTransport)
    {
        var cells = transport.GroupBy(r => $"{r[1]}×{r[2]}").Select(g =>
        {
            var c = new Dictionary<string, int>();
            foreach (var r in g) c[r[4]] = c.GetValueOrDefault(r[4]) + 1;
            return (Combo: g.Key, Count: g.Count(), States: c);
        }).OrderBy(x => x.Combo);
        foreach (var c in cells)
        {
            var d = c.States.GetValueOrDefault("direct", 0);
            var rel = c.States.GetValueOrDefault("relay", 0);
            var f = c.States.GetValueOrDefault("failed", 0);
            var t = c.States.GetValueOrDefault("timeout", 0);
            summary.Add($"| {transport.Key} | {c.Combo} | {d} | {rel} | {f} | {t} | {c.Count} |");
        }
    }
    summary.Add("");
}

static void SummarizeLatency(Csv latency, List<string> summary)
{
    summary.Add("## ② 打洞建立耗时分布（enable→终态，50 样本/目标）");
    summary.Add("");
    summary.Add("| target | 终态分布 | min | p50 | p95 | max | avg(ms) |");
    summary.Add("|---|---|---|---|---|---|---|");
    foreach (var g in Rows(latency).GroupBy(r => r[0]))
    {
        var states = string.Join(";", g.GroupBy(r => r[2]).Select(s => $"{s.Key}={s.Count()}"));
        var ms = g.Select(r => long.Parse(r[3])).ToList();
        var st = new DurationStats(ms);
        summary.Add($"| {g.Key} | {states} | {st.Min} | {st.P50} | {st.P95} | {st.Max} | {st.Avg:F0} |");
    }
    summary.Add("");
}

static void SummarizeThroughput(Csv throughput, List<string> summary)
{
    summary.Add("## ③ 吞吐本机回环基线（L1 标注：≠真实网络性能）");
    summary.Add("");
    summary.Add("| scenario | streams | chunk | total(MB) | elapsed(ms) | Mbps |");
    summary.Add("|---|---|---|---|---|---|");
    foreach (var r in Rows(throughput))
        summary.Add($"| {r[0]} | {r[1]} | {r[2]} | {long.Parse(r[3]) / 1048576.0:F0} | {r[4]} | {r[5]} |");
    summary.Add("");
}

static void SummarizeRtt(Csv ping, Csv rtt, List<string> summary)
{
    summary.Add("## ④ 新增延迟（回环亚毫秒基线，差值=封装/加密/分帧相对开销）");
    summary.Add("");
    summary.Add("| scenario | samples | min | p50 | p95 | max | avg(ms) |");
    summary.Add("|---|---|---|---|---|---|---|");
    var pings = Rows(ping).Where(r => int.Parse(r[5]) > 0).GroupBy(r => r[0]).OrderBy(g => g.Key);
    foreach (var g in pings)
    {
        var st = new DurationStats(g.Select(r => (long)Math.Round(double.Parse(r[3]))).ToList());
        summary.Add($"| {g.Key}(API avg) | {st.Count} | {st.Min} | {st.P50} | {st.P95} | {st.Max} | {st.Avg:F2} |");
    }
    foreach (var g in Rows(rtt).GroupBy(r => r[0]))
    {
        // echo 行=Stopwatch µs 精度小数 ms（回环亚毫秒）；分位双精度取值、表内统一 3 位小数呈现
        var ms = g.Select(r => double.Parse(r[2], System.Globalization.CultureInfo.InvariantCulture)).OrderBy(x => x).ToList();
        double Pct(double p) => ms[Math.Min(ms.Count - 1, (int)(ms.Count * p))];
        summary.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "| {0} | {1} | {2:F3} | {3:F3} | {4:F3} | {5:F3} | {6:F3} |",
            g.Key, ms.Count, ms[0], Pct(0.5), Pct(0.95), ms[^1], ms.Average()));
    }
    summary.Add("");
}

static void SummarizeConcurrency(Csv concurrency, List<string> summary)
{
    summary.Add("## ⑤ 并发映射与 channel 复用");
    summary.Add("");
    summary.Add("| scenario | streams | per-stream(MB) | total(MB) | 建立到齐(ms) | 全量完成(ms) | Mbps |");
    summary.Add("|---|---|---|---|---|---|---|");
    foreach (var r in Rows(concurrency))
        summary.Add($"| {r[0]} | {r[1]} | {long.Parse(r[2]) / 1048576.0:F0} | {long.Parse(r[3]) / 1048576.0:F0} | {r[4]} | {r[5]} | {r[6]} |");
    summary.Add("");
}

static void SummarizeKcpFour(Csv four, Csv ping, List<string> summary)
{
    summary.Add("## ⑧ KCP 承载四组对照（0% loss 回环；L1 标注 TD-24：≠真实网络）");
    summary.Add("");
    summary.Add("| group | tier | chunk | streams | total(MB) | elapsed(ms) | Mbps | CPU% | delivered% |");
    summary.Add("|---|---|---|---|---|---|---|---|---|");
    foreach (var r in Rows(four))
    {
        var deliveredPct = r.Length > 8 && long.TryParse(r[8], out var d)
            ? $"{d * 100.0 / long.Parse(r[4]):F1}" : "100";
        summary.Add($"| {r[0]} | {r[1]} | {r[2]} | {r[3]} | {long.Parse(r[4]) / 1048576.0:F0} | {r[5]} | {r[6]} | {r[7]} | {deliveredPct} |");
    }
    summary.Add("");
    summary.Add("| group | PING p50(ms) | p95(ms) | max(ms) | samples |");
    summary.Add("|---|---|---|---|---|");
    var p50ByGroup = new Dictionary<string, double>();
    foreach (var g in Rows(ping).GroupBy(r => r[0]))
    {
        var ms = g.Select(r => double.Parse(r[2], System.Globalization.CultureInfo.InvariantCulture))
            .OrderBy(x => x).ToList();
        p50ByGroup[g.Key] = ms[Math.Min(ms.Count - 1, (int)(ms.Count * 0.5))];
        summary.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "| {0} | {1:F3} | {2:F3} | {3:F3} | {4} |",
            g.Key, p50ByGroup[g.Key], ms[Math.Min(ms.Count - 1, (int)(ms.Count * 0.95))], ms[^1], ms.Count));
    }
    summary.Add("");

    // 性能闸（决议⑥：0% loss KCP 吞吐 ≥ 裸 UDP 70% 且 PING RTT 增量 ≤5ms；不达线回调参矩阵→移植核对层）
    const double ThroughputGate = 0.70;
    const double PingGateMs = 5.0;
    var mbps = Rows(four).Where(r => r[1] == "1ch-big")
        .ToDictionary(r => r[0], r => double.Parse(r[6], System.Globalization.CultureInfo.InvariantCulture));
    if (mbps.TryGetValue("udp-raw", out var raw) && mbps.TryGetValue("kcp-nc1", out var kcp) && raw > 0)
    {
        var ratio = kcp / raw;
        var delta = p50ByGroup.GetValueOrDefault("kcp-nc1") - p50ByGroup.GetValueOrDefault("udp-raw");
        var pass = ratio >= ThroughputGate && delta <= PingGateMs;
        summary.Add($"**性能闸判定：吞吐比 kcp-nc1/udp-raw = {ratio:P1}（门 ≥{ThroughputGate:P0}），"
                    + $"PING p50 增量 = {delta:F3}ms（门 ≤{PingGateMs}ms）→ "
                    + (pass ? "**PASS**" : "**FAIL**（回调参矩阵→移植核对层）") + "**");
        summary.Add("");
    }
}

static void SummarizeKcpMatrix(Csv matrix, List<string> summary)
{
    summary.Add("## ⑨ KCP 调参矩阵（Nodelay×Resend×Interval×nc；1ch 1368B×8MB@0% loss）");
    summary.Add("");
    summary.Add("| nodelay | resend | interval_ms | nc | elapsed(ms) | Mbps |");
    summary.Add("|---|---|---|---|---|---|");
    foreach (var r in Rows(matrix))
        summary.Add($"| {r[0]} | {r[1]} | {r[2]} | {r[3]} | {r[5]} | {r[6]} |");
    var best = Rows(matrix).OrderByDescending(r =>
        double.Parse(r[6], System.Globalization.CultureInfo.InvariantCulture)).FirstOrDefault();
    if (best is not null)
        summary.Add("");
    if (best is not null)
        summary.Add($"**推荐参数（最高吞吐 cell）**：Nodelay={best[0]} Resend={best[1]} "
                    + $"Interval={best[2]}ms nc={best[3]} → {best[6]}Mbps（回填设计 11 §5.4）");
    summary.Add("");
}

static void SummarizeKcpLoss(Csv loss, List<string> summary)
{
    summary.Add("## ⑩ KCP 丢包-吞吐曲线（1ch 1368B×8MB；完整性恒成立；裸 UDP 在 loss 下无完整性语义）");
    summary.Add("");
    summary.Add("| loss% | elapsed(ms) | Mbps | sim 丢包数 |");
    summary.Add("|---|---|---|---|");
    foreach (var r in Rows(loss))
        summary.Add($"| {r[0]} | {r[2]} | {r[3]} | {r[4]} |");
    summary.Add("");
    summary.Add("> 高丢包段不设绝对吞吐门禁（决议⑥）；持续 >20%=网络不可用，职责归 KEEPALIVE 3×20s 断链。");
    summary.Add("");
}

static void SummarizeDevices(Csv devices, List<string> summary)
{
    summary.Add("## ⑦ 服务端并发设备（in-proc：服务端+设备模拟同进程，真实部署拆进程另计）");
    summary.Add("");
    summary.Add("| tier | 设备数 | 注册(ms) | 单设备(ms) | established | 稳态CPU% | WS(MB) |");
    summary.Add("|---|---|---|---|---|---|---|");
    foreach (var r in Rows(devices))
        summary.Add($"| {r[0]} | {r[1]} | {r[3]} | {r[4]} | {r[5]} | {r[7]} | {r[8]}→{r[9]} |");
    summary.Add("");
}

// Csv 内部行只写不读——summary 需要回读；以对象数组原值重取（Row 的参数即原始值）。
static List<string[]> Rows(Csv csv) => csv.RowsAsString();

static async Task WriteAllAsync(List<Csv> csvs, string dir)
{
    foreach (var csv in csvs) await csv.WriteAsync(dir);
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AI-P2P.sln")))
        dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}

/// <summary>程序集 informational 版本（Directory.Build.props 注入；报告环境段数据源）。</summary>
internal static class BenchAppVersion
{
    public static string Informational { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";
}

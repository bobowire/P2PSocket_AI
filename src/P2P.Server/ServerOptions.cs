using System.Net;

namespace P2P.Server;

/// <summary>
/// 服务端进程级配置（08 §5.1，与 03 §2.8 分工：端口/路径在文件，业务开关在库）。
/// 启动校验（NFR-35）：非法值拒绝启动并输出「字段名 + 合法范围 + 修复建议」。
/// </summary>
public sealed class ServerOptions
{
    public const int MinPort = 1;
    public const int MaxPort = 65535;

    public ListenSection Listen { get; set; } = new();
    public DatabaseSection Database { get; set; } = new();
    public HeartbeatSection Heartbeat { get; set; } = new();
    public PunchSection Punch { get; set; } = new();
    public RelaySection Relay { get; set; } = new();
    public LoggingSection Logging { get; set; } = new();

    public sealed class ListenSection
    {
        public int Control { get; set; } = 7000;
        public int StunUdp { get; set; } = 3478;
        public int StunTcp { get; set; } = 3478;
        public int[] RelayPorts { get; set; } = [7010, 7020];
        public int Web { get; set; } = 7500;
        public string WebBind { get; set; } = "127.0.0.1";
    }

    public sealed class DatabaseSection
    {
        public string Path { get; set; } = "data/p2p.db";
    }

    public sealed class HeartbeatSection
    {
        public int TimeoutSec { get; set; } = 30;
    }

    public sealed class PunchSection
    {
        public int DefaultConcurrency { get; set; } = 3;
        public int TimeoutSec { get; set; } = 10;
    }

    public sealed class RelaySection
    {
        public int IdleTimeoutSec { get; set; } = 90;
    }

    public sealed class LoggingSection
    {
        public string Level { get; set; } = "Information";
        public int RetentionDays { get; set; } = 14;
    }

    /// <summary>收集全部校验错误（一次报全，不带病静默运行，NFR-35）。</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();

        Port(errors, "listen.control", Listen.Control);
        Port(errors, "listen.stunUdp", Listen.StunUdp);
        Port(errors, "listen.stunTcp", Listen.StunTcp);
        foreach (var p in Listen.RelayPorts)
            Port(errors, "listen.relayPorts[]", p);
        Port(errors, "listen.web", Listen.Web);
        if (!IPAddress.TryParse(Listen.WebBind, out _))
            errors.Add($"listen.webBind=\"{Listen.WebBind}\" 不是合法 IP 地址；建议：填本机网卡 IP 或 127.0.0.1（仅本机访问）");

        if (string.IsNullOrWhiteSpace(Database.Path))
            errors.Add("database.path 为空；建议：填写相对或绝对 SQLite 文件路径（默认 data/p2p.db）");

        if (Heartbeat.TimeoutSec is < 5 or > 3600)
            errors.Add($"heartbeat.timeoutSec={Heartbeat.TimeoutSec} 超出范围 5~3600；建议：按心跳 30s 的 2.5 倍冗余配置（默认 30）");

        if (Punch.DefaultConcurrency is < 1 or > 5)
            errors.Add($"punch.defaultConcurrency={Punch.DefaultConcurrency} 超出范围 1~5；建议：对称 NAT 用 3，受限锥形用 1~2（OQ-1 决议）");
        if (Punch.TimeoutSec is < 1 or > 60)
            errors.Add($"punch.timeoutSec={Punch.TimeoutSec} 超出范围 1~60；建议：公网正常往返 10s 足够（默认 10）");

        if (Relay.IdleTimeoutSec is < 1 or > 3600)
            errors.Add($"relay.idleTimeoutSec={Relay.IdleTimeoutSec} 超出范围 1~3600；建议：大于隧道 KEEPALIVE 间隔（默认 90）");

        if (!Enum.TryParse<Serilog.Events.LogEventLevel>(Logging.Level, ignoreCase: true, out _))
            errors.Add($"logging.level=\"{Logging.Level}\" 不是合法日志级别；建议：Verbose/Debug/Information/Warning/Error/Fatal 之一（默认 Information）");
        if (Logging.RetentionDays is < 1 or > 365)
            errors.Add($"logging.retentionDays={Logging.RetentionDays} 超出范围 1~365；建议：按磁盘容量配置（默认 14）");

        return errors;
    }

    private static void Port(List<string> errors, string field, int value)
    {
        if (value is < MinPort or > MaxPort)
            errors.Add($"{field}={value} 超出端口范围 {MinPort}~{MaxPort}；建议：修改 appsettings.json 中该端口（控制面默认 7000、STUN 3478、Web 7500）");
    }
}

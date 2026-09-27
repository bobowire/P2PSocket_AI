namespace P2P.Server.Data;

// ── 03 §2.4 mappings / §2.6 mapping_stats（FR-S-801、FR-C-1001/1002）───────

/// <summary>端口映射（字段=PRD 06 §2）。中继回退开关在客户端 peers.json，不入本表（OQ-10）。</summary>
public sealed class Mapping
{
    public Guid Id { get; set; }
    public Guid OwnerDeviceId { get; set; }            // 访问方设备
    public string Name { get; set; } = "";
    public int LocalPort { get; set; }
    public string Proto { get; set; } = "tcp";         // tcp|udp（D19）
    public Guid TargetDeviceId { get; set; }
    public string TargetAddr { get; set; } = "self";   // 'self' 或开放网段内 IP
    public int TargetPort { get; set; }
    public bool Enabled { get; set; }
    public DateTime CreatedAt { get; set; }

    public MappingStat? Stats { get; set; }
}

/// <summary>流量累计（30s 刷盘 + 优雅停机刷盘；实时速率在内存）。</summary>
public sealed class MappingStat
{
    public Guid MappingId { get; set; }                // 1:1，级联删除
    public long BytesUp { get; set; }                  // 访问方→对端
    public long BytesDown { get; set; }
    public long RelayBytes { get; set; }               // 其中经中继的字节
    public DateTime UpdatedAt { get; set; }

    public Mapping Mapping { get; set; } = null!;
}

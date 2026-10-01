// M3-12 流量汇总与导出（04 §2.5、FR-C-1002）：
// - GET /api/stats/summary：按映射/按设备（目标远程码）两维分组 + 总计——本地引擎累计口径
//   （进程重启清零如实反映；目标侧归对端映射 = M2-22 引擎口径）+ 当前路径；
// - GET /api/stats/export?format=csv：同源 CSV 附件（text/csv; charset=utf-8 + BOM[Excel 中文映射名]，
//   LogsApi 导出同模式）——映射明细段 + 空行 + 设备汇总段 + total 行；
// - 纯本地读取（引擎快照 + state.json），无协议往返、passive 无关。
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using P2P.Client.Mapping;
using P2P.Core.Protocol;

namespace P2P.Client.Web;

/// <summary>流量统计端点（扩展方法挂载，闭包持有 <see cref="MappingSyncService"/>）。</summary>
public static class StatsApi
{
    public static IEndpointRouteBuilder MapStatsApi(this IEndpointRouteBuilder app, MappingSyncService sync)
    {
        app.MapGet("/api/stats/summary", () =>
        {
            try { return Api.Ok(sync.Summary()); }
            catch (Exception e) { return Api.Fail(e); }
        });

        app.MapGet("/api/stats/export", (string? format) =>
        {
            try
            {
                if (!string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
                    throw new ApiException(ErrorCode.BadRequest, $"format 仅支持 csv（得 {format ?? "空"}）");
                var s = sync.Summary();
                var sb = new StringBuilder("﻿"); // UTF-8 BOM：Excel 正确识别中文映射名（﻿）
                sb.AppendLine("维度,名称,协议,本地端口,目标远程码,目标端口,当前路径,累计上行(B),累计下行(B),其中中继(B)");
                foreach (var m in s.ByMappings)
                    sb.AppendLine($"mapping,{Csv(m.Name)},{m.Proto},{m.LocalPort},{Csv(m.TargetRemoteCode)},"
                        + $"{m.TargetPort},{m.Path},{m.BytesUp},{m.BytesDown},{m.RelayBytes}");
                sb.AppendLine();
                sb.AppendLine("维度,目标远程码,映射数,累计上行(B),累计下行(B),其中中继(B)");
                foreach (var d in s.ByDevices)
                    sb.AppendLine($"device,{Csv(d.TargetRemoteCode)},{d.Mappings},{d.BytesUp},{d.BytesDown},{d.RelayBytes}");
                sb.AppendLine($"total,,{s.ByMappings.Length},{s.TotalBytesUp},{s.TotalBytesDown},{s.TotalRelayBytes}");
                return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8",
                    $"p2p-stats-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            }
            catch (Exception e) { return Api.Fail(e); }
        });

        return app;
    }

    /// <summary>CSV 字段转义（RFC 4180：含逗号/引号/换行 → 双引号包裹 + 内部引号翻倍）。</summary>
    private static string Csv(string v)
        => v.Contains(',') || v.Contains('"') || v.Contains('\n') || v.Contains('\r')
            ? $"\"{v.Replace("\"", "\"\"")}\""
            : v;
}

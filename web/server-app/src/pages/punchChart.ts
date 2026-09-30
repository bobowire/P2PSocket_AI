// M3-09 打洞成功率折线数据变换（06 §3 仪表盘，FR-S-810）：hourly 24 桶 → SVG 几何。
// 纯函数抽离（任务清单完成判定"折线数据变换"直测）；空桶（total=0）不参与连线，
// 相邻非空桶连段、非空桶画点——时间轴等距固定 24 位（不因空桶错位）。
import type { HourlyBucketView } from "@p2p/ui-shared";

export interface SuccessChartGeom {
  /** 折线段（相邻非空桶；"x,y x,y" SVG points）。 */
  polylines: string[];
  /** 非空桶数据点（circle 定位）。 */
  dots: { cx: number; cy: number }[];
}

export function successChartGeom(
  hourly: HourlyBucketView[],
  width = 560,
  height = 120,
  pad = 4,
): SuccessChartGeom {
  const n = hourly.length;
  if (n === 0) return { polylines: [], dots: [] };
  const step = (width - 2 * pad) / (n - 1);
  const points = hourly.map((b, i) => ({
    x: pad + i * step,
    rate: b.total > 0 ? b.success / b.total : null,
  }));

  const dots = points
    .filter((p) => p.rate !== null)
    .map((p) => ({ cx: p.x, cy: height - pad - (height - 2 * pad) * (p.rate as number) }));

  const polylines: string[] = [];
  let segment: string[] = [];
  for (const p of points) {
    if (p.rate === null) {
      if (segment.length > 0) polylines.push(segment.join(" "));
      segment = [];
      continue;
    }
    const y = height - pad - (height - 2 * pad) * p.rate;
    segment.push(`${p.x.toFixed(1)},${y.toFixed(1)}`);
  }
  if (segment.length > 0) polylines.push(segment.join(" "));
  return { polylines, dots };
}

/** 字节量人类可读（中继流量卡）。 */
export function humanBytes(n: number): string {
  if (n >= 1024 * 1024 * 1024) return `${(n / 1024 / 1024 / 1024).toFixed(2)} GiB`;
  if (n >= 1024 * 1024) return `${(n / 1024 / 1024).toFixed(1)} MiB`;
  if (n >= 1024) return `${(n / 1024).toFixed(1)} KiB`;
  return `${n} B`;
}

// 本文件由 export-ts 反射生成（06 §5、编制定案③），禁止手改。
// 源：P2P.Server Web 展示 DTO（ServerViews，src/Tools/ExportTs @ 2026-10-01T00:33:29Z）。

/** 服务端响应包裹（04 §3：code=0 成功；!=0 见错误码表 04 §5）。注意字段名 message（客户端为 msg）。 */
export interface ServerApiEnvelope<T> {
  code: number;
  message: string;
  data: T;
}

export interface LoginResult {
  mustChangePassword: boolean;
}


export interface DashboardView {
  onlineDevices: number;
  groups: number;
  mappings: MappingsSummaryView;
  relay: RelaySnapshotView;
  stun: StunSnapshotView;
  punch: PunchStatsView;
}


export interface MappingsSummaryView {
  total: number;
  enabled: number;
  byStatus: Record<string, number>;
}


export interface RelaySnapshotView {
  sessions: number;
  bytesForwarded: number;
  reaped: number;
  startedAt: string | null;
  uptimeSec: number;
}


export interface StunSnapshotView {
  admitted: number;
  qps: number;
  dropped: StunDroppedView;
  uptimeSec: number;
}


export interface StunDroppedView {
  rate: number;
  auth: number;
  circuit: number;
}


export interface PunchStatsView {
  total24h: number;
  direct24h: number;
  relay24h: number;
  failed24h: number;
  successRate24h: number | null;
  hourly: HourlyBucketView[];
}


export interface HourlyBucketView {
  hourStart: number;
  total: number;
  success: number;
}


/** TD-22 映射状态投影域（0x62 流水末次；无流水=unknown——客户端状态机是真相源）。 */
export type MappingStatusProjection = "direct" | "relay" | "failed" | "invalid" | "unknown";

// 本文件由 tools/ExportTs 反射生成（06 §5、08 §2③），禁止手改。
// M1-31 接入生成链路前的占位。
export interface ApiEnvelope<T> {
  code: number;
  msg: string;
  data: T;
}

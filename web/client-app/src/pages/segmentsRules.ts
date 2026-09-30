// M2-28 网段表单校验（04 §2.5 /api/lan-segments 与服务端 NormalizeCidr 同口径）：
// 合法输入 = IPv4/IPv6 CIDR（如 192.168.1.0/24）或裸 IP（服务端规范化补 /32 或 /128）；
// 全开放须显式 0.0.0.0/0 或 ::/0（"::"裸值按单地址 /128 处理——勿作全开放）。
import type { FormRules } from "element-plus";
import { isValidIp } from "./mappingFormRules";

/** 规范化预览：裸 IP → 补满前缀（展示用；实际以服务端返回为准）。 */
export function normalizeCidrPreview(value: string): string {
  const v = value.trim();
  if (!v.includes("/")) return v.includes(":") ? `${v}/128` : `${v}/32`;
  return v;
}

function validateCidr(_rule: unknown, value: string, callback: (error?: Error) => void) {
  const v = value?.trim() ?? "";
  if (!v) return callback(new Error("请输入网段"));
  const [addr, prefix, ...rest] = v.split("/");
  if (rest.length > 0) return callback(new Error("前缀最多一项（地址/前缀长度）"));
  if (!isValidIp(addr)) return callback(new Error("地址部分须为合法 IPv4/IPv6"));
  if (prefix !== undefined) {
    if (!/^\d{1,3}$/.test(prefix)) return callback(new Error("前缀长度须为数字"));
    const n = Number(prefix);
    const max = addr.includes(":") ? 128 : 32;
    if (n < 0 || n > max) return callback(new Error(`前缀长度 0~${max}`));
  }
  callback();
}

export const segmentsFormRules: FormRules = {
  cidr: [{ required: true, validator: validateCidr, trigger: "blur" }],
};

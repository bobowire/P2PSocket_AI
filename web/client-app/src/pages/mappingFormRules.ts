// M1-32 映射表单校验规则（04 §2.5 与服务端同口径：端口 1~65535、远程码 6 位）。
// M2-28：目标地址 IP 输入（self 恒放行；IP 模式须合法 IPv4/IPv6——服务端 L3 白名单
// 4002 校验前置）；规则改为工厂形态——targetAddr 是否必填随表单 targetMode 联动。
import type { FormRules } from "element-plus";

export const REMOTE_CODE_PATTERN = /^[0-9a-z]{6}$/;

/** 端口 1~65535（0 保留；65535 上限）。 */
export function validatePort(_rule: unknown, value: number, callback: (error?: Error) => void) {
  if (!Number.isInteger(value) || value < 1 || value > 65535)
    callback(new Error("端口取值 1~65535"));
  else callback();
}

/** IPv4/IPv6 合法性（服务端 IPAddress.TryParse 同口径；主机名不支持——4002 前置）。 */
export function isValidIp(value: string): boolean {
  const v = value.trim();
  if (v.includes(":")) {
    // IPv6：冒号分组（含 :: 缩写，至多一处），每组 1~4 位十六进制
    return !v.includes(":::")
      && v.split("::").length <= 2
      && /^([0-9a-fA-F]{0,4}:){2,7}[0-9a-fA-F]{0,4}$/.test(v);
  }
  const parts = v.split(".");
  return parts.length === 4 && parts.every((p) => /^\d{1,3}$/.test(p) && Number(p) <= 255);
}

/** 目标地址（IP 模式）：必填 + 合法 IP；self 模式恒放行（服务端语义）。 */
export function makeTargetAddrRule(ipMode: () => boolean) {
  return (_rule: unknown, value: string, callback: (error?: Error) => void) => {
    if (!ipMode()) return callback(); // self 模式：不校验
    if (!value?.trim()) callback(new Error("请输入目标 IP 地址"));
    else if (!isValidIp(value)) callback(new Error("目标地址须为合法 IPv4/IPv6（self 恒放行）"));
    else callback();
  };
}

/** 规则工厂（targetAddr 联动 targetMode；其余静态）。 */
export function makeMappingRules(ipMode: () => boolean): FormRules {
  return {
    name: [
      { required: true, message: "请输入名称", trigger: "blur" },
      { max: 50, message: "名称最长 50 字符", trigger: "blur" },
    ],
    localPort: [{ required: true, validator: validatePort, trigger: "blur" }],
    targetRemoteCode: [
      { required: true, message: "请输入目标远程码", trigger: "blur" },
      { pattern: REMOTE_CODE_PATTERN, message: "远程码为 6 位小写字母数字", trigger: "blur" },
    ],
    targetAddr: [{ validator: makeTargetAddrRule(ipMode), trigger: "blur" }],
    targetPort: [{ required: true, validator: validatePort, trigger: "blur" }],
  };
}

/** M1 兼容导出（静态规则——无 targetAddr 联动的既有用法/单测）。 */
export const mappingFormRules: FormRules = makeMappingRules(() => false);

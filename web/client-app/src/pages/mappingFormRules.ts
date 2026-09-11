// M1-32 映射表单校验规则（04 §2.5 与服务端同口径：端口 1~65535、远程码 6 位）。
// 独立模块导出——规则逻辑可单测（el-input-number 的 min/max 只约束 UI 交互路径）。
import type { FormRules } from "element-plus";

export const REMOTE_CODE_PATTERN = /^[0-9a-z]{6}$/;

/** 端口 1~65535（0 保留；65535 上限）。 */
export function validatePort(_rule: unknown, value: number, callback: (error?: Error) => void) {
  if (!Number.isInteger(value) || value < 1 || value > 65535)
    callback(new Error("端口取值 1~65535"));
  else callback();
}

export const mappingFormRules: FormRules = {
  name: [
    { required: true, message: "请输入名称", trigger: "blur" },
    { max: 50, message: "名称最长 50 字符", trigger: "blur" },
  ],
  localPort: [{ required: true, validator: validatePort, trigger: "blur" }],
  targetRemoteCode: [
    { required: true, message: "请输入目标远程码", trigger: "blur" },
    { pattern: REMOTE_CODE_PATTERN, message: "远程码为 6 位小写字母数字", trigger: "blur" },
  ],
  targetPort: [{ required: true, validator: validatePort, trigger: "blur" }],
};

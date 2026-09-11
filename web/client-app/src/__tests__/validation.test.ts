// M1-32 表单校验测试（任务清单完成判定：校验规则与 04 §2 一致）。
// - 端口 1~65535（0 保留、65535 上限）：validatePort 规则逻辑（抽屉 input-number 的 min/max 只约束 UI 交互）；
// - 远程码 6 位小写字母数字（04 §2.5 / 生成规则 6 位去混淆字符集）。
import { describe, expect, it } from "vitest";
import { mappingFormRules, REMOTE_CODE_PATTERN, validatePort } from "../pages/mappingFormRules";

function runPortRule(value: number): string | null {
  let error: string | null = null;
  validatePort({}, value, (e) => {
    error = e?.message ?? null;
  });
  return error;
}

describe("端口校验（04 §2.5：1~65535）", () => {
  it("边界内通过：1、80、65535", () => {
    expect(runPortRule(1)).toBeNull();
    expect(runPortRule(80)).toBeNull();
    expect(runPortRule(65535)).toBeNull();
  });

  it("边界外拒绝：0（保留）、-1、65536、小数、NaN", () => {
    expect(runPortRule(0)).toContain("1~65535");
    expect(runPortRule(-1)).toContain("1~65535");
    expect(runPortRule(65536)).toContain("1~65535");
    expect(runPortRule(80.5)).toContain("1~65535");
    expect(runPortRule(Number.NaN)).toContain("1~65535");
  });

  it("映射表单两条端口字段（localPort/targetPort）均挂同一规则", () => {
    expect(mappingFormRules.localPort?.[0]?.validator).toBe(validatePort);
    expect(mappingFormRules.targetPort?.[0]?.validator).toBe(validatePort);
  });
});

describe("远程码校验（04 §2.5：6 位）", () => {
  it("合法：6 位小写字母数字", () => {
    for (const code of ["a1b2c3", "012345", "zz9999"]) expect(REMOTE_CODE_PATTERN.test(code)).toBe(true);
  });

  it("非法：大写/短/长/含特殊字符/空", () => {
    for (const code of ["A1B2C3", "a1b2c", "a1b2c3d", "a1b2c%", ""]) {
      expect(REMOTE_CODE_PATTERN.test(code)).toBe(false);
    }
  });
});

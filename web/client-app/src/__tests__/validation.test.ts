// M1-32 表单校验测试（任务清单完成判定：校验规则与 04 §2 一致）。
// - 端口 1~65535（0 保留、65535 上限）：validatePort 规则逻辑（抽屉 input-number 的 min/max 只约束 UI 交互）；
// - 远程码 6 位小写字母数字（04 §2.5 / 生成规则 6 位去混淆字符集）；
// - M2-28：目标地址 IP（self 恒放行 / IP 模式必填+IPv4/IPv6 格式）与网段 CIDR
//   （与服务端 NormalizeCidr 同口径：CIDR 或裸 IP→补 /32、/128）。
import { describe, expect, it } from "vitest";
import { isValidIp, makeMappingRules, mappingFormRules, REMOTE_CODE_PATTERN, validatePort } from "../pages/mappingFormRules";
import { normalizeCidrPreview, segmentsFormRules } from "../pages/segmentsRules";

function runPortRule(value: number): string | null {
  let error: string | null = null;
  validatePort({}, value, (e) => {
    error = e?.message ?? null;
  });
  return error;
}

function runRule(rule: unknown, value: unknown): string | null {
  let error: string | null = null;
  (rule as { (_r: unknown, v: unknown, cb: (e?: Error) => void): void })({}, value, (e: Error | undefined) => {
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

describe("目标地址校验（M2-28，04 §2.5：self 恒放行 / IP 格式）", () => {
  it("IPv4 合法/非法", () => {
    for (const ip of ["192.168.1.50", "10.0.0.1", "127.0.0.1", "255.255.255.255"])
      expect(isValidIp(ip)).toBe(true);
    for (const ip of ["256.1.1.1", "1.2.3", "1.2.3.4.5", "a.b.c.d", "192.168.1", ""])
      expect(isValidIp(ip)).toBe(false);
  });

  it("IPv6 含 :: 缩写合法；三连冒号/坏分组非法", () => {
    for (const ip of ["::1", "fd00::1", "2001:db8::", "::", "fe80:0:0:0:1:2:3:4"])
      expect(isValidIp(ip)).toBe(true);
    for (const ip of [":::", "g::1", "12345::1"])
      expect(isValidIp(ip)).toBe(false);
  });

  it("规则工厂：self 模式空值放行；IP 模式必填+格式拦截（联动 targetMode）", () => {
    const ipRules = makeMappingRules(() => true);
    const targetRule = ipRules.targetAddr![0]!.validator;
    expect(runRule(targetRule, "")).toContain("请输入目标 IP");
    expect(runRule(targetRule, "not-an-ip")).toContain("合法 IPv4/IPv6");
    expect(runRule(targetRule, "192.168.1.50")).toBeNull();

    const selfRules = makeMappingRules(() => false);
    expect(runRule(selfRules.targetAddr![0]!.validator, "")).toBeNull(); // self 恒放行
  });
});

describe("网段校验（M2-28，服务端 NormalizeCidr 同口径）", () => {
  const cidrRule = segmentsFormRules.cidr![0]!.validator;

  it("CIDR 与裸 IP 合法；空/坏地址/越界前缀/双斜杠拒绝", () => {
    for (const v of ["192.168.1.0/24", "10.0.0.0/8", "0.0.0.0/0", "10.0.0.5", "fd00::/8", "::1"])
      expect(runRule(cidrRule, v)).toBeNull();
    for (const v of ["", "not-a-cidr", "256.0.0.0/24", "10.0.0.0/33", "fd00::/129", "10.0.0.0/8/8"])
      expect(runRule(cidrRule, v)).not.toBeNull();
  });

  it("normalizeCidrPreview：裸 IP 补满前缀（/32、/128），显式前缀原样", () => {
    expect(normalizeCidrPreview("10.0.0.5")).toBe("10.0.0.5/32");
    expect(normalizeCidrPreview("::1")).toBe("::1/128");
    expect(normalizeCidrPreview("192.168.1.0/24")).toBe("192.168.1.0/24");
  });
});

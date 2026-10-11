import { describe, expect, it } from "vitest";
import { DETAILS_KEY, readDetails, writeDetails } from "./detailsSetting.ts";

function memory(initial: Record<string, string> = {}) {
  const m = new Map(Object.entries(initial));
  return { getItem: (k: string) => m.get(k) ?? null, setItem: (k: string, v: string) => void m.set(k, v), m };
}

describe("Details card setting", () => {
  it("is off when nothing is stored, and keeps a remembered value", () => {
    expect(readDetails(() => memory())).toBe(false);
    expect(readDetails(() => memory({ [DETAILS_KEY]: "1" }))).toBe(true);
    expect(readDetails(() => memory({ [DETAILS_KEY]: "0" }))).toBe(false);
  });

  it("round-trips through storage", () => {
    const s = memory();
    writeDetails(true, () => s);
    expect(s.m.get(DETAILS_KEY)).toBe("1");
    expect(readDetails(() => s)).toBe(true);
    writeDetails(false, () => s);
    expect(readDetails(() => s)).toBe(false);
  });

  it("falls back to off, and doesn't throw, when storage is unavailable", () => {
    const blocked = () => {
      throw new Error("SecurityError");
    };
    expect(readDetails(blocked)).toBe(false);
    expect(() => writeDetails(true, blocked)).not.toThrow();
  });
});
